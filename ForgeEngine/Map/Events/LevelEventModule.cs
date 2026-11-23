using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The publication half of the level-event rows: what one native report means, the identity of the fact
/// it produces, and the transition that makes it new. Everything that is a decision about publishing lives here;
/// the game-bound half only reports what it read and never decides whether that is news.
///
/// Two of the six rows repeat within one world — a reactor objective advances its chain more than once, and a
/// player walks into zone after zone — so a fact's identity is built from the state the report carries as well as
/// its subject: the same reading twice is one event, and a different reading is its own. The remaining four are
/// one transition per world, and the row says so by publishing on the transition alone.
///
/// The module is game-independent on purpose. It takes the level reference as the string its provider already
/// resolved, the status and chain as the numbers the game's own enum spells, and the three actions as delegates
/// the game-bound half supplies; that is what lets a focused test assert the published ports, the repeat and
/// absence semantics and the world-epoch cleanup without a copy of the game.</summary>
internal sealed class LevelEventModule : IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    /// <summary>Each fact's last published state and the transition number that makes its event id. A fact that
    /// repeated the same state has no new transition, which is what keeps a per-frame zone reading from flooding
    /// the kernel.</summary>
    private readonly FactTransitions _states = new();
    private readonly OnceReporter _once;
    private long _roundReports;
    private readonly Row _roundCompleted, _timedRoundStarted, _timedRoundFailed;
    private readonly Row _expeditionStarted, _reactorWave, _hsuSampled, _objective, _checkpointRestored, _zoneEntered,
        _portalWarped;
    private RuntimeLifecycleSubscription? _lifecycle;
    private bool _disposed;

    /// <summary>One published row: its binding, the fact kind its event ids and state memory are keyed by, and the
    /// row's subscription gate, resolved once at attach. The kernel refreshes the gate in place wherever the
    /// subscription table changes, so a hook reads one field per call. A registration that does not declare the
    /// binding has no gate and nothing is published for it (a binding withdrawn with its feature is not declared).</summary>
    private sealed class Row
    {
        internal Row(IReadOnlyDictionary<string, RuntimeSubscriptionGate> gates, string fact, string capability)
        {
            Fact = fact;
            Binding = LevelEventContract.Binding(capability);
            Gate = gates.TryGetValue(Binding, out var gate) ? gate : null;
        }

        internal readonly string Binding;
        internal readonly string Fact;
        internal readonly RuntimeSubscriptionGate? Gate;
        internal bool Observed => Gate is { HasSubscribers: true };
    }

    public LevelEventModule(IForgeEngine kernel, RuntimeModuleHandle registration, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        var gates = registration.SubscriptionGates();
        _expeditionStarted = new Row(gates, LevelEventContract.ExpeditionStartedFact, Blocks.TriggerSessionExpeditionStarted.Id);
        _reactorWave = new Row(gates, LevelEventContract.ReactorWaveFact, Blocks.TriggerObjectiveReactorWave.Id);
        _hsuSampled = new Row(gates, LevelEventContract.HsuSampledFact, Blocks.TriggerObjectiveHsuSampled.Id);
        _roundCompleted = new Row(gates, ObjectiveRoundContract.RoundCompletedFact, Blocks.TriggerObjectiveRoundCompleted.Id);
        _timedRoundStarted = new Row(gates, ObjectiveRoundContract.TimedRoundStartedFact, Blocks.TriggerObjectiveTimedRoundStarted.Id);
        _timedRoundFailed = new Row(gates, ObjectiveRoundContract.TimedRoundFailedFact, Blocks.TriggerObjectiveTimedRoundFailed.Id);
        _objective = new Row(gates, ObjectiveTriggerContract.StateFact, Blocks.TriggerObjectiveStateChanged.Id);
        _checkpointRestored = new Row(gates, LevelEventContract.CheckpointRestoredFact, Blocks.TriggerSessionCheckpointRestored.Id);
        _zoneEntered = new Row(gates, LevelEventContract.ZoneEnteredFact, Blocks.TriggerMapZoneEntered.Id);
        _portalWarped = new Row(gates, LevelEventContract.PortalWarpedFact, Blocks.TriggerMapPortalWarped.Id);
        _lifecycle = _registration.ObserveLifecycle(OnLifecycle);
    }

    public bool IsRegistered => !_disposed && _registration.IsRegistered;

    /// <summary>Drops this world's own tables. The kernel's world epoch is what invalidates an earlier id; this
    /// only releases the memory of a level that no longer exists, so the next world publishes its own facts rather
    /// than answering from the one that is gone.</summary>
    public void ClearWorld()
    {
        CheckThread();
        _states.Clear();
    }

    // ---- triggers -----------------------------------------------------------------------------------

    /// <summary>The elevator has landed and the expedition is running. One transition per world: the game's own
    /// start entry is called once for the level, and a second call would be the same expedition starting twice.
    /// `levelReference` is the identity the `level` attachment matcher already compares, read by the game-bound
    /// half; a report that could not read one publishes nothing rather than a reference nothing can match.</summary>
    public void ExpeditionStarted(string? levelReference)
    {
        CheckThread();
        if (string.IsNullOrEmpty(levelReference)) return;
        if (!Admit(_expeditionStarted, "session", null, new FactReading("started"), out long transition)) return;
        var raised = Begin(_expeditionStarted);
        raised.Values.Resource(Blocks.TriggerSessionExpeditionStarted.OutMap, LevelEventContract.MapReference(levelReference!));
        Emit(_expeditionStarted, "session", transition, raised);
    }

    /// <summary>A reactor objective advanced to the next entry of its own event chain. The report is the chain
    /// index the objective machine already keeps, so a wave is a chain step and the fact carries both numbers: a
    /// plan that wants "the third wave" reads `wave`, and one that wants "every wave" subscribes to the row.</summary>
    public void ReactorWaveAdvanced(string layer, int wave, int previous)
    {
        CheckThread();
        if (string.IsNullOrEmpty(layer)) return;
        if (wave <= previous) return;
        if (!Admit(_reactorWave, layer, null, new FactReading(null, wave), out long transition)) return;
        var raised = Begin(_reactorWave);
        raised.Values.Integer(Blocks.TriggerObjectiveReactorWave.OutWave, wave);
        raised.Values.Integer(Blocks.TriggerObjectiveReactorWave.OutPrevious, previous);
        Emit(_reactorWave, layer, transition, raised);
    }

    /// <summary>An uplink or timed-terminal round finished. A round can repeat after a recall, so every report is its own transition.</summary>
    public void RoundCompleted(string layer, int round)
    {
        CheckThread();
        if (!Admit(_roundCompleted, layer, null, new FactReading(null, ++_roundReports), out long transition)) return;
        var raised = Begin(_roundCompleted);
        raised.Values.Resource(Blocks.TriggerObjectiveRoundCompleted.OutObjective, LevelEventContract.ObjectiveReference(layer));
        raised.Values.Integer(Blocks.TriggerObjectiveRoundCompleted.OutRound, round);
        Emit(_roundCompleted, layer, transition, raised);
    }

    /// <summary>A timed-terminal round began and its receiver is chosen; zone is the receiver's local zone index.</summary>
    public void TimedRoundStarted(string layer, int round, ResourceRef? zone)
    {
        CheckThread();
        if (!Admit(_timedRoundStarted, layer, null, new FactReading(null, ++_roundReports), out long transition)) return;
        var raised = Begin(_timedRoundStarted);
        raised.Values.Resource(Blocks.TriggerObjectiveTimedRoundStarted.OutObjective, LevelEventContract.ObjectiveReference(layer));
        raised.Values.Integer(Blocks.TriggerObjectiveTimedRoundStarted.OutRound, round);
        if (zone is { } resource) raised.Values.Resource(Blocks.TriggerObjectiveTimedRoundStarted.OutZone, resource);
        Emit(_timedRoundStarted, layer, transition, raised);
    }

    /// <summary>A timed-terminal round ran out of time.</summary>
    public void TimedRoundFailed(string layer, int round)
    {
        CheckThread();
        if (!Admit(_timedRoundFailed, layer, null, new FactReading(null, ++_roundReports), out long transition)) return;
        var raised = Begin(_timedRoundFailed);
        raised.Values.Resource(Blocks.TriggerObjectiveTimedRoundFailed.OutObjective, LevelEventContract.ObjectiveReference(layer));
        raised.Values.Integer(Blocks.TriggerObjectiveTimedRoundFailed.OutRound, round);
        Emit(_timedRoundFailed, layer, transition, raised);
    }

    /// <summary>The HSU objective's own item was taken out. One transition per world: the objective item can be
    /// solved once, and the game's own entry is what the vanilla `ActivateHSU_Events` list is attached to. The
    /// container is reported when the report carries one.</summary>
    public void HsuSampled(string layer, string? containerReference)
    {
        CheckThread();
        if (string.IsNullOrEmpty(layer)) return;
        if (!Admit(_hsuSampled, layer, null, new FactReading("sampled"), out long transition)) return;
        var raised = Begin(_hsuSampled);
        raised.Values.Resource(Blocks.TriggerObjectiveHsuSampled.OutObjective, LevelEventContract.ObjectiveReference(layer));
        // The container is optional: a report that carries none leaves the output unwritten.
        if (!string.IsNullOrEmpty(containerReference))
            raised.Values.Resource(Blocks.TriggerObjectiveHsuSampled.OutContainer, new ResourceRef("item", containerReference!));
        Emit(_hsuSampled, layer, transition, raised);
    }

    /// <summary>One objective layer moved. The transition is the one the objective machine's own state replicator
    /// callback carries: the native half reads the layer's status, chain index and item counters before and after
    /// the change and hands them over, so the fact is the transition itself and not a reading this module compares
    /// against a memory of its own. `change` names which reading moved (`state`, `phase` or `progress`) and is part
    /// of the subject, so the three kinds of change of one layer are three facts with their own transitions. The
    /// status names are the `eWardenObjectiveStatus` members the value row already publishes (`not_discovered` …
    /// `item_solved`), so a plan reading an objective's state and a plan reacting to its change speak one
    /// vocabulary.</summary>
    public void ObjectiveChanged(string layer, string change, ObjectiveReading reading)
    {
        CheckThread();
        if (string.IsNullOrEmpty(layer)) return;
        // The status ports are `objective_status` members; a name outside the set publishes nothing.
        int stateMember = LevelObjectiveValueContract.StatusMemberOf(reading.State),
            previousMember = LevelObjectiveValueContract.StatusMemberOf(reading.Previous);
        if (stateMember < 0 || previousMember < 0) return;
        FactReading? state = change switch
        {
            ObjectiveTriggerContract.StateChange => new FactReading(reading.State),
            ObjectiveTriggerContract.PhaseChange => new FactReading(null, reading.Phase),
            ObjectiveTriggerContract.ProgressChange => new FactReading(null, reading.Count, reading.Total),
            _ => null
        };
        if (state is not { } changed) return;
        // The three kinds of change of one layer are three subjects with their own transitions.
        if (!Admit(_objective, layer, change, changed, out long transition)) return;
        var raised = Begin(_objective);
        var values = raised.Values;
        // `change` is an enum port: its wire form is the member index.
        values.Enum(Blocks.TriggerObjectiveStateChanged.OutChange, ObjectiveTriggerContract.ChangeMember(change));
        values.Enum(Blocks.TriggerObjectiveStateChanged.OutState, stateMember);
        values.Enum(Blocks.TriggerObjectiveStateChanged.OutPrevious, previousMember);
        values.Integer(Blocks.TriggerObjectiveStateChanged.OutPhase, reading.Phase);
        values.Integer(Blocks.TriggerObjectiveStateChanged.OutPreviousPhase, reading.PreviousPhase);
        values.Integer(Blocks.TriggerObjectiveStateChanged.OutCount, reading.Count);
        values.Integer(Blocks.TriggerObjectiveStateChanged.OutTotal, reading.Total);
        // The scope is the layer's own objective reference, so a trigger subscribed to one layer hears only it.
        Emit(_objective, layer + "/" + change, transition, raised, LevelEventContract.ObjectiveReference(layer).ResourceId);
    }

    /// <summary>The checkpoint recall finished. One transition per restore: the game reports the recall's own
    /// completion, and the row is the "extra handling after a load" hook the checklist asks for — the framework
    /// restores the variables itself.</summary>
    public void CheckpointRestored()
    {
        if (!Admit(_checkpointRestored, "checkpoint", null, new FactReading("restored"), out long transition)) return;
        Emit(_checkpointRestored, "checkpoint", transition, Begin(_checkpointRestored));
    }

    /// <summary>One player entered a zone. The subject is the player and the state is the zone, so walking into
    /// three zones is three facts and the same zone twice is one. The zone is the `zone` resource every zone reader
    /// answers with, and it is also the event's scope, so a trigger whose `resource` names one zone hears only
    /// entries into that zone while an unnamed one hears them all.</summary>
    public void ZoneEntered(EntityReference player, ResourceRef zone)
    {
        CheckThread();
        if (string.IsNullOrEmpty(zone.ResourceId)) return;
        if (player.WorldEpoch != _kernel.WorldEpoch) return;
        if (!Admit(_zoneEntered, player.Id, null, new FactReading(zone.ResourceId), out long transition)) return;
        var raised = Begin(_zoneEntered);
        raised.Values.Entity(Blocks.TriggerMapZoneEntered.OutPlayer, player);
        raised.Values.Resource(Blocks.TriggerMapZoneEntered.OutZone, zone);
        Emit(_zoneEntered, player.Id, transition, raised, zone.ResourceId);
    }

    /// <summary>One confirmed native portal event execution. Repeated journeys through the same door are
    /// separate occurrences; coroutine yields and unrelated warp notifications never call this entry.</summary>
    public void PortalWarped(string portalId, int dimension, int previous)
    {
        CheckThread();
        if (string.IsNullOrEmpty(portalId)) return;
        _states.Settle(new FactSubject(_portalWarped.Fact, portalId), new FactReading("waiting"));
        if (!Admit(_portalWarped, portalId, null, new FactReading(null, dimension), out long transition)) return;
        var raised = Begin(_portalWarped);
        raised.Values.Entity(Blocks.TriggerMapPortalWarped.OutPortal, new EntityReference(portalId, _kernel.WorldEpoch, 1));
        raised.Values.Integer(Blocks.TriggerMapPortalWarped.OutDimension, dimension);
        raised.Values.Integer(Blocks.TriggerMapPortalWarped.OutPrevious, previous);
        Emit(_portalWarped, portalId, transition, raised);
    }

    // ---- actions ------------------------------------------------------------------------------------

    /// <summary>The countdown command: `add` moves the objective's own countdown by the ticks the input carries,
    /// converted to the native seconds field at the rate the running world reported (contract §3.2 时间单位),
    /// and `reset` puts it back to the value the objective's data block set. Each operation is one native
    /// entry (`AddToTimer` and `ResetTimer`), which is why they are one row with one structural parameter rather
    /// than two rows the author has to choose between. Both write through the game's own level-event executor, so
    /// the change replicates the way a vanilla mount point's does.
    ///
    /// The row's recipient is the `objectives` resource reference the plan wired — the level's own `objective`
    /// resource kind, whose id is the `layer:<name>` reference this package publishes an objective under. It is
    /// the row's one authoritative identity: the layer the native event names is read from that reference and the
    /// write is addressed to it, so the row's `target` is the objective the command really moved rather than a
    /// second, independent choice. A reference that is not a layer reference of this package is refused by name
    /// instead of being written against a layer the row would then misreport.
    ///
    /// The layer travels to the native half as the index it is in <see cref="LevelEventContract.Layers"/>, which
    /// is the member order the game's own `LG_LayerType` declares, so the game-bound half casts rather than
    /// looking the name up again.</summary>
    internal CommandResult ExecuteTimer(CommandContext context, Action<int, float> addTimer, Action<int> resetTimer)
    {
        if (!context.IsHost) return Refused(ResultCode.AuthorityOrPhase);
        ArgumentNullException.ThrowIfNull(addTimer);
        ArgumentNullException.ThrowIfNull(resetTimer);
        // The row's `target` must be the objective the native event really moves, so a reference that names no
        // layer of this package is refused by name rather than served by moving a default layer.
        if (RecipientLayer(context) is not { } layer) return Refused(ResultCode.ObjectiveTargetUnsupported);
        string operation = context.Text(LevelEventContract.TimerOperation) ?? "";
        if (Array.IndexOf(LevelEventContract.TimerOperations, operation) < 0) return Refused(ResultCode.ObjectiveTimerOperationUnknown);
        float? seconds = context.Number(LevelEventContract.TimerSeconds) is { } ticks ? (float)ticks : null;
        if (operation == "add" && (seconds == null || !float.IsFinite(seconds.Value) || seconds.Value <= 0))
            return Refused(ResultCode.ObjectiveTimerSecondsInvalid);
        try
        {
            // The port declares ticks and the native event's `Duration` is seconds, so the write is converted at
            // the rate the running world reported (contract §3.2 时间单位) before it reaches the game, never handed
            // over as the tick count it was authored in.
            if (operation == "add") addTimer(layer, (float)(seconds!.Value / context.TicksPerSecond));
            else resetTimer(layer);
        }
        catch (Exception error)
        {
            ReportOnce(ResultCode.NativeCommitException, "objective timer action threw: " + error.GetType().Name);
            return Failure(ResultCode.NativeCommitException);
        }
        return IssuedTimer(context, ResultCode.ObjectiveTimerIssued);
    }

    /// <summary>The layer the objective reference a request carried designates, as its index in
    /// <see cref="LevelEventContract.Layers"/>, or null when the request carried no reference or one that is not
    /// a layer reference of this package. The id is read through <see cref="ObjectiveLayerReference"/>, the one
    /// grammar this package spells an objective reference with, and matched against this package's own layer
    /// vocabulary, so the write and the row cannot name two different layers.</summary>
    private static int? RecipientLayer(CommandContext context)
    {
        if (!context.TryResource(LevelEventContract.TimerObjectives, out var objective)) return null;
        int index = ObjectiveLayerReference.LayerIndex(objective, LevelEventContract.Layers);
        return index < 0 ? null : index;
    }

    /// <summary>The dimension command: flash accepts the complete named team and clear validates the named actor
    /// lives, then empties the explicitly named dimension through `move`. The mode selects the native event: `clear`
    /// names `eWardenObjectiveEventType.ClearDimension` itself, so the request carries no clear flag. Moving players
    /// into a dimension is the teleport row's (`forge.action.player.teleport` with `dimension`), which runs on each
    /// moved player's own machine.
    ///
    /// The follow policy the deleted `policy` parameter used to carry is not replaced: the deployed sentries a
    /// warp takes along are an `IWarpableObject` the game's own notify already moves, and the items it does not
    /// move have no carry this row could promise, so the ruling deleted the parameter rather than keeping a choice
    /// the game cannot honor.</summary>
    internal CommandResult ExecuteDimension(CommandContext context, Action<string, int> move,
        Func<string, string?> validateTargets)
    {
        if (!context.IsHost) return Refused(ResultCode.AuthorityOrPhase);
        ArgumentNullException.ThrowIfNull(move);
        string mode = context.Text(LevelEventContract.DimensionMode) ?? "";
        if (Array.IndexOf(LevelEventContract.DimensionModes, mode) < 0) return Refused(ResultCode.DimensionModeUnknown);
        // (An empty player set never reaches here: the kernel answers it.)
        double? dimension = context.Number(LevelEventContract.DimensionIndex);
        // Clear names a dimension too; omitting it must never silently clear Reality.
        if (dimension == null || !double.IsFinite(dimension.Value) || dimension.Value != Math.Truncate(dimension.Value) || dimension.Value < 0 || dimension.Value > 20)
            return Refused(ResultCode.DimensionIndexInvalid);
        ArgumentNullException.ThrowIfNull(validateTargets);
        if (validateTargets(mode) is { } refusal) return Refused(refusal);
        int index = (int)dimension.Value;
        try
        {
            move(mode, index);
        }
        catch (Exception error)
        {
            ReportOnce(ResultCode.NativeCommitException, "dimension action threw: " + error.GetType().Name);
            return Failure(ResultCode.NativeCommitException);
        }
        var code = mode == "clear" ? ResultCode.DimensionClearIssued : ResultCode.DimensionFlashIssued;
        // One row per named player, each counting itself; the command's own code is the issued one.
        var rows = context.Rows;
        for (int at = 0; at < rows.Count; at++)
            rows.Begin(at).Integer(Blocks.ActionPlayerDimension.RowTargetCount, 1).End(Status.Succeeded, Commit.Confirmed, code);
        return Outcome.Succeeded(code);
    }

    /// <summary>The expedition-end command: `instant_win` ends the expedition now, `win_on_death` makes the next
    /// wipe the win the objective's own completion check looks for. Both are the game's own members
    /// (`ForceInstantWin` and `WinOnDeath`), which is why they are one row.</summary>
    internal CommandResult ExecuteExpeditionEnd(CommandContext context, Action<string> end)
    {
        if (!context.IsHost) return Refused(ResultCode.AuthorityOrPhase);
        ArgumentNullException.ThrowIfNull(end);
        string ending = context.Text(LevelEventContract.ExpeditionEnding) ?? "";
        if (Array.IndexOf(LevelEventContract.ExpeditionOutcomes, ending) < 0) return Refused(ResultCode.ExpeditionEndingUnknown);
        try
        {
            end(ending);
        }
        catch (Exception error)
        {
            ReportOnce(ResultCode.NativeCommitException, "expedition end action threw: " + error.GetType().Name);
            return Failure(ResultCode.NativeCommitException);
        }
        var code = ending switch { "fail" => ResultCode.ExpeditionFailIssued, "win_on_death" => ResultCode.ExpeditionWinOnDeathIssued, _ => ResultCode.ExpeditionInstantWinIssued };
        // The expedition ends for everyone in it whatever the port holds. The participants port is optional and a
        // single reference: a wired one is the row's recipient, and a request without it has no recipient and so
        // no row.
        var rows = context.Rows;
        if (rows.Count > 0) rows.Begin(0).Integer(Blocks.ActionMapExpeditionEnd.RowTargetCount, 1).End(Status.Succeeded, Commit.Confirmed, code);
        return Outcome.Succeeded(code);
    }

    // ---- publication --------------------------------------------------------------------------------

    /// <summary>Whether one reading of one subject is a fact to build. A repeated reading of one state has no new
    /// transition and therefore no new event id. When nothing listens on the row's binding the kernel would answer
    /// `no-consumer`, so the caller builds no event value at all; the reading is still remembered, because a state
    /// observed while nobody listened is a state this module has already reported. Nothing on this path allocates
    /// once the subject has been seen. The host guard is the caller's: every one of these rows is a report a
    /// native callback makes, and the callback is the side that knows whether it is the master.</summary>
    private bool Admit(Row row, string subject, string? part, in FactReading reading, out long transition)
    {
        transition = 0;
        if (_disposed || !_registration.IsRegistered) return false;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (!_states.Advance(new FactSubject(row.Fact, subject, part), reading, out transition)) return false;
        return row.Observed;
    }

    /// <summary>Begins one admitted fact's event on the row's own gate; its values are written by the trigger's
    /// output positions (the `next` port is position 0).</summary>
    private EventWriter Begin(Row row) => _registration.BeginEvent(row.Gate!);

    /// <summary>Publishes one admitted fact under the transition <see cref="Admit"/> gave it.</summary>
    private void Emit(Row row, string subject, long transition, EventWriter raised, string? scope = null)
    {
        var world = _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);
        raised.Raise(
            EntityKinds.Level + "." + row.Fact + ":" + world + ":" + subject + ":" + transition.ToString(CultureInfo.InvariantCulture),
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), scope ?? "gtfo.world:" + world);
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
    }

    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        // A host migration keeps the level: its once-per-world facts do not fire again.
        if (value.Kind == RuntimeLifecycleKind.WorldChanged) { if (!value.IsMigration) ClearWorld(); }
        else if (value.Kind == RuntimeLifecycleKind.CheckpointRestored)
            // Re-arm the existing readback row without resetting its event sequence. Expedition start remains
            // a once-per-world fact; authors connect checkpoint completion to the same presentation chain.
            _states.Settle(new FactSubject(_checkpointRestored.Fact, "checkpoint"), new FactReading("restoring"));
    }

    /// <summary>One refusal in the shape every handler of this family reports: nothing was written and the code
    /// names the one check that refused it. Exposed because the game-bound half refuses a request the same way
    /// when its own registration is not there.</summary>
    internal static CommandResult Refused(string code) => Outcome.Rejected(code);

    private static CommandResult Failure(string code) => Outcome.FailedUnknown(code);

    /// <summary>The countdown row: the one row about the objective reference the request named on its recipient
    /// port, which declares no column beyond the four fixed ones. A request that carried no reference has no
    /// recipient, so no row is written for it, while the command's own status and code still report the decision.</summary>
    private static CommandResult IssuedTimer(CommandContext context, string code)
    {
        var rows = context.Rows;
        if (rows.Count > 0) rows.Row(0, Status.Succeeded, Commit.Confirmed, code);
        return Outcome.Succeeded(code);
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Level event facts must be published from the thread that owns the session.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _lifecycle = null;
    }
}

/// <summary>One objective layer's reading across one state change: its status name now and before, its chain index
/// now and before, and the state's item counters now.</summary>
internal readonly record struct ObjectiveReading(string State, string Previous, int Phase, int PreviousPhase, int Count,
    int Total);
