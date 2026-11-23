using System;
using System.Collections.Generic;
using System.Linq;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The wave facts of build 20403457. Every one of them is a Forge derivation from native state, because
/// the game has no wave started/exhausted/cleared event (see
/// <c>ForgeEnemy/evidence/enemy-wave-facts.json</c> and <c>ForgeMap/evidence/encounter-wave-hooks.json</c>):
///
///   * ForgeMap registers the exported wave resource address against the allocated native EventID before
///     spawn. That address is the fact scope; an unregistered EventID is explicitly refused;
///   * `wave_started` is published once for the whole wave after its first native spawn callback;
///   * `wave_spawned` is one <c>SurvivalWave.SpawnGroup</c> call: the members of every group that call
///     registered belong to that one batch, and `count` is how many of them this provider can name;
///   * `wave_exhausted` observes a successful StopEvent from the native budget-end branch (including the inline update),
///     and waits for every registered native event and no pending delayed member;
///   * `wave_cleared` requires whole-wave exhaustion and no group remaining in <c>Mastermind.m_activeGroups</c>, read after the
///     Mastermind's own group maintenance, which is the native answer to "is anything of this wave still alive".
///
/// A teardown is not a clear: cancellation retires unfinished waves. Exhausted waves retain their live groups
/// until maintenance confirms the last one is gone. Nothing here writes the world, and
/// no fact is emitted for a wave this provider has not observed spawn.
///
/// The rows declare no `wave` handle output: an observation mints no handle, so the registered resource identity
/// travels in the frame's scope id. See <c>EnemyWaveContract</c> and the evidence file.</summary>
internal sealed class EnemyWaveFacts
{
    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly Func<bool> _canObserve;
    private readonly EnemyLog _log;
    private readonly Func<EnemyAgent?, EntityReference?> _referenceOf;
    /// <summary>PERF(P5): the wave bindings' kernel gates, resolved once at attach in
    /// <see cref="EnemyWaveContract.Bindings"/> order.</summary>
    private readonly EnemyGate[] _gates;
    /// <summary>PERF(P5): the Mastermind's live group pointers, refilled by each maintenance read instead of a new
    /// set per frame.</summary>
    private readonly HashSet<IntPtr> _alive = new();
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    /// <summary>The state one observed wave has in this world.</summary>
    private sealed class WaveState
    {
        internal WaveState(ushort eventId, IntPtr pointer, LogicalWave whole) { EventId = eventId; Pointer = pointer; Whole = whole; }
        internal ushort EventId { get; }
        internal LogicalWave Whole { get; }
        /// <summary>The native instance this state was opened for. Re-checked on every touch, so a recycled
        /// wrapper or a second wave on the same EventID is never read as this one.</summary>
        internal IntPtr Pointer { get; }
        internal bool Exhausted;
        internal int PendingEndStops;
        internal bool Despawned;
        internal bool Retired;
        internal float BudgetTotal = -1, BudgetSpent = -1;
        internal string StopReason = "none";
        internal long StopChecks;
    }

    private sealed class LogicalWave
    {
        internal LogicalWave(string address, long instance)
        { Address = address; Instance = instance; }
        internal readonly string Address;
        internal readonly long Instance;
        // Captured native instances survive retirement from the reusable EventID index while their own groups live.
        internal readonly List<WaveState> Events = new();
        /// <summary>The groups this wave registered and that have not left the Mastermind's list yet. Members are
        /// native pointers: the interop layer hands out a new managed wrapper for the same native object on every
        /// call, so identity is the pointer the whole file compares.</summary>
        internal readonly Dictionary<IntPtr, GroupObservation> LiveGroups = new();
        /// <summary>Groups this wave registered over its whole life; `wave_exhausted.count`.</summary>
        internal int Groups;
        internal bool Exhausted;
        internal bool Cleared;
        internal bool Cancelled;
        internal long MembersObserved, EndChecks;
        /// <summary>Group maintenance passes that found this wave exhausted but not cleared, and why the last one did not clear it.</summary>
        internal long ClearChecks;
        /// <summary>The last block's reason and counts, kept as plain fields: the text is only built when a record is written.</summary>
        internal string ClearHeld = "none";
        internal int ClearAlive, ClearActive, ClearPending, ClearLive;
        internal bool DiagnosticsEnded;
    }

    /// <summary>One open <c>SurvivalWave.SpawnGroup</c> call: the batch boundary the game itself does not have,
    /// defined as the groups that call registers. Its prefix/finalizer bound the original call; successful
    /// batches remain pending until all their original group request loops complete. Outside registrations
    /// are tracked for wave liveness but never appended to an unrelated batch.</summary>
    private sealed class WaveBatch
    {
        internal WaveBatch(ushort eventId, WaveState state, WaveGroupObservation observation)
        { EventId = eventId; State = state; Observation = observation; }
        internal readonly ushort EventId;
        internal readonly WaveState State;
        internal IntPtr Wave => State.Pointer;
        internal readonly WaveGroupObservation Observation;
        internal readonly List<GroupObservation> Delivered = new();
        internal readonly HashSet<IntPtr> DeliveredPointers = new();
    }

    private sealed class GroupObservation
    {
        internal GroupObservation(EnemyGroup group, WaveState state, WaveBatch? batch, long sequence)
        { Group = group; Pointer = group.Pointer; State = state; Batch = batch; Sequence = sequence; }
        internal readonly long Sequence;
        internal readonly EnemyGroup Group;
        internal readonly IntPtr Pointer;
        internal readonly WaveState State;
        internal readonly WaveBatch? Batch;
        internal readonly List<MemberDelivery> Members = new();
        internal bool Completed;
    }

    private sealed class MemberDelivery
    {
        internal MemberDelivery(EnemyAgent agent, EntityReference? reference)
        { Agent = agent; Pointer = agent.Pointer; Id = agent.GlobalID; Reference = reference; }
        internal readonly EnemyAgent Agent;
        internal readonly IntPtr Pointer;
        internal readonly ushort Id;
        internal EntityReference? Reference;
        internal bool ResolveAfterStep = true;
    }

    internal sealed class WaveGroupObservation
    {
        private readonly EnemyWaveFacts _owner;
        private bool _consumed;
        internal WaveGroupObservation(EnemyWaveFacts owner) { _owner = owner; Epoch = owner._kernel.WorldEpoch; }
        internal readonly long Epoch;
        internal bool TryConsume(EnemyWaveFacts owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    internal sealed class GroupSpawningObservation
    {
        private readonly EnemyWaveFacts _owner;
        private bool _consumed;
        internal GroupSpawningObservation(EnemyWaveFacts owner, IntPtr pointer, long sequence)
        { _owner = owner; Pointer = pointer; Sequence = sequence; Epoch = owner._kernel.WorldEpoch; }
        internal readonly IntPtr Pointer;
        internal readonly long Sequence;
        internal readonly long Epoch;
        internal bool TryConsume(EnemyWaveFacts owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    /// <summary>A read-only scope around one original native end check. StopEvent is also used for spawn
    /// refusal and cancellation, so budget equality by itself at an arbitrary StopEvent is not exhaustion.</summary>
    internal sealed class WaveEndObservation
    {
        /// <summary>PERF(E4): a scope is open for the length of one native call and closed by its finalizer, so the
        /// owner recycles a closed one instead of allocating a token per wave tick.</summary>
        internal void Open(IntPtr wave, ushort eventId, long epoch, long instance, bool exhausted)
        { Wave = wave; EventId = eventId; Epoch = epoch; Instance = instance; Exhausted = exhausted; }
        internal IntPtr Wave { get; private set; }
        internal ushort EventId { get; private set; }
        internal long Epoch { get; private set; }
        internal long Instance { get; private set; }
        internal bool Exhausted { get; private set; }
    }

    internal sealed class WaveStopObservation
    {
        private readonly EnemyWaveFacts _owner;
        private bool _consumed;
        internal WaveStopObservation(EnemyWaveFacts owner, IntPtr wave, ushort eventId, long instance, bool exhausts = true)
        { _owner = owner; Wave = wave; EventId = eventId; Instance = instance; Epoch = owner._kernel.WorldEpoch; Exhausts = exhausts; }
        internal readonly IntPtr Wave;
        internal readonly ushort EventId;
        internal readonly long Instance;
        internal readonly long Epoch;
        internal readonly bool Exhausts;
        internal bool TryConsume(EnemyWaveFacts owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    /// <summary>The spawn observation carries the native instance and world epoch. The postfix reads its native
    /// EventID and resolves the resource identity registered by ForgeMap before the native spawn call.</summary>
    internal sealed class WaveSpawnObservation
    {
        private readonly EnemyWaveFacts _owner;
        private bool _consumed;
        internal IntPtr Wave { get; }
        internal long Epoch { get; }
        internal WaveSpawnObservation(EnemyWaveFacts owner, IntPtr wave) { _owner = owner; Wave = wave; Epoch = owner._kernel.WorldEpoch; }
        internal bool TryConsume(EnemyWaveFacts owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    private WaveState?[]? _waves; // allocated by the first wave the game starts
    private readonly List<LogicalWave> _logical = new();
    // A publication watermark, keyed only by Map's existing run identity. Cleared runs release their group
    // records but a repeated native callback must not publish started again in the same world.
    private readonly HashSet<long> _observedInstances = new();
    private int _waveCount;
    private long _maintenancePasses;
    /// <summary>Map's wave resources: the identity of every run it registered, its cancel entry, and the relay record told
    /// the wave address of every exhausted and cleared fact, so a checkpoint restore knows which later list entries were owed a start.</summary>
    private ForgeMap.Native.WaveResourceSession? _mapWaves;
    private readonly List<WaveBatch> _pendingBatches = new();
    private readonly Dictionary<IntPtr, GroupObservation> _groups = new();
    private readonly List<WaveEndObservation> _endChecks = new();
    private readonly Stack<WaveEndObservation> _closedEndChecks = new();
    private readonly List<WaveStopObservation> _stops = new();
    private long _waveEpoch = long.MinValue;
    private WaveBatch? _batch;
    private long _eventSequence;
    private long _groupSequence;

    internal EnemyWaveFacts(IForgeEngine kernel, RuntimeModuleHandle registration, Func<bool> canObserve,
        EnemyLog log, Func<EnemyAgent?, EntityReference?> referenceOf, IReadOnlyCollection<string>? withdrawnBindings = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _canObserve = canObserve ?? throw new ArgumentNullException(nameof(canObserve));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _referenceOf = referenceOf ?? throw new ArgumentNullException(nameof(referenceOf));
        _gates = new EnemyGate[EnemyWaveContract.Bindings.Length];
        for (int index = 0; index < _gates.Length; index++)
        {
            var binding = EnemyWaveContract.Bindings[index];
            _gates[index] = new EnemyGate(binding, withdrawnBindings?.Contains(binding) == true ? null : registration.SubscriptionGate(binding));
        }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy wave facts require the owning simulation thread.");
    }

    /// <summary>The four bindings this family publishes through, as one set: the wave table is only kept while a
    /// plan subscribes to at least one of them, and a native hook that no plan feeds reads nothing.</summary>
    internal bool CanPublish => Subscribed && _canObserve();

    /// <summary>Whether any wave is tracked: the hooks that act only on a tracked wave stop here before they build
    /// anything. A count too high is harmless; it is never lower than the number of live table slots.</summary>
    internal bool HasTrackedWaves => _waveCount != 0;

    /// <summary>Whether any wave gate is open: field reads only.</summary>
    internal bool Subscribed
    {
        get
        {
            foreach (var gate in _gates) if (gate.HasSubscribers) return true;
            return false;
        }
    }

    /// <summary>Drops every wave of an earlier world. A new world invalidates the EventID space itself, so the
    /// table is emptied on first touch rather than trusted across the boundary — the same discipline an entity
    /// reference's world epoch gets.</summary>
    private void EnsureWaveEpoch()
    {
        if (_waveEpoch == _kernel.WorldEpoch) return;
        EndWorldDiagnostics();
        _waves = null; _waveCount = 0;
        _logical.Clear();
        _observedInstances.Clear();
        _pendingBatches.Clear();
        _groups.Clear();
        _endChecks.Clear();
        _stops.Clear();
        _batch = null;
        _waveEpoch = _kernel.WorldEpoch;
    }

    /// <summary>The wave a hook is about, or null when this provider never saw it spawn, when the EventID names a
    /// different native instance, or when the world moved on.</summary>
    private WaveState? TrackedWave(SurvivalWave? wave)
    {
        EnsureWaveEpoch();
        if (wave == null) return null;
        var state = _waves?[wave.EventID];
        if (state == null || state.Pointer != wave.Pointer) return null;
        return state;
    }

    internal WaveSpawnObservation? BeforeWaveSpawn(SurvivalWave? wave)
    {
        CheckThread();
        if (!CanPublish || wave == null) return null;
        EnsureWaveEpoch();
        // The native pool can reuse a pointer while the prior spent wave's groups remain alive. EventID and
        // Map's run identity are readable only after OnSpawn; duplicate/new-instance checks belong there.
        return new WaveSpawnObservation(this, wave.Pointer);
    }

    /// <summary>The native spawn callback ran. Its EventID must resolve to the wave resource ForgeMap registered;
    /// a missing registration is refused explicitly instead of publishing a made-up resource identity.</summary>
    internal void AfterWaveSpawn(SurvivalWave? wave, WaveSpawnObservation? before)
    {
        CheckThread();
        if (before == null || !before.TryConsume(this) || !CanPublish || wave == null
            || wave.Pointer != before.Wave || before.Epoch != _kernel.WorldEpoch) return;
        ushort eventId = wave.EventID;
        if (eventId == 0) { _log.Reason(RuntimeLogLevel.Error, EnemyLogCodes.WaveUntracked, "no-event-id"); return; }
        EnsureWaveEpoch();
        // Registration can be replaced; refresh at this native event boundary, never during maintenance.
        _mapWaves = ForgeMap.Native.MapHost.Session?.WaveResources;
        var identity = RegisteredIdentity(eventId, first: true);
        if (identity is { Cancelled: false } mapped && _waves?[eventId] is { } current && current.Whole.Instance == mapped.Instance)
        {
            if (current.Pointer != wave.Pointer) _log.Reason(RuntimeLogLevel.Error, EnemyLogCodes.WaveUntracked, "identity-mismatch");
            return;
        }
        // A run Map registered with several native members (one per exact-roster kind) reads the same identity
        // for every member event. A later member joins the open logical wave instead of opening a second one.
        LogicalWave? whole = null;
        if (identity is { } known)
            foreach (var registered in _logical)
                if (registered.Instance == known.Instance) { whole = registered; break; }
        if (whole == null && identity is { } ended && _observedInstances.Contains(ended.Instance))
        {
            // The run already ended here (cleared or cancelled); a member arriving after that cannot reopen it.
            _log.Reason(RuntimeLogLevel.Error, EnemyLogCodes.WaveUntracked, "member-after-wave-ended");
            return;
        }
        // Even an unregistered native replacement invalidates the old active slot. It may not adopt that
        // run's batches merely because the pointer/EventID was reused; confirmed old groups remain retained.
        foreach (var previous in _logical)
            foreach (var state in previous.Events)
                if (!state.Retired && (state.EventId == eventId || state.Pointer == wave.Pointer)) RetireWave(state);
        if (identity == null || identity.Value.Cancelled) return;
        if (whole != null)
        {
            // The member joins: started was published once for the whole wave; exhausted waits for every
            // registered member and for Map's pending count to reach zero.
            if (whole.Cancelled || whole.Exhausted || whole.Cleared)
            { _log.Reason(RuntimeLogLevel.Error, EnemyLogCodes.WaveUntracked, "member-after-wave-ended"); return; }
            var member = new WaveState(eventId, wave.Pointer, whole);
            whole.Events.Add(member);
            (_waves ??= new WaveState?[ushort.MaxValue + 1])[eventId] = member;
            _waveCount++;
            TraceWave(whole, "member-registered", whole.Events.Count, member);
            return;
        }
        whole = new LogicalWave(identity.Value.Address, identity.Value.Instance);
        if (RegisteredIdentity(whole) is not { Cancelled: false }) return;
        _logical.Add(whole);
        _observedInstances.Add(whole.Instance);
        var opened = new WaveState(eventId, wave.Pointer, whole);
        whole.Events.Add(opened);
        (_waves ??= new WaveState?[ushort.MaxValue + 1])[eventId] = opened;
        _waveCount++;
        TraceWave(whole, "registered", 1, opened);
        if (CanPublishWave(whole)) PublishWave(StartedGate, whole, BeginWave(StartedGate));
    }

    private void RetireWave(WaveState state)
    {
        state.Retired = true;
        if (_waves?[state.EventId] == state) _waves![state.EventId] = null;
        if (!state.Exhausted) state.Whole.Cancelled = true;
        if (_batch?.State == state) _batch = null;
        // Retirement is not native cancellation. In particular Cancel(eventId) would now cancel the new run.
    }

    private (string Address, long Instance, int Pending, bool Cancelled)? RegisteredIdentity(LogicalWave whole)
    {
        var identity = _mapWaves?.ReadInstanceIdentity(_kernel.WorldEpoch, whole.Instance);
        return identity is { } found && found.Instance == whole.Instance && found.Address == whole.Address ? identity : null;
    }

    private bool OwnsState(WaveState state)
        => !state.Whole.Cancelled && (_waves?[state.EventId] == state || state.Retired && state.Exhausted);

    private (string Address, long Instance, int Pending, bool Cancelled)? RegisteredIdentity(ushort eventId, bool first = false)
    {
        // Resolve Map's one service at the native spawn boundary, then keep the numeric EventID reader.
        // Maintenance and delayed-member checks never resolve service names or registries again.
        if (_mapWaves?.ReadIdentity(_kernel.WorldEpoch, eventId) is { } identity && identity.Address.Length > 0) return identity;
        // A wave seen for the first time that Map never registered is a native wave outside the exported
        // registry (a vanilla alarm or scan wave); Map keeps those on their normal path by design, so it is
        // only a trace. The same loss on a wave that was already tracked is an inconsistency and stays an error.
        var level = first ? RuntimeLogLevel.Trace : RuntimeLogLevel.Error;
        if (_log.Logs(level)) _log.Write(level, EnemyLogCodes.WaveUntracked, new RuntimeLogFacts
        {
            EventId = "native-wave/" + eventId,
            Values = new[] { RuntimeLogValue.Of("reason", "identity-unregistered"), RuntimeLogValue.Of("eventId", (long)eventId) }
        });
        return null;
    }

    /// <summary>A `SurvivalWave.SpawnGroup` call is starting: the batch boundary. A call nested inside another one
    /// does not open a second batch, so the outer call's own accounting stays the one that is published.</summary>
    internal WaveGroupObservation? BeforeWaveGroupStep(SurvivalWave? wave)
    {
        CheckThread();
        if (!CanPublish) return null;
        EnsureWaveEpoch();
        if (_batch != null) return null;
        var state = TrackedWave(wave);
        if (state == null || state.Whole.Cancelled) return null;
        var identity = RegisteredIdentity(state.EventId);
        if (identity == null || identity.Value.Instance != state.Whole.Instance || identity.Value.Cancelled) return null;
        FlushPendingBatches();
        var observation = new WaveGroupObservation(this);
        _batch = new WaveBatch(wave!.EventID, state, observation);
        return observation;
    }

    /// <summary>The native RegisterGroup body has assigned the owning SurvivalWave. A group observed inside
    /// this SpawnGroup call belongs to this batch; a registration outside it only contributes to wave liveness.</summary>
    internal void AfterWaveGroup(SurvivalWave? wave, EnemyGroup? group)
    {
        CheckThread();
        if (!CanPublish || group == null || wave == null || group.WasCollected || group.SurvivalWave == null
            || group.SurvivalWave.Pointer != wave.Pointer) return;
        var state = TrackedWave(wave);
        if (state != null)
        {
            var identity = RegisteredIdentity(state.EventId);
            if (state.Whole.Cancelled || identity == null || identity.Value.Instance != state.Whole.Instance
                || identity.Value.Cancelled) state = null;
        }
        // This successful native RegisterGroup explicitly assigned the object to its current owner, even
        // when that owner is unregistered or its OnSpawn postfix has not run. A reused group pointer can no
        // longer complete or keep alive the retired owner's batch. Do not invent a new logical identity.
        if (_groups.TryGetValue(group.Pointer, out var previous) && previous.State != state) _groups.Remove(group.Pointer);
        if (state == null) return;
        var batch = _batch?.State == state ? _batch : null;
        if (batch != null && !batch.DeliveredPointers.Add(group.Pointer)) return;
        if (batch == null && _groups.TryGetValue(group.Pointer, out var existing) && existing.State == state) return;
        var observed = new GroupObservation(group, state, batch, checked(++_groupSequence));
        _groups[group.Pointer] = observed;
        state.Whole.LiveGroups[group.Pointer] = observed;
        state.Whole.Groups++;
        if (DiagnosticSample(state.Whole.Groups)) TraceWave(state.Whole, "group", state.Whole.Groups, state);
        if (batch != null) batch.Delivered.Add(observed);
    }

    /// <summary>Close the original SpawnGroup call, not the asynchronous group's member-generation loop.
    /// A nested call did not open a scope and cannot close its parent. Failed original calls publish no batch.</summary>
    internal void AfterWaveGroupStep(SurvivalWave? wave, WaveGroupObservation? before, bool succeeded)
    {
        CheckThread();
        if (before == null || !before.TryConsume(this)) return;
        var batch = _batch;
        if (batch == null || batch.Observation != before) return;
        _batch = null;
        if (!succeeded || !CanPublish) return;
        EnsureWaveEpoch();
        if (before.Epoch != _waveEpoch || wave == null || wave.Pointer != batch.Wave || !CurrentBatch(batch)) return;
        _pendingBatches.Add(batch);
        FlushPendingBatches();
    }

    /// <summary>Only an argument actually appended to this group's native Members list is a delivery. The
    /// normal host RegisterMember callback precedes EnemySync.OnSpawn's postfix, so its current module life may
    /// not exist yet. Resolve it at the enclosing SurvivalSpawning return, never by a later frame heuristic.</summary>
    internal void AfterGroupMember(EnemyGroup? group, EnemyAgent? member)
    {
        CheckThread();
        if (!CanPublish || group == null || member == null) return;
        var observed = ObservedGroup(group);
        if (observed == null || observed.Completed || observed.Batch == null) return;
        var members = observed.Group.Members;
        if (members == null) return;
        for (int index = 0; index < members.Count; index++)
            if (members[index]?.Pointer == member.Pointer && members[index].GlobalID == member.GlobalID)
            { CaptureMember(observed, members[index]); return; }
    }

    internal GroupSpawningObservation? BeforeGroupSpawning(EnemyGroup? group)
    {
        CheckThread();
        if (!CanPublish || group == null) return null;
        var observed = ObservedGroup(group);
        if (observed == null || observed.Completed || observed.Batch == null) return null;
        return new GroupSpawningObservation(this, observed.Pointer, observed.Sequence);
    }

    /// <summary>The decoded normal-host chain registers each successful member synchronously before returning
    /// from SpawnEnemy, then returns from SurvivalSpawning. Its true result ends requesting, not proof of a
    /// successful request. Only the actual registered members can be published, including an honest empty batch.</summary>
    internal void AfterGroupSpawning(EnemyGroup? group, GroupSpawningObservation? before, bool completed)
    {
        CheckThread();
        if (before == null || !before.TryConsume(this) || !CanPublish || group == null) return;
        var observed = ObservedGroup(group);
        if (before.Epoch != _waveEpoch || group.Pointer != before.Pointer || observed == null
            || observed.Sequence != before.Sequence || observed.Completed || observed.Batch == null) return;
        for (int index = observed.Members.Count - 1; index >= 0; index--)
        {
            var delivered = observed.Members[index];
            if (!delivered.ResolveAfterStep) continue;
            // RegisterMember can precede retirement/replacement of a cached module life, not just its creation.
            // Refresh each registration at this return even when the callback initially saw a non-null life.
            // Older deliveries without a new registration stay pinned and can never inherit a recycled life.
            delivered.ResolveAfterStep = false;
            delivered.Reference = CurrentMember(delivered);
            if (delivered.Reference == null) observed.Members.RemoveAt(index);
        }
        if (!completed) return;
        observed.Completed = true;
        FlushPendingBatches();
    }

    private GroupObservation? ObservedGroup(EnemyGroup group)
    {
        EnsureWaveEpoch();
        if (!_groups.TryGetValue(group.Pointer, out var observed) || !OwnsGroup(observed, group)) return null;
        return observed;
    }

    private bool OwnsGroup(GroupObservation observed, EnemyGroup group)
    {
        if (group.WasCollected || group.Pointer != observed.Pointer
            || !_groups.TryGetValue(observed.Pointer, out var current) || current != observed) return false;
        if (!OwnsState(observed.State)) return false;
        var wave = group.SurvivalWave;
        // A spent native wave can despawn before its live groups. Their captured owner survives that teardown;
        // never read EventID/settings from the destroyed parent object. A changed non-null owner is still refused.
        if (wave != null ? wave.Pointer != observed.State.Pointer : !observed.State.Despawned && !observed.State.Exhausted)
            return false;
        return RegisteredIdentity(observed.State.Whole) is { Cancelled: false } identity
            && identity.Instance == observed.State.Whole.Instance && !observed.State.Whole.Cancelled;
    }

    private void CaptureMember(GroupObservation group, EnemyAgent? agent)
    {
        if (agent == null) return;
        var reference = _referenceOf(agent);
        foreach (var delivered in group.Members)
        {
            if (reference != null && delivered.Reference == reference)
            { delivered.ResolveAfterStep = true; return; }
            if (delivered.Reference == null && delivered.Pointer == agent.Pointer && delivered.Id == agent.GlobalID)
            { delivered.Reference = reference; delivered.ResolveAfterStep = true; return; }
        }
        var member = new MemberDelivery(agent, reference);
        group.Members.Add(member);
        if (DiagnosticSample(++group.State.Whole.MembersObserved))
        {
            TraceWave(group.State.Whole, reference == null ? "member-unresolved" : "member", group.State.Whole.MembersObserved, group.State);
            if (ForgeEngine.Plugin.ConfiguredMode == ForgeEngine.RuntimeMode.Authoring && _log.Logs(RuntimeLogLevel.Info))
                _log.Write(RuntimeLogLevel.Info, "enemy.wave-lifecycle", new RuntimeLogFacts
                {
                    Entity = reference?.Id,
                    Values = new[] { RuntimeLogValue.Of("phase", "member-identity"),
                        RuntimeLogValue.Of("instance", group.State.Whole.Instance), RuntimeLogValue.Quote("address", group.State.Whole.Address),
                        RuntimeLogValue.Of("groupPointer", group.Pointer.ToString("x")), RuntimeLogValue.Of("memberPointer", member.Pointer.ToString("x")),
                        RuntimeLogValue.Of("nativeId", (long)member.Id), RuntimeLogValue.Of("life", reference?.LifeEpoch ?? -1),
                        RuntimeLogValue.Of("world", reference?.WorldEpoch ?? -1), RuntimeLogValue.Of("count", group.State.Whole.MembersObserved) }
                });
        }
    }

    private EntityReference? CurrentMember(MemberDelivery delivered)
        => delivered.Agent.Pointer == delivered.Pointer && delivered.Agent.GlobalID == delivered.Id
            ? _referenceOf(delivered.Agent) : null;

    private bool CurrentBatch(WaveBatch batch)
    {
        if (batch.Observation.Epoch != _waveEpoch || !OwnsState(batch.State)
            || batch.State.Whole.Cancelled) return false;
        var identity = RegisteredIdentity(batch.State.Whole);
        if (identity == null || identity.Value.Instance != batch.State.Whole.Instance || identity.Value.Cancelled) return false;
        foreach (var group in batch.Delivered) if (!OwnsGroup(group, group.Group)) return false;
        return true;
    }

    private void FlushPendingBatches()
    {
        for (int index = _pendingBatches.Count - 1; index >= 0; index--)
        {
            var batch = _pendingBatches[index];
            if (CurrentBatch(batch))
            {
                bool waiting = false;
                foreach (var group in batch.Delivered) if (!group.Completed) { waiting = true; break; }
                if (waiting) continue;
                PublishBatch(batch);
            }
            _pendingBatches.RemoveAt(index);
        }
    }

    private void PublishBatch(WaveBatch batch)
    {
        var members = new List<EntityReference>();
        foreach (var group in batch.Delivered)
            foreach (var delivered in group.Members)
            {
                var reference = CurrentMember(delivered);
                if (reference != null && reference == delivered.Reference && !members.Contains(reference)) members.Add(reference);
            }
        var whole = batch.State.Whole;
        if (!CanPublishWave(whole)) return;
        var fact = BeginWave(SpawnedGate);
        if (fact is { } written)
        {
            written.Values.Entities(Blocks.TriggerObjectiveWaveSpawned.OutSpawned, members);
            written.Values.Integer(Blocks.TriggerObjectiveWaveSpawned.OutCount, members.Count);
        }
        PublishWave(SpawnedGate, whole, fact);
    }

    internal WaveEndObservation? BeforeWaveEndCheck(SurvivalWave? wave)
    {
        CheckThread();
        if (!CanPublish) return null;
        var state = TrackedWave(wave);
        if (state == null || state.Exhausted || state.Whole.Cancelled) return null;
        var identity = RegisteredIdentity(wave!.EventID);
        if (identity == null || identity.Value.Instance != state.Whole.Instance || identity.Value.Cancelled
            || wave.m_settings == null) return null;
        float total = wave.m_settings.m_populationPointsTotal;
        state.BudgetTotal = total;
        state.BudgetSpent = wave.m_populationPointsSpentTotal;
        var observation = _closedEndChecks.Count > 0 ? _closedEndChecks.Pop() : new WaveEndObservation();
        observation.Open(wave.Pointer, wave.EventID, _waveEpoch, state.Whole.Instance,
            total > 0 && state.BudgetSpent >= total);
        if (DiagnosticSample(++state.Whole.EndChecks)) TraceWave(state.Whole,
            observation.Exhausted ? "budget-end-candidate" : "budget-not-exhausted", state.Whole.EndChecks, state);
        _endChecks.Add(observation);
        return observation;
    }

    internal void AfterWaveEndCheck(WaveEndObservation? observation)
    {
        CheckThread();
        if (observation != null && _endChecks.Remove(observation)) _closedEndChecks.Push(observation);
    }

    /// <summary>Observe entry to the original stop reached by a native budget-end branch. This token is
    /// provisional: synchronous OnDespawn must not cancel it, but nothing is published until StopEvent returns.</summary>
    internal WaveStopObservation? BeforeWaveStop(SurvivalWave? wave, bool includeCancellationScope = false)
    {
        CheckThread();
        if (!CanPublish) return null;
        var state = TrackedWave(wave);
        if (state == null) return null;
        var identity = RegisteredIdentity(wave!.EventID);
        if (identity == null || identity.Value.Instance != state.Whole.Instance) return null;
        // Capture while the native instance still exists. StopEvent may synchronously despawn/recycle it.
        state.BudgetTotal = wave.m_settings?.m_populationPointsTotal ?? -1;
        state.BudgetSpent = wave.m_populationPointsSpentTotal;
        long stopCheck = ++state.StopChecks;
        for (int index = !state.Exhausted && !state.Whole.Cancelled && !identity.Value.Cancelled
            ? _endChecks.Count - 1 : -1; index >= 0; index--)
        {
            var observed = _endChecks[index];
            if (observed.Epoch == _waveEpoch && observed.Wave == state.Pointer && observed.EventId == wave.EventID
                && observed.Instance == state.Whole.Instance)
            {
                if (!observed.Exhausted) break;
                state.PendingEndStops++;
                state.StopReason = "native-budget-end";
                if (DiagnosticSample(stopCheck)) TraceWaveStop(state, "stop-enter");
                var ending = new WaveStopObservation(this, state.Pointer, wave.EventID, state.Whole.Instance);
                _stops.Add(ending); return ending;
            }
        }
        if (includeCancellationScope)
        {
            // An ordinary stop has no exhaustion candidate, but still owns its synchronous teardown. Map
            // cancels its registry before StopEvent, and an already spent wave can also be stopped: both need
            // this identity-only scope to keep stale teardown from cancelling a nested replacement run.
            var stopping = new WaveStopObservation(this, state.Pointer, wave.EventID, state.Whole.Instance, false);
            state.StopReason = identity.Value.Cancelled ? "registry-cancelled" : "native-stop-without-budget-end";
            if (DiagnosticSample(stopCheck)) TraceWaveStop(state, "stop-enter");
            _stops.Add(stopping); return stopping;
        }
        return null;
    }

    internal void AfterWaveStop(WaveStopObservation? observation, bool succeeded)
    {
        CheckThread();
        if (observation == null || !observation.TryConsume(this)) return;
        _stops.Remove(observation);
        EnsureWaveEpoch();
        if (observation.Epoch != _waveEpoch || _waves?[observation.EventId] is not { } state
            || state.Pointer != observation.Wave || state.Whole.Instance != observation.Instance) return;
        if (DiagnosticSample(state.StopChecks)) TraceWaveStop(state, succeeded ? "stop-return" : "stop-failed");
        if (!observation.Exhausts) return;
        state.PendingEndStops--;
        var identity = RegisteredIdentity(observation.EventId);
        if (identity == null || identity.Value.Instance != state.Whole.Instance || identity.Value.Cancelled)
        { state.Whole.Cancelled = true; return; }
        if (succeeded)
        {
            if (state.Exhausted || state.Whole.Cancelled) return;
            state.Exhausted = true;
            if (CanPublish) { FlushPendingBatches(); PublishExhausted(state.Whole); }
        }
        else if (state.Despawned && state.PendingEndStops == 0 && !state.Exhausted)
        {
            state.Whole.Cancelled = true;
            _mapWaves?.Cancel(_waveEpoch, observation.EventId);
        }
    }

    private bool MembersExhausted(LogicalWave whole)
    {
        var identity = RegisteredIdentity(whole);
        if (identity == null || identity.Value.Instance != whole.Instance || identity.Value.Cancelled)
        { whole.Cancelled = true; return false; }
        if (identity.Value.Pending != 0) return false;
        foreach (var state in whole.Events) if (!state.Exhausted) return false;
        return true;
    }

    private void PublishExhausted(LogicalWave whole)
    {
        if (whole.Exhausted || whole.Cancelled || !MembersExhausted(whole)) return;
        whole.Exhausted = true;
        if (!CanPublishWave(whole)) return;
        var fact = BeginWave(ExhaustedGate);
        fact?.Values.Integer(Blocks.TriggerObjectiveWaveExhausted.OutCount, whole.Groups);
        PublishWave(ExhaustedGate, whole, fact);
    }

    /// <summary>The Mastermind finished its own group maintenance: the post-maintenance group list is the native
    /// answer to what is still alive. Exhaustion and an empty live group set together mean cleared, including
    /// an exhausted zero-spawn wave. A temporarily empty wave that still has spawn budget is not cleared.</summary>
    internal void AfterGroupMaintenance()
    {
        CheckThread();
        _maintenancePasses++;
        if (_waveCount == 0 || !CanPublish) return;
        EnsureWaveEpoch();
        FlushPendingBatches();
        var mastermind = Mastermind.Current;
        var active = mastermind?.m_activeGroups;
        if (active == null)
        {
            NoteClearUnreadable(mastermind == null ? "mastermind-absent" : "active-groups-absent");
            return;
        }
        // PERF(E4): the live-group set is only asked of a wave that is exhausted, drained of members and not yet
        // cleared; it is built on the first such wave, not on every maintenance tick of a running one.
        var alive = _alive;
        bool aliveBuilt = false;
        for (int logicalIndex = _logical.Count - 1; logicalIndex >= 0; logicalIndex--)
        {
            var state = _logical[logicalIndex];
            PublishExhausted(state);
            if (!state.Cancelled)
            {
                if (state.Cleared || !state.Exhausted) continue;
                if (!MembersExhausted(state))
                {
                    NoteClearBlock(state, "members-pending", alive: -1, active.Count);
                    continue;
                }
                if (!aliveBuilt)
                {
                    alive.Clear();
                    int count = active.Count;
                    for (int index = 0; index < count; index++)
                    {
                        var group = active[index];
                        if (group != null) alive.Add(group.Pointer);
                    }
                    aliveBuilt = true;
                }
                bool anyAlive = false;
                string block = "none";
                foreach (var pair in state.LiveGroups)
                    if (alive.Contains(pair.Key) && OwnsGroup(pair.Value, pair.Value.Group)) { anyAlive = true; block = "group-in-mastermind"; break; }
                if (!anyAlive)
                    foreach (var pending in _pendingBatches)
                        if (pending.State.Whole == state) { anyAlive = true; block = "batch-pending"; break; }
                if (anyAlive) { NoteClearBlock(state, block, alive.Count, active.Count); continue; }
                state.Cleared = true;
                if (CanPublishWave(state)) PublishWave(ClearedGate, state, BeginWave(ClearedGate));
            }
            foreach (var member in state.Events)
            {
                if (_waves?[member.EventId] == member) _waves![member.EventId] = null;
                _waveCount--;
            }
            foreach (var pair in state.LiveGroups)
                if (_groups.TryGetValue(pair.Key, out var observed) && observed == pair.Value) _groups.Remove(pair.Key);
            TraceWave(state, state.Cleared ? "cleared" : "cancelled", state.EndChecks);

            state.DiagnosticsEnded = true;
            _logical.RemoveAt(logicalIndex);
        }
    }

    /// <summary>A maintenance pass that could not read the live group list: every exhausted, uncleared wave names why.</summary>
    private void NoteClearUnreadable(string held)
    {
        foreach (var whole in _logical)
            if (whole.Exhausted && !whole.Cleared && !whole.Cancelled) NoteClearBlock(whole, held, alive: -1, active: -1);
    }

    /// <summary>An exhausted wave that a group maintenance pass did not clear: the reason is kept on the wave (printed with
    /// its world-end line) and written at the 1st, 10th, 100th... pass, so a wave that never clears names what held it.</summary>
    private void NoteClearBlock(LogicalWave whole, string held, int alive, int active)
    {
        whole.ClearHeld = held; whole.ClearAlive = alive; whole.ClearActive = active;
        whole.ClearLive = whole.LiveGroups.Count; whole.ClearPending = _pendingBatches.Count;
        if (DiagnosticSample(++whole.ClearChecks)) TraceClearBlock(whole, "clear-blocked");
    }

    /// <summary>The wave is being torn down. A teardown is not a clear — level cleanup and cancellation take the
    /// same path — so this publishes nothing. An exhausted wave keeps its groups until their maintenance read.</summary>
    internal void BeforeWaveDespawn(SurvivalWave? wave)
    {
        CheckThread();
        if (!CanPublish || wave == null || _waveCount == 0) return;
        EnsureWaveEpoch();
        if (_waves?[wave.EventID] is { } state && state.Pointer == wave.Pointer)
        {
            // A teardown still inside an old native stop cannot cancel a new run occupying that slot.
            for (int index = _stops.Count - 1; index >= 0; index--)
                if (_stops[index] is { } check && check.Epoch == _waveEpoch && check.Wave == state.Pointer)
                { if (check.EventId != state.EventId || check.Instance != state.Whole.Instance) return; break; }
            // Map can rebind before the replacement's Enemy OnSpawn postfix updates this index. Never write
            // cancellation through a reusable EventID unless it still names this captured logical run.
            var identity = RegisteredIdentity(state.EventId);
            if (identity == null || identity.Value.Instance != state.Whole.Instance) return;
            if (_batch?.State == state) _batch = null;
            if (!state.Despawned) TraceWave(state.Whole, "despawn", 1, state);
            state.Despawned = true;
            if (state.Exhausted) return;
            if (state.PendingEndStops != 0) return;
            state.Whole.Cancelled = true;
            if (state.StopReason == "none") state.StopReason = "despawn-without-budget-end";
            _mapWaves?.Cancel(_kernel.WorldEpoch, wave.EventID);
        }
    }

    /// <summary>The positions of the four wave gates, in the order of <see cref="EnemyWaveContract.Bindings"/>.</summary>
    private const int StartedGate = 0, SpawnedGate = 1, ExhaustedGate = 2, ClearedGate = 3;

    /// <summary>Whether a wave fact may be published for this wave at all: the wave is not cancelled and its
    /// registered identity is still the one Map holds.</summary>
    private bool CanPublishWave(LogicalWave whole)
    {
        if (!CanPublish || whole.Cancelled) return false;
        var identity = RegisteredIdentity(whole);
        return identity != null && !identity.Value.Cancelled;
    }

    /// <summary>Begins the fact of one wave gate when a plan subscribes to it, to be written by output position and
    /// handed to <see cref="PublishWave"/>; null when nothing consumes the gate.</summary>
    private EventWriter? BeginWave(int gate)
        => _gates[gate].HasSubscribers ? _gates[gate].Begin(_registration) : null;

    /// <summary>Publishes one wave fact, when a plan subscribes to it. The frame's scope is the stable registered
    /// resource address; the rows declare no `wave` handle because these observations mint no provider handle. The
    /// stage report and the trace happen whether or not a plan consumes the gate.</summary>
    private void PublishWave(int gate, LogicalWave whole, EventWriter? fact)
    {
        // Runtime owns causal propagation and dispatch, and records a refusal itself (`event.rejected`); a rejection
        // never reopens this native observation.
        var factId = "gtfo.enemy.wave:" + _kernel.WorldEpoch + ":" + (++_eventSequence);
        fact?.Raise(factId, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), whole.Address);
        TraceWave(whole, "fact-submitted", 1, factId: factId, binding: _gates[gate].BindingId);
        if (gate == ExhaustedGate) _mapWaves?.ReportStage(_kernel.WorldEpoch, whole.Address, 2);
        else if (gate == ClearedGate) _mapWaves?.ReportStage(_kernel.WorldEpoch, whole.Address, 3);
    }

    private static bool DiagnosticSample(long count)
    {
        if (count < 1) return false;
        while (count % 10 == 0) count /= 10;
        return count == 1;
    }

    internal void EndWorldDiagnostics()
    {
        foreach (var whole in _logical)
        {
            if (whole.DiagnosticsEnded) continue;
            TraceWave(whole, "world-end", whole.EndChecks);
            if (whole.Exhausted && !whole.Cleared && !whole.Cancelled)
            {
                // No clear pass ever reached this wave: with zero maintenance passes the native hook never ran.
                TraceClearBlock(whole, "world-end-uncleared");
            }
            whole.DiagnosticsEnded = true;
        }
    }

    private void TraceWaveStop(WaveState state, string phase)
    {
        if (ForgeEngine.Plugin.ConfiguredMode != ForgeEngine.RuntimeMode.Authoring || !_log.Logs(RuntimeLogLevel.Info)) return;
        _log.Write(RuntimeLogLevel.Info, "enemy.wave-lifecycle", new RuntimeLogFacts
        {
            Values = new[]
            {
                RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Quote("address", state.Whole.Address),
                RuntimeLogValue.Of("instance", state.Whole.Instance), RuntimeLogValue.Of("observedWorld", _waveEpoch),
                RuntimeLogValue.Of("eventId", state.EventId), RuntimeLogValue.Of("nativePointer", state.Pointer.ToString("x")),
                RuntimeLogValue.Of("count", state.StopChecks), RuntimeLogValue.Of("stopReason", state.StopReason),
                RuntimeLogValue.Of("budgetTotal", state.BudgetTotal), RuntimeLogValue.Of("budgetSpent", state.BudgetSpent),
                RuntimeLogValue.Of("exhausted", state.Exhausted), RuntimeLogValue.Of("cancelled", state.Whole.Cancelled),
                RuntimeLogValue.Of("despawned", state.Despawned)
            }
        });
    }

    /// <summary>Why an exhausted wave is not cleared: its own row, so the wave row stays one fact.</summary>
    private void TraceClearBlock(LogicalWave whole, string phase)
    {
        if (ForgeEngine.Plugin.ConfiguredMode != ForgeEngine.RuntimeMode.Authoring || !_log.Logs(RuntimeLogLevel.Info)) return;
        // No clear pass ever reached this wave: with zero maintenance passes the native hook never ran.
        string block = whole.ClearChecks == 0 ? "no-clear-pass/maintenancePasses=" + _maintenancePasses
            : whole.ClearHeld + "/liveGroups=" + whole.ClearLive + "/mastermindAlive=" + whole.ClearAlive
                + "/activeGroups=" + whole.ClearActive + "/pendingBatches=" + whole.ClearPending;
        _log.Write(RuntimeLogLevel.Info, "enemy.wave-lifecycle", new RuntimeLogFacts
        {
            Values = new[]
            {
                RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Quote("address", whole.Address),
                RuntimeLogValue.Of("instance", whole.Instance), RuntimeLogValue.Of("observedWorld", _waveEpoch),
                RuntimeLogValue.Of("clearChecks", whole.ClearChecks), RuntimeLogValue.Quote("clearBlock", block),
                RuntimeLogValue.Of("exhausted", whole.Exhausted), RuntimeLogValue.Of("cancelled", whole.Cancelled),
                RuntimeLogValue.Of("cleared", whole.Cleared), RuntimeLogValue.Of("groups", whole.Groups),
                RuntimeLogValue.Of("pending", RegisteredIdentity(whole)?.Pending ?? -1)
            }
        });
    }
    private void TraceWave(LogicalWave whole, string phase, long count, WaveState? state = null,
        string? factId = null, string? binding = null)
    {
        if (ForgeEngine.Plugin.ConfiguredMode != ForgeEngine.RuntimeMode.Authoring || !_log.Logs(RuntimeLogLevel.Info)) return;
        state ??= whole.Events.Count == 0 ? null : whole.Events[0];
        _log.Write(RuntimeLogLevel.Info, "enemy.wave-lifecycle", new RuntimeLogFacts
        {
            EventId = factId,
            Binding = binding,
            Values = new[]
            {
                RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Quote("address", whole.Address),
                RuntimeLogValue.Of("instance", whole.Instance), RuntimeLogValue.Of("observedWorld", _waveEpoch),
                RuntimeLogValue.Of("eventId", state?.EventId ?? 0),
                RuntimeLogValue.Of("nativePointer", state?.Pointer.ToString("x") ?? "none"),
                RuntimeLogValue.Of("count", count), RuntimeLogValue.Of("groups", whole.Groups),
                RuntimeLogValue.Of("members", whole.MembersObserved), RuntimeLogValue.Of("endChecks", whole.EndChecks),
                RuntimeLogValue.Of("pending", RegisteredIdentity(whole)?.Pending ?? -1),
                RuntimeLogValue.Of("exhausted", whole.Exhausted), RuntimeLogValue.Of("cancelled", whole.Cancelled),
                RuntimeLogValue.Of("cleared", whole.Cleared),
                RuntimeLogValue.Of("budgetTotal", state?.BudgetTotal ?? -1), RuntimeLogValue.Of("budgetSpent", state?.BudgetSpent ?? -1)
            }
        });
    }
}
