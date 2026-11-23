using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>One tick's accounting: the per-tick event and command counts and their budget refusal, the receipts a tick collects and the result it hands back, the plans an admitted event is grouped by, the trigger-gate questions asked this tick, the deferred command results and the presentation outputs waiting to leave. Taking due events off the queue and walking them is driven from the facade, which owns the observers and the handlers' kernel.</summary>
internal sealed class KernelScheduler
{
    private readonly KernelWorld world;
    private readonly KernelRegistry registry;
    private readonly KernelLog log;
    private readonly KernelPlans plans;
    private readonly KernelExecutor executor;
    private readonly EventQueue eventQueue;
    private readonly KernelVariables kernelVariables;
    private readonly KernelEffects kernelEffects;
    private readonly KernelOwner kernelOwner;
    private readonly KernelFlow kernelFlow;
    private readonly RuntimeLimits limits;

    internal KernelScheduler(KernelWorld world, KernelRegistry registry, KernelLog log, KernelPlans plans, KernelExecutor executor, EventQueue eventQueue, KernelVariables kernelVariables, KernelEffects kernelEffects, KernelOwner kernelOwner, KernelFlow kernelFlow, RuntimeLimits limits)
    {
        this.world = world;
        this.registry = registry;
        this.log = log;
        this.plans = plans;
        this.executor = executor;
        this.eventQueue = eventQueue;
        this.kernelVariables = kernelVariables;
        this.kernelEffects = kernelEffects;
        this.kernelOwner = kernelOwner;
        this.kernelFlow = kernelFlow;
        this.limits = limits;
    }

    internal TickResult? deferredTickResult;

    // `receipts: false` is the host's per-frame advance: it reads only the tick's presentation and owner outputs, so the
    // receipts a test or tool would inspect are not built or retained for it. Execution rows remain in their typed
    // activation arena, independently of whether the caller requested a diagnostic receipt.
    internal bool collectReceipts = true;

    // Event receipts exist only to be copied into a caller's TickResult; an advance that takes none records none.
    internal void AddReceipt(List<EventReceipt>? events, string eventId, string status, string code)
    {
        if (collectReceipts) events?.Add(new EventReceipt(eventId, status, code));
    }

    internal void BeginAdvanceTick(long simulationTick, bool isHost)
    {
        if (simulationTick > world.Tick)
        {
            world.Tick = simulationTick; eventsThisTick = commandsThisTick = kernelFlow.scheduledThisTick = 0; deferredLogged = false; plans.TickUsage.Clear();
            if (world.Tick >= eventQueue.nextLedgerSweepTick) kernelFlow.SweepLedger();
        }
        world.worldHost = isHost;
        // The intents belong to this advance: a caller that never took them asked for nothing to be presented.
        presentationOutputs.Clear();
        presentationOutputs.AddRange(kernelEffects.pendingSoundStops);
        kernelEffects.pendingSoundStops.Clear();
        // The variable touch set is deliberately not reset here: it is what no body has carried yet, so an advance
        // that happens while the host is still handing over a complete document leaves its writes owed and the next
        // delta after that document carries them.
        // The same rule for the owner tier's dispatches: they are this advance's decisions and nothing else's.
        kernelOwner.ownerOutputs.Clear();
        // The five receipt lists are the kernel's own and are refilled by every advance; the result is handed
        // copies of what this advance wrote, so a caller keeps its receipts after the next advance starts.
        tickCommands.Clear(); tickEvents.Clear(); tickSchedules.Clear(); tickLeases.Clear(); tickGateRefusals.Clear();
    }

    internal TickResult DrainClientTick(bool receipts)
    {
        foreach (var job in kernelFlow.schedules.Values) tickSchedules.Add(kernelFlow.ScheduleReport(job, "cancelled", "not-host"));
        foreach (var lease in kernelVariables.stateLeases.Values) tickLeases.Add(new StateLeaseReceipt(lease.Handle.ProviderId, lease.Handle.LeaseId, lease.Key.Definition, lease.Key.Target, "cancelled", "not-host"));
        kernelFlow.StopScheduledSource(null, null, "not-host"); kernelVariables.StopStateSource(null, null, "not-host");
        kernelEffects.StopOwnedHandleSource(null, null, "not-host");
        // A non-host advance clears the queue, and every dropped event is recorded once.
        while (eventQueue.queue.TryDequeue(out var stale, out _))
        {
            log.LogEventRejected(stale.Event.BindingId, stale.Event.EventId, "not-host", stale.Provider);
            AddReceipt(tickEvents, stale.Event.EventId, "rejected", "not-host");
        }
        if (!receipts || tickCommands.Count == 0 && tickEvents.Count == 0 && tickSchedules.Count == 0 && tickLeases.Count == 0)
            return EmptyTickResult;
        return new TickResult(0, 0, 0, RuntimeKernel.Copy(tickCommands), RuntimeKernel.Copy(tickEvents))
        { Schedules = RuntimeKernel.Copy(tickSchedules), StateLeases = RuntimeKernel.Copy(tickLeases) };
    }

    internal TickResult CompleteAdvanceTick(int processed, int executed, List<CommandReceipt> commands,
        List<EventReceipt> events, List<ScheduleReceipt> scheduleReports, List<StateLeaseReceipt> leaseReports,
        List<TriggerGateReceipt> gateRefusals)
    {
        if (processed == 0 && executed == 0 && commands.Count == 0 && events.Count == 0
            && scheduleReports.Count == 0 && leaseReports.Count == 0 && gateRefusals.Count == 0
            && presentationOutputs.Count == 0 && kernelOwner.ownerOutputs.Count == 0)
            return QuietTickResult();
        return new TickResult(processed, executed, eventQueue.queue.Count, RuntimeKernel.Copy(commands), RuntimeKernel.Copy(events))
        {
            Schedules = RuntimeKernel.Copy(scheduleReports),
            StateLeases = RuntimeKernel.Copy(leaseReports),
            GateRefusals = RuntimeKernel.Copy(gateRefusals),
            // The intents are handed out with the tick that decided them, which is what makes "the host decided
            // the timing" true: a caller that reads this result holds exactly the presentations of this advance.
            Presentations = presentationOutputs.Count == 0 ? Array.Empty<PresentationOutput>() : presentationOutputs.ToArray(),
            // The owner dispatches travel with the tick that decided them, for the same reason: the host decided
            // the timing, and a caller that reads this result holds exactly the commands of this advance.
            OwnerCommands = kernelOwner.ownerOutputs.Count == 0 ? Array.Empty<OwnerOutput>() : kernelOwner.ownerOutputs.ToArray()
        };
    }

    // The host's result: counts and the outputs this advance decided, without receipt copies. A tick that decided
    // no presentation and no owner dispatch returns the shared empty result and allocates nothing.
    internal TickResult CompleteOutputsTick(int processed, int executed)
    {
        if (presentationOutputs.Count == 0 && kernelOwner.ownerOutputs.Count == 0) return EmptyTickResult;
        return new TickResult(processed, executed, eventQueue.queue.Count, Array.Empty<CommandReceipt>(), Array.Empty<EventReceipt>())
        {
            Presentations = presentationOutputs.Count == 0 ? Array.Empty<PresentationOutput>() : presentationOutputs.ToArray(),
            OwnerCommands = kernelOwner.ownerOutputs.Count == 0 ? Array.Empty<OwnerOutput>() : kernelOwner.ownerOutputs.ToArray()
        };
    }

    internal TickResult QuietTickResult()
    {
        if (eventQueue.queue.Count == 0) return EmptyTickResult;
        // Quiet results contain only immutable counts; active receipts remain independent snapshots.
        if (deferredTickResult?.DeferredEvents == eventQueue.queue.Count) return deferredTickResult;
        return deferredTickResult = new TickResult(0, 0, eventQueue.queue.Count,
            Array.Empty<CommandReceipt>(), Array.Empty<EventReceipt>());
    }

    internal int eventsThisTick, commandsThisTick;

    /// <summary>`event.deferred` is one record per tick even when several events wait on it.</summary>
    internal bool deferredLogged;

    /// <summary>The budget reason that keeps the head event queued for the next tick, or null when it can dispatch now,
    /// in the order the dispatch loop has always evaluated them (global event, global command, then per plan). The first
    /// refused condition is the reason, so the code names what actually stopped it; the caller writes at most one
    /// `event.deferred` per tick.</summary>
    internal string? BudgetRefusal(Pending pending)
    {
        var groups = PendingPlans(pending, out _);
        var count = 0;
        for (var i = 0; i < groups.Count; i++) count += groups[i].Steps;
        if (eventsThisTick >= limits.MaxEventsPerTick) return "tick-event-budget";
        if (commandsThisTick + count > limits.MaxCommandsPerTick) return "tick-command-budget";
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var used = group.Plan.TickUsage;
            if (used.Events >= group.Plan.Plan.Limits.MaxEventsPerTick) return "plan-tick-event-budget";
            if (used.Commands + group.Steps > group.Plan.Plan.Limits.MaxCommandsPerTick) return "plan-tick-command-budget";
        }
        return null;
    }

    /// <summary>The lifecycle moved since the grouping was decided: drop the work whose plan unloaded. Kept out of
    /// <see cref="PendingPlans"/> because these lambdas capture `pending`, which makes the compiler allocate a closure
    /// on every <see cref="PendingPlans"/> call.</summary>
    internal IReadOnlyList<Work> RegroupAdmitted(Pending pending, IReadOnlyList<Work> admitted)
    {
        pending.Admitted = admitted = admitted.Where(item => plans.Loaded.TryGetValue(item.Plan.Plan.Id, out var loaded)
            && ReferenceEquals(loaded, item.Plan)).ToArray();
        pending.PlanUsage = PlanUsageOf(admitted);
        pending.PlanUsageGeneration = world.lifecycleGeneration;
        pending.GateRefusals?.RemoveAll(refusal => !pending.Work.Any(item => item.Plan.Plan.Id == refusal.PlanId
            && item.Entry.NodeId == refusal.NodeId && plans.Loaded.TryGetValue(item.Plan.Plan.Id, out var loaded)
            && ReferenceEquals(loaded, item.Plan)));
        return admitted;
    }

    /// <summary>
    /// This dispatch's per-plan share and the work items still owned by a loaded plan, at the generation they were
    /// derived at. The claimed work is a decision of the enqueue and cannot change; the one thing that can retire it
    /// before it is walked is a plan unloading, which is exactly what the lifecycle generation reports. While the
    /// number stands still the enqueue's own grouping is the answer, and the same question asked twice — by the
    /// budget check and then by the walk — is answered once.
    ///
    /// The gate is asked here rather than at the enqueue because it is a decision about a dispatch, not about a
    /// claim, and it is asked exactly once because it charges what it admits: <see cref="Pending.Admitted"/> is the
    /// answer across budget ticks and lifecycle regrouping. A plan unloading only removes its decided work;
    /// surviving entries never pay the same gate twice.
    /// </summary>
    internal IReadOnlyList<PendingPlan> PendingPlans(Pending pending, out IReadOnlyList<Work> active)
    {
        if (pending.Admitted is { } admitted)
        {
            if (pending.PlanUsageGeneration != world.lifecycleGeneration) admitted = RegroupAdmitted(pending, admitted);
            active = admitted; return pending.PlanUsage!;
        }
        // A dispatch no gate is asked about, whose grouping was computed at the generation that still stands, is
        // walked as it was claimed: no plan it claimed can have unloaded without the generation moving.
        // Only an entry this dispatch claimed can gate it: a gate on some other card leaves this one on the fast path.
        var gated = pending.Schedule?.Resume == null && pending.Release == null && pending.Wait == null && ClaimsGate(pending.Work);
        if (!gated && pending.PlanUsage != null && pending.PlanUsageGeneration == world.lifecycleGeneration)
        {
            pending.GateRefusals = null;
            pending.Admitted = active = pending.Work; pending.DecidedTick = world.Tick;
            return pending.PlanUsage;
        }
        var live = new List<Work>();
        for (var i = 0; i < pending.Work.Count; i++)
            if (plans.Loaded.TryGetValue(pending.Work[i].Plan.Plan.Id, out var loaded) && ReferenceEquals(loaded, pending.Work[i].Plan)) live.Add(pending.Work[i]);
        active = live;
        pending.GateRefusals = null;
        // A release is the activation a quiet window already earned: it is not a second arrival at the card, so the
        // gate is not asked again and the entry is walked as it was claimed. A `g-wait` resumption is the same
        // continuation of an activation a gate already admitted — this rule's own reason, and the condition that
        // carries it — so a gate can never be the reason a resumed wait is dropped.
        if (gated) active = GateWork(pending, live);
        var usage = PlanUsageOf(active);
        pending.Admitted = active; pending.PlanUsage = usage;
        pending.PlanUsageGeneration = world.lifecycleGeneration; pending.DecidedTick = world.Tick;
        return usage;
    }

    /// <summary>
    /// Drops the work items whose entry point's own trigger options refuse this activation, asking each entry once
    /// however many subjects it claimed. The question is per activation, not per subject: a threshold counts the
    /// events a card matched, so one event that claims three enemies is one arrival.
    ///
    /// Only an event dispatch is gated, including a provider schedule's new event occurrence. A control schedule's
    /// Resume and a `g-wait` resumption continue an activation already admitted, so neither is another arrival.
    /// </summary>
    // The entry's own declared options are the gate table's row (TriggerGates.Declare keeps exactly the non-default ones).
    // Reused by every GateWork call; a gate resolver cannot re-enter GateWork, which runs only from PendingPlans.
    internal readonly HashSet<(LoadedPlan Plan, string Node)> gateAsked = new();

    internal List<Work> GateWork(Pending pending, List<Work> live)
    {
        var admitted = new List<Work>(live.Count);
        // The entry points already asked, so one activation that claimed several subjects is one arrival: a
        // threshold counts the events a card matched, not the entities each of them expanded to.
        var asked = gateAsked; asked.Clear();
        for (var i = 0; i < live.Count; i++)
        {
            var item = live[i];
            var planId = item.Plan.Plan.Id;
            // The gate was resolved onto the entry when the plan loaded; an entry without one is walked as claimed.
            if (item.Entry.Gate is not { IsDefault: false } gate) { admitted.Add(item); continue; }
            if (!asked.Add((item.Plan, item.Entry.NodeId))) { admitted.Add(item); continue; }
            var player = GatePlayer(gate, pending.Event, item);
            // A `debounce` gate answers with a window instead of a verdict: what the release will re-run is this
            // arrival and this entry, and the store keeps that capture with the booked set so a released plan or a
            // world boundary drops the window with the counter it belongs to.
            var capture = gate.Debounce == null ? null : new GateReleasePayload(pending, item);
            var verdict = kernelVariables.triggerGates.Admit(planId, item.Entry.NodeId, gate, world.Tick, player,
                GateInstance(gate, item), pending.Event.ScopeId, GateResolver(gate, item),
                CellNumber(executor.EventCell(pending.Event, item.Entry.GateAmountCell)), capture,
                pending.GateRefusals ??= new List<TriggerGateReceipt>());
            if (verdict.Admitted) admitted.Add(item);
            // A moved window leaves the release already queued behind it: the compaction is the one place a queued
            // entry stops occupying queue capacity without being dispatched.
            else if (verdict.Deferred) eventQueue.queueDirty = true;
        }
        // Nothing was refused: the caller keeps the list it built, with no second copy of a dispatch's work.
        return admitted.Count == live.Count ? live : admitted;
    }

    /// <summary>The resolver a per-instance gate asks whether its booked entity is still part of the world: the
    /// entity's own kind decides, and a kind nothing resolves counts as current.</summary>
    internal Func<EntityReference, bool>? GateResolver(TriggerGateOptions gate, Work item)
    {
        if (gate.Scope != TriggerGateContract.InstanceScope || item.Subjects.Count != 1) return null;
        if (!registry.Tables.Resolvers.TryGetValue(RuntimeJson.KindOf(item.Subjects[0].Id), out var resolver)) return null;
        // A resolver that throws cannot prove its entity is still here, so the booked set is treated as gone (the
        // store drops only what is provably stale) and the fault is recorded.
        return entity =>
        {
            try { return resolver.Resolve(entity); }
            catch (Exception error)
            {
                log.LogCallbackFailed("trigger-gate-resolver-failed entity=" + entity.Id, error);
                return false;
            }
        };
    }

    /// <summary>
    /// The entity a per-player trigger option is charged against: the event's own `instigator` where it names one,
    /// and otherwise the entity the plan's mount accepted for this event. A player-caused event carries the player,
    /// and an event about one entity is charged to that entity — both are "the player this is about", and a gate
    /// with a per-player booking refuses rather than guesses where the event carries neither. Only a per-player gate
    /// is asked: the other two scopes book the level and the mount, and neither reads the event's actors.
    /// The subject must be a player: an enemy or an object found that way is not "the player this is about", and
    /// booking it would grow the gate by one set per enemy for the whole level until the per-gate cap refused every
    /// newcomer. Such an arrival is refused as `gate-scope`, the answer an event naming nobody already gets.
    /// </summary>
    internal EntityReference? GatePlayer(TriggerGateOptions gate, RuntimeEvent value, Work item)
    {
        if (gate.Scope != TriggerGateContract.PlayerScope) return null;
        executor.CellsOf(value);
        // An enemy that caused an event about a player does not stand in for that player: the instigator counts only
        // when it is a player, otherwise the one entity the mount accepted is asked.
        var instigator = RuntimeActorRoles.FromTriggerEvent(value, item.Subjects).Get("instigator");
        if (IsPlayer(instigator)) return instigator;
        var subject = item.Subjects.Count == 1 ? item.Subjects[0] : null;
        return IsPlayer(subject) ? subject : null;
    }

    private static bool IsPlayer(EntityReference? entity)
        => entity != null && entity.Id.StartsWith(PlayerIdPrefix, StringComparison.Ordinal);

    private const string PlayerIdPrefix = EntityKinds.Player + ":";

    internal Dictionary<DeferredKey, DeferredCommand> deferredCommands = new();

    internal Dictionary<DeferredKey, string> deferredCompleted = new();

    internal Queue<CommandReceipt> deferredReceipts = new();

    internal List<(DeferredCommand Entry, string Code)> deferredEnding = new();

    internal void FinalizeDeferredResults(CommandContext? context, CommandResult result)
    {
        if (context?.DeferredResults is not { Count: > 0 } tokens) return;
        var keep = result.CommitState == CommitStates.Confirmed
            && result.Status is CommandStatuses.Succeeded or CommandStatuses.Partial;
        foreach (var token in tokens)
        {
            var key = Key(token);
            if (!deferredCommands.TryGetValue(key, out var entry) || entry.Started) continue;
            if (!keep) { deferredCommands.Remove(key); continue; }
            if (TryTargetRow(result.Outputs, entry.ResultPort, token.Target, out var row, out var set))
            { entry.InitialRow = row.Clone(); entry.RowSet = set; }
            entry.Started = true;
        }
    }

    internal void FlushDeferredResults()
    {
        while (deferredReceipts.TryDequeue(out var receipt)) tickCommands.Add(receipt);
    }

    internal void DetachDeferredPlan(string plan)
    {
        foreach (var entry in deferredCommands.Values.Where(entry => entry.Plan.PlanId == plan && entry.Started))
        {
            RuntimeJson.Require(entry.DetachedContract.ValueKind == JsonValueKind.Object,
                "deferred-result-schema", entry.Token.CommandId);
            entry.DetachedContract = entry.DetachedContract.Clone();
            entry.Detached = true;
        }
        foreach (var key in deferredCommands.Where(entry => entry.Value.Plan.PlanId == plan && !entry.Value.Started)
            .Select(entry => entry.Key).ToArray()) deferredCommands.Remove(key);
    }

    internal object CaptureDeferredResults() => new
    {
        active = deferredCommands.Values.Where(entry => entry.Token.WorldEpoch == world.WorldEpoch
                && entry.Token.Target.WorldEpoch == world.WorldEpoch)
            .OrderBy(entry => entry.Token.CommandId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Token.Target.Id, StringComparer.Ordinal).Select(entry => new
            {
                token = TokenValue(entry.Token),
                provider = entry.Provider,
                origin = entry.Origin,
                planId = entry.Plan.PlanId,
                resourceId = entry.Plan.ResourceId,
                resourceRevision = entry.Plan.ResourceRevision,
                entry = entry.Entry,
                node = entry.Node,
                binding = entry.Binding,
                resultPort = entry.ResultPort,
                target = entry.Target,
                initialRow = entry.InitialRow,
                rowSet = entry.RowSet,
                detached = entry.Detached,
                contract = entry.Detached ? entry.DetachedContract : (JsonElement?)null
            }).ToArray(),
        completed = deferredCompleted.Where(entry => entry.Key.WorldEpoch == world.WorldEpoch
                && entry.Key.TargetWorldEpoch == world.WorldEpoch)
            .OrderBy(entry => entry.Key.CommandId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.TargetId, StringComparer.Ordinal).Select(entry => new
            {
                token = TokenValue(new DeferredCommandToken(entry.Key.CommandId, entry.Key.WorldEpoch,
                    new EntityReference(entry.Key.TargetId, entry.Key.TargetWorldEpoch, entry.Key.TargetLifeEpoch))),
                provider = entry.Value
            }).ToArray(),
        receipts = deferredReceipts.Where(row => row.WorldEpoch == world.WorldEpoch).Select(CaptureMigrationReceipt).ToArray()
    };

    /// <summary>This component's parts of a host-migration image: the deferred results still owed and what this tick
    /// has already spent of its budgets, the flow's scheduled pulses and each plan's own cell included.</summary>
    internal void CaptureMigration(Dictionary<string, object?> image)
    {
        image["deferredResults"] = CaptureDeferredResults();
        image["tickUsage"] = new
        {
            events = eventsThisTick,
            commands = commandsThisTick,
            scheduled = kernelFlow.scheduledThisTick,
            plans = plans.TickUsage.Select(row => new { plan = row.Key, events = row.Value.Events, commands = row.Value.Commands }).ToArray()
        };
    }

    /// <summary>Reads the tick budgets of an image into this staged component and, on the machine that becomes host,
    /// the deferred results the facade already validated against the loaded plans.</summary>
    internal void RestoreMigration(JsonElement snapshot, DeferredImage deferred, bool isHost)
    {
        var usage = snapshot.GetProperty("tickUsage");
        RuntimeJson.Shape(usage, "events commands scheduled plans");
        eventsThisTick = (int)RuntimeJson.Integer(usage.GetProperty("events"), 0, limits.MaxEventsPerTick);
        commandsThisTick = (int)RuntimeJson.Integer(usage.GetProperty("commands"), 0, limits.MaxCommandsPerTick);
        kernelFlow.scheduledThisTick = (int)RuntimeJson.Integer(usage.GetProperty("scheduled"), 0, MaximumScheduledPulsesPerTick);
        // The parsed snapshot is byte-bounded. Usage includes unloaded plans that already spent this tick's budget.
        var usagePlans = usage.GetProperty("plans");
        RuntimeJson.Require(usagePlans.ValueKind == JsonValueKind.Array, "invalid-array", "plans");
        foreach (var row in usagePlans.EnumerateArray())
        {
            RuntimeJson.Shape(row, "plan events commands");
            plans.TickUsage.Add(RuntimeJson.Text(row, "plan"),
                ((int)RuntimeJson.Integer(row.GetProperty("events"), 0, limits.MaxEventsPerTick),
                 (int)RuntimeJson.Integer(row.GetProperty("commands"), 0, limits.MaxCommandsPerTick)));
        }
        if (!isHost) return;
        // External lifetime probes are bound only after the live world commits.
        foreach (var entry in deferred.Active) deferredCommands.Add(Key(entry.Token), entry);
        foreach (var entry in deferred.Completed) deferredCompleted.Add(entry.Key, entry.Value);
        foreach (var receipt in deferred.Receipts) deferredReceipts.Enqueue(receipt);
        deferredEnding.EnsureCapacity(deferredCommands.Count);
    }

    internal string CaptureDeferredCheckpoint(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        values.Add("deferredResults", RuntimeJson.From(CaptureDeferredResults()));
        return JsonSerializer.Serialize(values);
    }

    /// <summary>Where an effect pulse's own command receipts go while a host advance runs, and the event rows a
    /// pulse's confirmed facts are published into: both are the running advance's, so they are set at its start and
    /// cleared with it. Null outside an advance, which is where a pulse cannot happen at all.</summary>
    internal List<CommandReceipt>? pulseReceipts;

    internal List<EventReceipt>? pulseEvents;

    internal List<PresentationOutput> presentationOutputs = new();

    internal readonly List<CommandReceipt> tickCommands = new();

    internal readonly List<EventReceipt> tickEvents = new();

    internal readonly List<ScheduleReceipt> tickSchedules = new();

    internal readonly List<StateLeaseReceipt> tickLeases = new();

    internal readonly List<TriggerGateReceipt> tickGateRefusals = new();

    /// <summary>Exchanges the state a host migration swaps with the staged image's. Nothing runs between the
    /// assignments; the image keeps the prior state for rollback.</summary>
    internal void SwapWith(KernelScheduler other)
    {
        (deferredCommands, other.deferredCommands) = (other.deferredCommands, deferredCommands);
        (deferredCompleted, other.deferredCompleted) = (other.deferredCompleted, deferredCompleted);
        (deferredReceipts, other.deferredReceipts) = (other.deferredReceipts, deferredReceipts);
        (deferredEnding, other.deferredEnding) = (other.deferredEnding, deferredEnding);
        (presentationOutputs, other.presentationOutputs) = (other.presentationOutputs, presentationOutputs);
        (eventsThisTick, other.eventsThisTick) = (other.eventsThisTick, eventsThisTick);
        (commandsThisTick, other.commandsThisTick) = (other.commandsThisTick, commandsThisTick);
        (deferredLogged, other.deferredLogged) = (other.deferredLogged, deferredLogged);
    }
}
