using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>A module can publish its own bindings and cancel its own scopes. This is ownership isolation, not an untrusted-code sandbox.</summary>
public sealed partial class RuntimeModuleHandle : IDisposable
{
    private readonly RuntimeKernel kernel;
    internal RuntimeKernel Kernel => kernel;
    internal RuntimeModuleHandle(RuntimeKernel kernel, string providerId, long generation)
    { this.kernel = kernel; ProviderId = providerId; Generation = generation; }
    public string ProviderId { get; }
    internal long Generation { get; }
    public bool IsRegistered => kernel.IsRegistered(ProviderId, Generation);
    public int CancelScope(string scopeId) => kernel.CancelScope(this, scopeId);
    /// <summary>This provider's level gate, resolved on first use and then read as one field. The kernel mutates the same
    /// object on elevation and closes it when the provider unregisters, so a stale handle stops writing on its own.</summary>
    private RuntimeLogGate? logGate;
    /// <summary>Whether a record at <paramref name="level"/> would be written for this provider: one comparison once the
    /// gate is resolved. A record point that builds strings, arrays or payloads asks this first, so a disabled level
    /// allocates nothing (contract §3.2 I-DIAG).</summary>
    public bool Logs(RuntimeLogLevel level) => (logGate ??= kernel.ModuleLogGate(ProviderId, Generation)).Writes(level);
    /// <summary>The structured record point of this provider (contract §3.2 I-DIAG): one line of the Forge Engine log,
    /// stamped with this handle's provider and the kernel's tick and world epoch, gated by this provider's level. A
    /// disabled level returns without writing; a malformed code or value is refused with <c>log-record</c>. The code is
    /// the package's own diagnostic code — never one of the kernel's event codes, which only the kernel writes.</summary>
    public void Log(RuntimeLogLevel level, string code, in RuntimeLogFacts facts)
    {
        if (!Logs(level)) return;
        kernel.WriteDomainLog(ProviderId, level, code, in facts);
    }
    public void Dispose() => kernel.Unregister(this);
}

/// <summary>Single-thread simulation dispatcher. Registration and plans are resolved once; no Unity/game types are owned here.</summary>
public sealed partial class RuntimeKernel : IForgeEngine
{
    /// <summary>The release wire of the Forge Standard: the one version the website and this runtime both read.</summary>
    public const string ApiVersion = Wire.ApiVersion;
    public const int MaximumEventHistory = 65536;
    /// <summary>How many ticks a replay-ledger identity is remembered after its tick. An event older than this is
    /// refused as `event-stale`, so forgetting its row never lets a replay in.</summary>
    public const int EventHistoryHorizonTicks = 3600;
    public const int MaximumEventPayloadBytes = 65536;
    private readonly KernelRegistry registry = new();
    private readonly KernelPlans plans;
    private readonly KernelExecutor executor;
    internal readonly EventQueue eventQueue;
    internal readonly EntityLives entityLives;
    internal readonly KernelVariables kernelVariables;
    internal readonly KernelEffects kernelEffects;
    internal readonly KernelOwner kernelOwner;
    internal readonly KernelFlow kernelFlow;
    internal readonly KernelScheduler kernelScheduler;
    private readonly Dictionary<string, RuntimeSubscriptionGate> subscriptionGates = new(StringComparer.Ordinal);
    internal bool advancing;
    private CommandContext? currentCommand;
    private string? effectCancellationProvider;
    public RuntimeIdentity Identity { get; }
    public RuntimeLimits Limits { get; }
    private readonly KernelWorld world = new();
    public long WorldEpoch { get => world.WorldEpoch; private set => world.WorldEpoch = value; }
    public long CurrentTick { get => world.Tick; private set => world.Tick = value; }
    /// <summary>
    /// The rate the running world's ticks are counted at, in ticks per second: one tick is one host
    /// <c>FixedUpdate</c> and the rate is <c>1/Time.fixedDeltaTime</c>, read by the host binding when it begins the
    /// world (contract §3.2 时间单位). This is the one authority a provider converts a unit declared in ticks
    /// through; a world whose rate was never reported refuses the read by name rather than answering with a rate
    /// the world never declared.
    /// </summary>
    public double TicksPerSecond
    {
        get
        {
            ReadThread();
            RuntimeJson.Require(world.worldStarted && world.tickRate != null, "runtime-not-ready",
                "The world's tick rate is only known once the host reports it for the running world.");
            return world.tickRate!.Value;
        }
    }
    /// <summary>The rate as the frozen contexts carry it: the same fact as <see cref="TicksPerSecond"/>, read
    /// without the world check, because a context is only built while a dispatch is running in a started world and
    /// the row that reads it refuses the absence itself.</summary>
    internal double? ReportedTickRate => world.tickRate;
    public int QueuedEvents => eventQueue.queue.Count;
    public int LoadedPlans => plans.Loaded.Count;
    /// <summary>Raised every time the subscription table is rebuilt: a plan loading or unloading, a module
    /// registering or unregistering, a world boundary. A provider that caches a subscription-derived answer
    /// compares this number instead of re-asking the kernel per tick.</summary>
    public long SubscriptionGeneration => plans.SubscriptionGeneration;
    /// <summary>One loaded plan's stable per-tick budget cell. Dispatch carries the cell through
    /// <see cref="LoadedPlan"/> and never resolves the plan id again. A cell stays in the table until the next tick
    /// after unload, so unloading and reloading the same plan id in one tick cannot reset what it already spent.</summary>
    internal sealed class PlanTickUsageCell
    {
        internal PlanTickUsageCell(string planId) { PlanId = planId; }
        internal string PlanId { get; }
        internal int Events { get; private set; }
        internal int Commands { get; private set; }
        internal bool Loaded;
        internal bool Included;
        internal bool RetirementPending;
        internal void Charge(int events, int commands)
        {
            Events += events; Commands += commands; Included = true;
        }
        internal void Restore(int events, int commands)
        {
            Events = events; Commands = commands; Included = true;
        }
        internal void Reset()
        {
            Events = Commands = 0; Included = false;
        }
    }

    /// <summary>The load/migration boundary for plan budget cells. Its small dictionary is never consulted by
    /// dispatch: it only binds a cell while a plan loads and restores keyed rows from a migration image. The
    /// existing Clear/Add/enumeration surface is retained for the migration serializer, while loaded plans keep
    /// their cell references across a restore.</summary>
    internal sealed class PlanTickUsageTable : IEnumerable<KeyValuePair<string, PlanTickUsageCell>>
    {
        private readonly Dictionary<string, PlanTickUsageCell> cells = new(StringComparer.Ordinal);
        private readonly List<PlanTickUsageCell> retired = new();

        internal PlanTickUsageCell Load(string planId)
        {
            if (!cells.TryGetValue(planId, out var cell))
                cells.Add(planId, cell = new PlanTickUsageCell(planId));
            cell.Loaded = true;
            return cell;
        }

        internal void Unload(PlanTickUsageCell cell)
        {
            cell.Loaded = false;
            if (cell.RetirementPending) return;
            cell.RetirementPending = true;
            retired.Add(cell);
        }

        /// <summary>Matches cell ownership to the installed table after a batch lifecycle change. Most plans leave
        /// through <see cref="Unload"/>; checkpoint re-arming replaces the table as one batch, so this same boundary
        /// marks any plan omitted by the replacement without adding a second cleanup path.</summary>
        internal void Synchronize(IReadOnlyDictionary<string, LoadedPlan> loaded)
        {
            foreach (var pair in cells) pair.Value.Loaded = false;
            foreach (var plan in loaded.Values) plan.TickUsage.Loaded = true;
            foreach (var pair in cells)
            {
                var cell = pair.Value;
                if (cell.Loaded || cell.RetirementPending) continue;
                cell.RetirementPending = true;
                retired.Add(cell);
            }
        }

        /// <summary>Starts a new usage table without invalidating cells held by loaded plans. Unloaded cells remain
        /// through the tick in which they were spent, then leave here; a migration Add can immediately recreate its
        /// unloaded rows before dispatch resumes.</summary>
        internal void Clear()
        {
            for (var index = 0; index < retired.Count; index++)
            {
                var cell = retired[index];
                cell.RetirementPending = false;
                if (!cell.Loaded) cells.Remove(cell.PlanId);
            }
            retired.Clear();
            foreach (var pair in cells) pair.Value.Reset();
        }

        internal void Add(string planId, (int Events, int Commands) usage)
        {
            if (!cells.TryGetValue(planId, out var cell))
                cells.Add(planId, cell = new PlanTickUsageCell(planId));
            if (cell.Included) throw new ArgumentException("Duplicate plan tick usage.", nameof(planId));
            cell.Restore(usage.Events, usage.Commands);
            if (!cell.Loaded && !cell.RetirementPending)
            {
                cell.RetirementPending = true;
                retired.Add(cell);
            }
        }

        public IEnumerator<KeyValuePair<string, PlanTickUsageCell>> GetEnumerator()
        {
            foreach (var row in cells)
                if (row.Value.Included) yield return row;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // A loaded instance owns dispatch and migration identity; byte identity is checked at admission/handshake.
    internal sealed class LoadedPlan
    {
        internal LoadedPlan(ResolvedPlan plan, IReadOnlyDictionary<string, long> modules, string contentDigest,
            PlanTickUsageCell tickUsage)
        {
            Plan = plan; Modules = modules; ContentDigest = contentDigest; TickUsage = tickUsage;
            Names = new(plan.Id, plan.ResourceId, plan.ResourceRevision);
            Steps = new ResolvedStep[plan.Frames.Steps.Length];
            ProviderGenerations = new long[Steps.Length];
            foreach (var entry in plan.Entries)
                foreach (var step in entry.Steps)
                {
                    Steps[step.Frames.Index] = step;
                    ProviderGenerations[step.Frames.Index] = modules[step.ProviderId];
                }
        }
        internal ResolvedPlan Plan { get; }
        internal IReadOnlyDictionary<string, long> Modules { get; }
        /// <summary>Plan-wide step slots and their load-time module generations. Dispatch never resolves a provider name.</summary>
        internal ResolvedStep[] Steps { get; }
        internal long[] ProviderGenerations { get; }
        internal ResolvedStep? Step(int index) => (uint)index < (uint)Steps.Length ? Steps[index] : null;
        internal string ContentDigest { get; }
        /// <summary>The registry revision the plan was parsed under; a different revision never reuses it.</summary>
        internal long RegistryVersion { get; init; }
        internal PlanTickUsageCell TickUsage { get; }
        /// <summary>The module table version <see cref="Modules"/> was last found live at (<see cref="ModulesLive"/>).</summary>
        internal long ModulesCheckedAt = -1;
        /// <summary>The plan identity every command of this plan carries.</summary>
        internal CommandPlan Names { get; }
    }
    /// <summary>One plan waiting on one event. <see cref="Subjects"/> is what the plan's own mount targets accepted
    /// for that event — the entities its steps read `self` from — and travels with the item so no step re-matches:
    /// the queue carries the decision the dispatch already made.</summary>
    internal sealed record Work(LoadedPlan Plan, ResolvedEntry Entry, IReadOnlyList<EntityReference> Subjects)
    {
        internal KernelLog.BoundedRefusalSlot[][]? AttachmentRefusals;
    }
    /// <summary>One queued dispatch: the event, the work it claimed, and whether it carries an event table
    /// (<see cref="EventRows"/>). Row 0 of that table is this event and every event its own handlers publish is
    /// appended while it is walked, so a row index means something for exactly as long as the dispatch that handed
    /// it out. <see cref="Sequence"/> is the publish order the event reached the queue in, which is the order its own
    /// envelope reports.</summary>
    internal sealed record Pending(string Provider, long Generation, RuntimeEvent Event, IReadOnlyList<Work> Work,
        ScheduledJob? Schedule = null, long Sequence = 0, bool Tabled = false,
        WaitJob? Wait = null, bool WaitTimeout = false, GateReleaseTicket? Release = null)
    {
        /// <summary>This dispatch's work grouped by plan, and how many commands it can produce, computed the first
        /// time a budget question is asked and kept for the rest of the item's life. <see cref="PlanUsageGeneration"/>
        /// is the lifecycle generation it was computed at: a plan that unloaded since then makes the grouping stale,
        /// and it is recomputed rather than trusted.</summary>
        internal IReadOnlyList<PendingPlan>? PlanUsage;
        internal long PlanUsageGeneration = -1;
        /// <summary>The work this dispatch may actually walk: the enqueue's claim once a plan unloaded since is
        /// dropped, and once the gate has dropped the entries whose options refuse this activation. Null until the
        /// gate has been asked, which is what distinguishes "not decided yet" from "everything was admitted".</summary>
        internal IReadOnlyList<Work>? Admitted;
        /// <summary>The tick this occurrence's decision was made. Waiting for a later budget does not expire it
        /// or charge its gate again; a lifecycle change may only remove work whose plan unloaded.</summary>
        internal long DecidedTick = -1;
        /// <summary>A scheduled occurrence owns its replay-ledger identity before any gate or callback can
        /// mutate state. Its cursor and dispatched count advance only when this queued occurrence is consumed.</summary>
        internal bool PulseClaimed;
        /// <summary>The entry points this dispatch asked a gate about and was refused: the refusals this same
        /// computation produced, reported once so a receipt that names them is not written twice.</summary>
        internal List<TriggerGateReceipt>? GateRefusals;
        /// <summary>The last entity reference the enqueue read out of this event's payload and where in the payload's
        /// text it stands, so the walk that reads the same reference again reuses it (<see cref="ReadEntity"/>).</summary>
        internal EntityReference? Entity;
        internal int EntityAt = -1;
        /// <summary>Only the trigger's load-time-selected subject, decoded while this event was admitted.
        /// Continuations do not acquire a second ending merely by retaining the original event's payload.</summary>
        internal EventSubject EndSubject;
        /// <summary>The publisher's log gate, resolved when this dispatch began (<see cref="TryBeginDispatch"/>).</summary>
        internal RuntimeLogGate? LogGate;
    }
    /// <summary>One plan's share of a queued dispatch: the plan and the commands its claimed entries can produce.</summary>
    internal sealed record PendingPlan(LoadedPlan Plan, int Steps);
    /// <summary>The booked set one queued `debounce` release belongs to. The generation is which move of the quiet
    /// window it was taken from, so a release a later arrival moved past is dropped instead of fired at a tick the
    /// card no longer waits for.</summary>
    internal sealed record GateReleaseTicket(string PlanId, string NodeId, string Subject, long Generation);
    /// <summary>What one `debounce` release re-runs: the arrival that opened the quiet window — the dispatch whose
    /// frame, scope and provider the release travels under — and the claimed work item of the gated entry, which is
    /// the one entry the window was opened for.</summary>
    internal sealed record GateReleasePayload(Pending Origin, Work Item);
    /// <summary>One entity reference and the resolver that answered for its kind, kept for as long as the value that
    /// carried it is still being read — one activation for a frame, the life of the job for a captured template.</summary>
    internal readonly record struct EntityCheck(EntityReference Entity, EntityKindResolver Kind)
    {
        internal Func<EntityReference, bool> Resolve => Kind.Resolve;
    }

    /// <summary>One registered entity kind as a capture holds it: the resolver that answered for it and whether its
    /// owner reports the end of every life (<see cref="RuntimeModule.EntityEndReports"/>). A capture keeps the same
    /// object for its whole life; when the registration it was taken from goes away the object is
    /// <see cref="Retired"/>, and a retired kind is asked through its captured resolver again because nobody reports
    /// its ends any more.</summary>
    internal sealed class EntityKindResolver
    {
        internal readonly string Kind;
        internal readonly string Owner;
        internal readonly Func<EntityReference, bool> Resolve;
        internal readonly bool ReportsEnd;
        internal bool Retired;
        internal EntityKindResolver(string kind, string owner, Func<EntityReference, bool> resolve, bool reportsEnd)
        { Kind = kind; Owner = owner; Resolve = resolve; ReportsEnd = reportsEnd; }
        /// <summary>Whether a held reference of this kind is answered by the end reports rather than by a resolver call.</summary>
        internal bool Reported => ReportsEnd && !Retired;
    }

    public RuntimeKernel(RuntimeIdentity identity, RuntimeLimits? limits = null)
    {
        RuntimeJson.Require(identity.ApiVersion == ApiVersion, "api-version", "Unsupported runtime API.");
        var json = RuntimeJson.From(identity);
        RuntimeJson.Id(json, "id"); RuntimeJson.Version(json, "version"); RuntimeJson.Text(json, "gameBuild");
        Identity = identity; Limits = limits ?? new RuntimeLimits(); log = new KernelLog(world, identity.Id); plans = new KernelPlans(registry); eventQueue = new EventQueue(world); entityLives = new EntityLives(world, registry, log); executor = new KernelExecutor(world, registry, entityLives); kernelVariables = new KernelVariables(world, registry, plans, entityLives); kernelEffects = new KernelEffects(world, registry, log, entityLives, Limits); kernelOwner = new KernelOwner(world, registry, plans, executor, eventQueue, kernelEffects, Limits); kernelFlow = new KernelFlow(world, registry, log, plans, executor, eventQueue, kernelVariables, kernelEffects, kernelOwner, Limits); kernelScheduler = new KernelScheduler(world, registry, log, plans, executor, eventQueue, kernelVariables, kernelEffects, kernelOwner, kernelFlow, Limits);
        var actual = RuntimeJson.From(Limits); var ceiling = RuntimeJson.From(new RuntimeLimits());
        foreach (var property in actual.EnumerateObject()) RuntimeJson.Integer(property.Value, 1, ceiling.GetProperty(property.Name).GetInt32());
        // PriorityQueue creates its read-only unordered view lazily. Sweeps enumerate that view inside Advance, so
        // every queue image constructs the wrapper here, including detached migration images later swapped live.
        _ = eventQueue.queue.UnorderedItems;
        entityInstance = EntityInstance;
    }
    /// <summary>The one delegate every command and evaluation context is handed for <see cref="EntityInstance(EntityReference)"/>.</summary>
    private readonly Func<EntityReference, object?> entityInstance;
    private void Thread() { ReadThread(); AcceptRuntimeWork(); }
    internal void Mutable()
    { ReadThread(); NoLifecycleMutation(); RuntimeJson.Require(!advancing, "reentrant-mutation", "Cannot change registration, plans or world during dispatch."); }
    internal bool IsRegistered(string provider, long token) => registry.IsRegistered(provider, token);
    /// <summary>I-DIAG. The level is the registering package's own cfg value and is required: a provider has no level
    /// until its package hands one over, and the level table never invents a default for it. One package registering more
    /// than one provider passes the same level for each. The Runtime's own built-in providers do not come through here.</summary>
    private const string PlanInternalError = "plan-internal-error";

    /// <summary>The exception type and a short message of a failure that is not a contract refusal.</summary>
    private static string InternalPlanFailure(Exception error)
    {
        var message = error.Message ?? "";
        return error.GetType().Name + ": " + (message.Length > 120 ? message.Substring(0, 120) : message);
    }

    public RuntimeModuleHandle RegisterModule(RuntimeModule module, RuntimeLogLevel level)
    {
        RuntimeJson.Require(level is RuntimeLogLevel.Off or RuntimeLogLevel.Error or RuntimeLogLevel.Info, "log-level",
            "Configured log levels are off, error or info; trace is only reachable through elevation.");
        return Register(module, level);
    }
    /// <summary>The Runtime's own built-in providers (CombatContracts, ControlContracts) ship no package cfg, so they
    /// take the Runtime provider's level. Host assembly only: a domain package must hand over its own.</summary>
    internal RuntimeModuleHandle RegisterBuiltinModule(RuntimeModule module) => Register(module, log.RuntimeLevel);
    private RuntimeModuleHandle Register(RuntimeModule module, RuntimeLogLevel level)
    {
        Mutable(); RuntimeRegistry next; string provider;
        try
        {
            RuntimeJson.Require(IsRegistrationOpen, "registration-closed", "Module registration is frozen before host startup.");
            next = registry.Admit(module, ApiVersion, out var declared);
            provider = declared;
        }
        catch (RuntimeContractException ex)
        {
            // The seed's own provider id is the only thing known before the seed validates; anything else is Runtime's own record without a subject.
            log.LogRegistrationRejected(DeclaredProvider(module), ex.Code, ex.Message);
            throw;
        }
        var token = registry.Install(next, provider);
        RefreshRegistryViews(null);
        if (module.Package != null) registry.Packages.Add(provider, module.Package);
        log.RegisterLogGate(provider, level);
        // The bindings a module brings include the ones that carry a status ending: the list is refreshed here, at
        // the one moment the registry's own binding table grew.
        RebuildStatusEndedBindings();
        log.RepublishLogLevels();
        LogRegisteredBindings(provider);
        return new RuntimeModuleHandle(this, provider, token);
    }
    /// <summary>The declaration owns the provider id, including on a failed registration.</summary>
    private static string DeclaredProvider(RuntimeModule module) => module.Declaration.Id;
    private void LogRegisteredBindings(string provider)
    {
        if (!log.HasSink || !log.LogGate(provider).IsEnabled(RuntimeLogLevel.Info)) return;
        foreach (var binding in registry.Tables.Bindings.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray())
            if (RuntimeJson.Text(binding.Value, "providerId") == provider) log.LogBindingRegistered(provider, binding.Key);
    }
    internal void Unregister(RuntimeModuleHandle handle)
    {
        Mutable(); if (!handle.IsRegistered) return;
        var provider = handle.ProviderId;
        var ownedCaps = registry.Tables.CapabilityRegistrants.Where(x => x.Value == provider).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var ownedBindings = registry.Tables.Bindings.Where(x => RuntimeJson.Text(x.Value, "providerId") == provider).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var binding in registry.Tables.Bindings.Values.Where(b => RuntimeJson.Text(b, "providerId") != provider))
            RuntimeJson.Require(!ownedCaps.Contains(RuntimeJson.Text(binding, "capabilityId")) && !RuntimeJson.Strings(binding.GetProperty("requires")).Any(ownedBindings.Contains), "module-in-use", "Another module still requires this module's contracts/bindings.");
        RuntimeJson.Require(!kernelVariables.stateLeases.Values.Any(l => l.Handle.ProviderId != provider && ownedCaps.Contains(l.Key.Definition)), "module-in-use", "Another module still holds a lease of this definition.");
        // A queued event whose publisher unregisters is dropped at its next dispatch, where the removal is recorded with the
        // reason the pending snapshot carries (source-lifecycle / plan-unloaded); cancel and unload count before removal.
        kernelFlow.StopScheduledSource(provider, null, "module-unregistered"); kernelVariables.StopStateSource(provider, null, "module-unregistered");
        kernelEffects.StopOwnedHandleSource(provider, null, "module-unregistered");
        EndDeferredResults(provider, "binding-lifecycle");
        // A cancellation callback may have recursively removed this registration and installed a new one.
        if (!handle.IsRegistered) return;
        RemoveLifecycleObservers(provider, handle.Generation);
        RemoveTriggerObservers(provider, handle.Generation);
        RemoveAuthoringRoomResolver(provider, handle.Generation);
        // A declared definition field is a fact about this provider's appliers, so it leaves with them; a
        // leftover would keep a field implemented in the manifest while nothing writes it any more.
        registry.RemoveDefinitionFields(provider);
        registry.Modules.Remove(provider); registry.ModuleTableVersion++;
        registry.Tables.RemoveModuleManifest(provider, ownedCaps, ownedBindings);
        registry.RemoveServices(provider);
        providerHandovers.Remove(provider);
        registry.Packages.Remove(provider);
        foreach (var id in ownedBindings) { registry.Tables.Handlers.Remove(id); registry.Tables.Evaluators.Remove(id); registry.Tables.Shapes.Remove(id); }
        foreach (var id in ownedCaps) registry.Tables.CapabilityRegistrants.Remove(id);
        foreach (var key in registry.Tables.Resolvers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray()) registry.Tables.Resolvers.Remove(key);
        // The end-report promise goes with the resolver: the table rebuild below retires this provider's kinds, so a
        // capture still holding one of them goes back to asking the resolver it captured.
        foreach (var key in registry.Tables.EntityEndReports.Where(x => x.Value == provider).Select(x => x.Key).ToArray())
            registry.Tables.EntityEndReports.Remove(key);
        foreach (var key in registry.Tables.EntityObservers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.EntityObservers.Remove(key);
        foreach (var key in registry.Tables.EntityPoseObservers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.EntityPoseObservers.Remove(key);
        RefreshRegistryViews(ownedBindings);
        foreach (var key in registry.Tables.EntityInstanceResolvers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.EntityInstanceResolvers.Remove(key);
        foreach (var key in registry.Tables.EntityInstances.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.EntityInstances.Remove(key);
        // A candidate source is claimed per kind and one kind has exactly one owner, so the kinds go with the
        // provider: a leftover would refuse the replacement provider's claim as a conflict and would keep handing
        // a query step entities the kernel no longer has any provider for.
        foreach (var key in registry.Tables.EntityCandidates.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.EntityCandidates.Remove(key);
        // A zone responder goes with the resolver it was registered beside: a leftover would keep answering where
        // a kind's entities stand for a package that no longer owns the kind.
        foreach (var key in registry.Tables.EntityZones.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.EntityZones.Remove(key);
        // An area responder goes with the zone responder it is the other half of, and with the resolver beside it.
        foreach (var key in registry.Tables.EntityZoneAreas.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.EntityZoneAreas.Remove(key);
        // A mount matcher is claimed per kind and one kind has exactly one owner, so a provider that unregisters
        // has to take its own kinds with it: a leftover would refuse the replacement provider's claim as a
        // conflict and would keep matching mounts for a module that is gone.
        foreach (var key in registry.Tables.AttachmentMatchers.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.AttachmentMatchers.Remove(key);
        // Resource kinds go with their owner for the same reason: every read of a kind is answered by the package
        // that knows it, and a kind left behind would answer for a provider that is gone.
        foreach (var key in registry.Tables.ResourceProviders.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.ResourceProviders.Remove(key);
        // A native-object lookup is the package's own reading of its own native types, so it goes with it. The
        // handles that package cast are dropped here too: a handle whose owner is gone stands for nothing.
        registry.Tables.ObjectEntityResolvers.RemoveAll(entry => entry.Owner == provider);
        // A presentation session list is the provider's own answer about the running process, so it goes with the
        // provider: a leftover would keep presenting steps to sessions nothing owns any more.
        foreach (var key in registry.Tables.PresentationSessions.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.PresentationSessions.Remove(key);
        // The owner-session answers are the same kind of fact and go the same way: a leftover would keep routing
        // owner steps to a holder nothing owns any more.
        foreach (var key in registry.Tables.OwnerSessions.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.OwnerSessions.Remove(key);
        for (var slot = 0; slot < kernelEffects.handleSlots.Count; slot++)
            if (kernelEffects.handleSlots[slot] is { Active: true } entry && entry.ProviderId == provider) kernelEffects.ReleaseHandle(slot);
        // A wait this provider started goes with it, wherever it was started from: a wait that declared no deadline
        // has no clock that would ever reach it again, so it would hold a place in the wait budget and a
        // subscription gate open for the rest of the world. Waits a *dependent plan* holds are the plan's own and
        // retire with it below — this sweep is about the source, not about one plan.
        StopWaitSource(provider, null);
        // With the handles go the effects that named them: the module is called back while its own registration
        // still exists, so the native state it wrote is undone rather than left running for a provider that is gone.
        ReclaimEffectProvider(provider, CurrentTick);
        foreach (var key in registry.Tables.EffectRestores.Where(x => x.Value.Owner == provider).Select(x => x.Key).ToArray())
            registry.Tables.EffectRestores.Remove(key);
        // The plans that depended on this provider leave through the one plan-removal path, exactly as an explicit
        // unload takes them: their effects are ended while the other providers that own them can still restore their
        // native state, their waits retire, and their declarations, gate rows, latches and module values are
        // forgotten before the plan leaves. A bare removal here is what used to leave a removed plan's rows live.
        foreach (var id in plans.Loaded.Where(x => x.Value.Modules.ContainsKey(provider)).Select(x => x.Key).ToArray()) RemovePlan(id);
        log.UnregisterLogGate(provider);
        // The batch boundary: one lifecycle move and one subscription rebuild for the whole unregistration, after
        // every plan it took with it has finished leaving.
        world.lifecycleGeneration++;
        RebuildStatusEndedBindings();
        RebuildSubscriptions();
    }
    public string ExportManifest() { ReadThread(); return registry.ExportManifest(Identity, Limits); }
    /// <summary>I-PACK batch load. Every file gets its own outcome and its own plan.loaded/plan.rejected record;
    /// one file's problem never affects another's, and the whole call never throws for a domain-level rejection.
    /// Files are expected pre-sorted by the host (ordinal on the BepInEx-relative path); that order drives conflict-group
    /// order and load order.</summary>
    internal IReadOnlyList<PlanLoadOutcome> LoadPlans(IReadOnlyList<PlanCandidate> files) => LoadPlans(files, null);
    /// <summary>Plans this kernel has fully parsed since it was created (a reused plan is not counted).</summary>
    internal long PlansParsed => plans.Parsed;

    /// <summary>reusable holds plans the kernel already parsed: a resolved plan is immutable and carries no world
    /// state, so a candidate with the same planId and content digest, parsed under the same registry revision, keeps
    /// its resolved plan. Everything that belongs to a load (declarations, activation slots, tick usage, records)
    /// is still performed for it.</summary>
    private IReadOnlyList<PlanLoadOutcome> LoadPlans(IReadOnlyList<PlanCandidate> files, IReadOnlyDictionary<string, LoadedPlan>? reusable)
    {
        ArgumentNullException.ThrowIfNull(files);
        Mutable();
        AcceptRuntimeWork(true);
        // Where the time of this call goes, in stopwatch ticks, reported once at the end: the identity pass, the plan
        // parse, the plan records, and the subscription rebuild that follows the last plan.
        var started = Stopwatch.GetTimestamp();
        var parseTable = new RuntimePlan.ParseTable(registry.Tables);
        long identityTicks = 0, parseTicks = 0, recordTicks = 0, recordPrepareTicks = 0, recordCount = 0, parsedCount = 0, reusedCount = 0, rebuildTicks = 0;
        void Rejected(string path, RuntimeLogPlan? plan, string code, string? detail = null)
        {
            var at = Stopwatch.GetTimestamp();
            if (log.RuntimeLogs(RuntimeLogLevel.Error))
            {
                var record = new RuntimeLogRecord
                {
                    Level = RuntimeLogLevel.Error,
                    Code = RuntimeLogCodes.PlanRejected,
                    Provider = Identity.Id,
                    Tick = CurrentTick,
                    WorldEpoch = WorldEpoch,
                    Path = path,
                    Plan = plan,
                    Detail = detail,
                    Result = new RuntimeLogResult { Status = "rejected", Commit = null, Reason = code }
                };
                recordPrepareTicks += Stopwatch.GetTimestamp() - at;
                log.WriteLog(in record);
                recordCount++;
            }
            recordTicks += Stopwatch.GetTimestamp() - at;
        }
        void Loaded(string path, RuntimeLogPlan plan, IReadOnlyList<string> permissions)
        {
            var at = Stopwatch.GetTimestamp();
            if (log.RuntimeLogs(RuntimeLogLevel.Info))
            {
                var record = new RuntimeLogRecord
                {
                    Level = RuntimeLogLevel.Info,
                    Code = RuntimeLogCodes.PlanLoaded,
                    Provider = Identity.Id,
                    Tick = CurrentTick,
                    WorldEpoch = WorldEpoch,
                    Path = path,
                    Plan = plan,
                    Permissions = permissions
                };
                recordPrepareTicks += Stopwatch.GetTimestamp() - at;
                log.WriteLog(in record);
                recordCount++;
            }
            recordTicks += Stopwatch.GetTimestamp() - at;
        }
        var outcomes = new List<PlanLoadOutcome>(files.Count);
        var identified = new List<(PlanCandidate File, PlanIdentity Identity, JsonElement Plan)>();
        var batchSink = log.Sink as IRuntimePlanLoadLogSink;
        RuntimePlanLoadLogMetrics? batchMetrics = null;
        batchSink?.BeginPlanLoad();
        try
        {
            foreach (var file in files)
            {
                if (file.IsHostRejected)
                {
                    Rejected(file.Path, null, file.RejectedCode!, file.RejectedDetail);
                    outcomes.Add(new PlanLoadOutcome(file.Path, false, null, file.RejectedCode, file.RejectedDetail));
                    continue;
                }
                var at = Stopwatch.GetTimestamp();
                try
                {
                    var (plan, identity) = RuntimePlan.Identify(file.Document!, Identity);
                    identified.Add((file, identity, plan));
                    identityTicks += Stopwatch.GetTimestamp() - at;
                }
                catch (RuntimeContractException ex)
                {
                    identityTicks += Stopwatch.GetTimestamp() - at;
                    // A file whose planId cannot even be parsed is rejected on its own error and never joins conflict grouping.
                    Rejected(file.Path, null, ex.Code);
                    outcomes.Add(new PlanLoadOutcome(file.Path, false, null, ex.Code, ex.Message));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One plan's non-contract failure refuses that plan only; the rest of the batch still loads.
                    identityTicks += Stopwatch.GetTimestamp() - at;
                    var detail = InternalPlanFailure(ex);
                    Rejected(file.Path, null, PlanInternalError, detail);
                    outcomes.Add(new PlanLoadOutcome(file.Path, false, null, PlanInternalError, detail));
                }
            }
            var loadedAny = false;
            foreach (var group in identified.GroupBy(x => x.Identity.Id, StringComparer.Ordinal))
            {
                var members = group.ToArray();
                if (members.Skip(1).Any(member => !string.Equals(member.File.ContentDigest, members[0].File.ContentDigest, StringComparison.Ordinal)))
                {
                    // Each conflicting file gets its own path-carrying record; collectively the group's records cover every path.
                    var paths = string.Join(", ", members.Select(m => m.File.Path).OrderBy(p => p, StringComparer.Ordinal));
                    foreach (var member in members)
                    {
                        var plan = new RuntimeLogPlan { PlanId = member.Identity.Id, ResourceId = member.Identity.ResourceId, ResourceRevision = member.Identity.ResourceRevision };
                        Rejected(member.File.Path, plan, "plan-conflict", paths);
                        outcomes.Add(new PlanLoadOutcome(member.File.Path, false, member.Identity.Id, "plan-conflict", paths));
                    }
                    continue;
                }
                var (file, identity, planValue) = members[0];
                var identityPlan = new RuntimeLogPlan { PlanId = identity.Id, ResourceId = identity.ResourceId, ResourceRevision = identity.ResourceRevision };
                // Identical exports of one mount execute once; every installed copy still gets its own outcome.
                void RejectMembers(string code, string detail)
                {
                    foreach (var member in members)
                    {
                        Rejected(member.File.Path, identityPlan, code, detail);
                        outcomes.Add(new PlanLoadOutcome(member.File.Path, false, identity.Id, code, detail));
                    }
                }
                if (plans.Loaded.ContainsKey(identity.Id))
                {
                    RejectMembers("plan-conflict", file.Path);
                    continue;
                }
                ResolvedPlan resolved;
                var parsing = Stopwatch.GetTimestamp();
                try
                {
                    if (reusable != null && reusable.TryGetValue(identity.Id, out var kept) && kept.RegistryVersion == registry.Tables.ManifestVersion
                        && string.Equals(kept.ContentDigest, file.ContentDigest, StringComparison.Ordinal)
                        && kept.Plan.ResourceId == identity.ResourceId && kept.Plan.ResourceRevision == identity.ResourceRevision)
                    { resolved = kept.Plan; reusedCount++; }
                    else { parsedCount++; plans.Parsed++; resolved = RuntimePlan.Parse(planValue, identity, Limits, registry.Tables, this, parseTable); }
                    foreach (var entry in resolved.Entries)
                        foreach (var step in entry.Steps)
                            if (step.NodeKind == "action" && !StatusContract.IsDispel(step.CapabilityId))
                                RuntimeJson.Require(step.Handler != null, "missing-handler", step.BindingId);
                    kernelVariables.DeclareVariables(resolved);
                }
                catch (RuntimeContractException ex)
                {
                    parseTicks += Stopwatch.GetTimestamp() - parsing;
                    RejectMembers(ex.Code, ex.Message);
                    continue;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    parseTicks += Stopwatch.GetTimestamp() - parsing;
                    RejectMembers(PlanInternalError, InternalPlanFailure(ex));
                    continue;
                }
                parseTicks += Stopwatch.GetTimestamp() - parsing;
                var providers = resolved.Bindings.Select(b => RuntimeJson.Text(registry.Tables.Bindings[b], "providerId"))
                    .Concat(resolved.Bindings.Select(b => RuntimeJson.Text(registry.Tables.Capabilities[RuntimeJson.Text(registry.Tables.Bindings[b], "capabilityId")], "owner")))
                    .Concat(resolved.Attachments.Select(attachment => registry.Tables.AttachmentMatchers[attachment.Kind].Owner)).Distinct(StringComparer.Ordinal);
                executor.ReserveActivationSlots(resolved);
                plans.Loaded.Add(resolved.Id, new LoadedPlan(resolved,
                    providers.ToDictionary(p => p, p => registry.Modules[p], StringComparer.Ordinal), file.ContentDigest!,
                    plans.TickUsage.Load(resolved.Id))
                { RegistryVersion = registry.Tables.ManifestVersion });
                loadedAny = true;
                foreach (var member in members)
                {
                    Loaded(member.File.Path, identityPlan, resolved.Permissions);
                    outcomes.Add(new PlanLoadOutcome(member.File.Path, true, identity.Id, null, null));
                }
            }
            var rebuilding = Stopwatch.GetTimestamp();
            if (loadedAny) RebuildSubscriptions();
            rebuildTicks = Stopwatch.GetTimestamp() - rebuilding;
        }
        finally
        {
            var finishing = Stopwatch.GetTimestamp();
            // Sink callbacks retain the kernel thread and the same mutation guard as WriteLog.
            var wasNotifying = log.NotifyingSink;
            log.NotifyingSink = true;
            try { if (batchSink != null) batchMetrics = batchSink.EndPlanLoad(); }
            finally { log.NotifyingSink = wasNotifying; recordTicks += Stopwatch.GetTimestamp() - finishing; }
        }
        if (log.RuntimeLogs(RuntimeLogLevel.Info))
        {
            static long Milliseconds(long ticks) => ticks * 1000 / Stopwatch.Frequency;
            static double PreciseMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            var loadedCount = 0L;
            foreach (var outcome in outcomes) if (outcome.Loaded) loadedCount++;
            var values = new List<RuntimeLogValue>
            {
                RuntimeLogValue.Of("files", (long)files.Count),
                RuntimeLogValue.Of("loaded", loadedCount),
                RuntimeLogValue.Of("rejected", outcomes.Count - loadedCount),
                RuntimeLogValue.Of("parsed", parsedCount),
                RuntimeLogValue.Of("reused", reusedCount),
                RuntimeLogValue.Of("records", recordCount),
                RuntimeLogValue.Of("identityMs", Milliseconds(identityTicks)),
                RuntimeLogValue.Of("parseMs", Milliseconds(parseTicks)),
                RuntimeLogValue.Of("recordMs", Milliseconds(recordTicks)),
                RuntimeLogValue.Of("recordPrepareMs", PreciseMilliseconds(recordPrepareTicks)),
                RuntimeLogValue.Of("rebuildMs", Milliseconds(rebuildTicks)),
                RuntimeLogValue.Of("elapsedMs", Milliseconds(Stopwatch.GetTimestamp() - started))
            };
            if (batchMetrics is { } metrics)
            {
                values.Add(RuntimeLogValue.Of("recordQueued", metrics.Records));
                values.Add(RuntimeLogValue.Of("recordQueueMs", PreciseMilliseconds(metrics.QueueTicks)));
                values.Add(RuntimeLogValue.Of("recordMirrorMs", PreciseMilliseconds(metrics.MirrorTicks)));
                values.Add(RuntimeLogValue.Of("recordMirrors", metrics.MirrorCalls));
            }
            log.LogRuntime(RuntimeLogLevel.Info, PlanLoadSummary, new RuntimeLogFacts { Values = values });
        }
        return outcomes;
    }
    // The one summary line of a plan batch (host record point, same style as the discovery summary): where the load
    // spent its time, with the per-file records counted in recordMs rather than repeated here.
    private const string PlanLoadSummary = "plan.load";

    /// <summary>Makes <paramref name="files"/> the loaded plan set, as the level loader does when a level is chosen:
    /// a loaded plan whose exact bytes are among the files stays as it is (its variables and gates included), a loaded
    /// plan that is not among them leaves through the one removal path, and only the remaining files are parsed. A
    /// kept file's outcome is the load it already had; it writes no second plan.loaded record.</summary>
    internal IReadOnlyList<PlanLoadOutcome> AdoptPlanSet(IReadOnlyList<PlanCandidate> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Mutable();
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files) if (!file.IsHostRejected) wanted.Add(file.ContentDigest!);
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var removed = false;
        foreach (var (planId, loaded) in plans.Loaded.ToArray())
        {
            if (wanted.Contains(loaded.ContentDigest)) kept[loaded.ContentDigest] = planId;
            else removed |= RemovePlan(planId);
        }
        if (removed) world.lifecycleGeneration++;
        var fresh = new List<PlanCandidate>(files.Count);
        foreach (var file in files)
            if (file.IsHostRejected || !kept.ContainsKey(file.ContentDigest!)) fresh.Add(file);
        var loadedNow = LoadPlans(fresh, null);
        // LoadPlans rebuilds the subscriptions only when it loaded something; a level that only dropped plans
        // still owes the rebuild, because the table must stop naming the plans that left.
        if (removed && loadedNow.All(outcome => !outcome.Loaded)) RebuildSubscriptions();
        var byPath = loadedNow.ToDictionary(outcome => outcome.Path, StringComparer.Ordinal);
        var outcomes = new PlanLoadOutcome[files.Count];
        for (var i = 0; i < files.Count; i++)
            outcomes[i] = byPath.TryGetValue(files[i].Path, out var outcome) ? outcome
                : new PlanLoadOutcome(files[i].Path, true, kept[files[i].ContentDigest!], null, null);
        return outcomes;
    }

    internal bool UnloadPlan(string planId)
    {
        Mutable();
        if (!RemovePlan(planId)) return false;
        world.lifecycleGeneration++;
        RebuildSubscriptions();
        return true;
    }
    /// <summary>
    /// The one plan-removal path, shared by an explicit unload and by a module unregistration that takes dependent
    /// plans with it: the plan's effects are ended while the providers that own them can still undo their native
    /// state, its waits retire, its variable, gate, latch and module-scope rows are forgotten, and only then does the
    /// plan itself leave the table. Lifecycle and subscription state are the caller's batch boundary — one rebuild
    /// per unload, one for a whole unregistration — because a rebuild is about the table, not about one plan.
    ///
    /// Nothing here is optional for one caller and not the other: a plan that leaves by either route leaves with
    /// exactly the same cleanup, which is what keeps a removed plan's declarations from outliving it and rejecting
    /// or contaminating a later one.
    /// </summary>
    private bool RemovePlan(string planId)
    {
        if (!plans.Loaded.TryGetValue(planId, out var loaded)) return false;
        ClearSounds(plan: planId);
        kernelScheduler.DetachDeferredPlan(planId);
        ReclaimEffectPlan(planId, CurrentTick);
        ForgetVariables(planId);
        plans.Loaded.Remove(planId);
        plans.TickUsage.Unload(loaded.TickUsage);
        return true;
    }
    /// <summary>Rebuilds the subscription table, then the two things derived from it. The gates are refreshed here
    /// and nowhere else, because this is the one place the table is rebuilt: a publisher's own boolean changes
    /// exactly when the set of plans mounted on its binding does.</summary>
    private void RebuildSubscriptions()
    {
        plans.RebuildSubscriptions();
        RefreshSubscriptionGates();
        BindDeferredResults();
    }
    /// <summary>The structural enum members the loaded plans mounted on one binding name, ordinal ascending. The
    /// binding must be the caller's: the answer is a property of that module's own subscription, and a table
    /// handed to someone else would describe plans they do not own.</summary>
    internal IReadOnlyList<string> SubscribedEnumMembers(string provider, string bindingId, string parameterId)
    {
        Thread();
        RuntimeJson.Require(registry.Tables.Bindings.TryGetValue(bindingId, out var binding) && RuntimeJson.Text(binding, "providerId") == provider, "binding-owner", bindingId);
        RuntimeJson.Text(parameterId);
        return plans.SubscribedEnumMembers(bindingId, parameterId);
    }
    /// <summary>
    /// Recomputes every gate from the four things that consume a binding: a loaded plan mounted on it, a suspended
    /// `g-wait` naming it, an `end_on` ability that ends an effect on it, and a loaded plan's `query` step that reads
    /// it. The first three make a publish reach somebody and the fourth makes a read possible, so the gate is open
    /// while any of them is there, and it is refreshed wherever one of the tables changes.
    /// </summary>
    private void RefreshSubscriptionGates()
    {
        foreach (var gate in subscriptionGates.Values)
        {
            gate.HasSubscribers = Consumes(gate.BindingId);
            gate.Subscription = plans.Subscriptions.TryGetValue(gate.BindingId, out var mounted) ? mounted : null;
        }
        equipmentAttributionGate.HasSubscribers = EquipmentConsumed();
    }
    /// <summary>Gives one gate the registered binding it names and, for a trigger, the sheet its events are written
    /// on: done where the gate is made and where the registry changes, never per event.</summary>
    private void BindGate(RuntimeSubscriptionGate gate)
    {
        gate.Kernel = this;
        var binding = registry.BindingViews.TryGetValue(gate.BindingId, out var view) ? view : null;
        if (ReferenceEquals(gate.Binding, binding)) return;
        gate.Binding = binding;
        gate.EventValues = binding is { IsTrigger: true } ? new EvaluationOutputs(binding.OutputIds, null) : null;
    }
    /// <summary>The registered trigger bindings whose payload can name equipment, rebuilt with the registry views.</summary>
    private string[] equipmentBindings = Array.Empty<string>();
    private readonly RuntimeSubscriptionGate equipmentAttributionGate = new("combat.equipment-attribution");
    /// <summary>Whether any loaded plan, `g-wait`, `end_on` or `query` consumes a trigger whose payload can name
    /// equipment. Only that kind of consumer can read a native attack's equipment attribution, so a producer that opens
    /// an attribution window asks this one precomputed boolean instead of "is any plan loaded".</summary>
    private bool EquipmentConsumed()
    {
        foreach (var binding in equipmentBindings)
            if (Consumes(binding)) return true;
        return false;
    }
    internal RuntimeSubscriptionGate EquipmentAttributionGate() { Thread(); return equipmentAttributionGate; }
    /// <summary>Whether anything in this kernel would read an event published on one binding, or read the binding
    /// itself through a `query` step.</summary>
    internal bool Consumes(string bindingId) =>
        plans.Subscriptions.ContainsKey(bindingId) || kernelVariables.waits.ContainsKey(bindingId) || plans.EndOnBindings.Contains(bindingId)
        || plans.QueryBindings.Contains(bindingId) || IsObserved(bindingId);
    /// <summary>The gate of one binding, resolved by its own provider. The binding must be the caller's: a publisher
    /// asks about its own trigger, and a gate handed to someone else would answer a question that is not theirs.</summary>
    internal RuntimeSubscriptionGate SubscriptionGate(string provider, string bindingId)
    {
        Thread();
        RuntimeJson.Require(registry.Tables.Bindings.TryGetValue(bindingId, out var binding) && RuntimeJson.Text(binding, "providerId") == provider, "binding-owner", bindingId);
        if (!subscriptionGates.TryGetValue(bindingId, out var gate))
        {
            gate = new RuntimeSubscriptionGate(bindingId) { HasSubscribers = Consumes(bindingId) };
            gate.Subscription = plans.Subscriptions.TryGetValue(bindingId, out var mounted) ? mounted : null;
            BindGate(gate);
            subscriptionGates.Add(bindingId, gate);
        }
        return gate;
    }
    /// <summary>The registered trigger one of this kernel's gates names, owned by the module that writes on it. The
    /// gate carries the binding, so nothing is looked up.</summary>
    private BindingView GateTrigger(RuntimeModuleHandle module, RuntimeSubscriptionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var binding = ReferenceEquals(gate.Kernel, this) ? gate.Binding : null;
        RuntimeJson.Require(binding != null && binding.ProviderId == module.ProviderId, "binding-owner", gate.BindingId);
        RuntimeJson.Require(binding!.IsTrigger, "not-trigger", gate.BindingId);
        return binding;
    }
    internal EventWriter BeginEvent(RuntimeModuleHandle module, RuntimeSubscriptionGate gate)
    {
        ReadThread();
        GateTrigger(module, gate);
        gate.EventValues!.Begin();
        return new EventWriter(module, gate);
    }
    /// <summary>Publishes the event written on one gate's sheet. The event is the values as they were written: the
    /// admission checks them cell by cell and keeps them, and the payload's text — what the replay ledger mixes and a
    /// migration image carries — is rendered from them where it is asked for. The binding and the entries mounted on
    /// it are the gate's, so the admission resolves neither by name.</summary>
    internal DispatchResult Raise(RuntimeModuleHandle module, RuntimeSubscriptionGate gate, string eventId, long worldEpoch,
        long simulationTick, string scopeId, string? causeId, string? rootEventId, int causalDepth,
        EntityReference? reporter = null, long token = 0)
    {
        ReadThread();
        var binding = GateTrigger(module, gate);
        return Publish(module, new RuntimeEvent(eventId, gate.BindingId, worldEpoch, simulationTick, scopeId,
            default, causeId, rootEventId, causalDepth)
        { Reporter = reporter, PredictionToken = token }, binding, gate);
    }
    /// <summary>The gates of one provider's own bindings, as the module's registration takes them. A publisher
    /// resolves this once and then reads one boolean per publish, so the only thing left between a native callback
    /// and a skipped event is a dictionary lookup.</summary>
    internal IReadOnlyDictionary<string, RuntimeSubscriptionGate> SubscriptionGates(string provider)
    {
        Thread();
        var gates = new Dictionary<string, RuntimeSubscriptionGate>(StringComparer.Ordinal);
        foreach (var entry in registry.Tables.Bindings)
            if (registry.BindingViews[entry.Key].ProviderId == provider) gates[entry.Key] = SubscriptionGate(provider, entry.Key);
        return gates;
    }
    /// <summary>A client joined another session, whose host counts its worlds from a lower number than this process
    /// already reached: the next <see cref="BeginWorld"/> or <see cref="FollowWorld"/>, and only that one, may name any
    /// other epoch. Every other world still has to advance.</summary>
    internal void RestartWorldCount() { Mutable(); world.worldCountRestarts = true; }
    internal void BeginWorld(long worldEpoch, long? gateSeed = null)
    {
        Mutable(); AcceptRuntimeWork(true); RuntimeJson.Integer(worldEpoch);
        var restarts = world.worldCountRestarts; world.worldCountRestarts = false;
        RuntimeJson.Require(!world.worldStarted || (restarts ? worldEpoch != WorldEpoch : worldEpoch > WorldEpoch),
            "world-epoch", "A new world must advance its epoch.");
        FlushDiagnosticSummaries();
        long? previousWorldEpoch = world.worldStarted ? WorldEpoch : null;
        kernelFlow.StopScheduledSource(null, null, "world-ended"); kernelVariables.StopStateSource(null, null, "world-ended");
        kernelEffects.StopOwnedHandleSource(null, null, "world-ended");
        if (world.worldStarted)
        {
            EndDeferredResults(null, "stale-world");
            // The terminal receipt crosses the boundary once, but its old-world idempotency row cannot consume
            // this world's capacity. A later use of that token is rejected by its world identity instead.
            kernelScheduler.deferredCompleted.Clear();
        }
        else
        {
            kernelScheduler.deferredCommands.Clear(); kernelScheduler.deferredCompleted.Clear(); kernelScheduler.deferredReceipts.Clear();
        }
        // Every effect the old world holds is undone before the epoch moves: a module's restore callback is the
        // only thing that can put a native instance back, and after this line the handles it names are gone.
        ReclaimEffects(CurrentTick);
        ClearSounds(broadcast: false);
        ClearPredictions();
        WorldEpoch = worldEpoch; world.worldStarted = true; CurrentTick = -1; world.worldHost = null; kernelFlow.scheduledThisTick = 0; kernelScheduler.deferredLogged = false;
        // The reported ends were about the lives of the world that just closed.
        entityLives.endedLives.Clear(); entityLives.verifyReportedLives = false;
        SyncOwnerWorld();
        // The previous world's tick rate dies with it: the host reads the running world's step and reports it
        // before this world's first advance, so a world that never reported one has no rate to convert by.
        world.tickRate = null;
        migrationPaused = false; migrationArrivals.Clear();
        eventQueue.queue.Clear(); eventQueue.ResetLedger(); world.cancelled.Clear(); plans.TickUsage.Clear(); kernelScheduler.eventsThisTick = kernelScheduler.commandsThisTick = 0;
        // A new world invalidates everything derived from the old one: the queue is gone, no plan's capture survives
        // it, and every provider's own answers about entities belong to the epoch that just ended.
        world.lifecycleGeneration++;
        // Continuations, their schedules and every handle die with the world: a handle names a world epoch, so
        // nothing from the previous one may stay live, and the handle table starts over with it. The faction
        // relations belong to the world the same way, so a new world starts with none until its own providers
        // publish theirs. The parent index is observations of a world too, so it starts empty with them.
        kernelEffects.handleSlots.Clear(); kernelFlow.controlWalk.Clear(); kernelFlow.BeginActivation(); children.Clear();
        // A variable from the previous world names entities that no longer exist, and a suspended `g-wait` waits on
        // an event of that world: values and latches go, the declarations the loaded plans own stay. The gate
        // store's die takes this world's seed here, drawn by the host that began the world; an explicit seed is
        // what a test or a replay passes, and the checkpoint carries it from then on.
        BeginVariableWorld(ModuleRandomSeed = gateSeed ?? Random.Shared.NextInt64());
        relations = new RuntimeFactionRelations(Array.Empty<RuntimeFactionRelation>());
        log.LogWorldBegan();
        NotifyLifecycle(RuntimeLifecycleKind.WorldChanged, previousWorldEpoch);
    }
    internal int CancelScope(RuntimeModuleHandle handle, string scopeId)
    {
        Thread(); RuntimeJson.Require(handle.IsRegistered, "module-unregistered", handle.ProviderId);
        RuntimeJson.Text(scopeId);
        RuntimeJson.Require(world.cancelled.Count < MaximumEventHistory || world.cancelled.Contains((handle.ProviderId, scopeId)), "scope-budget", scopeId);
        world.cancelled.Add((handle.ProviderId, scopeId));
        var affected = eventQueue.queue.UnorderedItems.Count(x => x.Element.Provider == handle.ProviderId && x.Element.Event.ScopeId == scopeId);
        kernelFlow.StopScheduledSource(handle.ProviderId, scopeId, "scope-cancelled"); kernelVariables.StopStateSource(handle.ProviderId, scopeId, "scope-cancelled");
        kernelEffects.StopOwnedHandleSource(handle.ProviderId, scopeId, "scope-cancelled");
        // A wait the cancelled scope started is the source's own work like a schedule or a state lease: it retires
        // here, because a wait that declared no deadline has no clock that would ever reach it again.
        StopWaitSource(handle.ProviderId, scopeId);
        ReclaimEffectScope(handle.ProviderId, scopeId, CurrentTick);
        ClearSounds(source: handle.ProviderId, scope: scopeId);
        return affected;
    }
    // Both immediate and scheduled facts enter here before payload snapshots, replay identity or budgets.
    // A sink callback receives its explicit result directly; reporting it into that same sink would recurse.
    private string? RefuseFactAdmission(RuntimeModuleHandle handle, RuntimeEvent value)
    {
        if (log.NotifyingSink) return "log-observer-mutation";
        if (FactAdmissionClosure is not { } closure) return null;
        try
        {
            NoLifecycleMutation();
            RuntimeJson.Require(handle.IsRegistered, "module-unregistered", handle.ProviderId);
            RuntimeJson.Require(registry.BindingViews.TryGetValue(value.BindingId, out var owned) && owned!.ProviderId == handle.ProviderId,
                "binding-owner", value.BindingId);
        }
        catch (RuntimeContractException error) { closure = error.Code; }
        log.LogEventRejected(value.BindingId, value.EventId, closure, handle.ProviderId);
        return closure;
    }

    internal DispatchResult Publish(RuntimeModuleHandle handle, RuntimeEvent value) => Publish(handle, value, null, null);

    /// <summary><paramref name="written"/> and <paramref name="gate"/> are set by <see cref="Raise"/>: the binding
    /// was already resolved and checked against the publisher, the mounted entries are the gate's, and the payload
    /// is text this kernel wrote itself, so only its size is still to be judged.</summary>
    private DispatchResult Publish(RuntimeModuleHandle handle, RuntimeEvent value, BindingView? written, RuntimeSubscriptionGate? gate)
    {
        ReadThread();
        if (RefuseFactAdmission(handle, value) is { } admissionCode)
            return new DispatchResult("rejected", admissionCode, value.EventId);
        // A publish inside an advance reads its own event's references; the walk's are handed back afterwards.
        var walkEntities = entityLives.SaveEntityMemo();
        try
        {
            AcceptRuntimeWork();
            RuntimeJson.Require(handle.IsRegistered, "module-unregistered", handle.ProviderId);
            var binding = written;
            if (binding == null)
                RuntimeJson.Require(registry.BindingViews.TryGetValue(value.BindingId, out binding) && binding!.ProviderId == handle.ProviderId, "binding-owner", value.BindingId);
            var observedOnly = false;
            var subscription = gate?.Subscription as Subscription;
            if (subscription == null && !(gate == null && plans.Subscriptions.TryGetValue(value.BindingId, out subscription)))
            {
                // A message no plan hangs on still reaches the `g-wait` steps that named it: a waiting flow is a
                // consumer of the message even when no entry is mounted on the receive trigger. What the wake hands
                // over is validated exactly as a dispatch's payload is, so a malformed message wakes nothing.
                if (kernelVariables.waits.ContainsKey(value.BindingId))
                {
                    var waited = written != null ? value with { } : value with { Outputs = RawJson.Snapshot(value.Outputs, RawJson.Text(value.Outputs)) };
                    executor.AdmitEvent(waited, binding!, gate);
                    WakeWaiters(waited);
                }
                // An `end_on` ability is a consumer too, and the one consumer whose answer is a tick's decision
                // rather than a handler's: the event is queued like any other so the effects it ends end inside the
                // advance it lands in, and no entry point is dispatched for it.
                else if (!plans.EndOnBindings.Contains(value.BindingId))
                {
                    if (!IsObserved(value.BindingId)) return new DispatchResult("ignored", "no-consumer", value.EventId);
                    observedOnly = true;
                }
                subscription = NoSubscription;
            }
            RuntimeJson.Require(world.worldStarted && value.WorldEpoch == WorldEpoch, "stale-world", value.EventId);
            RuntimeJson.Text(value.EventId); RuntimeJson.Text(value.ScopeId);
            RuntimeJson.Integer(value.WorldEpoch); RuntimeJson.Integer(value.SimulationTick);
            // The ledger only remembers identities inside the horizon and above its capacity floor, so an event older
            // than either is refused here, before the ledger is read: a forgotten identity can never be admitted twice.
            RuntimeJson.Require(value.SimulationTick >= eventQueue.ReplayFloor, "event-stale", value.EventId);
            RuntimeJson.Require(world.cancelled.Count == 0 || !world.cancelled.Contains((handle.ProviderId, value.ScopeId)), "scope-cancelled", value.ScopeId);
            // The payload is judged by its own UTF-8 text, read in place: the byte budget first, then the checks a
            // parse of that text makes. The kept value is independent of whatever document the provider may dispose.
            // A written event has no document: its budget is judged by the text its values render to, written into
            // the kernel's own buffer, and the event that is kept is the values themselves.
            JsonElement outputs = default;
            var sheetText = 0L;
            if (written != null)
            {
                RuntimeJson.Require(executor.SheetText(gate!.EventValues!).Length <= MaximumEventPayloadBytes, "event-payload-budget", value.EventId);
                sheetText = executor.eventTextStamp;
            }
            else
            {
                var payload = RawJson.Text(value.Outputs);
                RuntimeJson.Require(payload.Length <= MaximumEventPayloadBytes, "event-payload-budget", value.EventId);
                outputs = RawJson.Snapshot(value.Outputs, payload);
            }
            RuntimeEvent snapshot;
            if (currentCommand is { } command)
                snapshot = written != null
                    ? value with { CauseId = command.CommandId, RootEventId = command.RootEventId, CausalDepth = command.CausalDepth + 1 }
                    : value with { Outputs = outputs, CauseId = command.CommandId, RootEventId = command.RootEventId, CausalDepth = command.CausalDepth + 1 };
            else
            {
                RuntimeJson.Require(value.CausalDepth == 0 && value.CauseId == null && (value.RootEventId == null || value.RootEventId == value.EventId), "external-causality", "Root hooks cannot invent an internal cause.");
                // A payload the provider cannot dispose already is its own snapshot, and the event is kept as it
                // arrived: every field of it is immutable.
                // An event some admission already gave its cells — the same object published again, or to another
                // kernel — is copied, so the cells a queue holds are never rewritten under it.
                snapshot = written != null ? value : !RawJson.Owned(value.Outputs) ? value with { Outputs = outputs }
                    : value.Cells == null ? value : value with { };
            }
            RuntimeJson.Require(snapshot.CausalDepth >= 0 && snapshot.CausalDepth <= Limits.MaxCausalDepth, "causal-depth", value.EventId);
            entityLives.OpenEntityMemo(snapshot.RenderedOutputs, -1, null);
            EventSubject endSubject;
            IReadOnlyList<Work> work;
            try
            {
                endSubject = executor.AdmitEvent(snapshot, binding!, gate);
                // The text the budget was judged by is the admitted cells' own text while nothing else was written
                // since: the ledger mixes it without rendering the event a second time.
                if (written != null && sheetText == executor.eventTextStamp) executor.eventBufferCells = snapshot.Cells;
                // A custom message is claimed by name: only the entries that listen for this message are candidates.
                subscription = subscription!.ForEvent(snapshot);
                // Mount decisions precede the replay ledger: an unclaimed event never consumes its identity.
                work = ClaimedWork(subscription!, snapshot, endSubject);
            }
            catch (RuntimeContractException)
            {
                // Invalid declared identities still retain the actual failed candidate inputs; never probe a
                // matcher again or replace a stale/missing identity with the holder's current equipment.
                if (log.RuntimeLogs(RuntimeLogLevel.Trace))
                {
                    // A written event the admission refused has no cells to render: the trace reads what was written.
                    if (written != null && snapshot.Cells == null) snapshot = snapshot with { Outputs = gate!.EventValues!.ToJson() };
                    if (snapshot.Outputs.ValueKind == JsonValueKind.Object)
                        TraceAttachmentMismatch(subscription!, snapshot, validated: false);
                }
                throw;
            }
            // A dispatch no mounted card claims is ignored rather than queued — unless an `end_on` ability named
            // this binding, whose reading is the event itself and not the mount that carried it.
            if (work.Count == 0 && !plans.EndOnBindings.Contains(value.BindingId))
            {
                if (IsObserved(value.BindingId)) NotifyTriggerObservers(snapshot);
                if (observedOnly) return new DispatchResult("ignored", "no-consumer", value.EventId);
                TraceAttachmentMismatch(subscription, snapshot);
                return new DispatchResult("ignored", "attachment-mismatch", value.EventId);
            }
            var key = new LedgerKey(handle.ProviderId, LedgerSpace.Event, value.EventId);
            // The fingerprint is only computed where it is compared or kept: against the row an identity already
            // has, into the migration buffer, or into the ledger once every budget below has admitted the event.
            if (eventQueue.history.TryGetValue(key, out var previous))
            {
                if (previous.Fingerprint == EventLedgerFingerprint(snapshot)) return new DispatchResult("duplicate", "duplicate-event", value.EventId);
                log.LogEventRejected(value.BindingId, value.EventId, "event-id-conflict", handle.ProviderId);
                return new DispatchResult("rejected", "event-id-conflict", value.EventId);
            }
            IReadOnlyList<PendingPlan> planUsage = ReferenceEquals(work, subscription!.Work) ? subscription.Usage
                : work.Count == 0 ? Array.Empty<PendingPlan>() : PlanUsageOf(work);
            if (migrationPaused)
                return BufferMigrationArrival(handle.ProviderId, handle.Generation, snapshot,
                    EventLedgerFingerprint(snapshot), work, endSubject, planUsage);
            var admitted = EnqueueAdmittedEvent(handle.ProviderId, handle.Generation, snapshot, work, endSubject, planUsage);
            if (admitted.Status == "queued" && IsObserved(value.BindingId)) NotifyTriggerObservers(snapshot);
            return admitted;
        }
        // A refusal is recorded once: a budget reason is what the code table calls budget.exceeded, everything else is
        // event.rejected. The event's binding may already be unregistered, so the raw id is reported as it arrived.
        catch (RuntimeContractException ex)
        {
            if (ex.Code.EndsWith("-budget", StringComparison.Ordinal)) log.LogBudgetExceeded(value.BindingId, value.EventId, ex.Code);
            else log.LogEventRejected(value.BindingId, value.EventId, ex.Code, handle.ProviderId);
            return new DispatchResult("rejected", ex.Code, value.EventId);
        }
        catch (ObjectDisposedException)
        {
            log.LogEventRejected(value.BindingId, value.EventId, "disposed-payload", handle.ProviderId);
            return new DispatchResult("rejected", "disposed-payload", value.EventId);
        }
        finally { entityLives.RestoreEntityMemo(walkEntities); }
    }

    private void TraceAttachmentMismatch(Subscription subscription, RuntimeEvent value, bool validated = true)
    {
        if (!log.RuntimeLogs(RuntimeLogLevel.Trace)) return;
        foreach (var candidate in subscription.Work)
            for (var index = 0; index < candidate.Entry.Attachments.Length; index++)
            {
                var attachment = candidate.Entry.Attachments[index];
                // A normal scope/enum filter is one refusal, not an apparent failure of every entity port.
                if (validated && (!EventScopeMatches(candidate.Entry, value) || !EnumFilterMatches(candidate.Entry, value))
                    || attachment.Ports.Length == 0) TracePort(candidate, attachment, null, index, 0);
                else
                {
                    for (var port = 0; port < attachment.Ports.Length; port++)
                        TracePort(candidate, attachment, attachment.Ports[port], index, port + 1);
                    if (attachment.Holder != null && attachment.EquipmentAttachment is { } roles)
                    {
                        if (!attachment.Ports.Any(port => port.Id == roles.Holder.Id))
                            TracePort(candidate, attachment, roles.Holder, index, attachment.Ports.Length + 1);
                        if (!attachment.Ports.Any(port => port.Id == roles.Equipment.Id))
                            TracePort(candidate, attachment, roles.Equipment, index, attachment.Ports.Length + 2);
                    }
                }
            }

        void TracePort(Work candidate, ResolvedAttachment attachment, PortView? port, int index, int portIndex)
        {
            var present = port != null && value.Outputs.TryGetProperty(port.IdUtf8, out _);
            var data = present ? value.Outputs.GetProperty(port!.IdUtf8) : default;
            candidate.AttachmentRefusals ??= new KernelLog.BoundedRefusalSlot[candidate.Entry.Attachments.Length][];
            var slots = candidate.AttachmentRefusals[index] ??= new KernelLog.BoundedRefusalSlot[(attachment.Ports.Length + 3) * 16];
            ref var slot = ref slots[portIndex * 16 + (validated ? 8 : 0) + (int)data.ValueKind];
            if (validated && log.CountBoundedRefusal(ref slot)) return;
            // These are the failed candidate's inputs, not a second matching pass: native matchers and holder
            // probes must never be called again for diagnostics. A non-null value alone cannot explain refusal.
            var actual = present ? JsonSerializer.Serialize(data) : port == null ? "<not-applicable>" : "<missing>";
            var truncated = false;
            string Text(string? text)
            {
                text ??= "<none>";
                if (text.Any(char.IsControl)) text = JsonSerializer.Serialize(text);
                if (text.Length <= KernelLog.MaxLogText) return text;
                truncated = true;
                return text.Substring(0, KernelLog.MaxLogText - 1) + "…";
            }
            var target = attachment.Target;
            var values = new[] {
                RuntimeLogValue.Of("kind", Text(target.Kind)), RuntimeLogValue.Of("category", Text(target.Category)),
                RuntimeLogValue.Of("reference", Text(target.Reference)), RuntimeLogValue.Of("level", Text(target.Level)),
                RuntimeLogValue.Of("port", Text(port?.Id)), RuntimeLogValue.Of("actual", Text(actual)),
                RuntimeLogValue.Of("mode", attachment.Matcher.Scope != null ? "scope" : attachment.Holder != null ? "holder" : "subject"),
                RuntimeLogValue.Of("eventScope", Text(value.ScopeId)), RuntimeLogValue.Of("resourceScope", Text(candidate.Entry.ResourceScope)),
                RuntimeLogValue.Of("objectScope", Text(candidate.Entry.Scope)),
                RuntimeLogValue.Of("scopeMatched", validated && EventScopeMatches(candidate.Entry, value)),
                RuntimeLogValue.Of("enumMatched", validated && EnumFilterMatches(candidate.Entry, value)),
                RuntimeLogValue.Of("attachmentIndex", index), RuntimeLogValue.Of("subscription", SubscriptionGeneration),
                RuntimeLogValue.Of("truncated", truncated), RuntimeLogValue.Of("inputsValidated", validated)
            };
            var plan = candidate.Plan.Plan;
            var record = new RuntimeLogRecord
            {
                Level = RuntimeLogLevel.Trace,
                Code = "runtime.attachment-mismatch",
                Provider = Identity.Id,
                Tick = CurrentTick,
                WorldEpoch = WorldEpoch,
                Binding = value.BindingId,
                EventId = KernelLog.Redact(value.EventId),
                Plan = new RuntimeLogPlan { PlanId = plan.Id, ResourceId = plan.ResourceId, ResourceRevision = plan.ResourceRevision },
                Entry = candidate.Entry.NodeId,
                Values = KernelLog.SnapshotLogValues(values)
            };
            if (validated) log.WriteBoundedRefusal(ref slot, in record, new[] { values[4], values[12], values[13] });
            // An actual malformed/stale identity remains a refusal diagnostic, independent of the normal
            // candidate-filter budget. Its first failed values must survive a busy world's filtering traffic.
            else log.WriteRepeatedRefusal(in record, new KernelLog.RefusalSite(StepPlan: plan.Id, StepEntry: candidate.Entry.NodeId, Reason: "attachment-mismatch",
                Port: port?.Id ?? "<none>", Generation: SubscriptionGeneration, Index: index, ValueKind: (int)data.ValueKind));
        }
    }

    // Admission has already validated the payload, resolved its targets and claimed its work. Migration carries
    // that same decision across the authority epoch; it must not re-query native roots before WorldChanged.
    private DispatchResult EnqueueAdmittedEvent(string provider, long moduleGeneration, RuntimeEvent snapshot,
        IReadOnlyList<Work> work, EventSubject endSubject, IReadOnlyList<PendingPlan> planUsage)
    {
        RuntimeJson.Require(world.worldHost != false, "not-host", "This world is not authoritative.");
        RuntimeJson.Require(snapshot.WorldEpoch == WorldEpoch, "stale-world", snapshot.EventId);
        RuntimeJson.Require(snapshot.SimulationTick >= eventQueue.ReplayFloor, "event-stale", snapshot.EventId);
        RuntimeJson.Require(world.cancelled.Count == 0 || !world.cancelled.Contains((provider, snapshot.ScopeId)), "scope-cancelled", snapshot.ScopeId);
        var commandBound = 0;
        for (var i = 0; i < planUsage.Count; i++) commandBound += planUsage[i].Steps;
        for (var i = 0; i < planUsage.Count; i++)
        {
            var plan = planUsage[i];
            var limit = plan.Plan.Plan.Limits;
            RuntimeJson.Require(snapshot.CausalDepth <= limit.MaxCausalDepth, "causal-depth", plan.Plan.Plan.Id);
            RuntimeJson.Require(plan.Steps <= limit.MaxCommandsPerTick, "event-command-budget", plan.Plan.Plan.Id);
            RuntimeJson.Require(eventQueue.QueuedFor(plan.Plan) < limit.MaxQueuedEvents, "plan-queue-budget", plan.Plan.Plan.Id);
        }
        RuntimeJson.Require(commandBound <= Limits.MaxCommandsPerTick, "event-command-budget", snapshot.EventId);
        RuntimeJson.Require(kernelFlow.LedgerHasRoom(), "event-history-budget", "The replay ledger holds its maximum number of identities inside the replay horizon.");
        RuntimeJson.Require(eventQueue.queue.Count < Limits.MaxQueuedEvents, "queue-budget", snapshot.EventId);
        eventQueue.history.Add(new LedgerKey(provider, LedgerSpace.Event, snapshot.EventId), new LedgerRow(EventLedgerFingerprint(snapshot), snapshot.SimulationTick));
        eventQueue.queue.Enqueue(new Pending(provider, moduleGeneration, snapshot, work, null, ++eventQueue.sequence, true)
        {
            PlanUsage = planUsage,
            PlanUsageGeneration = world.lifecycleGeneration,
            Entity = entityLives.entityMemo,
            EntityAt = entityLives.entityMemoAt,
            EndSubject = endSubject
        },
            (snapshot.SimulationTick, eventQueue.sequence));
        // A `g-wait` that named this event resumes in the same advance the event was published in, so a message
        // and the flow that answers it are one tick's work rather than two.
        WakeWaiters(snapshot);
        log.LogTriggerFired(provider, snapshot.BindingId, snapshot.EventId,
            snapshot.CauseId, snapshot.RootEventId ?? snapshot.EventId);
        return new DispatchResult("queued", "accepted", snapshot.EventId);
    }
    private static CommandResult PreInvocationFailure(RuntimeContractException error)
    {
        var code = CommandResult.TruncateCode(error.Code);
        var detail = CommandResult.TruncateDetail(error.Message);
        return code is "scope-cancelled" or "schedule-cancelled" or "binding-lifecycle" or "stale-entity" or "stale-world"
            ? CommandResult.Cancelled(code, detail)
            : CommandResult.Rejected(code, detail);
    }
    /// <summary>
    /// The handler boundary every invoked command crosses: the result has to be a canonical command result, and the
    /// one pairing a tier narrows is checked against the tier that invoked the handler. `partial + none` is the
    /// `presentation` tier's own result — presenting is work the handler really did and none of it commits world
    /// state — so a host or owner handler that reports it is refused rather than trusted; only the tier that writes
    /// nothing may report that nothing was committed.
    /// </summary>
    internal static CommandResult NormalizeInvokedResult(CommandResult? result, bool presentation = false)
    {
        if (result == null) return CommandResult.FailedUnknown("null-result", "Handler returned null after invocation.");
        if (CommandResultRules.TryValidate(result, out var violation, presentation))
        {
            if (presentation || result.Status != CommandStatuses.Partial || result.CommitState != CommitStates.None) return result;
            violation = "partial-none-requires-presentation";
        }
        var detail = CommandResult.TruncateDetail($"Invalid handler result ({violation}): status={result.Status}, commitState={result.CommitState}.");
        return CommandResult.FailedUnknown(result.Outputs, "invalid-handler-result", detail, result.Facts.ToArray());
    }

    /// <summary>One step's written rows as the activation holds them: the descriptor they were written through, how
    /// many rows were written, and whether the handler carried them as a set. The rows themselves are the descriptor's
    /// region of the activation's result arena — every identity a row reports, its target included, is in those slots —
    /// and this record is the only internal representation of them: the receipt's rendering is produced for the
    /// boundary and is never what a step reads.</summary>
    internal sealed record ResultRowSet(ResultRowDescriptor Row, int Count, bool Set)
    {
        /// <summary>The space whose region holds the rows: this kernel's result arena. A set restored from a
        /// snapshot is given it where it is first read.</summary>
        internal FrameSpace Space { get; init; } = null!;
    }

    /// <summary>The plan-wide index of one step a `fromStepSlot` read names: the wiring addresses a step inside its
    /// own entry, while the compiled descriptor — and the row region it describes — is addressed plan-wide.</summary>
    private static int StepIndexOf(Work item, int step) => item.Entry.Steps[step].Frames.Index;

    /// <summary>
    /// The recipient bound an action is charged before anything of it runs: a step whose explicit recipients are
    /// wider than the row region its own contract reserved is refused by `result-row-budget` without the handler
    /// running and without a single row being constructed, because a set that cannot be written per recipient is a
    /// set whose command cannot be reported.
    /// </summary>
    private static void RequireRecipientBudget(ResolvedStep step, StepInputs inputs)
    {
        if (step.Frames.Row is not { } row) return;
        // The count is read off the resolved cell; only a step whose recipient input carries nothing falls back to
        // the payload its wiring names.
        if (!(row.RecipientPort.Length > 0 && inputs.TryRecipientCount(step.ResultRecipientInput, out var count, out _)))
            count = KernelExecutor.Recipients(row, step, inputs, inputs).Count;
        if (count > row.Capacity)
            throw new RuntimeContractException(RuntimeAbiCodes.ResultRowBudget,
                row.Port + " carries " + count + " explicit recipients, above the " + row.Capacity + " its contract allows.");
    }
    private void PublishConfirmedFacts(CommandResult result, string owner, long moduleGeneration, string scopeId,
        long simulationTick, List<EventReceipt> events)
    {
        if (!registry.IsRegistered(owner, moduleGeneration))
        {
            for (var i = 0; i < result.Facts.Count; i++)
            {
                var refused = "fact:" + WorldEpoch + ":" + (++eventQueue.sequence);
                log.LogEventRejected(result.Facts[i].BindingId, refused, "module-unregistered", owner);
                kernelScheduler.AddReceipt(events, refused, "rejected", "module-unregistered");
            }
            return;
        }
        var handle = new RuntimeModuleHandle(this, owner, moduleGeneration);
        for (var i = 0; i < result.Facts.Count; i++)
        {
            var fact = result.Facts[i];
            var factId = "fact:" + WorldEpoch + ":" + (++eventQueue.sequence);
            try
            {
                var dispatch = PublishFact(handle, fact, factId, simulationTick, scopeId);
                if (dispatch.Status == "rejected") kernelScheduler.AddReceipt(events, factId, dispatch.Status, dispatch.Code);
            }
            catch (Exception error)
            {
                var code = error is RuntimeContractException contract ? CommandResult.TruncateCode(contract.Code) : "fact-publish-failed";
                log.LogEventRejected(fact.BindingId, factId, code);
                kernelScheduler.AddReceipt(events, factId, "rejected", code);
            }
        }
    }
    /// <summary>One committed fact as an event. A fact its handler wrote goes back onto its binding's sheet and is
    /// published as a written event is, so no document is made of it; a fact that arrived as a document — an
    /// owner's result off the network, a migration image — is published as that document.</summary>
    private DispatchResult PublishFact(RuntimeModuleHandle handle, RuntimeFact fact, string factId, long simulationTick, string scopeId)
    {
        if (fact.Cells is { } cells && fact.Gate is { } gate && ReferenceEquals(gate.Kernel, this)
            && gate.EventValues is { } sheet && ReferenceEquals(sheet.Ids, fact.Ids))
        {
            Array.Copy(cells, sheet.Cells, cells.Length);
            return Raise(handle, gate, factId, WorldEpoch, simulationTick, scopeId, null, null, 0);
        }
        return Publish(handle, new RuntimeEvent(factId, fact.BindingId, WorldEpoch, simulationTick, scopeId, fact.Outputs));
    }
    /// <summary>The kernel's own event on one of its bindings, begun on that binding's sheet: the values are written
    /// by output position and <see cref="Raise"/> publishes them.</summary>
    private RuntimeSubscriptionGate BeginOwnEvent(RuntimeModuleHandle handle, string bindingId, out EvaluationOutputs values)
    {
        if (!subscriptionGates.TryGetValue(bindingId, out var gate)) gate = SubscriptionGate(handle.ProviderId, bindingId);
        GateTrigger(handle, gate);
        values = gate.EventValues!;
        values.Begin();
        return gate;
    }

    internal static bool ClaimsGate(IReadOnlyList<Work> work)
    {
        for (var i = 0; i < work.Count; i++) if (work[i].Entry.Gate is { IsDefault: false }) return true;
        return false;
    }
    /// <summary>
    /// The tick's `debounce` releases: every quiet window that has passed is dispatched here, inside the kernel's
    /// own advance, so a debounced card fires `debounce` ticks after its last arrival instead of waiting for an
    /// event that may never come — and an entry point nothing else would ever re-enter does run again.
    ///
    /// A release is the arrival that opened the window, re-dispatched under an identity of its own: same provider,
    /// same scope, same frame, and the one plan entry the window was opened for. The gate is not asked again — the
    /// store charged the card when the window came due — so what is queued here is an ordinary entry of one plan,
    /// and a plan that unloaded since is dropped by the same walk that drops any other stale work.
    /// </summary>
    private void QueueGateReleases(List<TriggerGateReceipt> gateRefusals)
    {
        if (!kernelVariables.triggerGates.HasReleases) return;
        foreach (var release in kernelVariables.triggerGates.DueReleases(CurrentTick, Math.Max(0, Limits.MaxQueuedEvents - eventQueue.queue.Count)))
        {
            if (release.Refusal is { } refusal)
            {
                // The window came due and the card was not allowed to fire after all: the refusal is reported
                // against the arrival that opened the window, which is the event the author is looking at.
                gateRefusals.Add(refusal);
                if (release.Payload is GateReleasePayload refused)
                    LogGateRefused(refusal, refused.Origin.Event.BindingId, refused.Origin.Event.EventId);
                continue;
            }
            if (release.Payload is not GateReleasePayload captured) continue;
            var origin = captured.Origin;
            var value = origin.Event with
            {
                EventId = "gate:" + release.PlanId + "." + release.NodeId + "@" + release.Subject + ":" + release.Generation,
                SimulationTick = CurrentTick,
                RootEventId = origin.Event.RootEventId ?? origin.Event.EventId
            };
            var work = new[] { captured.Item };
            var order = ++eventQueue.sequence;
            eventQueue.queue.Enqueue(new Pending(origin.Provider, origin.Generation, value, work, null, order,
                true, null, false,
                new GateReleaseTicket(release.PlanId, release.NodeId, release.Subject, release.Generation)),
                (CurrentTick, order));
            log.LogTriggerFired(origin.Provider, value.BindingId, value.EventId, value.CauseId, value.RootEventId ?? value.EventId);
        }
    }
    /// <summary>
    /// The entity a per-instance trigger option is booked against: the entity this plan's own mount target accepted
    /// for this event, which is the instance the card counts and the one whose death drops the count. Only a
    /// per-instance gate is asked, because that is the only scope whose subject is the mount rather than the event.
    /// </summary>
    internal static EntityReference? GateInstance(TriggerGateOptions gate, Work item)
        => gate.Scope == TriggerGateContract.InstanceScope && item.Subjects.Count == 1 ? item.Subjects[0] : null;
    /// <summary>The work of one dispatch grouped by plan, in the order the plans were first claimed, with each
    /// plan's command bound. One pass, no grouping object and no closure: the numbers are read as often as the
    /// dispatch is asked about its budget.</summary>
    internal static IReadOnlyList<PendingPlan> PlanUsageOf(IReadOnlyList<Work> work)
    {
        var usage = new List<PendingPlan>();
        for (var i = 0; i < work.Count; i++)
        {
            var index = -1;
            for (var j = 0; j < usage.Count; j++) if (ReferenceEquals(usage[j].Plan, work[i].Plan)) { index = j; break; }
            if (index < 0) usage.Add(new PendingPlan(work[i].Plan, work[i].Entry.DispatchableStepCount));
            else usage[index] = usage[index] with { Steps = usage[index].Steps + work[i].Entry.DispatchableStepCount };
        }
        return usage;
    }
    internal static bool NamesPlan(Pending pending, LoadedPlan plan)
    {
        if (pending.PlanUsage is { } usage)
        {
            for (var i = 0; i < usage.Count; i++) if (ReferenceEquals(usage[i].Plan, plan)) return true;
            return false;
        }
        for (var i = 0; i < pending.Work.Count; i++) if (ReferenceEquals(pending.Work[i].Plan, plan)) return true;
        return false;
    }
    /// <summary>
    /// The first half of every step's command identity for one dispatch: the publisher and the event, each quoted
    /// by the JSON encoder. The identity a receipt carries is the same text it always was — the whole identity is
    /// `[publisher,event,plan,node]` — but only this half is per dispatch: the plan and the node half is decided
    /// when the plan loads, so composing an identity never serializes a document again.
    /// </summary>
    private static string CommandPrefixOf(Pending pending)
        => "[" + RuntimeJson.Quote(pending.Provider) + "," + RuntimeJson.Quote(pending.Event.EventId);
    /// <summary>One step's whole command identity: the dispatch's half and the step's own, written as one text. A
    /// publisher and an event the encoder spells as themselves are written straight into the identity between their
    /// quotes, so a step composes one string; any other spelling goes through the encoder.</summary>
    private static string CommandIdOf(Pending pending, string suffix) => CommandIdOf(pending.Provider, pending.Event.EventId, suffix);

    internal static string CommandIdOf(string provider, string eventId, string suffix)
    {
        if (!RuntimeJson.Plain(provider) || !RuntimeJson.Plain(eventId))
            return string.Concat("[", RuntimeJson.Quote(provider), ",", RuntimeJson.Quote(eventId), suffix);
        return string.Create(provider.Length + eventId.Length + suffix.Length + 6, (provider, eventId, suffix), static (text, parts) =>
        {
            // ["provider","event" followed by the step's own half.
            text[0] = '['; text[1] = '"';
            parts.provider.AsSpan().CopyTo(text[2..]);
            var at = 2 + parts.provider.Length;
            text[at++] = '"'; text[at++] = ','; text[at++] = '"';
            parts.eventId.AsSpan().CopyTo(text[at..]);
            at += parts.eventId.Length;
            text[at++] = '"';
            parts.suffix.AsSpan().CopyTo(text[at..]);
        });
    }

    /// <summary>One step's command identity as the walk carries it: the dispatch and the step's own half, composed
    /// into text only when something writes it out (a log record, a trace, a receipt, the wire, an effect layer), so a
    /// step nobody reports on composes nothing. Held in a local and passed by reference, so a step that writes its
    /// identity twice composes it once.</summary>
    internal struct StepCommand
    {
        private readonly string provider, eventId, suffix;
        private readonly long? round;
        private string? text;
        internal StepCommand(string provider, string eventId, string suffix, long? round = null)
        { this.provider = provider; this.eventId = eventId; this.suffix = suffix; this.round = round; text = null; }
        /// <summary>An identity that was already written out.</summary>
        internal static StepCommand Of(string text) => new(null!, null!, null!) { text = text };
        internal string Id => text ??= CommandIdOf(provider, eventId, suffix, round);
        internal string Provider => provider;
        internal string Suffix => suffix;
        /// <summary>The loop rounds the step runs in, folded into one number (<see cref="WalkRound"/>); null outside a loop.</summary>
        internal long? Round => round;
    }

    private static StepCommand StepCommandOf(Pending pending, string suffix, long? round = null)
        => new(pending.Provider, pending.Event.EventId, suffix, round);

    /// <summary>One step's identity inside loop rounds: the step's own half closed by the folded rounds. Only an identity that
    /// is written out (wire, receipt, log, migration) is composed, so the walk itself keeps the number and builds no text.</summary>
    internal static string CommandIdOf(string provider, string eventId, string suffix, long? round)
        => round is not { } rounds ? CommandIdOf(provider, eventId, suffix)
            : CommandIdOf(provider, eventId, string.Concat(suffix.AsSpan(0, suffix.Length - 1), ",\"round:",
                rounds.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), "\"]"));
    /// <summary>
    /// The replay ledger's value for one canonical text. The ledger only ever compares it with the entry the same
    /// identity already recorded — nothing carries it to the wire, a log record or a receipt — so a local 128-bit
    /// hash of the canonical text is enough and is computed without a digest. Identity that does travel keeps
    /// <see cref="Fingerprint"/>'s SHA-256: a schedule's own identity is part of the event identity its pulses and
    /// receipts report.
    /// </summary>
    internal static LedgerHash LedgerFingerprint(string canonical)
    {
        var low = 14695981039346656037UL; var high = 1099511628211UL;
        RawJson.Mix(ref low, ref high, canonical);
        return new LedgerHash(low, high);
    }
    internal static LedgerHash PulseLedgerFingerprint(RuntimeEvent value)
    {
        var low = 14695981039346656037UL; var high = 1099511628211UL;
        RawJson.MixUtf8(ref low, ref high, RuntimeJson.StableEventBytes(value));
        return new LedgerHash(low, high);
    }
}
