using System;
using ChainedPuzzles;
using HarmonyLib;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>One native Activate call's identity, captured before the body and validated after it. A scan's
/// published entity is world-scoped (life epoch 1); native collection and owner/slot replacement
/// must still refuse an old invocation rather than assigning it to a new native life.</summary>
internal static class ScanStepActivated
{
    internal readonly record struct Reading(ChainedPuzzleInstance? Puzzle, IntPtr PuzzlePointer,
        IntPtr CorePointer, string? Uid, int? Index, long WorldEpoch, string? ResourceId = null, string? Refusal = null);

    internal static Reading Capture(iChainedPuzzleCore? core, long worldEpoch)
    {
        if (core == null || core.WasCollected)
            return new Reading(null, default, default, null, null, worldEpoch, Refusal: "stale-life");
        var owner = core.TryCast<CP_Bioscan_Core>()?.m_owner ?? core.TryCast<CP_Cluster_Core>()?.m_owner;
        // A child circle's index belongs to its cluster, not to the chain. The cluster activation hook
        // publishes the chain step; its child callbacks are not additional chain-step activations.
        // Keep them out of this producer rather than inventing an index or rejecting a valid native callback.
        if (owner != null && !owner.WasCollected && owner.TryCast<CP_Cluster_Core>() != null)
            return new Reading(null, default, core.Pointer, null, null, worldEpoch);
        var (puzzle, index) = ScanStepIndex.Resolve(core);
        if (puzzle == null)
            return new Reading(null, default, core.Pointer, null, null, worldEpoch, Refusal: "scan-unavailable");
        var resourceId = ScanInstanceResolver.ResourceId(puzzle);
        return new Reading(puzzle, puzzle.Pointer, core.Pointer, puzzle.m_puzzleUID, index, worldEpoch, resourceId,
            resourceId == null ? "scan-resource-missing" : null);
    }

    internal static string? Publish(iChainedPuzzleCore? core, in Reading reading, LevelObjectModule module,
        DoorTerminalFacts? doors)
    {
        if (reading.Refusal != null)
        {
            module.ScanStepRefused(reading.Refusal);
            return reading.Refusal;
        }
        if (reading.Puzzle is null) return null;
        string? refusal = reading.WorldEpoch != module.WorldEpoch ? "stale-world" : null;
        if (refusal == null && (core == null || core.WasCollected || reading.Puzzle.WasCollected
            || core.Pointer != reading.CorePointer || reading.Puzzle.Pointer != reading.PuzzlePointer
            || !string.Equals(reading.Uid, reading.Puzzle.m_puzzleUID, StringComparison.Ordinal)))
            refusal = "stale-life";
        if (refusal == null)
        {
            var current = ScanStepIndex.Resolve(core);
            if (current.Puzzle?.Pointer != reading.PuzzlePointer || current.Index != reading.Index)
                refusal = "stale-life";
        }
        if (refusal == null)
        {
            var resourceId = ScanInstanceResolver.ResourceId(reading.Puzzle);
            if (resourceId == null) refusal = "scan-resource-missing";
            else if (!string.Equals(resourceId, reading.ResourceId, StringComparison.Ordinal)) refusal = "stale-resource";
        }
        if (refusal != null)
        {
            module.ScanStepRefused(refusal);
            return refusal;
        }
        if (string.IsNullOrEmpty(reading.Uid))
        {
            module.ScanStepRefused("scan-unavailable");
            return "scan-unavailable";
        }
        module.ScanStateChanged(reading.Uid, reading.ResourceId, true, false, reading.Index);
        doors?.ScanStepActivated(reading.Puzzle, reading.Index);
        return null;
    }

    internal static Reading Begin(iChainedPuzzleCore? core)
    {
        var reading = default(Reading);
        if (Session.IsHost && MapHost.Session is { } session)
            session.Run(session.Levels, module => reading = Capture(core, module.WorldEpoch), "scan activation");
        return reading;
    }

    internal static void End(iChainedPuzzleCore? core, Reading reading)
    {
        if (Session.IsHost && (reading.Puzzle is not null || reading.Refusal != null) && MapHost.Session is { } session)
            session.Run(session.Levels, module => Publish(core, reading, module, DoorTerminalFacts.Current), "scan activation");
    }
}

// dump.cs 608781 / 609257 and catalog/native/native-runtime.json: both are void Activate() entries.
// Prefixes capture identity only; they never skip or schedule native work.
[HarmonyPatch(typeof(CP_Bioscan_Core), nameof(CP_Bioscan_Core.Activate))]
internal static class ScanBioscanActivatedReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(CP_Bioscan_Core __instance, out ScanStepActivated.Reading __state)
        => __state = ScanStepActivated.Begin(__instance.TryCast<iChainedPuzzleCore>());

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(CP_Bioscan_Core __instance, ScanStepActivated.Reading __state)
        => ScanStepActivated.End(__instance.TryCast<iChainedPuzzleCore>(), __state);
}

[HarmonyPatch(typeof(CP_Cluster_Core), nameof(CP_Cluster_Core.Activate))]
internal static class ScanClusterActivatedReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(CP_Cluster_Core __instance, out ScanStepActivated.Reading __state)
        => __state = ScanStepActivated.Begin(__instance.TryCast<iChainedPuzzleCore>());

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(CP_Cluster_Core __instance, ScanStepActivated.Reading __state)
        => ScanStepActivated.End(__instance.TryCast<iChainedPuzzleCore>(), __state);
}
