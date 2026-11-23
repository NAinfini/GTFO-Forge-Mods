using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The publication half of the level-object facts, game-independent: one entry point per native callback, each
/// taking the values the callback's subject already answered and turning them into one fact through the
/// registration the session owns.
///
/// The game-bound half is the only side that can read a level, so it reads the subject, builds the key and
/// counts a group's members; everything that is a decision about publishing — the identity of one fact, the
/// transition that makes it new, the refusal to publish a reading of nothing, the per-world tables — lives
/// here. That split is what keeps this file compilable against the framework alone, which is how the focused
/// test project exercises every branch of it.
///
/// Every subject lives in `gtfo.level_object`. Captured cabinets and dynamic items use their native network
/// key and current lifetime; legacy fixed items and scans retain their existing authored identity. A cabinet's
/// random serial is fact data, never an author address. Authors subscribe to its zone and consume the exact
/// captured entity through the same entity resolver used by later actions.
/// </summary>
internal sealed partial class LevelObjectModule : IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly FactTransitions _states = new();
    private Dictionary<string, RuntimeHandle> _alarmHandles = new(StringComparer.Ordinal);
    private readonly OnceReporter _once;
    private readonly Row _scanProgress, _scanStarted, _scanCompleted, _alarmStopped, _alarmStarted, _containerState, _itemPickup;
    private bool _disposed;

    /// <summary>One published row: its binding, the fact kind its event ids and state memory are keyed by, and the
    /// row's subscription gate, resolved once at attach. The kernel refreshes the gate in place wherever the
    /// subscription table changes, so a hook reads one field per call. A registration that does not declare the
    /// binding has no gate and nothing is published for it (a binding withdrawn with its feature is not declared).</summary>
    private sealed class Row
    {
        internal Row(IReadOnlyDictionary<string, RuntimeSubscriptionGate> gates, string binding, string fact)
        {
            Fact = fact;
            Gate = gates.TryGetValue(binding, out var gate) ? gate : null;
        }

        internal readonly string Fact;
        internal readonly RuntimeSubscriptionGate? Gate;
        internal bool Observed => Gate is { HasSubscribers: true };
    }

    public LevelObjectModule(IForgeEngine kernel, RuntimeModuleHandle registration, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        var gates = registration.SubscriptionGates();
        _scanProgress = new Row(gates, LevelObjectContract.ScanProgressBinding, "scan.progress");
        _scanStarted = new Row(gates, LevelObjectContract.ScanStartedBinding, "scan.started");
        _scanCompleted = new Row(gates, LevelObjectContract.ScanCompletedBinding, "scan.completed");
        _alarmStopped = new Row(gates, LevelObjectContract.AlarmStoppedBinding, "alarm.stopped");
        _alarmStarted = new Row(gates, LevelObjectContract.AlarmStartedBinding, "alarm.started");
        _containerState = new Row(gates, LevelObjectContract.ContainerStateBinding, "container.state");
        _itemPickup = new Row(gates, LevelObjectContract.ItemPickupBinding, "item.pickup");
        _instanceLifecycle = registration.ObserveLifecycle(value =>
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration
                || value.Current.StartupState is RuntimeStartupState.Stopped or RuntimeStartupState.Failed) ClearWorld();
        });
    }

    /// <summary>Whether a plan consumes the alarm start row right now, read before checking the alarm flag.
    /// The native data block identity is still required for the scan facts that share this callback.</summary>
    public bool AlarmStartedObserved => _alarmStarted.Observed;

    /// <summary>Whether a plan consumes the scan progress row right now. The progress callback runs every scan beat,
    /// so the game-bound half reads nothing from the scan until this is true.</summary>
    public bool ScanProgressObserved => _scanProgress.Observed;

    public bool IsRegistered => !_disposed && _registration.IsRegistered;

    /// <summary>Drops this world's own tables. The kernel's world epoch is what invalidates an earlier id; this
    /// only releases the memory of a level that no longer exists, so the next world reads its own level rather
    /// than answering from the one that is gone.</summary>
    public void ClearWorld()
    {
        CheckThread();
        _states.Clear();
        _alarmHandles.Clear();
        ClearInstances();
    }

    // ---- scan ---------------------------------------------------------------------------------------

    /// <summary>One progress reading of one scan, from the instance's own master-side callback. The callback is
    /// the host's own report and its argument is the fraction the native side computed, so the fact reports
    /// that value rather than re-deriving it: the instance's state is written by the body this callback runs
    /// before, and reading it back here would report the state after that write. A reading at or below zero is
    /// the state the callback reports while a scan is only being approached, so it is not published as
    /// progress. The value row reads the same fraction from the instance itself and keeps no copy of this
    /// report.</summary>
    public void ScanProgress(string scanUid, string? resourceId, double progress, IReadOnlyList<EntityReference> participants,
        int point, double pointProgress)
    {
        CheckThread();
        if (!ScanIdentified(scanUid, resourceId)) return;
        if (progress <= 0) return;
        if (!Admit(_scanProgress, scanUid, new FactReading(null, progress, point + pointProgress), out long transition)) return;
        var raised = _registration.BeginEvent(_scanProgress.Gate!);
        var values = raised.Values;
        values.Number(Blocks.TriggerObjectiveScanMembership.OutProgress, progress);
        values.Integer(Blocks.TriggerObjectiveScanMembership.OutPoint, point);
        values.Number(Blocks.TriggerObjectiveScanMembership.OutPointProgress, pointProgress);
        values.Entities(Blocks.TriggerObjectiveScanMembership.OutParticipants, participants);
        values.Integer(Blocks.TriggerObjectiveScanMembership.OutCount, participants.Count);
        Raise(raised, _scanProgress, scanUid, transition, resourceId!);
    }

    /// <summary>One scan whose own replicated state changed. The started and the completed row are the same
    /// instance kind's two readings, which is why one entry point answers both. `active` is the game's own
    /// `IsActive`, `solved` its own `IsSolved`; the game-bound half reads them after the native body returned,
    /// so a fact never claims a state the instance is not in.</summary>
    public void ScanStateChanged(string scanUid, string? resourceId, bool active, bool solved, int? scanIndex = null)
    {
        CheckThread();
        if (!ScanIdentified(scanUid, resourceId)) return;
        var row = solved ? _scanCompleted : active ? _scanStarted : null;
        if (row == null) return;
        // NaN distinguishes an unresolved native step in the existing ledger; the output is JSON null.
        if (!Admit(row, scanUid, new FactReading(solved ? "solved" : "active",
            solved || scanIndex == null ? double.NaN : scanIndex.Value), out long transition)) return;
        // The optional scan port is an effect handle, not a level-object entity. A native core activation
        // creates no Forge effect handle; omit that unavailable value instead of publishing a forged handle.
        var raised = _registration.BeginEvent(row.Gate!);
        if (!solved)
        {
            if (scanIndex is { } index) raised.Values.Integer(Blocks.TriggerObjectiveScanStarted.OutScanIndex, index);
            else raised.Values.Null(Blocks.TriggerObjectiveScanStarted.OutScanIndex);
        }
        Raise(raised, row, scanUid, transition, resourceId!);
    }

    /// <summary>An activation whose captured native lifetime is no longer current is refused before publishing.</summary>
    public void ScanStepRefused(string code)
    {
        CheckThread();
        ReportOnce("scan-step:" + code, "scan step activation refused: " + code);
    }

    /// <summary>One alarm's own stop: the chained puzzle the level built as an alarm reached `Solved`. The
    /// instance is named by the same `chained-puzzle` subject the three scan rows use — an alarm is that instance
    /// kind, addressed by its own `m_puzzleUID` — and the outcome is the one member this transition is,
    /// `succeeded`. One stop per instance per world, the same repeat rule the started and completed rows carry.
    /// Only the game-bound half can tell an alarm from a plain scan: the puzzle's own data block is what says
    /// so, and it calls this only for the instances whose block marks them.</summary>
    public void AlarmStopped(string scanUid, string? resourceId, object native, Func<object, bool> isLive,
        EntityReference? source = null)
    {
        CheckThread();
        if (!ScanIdentified(scanUid, resourceId)) return;
        var handle = AlarmHandle(_alarmStopped, scanUid, native, isLive);
        if (!Admit(_alarmStopped, scanUid, new FactReading("solved"), out long transition)) return;
        var raised = _registration.BeginEvent(_alarmStopped.Gate!);
        var values = raised.Values;
        if (handle is { } alarm) values.Handle(Blocks.TriggerObjectiveAlarmStopped.OutAlarm, alarm);
        values.Entity(Blocks.TriggerObjectiveAlarmStopped.OutSource, source?.WorldEpoch == WorldEpoch ? source : null);
        values.Enum(Blocks.TriggerObjectiveAlarmStopped.OutOutcome, MapObjectOutcomes.Succeeded);
        Raise(raised, _alarmStopped, scanUid, transition, resourceId!);
    }

    /// <summary>One alarm's own start: the chained puzzle the level built as an alarm became `Active`. The instance
    /// keeps its own UID for transition identity; the `puzzle` resource and event scope name its data block.
    /// One start per activation: the native half calls <see cref="AlarmSettled"/> when the instance leaves
    /// `Active`, so an alarm that is reset and started again publishes a new start with a new event id.</summary>
    public void AlarmStarted(string scanUid, string? resourceId, object native, Func<object, bool> isLive,
        EntityReference? source = null)
    {
        CheckThread();
        if (!ScanIdentified(scanUid, resourceId)) return;
        var handle = AlarmHandle(_alarmStarted, scanUid, native, isLive);
        if (!Admit(_alarmStarted, scanUid, new FactReading("active"), out long transition)) return;
        var raised = _registration.BeginEvent(_alarmStarted.Gate!);
        var values = raised.Values;
        if (handle is { } alarm) values.Handle(Blocks.TriggerObjectiveAlarmStarted.OutAlarm, alarm);
        values.Entity(Blocks.TriggerObjectiveAlarmStarted.OutSource, source?.WorldEpoch == WorldEpoch ? source : null);
        values.Resource(Blocks.TriggerObjectiveAlarmStarted.OutPuzzle, new ResourceRef(LevelObjectContract.ChainedPuzzleKind, resourceId!));
        Raise(raised, _alarmStarted, scanUid, transition, resourceId!);
    }

    /// <summary>Records that one alarm is no longer active, without publishing anything: the next activation of the
    /// same instance is then a new transition of the start row instead of a repeat of the last one.</summary>
    public void AlarmSettled(string scanUid)
    {
        CheckThread();
        if (string.IsNullOrEmpty(scanUid)) return;
        _states.Settle(new FactSubject(_alarmStarted.Fact, scanUid), new FactReading("inactive"));
    }

    // ---- containers and level items -----------------------------------------------------------------

    /// <summary>One container state change, from the container's own replicated state. The reading is the
    /// game's own status value, which is what the row's enum carries; a value outside the declared set is not
    /// published at all, so a member this provider does not know is never reported as one it does. The scope is
    /// the same item resource address the trigger selects, including for a plan mounted on the whole level;
    /// the event's WorldEpoch still owns its lifetime.</summary>
    public void ContainerStateChanged(EntityReference container, EntityReference zone, int serial, string state)
    {
        CheckThread();
        if (!IsCurrent(container) || zone.WorldEpoch != WorldEpoch || serial < 0 || string.IsNullOrEmpty(state))
        {
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.InstanceRefused, "Container fact receiver is unavailable.",
                RuntimeLogValue.Of("entity", container.Id), RuntimeLogValue.Of("reason",
                    container.WorldEpoch != WorldEpoch || zone.WorldEpoch != WorldEpoch ? "stale-world" : "container-instance-unavailable"));
            return;
        }
        int stateIndex = Array.IndexOf(LevelObjectContract.ContainerStateNames, state);
        if (stateIndex < 0) return;
        if (!Admit(_containerState, container.Id, new FactReading(state, container.LifeEpoch), out long transition)) return;
        var raised = _registration.BeginEvent(_containerState.Gate!);
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionContainerState.OutContainer, container);
        values.Entity(Blocks.TriggerInteractionContainerState.OutZone, zone);
        values.Integer(Blocks.TriggerInteractionContainerState.OutSerial, serial);
        values.Enum(Blocks.TriggerInteractionContainerState.OutState, stateIndex);
        Raise(raised, _containerState, container.Id, transition, zone.Id);
    }

    /// <summary>One level item changing hands, from the item's own replicated state: `picked-up` when it went
    /// into a player's hands and `placed-in-level` when it came back to the floor. The actor is the player the
    /// state named, resolved by the game-bound half through the player domain's own instance lookup.</summary>
    public void ItemStateChanged(string itemKey, EntityReference item, bool pickedUp, EntityReference? actor)
    {
        CheckThread();
        if (string.IsNullOrEmpty(itemKey) || NativeInstance(item) == null) return;
        if (!Admit(_itemPickup, itemKey, new FactReading(pickedUp ? "picked-up" : "placed-in-level", item.LifeEpoch), out long transition))
            return;
        var raised = _registration.BeginEvent(_itemPickup.Gate!);
        var values = raised.Values;
        values.Entity(Blocks.TriggerMapItemPickup.OutItem, item);
        values.Boolean(Blocks.TriggerMapItemPickup.OutPickedUp, pickedUp);
        if (actor != null) values.Entity(Blocks.TriggerMapItemPickup.OutActor, actor);
        Raise(raised, _itemPickup, itemKey, transition, itemKey);
    }

    // ---- fog repellers ------------------------------------------------------------------------------

    /// <summary>The sphere owned by a fixed heavy repeller, keyed by its placement token and authored zone.
    /// Runtime Unity instance ids are never saved as author addresses.</summary>
    public const string FogRepellerCategory = LevelEndpointAddress.FogRepeller;

    /// <summary>The key one `fog_repeller` reference carries, or null when the reference is not one of this
    /// category. The id's first segment after the namespace is the category, so an id of another category or of
    /// another kind is not this row's subject and is refused by name rather than read as a repeller of the same
    /// key.</summary>
    public static string? FogRepellerKey(EntityReference reference)
    {
        return LevelEndpointAddress.Key(reference, FogRepellerCategory);
    }

    // ---- value rows ---------------------------------------------------------------------------------

    /// <summary>One scan's current state and progress, for `forge.condition.predicate.scan`. The reading itself
    /// — the game's own `eChainedPuzzleStatus` name and the fraction the instance's own cores carry — is the
    /// game-bound half's; the one thing this half adds is the row's predicate answer, `value`, which is whether
    /// that reading is the state the row asked for. A row that names no state, or a state the game cannot be in,
    /// is refused by name rather than answered `false`, so a plan never reads "not solved" out of a typo.</summary>
    public void ReadScanState(EvaluationContext context, EvaluationOutputs outputs)
    {
        CheckThread();
        if (ValueRowReading.ResourceId(context, "scan") is not { } id)
            throw new RuntimeContractException("scan-resource-missing", "The scan input names no resource.");
        if (NamedState(context) is not { } requested)
            throw new RuntimeContractException("scan-state-missing", "The scan row names no state to test.");
        var reader = ScanStateReader ?? throw new RuntimeContractException("scan-unavailable", id);
        var reading = reader(id);
        var state = Text(reading, "state", id);
        if (!LevelObjectContract.ScanStateNames.Contains(state))
            throw new RuntimeContractException("scan-state-unreadable", state);
        outputs.Boolean(0, string.Equals(state, requested, StringComparison.Ordinal));
        outputs.Text(1, state);
        outputs.Json(2, ValueRowReading.Require(reading, "progress", id));
        outputs.Json(3, ValueRowReading.Require(reading, "count", id));
    }

    /// <summary>The state one scan row asks about. The member is validated against the declared set here, so a
    /// member the game has no status for is refused before the native read rather than compared and answered
    /// `false`.</summary>
    private static string? NamedState(EvaluationContext context)
    {
        if (context.Parameters.ValueKind != JsonValueKind.Object
            || !context.Parameters.TryGetProperty("state", out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        string state = value.GetString()!;
        if (!LevelObjectContract.ScanStateNames.Contains(state))
            throw new RuntimeContractException("scan-state-unknown", state);
        return state;
    }

    private static string Text(JsonElement reading, string port, string id)
    {
        var value = ValueRowReading.Require(reading, port, id);
        if (value.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("reading-incomplete", id + "." + port);
        return value.GetString()!;
    }

    /// <summary>The game-bound half of the value row, handed in by the session that owns the level. A
    /// registration with no reader answers the row with a refusal instead of an empty value, which is what keeps
    /// a row that cannot be read from looking like a row that read nothing: the scan reader answers the state
    /// name and the fraction for one address, so the whole reading of a native subject stays on the side that
    /// can see the subject. It is a function of the address alone because the fraction is read from the instance
    /// the address names, not remembered from an earlier callback.</summary>
    public Func<string, JsonElement>? ScanStateReader { get; set; }

    /// <summary>The world the native objects these facts name belong to: the same epoch the references carry,
    /// which is what the registration's entity resolver answers with.</summary>
    public long WorldEpoch => _kernel.WorldEpoch;

    /// <summary>Whether one level-object reference names something of this provider in the world this module
    /// holds. The kind namespace is this module's own and the epoch is the kernel's current one, so a reference
    /// of a level that no longer exists is refused rather than accepted as a same-key object of the new
    /// one.</summary>
    public bool IsCurrent(EntityReference reference)
    {
        if (_disposed || !EntityKinds.Is(reference, EntityKinds.LevelObject)
            || reference.WorldEpoch != _kernel.WorldEpoch) return false;
        if (reference.Id.StartsWith(EntityKinds.LevelObject + ":container/", StringComparison.Ordinal)
            || reference.Id.StartsWith(EntityKinds.LevelObject + ":item/network/", StringComparison.Ordinal))
            return NativeInstance(reference) != null;
        return reference.LifeEpoch == 1;
    }

    // ---- publication --------------------------------------------------------------------------------

    /// <summary>The alarm this module's own handle in a kernel slot names, or null when the slot is not one of its handles.</summary>
    internal string? AlarmUidOf(int slot)
    {
        foreach (var (uid, handle) in _alarmHandles)
            if (handle.Local == slot) return uid;
        return null;
    }

    /// <summary>Puts a host migration's re-addressed alarm handles in place of the held ones and answers the table it
    /// replaced, which is what a rollback puts back.</summary>
    internal Dictionary<string, RuntimeHandle> SwapAlarmHandles(Dictionary<string, RuntimeHandle> next)
    {
        var previous = _alarmHandles;
        _alarmHandles = next;
        return previous;
    }

    private RuntimeHandle? AlarmHandle(Row row, string uid, object native, Func<object, bool> isLive)
    {
        if (_disposed || !_registration.IsRegistered || _kernel.StartupState != RuntimeStartupState.Ready || !row.Observed) return null;
        if (_alarmHandles.TryGetValue(uid, out var existing) && _registration.TryNative(existing, out var bound)
            && bound != null && isLive(bound)) return existing;
        var handle = _registration.CastEffectHandle("encounter");
        _registration.RegisterNative(handle, native, isLive);
        _alarmHandles[uid] = handle;
        return handle;
    }

    /// <summary>Whether one reading of one subject is a fact to build. A repeated reading of one state has no new
    /// transition and therefore no new event id, which is what keeps a per-frame caller from flooding the kernel.
    /// When nothing listens on the row's binding the kernel would answer `no-consumer`, so the caller builds no
    /// event value at all; the reading is still remembered, because a state observed while nobody listened is a
    /// state this module has already reported. Nothing on this path allocates once the subject has been
    /// seen.</summary>
    private bool Admit(Row row, string subject, in FactReading reading, out long transition)
    {
        transition = 0;
        if (_disposed || !_registration.IsRegistered) return false;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (!_states.Advance(new FactSubject(row.Fact, subject), reading, out transition)) return false;
        return row.Observed;
    }

    /// <summary>Publishes one admitted fact, written by output position, under the transition <see cref="Admit"/>
    /// gave it.</summary>
    private void Raise(EventWriter raised, Row row, string subject, long transition, string scopeId)
    {
        raised.Raise(
            EntityKinds.LevelObject + "." + row.Fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture) + ":"
                + subject + ":" + transition.ToString(CultureInfo.InvariantCulture),
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), scopeId);
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
    }

    private bool ScanIdentified(string uid, string? resourceId)
    {
        var refusal = string.IsNullOrEmpty(uid) ? "scan-unavailable"
            : string.IsNullOrEmpty(resourceId) ? "scan-resource-missing" : null;
        if (refusal == null) return true;
        ScanStepRefused(refusal);
        return false;
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Level objects require the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _instanceLifecycle.Dispose();
        ClearWorld();
    }
}
