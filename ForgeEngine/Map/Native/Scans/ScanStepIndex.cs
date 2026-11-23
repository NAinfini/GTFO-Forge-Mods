using ChainedPuzzles;

namespace ForgeMap.Native;

/// <summary>The ChainedPuzzle step, distinct from the expanded coverage-component ordinal. Native members are
/// catalog/native/native-runtime.json CP_Bioscan_Core.m_puzzleIndex/m_owner and CP_Cluster_Core.m_puzzleIndex/m_owner;
/// dump.cs 608682/608686 and 609185/609188. A top-level core supplies its Setup index.
/// A child circle is not a chain slot: only its owning cluster publishes the chain-step activation.
/// The native slot must still hold the top-level core; an unresolved slot is explicitly null.</summary>
internal static class ScanStepIndex
{
    internal static (ChainedPuzzleInstance? Puzzle, int? Index) Resolve(iChainedPuzzleCore? core)
    {
        if (core == null || core.WasCollected) return (null, null);
        var bioscan = core.TryCast<CP_Bioscan_Core>();
        var cluster = core.TryCast<CP_Cluster_Core>();
        var owner = bioscan?.m_owner ?? cluster?.m_owner;
        if (owner == null || owner.WasCollected || owner.TryCast<ChainedPuzzleInstance>() is not { } puzzle)
            return (null, null);
        var index = bioscan != null ? bioscan.m_puzzleIndex : cluster?.m_puzzleIndex;
        if (index == null || index < 0 || index >= puzzle.NRofPuzzles()) return (puzzle, null);
        var slot = puzzle.GetPuzzle(index.Value);
        return (puzzle, slot != null && !slot.WasCollected && slot.Pointer == core.Pointer ? index : null);
    }
}
