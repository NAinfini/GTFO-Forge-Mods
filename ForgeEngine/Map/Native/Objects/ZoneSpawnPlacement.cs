using System;
using System.Collections.Generic;
using AIGraph;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// Where an enemy goes when the spawn names a zone and no position: one of the places the game itself puts the members of a group
/// (<c>AIG_NodeCluster.TryGetPlacements</c>: a random position of the cluster's scored node list with a random facing), and only a place
/// the zone really owns and the enemy's body fits in. The host alone asks; the spawn that follows is the game's replicated one, so every
/// machine sees the result. The areas of a zone are filled evenly: the area with the fewest zone-random spawns of this world epoch goes
/// first, ties are drawn at random.
/// </summary>
internal static class ZoneSpawnPlacement
{
    // The game's scored list is asked for this many placements per area and per round, and for this many rounds. Each answer is random, so an
    // area in which only a few places suit a wide enemy is still searched; this is sampling effort, not a limit on the enemy or the zone.
    private const int PerArea = 4, Rounds = 4;

    private static readonly Dictionary<IntPtr, int> Spawned = new();
    private static long _epoch = long.MinValue;
    private static readonly List<AIG_CourseNode> Open = new();
    private static readonly List<(Vector3 Position, Quaternion Rotation)> Pool = new();
    internal static bool TryPick(EnvironmentZone address, LG_Zone zone, uint enemyId, long worldEpoch,
        out Vector3 position, out Quaternion rotation, out AIG_CourseNode? node, out string detail)
    {
        position = default; rotation = Quaternion.identity; node = null;
        var prefix = FormattableString.Invariant($"zone=gtfo.zone:{address.Dimension}:{address.Layer}:{address.Zone}");
        if (_epoch != worldEpoch) { Spawned.Clear(); _epoch = worldEpoch; }
        var courseNodes = zone.m_courseNodes;
        if (courseNodes == null || courseNodes.Count == 0) { detail = prefix + ";rule=zone-has-no-course-node"; return false; }
        // A body that fits inside the agent the mesh was baked for needs no clearance read; a wider one needs that much open floor.
        var known = ForgeCore.EnemySpawn.TryCollisionRadius(enemyId, out var radius);
        var needsRoom = known && radius > ForgeCore.Level.NavAgentRadius;
        int asked = 0, outside = 0, narrow = 0, unmeasured = 0;
        string? firstOutside = null;
        Open.Clear();
        foreach (var course in courseNodes)
            if (course != null && !course.WasCollected && course.IsValid && course.m_nodeCluster != null) Open.Add(course);
        var areas = Open.Count;
        while (Open.Count > 0)
        {
            // Fewest zone-random spawns so far, ties drawn at random.
            var least = int.MaxValue;
            for (var i = 0; i < Open.Count; i++) least = Math.Min(least, Count(Open[i]));
            var ties = 0;
            for (var i = 0; i < Open.Count; i++) if (Count(Open[i]) == least) ties++;
            var draw = UnityEngine.Random.Range(0, ties); var at = 0;
            for (var i = 0; i < Open.Count; i++) if (Count(Open[i]) == least && draw-- == 0) { at = i; break; }
            var area = Open[at]; Open.RemoveAt(at);
            Pool.Clear();
            for (var round = 0; round < Rounds; round++)
            {
                var placements = FixedEnemyPlacement.ClusterPlacements(area.m_nodeCluster, PerArea);
                if (placements == null) break;
                for (var i = 0; i < placements.Count; i++) Pool.Add((placements[i].position, placements[i].rotation));
            }
            for (var i = Pool.Count - 1; i > 0; i--)
            {
                var j = UnityEngine.Random.Range(0, i + 1);
                (Pool[i], Pool[j]) = (Pool[j], Pool[i]);
            }
            foreach (var (candidate, facing) in Pool)
            {
                asked++;
                if (!SpacePointActions.SpawnNode(address, zone, candidate, out var found, out _, out var refusal))
                { outside++; firstOutside ??= refusal; continue; }
                if (needsRoom)
                {
                    if (!ForgeCore.Level.TrySnap(candidate, ForgeCore.Level.SampleReach, out var floor)
                        || !ForgeCore.Level.TryFreeRadius(floor, out var free)) { unmeasured++; continue; }
                    if (free < radius) { narrow++; continue; }
                }
                position = candidate; rotation = facing; node = found;
                Spawned[area.Pointer] = Count(area) + 1;
                detail = FormattableString.Invariant($"{prefix};placed={SpacePointActions.PointText(candidate)};areas={areas};areaSpawns={Count(area)};candidates={asked};bodyRadius={(known ? radius : -1f):R}");
                return true;
            }
        }
        detail = FormattableString.Invariant($"{prefix};rule=no-legal-placement;areas={areas};candidates={asked};outsideZoneOrOffFloor={outside};tooNarrow={narrow};clearanceUnmeasured={unmeasured};bodyRadius={(known ? radius : -1f):R}")
            + (firstOutside == null ? "" : ";firstOutside=" + firstOutside);
        return false;
    }

    private static int Count(AIG_CourseNode area) => Spawned.TryGetValue(area.Pointer, out var n) ? n : 0;
}
