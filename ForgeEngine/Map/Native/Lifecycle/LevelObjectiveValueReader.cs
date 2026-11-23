using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>The game-bound half of the `v-obj` row: the one read of one objective layer's live state. Every member
/// below is taken from the build's own metadata (20403457, `%TEMP%\covtrigb\ilspy` and the interop assemblies):
///
/// - `WardenObjectiveManager.CurrentState` is the machine's one replicated `pWardenObjectiveState`.
/// - `HasWardenObjectiveDataForLayer(layer)` is the machine's own answer to "is there an objective here", so a
///   layer that has none is refused from the game's table and not from a Forge-side list.
/// - `pWardenObjectiveState.GetLayerStatus(layer)` / `GetLayerSubStatus(layer)` / `GetChainIndexForLayer(layer)` /
///   `GetStartTimeFromLayer(layer)` are the machine's per-layer accessors: they take the layer, which is why this
///   row selects a layer instead of asking for "the current one".
/// - `pWardenObjectiveState.extraTime` is the countdown's own term, and it is read: it is an **extension of the
///   deadline**, not a remaining value. The one coroutine that writes it
///   (`WorldEventManager.<DoExcecuteEvent>d__41.MoveNext`, RVA 0x1683CD0) sets it to `GetExtraTime() + duration`
///   for the native `AddToTimer` event and to zero for `ResetTimer`, and `WO_Survival.OnUpdate` adds it to the
///   deadline it compares against the clock. The formula and the four members it is made of are in
///   `LevelObjectiveValueContract`'s own summary and in `evidence/level-objective-value.json`.
/// - `forceWinOnDeath` / `exitWaveTriggered` are the state's own flags; `ObjectiveItemStates` is the
///   byte-per-item table (`OBJECTIVE_ITEM_STATE_COUNT` = 96) and `RequiredObjectiveItems` the byte-per-required-item
///   table (`REQUIRED_OBJECTIVE_ITEM_MAX_COUNT` = 8), so both counters are counts of the tables themselves and
///   neither is derived.
/// - The objective's type comes from the layer's objective instance: `TryGetWardenObjective(layer, chainIndex,
///   out IWardenObjective)` gives the instance and `WardenObjective.ObjectiveType` names the type the machine
///   built for that layer. The chain index is the state's own, so the instance resolved is the one the layer is
///   actually running. The same instance is where the countdown's two durations live
///   (`IWardenObjective.WardenObjectiveData`), which is why one lookup answers both.
///
/// The read writes nothing: it takes no lock, publishes no event and calls no setter, which is what the row's
/// `query` tier means. A layer the machine has no state for, no data for, or no objective instance for answers
/// null, and the contract turns that into a refusal by name rather than into zeros.</summary>
internal static class LevelObjectiveValueReader
{
    /// <summary>The native manager's connection id, resolved through the existing terminal and entity tables.
    /// The id is a static UInt32 in build 20403457's interop, not an authored serial or progress string.</summary>
    internal static EntityReference? ReadConnectedTerminal(IForgeEngine kernel, string layer, long world)
    {
        if (kernel.WorldEpoch != world)
            throw new RuntimeContractException("terminal-progress-stale", layer);
        var manager = LevelGeneration.LG_ComputerTerminalManager.Current;
        if (manager == null || manager.WasCollected)
            throw new RuntimeContractException(LevelObjectiveValueContract.UnavailableCode, "Terminal manager unavailable.");
        var terminal = TerminalObservation.BySyncId(LevelGeneration.LG_ComputerTerminalManager.OngoingUplinkConnectionTerminalId);
        if (terminal == null) return null;
        var coordinates = terminal.SpawnNode?.m_zone is { } zone ? ZoneIndex.Coordinates(zone) : null;
        if (coordinates == null)
            throw new RuntimeContractException("terminal-progress-missing", layer);
        if (coordinates.Value.Layer != (int?)LayerType(layer)) return null;
        var reference = kernel.ResolveEntityInstance(EntityKinds.MapObject, terminal)
            ?? throw new RuntimeContractException("terminal-progress-missing", layer);
        if (reference.WorldEpoch != world || reference.LifeEpoch != 1)
            throw new RuntimeContractException("terminal-progress-stale", reference.Id);
        return reference;
    }

    /// <summary>CurrentProgress is a native string. CurrentRound is a native round object;
    /// publish its own ToString rather than inventing a percentage or treating it as a round number.</summary>
    internal static LevelObjectiveValueContract.TerminalSample? ReadTerminal(EntityReference reference, string layer, long world)
    {
        if (reference.WorldEpoch != world || reference.LifeEpoch != 1)
            throw new RuntimeContractException("terminal-progress-stale", reference.Id);
        var terminal = TerminalObjectActions.ResolveByAddress(reference)
            ?? throw new RuntimeContractException("terminal-progress-missing", reference.Id);
        var coordinates = terminal.SpawnNode?.m_zone is { } zone ? ZoneIndex.Coordinates(zone) : null;
        if (coordinates == null || coordinates.Value.Layer != (int?)LayerType(layer))
            throw new RuntimeContractException("terminal-progress-layer", reference.Id);
        if (terminal.UplinkPuzzle is { } uplink && !uplink.WasCollected)
        {
            var round = uplink.CurrentRound;
            return new(uplink.CurrentProgress, round == null || round.WasCollected ? null : round.ToString());
        }
        if (terminal.TimedSequencePuzzle is { } sequence && !sequence.WasCollected)
        {
            var round = sequence.CurrentRound;
            return new(sequence.CurrentProgress, round == null || round.WasCollected ? null : round.ToString());
        }
        return null;
    }

    /// <summary>Reads one layer's objective state, or null when this process cannot read it. The data check comes
    /// first because `pWardenObjectiveState` is a value type: the machine's `CurrentState` answers a state for a
    /// level that has no objective at all, so "there is no objective here" has to be asked of
    /// `HasWardenObjectiveDataForLayer` rather than inferred from a state that happens to read as untouched.</summary>
    internal static LevelObjectiveValueContract.LayerSample? Read(string layer)
    {
        var type = LayerType(layer);
        if (type == null) return null;
        if (!WardenObjectiveManager.HasWardenObjectiveDataForLayer(type.Value)) return null;
        var state = WardenObjectiveManager.CurrentState;
        if (state == null) return null;
        int chainIndex = state.GetChainIndexForLayer(type.Value);
        float startTime = state.GetStartTimeFromLayer(type.Value);
        int kind = KindOf(type.Value, chainIndex, out var objective);
        var countdown = Countdown(kind, startTime, state.extraTime, objective);
        return new LevelObjectiveValueContract.LayerSample(
            kind,
            (int)state.GetLayerStatus(type.Value),
            (int)state.GetLayerSubStatus(type.Value),
            chainIndex,
            startTime,
            state.forceWinOnDeath,
            state.exitWaveTriggered,
            CountSet(state.ObjectiveItemStates),
            state.RequiredObjectiveItems?.Length ?? 0,
            countdown.Reading,
            countdown.Seconds);
    }

    /// <summary>One layer's countdown, in the seconds the game itself computes. This is `WO_Survival.OnUpdate`
    /// (RVA 0x150E930) read as native code, term for term: the activation deadline is the layer's own start time
    /// plus the objective's `Survival_TimeToActivate` plus the state's `extraTime`, and the deadline the HUD counts
    /// down to is that plus `Survival_TimeToSurvive` once the clock has passed it. `Clock.ExpeditionProgressionTime`
    /// is the clock both the decision and the countdown are taken against — the same static field block
    /// `Clock.get_Time` reads its own `Time` from, which is what makes the two the game's own clock and not a
    /// second one this provider picked.
    ///
    /// The objective type decides the rest. `TimedTerminalSequence` has a countdown of its own, but it is per
    /// terminal (`TimedTerminalSequencePuzzle`, reached through `LG_ComputerTerminal`) and not a field of the
    /// state, so a layer has no single one to read and the answer is `Unreadable` — never a number taken from one
    /// terminal, and never zero. Every other type has no countdown, which is `Absent`. The `Survival` formula is
    /// only ever computed for `Survival`: it is not applied to a type whose data block carries no such durations.</summary>
    private static (LevelObjectiveValueContract.CountdownReading Reading, float Seconds) Countdown(
        int kind, float startTime, float extraTime, IWardenObjective? objective)
    {
        if (kind == LevelObjectiveValueContract.TimedTerminalSequenceKind)
            return (LevelObjectiveValueContract.CountdownReading.Unreadable, 0f);
        if (kind != LevelObjectiveValueContract.SurvivalKind)
            return (LevelObjectiveValueContract.CountdownReading.Absent, 0f);
        // A Survival layer whose data block the process cannot reach is a state that cannot be answered: the two
        // durations are the formula's own terms, and filling them in would be the fixed formula the row refuses
        // to apply.
        if (objective?.WardenObjectiveData is not { } data)
            return (LevelObjectiveValueContract.CountdownReading.Unreadable, 0f);
        float now = Clock.ExpeditionProgressionTime;
        float activateBy = startTime + data.Survival_TimeToActivate + extraTime;
        float deadline = now > activateBy ? activateBy + data.Survival_TimeToSurvive : activateBy;
        return (LevelObjectiveValueContract.CountdownReading.Running, deadline - now);
    }

    /// <summary>The layer member one of the contract's three names spells, or null for a name the contract would
    /// have refused already. `LG_LayerType` declares exactly these three, so the name table is the enum's own and
    /// the contract's own list is asserted against it by a case. It is read by the session's `objective` resource
    /// provider as well, so the resource table and the read answer about the same three slots.</summary>
    internal static LevelGeneration.LG_LayerType? LayerType(string layer) => layer switch
    {
        "main" => LevelGeneration.LG_LayerType.MainLayer,
        "secondary" => LevelGeneration.LG_LayerType.SecondaryLayer,
        "third" => LevelGeneration.LG_LayerType.ThirdLayer,
        _ => null
    };

    /// <summary>The objective type the layer is running. The instance is resolved through the machine's own chain
    /// lookup with the state's own chain index, so the type belongs to the objective the layer is on. A lookup that
    /// cannot answer leaves the kind unknown rather than guessing one: `Empty` (14) is the game's own member for
    /// "no objective of any type", which is the honest answer for a layer whose instance could not be resolved.
    /// The instance is handed back as well, because the countdown's two durations are read from its data block.</summary>
    private static int KindOf(LevelGeneration.LG_LayerType layer, int chainIndex, out IWardenObjective? objective)
    {
        if (WardenObjectiveManager.TryGetWardenObjective(layer, chainIndex, out var found) && found != null)
        {
            objective = found;
            return (int)found.ObjectiveType;
        }
        objective = null;
        return EmptyKind;
    }

    /// <summary>`eWardenObjectiveType.Empty`, the member the game itself uses for a layer that runs nothing.</summary>
    private const int EmptyKind = 14;

    /// <summary>How many of the objective's item slots are set. The table is one byte per slot and a non-zero byte
    /// is the game's own "solved", so the count is taken the same way. Shared with the objective trigger rows,
    /// which compare the count of two replicated states: one reading of "a solved slot" keeps the value row and
    /// the progress trigger from disagreeing about the same table.</summary>
    internal static int CountSet(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>? states)
    {
        if (states == null) return 0;
        int count = 0;
        for (int index = 0; index < states.Length; index++) if (states[index] != 0) count++;
        return count;
    }
}
