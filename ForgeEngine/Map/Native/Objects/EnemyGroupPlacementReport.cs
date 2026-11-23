using System;
using System.Collections.Generic;
using System.Linq;
using AIGraph;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>Where the native build put the sleeping enemy population: for every area of every zone, its voxel coverage and the
/// score the build recorded in it (PlacedPopScore, the sum of the placed groups' score times ScoreInAreaPaddingMulti). Read once
/// at FactoryDone from the finished world, and only in a level that runs a Forge work; one info row per sleeping group with the
/// members it spawned (tallied by the allocator hook), one per area and one summary row. The scorer ranks areas by remaining coverage, so these rows show whether the groups spread over a zone's areas.</summary>
internal static class EnemyGroupPlacementReport
{
    internal const string LogCode = "map.enemy-group-placed";

    /// <summary>The members the allocator spawned for one sleeping group of the build, tallied by the spawn hook
    /// (`SpawnRuleEnemySpawned`) and written as one `group` row at FactoryDone. Survival-wave groups spawn over the
    /// whole run and are not tallied here.</summary>
    private sealed class GroupTally
    {
        internal long World;
        internal string Zone = "";
        internal uint Block;
        internal string Type = "", Difficulty = "";
        internal int Members;
        internal readonly SortedDictionary<uint, int> Enemies = new();
    }

    private static readonly Dictionary<IntPtr, GroupTally> Groups = new();

    internal static void Count(EnemyAgent agent, EnemyGroup group, AIG_CourseNode? node)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info) || group.SurvivalWave != null || group.m_isSurvivalWave) return;
        long world = ForgeEngine.Plugin.Runtime?.WorldEpoch ?? 0;
        if (!Groups.TryGetValue(group.Pointer, out var tally) || tally.World != world)
        {
            var block = group.m_groupDataBlock;
            var zone = (node ?? group.CurrentCourseNode)?.m_zone;
            var at = zone == null ? null : ZoneIndex.Coordinates(zone);
            Groups[group.Pointer] = tally = new GroupTally
            {
                World = world,
                Zone = at is { } c ? $"{c.Dimension}:{c.Layer}:{c.Zone}" : "unknown",
                Block = block?.persistentID ?? 0,
                Type = block == null ? "" : block.Type.ToString(),
                Difficulty = block == null ? "" : block.Difficulty.ToString()
            };
        }
        tally.Members++;
        tally.Enemies[agent.EnemyDataID] = tally.Enemies.TryGetValue(agent.EnemyDataID, out var seen) ? seen + 1 : 1;
    }

    private static void ReportGroups(long world)
    {
        foreach (var tally in Groups.Values.Where(row => row.World == world).OrderBy(row => row.Zone, StringComparer.Ordinal))
            MapLog.Record(RuntimeLogLevel.Info, LogCode, "Sleeping enemy group spawned.",
                RuntimeLogValue.Of("phase", "group"),
                RuntimeLogValue.Of("zone", tally.Zone),
                RuntimeLogValue.Of("groupBlock", tally.Block),
                RuntimeLogValue.Of("groupType", tally.Type),
                RuntimeLogValue.Of("difficulty", tally.Difficulty),
                RuntimeLogValue.Of("members", tally.Members),
                RuntimeLogValue.Of("enemies", string.Join(",", tally.Enemies.Select(row => row.Key + "x" + row.Value))));
        Groups.Clear();
    }

    internal static void Report(long world)
    {
        if (MapHost.Session == null || !MapLog.Logs(RuntimeLogLevel.Info)) { Groups.Clear(); return; }
        ReportGroups(world);
        var index = ZoneIndex.Current(world);
        int zones = 0, areas = 0, placed = 0;
        foreach (var zone in index.Zones())
        {
            if (zone == null || zone.WasCollected || zone.m_areas == null || !index.TryCoordinates(zone, out var at)) continue;
            zones++;
            foreach (var area in zone.m_areas)
            {
                if (area == null || area.WasCollected) continue;
                areas++;
                if (area.PlacedPopScore > 0) placed++;
                MapLog.Record(RuntimeLogLevel.Info, LogCode, "Zone area coverage read back.",
                    RuntimeLogValue.Of("phase", "area"),
                    RuntimeLogValue.Of("zone", $"{at.Dimension}:{at.Layer}:{at.Zone}"),
                    RuntimeLogValue.Of("area", area.UID),
                    RuntimeLogValue.Of("room", area.m_geomorph?.name ?? ""),
                    RuntimeLogValue.Of("areaSize", area.m_size.ToString()),
                    RuntimeLogValue.Of("areaCoverage", area.VoxelCoverage),
                    RuntimeLogValue.Of("areaPopScore", area.PlacedPopScore),
                    RuntimeLogValue.Of("spawnPoints", area.SpawnPointCount),
                    RuntimeLogValue.Of("nodes", area.m_courseNode?.m_nodeCluster?.m_nodes?.Count ?? -1));
            }
        }
        MapLog.Record(RuntimeLogLevel.Info, LogCode, "Sleeping enemy placement read back.",
            RuntimeLogValue.Of("phase", "summary"),
            RuntimeLogValue.Of("zones", zones), RuntimeLogValue.Of("areas", areas), RuntimeLogValue.Of("areasWithPlacedScore", placed));
    }
}
