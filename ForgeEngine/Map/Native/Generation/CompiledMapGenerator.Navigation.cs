using System;
using System.Collections.Generic;
using AIGraph;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    // One summary remains available after the detail budget is exhausted. This stays below the player-log
    // per-tick limit even when FactoryDone writes other Map records on the same frame.
    private const int NavigationDetailLimit = 127;
    private const int NavigationInvalidDetailLimit = 32;

    /// <summary>The game reads a start room's <c>m_areas[0]</c> for player spawn and state capture ("No cluster/nodes in first area?").
    /// A room's authored area order can put an area with no navigation cluster first; when that happens in a zone's entry room, the
    /// first area that has a cluster trades places with it and nothing else in the order changes.</summary>
    private static void EnsureFirstAreaNavigable(Run run)
    {
        for (int zone = 0; zone < run.EntryRooms.Length; zone++)
        {
            try
            {
                if (run.EntryRooms[zone] < 0) continue;
                var areas = run.Rooms[run.EntryRooms[zone]]?.Root.m_areas;
                if (areas == null || areas.Length < 2 || HasNavigation(areas[0])) continue;
                for (int i = 1; i < areas.Length; i++)
                {
                    if (!HasNavigation(areas[i])) continue;
                    (areas[0], areas[i]) = (areas[i], areas[0]);
                    MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation,
                        "Entry room's first area had no navigation cluster; swapped with the first area that has one.",
                        RuntimeLogValue.Of("kind", "first-area-swap"), RuntimeLogValue.Of("zone", (long)zone), RuntimeLogValue.Of("with", (long)i));
                    break;
                }
            }
            catch (Exception error)
            {
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation, "First-area navigation check failed.",
                RuntimeLogValue.Of("kind", "first-area-error"), RuntimeLogValue.Quote("reason", error.GetBaseException().Message));
            }
        }
    }

    private static bool HasNavigation(LG_Area? area)
    {
        var cluster = area?.m_courseNode?.m_nodeCluster;
        return cluster != null && !cluster.WasCollected;
    }

    private static IDisposable? _otherLevelNavigation;

    /// <summary>The same readback for a level this generator did not build (vanilla or another mod's), once its factory is done.</summary>
    internal static void ObserveOtherLevelNavigation()
        => _otherLevelNavigation ??= ForgeCore.FactoryEvents.OnBuildDone(() => { if (!Live(out _)) RecordNavigationGraph(null); });

    /// <summary>Read-only generation evidence for the native course graph. The wave scorer consumes these same
    /// course-node clusters and volumes. A missing volume is recorded before ordinary gates so a bounded trace
    /// still contains the refusal's likely neighborhood; no node, portal, gate or connection is repaired here.
    /// <paramref name="run"/> is null on a level this generator did not build.</summary>
    private static void RecordNavigationGraph(Run? run)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        try
        {
            var nodes = AIG_CourseNode.s_allNodes;
            if (nodes == null)
            {
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation,
                    "Native course graph was unavailable after generation.",
                    RuntimeLogValue.Of("kind", "summary"), RuntimeLogValue.Of("nodesObserved", 0L),
                    RuntimeLogValue.Of("invalidNodes", 0L), RuntimeLogValue.Of("gatesCaptured", 0L),
                    RuntimeLogValue.Of("invalidDetailsWritten", 0L), RuntimeLogValue.Of("invalidDetailLimit", NavigationInvalidDetailLimit),
                    RuntimeLogValue.Of("detailsWritten", 0L), RuntimeLogValue.Of("detailLimit", NavigationDetailLimit),
                    RuntimeLogValue.Of("readFailures", 0L), RuntimeLogValue.Of("truncated", false));
                return;
            }

            var invalid = new List<nint>(NavigationDetailLimit);
            var gates = new List<LG_Gate>(NavigationDetailLimit);
            var gatePointers = new List<nint>(NavigationDetailLimit);
            int nodesObserved = 0, invalidNodes = 0, invalidDetailsWritten = 0, detailsWritten = 0, readFailures = 0;
            bool truncated = false;

            // First pass: missing cluster/volume state is the evidence the positioned-wave refusal needs.
            for (int index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                if (node == null || node.WasCollected) continue;
                nodesObserved++;
                try
                {
                    var cluster = node.m_nodeCluster;
                    var volume = cluster == null || cluster.WasCollected ? null : cluster.m_nodeVolume;
                    var voxels = volume == null || volume.WasCollected ? null : volume.m_voxelNodeVolume;
                    string? reason = cluster == null || cluster.WasCollected ? "node-cluster-unavailable"
                        : volume == null || volume.WasCollected ? "node-volume-unavailable"
                        : voxels == null || voxels.WasCollected ? "voxel-volume-unavailable" : null;
                    if (reason == null) continue;
                    invalidNodes++;
                    if (invalid.Count < NavigationDetailLimit) invalid.Add(node.Pointer);
                    else truncated = true;
                    if (invalidDetailsWritten < NavigationInvalidDetailLimit)
                    {
                        var area = node.m_area;
                        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation,
                            "Native course node has no complete spawn volume after generation.",
                            RuntimeLogValue.Of("kind", "node"), RuntimeLogValue.Of("node", node.Pointer.ToInt64()),
                            RuntimeLogValue.Of("area", area == null || area.WasCollected ? -1L : area.UID),
                            RuntimeLogValue.Of("cluster", cluster == null || cluster.WasCollected ? 0L : cluster.Pointer.ToInt64()),
                            RuntimeLogValue.Of("volume", volume == null || volume.WasCollected ? 0L : volume.Pointer.ToInt64()),
                            RuntimeLogValue.Of("voxels", voxels == null || voxels.WasCollected ? 0L : voxels.Pointer.ToInt64()),
                            RuntimeLogValue.Of("portals", node.m_portals?.Count ?? -1),
                            RuntimeLogValue.Of("gates", area?.m_gates?.Count ?? -1), RuntimeLogValue.Of("reason", reason));
                        invalidDetailsWritten++; detailsWritten++;
                    }
                    else truncated = true;
                    CollectGates(node, gates, gatePointers, ref truncated);
                }
                catch (Exception containedError) { MapLog.Contained("compiled-map-generator-navigation.record-navigation-graph", containedError); readFailures++; }
            }

            // Second pass: include healthy neighboring gates too, until the same fixed detail budget is full.
            for (int index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                if (node == null || node.WasCollected) continue;
                try { CollectGates(node, gates, gatePointers, ref truncated); }
                catch (Exception containedError) { MapLog.Contained("compiled-map-generator-navigation.record-navigation-graph", containedError); readFailures++; }
            }

            // A zone's source gate goes first, so the gates enemy navigation treats as progression gates are never cut by the budget.
            gates.Sort((a, b) => (SourceZone(b) != null).CompareTo(SourceZone(a) != null));
            foreach (var gate in gates)
            {
                if (detailsWritten >= NavigationDetailLimit) { truncated = true; break; }
                try
                {
                    int gateNodeCount = gate.m_nodes?.Count ?? -1;
                    var portal = gate.CoursePortal;
                    bool livePortal = portal != null && !portal.WasCollected && portal.m_gate != null && !portal.m_gate.WasCollected;
                    var from = gate.m_linksFrom?.m_courseNode;
                    var to = gate.m_linksTo?.m_courseNode;
                    var door = gate.SpawnedDoor;
                    var sourceOf = SourceZone(gate);
                    bool written = MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation,
                        "Native gate nodes, course portal and connection endpoints after generation.",
                        RuntimeLogValue.Of("kind", "gate"), RuntimeLogValue.Of("gate", gate.Pointer.ToInt64()),
                        RuntimeLogValue.Of("gateType", (long)gate.Type), RuntimeLogValue.Of("gateNodeCount", gateNodeCount),
                        // What enemy course navigation reads of this portal: AIG_CoursePortal.IsProgressionLocked is the gate's
                        // m_isZoneSource with HasBeenOpenedDuringPlay still false (EnemyCourseNavigation.UpdateTracking ISIL 237,
                        // TryFindNodeTowardGoalNode 122); only LG_Gate.set_IsTraversable (RVA 0x15D8020) sets that flag, and only
                        // outside the Generating state, so a gate with no door to open stays locked. A gate that is not traversable
                        // is skipped by open-only routes (ISIL 117, 351, 498).
                        RuntimeLogValue.Of("compiled", run != null),
                        RuntimeLogValue.Of("zoneSource", gate.m_isZoneSource), RuntimeLogValue.Of("openedDuringPlay", gate.HasBeenOpenedDuringPlay),
                        RuntimeLogValue.Quote("sourceOfZone", sourceOf == null ? "" : ZoneName(sourceOf)),
                        RuntimeLogValue.Quote("door", door == null ? "none" : door.TryCast<UnityEngine.Component>()?.GetIl2CppType().Name ?? "unresolved"),
                        RuntimeLogValue.Quote("fromArea", AreaName(gate.m_linksFrom)), RuntimeLogValue.Quote("toArea", AreaName(gate.m_linksTo)),
                        RuntimeLogValue.Quote("fromZone", gate.m_linksFrom?.m_zone == null ? "" : ZoneName(gate.m_linksFrom.m_zone)),
                        RuntimeLogValue.Quote("toZone", gate.m_linksTo?.m_zone == null ? "" : ZoneName(gate.m_linksTo.m_zone)),
                        RuntimeLogValue.Of("zonePortal", livePortal && portal!.IsZonePortal),
                        RuntimeLogValue.Of("progressionLocked", livePortal && portal!.IsProgressionLocked),
                        RuntimeLogValue.Of("traversable", gate.IsTraversable),
                        RuntimeLogValue.Of("portal", portal == null || portal.WasCollected ? 0L : portal.Pointer.ToInt64()),
                        RuntimeLogValue.Of("portalGate", portal?.m_gate == null || portal.m_gate.WasCollected ? 0L : portal.m_gate.Pointer.ToInt64()),
                        RuntimeLogValue.Of("portalNodeA", portal?.m_nodeA == null || portal.m_nodeA.WasCollected ? 0L : portal.m_nodeA.Pointer.ToInt64()),
                        RuntimeLogValue.Of("portalNodeB", portal?.m_nodeB == null || portal.m_nodeB.WasCollected ? 0L : portal.m_nodeB.Pointer.ToInt64()),
                        RuntimeLogValue.Of("fromNode", from == null || from.WasCollected ? 0L : from.Pointer.ToInt64()),
                        RuntimeLogValue.Of("toNode", to == null || to.WasCollected ? 0L : to.Pointer.ToInt64()),
                        RuntimeLogValue.Of("fromCluster", ClusterPointer(from)), RuntimeLogValue.Of("toCluster", ClusterPointer(to)),
                        RuntimeLogValue.Of("relatedInvalid", Contains(invalid, from) || Contains(invalid, to)
                            || Contains(invalid, portal?.m_nodeA) || Contains(invalid, portal?.m_nodeB)));
                    if (written) detailsWritten++; else readFailures++;
                }
                catch (Exception containedError) { MapLog.Contained("compiled-map-generator-navigation.record-navigation-graph", containedError); readFailures++; }
            }

            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation,
                "Bounded native navigation readback completed after generation.",
                RuntimeLogValue.Of("kind", "summary"), RuntimeLogValue.Of("nodesObserved", nodesObserved),
                RuntimeLogValue.Of("invalidNodes", invalidNodes), RuntimeLogValue.Of("gatesCaptured", gates.Count),
                RuntimeLogValue.Of("invalidDetailsWritten", invalidDetailsWritten), RuntimeLogValue.Of("invalidDetailLimit", NavigationInvalidDetailLimit),
                RuntimeLogValue.Of("detailsWritten", detailsWritten), RuntimeLogValue.Of("detailLimit", NavigationDetailLimit),
                RuntimeLogValue.Of("readFailures", readFailures), RuntimeLogValue.Of("truncated", truncated));
        }
        catch (Exception error)
        {
            // Diagnostics never change generation outcome. The normal compiled-generation readback keeps the
            // explicit failure if the I-DIAG writer itself or a native collection could not be observed.
            if (run != null) run.Record("navigation-observation", run.Level,
                new { status = "unavailable", error = error.GetType().Name + ": " + error.Message });
            else MapLog.Contained("compiled-map-generator-navigation.other-level", error);
        }
    }

    private static void CollectGates(AIG_CourseNode node, List<LG_Gate> gates, List<nint> pointers, ref bool truncated)
    {
        var values = node.m_area?.m_gates;
        if (values == null) return;
        for (int index = 0; index < values.Count; index++)
        {
            var gate = values[index];
            if (gate == null || gate.WasCollected) continue;
            var pointer = gate.Pointer;
            if (pointers.Contains(pointer)) continue;
            if (gates.Count >= NavigationDetailLimit) { truncated = true; continue; }
            pointers.Add(pointer); gates.Add(gate);
        }
    }

    /// <summary>The zone whose m_sourceExpander builds this gate (the gate Done's zone-source mark ends up on), or null.</summary>
    private static LG_Zone? SourceZone(LG_Gate gate)
    {
        foreach (var zone in new[] { gate.m_linksFrom?.m_zone, gate.m_linksTo?.m_zone })
            if (zone?.m_sourceExpander?.GetGate() is { } source && source.Pointer == gate.Pointer) return zone;
        return null;
    }

    private static string ZoneName(LG_Zone zone)
        => "dim" + (int)zone.DimensionIndex + "/" + zone.Layer?.m_type + "/zone" + (int)zone.LocalIndex + "/alias" + zone.Alias;

    private static string AreaName(LG_Area? area)
        => area == null ? "" : (area.m_geomorph == null ? "" : area.m_geomorph.name) + "/" + area.name;

    private static long ClusterPointer(AIG_CourseNode? node)
    {
        var cluster = node?.m_nodeCluster;
        return cluster == null || cluster.WasCollected ? 0L : cluster.Pointer.ToInt64();
    }

    private static bool Contains(List<nint> pointers, AIG_CourseNode? node)
        => node != null && !node.WasCollected && pointers.Contains(node.Pointer);
}
