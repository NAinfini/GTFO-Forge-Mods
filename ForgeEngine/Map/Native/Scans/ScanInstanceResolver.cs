using System;
using System.Collections.Generic;
using System.Globalization;
using ChainedPuzzles;

namespace ForgeMap.Native;

/// <summary>
/// The one reader of one instance's own components: the walk that turns a ChainedPuzzle block id into the native
/// scan cores it names, and the same walk's read of the fraction those cores carry. §4.2.9 fixes the row's key as
/// the block and the component ordinal: the author clones the block into this project's own block and the host
/// points at the clone, so after generation the block's own `persistentID` is what every machine can read back —
/// the host object is not part of the key, and the same block reached from two hosts is two instances the row is
/// written to.
///
/// The instances come from the game's own list (`ChainedPuzzleManager.Current.m_instances`), which is the only
/// collection the vanilla factory fills, and each instance's own `Data` property answers the block it was built
/// from. A component ordinal counts the instance's own components in the game's own order with every cluster's
/// child scans expanded in place, because a cluster is one component containing several scans and an author points
/// at the scan rather than at the container. That same expanded walk is what <see cref="Progress"/> reads, so the
/// fraction the value row reports and the ordinal an author writes are counted over one set of cores.
///
/// Nothing is guessed and nothing falls back: the caller reports the block id and the reason, and a row whose
/// instance, ordinal or component kind cannot be answered applies nothing at all. This file decides no code — the
/// one refusal policy is <see cref="ForgeMap.ScanCoverage.Refusal"/>, which is game-independent and pinned by test.
/// </summary>
internal static class ScanInstanceResolver
{
    [ThreadStatic] private static List<iChainedPuzzleCore>? ProgressComponents;

    /// <summary>The canonical resource the website exports, distinct from the native instance UID.
    /// Missing or collected data has no resource identity and must never become a world-scoped fact.</summary>
    internal static string? ResourceId(ChainedPuzzleInstance? instance)
    {
        try
        {
            if (instance == null || instance.WasCollected) return null;
            var data = instance.Data;
            return data == null || data.WasCollected || data.persistentID == 0
                ? null : data.persistentID.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception containedError) { MapLog.Contained("scan-instance-resolver.resource-id", containedError); return null; }
    }

    /// <summary>Every instance of the level this machine generated, in the game's own order, through the one
    /// reader of the manager's instance table (<see cref="ChainedPuzzleLookup"/>). An instance the game has
    /// already collected contributes nothing, because a destroyed object names no scan.</summary>
    internal static IReadOnlyList<ChainedPuzzleInstance> Instances() => ChainedPuzzleLookup.All();

    /// <summary>Every instance of this level whose own `Data.persistentID` is the authored block id, in the game's
    /// own order. An empty answer is the row's `scan-coverage-unresolved` and not a fallback to another block.</summary>
    internal static IReadOnlyList<ChainedPuzzleInstance> Instances(uint chainedPuzzleId)
    {
        var matched = new List<ChainedPuzzleInstance>();
        foreach (var instance in Instances())
        {
            var data = instance.Data;
            if (data == null || data.WasCollected) continue;
            if (data.persistentID == chainedPuzzleId) matched.Add(instance);
        }
        return matched.AsReadOnly();
    }

    /// <summary>
    /// Every component of one instance in the game's own order, with each cluster expanded in place: the cluster's
    /// `m_childCores` are walked in their own order and a child that is itself a cluster is expanded the same way.
    /// The list is the ordinal an author writes against, so a component's position in it is its number.
    /// </summary>
    internal static IReadOnlyList<iChainedPuzzleCore> Components(ChainedPuzzleInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var components = new List<iChainedPuzzleCore>();
        Append(instance, components);
        return components.AsReadOnly();
    }

    private static void Append(ChainedPuzzleInstance instance, List<iChainedPuzzleCore> components)
    {
        var count = instance.NRofPuzzles();
        for (var index = 0; index != count; index++) Append(instance.GetPuzzle(index), components);
    }

    /// <summary>
    /// Every player scanner one instance holds, through the same component walk. It is the complete version of the
    /// walk: a cluster's child scans are scanners of the same instance and are read here, which is what the
    /// encounter-state half's own read leaves out. A scanner with no speed table is left out, because that is the
    /// same test that half uses to tell a real scanner from an empty slot.
    /// </summary>
    internal static IReadOnlyList<CP_PlayerScanner> Scanners(ChainedPuzzleInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var scanners = new List<CP_PlayerScanner>();
        foreach (var component in Components(instance))
        {
            var bioscan = component.TryCast<CP_Bioscan_Core>();
            if (bioscan == null || bioscan.WasCollected) continue;
            var scanner = bioscan.PlayerScanner?.TryCast<CP_PlayerScanner>();
            if (scanner == null || scanner.WasCollected) continue;
            var speeds = scanner.m_scanSpeeds;
            if (speeds == null || speeds.Length == 0) continue;
            scanners.Add(scanner);
        }
        return scanners.AsReadOnly();
    }

    /// <summary>One core appended in the expanded order: a cluster is replaced by its children and anything else
    /// is one component. A null or collected member contributes no component, because a member nothing can read
    /// is not an ordinal an author could have counted.</summary>
    private static void Append(iChainedPuzzleCore? core, List<iChainedPuzzleCore> components)
    {
        if (core == null || core.WasCollected) return;
        if (core.TryCast<CP_Cluster_Core>() is { } cluster && !cluster.WasCollected)
        {
            var children = cluster.m_childCores;
            if (children == null) return;
            for (var index = 0; index != children.Length; index++) Append(children[index], components);
            return;
        }
        components.Add(core);
    }

    /// <summary>
    /// The fraction one instance's own scan cores report, or null when none of them carries a readable one.
    ///
    /// The member is the bioscan core's own replicated state: `CP_Bioscan_Core.State` answers a
    /// `ChainedPuzzles.pBioscanState` (`public pBioscanState get_State()`, dump.cs 608753) whose `progress` field
    /// (dump.cs 609008) is the fraction the core's own sync last wrote — the same number the master's
    /// `Master_OnPlayerScanChanged(float scanProgress, ...)` callback was handed. A cluster's own sync state is
    /// not read: the walk expands a cluster into its child scans, and the reported fraction is the smallest of
    /// them, because a chained puzzle is only as complete as its least advanced core.
    ///
    /// A core whose state does not read refuses the whole reading: a partially read fraction would be a fraction
    /// this layer invented for the cores it could not see. An instance with no bioscan core at all — a component
    /// this build has no progress member for — answers null for the same reason.
    /// </summary>
    internal static double? Progress(ChainedPuzzleInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var cores = ProgressComponents ?? new List<iChainedPuzzleCore>();
        // Lease the buffer before native reads: a reentrant read must receive a separate list.
        ProgressComponents = null;
        try
        {
            try { Append(instance, cores); }
            catch (Exception containedError) { MapLog.Contained("scan-instance-resolver.progress", containedError); return null; }
            double? least = null;
            foreach (var core in cores)
            {
                var bioscan = core.TryCast<CP_Bioscan_Core>();
                if (bioscan == null || bioscan.WasCollected) continue;
                double progress;
                try { progress = bioscan.State.progress; }
                catch (Exception containedError) { MapLog.Contained("scan-instance-resolver.progress", containedError); return null; }
                least = least is { } current ? Math.Min(current, progress) : progress;
            }
            return least;
        }
        finally
        {
            cores.Clear(); // Keep capacity, never retain native objects between readings or worlds.
            ProgressComponents ??= cores;
        }
    }

    /// <summary>Current participants of the native waiting/scanning circles, counted once across cluster
    /// children. Disabled, finished and timed-out cores hold no current circle. An unreadable participant list
    /// refuses the whole reading; it never becomes a fabricated zero.</summary>
    internal static int? PlayerCount(ChainedPuzzleInstance instance)
    {
        try
        {
            var players = new HashSet<IntPtr>();
            bool hasBioscan = false;
            foreach (var core in Components(instance))
            {
                var bioscan = core.TryCast<CP_Bioscan_Core>();
                if (bioscan == null || bioscan.WasCollected) continue;
                hasBioscan = true;
                if (bioscan.State.status is not (eBioscanStatus.Waiting or eBioscanStatus.Scanning)) continue;
                var participants = bioscan.PlayersOnScan;
                if (participants == null) return null;
                for (int index = 0; index < participants.Count; index++)
                {
                    var player = participants[index];
                    if (player == null || player.WasCollected || player.Pointer == IntPtr.Zero) return null;
                    players.Add(player.Pointer);
                }
            }
            return hasBioscan ? players.Count : null;
        }
        catch (Exception containedError) { MapLog.Contained("scan-instance-resolver.player-count", containedError); return null; }
    }
}
