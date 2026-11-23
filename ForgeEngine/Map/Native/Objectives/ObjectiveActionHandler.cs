using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The three `forge.action.map.*` command handlers: the objective machine's interaction channel and the level's
/// own exit item. One instance per Map session, handed to the one registration that declares the rows;
/// `CanExecute` is the session's own readiness gate and the two lookups are the native singletons the native half
/// already reaches.
///
/// Every handler is one row per request rather than one row per target, and that shape is native rather than
/// chosen: `WardenObjectiveManager.AttemptInteract` names exactly one layer and one chain in the struct it
/// carries, and the exit item belongs to the level. A request that addresses several objectives at once could
/// only be served by looping the same entry, and a row per target would then describe the loop instead of what
/// the entry did. The rows the catalog's result schema declares are written with the one target count the request
/// really had.
///
/// The whole plan-level request check runs before the first write, because a command that cannot be carried out
/// as asked must not half-apply: a parameter the native call cannot be made without and an expected value with no
/// native check are each refused by name. The row's own recipient — the `objectives` resource reference the plan
/// wired — is not a check but the identity the result row is about: it is copied into the row's `target` exactly
/// as the request carried it, and a request that carries none writes no row.
/// </summary>
internal sealed class ObjectiveActionHandler
{
    /// <summary>The state members this row carries, and the interaction each one reaches. The names are the
    /// vocabulary this provider publishes for the catalog's `state` input: the member's own native name, in the
    /// case the enum declares it. The interaction types the sibling rows own are deliberately absent — a request
    /// for one of them is refused by name instead of being served under a row that does not describe it.</summary>
    internal static readonly IReadOnlyDictionary<string, ObjectiveStateKind> StateMembers =
        new Dictionary<string, ObjectiveStateKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["DiscoverObjective"] = ObjectiveStateKind.Discover,
            ["StartObjective"] = ObjectiveStateKind.Start,
            ["UpdateSubObjective"] = ObjectiveStateKind.UpdateSubObjective,
            ["CustomSubObjectiveUpdate"] = ObjectiveStateKind.CustomSubObjectiveUpdate,
            ["SetItemSolved"] = ObjectiveStateKind.SetItemSolved,
            ["SetExtraTime"] = ObjectiveStateKind.SetExtraTime,
            ["SetSolveOnDeath"] = ObjectiveStateKind.SetSolveOnDeath,
            ["SetExitWaveTriggered"] = ObjectiveStateKind.SetExitWaveTriggered
        };

    /// <summary>The phase members this row carries: the objective's own event chain step, the win-condition
    /// member, and the whole-layer force completion. `CompleteChain` is this provider's own name for the one
    /// member that is not an interaction type.</summary>
    internal static readonly IReadOnlyDictionary<string, ObjectivePhaseKind> PhaseMembers =
        new Dictionary<string, ObjectivePhaseKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["EventUpdate"] = ObjectivePhaseKind.EventUpdate,
            ["SolveWinCondition"] = ObjectivePhaseKind.SolveWinCondition,
            ["CompleteChain"] = ObjectivePhaseKind.CompleteChain
        };

    private readonly Func<bool> _canExecute;
    private readonly Func<WardenObjectiveManager?> _objectiveManager;
    private readonly Func<EntityReference, long, UnityEngine.Component?> _exit;
    private readonly Action<string> _report;
    internal ObjectiveActionHandler(Func<bool> canExecute, Func<WardenObjectiveManager?> objectiveManager,
        Func<EntityReference, long, UnityEngine.Component?> exit, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(canExecute);
        _canExecute = () =>
        {
            try { return canExecute(); }
            catch (Exception containedError)
            {
                MapLog.Contained("objective-action-handler.can-execute", containedError);
                return false;
            }
        };
        _objectiveManager = objectiveManager ?? throw new ArgumentNullException(nameof(objectiveManager));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The production wiring: the session's own readiness gate plus the two native singletons, each
    /// read late through the property that owns it. Every lookup answers the game's current instance — the
    /// manager and the landing are process-wide singletons, and a world that was torn down leaves the property
    /// null, which is what the handlers refuse as `objective-unavailable`. The lookups are read on every request
    /// rather than captured once, because the instance behind each one is replaced when a level is rebuilt.</summary>
    internal static ObjectiveActionHandler For(Func<bool> canExecute, Action<string> report)
        => new(canExecute, () => WardenObjectiveManager.Current, ExtractionObservation.Resolve, report);

    /// <summary>The single handler body, bound to this instance. The session hands one method group per binding
    /// to the registration that declares it.</summary>
    internal CommandResult HandleState(CommandContext context) => State(context);
    internal CommandResult HandlePhase(CommandContext context) => Phase(context);
    internal CommandResult HandleExtraction(CommandContext context) => Extraction(context);
    internal CommandResult HandleReactorVerify(CommandContext context) => ReactorVerify(context);
    internal CommandResult HandleTimedRoundTime(CommandContext context) => TimedRoundTime(context);

    /// <summary>The `forge.action.map.reactor_verify` command.</summary>
    internal CommandResult ReactorVerify(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (ReadLayer(context, ObjectiveRoundContract.ReactorVerifyObjectives, out var layer) is { } refused)
            return ReportObjective(context, MapActionOutcome.Refused(refused));
        var target = new ObjectiveTarget(layer, (int)(context.Integer(ObjectiveRoundContract.ReactorVerifyChain) ?? 0));
        var manager = _objectiveManager();
        if (manager == null) return Outcome.Rejected(ResultCode.ObjectiveUnavailable);
        return ReportObjective(context, ObjectiveActions.VerifyReactorWave(manager, target));
    }

    /// <summary>The `forge.action.map.timed_round_time` command.</summary>
    internal CommandResult TimedRoundTime(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (ReadLayer(context, ObjectiveRoundContract.TimedRoundTimeObjectives, out var layer) is { } refused)
            return ReportObjective(context, MapActionOutcome.Refused(refused));
        var target = new ObjectiveTarget(layer, (int)(context.Integer(ObjectiveRoundContract.TimedRoundTimeChain) ?? 0));
        // Both ports are ticks (contract §3.2 时间单位); the round stamp the game counts is seconds.
        double seconds = (context.Number(ObjectiveRoundContract.TimedRoundTimeSeconds) ?? 0) / context.TicksPerSecond;
        double minimum = (context.Number(ObjectiveRoundContract.TimedRoundTimeMinimum) ?? 0) / context.TicksPerSecond;
        string code = MapHost.Session?.ObjectiveRounds?.MoveRoundTime(target.Layer, target.ChainIndex, seconds, minimum) ?? ResultCode.ObjectiveTimedRoundNotRunning;
        return ReportObjective(context,
            code == ObjectiveActions.TimedRoundMoved ? MapActionOutcome.AlreadyInState(code) : MapActionOutcome.Refused(code));
    }

    /// <summary>The layer a request writes is the one its objective reference (`layer:&lt;name&gt;`) names: the
    /// recipient is the only spelling of it, read through <see cref="ObjectiveLayerReference"/>. A request with no
    /// objective reference is refused as an unsupported target and a reference naming no layer as an unknown layer.</summary>
    private static string? ReadLayer(CommandContext context, HandlerSlot objectives, out LG_LayerType layer)
    {
        layer = default;
        if (!context.TryResource(objectives, out var reference) || reference.ResourceKind != ObjectiveLayerReference.Kind)
            return ResultCode.ObjectiveTargetUnsupported;
        int index = ObjectiveLayerReference.LayerIndex(reference, ObjectiveActionContract.Layers);
        if (index < 0) return ResultCode.ObjectiveLayerUnknown;
        layer = (LG_LayerType)index;
        return null;
    }

    /// <summary>The `forge.action.map.objective_state` command.</summary>
    internal CommandResult State(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var request = ReadState(context);
        if (!request.Ok) return ReportObjective(context, MapActionOutcome.Refused(request.Code!));
        string? state = context.Text(ObjectiveActionContract.StateInput);
        if (state == null || !StateMembers.TryGetValue(state, out var kind))
            return Outcome.Rejected(ResultCode.ObjectiveStateUnknown);
        var manager = _objectiveManager();
        if (manager == null) return Outcome.Rejected(ResultCode.ObjectiveUnavailable);

        var outcome = ObjectiveActions.SetState(manager, request.Target, kind,
            request.SubObjective, request.ItemId, request.ExtraTime);
        return ReportObjective(context, outcome);
    }

    /// <summary>The `forge.action.map.objective_phase` command.</summary>
    internal CommandResult Phase(CommandContext context)
    {
        if (!context.IsHost || !_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var request = ReadPhase(context);
        if (!request.Ok) return ReportObjective(context, MapActionOutcome.Refused(request.Code!));
        string? phase = context.Text(ObjectiveActionContract.PhaseInput);
        if (phase == null || !PhaseMembers.TryGetValue(phase, out var kind))
            return Outcome.Rejected(ResultCode.ObjectivePhaseUnknown);
        // The native interaction channel is the forcing form; it carries no expected phase.
        var manager = _objectiveManager();
        if (manager == null) return Outcome.Rejected(ResultCode.ObjectiveUnavailable);

        // The reactor write runs first when the plan supplied a wave index or progress: it is the one of the two
        // that can refuse without having written anything, so a refusal here leaves the objective untouched and
        // the request is refused as a whole. The wave state is the reactor's own replicated count and progress;
        // the status it is in is the reactor's own state machine's, never a member this row invents.
        var wave = Wave(context, out var malformedWave);
        if (malformedWave) return Outcome.Rejected(ResultCode.ObjectiveWaveState);
        if (wave != null)
        {
            var reactor = ObjectiveActions.SetReactorWave(manager, request.Target, wave.Value.Index, wave.Value.Progress);
            if (reactor.Code == ResultCode.ObjectiveReactorWaveUnconfirmed) return ReportObjective(context, reactor);
            if (reactor.Commit == MapActionCommit.Refused) return ReportObjective(context, reactor);
            var transition = ObjectiveActions.SetPhase(manager, request.Target, kind,
                new ObjectiveEventStep(request.EventBreakIndex, request.EventIndex));
            // The reactor write has its own immediate readback. The phase entry returns void, so a refusal leaves
            // a confirmed partial, while a submitted transition leaves the combined request unknown until the
            // native objective state is observed through an asynchronous Runtime result.
            return transition.Commit == MapActionCommit.Refused
                ? ReportPartial(context, transition, Commit.Confirmed)
                : ObjectiveConclusion(transition).Commit == Commit.Unknown
                    ? ReportPartial(context, transition, Commit.Unknown)
                    : ReportObjective(context, reactor);
        }
        return ReportObjective(context, ObjectiveActions.SetPhase(manager, request.Target, kind,
            new ObjectiveEventStep(request.EventBreakIndex, request.EventIndex)));
    }

    /// <summary>The wave index and progress a request carries, or null when it carries neither. The two are one
    /// group — a wave index without a progress keeps the progress the reactor is already at and the other way
    /// round — so a request that names either writes the reactor's own replicated wave state, and only the member
    /// it names. A value outside the reactor's own range sets <paramref name="malformed"/> and is refused by name
    /// rather than clamped, and never silently read as "this request carries no wave": a plan that asked for a
    /// wave and got a phase transition instead would be a request half served.</summary>
    private static (int? Index, double? Progress)? Wave(CommandContext context, out bool malformed)
    {
        malformed = false;
        bool hasIndex = context.Present(ObjectiveActionContract.WaveIndex);
        bool hasProgress = context.Present(ObjectiveActionContract.WaveProgress);
        if (!hasIndex && !hasProgress) return null;
        var nativeIndex = context.Integer(ObjectiveActionContract.WaveIndex);
        int? index = nativeIndex is >= 0 and <= 255 ? (int)nativeIndex.Value : null;
        double? progress = context.Number(ObjectiveActionContract.WaveProgress);
        // Preserve the existing authoring bound before narrowing; pReactorState.stateCount itself is an int,
        // not a byte. The reactor's mode-specific legal wave count still needs native data/game validation.
        if (hasIndex && !index.HasValue || hasProgress && !progress.HasValue
            || progress is { } fraction && (!double.IsFinite(fraction) || fraction < 0d || fraction > 1d))
        {
            malformed = true;
            return null;
        }
        return (index, progress);
    }

    /// <summary>The `forge.action.map.extraction_enable` command.</summary>
    internal CommandResult Extraction(CommandContext context)
    {
        if (!_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (context.Boolean(ObjectiveActionContract.ExtractionEnabled) is not { } enabled)
            return Outcome.Rejected(ResultCode.ExtractionEnabledRequired);
        if (context.Count(ObjectiveActionContract.Extractions) < 0) return Outcome.Rejected(ResultCode.MissingField);
        var request = new ExtractionRequest(this, enabled);
        return context.EachTarget(ref request, ExtractionStep, maximum: CommandResult.MaximumFacts,
            report: _reportExtraction ??= ReportExtraction, countColumn: Blocks.ActionMapExtractionEnable.RowTargetCount);
    }

    /// <summary>What one endpoint's step needs from the request.</summary>
    private readonly record struct ExtractionRequest(ObjectiveActionHandler Self, bool Enabled);

    private static readonly TargetStep<ExtractionRequest> ExtractionStep = SetExtraction;
    private Action<EntityReference, Exception>? _reportExtraction;

    private void ReportExtraction(EntityReference target, Exception error)
        => _report("extraction native call failed: " + error.GetType().Name);

    /// <summary>One endpoint: switched through the item's own entry and remembered for the observation path. Every
    /// row carries the request's whole endpoint count.</summary>
    private static void SetExtraction(ref ExtractionRequest request, EntityReference target, int index, RowWriter rows)
    {
        var item = request.Self._exit(target, target.WorldEpoch);
        if (item == null) { rows.End(Status.Rejected, Commit.None, ResultCode.ExtractionTargetMissing); return; }
        var outcome = ObjectiveActions.SetExtraction(item, request.Enabled);
        if (outcome.Commit != MapActionCommit.Refused) ExtractionObservation.Remember(item, target.WorldEpoch);
        var (status, commit) = Conclusion(outcome);
        rows.End(status, commit, outcome.Code);
    }

    /// <summary>The observed state of this exact native endpoint.</summary>
    internal bool? ReadExtraction(EntityReference reference, long worldEpoch)
    {
        return ExtractionObservation.Read(reference, worldEpoch);
    }


    /// <summary>One objective action's outcome as the command result: the shared success code or native refusal, and the one
    /// row the request had — about the objective reference the request carried on its recipient port, copied as
    /// the row's target. A refusal is a rejection with no commit; the reactor's independently read state can be a
    /// confirmed success; a void objective request remains unknown because returning from its request entry is not
    /// the later replicated-state receipt. Force completion also reads the solved final chain back. A request that
    /// carried no objective reference has no recipient, so it writes no row: a row's target is the recipient at
    /// its position, and the command's own status and code still report the decision.</summary>
    private static Outcome ReportObjective(CommandContext context, MapActionOutcome outcome)
    {
        var (status, commit) = ObjectiveConclusion(outcome);
        var code = status == Status.Succeeded ? ResultCode.Committed : outcome.Code;
        if (outcome.Commit == MapActionCommit.Refused && outcome.Code == ResultCode.ObjectiveUnavailable)
            return Outcome.Rejected(outcome.Code);
        return Report(context, status, commit, code);
    }

    /// <summary>One objective action's own outcome when its reactor write landed but its phase half was refused or
    /// remains unknown. The code is the phase half's result and the commit describes the combined request.</summary>
    private static Outcome ReportPartial(CommandContext context, MapActionOutcome outcome, Commit commit)
        => Report(context, Status.Partial, commit, outcome.Code);

    /// <summary>The command's conclusion and, when the request carried its objective reference, the one row about
    /// it. The objective rows declare no column beyond the four fixed ones.</summary>
    private static Outcome Report(CommandContext context, Status status, Commit commit, string code)
    {
        var rows = context.Rows;
        if (rows.Count > 0) rows.Row(0, status, commit, code);
        return new Outcome(status, commit, code);
    }

    /// <summary>The extraction action's existing conclusion. Extraction owns a separate observation path and is
    /// intentionally kept out of the objective-request conclusion below.</summary>
    private static (Status Status, Commit Commit) Conclusion(MapActionOutcome outcome)
        => outcome.Code is ResultCode.ObjectiveReactorWaveUnconfirmed or ResultCode.ExtractionNativeReadbackUnconfirmed
            ? (Status.Failed, Commit.Unknown)
            : (outcome.Commit switch
            {
                MapActionCommit.Issued => Status.Succeeded,
                MapActionCommit.AlreadyInState => Status.Succeeded,
                _ => Status.Rejected
            }, outcome.Commit == MapActionCommit.Refused ? Commit.None : Commit.Confirmed);

    /// <summary>An objective request returning only proves submission. The reactor update and whole-layer completion
    /// have immediate independent readbacks; clean refusals and already-observed state keep their
    /// ordinary conclusions, while every other void request is an explicit unknown commit.</summary>
    private static (Status Status, Commit Commit) ObjectiveConclusion(MapActionOutcome outcome)
    {
        if (outcome.Code is ObjectiveActions.ReactorWaveUpdated or ObjectiveActions.ReactorVerified or ObjectiveActions.ChainCompleted or ObjectiveActions.StateConfirmed)
            return (Status.Succeeded, Commit.Confirmed);
        return outcome.Commit switch
        {
            MapActionCommit.AlreadyInState => (Status.Succeeded, Commit.Confirmed),
            MapActionCommit.Refused => (Status.Rejected, Commit.None),
            _ => (Status.Failed, Commit.Unknown)
        };
    }

    /// <summary>What one request's structural parameters say. `Ok` is false when a parameter the native call
    /// cannot be made without is missing or names a member the layer does not have, and `Code` then carries the
    /// one reason; every other member is read only for the members that take it.</summary>
    private readonly struct Request
    {
        internal Request(bool ok, string? code, ObjectiveTarget target,
            int subObjective, int itemId, float extraTime, int eventBreakIndex, int eventIndex)
        {
            Ok = ok; Code = code; Target = target;
            SubObjective = subObjective; ItemId = itemId; ExtraTime = extraTime;
            EventBreakIndex = eventBreakIndex; EventIndex = eventIndex;
        }

        internal bool Ok { get; }
        internal string? Code { get; }
        internal ObjectiveTarget Target { get; }
        internal int SubObjective { get; }
        internal int ItemId { get; }
        internal float ExtraTime { get; }
        internal int EventBreakIndex { get; }
        internal int EventIndex { get; }

        internal static Request Refused(string code) => new(false, code, default, 0, ObjectiveActions.NoItem, 0, 0, 0);
    }

    private static Request ReadState(CommandContext context)
    {
        if (ReadLayer(context, ObjectiveActionContract.StateObjectives, out var layer) is { } refused) return Request.Refused(refused);
        var target = new ObjectiveTarget(layer, (int)(context.Integer(ObjectiveActionContract.StateChain) ?? -1));
        return new Request(true, null, target,
            (int)(context.Integer(ObjectiveActionContract.StateSubObjective) ?? 0),
            (int)(context.Integer(ObjectiveActionContract.StateItemId) ?? ObjectiveActions.NoItem),
            (float)(context.Number(ObjectiveActionContract.StateExtraTime) ?? 0), 0, 0);
    }

    private static Request ReadPhase(CommandContext context)
    {
        if (ReadLayer(context, ObjectiveActionContract.PhaseObjectives, out var layer) is { } refused) return Request.Refused(refused);
        var target = new ObjectiveTarget(layer, (int)(context.Integer(ObjectiveActionContract.PhaseChain) ?? -1));
        return new Request(true, null, target, 0, ObjectiveActions.NoItem, 0,
            (int)(context.Integer(ObjectiveActionContract.EventBreakIndex) ?? 0),
            (int)(context.Integer(ObjectiveActionContract.EventIndex) ?? 0));
    }

}
