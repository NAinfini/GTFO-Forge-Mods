using System;
using System.Collections.Generic;
using System.Globalization;
using ChainedPuzzles;
using ForgeEngine.Framework;
using GameData;

namespace ForgeMap.Native;

/// <summary>
/// The one reader of the level's own chained-puzzle instance table: every caller that has to turn an address into
/// the <see cref="ChainedPuzzleInstance"/> it names resolves it here, so the alarm and scan actions, the scan
/// resource provider and the scan value row cannot disagree about which instance an address means.
///
/// Compiled resources name loaded puzzle definitions by DataBlock id. Execution resolves that definition
/// against the game's own `ChainedPuzzleManager.Current.m_instances`; multiple matches are refused.
/// Internal instance observations can also name the native UID or the instance's public alarm name.
///
/// Nothing is guessed and nothing falls back: two instances answering the same address is not a match this layer
/// can make, so the lookup answers nothing and the caller refuses by name rather than starting an arbitrary one. A
/// manager that is gone, an address of the empty string and an instance the game already collected are all the
/// same answer — no instance — because a destroyed object names no scan.
/// </summary>
internal static class ChainedPuzzleLookup
{
    /// <summary>The uniquely active native step, or null when the lock callback names no readable step.
    /// Enumeration locates the active core; the result itself comes from m_puzzleIndex, never the loop counter.
    /// A solved chain has no active step. An active cluster is read from its own top-level slot, never a child index.</summary>
    internal static int? ActiveStepIndex(ChainedPuzzleInstance? puzzle)
    {
        try
        {
            if (puzzle == null || puzzle.WasCollected || puzzle.IsSolved) return null;
            int? found = null;
            for (var slot = 0; slot != puzzle.NRofPuzzles(); slot++)
            {
                var core = puzzle.GetPuzzle(slot);
                if (!Active(core)) continue;
                var reading = ScanStepIndex.Resolve(core);
                if (reading.Puzzle?.Pointer != puzzle.Pointer || reading.Index == null || found != null) return null;
                found = reading.Index;
            }
            return found;
        }
        catch (Exception containedError) { MapLog.Contained("chained-puzzle-lookup.active-step-index", containedError); return null; }
    }

    private static bool Active(iChainedPuzzleCore? core)
    {
        if (core == null || core.WasCollected) return false;
        if (core.TryCast<CP_Bioscan_Core>() is { } bioscan)
            return bioscan.State.status is eBioscanStatus.SplineReveal or eBioscanStatus.Waiting or eBioscanStatus.Scanning;
        var children = core.TryCast<CP_Cluster_Core>()?.m_childCores;
        if (children == null) return false;
        for (var index = 0; index != children.Length; index++)
            if (Active(children[index])) return true;
        return false;
    }

    /// <summary>Every instance of the level this machine generated, in the game's own order. An instance the game
    /// has already collected contributes nothing, because a destroyed object names no scan.</summary>
    internal static IReadOnlyList<ChainedPuzzleInstance> All()
    {
        var manager = ChainedPuzzleManager.Current;
        var list = manager == null || manager.WasCollected ? null : manager.m_instances;
        if (list == null) return Array.Empty<ChainedPuzzleInstance>();
        var instances = new List<ChainedPuzzleInstance>(list.Count);
        for (var index = 0; index != list.Count; index++)
        {
            var instance = list[index];
            if (instance == null || instance.WasCollected) continue;
            instances.Add(instance);
        }
        return instances.AsReadOnly();
    }

    /// <summary>One instance by the address a resource id carries, or null when this world holds none — or holds
    /// more than one, which is the one address this layer refuses to answer.</summary>
    internal static ChainedPuzzleInstance? ById(string resourceId) => Find(resourceId, out _);

    /// <summary><see cref="ById"/> that also says when the address is null because two instances answer it, so a caller
    /// can refuse by that name instead of reporting a puzzle the level never built.</summary>
    internal static ChainedPuzzleInstance? Find(string resourceId, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrEmpty(resourceId)) return null;
        ChainedPuzzleInstance? found = null;
        foreach (var candidate in All())
        {
            if (!Names(candidate, resourceId)) continue;
            if (found != null && found.Pointer != candidate.Pointer) { ambiguous = true; return null; }
            found = candidate;
        }
        return found;
    }

    /// <summary>Whether an instance matches the compiled definition id or an internal native instance key.</summary>
    private static bool Names(ChainedPuzzleInstance instance, string resourceId)
    {
        string uid = instance.m_puzzleUID ?? "";
        if (uid.Length != 0 && string.Equals(uid, resourceId, StringComparison.Ordinal)) return true;
        var data = instance.Data;
        if (data == null) return false;
        if (string.Equals(data.persistentID.ToString(CultureInfo.InvariantCulture), resourceId, StringComparison.Ordinal)) return true;
        string name = data.PublicAlarmName ?? "";
        return name.Length != 0 && string.Equals(name, resourceId, StringComparison.Ordinal);
    }

    /// <summary>The kernel's liveness answer for a handle bound to this instance: the same native object, still not
    /// collected, still carrying the definition it had when the handle named it.</summary>
    internal static Func<object, bool> Liveness(ChainedPuzzleInstance instance)
    {
        var pointer = instance.Pointer;
        var persistentId = instance.Data?.persistentID;
        return value => value is ChainedPuzzleInstance puzzle && !puzzle.WasCollected && puzzle.Pointer == pointer
            && puzzle.Data is { } data && !data.WasCollected && data.persistentID == persistentId;
    }

    /// <summary>The installed puzzle definitions, using the same DataBlock ids as compiled author references.
    /// Definitions exist before generation; ById separately resolves a unique native instance at execution.</summary>
    internal static IReadOnlyList<ResourceRef> Enumerate()
    {
        var blocks = GameDataBlockBase<ChainedPuzzleDataBlock>.GetAllBlocks();
        var references = new List<ResourceRef>();
        if (blocks == null) return references.AsReadOnly();
        for (var index = 0; index != blocks.Count; index++)
        {
            var block = blocks[index];
            if (block == null || block.WasCollected || block.persistentID == 0) continue;
            references.Add(new ResourceRef(AlarmWaveContract.ChainedPuzzleKind,
                block.persistentID.ToString(CultureInfo.InvariantCulture)));
        }
        return references.AsReadOnly();
    }

    /// <summary>A compiled definition id, independent of whether the native level has been generated.</summary>
    internal static ResourceRef? Resolve(string resourceId)
    {
        return uint.TryParse(resourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id != 0 && GameDataBlockBase<ChainedPuzzleDataBlock>.GetBlock(id) is { } block && !block.WasCollected
            ? new ResourceRef(AlarmWaveContract.ChainedPuzzleKind, resourceId) : null;
    }
}
