using System;
using System.Collections.Generic;
using System.Globalization;
using AIGraph;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The per-zone placement result: for each zone, how many terminals and generator clusters the level asked
/// for, how many the native build placed, and why a shortfall happened; resource containers report their placed count.
/// The native builders drop a request silently when the pinned area holds no marker of its function, so the marker
/// counts are read once at TerminalDistribution, after every area produced its markers and before any request consumed
/// one. The report is read once at FactoryDone; nothing here runs per tick.</summary>
internal static class ZonePlacementReport
{
    internal const string LogCode = "map.zone-placement";
    internal const string NoMarker = "zone-placement-no-marker";
    internal const string Unplaced = "zone-placement-request-unplaced";

    private static readonly ExpeditionFunction[] Functions =
    {
        ExpeditionFunction.Terminal, ExpeditionFunction.GeneratorCluster,
        ExpeditionFunction.ResourceContainerWeak, ExpeditionFunction.ResourceContainerSecure
    };
    private static readonly Dictionary<IntPtr, int[]> Markers = new();
    /// <summary>Per zone, each area's terminal markers in the zone's own area order: `index=count` (the full names did not fit a log value).</summary>
    private static readonly Dictionary<IntPtr, string> TerminalMarkersByArea = new();
    /// <summary>Per zone, the area each terminal request's weighted node query answered, in request order.</summary>
    private static readonly Dictionary<IntPtr, List<string>> TerminalDraws = new();
    private static long _world = long.MinValue;

    /// <summary>Counts each zone's markers per reported function before distribution consumes them.</summary>
    internal static void Snapshot(long world)
    {
        Markers.Clear();
        TerminalMarkersByArea.Clear();
        TerminalDraws.Clear();
        _world = world;
        foreach (var zone in ZoneIndex.Current(world).NativeZones())
        {
            if (zone == null || zone.WasCollected || zone.m_areas == null) continue;
            var counts = new int[Functions.Length];
            var byArea = new List<string>();
            for (var a = 0; a < zone.m_areas.Count; a++)
            {
                var area = zone.m_areas[a];
                var groups = area == null || area.WasCollected ? null : area.m_markerSpawnerGroups;
                var terminals = 0;
                if (groups != null)
                    for (var f = 0; f < Functions.Length; f++)
                        if (groups.Length > (int)Functions[f] && groups[(int)Functions[f]]?.m_spawners is { } spawners)
                        {
                            counts[f] += spawners.Count;
                            if (f == 0) terminals = spawners.Count;
                        }
                byArea.Add(a + "=" + terminals);
            }
            Markers[zone.Pointer] = counts;
            TerminalMarkersByArea[zone.Pointer] = string.Join(",", byArea);
        }
    }

    /// <summary>The final answer of one weighted node query (after an authored pin), kept when it answers one of the zone's
    /// TerminalPlacements entries: LG_Distribute_TerminalsPerZone asks once per entry, and the area named here is the one the
    /// populate job is later asked for a terminal marker.</summary>
    internal static void Drawn(LG_Zone? zone, ZonePlacementWeights? weights, AIG_CourseNode? node, bool success)
    {
        if (zone == null || weights == null || zone.m_settings?.m_zoneData?.TerminalPlacements is not { } placements) return;
        var entry = -1;
        for (var i = 0; i < placements.Count; i++)
            if (placements[i]?.PlacementWeights?.Pointer == weights.Pointer) { entry = i; break; }
        if (entry < 0) return;
        var area = success ? node?.m_area : null;
        var index = -1;
        if (area != null && zone.m_areas != null)
            for (var a = 0; a < zone.m_areas.Count; a++) if (zone.m_areas[a]?.Pointer == area.Pointer) { index = a; break; }
        if (!TerminalDraws.TryGetValue(zone.Pointer, out var draws)) TerminalDraws[zone.Pointer] = draws = new List<string>();
        draws.Add(entry + "->" + (area == null ? "none" : index.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>One row per addressable zone. A shortfall is an error with its reason; a complete zone is info.</summary>
    internal static void Report(long world)
    {
        var index = ZoneIndex.Current(world);
        var clusters = new Dictionary<IntPtr, int>();
        foreach (var cluster in UnityEngine.Object.FindObjectsOfType<LG_PowerGeneratorCluster>())
            if (cluster != null && !cluster.WasCollected && LevelObjectObservation.ZoneOf(cluster) is { } owner)
                clusters[owner.Pointer] = clusters.TryGetValue(owner.Pointer, out var seen) ? seen + 1 : 1;
        foreach (var zone in index.Zones())
        {
            if (!index.TryCoordinates(zone, out var at) || zone.m_settings?.m_zoneData is not { } data) continue;
            var markers = _world == world && Markers.TryGetValue(zone.Pointer, out var counted) ? counted : null;
            int terminalsAsked = data.ForbidTerminalsInZone ? 0 : data.TerminalPlacements?.Count ?? 0;
            int terminals = zone.TerminalsSpawnedInZone?.Count ?? 0;
            int clustersAsked = data.GeneratorClustersInZone;
            int clustersPlaced = clusters.TryGetValue(zone.Pointer, out var found) ? found : 0;
            int containers = 0;
            if (zone.m_courseNodes != null)
                foreach (var node in zone.m_courseNodes)
                    containers += node?.MetaData?.StorageContainers?.Count ?? 0;
            string terminalReason = Reason(terminals, terminalsAsked, markers, 0);
            string clusterReason = Reason(clustersPlaced, clustersAsked, markers, 1);
            bool shortfall = terminalReason != "none" || clusterReason != "none";
            var level = shortfall ? RuntimeLogLevel.Error : RuntimeLogLevel.Info;
            if (!MapLog.Logs(level)) continue;
            MapLog.Record(level, LogCode, shortfall
                    ? "Zone placed fewer objects than the level asked for."
                    : "Zone placed every object the level asked for.",
                RuntimeLogValue.Of("phase", "factory-done"),
                RuntimeLogValue.Of("zone", $"{at.Dimension}:{at.Layer}:{at.Zone}"),
                RuntimeLogValue.Of("terminalsRequested", terminalsAsked), RuntimeLogValue.Of("terminalsPlaced", terminals),
                RuntimeLogValue.Of("terminalMarkers", markers?[0] ?? -1), RuntimeLogValue.Of("terminalReason", terminalReason),
                RuntimeLogValue.Quote("terminalMarkersByArea", _world == world && TerminalMarkersByArea.TryGetValue(zone.Pointer, out var byArea) ? byArea : "unobserved"),
                RuntimeLogValue.Quote("terminalDraws", _world == world && TerminalDraws.TryGetValue(zone.Pointer, out var draws) ? string.Join(",", draws) : "none"),
                RuntimeLogValue.Of("generatorClustersRequested", clustersAsked), RuntimeLogValue.Of("generatorClustersPlaced", clustersPlaced),
                RuntimeLogValue.Of("generatorClusterMarkers", markers?[1] ?? -1), RuntimeLogValue.Of("generatorClusterReason", clusterReason),
                RuntimeLogValue.Of("containerAllocation", data.AllowResourceContainerAllocation),
                RuntimeLogValue.Of("containersPlaced", containers),
                RuntimeLogValue.Of("containerMarkers", markers == null ? -1 : markers[2] + markers[3]));
        }
    }

    /// <summary>The reason a zone placed fewer than it asked for; -1 markers means the count was not observed.</summary>
    private static string Reason(int placed, int requested, int[]? markers, int function)
        => placed >= requested ? "none" : markers != null && markers[function] == 0 ? NoMarker : Unplaced;
}
