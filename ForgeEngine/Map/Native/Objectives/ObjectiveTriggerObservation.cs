using System.Collections.Generic;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the objective trigger row: it reads one objective state change — the state as it
/// was and the state as it now is — and hands the layer-by-layer differences to the game-independent publication
/// half (<c>ForgeMap.LevelEventModule</c>). Nothing here knows a binding id, an event id or a transition, and the
/// publication half knows no game type; that seam is what lets every publishing branch be tested without a game.
///
/// **The transition comes from the callback, not from a memory.** `WardenObjectiveManager.OnStateChange` carries
/// the replicated `pWardenObjectiveState` before and after the change, so the old reading is the machine's own and
/// not a value this provider remembered from the last time it looked: a level whose objective moves from
/// `Started` to `WardenObjectiveItemSolved` is reported with exactly that pair, and the provider never claims to
/// know a state it did not read. A drop-in state (a late joiner catching up with a world it just received) is not
/// a transition, which is why the hook skips it.
///
/// **What is read per layer.** `GetLayerStatus` and `GetChainIndexForLayer` are the machine's own per-layer
/// accessors — the same two the `v-obj` value row reads — and they answer for the three fixed slots
/// (`main`/`secondary`/`third`) this package already names everywhere. An item count is not per layer: the
/// replicated state holds one item table and one required-item table (`ObjectiveItemStates`,
/// `RequiredObjectiveItems`), so a `progress` change is published for the layer the machine itself names as last
/// updated (`WardenObjectiveManager.LastUpdatedLayer`) rather than attached to all three; every fact still carries
/// the counters beside its layer's status and chain. A layer name outside the three this package knows publishes
/// nothing, and so does a layer whose status is outside the declared vocabulary.
///
/// Every member is read, never written: this half adds no objective mutation, and the values it reports are the
/// same ones `evidence/objective-hooks.json` locks (`WardenObjectiveManager.OnStateChange`,
/// `get_LastUpdatedLayer`) and `evidence/level-objective-value.json` locks for the accessors.</summary>
internal static class ObjectiveTriggerObservation
{
    /// <summary>The three layer slots with the machine's own enum member for each, in the order this package
    /// spells them. The list is the `v-obj` reader's own mapping applied to the contract's own layer names, so the
    /// trigger rows and the value row read the same slot under the same name.</summary>
    private static readonly IReadOnlyList<string> Layers = LevelObjectiveValueContract.Layers;

    /// <summary>One state change, as the callback carries it. The two readings are compared per layer and handed
    /// to the publication half as the changes they are — a status change, a chain step, and (for the layer the
    /// machine names as last updated) an item-counter change — so a layer whose readings all stayed the same
    /// publishes nothing at all. <paramref name="manager"/> is the machine the callback ran on, read for the layer
    /// it names as last updated: the item counters belong to one table and not to a slot, so the layer a counter
    /// change is reported under is the machine's own answer rather than one this provider picks.</summary>
    internal static void Changed(WardenObjectiveManager manager, pWardenObjectiveState oldState,
        pWardenObjectiveState newState, LevelEventModule module)
    {
        if (module == null) return;
        int previousSolved = LevelObjectiveValueReader.CountSet(oldState.ObjectiveItemStates);
        int solved = LevelObjectiveValueReader.CountSet(newState.ObjectiveItemStates);
        int previousRequired = oldState.RequiredObjectiveItems?.Length ?? 0;
        int required = newState.RequiredObjectiveItems?.Length ?? 0;
        bool counted = previousSolved != solved || previousRequired != required;
        string? updated = counted && manager != null ? LayerName(manager.LastUpdatedLayer) : null;
        foreach (var layer in Layers)
        {
            if (LevelObjectiveValueReader.LayerType(layer) is not { } type) continue;
            int previousStatus = (int)oldState.GetLayerStatus(type), status = (int)newState.GetLayerStatus(type);
            if (!LevelObjectiveValueContract.TryStatusName(status, out var state)
                || !LevelObjectiveValueContract.TryStatusName(previousStatus, out var previous)) continue;
            int previousChain = oldState.GetChainIndexForLayer(type), chain = newState.GetChainIndexForLayer(type);
            var reading = new ObjectiveReading(state, previous, chain, previousChain, solved, required);
            if (status != previousStatus) module.ObjectiveChanged(layer, ObjectiveTriggerContract.StateChange, reading);
            if (chain != previousChain) module.ObjectiveChanged(layer, ObjectiveTriggerContract.PhaseChange, reading);
            if (layer == updated) module.ObjectiveChanged(layer, ObjectiveTriggerContract.ProgressChange, reading);
        }
    }

    /// <summary>The layer name one `LG_LayerType` member spells, or null for a member this package has no name
    /// for. The machine's "last updated layer" readback is what a counter change is attached to, and a layer
    /// outside the three slots names nothing an author could have selected.</summary>
    internal static string? LayerName(LG_LayerType layer) => layer switch
    {
        LG_LayerType.MainLayer => Layers[0],
        LG_LayerType.SecondaryLayer => Layers[1],
        LG_LayerType.ThirdLayer => Layers[2],
        _ => null
    };
}
