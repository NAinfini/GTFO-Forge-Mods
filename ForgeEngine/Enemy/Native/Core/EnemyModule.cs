using System;
using System.Collections.Generic;
using System.Globalization;
using Agents;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using SNetwork;
using UnityEngine;
using ForgeCore;

namespace ForgeEnemy.Native;

/// <summary>GTFO receiver implementation. Entity kind is a supported API boundary, never an allegiance rule.</summary>
internal sealed partial class EnemyModule : IDisposable
{
    internal const string ProviderId = ModuleDefinition.ProviderId;
    internal const string DamageBinding = ProviderId + ".binding.damage_applied";
    private const string EnemyTypeAttachment = "enemy-type";
    internal const string HealBinding = ProviderId + ".binding.heal";
    internal const string DamageActionBinding = ProviderId + ".binding.damage";
    internal const string DamageActionHandler = "gtfo.enemy.damage";
    internal const string HealthChangedBinding = ProviderId + ".binding.health_changed";
    internal const double MinimumAmount = -float.MaxValue;
    internal const double MaximumAmount = float.MaxValue;
    private const double DamageMinimumAmount = 0;
    private const double DamageMaximumAmount = float.MaxValue;
    /// <summary>The refusal of a recipient this provider cannot resolve. A reference of another kind names
    /// something this provider does not serve; an enemy reference that no longer resolves names a life that ended
    /// after the kernel admitted the command.</summary>
    internal static string UnresolvedCode(EntityReference target)
        => EntityKinds.Is(target, EntityKinds.Enemy) ? ResultCode.StaleEntity : ResultCode.UnsupportedRecipient;
    /// <summary>Members of the declared `damage_kind` enum set, in its own declaration order; a name outside it is
    /// not a kind.</summary>
    private const int DamageKindCount = 9;
    private static readonly string[] DamageKindNames =
    {
        "direct", "melee", "explosion", "dot", "shrapnel", "collision", "fall", "environment", "reflection"
    };

    /// <summary>The index of one declared `damage_kind` member, from the member name the handler boundary resolved
    /// the compiled index into. A name the declared set does not carry answers -1 and the caller refuses it.</summary>
    internal static int DamageKindIndexOf(string? member)
    {
        for (int index = 0; index < DamageKindCount; index++)
            if (DamageKindNames[index] == member) return index;
        return -1;
    }
    internal sealed record Entry
    {
        internal Entry(EnemyAgent enemy, EntityReference reference, IntPtr enemyPointer, ushort globalId)
        {
            Enemy = enemy; Reference = reference; EnemyPointer = enemyPointer; GlobalId = globalId;
            NativePhaseCandidate = NativePhaseType(enemy);
            Type = ForgeEnemy.Native.Observation.EnemyTypeReader.ReadSpawned(enemy);
            Behavior = new(enemy, globalId);
        }
        /// <summary>The official type id, read once when the life is tracked: it never changes for a life, so the type
        /// query, the spawn fact and the `enemy-type` mount answer from this capture instead of reading a live (or dead)
        /// agent again. Null when the block
        /// did not read at spawn.</summary>
        internal uint? Type { get; }
        internal EnemyAgent Enemy { get; }
        internal EntityReference Reference { get; }
        internal EntityReference? ActionLife;
        internal IntPtr EnemyPointer { get; }
        internal bool WrapperAliasLogged;
        /// <summary>The health the last completed damage window (or Forge heal) left this life at, and the
        /// subscription generation it belongs to. Cleared while a window is open, so a window that never completed
        /// leaves no baseline to mistake for a bypass.</summary>
        internal float? LastWindowHealth;
        internal long LastWindowGeneration = -1;
        internal bool BypassReported;
        /// <summary>The GlobalID the life was tracked under; its reference id is `gtfo.enemy:` plus this number.</summary>
        internal ushort GlobalId { get; }
        internal Behavior Behavior { get; }
        internal LifecycleObservation? DeathObservation;
        internal readonly Dictionary<int, LifecycleObservation> LimbObservations = new();
        /// <summary>The last tag state this module published for the life, which is what turns a per-frame
        /// property sample into one rising and one falling fact per tag. Owned by `EnemyNodeFacts`.</summary>
        internal bool Tagged;
        /// <summary>The mode the game spawned this life with, recorded once at the spawn hook and never re-read:
        /// it is a fact about how the life entered the level, which the game does not publish anywhere else. It
        /// is what the `spawn_origin` port of `forge.query.enemy.type` answers from.</summary>
        internal Agents.AgentMode SpawnMode;
        internal bool SpawnModeKnown;
        /// <summary>Whether this life's immutable prefab type requires phase observation. An unreadable type stays
        /// conservatively included; the mutable AI, behaviour wrapper, pointer and phase are never cached here.</summary>
        internal bool NativePhaseCandidate { get; }
        internal int? ObservedNativePhase;
        internal IntPtr PhaseBehaviour;
        /// <summary>The look's per-life state (glow pulses, scout animation, marker text), started at the spawn
        /// hook from the life's profile and owned by `EnemyModuleLook`.</summary>
        internal EnemyLookLife? Look;
        /// <summary>The zone watermark of `forge.trigger.enemy.zone_changed`: the course node last sampled and the zone
        /// it belonged to, both as native pointers. Owned by `EnemyModule.ActionFacts`.</summary>
        internal IntPtr ZoneCourseNode, ZonePointer;
        /// <summary>The life's wake rules and the detection component the noise window is keyed by, started at the
        /// spawn hook from the life's profile and owned by `EnemyModule.Wake`.</summary>
        internal bool Stationary;
        internal bool WakeLocked;
        internal EnemyWakeLife? Wake;
        internal IntPtr WakeDetection;
        /// <summary>The life's per-ability sections, started at the spawn hook from the life's profile and owned by
        /// `EnemyModule.AbilityFields`.</summary>
        internal EnemyAbilityLife? Abilities;
    }
    internal sealed record DespawnObservation(EnemyModule Owner, EntityReference Target, IntPtr EnemyPointer);
    internal sealed class DamageObservation
    {
        private readonly EnemyModule _owner;
        private bool _consumed;
        internal EntityReference Target { get; }
        internal float HealthBefore { get; }
        internal IntPtr DamagePointer { get; }
        /// <summary>The array position the receiver's own `limbID` parameter named, or `-1` for a hit that named
        /// no part. It is a position into `DamageLimbs`, not an `m_limbID`, and is published as it arrived.</summary>
        internal int LimbIndex { get; }
        /// <summary>The `damage_kind` member the receive entry around this call named, or `NoDamageKind`.</summary>
        internal int Kind { get; }
        /// <summary>The receiver's own position argument, copied by value; non-finite points are omitted at publication.</summary>
        internal Vector3? Position { get; }
        internal DamageObservation(EnemyModule owner, EntityReference target, float healthBefore, IntPtr damagePointer,
            int limbIndex, int kind, Vector3? position)
        {
            _owner = owner; Target = target; HealthBefore = healthBefore; DamagePointer = damagePointer;
            LimbIndex = limbIndex; Kind = kind; Position = position;
        }
        internal bool TryConsume(EnemyModule owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true;
            return true;
        }
    }
    internal readonly Dictionary<ushort, Entry> _entities = new();
    // Authoring diagnostics only: powers of ten keep repeated polling bounded without a per-tick key table.
    internal static bool DiagnosticSample(long count)
    {
        if (count < 1) return false;
        while (count % 10 == 0) count /= 10;
        return count == 1;
    }

    private readonly long[] _healRecipientCounts = new long[6];
    private void TraceHealRecipient(CommandContext context, EntityReference target, Entry? entry, bool healthReceiver)
    {
        if (ForgeEngine.Plugin.ConfiguredMode != ForgeEngine.RuntimeMode.Authoring || !Log.Logs(RuntimeLogLevel.Info)) return;
        int kind = target.WorldEpoch != _kernel.WorldEpoch ? 1
            : !EntityKinds.Is(target, EntityKinds.Enemy)
                ? (EntityKinds.Is(target, EntityKinds.Player) ? 5 : 2) : entry == null ? 3 : healthReceiver ? 0 : 4;
        long count = ++_healRecipientCounts[kind];
        if (!DiagnosticSample(count)) return;
        bool? current = null;
        try { current = _kernel.IsEntityCurrent(target); }
        catch (Exception error) { EnemyLog.Contained(error); }
        Log.Write(RuntimeLogLevel.Info, "enemy.heal-recipient", new RuntimeLogFacts
        {
            EventId = context.EventId,
            Entity = target.Id,
            Binding = HealBinding,
            Values = new[]
            {
                RuntimeLogValue.Of("rootEvent", context.RootEventId), RuntimeLogValue.Quote("step", context.NodeId),
                RuntimeLogValue.Of("requestedWorld", target.WorldEpoch), RuntimeLogValue.Of("requestedLife", target.LifeEpoch),
                RuntimeLogValue.Of("current", current == true), RuntimeLogValue.Of("currentRead", current.HasValue),
                RuntimeLogValue.Of("receiverCapability", "health.heal"),
                RuntimeLogValue.Of("enemyHealthReceiver", healthReceiver), RuntimeLogValue.Of("count", count),
                RuntimeLogValue.Of("resolution", kind == 1 ? "stale-world-epoch" : kind == 2 ? "unsupported-recipient-kind" : kind == 5 ? "player-healing-service"
                    : kind == 3 ? "stale-or-untracked-life" : healthReceiver ? "ready" : "missing-health-receiver")
            }
        });
    }

    internal readonly IForgeEngine _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?>? _observe;
    private readonly EnemyPoseReader? _pose;
    private readonly Func<EnemyAgent, uint?>? _enemyType;
    internal readonly RuntimeModuleHandle _registration;
    /// <summary>Which native functions stand on this build; a withdrawn function's bindings are not registered.</summary>
    internal readonly EnemyHookAvailability _hooks;
    internal EnemyHookAvailability Hooks => _hooks;
    /// <summary>The gate of one of this provider's bindings, or a gate that never has subscribers when the binding was withdrawn.</summary>
    private EnemyGate Gate(string bindingId)
        => new(bindingId, _hooks.WithdrawsBinding(bindingId) ? null : _registration.SubscriptionGate(bindingId));
    /// <summary>This provider's structured records (`EnemyLogCodes`), attached to the registration once it exists.</summary>
    internal EnemyLog Log { get; } = new();
    private long _nextLife, _eventSequence;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private bool _disposed;
    internal bool IsRegistered => !_disposed && _registration.IsRegistered;
    /// <summary>This provider's registration handle, which the package's world-bound work lease renews against.</summary>
    internal RuntimeModuleHandle Registration => _registration;

    // PERF(F4/F6/F7): one kernel gate per binding a hot hook asks about. Each is created once in the constructor,
    // never replaced, and refreshed by the kernel wherever it rebuilds subscriptions, waits or end_on bindings. A
    // gate caches the binding's current consumption state. The hooks read these before building
    // a closure or touching a native member. The inner methods keep their own kernel checks, so a gate can only skip
    // work; it can never publish a fact the old code would not.
    private readonly EnemyGate _damageGate, _healthChangedGate, _killedGate, _staggeredGate;

    /// <summary>Whether the damage window has a consumer: the three facts its two observations publish, the same
    /// set `EnemyDamage` used to ask for by name.</summary>
    internal bool DamageWindowObserved => _damageGate.HasSubscribers || _healthChangedGate.HasSubscribers
        || _killedGate.HasSubscribers;
    /// <summary>Whether a plan consumes the staggered fact, which the damage postfix reports without a window.</summary>
    internal bool StaggerObserved => _staggeredGate.HasSubscribers;

    internal EnemyModule(IForgeEngine kernel, RuntimeLogLevel logLevel,
        Func<bool> canExecute,
        Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?>? observe = null, Func<EnemyAgent, uint?>? enemyType = null,
        EnemyHookAvailability? hooks = null, EnemyPoseReader? pose = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _hooks = hooks ?? EnemyHookAvailability.Complete;
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _observe = observe;
        _pose = pose;
        _enemyType = enemyType;
        var handlers = new Dictionary<string, CommandHandler> { ["gtfo.enemy.heal"] = Heal, [DamageActionHandler] = Damage };
        // The node-list family's own handlers and shapes, from the one declaration file that names them: the
        // three node actions, the six value rows, and the action families whose rows are list rows too.
        foreach (var handler in AllNodeHandlers()) handlers.Add(handler.Key, handler.Value);
        // The action families the node-list slice left unwired — enemy control, combat, foam, behaviour and the
        // profile's `phase_set`: their bindings, handlers and shapes are declared in their own
        // contracts, and `EnemyActionFamilies` is the one place they are composed into this registration.
        foreach (var handler in ActionFamilyHandlers()) handlers.Add(handler.Key, handler.Value);
        _registration = kernel.RegisterModule((ModuleDefinition.Create(handlers, NodeEvaluatorsWithSelector(), IsCurrent) with
        {
            // The module reports the end of every enemy life itself (`ReportEnded`, from each place a life leaves the
            // table: despawn, destruction, replacement by a new life of the same id, the world's end), so the kernel
            // stops asking `IsCurrent` once per tick for every held enemy reference.
            EntityEndReports = new[] { EntityKinds.Enemy },
            // The selector is evaluated on demand, so its evaluator and the entity set it reads are registered
            // here, on the one provider that already owns the `gtfo.enemy` kind: the kernel lets only a kind's own
            // owner say which entities of it exist.
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>> { [EnemySelector.EntityKind] = CurrentCandidates },
            EntityObservers = observe == null ? null : new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
            { [EntityKinds.Enemy] = ObserveEntity },
            // Zone judgement reads only where an enemy stands and whether it lives: the pose read builds no snapshot.
            EntityPoseObservers = pose == null ? null : new Dictionary<string, EntityPoseObserver> { [EntityKinds.Enemy] = ObservePose },
            // `AgentTarget.m_agent` is a cross-provider native instance: each kind is only ever asked of the
            // provider that registered it, and an instance no registered kind claims stays unresolved.
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
            { [EntityKinds.Enemy] = ResolveInstance },
            // The reverse door: the one live agent a `gtfo.enemy` reference names, for another module's handler
            // that reads a game member of it (the Map provider's dimension read).
            EntityInstances = new Dictionary<string, Func<EntityReference, object?>>
            { [EntityKinds.Enemy] = InstanceOf },
            // Which zone an enemy stands in is this provider's own reading of its own instances: the enemy's course
            // node names its zone, and the coordinates are the same text the Map provider names a zone with, so a
            // plan that filters a candidate set by zone compares one place and not two.
            EntityZones = new Dictionary<string, Func<EntityReference, EntityReference?>>
            { [EntityKinds.Enemy] = ZoneOfEnemy },
            // Which area of that zone the enemy stands in is the other half of the same placement, read from the
            // same course node: the zone-members row's `area` port reads it back through this responder.
            EntityZoneAreas = new Dictionary<string, Func<EntityReference, int?>>
            { [EntityKinds.Enemy] = AreaOfEnemy },
            // The one row of this provider that is written where it is seen: `forge.action.enemy.mark` is a
            // `presentation` step, so the host decides the timing and each recipient builds the marker on its own
            // machine. The audience is the realm's own player list, answered by the one function declared with
            // the row that needs it.
            PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>(StringComparer.Ordinal)
            { [ProviderId] = PresentationAudience },
            // The ability resource names the game's own ability enum and type-local component index. The same
            // table answers both directions — the id an ability action resolves and the id the ability-used fact
            // publishes — so a plan that triggers an ability and a plan that watches for one name one thing.
            ResourceProviders = new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
            { [EnemyAbilityResources.Kind] = AbilityKinds, ["enemy"] = EnemyKinds },
            // The one row whose clock is the step's own `effect` block: `forge.action.combat.effect_volume`
            // registers the callback the kernel ends that effect through, which is what allows the card to carry a
            // duration at all. It is the module's own method, so the callback runs against the ledger of the
            // process the effect was applied in and every end can carry it out on its own.
            EffectRestores = new Dictionary<string, EffectRestoreHandler>(StringComparer.Ordinal)
            {
                [VolumeHandler] = RestoreVolume,
                // The damage rule is the other row whose clock is the step's own `effect` block: every rule
                // one dispatch installed is dropped together when that effect ends.
                [CombatDamageRuleContract.HandlerName] = RestoreDamageRule,
                // The foam family's clock is the same one: its handle is the kernel's own for the step, so the
                // foam a dispatch applied is taken back when that effect ends — by its duration, by a
                // cancellation, by a released plan or by the world.
                [GlueContract.FoamingHandler] = RestoreFoam,
                [EnemyNativeStateContract.Handler(Blocks.ActionEnemyLimbSet.Id)] = RestoreNativeLimb,
                [EnemyProfileContract.PhaseSetHandler] = RestorePhaseSet,
                [EnemyControlContract.MoveToHandler] = RestoreMoveTo,
                [EnemyStationaryContract.Handler] = RestoreStationary,
                [EnemyWakeLockContract.Handler] = RestoreWakeLock
            },
            // A session with no way to read an enemy's block does not register the kind at all: a plan naming
            // `enemy-type` is then refused when it loads instead of being accepted and never dispatched. The
            // kind is matched against the event's own subject, the enemy instance the block was read from.
            AttachmentMatchers = enemyType == null ? null : new Dictionary<string, AttachmentMatcherRegistration>
            { [EnemyTypeAttachment] = AttachmentMatcherRegistration.BySubject(MatchesEnemyType) }
        }).Without(_hooks.WithdrawnBindings), logLevel);
        // I-MANIFEST: the Forge block fields this provider's profile appliers write. The registration window is
        // still open here — `StartRuntime` is what freezes it — and the declaration is the package's own, so the
        // exported manifest and the running registry name the same fields.
        ModuleDefinition.DeclareDefinitionFields(_registration);
        // The enemy provider alone resolves its exact native life: other plugins resolve a damage instigator through
        // this service, while source/owner/recipient remain independent identities.
        _registration.PublishService<EnemyAgentResolver>(reference => Resolve(reference)?.Enemy);
        _registration.PublishService<EnemyNoiseTraceResolver>(EnemyNoiseInspectionService.ResolveNoiseTrace);
        Log.Attach(_registration);
        try
        {
            InitializeActionState();
            // PERF(F4/F6/F7, P5): every gate a hook or observer asks is resolved here, while every binding of this
            // provider is already a registry fact, and never looked up by name again. `SubscriptionGate` refuses a
            // binding this provider does not own; the catch below then disposes the registration, the same rollback
            // a failing lifecycle subscription gets.
            _damageGate = Gate(DamageBinding);
            _healthChangedGate = Gate(HealthChangedBinding);
            _killedGate = Gate(ForgeEnemy.AttackInstanceContract.BindingId);
            _staggeredGate = Gate(ForgeEnemy.EnemyStaggerContract.BindingId);
            CreateBehaviorGates();
            CreateNativeStateGates();
            CreateNodeGates();
            CreateActionGates();
            CreateAbilityGates();
            _waveFacts = new EnemyWaveFacts(_kernel, _registration, () => CanObserveFacts, Log, ReferenceOf, _hooks.WithdrawnBindings);
            RegisterMigration();
            _lifecycle = _registration.ObserveLifecycle(value =>
            {
                if (value.Kind == RuntimeLifecycleKind.WorldChanged && value.IsMigration
                    && value.Current.StartupState == RuntimeStartupState.Ready)
                {
                    try { FollowMigration(); }
                    catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.CleanupFailed, error); }
                }
                else if (value.Kind == RuntimeLifecycleKind.WorldChanged && value.Current.IsHost == false
                    && value.Current.StartupState == RuntimeStartupState.Ready)
                {
                    ClearWorldContained(releaseInstanceAppearance: false);
                    try { ReleaseDestroyedInstanceAppearance(); }
                    catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.CleanupFailed, error); }
                }
                else if (value.Kind == RuntimeLifecycleKind.WorldChanged
                    || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                    ClearWorldContained(releaseInstanceAppearance: true);
                else if (value.Kind == RuntimeLifecycleKind.TickAdvanced)
                {
                    // P4: with no tracked life, no live marker and no volume this observer has nothing to step.
                    if (_entities.Count == 0 && _batches.Count == 0 && _volumes.Count == 0) return;
                    try { AdvanceLooks(value.Current); }
                    catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.TickPumpFailed, error); }
                    // PERF(F4): the two global sweeps run once per kernel tick here, not once per enemy behaviour
                    // update. This is the scheduler the looks already use, not a second one.
                    PumpTick();
                }
            });
            // The attachment crack stages ride the kernel's native-state snapshot; every machine registers the key.
            CreateModelLimbStages();
        }
        catch { DisposeActionState(); _registration.Dispose(); throw; }
        // The value rows read this module's own tracked lives, so the resolver is attached once the
        // registration that declares them is live. It goes with the module, and with the world the module
        // clears.
        AttachNodeFamily(kernel);
        // The foam family's ledger subscribes to the kernel's own lifecycle, and a subscription is a registration
        // change the kernel refuses while a command is being dispatched: the family is built here, once, while the
        // registration is still open, rather than on the first foam request.
        _ = Glue;
    }

    /// <summary>The selector's evaluator and the node family's value rows in one table: the selector is the
    /// module's own kind-level query, and the six value rows are the node list's `values` section.</summary>
    private Dictionary<string, EvaluatorHandler> NodeEvaluatorsWithSelector()
    {
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        { [EnemySelector.HandlerName] = EnemySelector.Evaluate };
        foreach (var evaluator in NodeEvaluators()) evaluators.Add(evaluator.Key, evaluator.Value);
        evaluators.Add(EnemyStationaryContract.ReadHandler, ReadStationary);
        evaluators.Add(EnemyWakeLockContract.ReadHandler, ReadWakeLocked);
        return evaluators;
    }

    internal void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy APIs require the owning simulation thread.");
    }
    internal bool CanExecute
    {
        get
        {
            CheckThread();
            return IsRegistered && _kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                && _canExecute() && IsMasterThisTick() && !ForgeCore.Puppets.Playback;
        }
    }

    private long _masterTick = -1;
    private bool _isMaster;
    /// <summary>PERF(E1): `Session.IsHost` is an Il2Cpp getter and the frame pump asks three gates per enemy, so the
    /// answer is read once per kernel tick. A master change inside one tick is seen on the next.</summary>
    private bool IsMasterThisTick()
    {
        var tick = _kernel.CurrentTick;
        if (tick < 0) return Session.IsHost;
        if (tick != _masterTick) { _isMaster = Session.IsHost; _masterTick = tick; }
        return _isMaster;
    }

    /// <summary>The gate the provider's one `presentation` handler passes on the machine that presents it: the
    /// registration, the startup state and the gameplay flag, without the master flag. The host decides the step
    /// and dispatches it to the players it addressed; the machine that runs the handler is a recipient, so being
    /// the master is not a condition of presenting.</summary>
    internal bool CanPresent
    {
        get
        {
            CheckThread();
            return IsRegistered && _kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
                && _canExecute();
        }
    }
    internal void ClearWorld() => ClearWorld(releaseInstanceAppearance: true);

    /// <summary>The cleanup of a disabled module. The enemies stay alive, and the game's glow animator was built from
    /// the private material copies this module put on them, so destroying the copies would leave a live animator
    /// holding dead objects and fail inside the game's own wind-up. The copies are released by the instance's
    /// recycle or by shutdown, like any other.</summary>
    internal void ClearWorldAfterFault() => ClearWorld(releaseInstanceAppearance: false);

    private void ClearWorld(bool releaseInstanceAppearance)
    {
        CheckThread();
        _waveFacts?.EndWorldDiagnostics();
        if (ForgeEngine.Plugin.ConfiguredMode == ForgeEngine.RuntimeMode.Authoring && Log.Logs(RuntimeLogLevel.Info))
            Log.Write(RuntimeLogLevel.Info, "enemy.heal-recipient", new RuntimeLogFacts
            {
                Values = new[] { RuntimeLogValue.Of("phase", "world-end"),
                    RuntimeLogValue.Of("tracked", _healRecipientCounts[0]), RuntimeLogValue.Of("staleWorld", _healRecipientCounts[1]),
                    RuntimeLogValue.Of("unsupportedKind", _healRecipientCounts[2]), RuntimeLogValue.Of("staleLife", _healRecipientCounts[3]),
                    RuntimeLogValue.Of("missingReceiver", _healRecipientCounts[4]) }
            });
        Array.Clear(_healRecipientCounts, 0, _healRecipientCounts.Length);
        // Every step is its own cleanup: one native restore that throws must not leave the rest of the world standing, so
        // the failures are collected and raised together at the end.
        List<Exception>? failures = null;
        bool Step(Action action)
        {
            try { action(); return true; }
            catch (Exception error) { (failures ??= new()).Add(error); return false; }
        }
        Step(() =>
        {
            foreach (var entry in _entities.Values)
            {
                if (Log.Logs(RuntimeLogLevel.Info)) TraceIdentity("world-cleared", entry);
                ReportEnded(entry.Reference);
            }
        });
        _holders.Clear();
        _identityRefusals.Clear();
        Step(ClearEnemyAttributes);
        // A failed native restore keeps its receiver in the entity table, so a later attempt (Dispose retries) is real work.
        bool actionsCleared = Step(ClearActionState);
        if (actionsCleared) Step(ClearActionLives);
        Step(ClearVolumeState);
        Step(_sounds.ClearWorld); Step(_actions.ClearWorld); Step(() => ClearModels()); Step(ClearNativePhases);
        if (actionsCleared) _entities.Clear();
        _candidateRoster = null;
        Step(ClearStationary); Step(ClearWakeLock);
        _wakeByDetection.Clear(); _pulseRateWorld = -1;
        Step(() => ClearLooks(releaseInstanceAppearance)); Step(ClearBehaviors); Step(ClearDamageRules);
        Step(ClearChildSpawning);
        Step(ClearProjectileOwners);
        if (failures != null) throw new AggregateException("Enemy world cleanup failed.", failures);
    }

    /// <summary>The lifecycle observer's world clear: the kernel permanently drops an observer that throws, and this one
    /// also drives the per-tick sweeps, so a failed cleanup is reported and the observer stays.</summary>
    private void ClearWorldContained(bool releaseInstanceAppearance)
    {
        try { ClearWorld(releaseInstanceAppearance); }
        catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.CleanupFailed, error); }
    }
    partial void ApplyModels(EnemyAgent enemy);
    partial void RemoveModels(EnemyAgent enemy);
    partial void ClearModels();
    partial void DisposeModels();
    partial void CreateModelLimbStages();

    /// <summary>PERF(F4): the per-tick half of the work that is a clock or a follow, called only from the lifecycle
    /// observer: marker lifetimes run out on a kernel-tick deadline, a followed volume moves with its anchor, and the
    /// action ledger retries a pending restore and watches a player source's life (which has no event on this side).
    /// "Is this enemy still there" is not asked here: despawn, destruction, replacement and death clean the
    /// ledgers when they happen (`CompleteDespawn`, `ReleaseDeadEnemy`). The kernel permanently drops a lifecycle
    /// observer that throws, and this observer is also the one that clears the world, so a failure is reported
    /// and swallowed here. Each sweep returns at once when its ledger is empty. The native phase sweep is not part of
    /// it: it publishes a fact, which a read-only lifecycle observer may not do, so the session's frame driver runs it.</summary>
    private void PumpTick()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.EnemyTickPump);
        if (_disposed) return;
        try
        {
            ExpireMarks();
            PumpVolumes();
            PumpActionState();
        }
        catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.TickPumpFailed, error); }
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed)
        {
            // A previous shutdown may have retained native action state. Keep the exact entity table until foam
            // cleanup confirms its receiver; registration and unrelated teardown are never repeated.
            var retryFailures = new List<Exception>();
            try { DisposeActionFamilies(); } catch (Exception error) { retryFailures.Add(error); }
            if (_glue == null)
                try { ClearWorld(); } catch (Exception error) { retryFailures.Add(error); }
            try { DisposeEnemyAttributes(); } catch (Exception error) { retryFailures.Add(error); }
            if (retryFailures.Count != 0) throw new AggregateException("Enemy shutdown cleanup is incomplete.", retryFailures);
            return;
        }
        // Admission first: an in-dispatch or dependency refusal must leave the entire live module untouched.
        _registration.Dispose();
        _disposed = true;
        var failures = new List<Exception>();
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); }
        }
        Cleanup(_lifecycle.Dispose);
        Cleanup(DetachNodeFamily);
        Cleanup(DisposeActionFamilies);
        // A failed exact-receiver cleanup retains the entity table for a later Dispose attempt. Clearing it here
        // would turn a recoverable native failure into an unresolvable obligation.
        if (_glue == null) Cleanup(ClearWorld);
        Cleanup(DisposeActionState);
        Cleanup(DisposeEnemyAttributes);
        Cleanup(DisposeModels);
        Cleanup(() => ContentEntryGeneration.UnregisterDomain(this));
        Cleanup(() => GameRuntimeBridge.Generations.Changed -= ContentStood);
        if (failures.Count != 0) throw new AggregateException("Enemy module shutdown cleanup is incomplete.", failures);
    }

    internal EntityReference TrackSpawn(EnemyAgent enemy)
    {
        CheckThread();
        if (!IsRegistered || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            throw new InvalidOperationException("Enemy module is not accepting native instances.");
        if (enemy == null) throw new ArgumentNullException(nameof(enemy));
        if (enemy.Pointer == IntPtr.Zero) throw new ArgumentException("Enemy must have a native instance.", nameof(enemy));
        // A new wrapper alone is not a new native life; despawn/world boundaries retire the old life.
        if (_entities.TryGetValue(enemy.GlobalID, out var existing)
            && existing.Reference.WorldEpoch == _kernel.WorldEpoch && existing.EnemyPointer == enemy.Pointer
            && Resolve(existing.Reference) != null) return existing.Reference;
        var reference = new EntityReference(EntityKinds.Enemy + ":" + enemy.GlobalID.ToString(CultureInfo.InvariantCulture),
            _kernel.WorldEpoch, checked(++_nextLife));
        // A life this call replaces without its despawn having been seen takes its wake key with it: the detection
        // pointer is the game's to hand out again.
        var retired = existing == null ? null : RetireLife(existing, "replaced");
        DropStaleWake(enemy);
        var entry = new Entry(enemy, reference, enemy.Pointer, enemy.GlobalID);
        _entities[enemy.GlobalID] = entry;
        if (existing != null)
        {
            _holders.Remove(existing);
            ReportEnded(existing.Reference);
            // The new life is already tracked: a failed cleanup of the old one is reported, it does not undo this spawn.
            try { FinishRetire(existing, retired); }
            catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.CleanupFailed, error); }
        }
        SetHolder(entry);
        TraceIdentity("registered", entry);
        _candidateRoster = null;
        // The profile goes on once per native life, right here: the spawn path runs after the receiver finished
        // setting itself up (limb array, detection and appearance components all built), and on every peer from
        // the same documents, so there is nothing to replicate and no second application point to keep in step.
        ForgetChildSpawning(enemy);
        ApplyProfiles(enemy);
        ApplyModels(enemy);
        TrackNativePhase(entry);
        TrackActionLife(entry);
        return reference;
    }

    /// <summary>Records how one tracked life entered the level, from the spawn packet the game handed the hook.
    /// It is the one place the mode is written, so the `spawn_origin` read answers the very fact the spawn hook
    /// saw. A reference this provider no longer tracks writes nothing.</summary>
    internal void RecordSpawnMode(EntityReference reference, Agents.AgentMode mode)
    {
        CheckThread();
        if (Resolve(reference) is not { } entry) return;
        entry.SpawnMode = mode;
        entry.SpawnModeKnown = true;
    }

    // Synchronous native entry. Deferred work must retain CaptureDespawn's token, not an EnemyAgent pointer.
    internal void TrackDespawn(EnemyAgent enemy) => CompleteDespawn(CaptureDespawn(enemy));
    internal DespawnObservation? CaptureDespawn(EnemyAgent enemy)
    {
        CheckThread();
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer || !IsLive(entry)) return null;
        return new DespawnObservation(this, entry.Reference, entry.EnemyPointer);
    }

    internal void CompleteDespawn(DespawnObservation? observation)
    {
        CheckThread();
        if (observation == null || !ReferenceEquals(observation.Owner, this)) return;
        var entry = Resolve(observation.Target);
        if (entry != null && entry.EnemyPointer == observation.EnemyPointer)
        {
            var retired = RetireLife(entry, "despawned");
            _entities.Remove(entry.Enemy.GlobalID);
            _holders.Remove(entry);
            ReportEnded(entry.Reference);
            _candidateRoster = null;
            FinishRetire(entry, retired);
        }
    }

    /// <summary>Ends one tracked life's ledgers; the one path a despawn and a replacement by a new life of the same id
    /// both take. Every step is its own cleanup, so a native clear that throws leaves the table removal, the end report
    /// and the rest of the ledgers to run; the failures come back for <see cref="FinishRetire"/> to raise.
    /// The ledger row goes first: reporting the ability a life's end cut short needs the entry the caller is about to
    /// drop. A standing damage rule belongs to the life it was written for, and the receiver pointer it is filed under
    /// is the game's to hand out again, so it goes with the entry, before the entry is dropped.</summary>
    private List<Exception>? RetireLife(Entry entry, string how)
    {
        List<Exception>? failures = null;
        void Step(Action action)
        {
            try { action(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        Step(() => ForgetBehavior(entry.Reference));
        Step(() => ForgetChildSpawning(entry.Enemy));
        Step(() => ForgetEnemyAttributes(entry.Reference));
        Step(() => ForgetActionLife(entry));
        Step(() => ForgetActionState(entry.Enemy));
        Step(() => ForgetMarksOf(entry.Reference));
        Step(() => ForgetDamageRule(entry));
        Step(() => ForgetWake(entry));
        Step(() => RemoveModels(entry.Enemy));
        Step(() => _actions.Release(entry.Enemy));
        Step(() => ForgetNativePhase(entry));
        Step(() => _glue?.ForgetLife(entry.Reference));
        TraceIdentity(how, entry);
        return failures;
    }

    /// <summary>The end of a retired life once the table no longer holds it: subscribers hear it, and it owns no
    /// variables either, so a plan that wrote a per-enemy value does not see it survive into whatever takes the
    /// instance's place. Raises what <see cref="RetireLife"/> collected.</summary>
    private void FinishRetire(Entry entry, List<Exception>? failures)
    {
        try { ReleaseEnemyScope(entry.Reference); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures != null) throw new AggregateException("Enemy life cleanup failed.", failures);
    }

    /// <summary>Tells the kernel one enemy life has left the table. Every place a life leaves it calls this once
    /// (despawn and destruction, replacement by a new life of the same id, the world's end), after which
    /// <see cref="IsCurrent"/> answers false for the reference. A life that did not die yet (a corpse stays in the table
    /// until it despawns) is not reported: it is still current. A module already unregistered reports nothing.</summary>
    private void ReportEnded(EntityReference reference)
    {
        if (_registration.IsRegistered) _registration.EntityEnded(reference);
    }

    /// <summary>An enemy died: what lives only while the enemy is alive goes now, instead of being found by a scan of
    /// every ledger every tick: its modifier contributions and its action states (the despawn and the world's end
    /// clean the same ledgers for the lives that never reach this).</summary>
    internal void ReleaseDeadEnemy(EnemyAgent? enemy)
    {
        CheckThread();
        // Both ledgers empty is the common death: nothing to release, and no native member is read to find that out.
        if (_enemyAttributes.Count == 0 && _actionStates.Count == 0) return;
        if (enemy == null || enemy.Alive || !_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer) return;
        try
        {
            ForgetEnemyAttributes(entry.Reference);
            RemoveReceiverActionStates(enemy);
        }
        catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.TickPumpFailed, error); }
    }

    internal Entry? Resolve(EntityReference reference)
    {
        CheckThread();
        if (!IsRegistered || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return null;
        return ResolveTracked(reference);
    }

    /// <summary>Looks up an already tracked exact life without reopening command admission. Shutdown cleanup uses
    /// this after registration has closed and before the entity table is released.</summary>
    private Entry? ResolveTracked(EntityReference reference)
    {
        CheckThread();
        const string prefix = EntityKinds.Enemy + ":";
        if (reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !ushort.TryParse(reference.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !_entities.TryGetValue(id, out var entry) || entry.Reference != reference || entry.Enemy == null
            || entry.Enemy.GlobalID != id || entry.Enemy.Pointer != entry.EnemyPointer) return null;
        return entry;
    }
    private bool IsCurrent(EntityReference reference) => Resolve(reference) != null;

    /// <summary>`Resolve(entry.Reference) == entry` for an entry just read from the table, without the id text parse:
    /// the reference id is `gtfo.enemy:` plus <see cref="Entry.GlobalId"/>, so the table key is the same lookup.</summary>
    internal bool IsLive(Entry entry)
    {
        CheckThread();
        return IsRegistered && _kernel.StartupState is not (RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            && entry.Reference.WorldEpoch == _kernel.WorldEpoch
            && _entities.TryGetValue(entry.GlobalId, out var current) && ReferenceEquals(current, entry)
            && entry.Enemy != null && entry.Enemy.GlobalID == entry.GlobalId && entry.Enemy.Pointer == entry.EnemyPointer;
    }

    /// <summary>The one `enemy-type` mount matcher: a plan mounted on an official enemy type runs for an event
    /// whose subject is this provider's own enemy instance of exactly that type. The reference is the block's
    /// `persistentID` as plain decimal text, the single spelling the website exports (ruling 61), and the type
    /// is read from the instance this module registered — an instance it cannot read back, a subject of another
    /// kind, and a life retired while the type was being read all answer false.</summary>
    private bool MatchesEnemyType(string? category, string reference, EntityReference subject)
    {
        // The kind names a type, never an address: a category is not this mount's to interpret.
        if (category != null || _enemyType == null || !TryEnemyTypeId(reference, out var declared)) return false;
        var entry = Resolve(subject);
        if (entry == null || (entry.Type ?? _enemyType(entry.Enemy)) != declared) return false;
        // The block getter is native: a callback may retire the life or replace the instance while it is read.
        return Resolve(subject) == entry;
    }

    /// <summary>Strict reference grammar: decimal digits that parse to a `uint` and are that value's own
    /// canonical text, so `007`, `+7`, `-7`, whitespace and anything above `uint.MaxValue` are not spellings of
    /// a type. `NumberStyles.None` already refuses a sign and surrounding space; the round trip refuses a
    /// leading zero, which would be a second spelling of one id.</summary>
    private static bool TryEnemyTypeId(string reference, out uint id)
        => uint.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && reference == id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The kernel's instance resolver for `gtfo.enemy`: a native agent is only ever answered for the
    /// life the module registered. Interop may return another managed wrapper for that same native life;
    /// GlobalID, native pointer and the registered world/life must still all match.</summary>
    private EntityReference? ResolveInstance(object instance)
    {
        CheckThread();
        if (instance is not EnemyAgent enemy) return null;
        _entities.TryGetValue(enemy.GlobalID, out var entry);
        if (entry == null || entry.EnemyPointer != enemy.Pointer || !IsLive(entry))
        {
            LogIdentityResolution(enemy, entry, entry == null ? "not-registered"
                : entry.EnemyPointer != enemy.Pointer ? "native-pointer-mismatch"
                : !IsRegistered ? "module-not-registered"
                : _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped ? "runtime-not-running"
                : entry.Reference.WorldEpoch != _kernel.WorldEpoch ? "tracked-world-mismatch"
                : entry.Enemy == null ? "tracked-agent-unavailable"
                : entry.Enemy.GlobalID != entry.GlobalId ? "tracked-native-id-mismatch" : "tracked-pointer-mismatch");
            return null;
        }
        LogIdentityResolution(enemy, entry, "resolved");
        if (!entry.WrapperAliasLogged && !ReferenceEquals(entry.Enemy, enemy) && Log.Logs(RuntimeLogLevel.Trace))
        {
            TraceIdentity("wrapper-alias", entry, sameWrapper: false);
            entry.WrapperAliasLogged = true;
        }
        return entry.Reference;
    }

    /// <summary>Only lifetime boundaries and the first wrapper alias are traced, never every identity query.
    /// Cached registration fields remain readable even after native teardown or pool reuse.</summary>
    private void TraceIdentity(string phase, Entry entry, bool sameWrapper = true)
    {
        var level = phase == "wrapper-alias" ? RuntimeLogLevel.Trace : RuntimeLogLevel.Info;
        if (!Log.Logs(level)) return;
        Log.Write(level, phase == "wrapper-alias" ? "enemy.identity" : "enemy.identity-lifecycle", new RuntimeLogFacts
        {
            Entity = entry.Reference.Id,
            Values = new[]
            {
                RuntimeLogValue.Quote("phase", phase), RuntimeLogValue.Of("nativeId", (long)entry.GlobalId),
                RuntimeLogValue.Quote("nativePointer", entry.EnemyPointer.ToString("x")),
                RuntimeLogValue.Of("trackedWorld", entry.Reference.WorldEpoch),
                RuntimeLogValue.Of("life", entry.Reference.LifeEpoch), RuntimeLogValue.Of("sameWrapper", sameWrapper),
                RuntimeLogValue.Of("currentWorld", _kernel.WorldEpoch),
                RuntimeLogValue.Quote("startup", _kernel.StartupState.ToString()), RuntimeLogValue.Of("registered", IsRegistered),
                RuntimeLogValue.Of("tracked", (long)_entities.Count)
            }
        });
    }

    /// <summary>The live agent of the life a reference names, or null where that life is gone or not this
    /// module's.</summary>
    private object? InstanceOf(EntityReference reference)
        => CanExecute ? Resolve(reference)?.Enemy : null;

    private bool ObservePose(EntityReference reference, out EntityPose pose)
    {
        pose = default;
        if (_pose == null || _kernel.StartupState != RuntimeStartupState.Ready || !CanExecute) return false;
        var entry = Resolve(reference);
        if (entry == null || !_pose(entry.Enemy, reference, out pose)) return false;
        // As for the snapshot: a read that replaced the instance or invalidated the world is not this life.
        return CanExecute && IsLive(entry);
    }

    private RuntimeEntitySnapshot? ObserveEntity(EntityReference reference)
    {
        if (_observe == null || _kernel.StartupState != RuntimeStartupState.Ready || !CanExecute) return null;
        var entry = Resolve(reference);
        if (entry == null) return null;
        var snapshot = _observe(entry.Enemy, reference);
        // A callback may replace the instance or invalidate the world. Never return its old life.
        return snapshot?.Ref == reference && CanExecute && IsLive(entry) ? snapshot : null;
    }

    /// <summary>Opens the health window of one received hit. <paramref name="limbId"/> is the receiver's own
    /// `limbID` argument, `-1` when the hit named no part; <paramref name="position"/> is its `position` argument.
    /// The kind is the one the receive entry around this call named (`EnemyActionHooks`), read now.</summary>
    internal DamageObservation? BeforeDamage(Dam_EnemyDamageBase damage, int limbId = -1, Vector3? position = null)
    {
        CheckThread();
        if (!(_damageGate.HasSubscribers || _healthChangedGate.HasSubscribers) || !CanExecute
            || damage == null || damage.Owner == null) return null;
        if (!_entities.TryGetValue(damage.Owner.GlobalID, out var entry)
            || !IsLive(entry) || entry.EnemyPointer != damage.Owner.Pointer
            || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer) return null;
        if (!float.IsFinite(damage.Health)) return null;
        NoteHealthContinuity(entry, damage.Health);
        return new DamageObservation(this, entry.Reference, damage.Health, damage.Pointer, limbId, EffectiveDamageKind(damage), position);
    }

    /// <summary>How many times a window opened on an enemy holding a health the previous completed window did not
    /// leave it at. Counted and logged, never published: the change was not seen, so no fact is made up for it.</summary>
    internal long HealthBypassesObserved { get; private set; }

    private void NoteHealthContinuity(Entry entry, float current)
    {
        if (entry.LastWindowHealth is { } known && entry.LastWindowGeneration == _kernel.SubscriptionGeneration
            && Math.Abs(known - current) > 1e-3f)
        {
            HealthBypassesObserved++;
            if (!entry.BypassReported)
            {
                entry.BypassReported = true;
                Log.Write(RuntimeLogLevel.Info, EnemyLogCodes.HealthBypassObserved, new RuntimeLogFacts
                {
                    Entity = entry.Reference.Id,
                    Values = new[] { RuntimeLogValue.Of("expected", (double)known), RuntimeLogValue.Of("actual", (double)current) },
                    Detail = "Health moved between two observed damage windows through no observed native entry; no health fact is published for it."
                });
            }
        }
        entry.LastWindowHealth = null;
    }

    internal void AfterDamage(Dam_EnemyDamageBase damage, DamageObservation? before)
    {
        CheckThread();
        // Consume on first delivery, including a rejected/zero-loss delivery. There is no later retry window.
        if (before == null || !before.TryConsume(this)) return;
        if (!CanExecute || damage == null || damage.Pointer != before.DamagePointer) return;
        var entry = Resolve(before.Target);
        if (entry == null || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer
            || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer || !float.IsFinite(damage.Health)) return;
        double healthAfter = Math.Max(0, damage.Health);
        entry.LastWindowHealth = (float)healthAfter; entry.LastWindowGeneration = _kernel.SubscriptionGeneration;
        double actualDamage = Math.Max(0, Math.Max(0, before.HealthBefore) - healthAfter);
        // Only a real loss inside the native damage call is a fact; a rise in this window is not inferred as healing.
        if (actualDamage == 0) return;
        long sequence = checked(++_eventSequence), tick = Math.Max(0, _kernel.CurrentTick);
        string scope = "gtfo.world:" + _kernel.WorldEpoch;
        // PERF(F6): each fact is built only while its own gate is open. The window opens when either binding has a
        // consumer, so the other fact's id, payload and kernel call were wasted. For an unconsumed binding the
        // kernel answers `ignored/no-consumer` and does nothing else, which is exactly what skipping it leaves.
        // The sequence is still taken once per hit, so event ids do not change.
        if (_damageGate.HasSubscribers)
        {
            // The hook observes health before/after, the part the receiver's own `limbID` argument named and the
            // point its `position` argument named. The attacker is the inflictor the receiver registered inside the
            // same call (`ReadInflictor`, the reading `killed` and `staggered` use), null when no registered kind
            // claims it; the kind is the one the receive entry around the call named, null when none did (a Forge
            // submission, a fire tick). A hit that named no part carries no limb. The equipment is the one
            // port of this fact that is not read from the receiver at all: which gear the damage came from
            // is the weapon provider's own correlation, handed over for the shooter it names, and a hit
            // with no shot behind it — an explosion, a melee swing, a deployable's own fire — carries none.
            // Source and equipment belong to the same stable double-read. No native call or
            // publication occurs between the two uses; the health fact below reads afresh.
            var source = ReadInflictor(entry);
            var equipment = CombatShotAttribution.EquipmentOf(source);
            var fact = _damageGate.Begin(_registration);
            fact.Values.Entity(Blocks.TriggerCombatDamageApplied.OutSource, source);
            fact.Values.Entity(Blocks.TriggerCombatDamageApplied.OutEquipment, equipment);
            fact.Values.Entity(Blocks.TriggerCombatDamageApplied.OutTarget, before.Target);
            fact.Values.Number(Blocks.TriggerCombatDamageApplied.OutAmount, actualDamage);
            if (before.Kind >= 0) fact.Values.Enum(Blocks.TriggerCombatDamageApplied.OutDamageKind, before.Kind);
            else fact.Values.Null(Blocks.TriggerCombatDamageApplied.OutDamageKind);
            if (before.LimbIndex >= 0) fact.Values.Integer(Blocks.TriggerCombatDamageApplied.OutLimb, before.LimbIndex);
            else fact.Values.Null(Blocks.TriggerCombatDamageApplied.OutLimb);
            // The native argument is a value in the window; a hit that named no finite point carries none.
            if (before.Position is { } point && float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z))
                fact.Values.Vector(Blocks.TriggerCombatDamageApplied.OutPosition, point.x, point.y, point.z);
            // A refusal is the kernel's to record (`event.rejected` under the binding).
            fact.Raise("gtfo.enemy.damage:" + _kernel.WorldEpoch + ":" + sequence, _kernel.WorldEpoch, tick, scope);
        }
        if (_healthChangedGate.HasSubscribers)
        {
            // The same observed loss as a health change: value is the clamped health read back after the call, delta
            // the signed loss, max the receiver's own `HealthMax` and ratio their quotient. A maximum that does not
            // read as a positive finite number cannot give a ratio, and both ports are required, so the fact is not
            // published rather than published with a guessed maximum.
            float healthMax = damage.HealthMax;
            if (float.IsFinite(healthMax) && healthMax > 0)
            {
                var inflictor = ReadInflictor(entry);
                var fact = _healthChangedGate.Begin(_registration);
                fact.Values.Entity(Blocks.TriggerCombatHealthChanged.OutTarget, before.Target);
                fact.Values.Number(Blocks.TriggerCombatHealthChanged.OutValue, healthAfter);
                fact.Values.Number(Blocks.TriggerCombatHealthChanged.OutDelta, -actualDamage);
                fact.Values.Number(Blocks.TriggerCombatHealthChanged.OutMax, healthMax);
                fact.Values.Number(Blocks.TriggerCombatHealthChanged.OutRatio, healthAfter / healthMax);
                fact.Values.Entity(Blocks.TriggerCombatHealthChanged.OutSource, inflictor);
                fact.Raise("gtfo.enemy.health:" + _kernel.WorldEpoch + ":" + sequence, _kernel.WorldEpoch, tick, scope);
            }
        }
    }

    /// <summary>Multi-target damage. One row is written per recipient in the plan's own order, and a hit whose
    /// effect cannot be read back is reported as an unknown commit rather than as success: the frozen native
    /// evidence shows that a rejected hit and a hit the receiver rules reduce to nothing are indistinguishable
    /// from the submitting side, so neither may be claimed. The host is the only authority that may submit: the
    /// native local-application gate is not an authority gate for this receiver type, so a client that reached the
    /// entry point would apply the hit locally, and `Session.IsHost` is what keeps that from happening.</summary>
    private CommandResult Damage(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        // The instigator may be a player or enemy. The source is an independent optional role; neither an absent source
        // nor an enemy instigator is replaced by an owner or recipient. The contract lets a plan leave the instigator
        // unwired, but whether this receiver's entries and kill attribution work with none is unverified, so it is refused,
        // with the same code the player receiver gives an unwired instigator.
        if (context.Present(EnemyRegistration.DamageSourceSlot)
                && !context.TryEntity(EnemyRegistration.DamageSourceSlot, out _))
            throw new RuntimeContractException("shape-port", "damage roles");
        if (context.Kind(EnemyRegistration.DamageInstigatorSlot) == ValueKind.Missing) return CommandResult.Rejected(ResultCode.InstigatorMissing);
        if (!context.TryEntity(EnemyRegistration.DamageInstigatorSlot, out var instigator))
            return CommandResult.Rejected(ResultCode.StaleOrUnsupportedInstigator);
        Agent? nativeInstigator;
        try { nativeInstigator = NativeAgentOf(instigator); }
        catch (RuntimeContractException error) { return CommandResult.Rejected(error.Code, error.Message); }
        if (nativeInstigator == null) return CommandResult.Rejected(ResultCode.StaleOrUnsupportedInstigator);
        double requested = context.Number(EnemyRegistration.DamageAmountSlot) ?? double.NaN;
        if (!double.IsFinite(requested) || requested < DamageMinimumAmount || requested > DamageMaximumAmount)
            return CommandResult.Rejected(ResultCode.AmountOutOfRange);
        // Some kinds have no equivalent native sender. Their explicit adapter still enters the complete
        // synced damage funnel, while the request scope retains the actual kind for rules and observed facts.
        int kind = DamageKindIndexOf(context.Text(EnemyRegistration.DamageKindSlot));
        if (kind < 0) return CommandResult.Rejected(ResultCode.DamageKindUnsupported);
        int limb = -1;
        if (context.Present(EnemyRegistration.DamageLimbSlot))
        {
            // An omitted optional port arrives as null; only a number can name a limb.
            var limbValue = context.Integer(EnemyRegistration.DamageLimbSlot);
            if (limbValue is null || limbValue.Value < -1 || limbValue.Value > int.MaxValue)
                return CommandResult.Rejected(ResultCode.InvalidLimb);
            limb = (int)limbValue.Value;
        }
        // The two multipliers are the effect's own weakspot and stagger factors, which the native entry takes as
        // separate arguments from the requested amount. An unwired port is the neutral 1; a present value that is
        // not a finite positive number names no multiplier and is refused whole.
        if (!OptionalMultiplier(context, EnemyRegistration.DamagePrecisionSlot, out float precisionMulti))
            return CommandResult.Rejected(ResultCode.PrecisionMultiOutOfRange);
        if (!OptionalMultiplier(context, EnemyRegistration.DamageStaggerSlot, out float staggerMulti))
            return CommandResult.Rejected(ResultCode.StaggerMultiOutOfRange);
        // The native explosion entry has no multiplier arguments; a plan cannot believe it asked for one.
        if (kind == 2 && (precisionMulti != 1 || staggerMulti != 1))
            return CommandResult.Rejected(ResultCode.ExplosionMultipliersUnsupported);
        Vector3? hitOrigin = null;
        if (context.Present(EnemyRegistration.DamageHitOriginSlot))
        {
            if (GameVectors.Unity(context.Vector(EnemyRegistration.DamageHitOriginSlot)) is not { } origin)
                return CommandResult.Rejected(ResultCode.HitOriginInvalid);
            hitOrigin = origin;
        }
        // The rows are the whole result: every recipient of the set gets its row, however many the set holds.
        var request = new DamageRequest(this, instigator, nativeInstigator, requested, kind, limb, staggerMulti,
            precisionMulti, hitOrigin, DamageAdapter(kind));
        var outcome = context.EachTarget(ref request, DamageStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionCombatDamage.RowTargetCount);
        return outcome.WithDetail(DamageDetails[kind]);
    }

    /// <summary>What the native entry is, per kind; the same for every target of a dispatch.</summary>
    private static string? DamageAdapter(int kind) => kind switch
    {
        3 => ResultCode.AdapterDotThroughNativeBullet,
        5 => ResultCode.AdapterCollisionThroughNativeBullet,
        6 => ResultCode.AdapterFallThroughNativeBullet,
        7 => ResultCode.AdapterEnvironmentThroughNativeBullet,
        8 => ResultCode.AdapterReflectionThroughNativeBullet,
        _ => null
    };

    /// <summary>The damage result's detail, one per kind: it depends on nothing else, so it is built once.
    /// Explosion has one settlement for every instigator: the native ExplosionDamage entry, on host and client
    /// alike. Its packet has no actor field, so the receiving peer attributes the hit to its own local player.</summary>
    private static readonly string[] DamageDetails = DamageDetailsByKind();

    private static string[] DamageDetailsByKind()
    {
        var details = new string[DamageKindCount];
        for (int kind = 0; kind < DamageKindCount; kind++)
        {
            string? adapter = DamageAdapter(kind);
            details[kind] = RuntimeJson.From(new
            {
                damageKind = DamageKindNames[kind],
                entry = kind == 1 ? "MeleeDamage" : kind == 2 ? "ExplosionDamage" : "BulletDamage",
                adapter,
                reason = adapter != null ? "no-equivalent-source-preserving-native-entry"
                    : kind == 2 ? "native-explosion-packet-has-no-actor" : "native-entry",
                attribution = kind == 2 ? "receiver-selects-each-peers-local-player" : "explicit-instigator"
            }).GetRawText();
        }
        return details;
    }

    private readonly record struct DamageRequest(EnemyModule Module, EntityReference Instigator, Agent NativeInstigator,
        double Requested, int Kind, int Limb, float StaggerMulti, float PrecisionMulti, Vector3? HitOrigin, string? Adapter);

    private static readonly TargetStep<DamageRequest> DamageStep = static (ref DamageRequest request, EntityReference target, int _, RowWriter rows)
        => rows.End(request.Module.DamageOne(in request, target));

    /// <summary>One recipient. The row's own status is the ABI's `committed` column: only a landed hit confirmed
    /// it, a refusal confirmed nothing, and a hit this side could not observe afterwards stays unknown.</summary>
    private BehaviorOutcome DamageOne(in DamageRequest request, EntityReference target)
    {
        static BehaviorOutcome Landed(string code) => new(Status.Succeeded, Commit.Confirmed, code);
        try
        {
            if (!CanExecute) return BehaviorOutcome.Refused(ResultCode.AuthorityOrPhase);
            var currentInstigator = NativeAgentOf(request.Instigator);
            if (currentInstigator == null || NativeAgentPointer(currentInstigator) != NativeAgentPointer(request.NativeInstigator))
                return BehaviorOutcome.Refused(ResultCode.StaleOrUnsupportedInstigator);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.PreflightException); }
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        var enemy = entry.Enemy;
        var damage = enemy.Damage;
        if (damage == null || !damage.IsSetup || damage.Pointer == IntPtr.Zero)
            return BehaviorOutcome.Refused(ResultCode.MissingHealthReceiver);
        var receiverPointer = damage.Pointer;
        if (damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer)
            return BehaviorOutcome.Refused(ResultCode.HealthReceiverOwnerMismatch);
        if (!enemy.Alive || !(damage.Health > 0)) return BehaviorOutcome.Refused(ResultCode.NotAlive);
        float before = damage.Health, maximum = damage.HealthMax;
        if (!float.IsFinite(before) || !float.IsFinite(maximum) || maximum <= 0 || before > maximum)
            return BehaviorOutcome.Refused(ResultCode.InvalidHealthState);
        if (!EnemyNativeWrite.TryNameLimb(damage, request.Limb, out int limbIndex))
            return BehaviorOutcome.Refused(ResultCode.InvalidLimb);

        // `hit_origin` names where the hit came from and the enemy's own position is the hit point, so the
        // entry's direction argument is origin -> target and the directional bonus is asked for. Without the
        // port the row keeps the neutral geometry it submitted before the port existed.
        var position = Vector3.zero;
        var direction = Vector3.zero;
        bool directional = false;
        if (request.HitOrigin is { } hitOrigin)
        {
            Vector3 targetPosition;
            try { targetPosition = enemy.Position; }
            catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.EnemyPositionUnavailable); }
            direction = targetPosition - hitOrigin;
            if (!float.IsFinite(direction.x) || !float.IsFinite(direction.y) || !float.IsFinite(direction.z)
                || direction.sqrMagnitude <= 0f)
                return BehaviorOutcome.Refused(ResultCode.HitOriginInvalid);
            position = targetPosition;
            direction = direction.normalized;
            directional = true;
        }

        // Entering the native entry point is the only place the world is written. The hit is one call and the
        // readback is its only proof, so a failure after it stays unknown rather than being retried. The plan's
        // instigator of an explosion is never smuggled into the hit (see DamageDetails).
        float amount = (float)request.Requested;
        try
        {
            using var native = BeginNativeDamageRequest(damage, request.Kind);
            switch (request.Kind)
            {
                case 1:
                    ForgeCore.Combat.Melee(damage, amount, request.NativeInstigator, position, direction, limbIndex,
                        request.StaggerMulti, request.PrecisionMulti);
                    break;
                case 2:
                    ForgeCore.Combat.ExplosionDamage(damage, amount, request.HitOrigin ?? position, Vector3.zero, limbIndex);
                    break;
                default: // Direct hits and fragments share the native bullet damage receiver.
                    EnemyNativeWrite.ApplyDamage(damage, limbIndex, amount, request.StaggerMulti,
                        request.PrecisionMulti, position, direction, directional, request.NativeInstigator);
                    break;
            }
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }
        float after;
        try
        {
            if (!CanExecute) return BehaviorOutcome.Unseen(ResultCode.AuthorityOrPhase);
            var current = Resolve(target);
            if (current == null || !ReferenceEquals(current, entry) || enemy.Damage == null
                || enemy.Damage.Pointer != receiverPointer || !enemy.Damage.IsSetup
                || enemy.Damage.Owner == null || enemy.Damage.Owner.Pointer != entry.EnemyPointer
                || enemy.Damage.HealthMax != maximum)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            after = enemy.Damage.Health;
            if (!float.IsFinite(after) || after < 0 || after > maximum)
                return BehaviorOutcome.Unseen(ResultCode.UnexpectedHealthReadback);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        // `after > before` can only be the receiver's own rules responding to the hit; the requested amount is
        // never reported as the committed one.
        if (after > before) return BehaviorOutcome.Unseen(ResultCode.UnexpectedHealthRise);
        // The native call returned and the readback is unchanged: this target took 0 damage (a lock or a rule
        // nullified it). That is a settled result, not an unknown commit.
        if (after == before) return Landed(ResultCode.NoHealthChange);
        return Landed(request.Adapter ?? ResultCode.Committed);
    }

    private const string HealDetailNoRegen =
        "{\"adapter\":\"SendSetHealth\",\"reason\":\"enemy-add-health-empty\",\"ceiling\":\"min-health-max-and-cap\",\"cancelRegen\":\"no-native-regen\"}";
    private const string HealDetailNotRequested =
        "{\"adapter\":\"SendSetHealth\",\"reason\":\"enemy-add-health-empty\",\"ceiling\":\"min-health-max-and-cap\",\"cancelRegen\":\"not-requested\"}";

    private CommandResult Heal(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        // Source carries no faction or targeting restriction (teammate/hostile/self are all valid);
        // it is still a required, kernel-validated recipient reference.
        if (!context.TryEntity(EnemyRegistration.HealSourceSlot, out var healSource))
            throw new RuntimeContractException("shape-port", "heal source");
        var policy = context.Text(EnemyRegistration.HealOverhealSlot);
        if (policy is not ("clamp" or "discard")) return CommandResult.Rejected(ResultCode.OverhealPolicyUnsupported);
        // Enemy has no natural-regeneration timer. cancel_regen is already satisfied for this receiver;
        // authored regeneration remains owned by the same Runtime effect, never by a new enemy timer.
        bool cancelRegen = context.Boolean(EnemyRegistration.HealCancelRegenSlot) == true;
        double requested = context.Number(EnemyRegistration.HealAmountSlot) ?? double.NaN;
        if (!double.IsFinite(requested) || requested < MinimumAmount || requested > MaximumAmount)
            return CommandResult.Rejected(ResultCode.AmountOutOfRange);
        double? cap = null;
        if (context.Present(EnemyRegistration.HealCapSlot))
        {
            double capValue = context.Number(EnemyRegistration.HealCapSlot) ?? double.NaN;
            if (!double.IsFinite(capValue) || capValue <= 0) return CommandResult.Rejected(ResultCode.InvalidCap);
            cap = capValue;
        }
        // A fully successful heal publishes one fact per target; the fact budget is the full recipient-set width (256). A heal
        // covers enemies and players alike, so the skeleton is asked for no one kind. A row that fell short of
        // the quantized amount is `partial + confirmed`: committed, but not done.
        var request = new HealRequest(this, context, healSource, requested, cap, policy, cancelRegen);
        var outcome = context.EachTarget(ref request, HealStep, null, CommandResult.MaximumFacts,
            ResultCode.NativeCommitException, doneWhenSucceeded: true, report: EnemyRows.Contained, countColumn: Blocks.ActionCombatHeal.RowTargetCount);
        return outcome.WithDetail(cancelRegen ? HealDetailNoRegen : HealDetailNotRequested);
    }

    private readonly record struct HealRequest(EnemyModule Module, CommandContext Context, EntityReference Source,
        double Requested, double? Cap, string Policy, bool CancelRegen);

    private static readonly TargetStep<HealRequest> HealStep = static (ref HealRequest request, EntityReference target, int _, RowWriter rows)
        => request.Module.HealOne(ref request, target, rows);

    /// <summary>One heal row: `amount` is the health the target was observed to gain, 0 for a row that gained none.</summary>
    private static void HealRow(RowWriter rows, Status status, string code, double amount = 0)
        => rows.Number(Blocks.ActionCombatHeal.RowAmount, amount).End(status, code);

    private void HealOne(ref HealRequest request, EntityReference target, RowWriter rows)
    {
        // A player recipient is healed by the owner of the player domain; the enemy resolver below only knows enemies.
        if (EntityKinds.Is(target, EntityKinds.Player))
        {
            TraceHealRecipient(request.Context, target, null, false);
            HealPlayer(in request, target, rows);
            return;
        }
        var entry = Resolve(target);
        if (entry == null)
        {
            TraceHealRecipient(request.Context, target, null, false);
            HealRow(rows, Status.Rejected, UnresolvedCode(target)); return;
        }
        var enemy = entry.Enemy;
        var damage = enemy.Damage;
        TraceHealRecipient(request.Context, target, entry, damage != null && damage.IsSetup && damage.Pointer != IntPtr.Zero);
        if (damage == null || !damage.IsSetup || damage.Pointer == IntPtr.Zero)
        { HealRow(rows, Status.Rejected, ResultCode.MissingHealthReceiver); return; }
        var receiverPointer = damage.Pointer;
        if (damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer)
        { HealRow(rows, Status.Rejected, ResultCode.HealthReceiverOwnerMismatch); return; }
        if (!enemy.Alive || !(damage.Health > 0)) { HealRow(rows, Status.Rejected, ResultCode.NotAlive); return; }
        float before = damage.Health, maximum = damage.HealthMax;
        if (!float.IsFinite(before) || !float.IsFinite(maximum) || maximum <= 0 || before > maximum)
        { HealRow(rows, Status.Rejected, ResultCode.InvalidHealthState); return; }
        double effectiveCap = request.Cap.HasValue ? Math.Min(maximum, request.Cap.Value) : maximum;
        double desiredUncapped = before + request.Requested;
        if (desiredUncapped > effectiveCap && request.Policy == "discard")
        { HealRow(rows, Status.Rejected, ResultCode.WouldOverheal); return; }
        double desired = Math.Max(before, Math.Min(effectiveCap, desiredUncapped));
        float quantized;
        try
        {
            var encoded = new SFloat16();
            encoded.Set((float)desired, maximum);
            quantized = encoded.Get(maximum);
        }
        catch (Exception caught)
        {
            EnemyLog.Contained(caught);
            // No SendSetHealth call has occurred; unlike commit exceptions this is known uncommitted.
            HealRow(rows, Status.Rejected, ResultCode.QuantizationFailed); return;
        }
        if (!float.IsFinite(quantized) || quantized < 0 || quantized > maximum)
        { HealRow(rows, Status.Rejected, ResultCode.QuantizationFailed); return; }
        // Native preview/getters can re-enter other mods. Revalidate even a no-op completion;
        // otherwise an absolute HP value computed from an old snapshot could damage the recipient.
        try
        {
            if (!CanExecute) { HealRow(rows, Status.Rejected, ResultCode.AuthorityOrPhase); return; }
            if (Resolve(target) != entry || enemy.Damage == null || enemy.Damage.Pointer != receiverPointer
                || damage.Pointer != receiverPointer
                || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer || !damage.IsSetup)
            { HealRow(rows, Status.Rejected, ResultCode.ReceiverChangedDuringCommit); return; }
            if (!enemy.Alive || damage.Health != before || damage.HealthMax != maximum)
            { HealRow(rows, Status.Rejected, ResultCode.StateChangedBeforeCommit); return; }
            if (!CanExecute) { HealRow(rows, Status.Rejected, ResultCode.AuthorityOrPhase); return; }
        }
        catch (Exception caught)
        { EnemyLog.Contained(caught); HealRow(rows, Status.Rejected, ResultCode.PreflightException); return; }

        float after = before;
        if (desired > before && quantized > before)
        {
            // Crossing the native call boundary may have sent a packet even when the call throws; that failure
            // mode (native-commit-exception) is distinct from a later, already-committed readback failure
            // (readback-exception), so each gets its own try/catch.
            // Build 20403457's inherited ReceiveAddHealth is an empty native body. Enemy healing must use
            // this functioning native entry; AddHealth would send a request that the enemy never applies.
            try { damage.SendSetHealth((float)desired); }
            catch (Exception caught) { EnemyLog.Contained(caught); HealRow(rows, Status.Failed, ResultCode.NativeCommitException); return; }
            try
            {
                if (!CanExecute || Resolve(target) == null
                    || enemy.Damage == null || enemy.Damage.Pointer != receiverPointer || damage.Pointer != receiverPointer
                    || !damage.IsSetup || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer)
                { HealRow(rows, Status.Failed, ResultCode.ReceiverChangedDuringCommit); return; }
                after = damage.Health;
                if (!enemy.Alive || damage.HealthMax != maximum || !float.IsFinite(after) || after <= before || after > maximum)
                { HealRow(rows, Status.Failed, ResultCode.UnexpectedHealthReadback); return; }
            }
            catch (Exception caught)
            { EnemyLog.Contained(caught); HealRow(rows, Status.Failed, ResultCode.ReadbackException); return; }
        }
        double actual = (double)after - before;
        bool partiallyApplied = desired > before && quantized > before && after < quantized;
        HealRow(rows, partiallyApplied ? Status.Partial : Status.Succeeded, ResultCode.Committed, actual);
        entry.LastWindowHealth = after; entry.LastWindowGeneration = _kernel.SubscriptionGeneration;
        // Zero effective healing is a valid result, but is never published as a health-change fact. The maximum
        // was checked positive and finite before the write and read back unchanged after it, so both required
        // ports (max, ratio) come from the receiver's own `HealthMax`.
        if (actual > 0 && _healthChangedGate.TryBeginFact(request.Context, out var fact))
        {
            fact.Values.Entity(Blocks.TriggerCombatHealthChanged.OutTarget, target);
            fact.Values.Number(Blocks.TriggerCombatHealthChanged.OutValue, after);
            fact.Values.Number(Blocks.TriggerCombatHealthChanged.OutDelta, actual);
            fact.Values.Number(Blocks.TriggerCombatHealthChanged.OutMax, maximum);
            fact.Values.Number(Blocks.TriggerCombatHealthChanged.OutRatio, (double)after / maximum);
            fact.Add();
        }
    }

    /// <summary>One player recipient of a heal, answered by the player domain's healing entry. A missing
    /// provider or a throwing entry is named, never answered as a success.</summary>
    private static void HealPlayer(in HealRequest request, EntityReference target, RowWriter rows)
    {
        if (ForgePlayer.Native.PlayerHost.Session?.Module is not { } players)
        { HealRow(rows, Status.Rejected, ResultCode.PlayerHealingMissing); return; }
        Status status; Commit commit; string code;
        double amount;
        try
        {
            var receipt = ForgePlayer.Native.PlayerHealthAction.HealOne(players, new PlayerHealRequest(target, request.Source,
                request.Context.EntityInstance(request.Source), request.Requested, request.Cap, request.Policy, request.CancelRegen));
            (status, commit, code, amount) = (receipt.Status, receipt.Commit, receipt.Code, receipt.Amount);
        }
        catch (Exception caught)
        {
            EnemyLog.Contained(caught);
            HealRow(rows, Status.Failed, ResultCode.PlayerHealException); return;
        }
        rows.Number(Blocks.ActionCombatHeal.RowAmount, amount).End(status, commit, code);
    }

    /// <summary>One optional multiplier input: an unwired or null port is the neutral 1, and a present value that
    /// is not a finite positive number names no multiplier. The entry takes these as separate arguments, so the
    /// row can carry an effect's own weakspot and stagger factors without changing the requested amount.</summary>
    private static bool OptionalMultiplier(CommandContext context, HandlerSlot slot, out float value)
    {
        value = 1f;
        if (!context.Present(slot)) return true;
        double number = context.Number(slot) ?? double.NaN;
        if (!double.IsFinite(number) || number <= 0 || number > float.MaxValue) return false;
        value = (float)number;
        return true;
    }
}
