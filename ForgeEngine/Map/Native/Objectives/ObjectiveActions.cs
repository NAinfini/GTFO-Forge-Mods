using System;
using ChainedPuzzles;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Which objective the request is about. The chain index is the one the native state carries
/// (`pWardenObjectiveState`'s chain byte), and the layer is the objective's own layer key: those two are what
/// `WardenObjectiveManager.TryGetWardenObjectiveDataForLayer` resolves a chain's data by, so an address is checked against the
/// same table the native objective machine itself builds from instead of against a table of this layer's own.</summary>
internal readonly struct ObjectiveTarget
{
    internal ObjectiveTarget(LG_LayerType layer, int chainIndex)
    {
        Layer = layer;
        ChainIndex = chainIndex;
    }

    internal LG_LayerType Layer { get; }
    internal int ChainIndex { get; }

    internal bool WholeLayer => ChainIndex < 0;
}

/// <summary>The state interactions this row carries, and the native meaning each one has. The names are this
/// layer's own vocabulary — the catalog's `state` string is compared against them — and the member values are
/// the bytes `eWardenObjectiveInteractionType` declares, so a member's byte is the index it has there.</summary>
internal enum ObjectiveStateKind : byte
{
    Discover = 0,
    Start = 1,
    UpdateSubObjective = 5,
    CustomSubObjectiveUpdate = 8,
    SetItemSolved = 9,
    SetExtraTime = 11,
    SetSolveOnDeath = 12,
    SetExitWaveTriggered = 13
}

/// <summary>The phase interactions this row carries: the objective's own event chain step and the win-condition
/// member its sibling row also reaches. `CompleteChain` is not an interaction type at all — it is the
/// whole-layer force entry, which names no chain, so it is the one member with its own native call.</summary>
internal enum ObjectivePhaseKind : byte
{
    EventUpdate = 7,
    SolveWinCondition = 4,
    CompleteChain = 255
}

/// <summary>One event step of the objective's own event chain, as `pWardenObjectiveInteraction` carries it: the
/// break index and the index inside it. Both are bytes, so a value outside one byte has nothing to write. The
/// two indices compare by value because every member but the event update is asked for with no step at all, and
/// that check is a comparison rather than a pair of field reads.</summary>
internal readonly struct ObjectiveEventStep : IEquatable<ObjectiveEventStep>
{
    internal ObjectiveEventStep(int breakIndex, int index)
    {
        BreakIndex = breakIndex;
        Index = index;
    }

    internal int BreakIndex { get; }
    internal int Index { get; }

    internal static ObjectiveEventStep None => new(0, 0);

    public bool Equals(ObjectiveEventStep other) => BreakIndex == other.BreakIndex && Index == other.Index;
    public override bool Equals(object? other) => other is ObjectiveEventStep step && Equals(step);
    public override int GetHashCode() => HashCode.Combine(BreakIndex, Index);
    public static bool operator ==(ObjectiveEventStep left, ObjectiveEventStep right) => left.Equals(right);
    public static bool operator !=(ObjectiveEventStep left, ObjectiveEventStep right) => !left.Equals(right);
}

/// <summary>
/// The objective half of the native execution layer: one method per objective action, each writing through the
/// game's own interaction entry or the manager member that owns the decision. Nothing here is a plan, a handle
/// or a result frame — a later binding resolves the target and the parameters and calls in, and this layer
/// answers with the one decision it made.
///
/// Two rules hold for every method. The target is resolved before anything is written: the layer must be this
/// build's own member and the addressed chain must be one `WardenObjectiveManager.TryGetWardenObjectiveDataForLayer`
/// answers for, so an objective this level did not build — a wrong layer, a chain index past the end, a world
/// that was torn down — is refused rather than asked. And a request whose arguments no native entry can carry is
/// refused by name: the interaction struct carries a byte for the chain, a byte each for the two event indices,
/// and it carries no target status at all, so a request for a value it cannot express is never run in its plain
/// form.
///
/// What this layer deliberately does not do is replace native progress. Every member it reaches is the entry the
/// game's own objective machine calls, so the state a request asks for is the state the objective's own rules
/// can reach: forcing a member makes the objective's own machine act, it does not write a status directly. A
/// native read that throws is not caught here: an action runs inside the same session guard as the readbacks,
/// which disables the provider once instead of letting this layer swallow a failure the rest of the session
/// would keep acting on.
/// </summary>
internal static class ObjectiveActions
{
    /// <summary>What this layer answers when its own readback confirmed the request. The handler concludes by
    /// them and the result carries `committed`, so none of them is a result code.</summary>
    internal const string StateConfirmed = "objective-state-confirmed";
    internal const string ChainCompleted = "objective-chain-completed";
    internal const string ReactorWaveUpdated = "objective-reactor-wave-issued";
    internal const string ReactorVerified = "objective-reactor-verified";
    internal const string TimedRoundMoved = "objective-timed-round-moved";

    /// <summary>The last sub-objective member the interaction struct carries: the vocabulary's own members run
    /// from 0 to `GoToWinConditionHelp`, and a value past it is not a member the native side can be asked for.</summary>
    private const int LastSubObjective = (int)eWardenSubObjectiveStatus.GoToWinConditionHelp;
    /// <summary>No item was named. The struct's item field is an int index, so "absent" is its own value rather
    /// than zero, which is a real item index.</summary>
    internal const int NoItem = -1;

    /// <summary>Asks the objective's own interaction entry for one state member. The member's own arguments are
    /// checked against the member that was asked for: only the sub-objective member carries a sub-objective, only
    /// the item member carries an item index, and only the extra-time member carries a time.</summary>
    internal static MapActionOutcome SetState(WardenObjectiveManager manager, ObjectiveTarget target,
        ObjectiveStateKind kind, int subObjective, int itemId, float extraTime)
    {
        if (manager == null || manager.WasCollected) return MapActionOutcome.Refused(ResultCode.ObjectiveUnavailable);
        if (!Reachable(target)) return MapActionOutcome.Refused(ResultCode.ObjectiveUnknownTarget);
        if (kind == ObjectiveStateKind.UpdateSubObjective)
        {
            if (subObjective < 0 || subObjective > LastSubObjective) return MapActionOutcome.Refused(ResultCode.ObjectiveSubObjectiveUnknown);
        }
        else if (subObjective != 0) return MapActionOutcome.Refused(ResultCode.ObjectiveSubObjectiveUnsupported);
        if (kind == ObjectiveStateKind.SetItemSolved)
        {
            if (itemId == NoItem) return MapActionOutcome.Refused(ResultCode.ObjectiveItemIdUnsupported);
        }
        else if (itemId != NoItem) return MapActionOutcome.Refused(ResultCode.ObjectiveItemIdUnsupported);
        if (kind == ObjectiveStateKind.SetExtraTime)
        {
            if (!float.IsFinite(extraTime)) return MapActionOutcome.Refused(ResultCode.ObjectiveExtraTimeUnsupported);
        }
        else if (extraTime != 0) return MapActionOutcome.Refused(ResultCode.ObjectiveExtraTimeUnsupported);
        // Native extra time writes the single shared field and its entry takes no layer or chain. Only the main
        // layer with no chain is the global request; any named chain (chain 0 included) or other layer is refused
        // rather than silently run globally.
        if (kind == ObjectiveStateKind.SetExtraTime)
        {
            if (!target.WholeLayer || target.Layer != LG_LayerType.MainLayer)
                return MapActionOutcome.Refused(ResultCode.ObjectiveTargetShapeUnsupported);
            WardenObjectiveManager.SetExtraTime(extraTime);
            return MapActionOutcome.Issued(StateSettled(target, kind, extraTime) ? StateConfirmed : ResultCode.ObjectiveExtraTimeIssued);
        }
        if (target.ChainIndex > 254) return MapActionOutcome.Refused(ResultCode.ObjectiveUnknownTarget);

        var interaction = new pWardenObjectiveInteraction
        {
            type = (eWardenObjectiveInteractionType)(byte)kind,
            inLayer = target.Layer,
            ownerChainIndexPlusOne = target.WholeLayer ? (byte)0 : (byte)(target.ChainIndex + 1),
            newSubObj = (eWardenSubObjectiveStatus)(byte)subObjective,
            itemID = itemId == NoItem ? 0 : itemId,
            extraTime = extraTime
        };
        manager.AttemptInteract(interaction);
        return MapActionOutcome.Issued(StateSettled(target, kind, extraTime) ? StateConfirmed : StateCode(kind));
    }

    /// <summary>True when the objective machine's replicated state already shows the member the request asked for. The
    /// interaction entry returns void, so this read is the only settlement the host has right after the call: it covers the
    /// members whose effect is one field of that state (the layer status of the current chain, the extra time, the two flags)
    /// and says nothing for the members whose effect it does not carry, which stay an unknown commit.</summary>
    private static bool StateSettled(ObjectiveTarget target, ObjectiveStateKind kind, float extraTime)
    {
        var state = WardenObjectiveManager.CurrentState;
        if (state == null) return false;
        switch (kind)
        {
            case ObjectiveStateKind.Discover:
            case ObjectiveStateKind.Start:
                if (!target.WholeLayer && state.GetChainIndexForLayer(target.Layer) != target.ChainIndex) return false;
                return (int)state.GetLayerStatus(target.Layer) >= (int)(kind == ObjectiveStateKind.Discover
                    ? eWardenObjectiveStatus.Discovered : eWardenObjectiveStatus.Started);
            case ObjectiveStateKind.SetExtraTime: return state.extraTime == extraTime;
            case ObjectiveStateKind.SetSolveOnDeath: return state.forceWinOnDeath;
            case ObjectiveStateKind.SetExitWaveTriggered: return state.exitWaveTriggered;
            default: return false;
        }
    }

    /// <summary>Asks the objective's own interaction entry for one phase member. The chain index is the byte the
    /// interaction struct carries and the layer is the request's own layer, so a request cannot silently move the
    /// interaction to another layer's objective.</summary>
    internal static MapActionOutcome SetPhase(WardenObjectiveManager manager, ObjectiveTarget target,
        ObjectivePhaseKind kind, ObjectiveEventStep step)
    {
        if (manager == null || manager.WasCollected) return MapActionOutcome.Refused(ResultCode.ObjectiveUnavailable);
        if (!Reachable(target)) return MapActionOutcome.Refused(ResultCode.ObjectiveUnknownTarget);
        if (kind == ObjectivePhaseKind.CompleteChain)
        {
            // The whole-layer force entry names no chain, so it cannot serve a chain-specific request.
            if (!target.WholeLayer) return MapActionOutcome.Refused(ResultCode.ObjectiveTargetShapeUnsupported);
            if (step != ObjectiveEventStep.None) return MapActionOutcome.Refused(ResultCode.ObjectiveEventStepUnsupported);
            WardenObjectiveManager.ForceCompleteObjectiveAll(target.Layer);
            // Returning from the request is not settlement. The native status callback activates the exit only
            // for a solved final chain (20403457, CheckWardenObjectiveStatus 0x1513a8a..0x1513acb).
            // Read the same replicated state and chain table; an earlier solved chain is not the whole layer.
            var state = WardenObjectiveManager.CurrentState;
            int status = state == null ? -1 : (int)state.GetLayerStatus(target.Layer);
            int chain = state == null ? -1 : state.GetChainIndexForLayer(target.Layer);
            int count = WardenObjectiveManager.GetWardenObjectiveCountForLayer(target.Layer);
            string? reason = state == null ? "objective-state-unavailable"
                : count <= 0 ? "objective-chain-table-unavailable"
                : chain != count - 1 ? "objective-chain-not-final"
                : status != (int)eWardenObjectiveStatus.WardenObjectiveItemSolved ? "objective-layer-not-solved"
                : null;
            if (reason == null) return MapActionOutcome.Issued(ChainCompleted);
            if (MapLog.Logs(RuntimeLogLevel.Error))
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ObjectiveCompletionUnconfirmed,
                    "Native force completion returned without a solved final-chain readback.",
                    RuntimeLogValue.Quote("layer", ObjectiveTriggerObservation.LayerName(target.Layer) ?? target.Layer.ToString()),
                    RuntimeLogValue.Of("status", status), RuntimeLogValue.Of("chain", chain),
                    RuntimeLogValue.Of("chains", count), RuntimeLogValue.Quote("reason", reason));
            return MapActionOutcome.Issued(ResultCode.ObjectiveChainCompletionUnconfirmed);
        }
        if (target.WholeLayer) return MapActionOutcome.Refused(ResultCode.ObjectiveTargetShapeUnsupported);
        if (kind == ObjectivePhaseKind.EventUpdate)
        {
            // Both indices are bytes on the wire, and the break index names a break the objective's own data
            // block declares: a value outside one byte has nothing to write.
            if (step.BreakIndex < 0 || step.BreakIndex > 255 || step.Index < 0 || step.Index > 255)
                return MapActionOutcome.Refused(ResultCode.ObjectiveEventStepUnsupported);
        }
        else if (step != ObjectiveEventStep.None) return MapActionOutcome.Refused(ResultCode.ObjectiveEventStepUnsupported);
        if (target.ChainIndex > 254) return MapActionOutcome.Refused(ResultCode.ObjectiveUnknownTarget);

        var interaction = new pWardenObjectiveInteraction
        {
            type = (eWardenObjectiveInteractionType)(byte)kind,
            inLayer = target.Layer,
            ownerChainIndexPlusOne = (byte)(target.ChainIndex + 1),
            newOnActivateEventBreakIndex = (byte)step.BreakIndex,
            newOnActivateEventIndex = (byte)step.Index
        };
        manager.AttemptInteract(interaction);
        return MapActionOutcome.Issued(kind == ObjectivePhaseKind.SolveWinCondition ? ResultCode.ObjectiveWinConditionIssued : ResultCode.ObjectivePhaseIssued);
    }

    /// <summary>Writes one objective chain's reactor wave state: the wave index and the progress inside it, on the reactor's
    /// own replicated state (`LG_WardenObjective_Reactor.m_stateReplicator`, whose `pReactorState.stateCount` is
    /// the wave counter the reactor advances through its `Increase_stateCount` interaction and whose
    /// `stateProgress` is the progress the level's own reactor GUI draws). The status the reactor is in is left
    /// exactly as the reactor's own state machine has it: this write moves the wave the reactor is on, it does not
    /// invent a status vocabulary beside the objective interaction's. A chain whose reactor this manager does not
    /// hold is refused by name, so nothing is written for an objective that has no reactor at all. Only the members
    /// the caller supplies are written: a null index or progress keeps the value the reactor is already at.</summary>
    internal static MapActionOutcome SetReactorWave(WardenObjectiveManager manager, ObjectiveTarget target,
        int? waveIndex, double? progress)
    {
        if (manager == null || manager.WasCollected) return MapActionOutcome.Refused(ResultCode.ObjectiveUnavailable);
        if (FindReactor(manager, target, out var reactor) is { } refusal) return MapActionOutcome.Refused(refusal);
        // The legal wave count of every reactor mode is the length of its own objective data's `ReactorWaves`
        // table: `OnStateCountUpdate` stores the count and `TryGetCurrentWaveData` reads entry count-1, logging
        // and skipping the wave when the table has no such entry (GameAssembly 0x15bdd50 / 0x15bf2a0, build
        // 20403457). A count above the table would be stored and then silently do nothing, so it is refused
        // here, before anything is written, instead of being served as a wave.
        if (waveIndex is { } requested)
        {
            if (!WardenObjectiveManager.TryGetWardenObjectiveDataForLayer(target.Layer, target.ChainIndex, out var data)
                || data == null || data.ReactorWaves == null)
                return MapActionOutcome.Refused(ResultCode.ObjectiveReactorWaveTableUnavailable);
            if (requested > data.ReactorWaves.Count) return MapActionOutcome.Refused(ResultCode.ObjectiveReactorWaveOutOfRange);
        }
        var state = reactor.m_stateReplicator.State;
        // Build 20403457 has no arbitrary Set/Decrease count interaction: `AttemptInteract` switches on the
        // interaction, `Verify_startup` is the only member that raises the count (by one, with the startup
        // verification), and `Increase_stateCount` (11) falls to the empty default (0x15bb4d0 jump table).
        // The replicator's own State setter is the one native replicated write left, so it is the adapter.
        if (waveIndex is { } index && state.stateCount != index)
        {
            state.stateCount = index;
            reactor.m_stateReplicator.State = state;
        }
        // Progress does have a formal native request. It constructs the state and submits through
        // the reactor's InteractWithState path, so native patches and replication observe this change.
        if (progress is { } fraction)
            reactor.AttemptInteract(eReactorInteraction.Update_stateProgress, (float)fraction);
        var actual = reactor.m_stateReplicator.State;
        if (waveIndex is { } expectedIndex && actual.stateCount != expectedIndex
            || progress is { } expectedProgress && !actual.stateProgress.Equals((float)expectedProgress))
            return MapActionOutcome.Issued(ResultCode.ObjectiveReactorWaveUnconfirmed);
        return MapActionOutcome.Issued(ReactorWaveUpdated);
    }

    /// <summary>Passes the wave the reactor is waiting on through the reactor's own verification entry, the one its
    /// terminal command calls after a correct code, so the wave ends and the next starts exactly as with a code.</summary>
    internal static MapActionOutcome VerifyReactorWave(WardenObjectiveManager manager, ObjectiveTarget target)
    {
        if (manager == null || manager.WasCollected) return MapActionOutcome.Refused(ResultCode.ObjectiveUnavailable);
        if (FindReactor(manager, target, out var reactor) is { } refusal) return MapActionOutcome.Refused(refusal);
        if (reactor.m_currentState.status != eReactorStatus.Startup_waitForVerify) return MapActionOutcome.Refused(ResultCode.ObjectiveReactorNotWaiting);
        reactor.OnTerminalStartupSequenceVerify();
        return MapActionOutcome.Issued(reactor.m_stateReplicator.State.status != eReactorStatus.Startup_waitForVerify
            ? ReactorVerified : ResultCode.ObjectiveReactorVerifyUnconfirmed);
    }

    /// <summary>The one reactor of an objective chain; the code names why there is none to act on.</summary>
    internal static string? FindReactor(WardenObjectiveManager manager, ObjectiveTarget target, out LG_WardenObjective_Reactor reactor)
    {
        reactor = null!;
        if (target.WholeLayer) return ResultCode.ObjectiveTargetShapeUnsupported;
        foreach (var item in manager.m_wardenObjectiveItem)
        {
            // The game's table keeps entries whose item is null (an objective that registered no item), so a value is read before it is cast.
            var candidate = item.Value?.TryCast<LG_WardenObjective_Reactor>();
            if (candidate == null || candidate.WasCollected || candidate.OriginLayer != target.Layer
                || candidate.WardenObjectiveChainIndex != target.ChainIndex) continue;
            if (reactor != null && reactor.Pointer != candidate.Pointer) return ResultCode.ObjectiveReactorTargetAmbiguous;
            reactor = candidate;
        }
        return reactor == null ? ResultCode.ObjectiveUnknownTarget : null;
    }

    /// <summary>Asks the level's own win-condition item to arm or disarm, then confirms the same native puzzle.
    /// DeactivateWinCondition only hides the marker in this build, so disabling also uses the puzzle's native
    /// interaction entry. A missing or changed readback is left for the handler to report as an unknown failure.</summary>
    internal static MapActionOutcome SetExtraction(UnityEngine.Component endpoint, bool enabled)
    {
        if (endpoint == null || endpoint.WasCollected) return MapActionOutcome.Refused(ResultCode.ObjectiveUnavailable);
        var puzzle = endpoint switch
        {
            ElevatorShaftLanding landing => landing.m_puzzle,
            LG_LevelExitGeo exit => exit.m_puzzle,
            _ => null
        };
        if (puzzle == null || puzzle.WasCollected) return MapActionOutcome.Refused(ResultCode.ObjectiveUnavailable);
        if (puzzle.IsActive == enabled) return MapActionOutcome.AlreadyInState(ResultCode.Committed);
        switch (endpoint)
        {
            case ElevatorShaftLanding landing:
                if (enabled) landing.ActivateWinCondition(); else landing.DeactivateWinCondition();
                break;
            case LG_LevelExitGeo exit:
                if (enabled) exit.ActivateWinCondition(); else exit.DeactivateWinCondition();
                break;
            default: return MapActionOutcome.Refused(ResultCode.ObjectiveUnavailable);
        }
        if (!enabled) puzzle.AttemptInteract(eChainedPuzzleInteraction.Deactivate);
        var readback = endpoint switch
        {
            ElevatorShaftLanding landing => landing.m_puzzle,
            LG_LevelExitGeo exit => exit.m_puzzle,
            _ => null
        };
        if (readback == null || readback.WasCollected || readback.Pointer != puzzle.Pointer || readback.IsActive != enabled)
            return MapActionOutcome.Issued(ResultCode.ExtractionNativeReadbackUnconfirmed);
        return MapActionOutcome.Issued(ResultCode.Committed);
    }

    /// <summary>The one gate every objective action passes: the layer must be a member of the layer vocabulary,
    /// and the whole-layer form must be a layer this build has objective data for. A chain address is checked
    /// against the layer's own objective data table, so a chain the level never built is refused before any write.
    /// The data table is the record of what was built: the objective instance lookup only answers for objective types
    /// that create an instance, so a built chain of another type (a special terminal command) would be refused as unknown.</summary>
    internal static bool Reachable(ObjectiveTarget target)
    {
        if (!Enum.IsDefined(typeof(LG_LayerType), target.Layer)) return false;
        if (target.WholeLayer) return WardenObjectiveManager.HasWardenObjectiveDataForLayer(target.Layer);
        return WardenObjectiveManager.TryGetWardenObjectiveDataForLayer(target.Layer, target.ChainIndex, out var data) && data != null;
    }

    private static string StateCode(ObjectiveStateKind kind) => kind switch
    {
        ObjectiveStateKind.Discover => ResultCode.ObjectiveDiscoverIssued,
        ObjectiveStateKind.Start => ResultCode.ObjectiveStartIssued,
        ObjectiveStateKind.UpdateSubObjective => ResultCode.ObjectiveSubObjectiveIssued,
        ObjectiveStateKind.CustomSubObjectiveUpdate => ResultCode.ObjectiveSubObjectiveIssued,
        ObjectiveStateKind.SetItemSolved => ResultCode.ObjectiveItemSolvedIssued,
        ObjectiveStateKind.SetExtraTime => ResultCode.ObjectiveExtraTimeIssued,
        ObjectiveStateKind.SetSolveOnDeath => ResultCode.ObjectiveSolveOnDeathIssued,
        _ => ResultCode.ObjectiveExitWaveIssued
    };
}
