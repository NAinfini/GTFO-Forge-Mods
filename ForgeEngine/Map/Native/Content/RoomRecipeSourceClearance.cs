using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Puts the area's AIGraph source on the walkable component the native cluster scan can use. The scan starts by
/// ray-casting 6 m down from the source (NodeClusterScanData.GetSourceNode, MASK_NODE_GENERATION), takes the voxel node in the
/// hit's pillar, and floods outwards through node links; it only matches when the flood touches the area's gates. An authored
/// source can sit on a prop top, over a floor the volume holds no node for, or on a piece of floor the voxel volume cut off from
/// the doors, and then the scan starts from a node with no way to a gate or from no node at all and the room is refused. The
/// voxel volume is built before the scan, so the walkable components (node links, voxel nodes only) and the gates each one
/// touches are known: the source moves, once, to the voxel node of the area's own cells in the component that touches the most of
/// the area's gates (largest on a tie), nearest the authored source. A source whose own hit already lands on a node of that
/// component never moves, and when the area holds no such node the source stays and the native scan refuses as before.</summary>
internal static class RoomRecipeSourceClearance
{
    private const float Reach = 6f, NodeTolerance = .3f, Half = 2f, Lift = 1f;

    internal static void Prepare(AIGraph.AIG_NodeClusterTools.NodeClusterScanData scan)
    {
        try { PrepareUnchecked(scan); }
        catch (Exception error) { MapLog.Contained("room-recipe-source-clearance.prepare", error); }
    }

    private sealed class Component
    {
        internal int Size; internal readonly HashSet<IntPtr> Gates = new();
        internal readonly List<AIGraph.AIG_VoxelNode> Nodes = new();
    }

    private static void PrepareUnchecked(AIGraph.AIG_NodeClusterTools.NodeClusterScanData scan)
    {
        var node = scan.m_sourceCourseNode; var area = node?.m_area; var room = area?.m_geomorph; var source = scan.m_graphSource;
        var voxel = room?.m_nodeVolume?.m_voxelNodeVolume;
        if (node == null || area == null || room == null || source == null || voxel == null || voxel.WasCollected) return;
        var recipe = RoomRecipeHost.RecipeFor(area);
        if (recipe == null) return;
        int index = -1;
        for (int i = 0; i < room.m_areas.Length; i++) if (room.m_areas[i].Pointer == area.Pointer) { index = i; break; }
        if (index < 0 || index >= recipe.Areas.Count) return;
        var cells = recipe.Areas[index].Cells;
        var gates = new HashSet<IntPtr>();
        if (node.m_portals != null)
            for (int i = 0; i < node.m_portals.Count; i++) { var gate = node.m_portals[i]?.m_gate; if (gate != null) gates.Add(gate.Pointer); }


        var (components, componentOf) = ComponentsOf(voxel);
        // The voxel nodes standing in this area's own cells, per component.
        var inArea = new Dictionary<Component, List<AIGraph.AIG_VoxelNode>>();
        foreach (var component in components)
            foreach (var candidate in component.Nodes)
            {
                var at = room.transform.InverseTransformPoint(candidate.Position);
                if (!cells.Any(c => Math.Abs(c.X - at.x) <= Half && Math.Abs(c.Z - at.z) <= Half)) continue;
                if (!inArea.TryGetValue(component, out var list)) inArea[component] = list = new();
                list.Add(candidate);
            }
        // Only the plan decides, never a cell's height: a cell records the highest surface over it, so where a roof covers the floor a
        // height test keeps the roof and loses the floor that reaches the doors (both floodways hubs landed their source on the roof, 0 of their gates).
        Component? best = null;
        foreach (var component in inArea.Keys)
            if (best == null || Rank(component, inArea[component].Count, gates) > Rank(best, inArea[best].Count, gates)) best = component;
        if (best != null && best.Gates.Count(gates.Contains) < gates.Count) ReportGateReach(recipe, index, room, inArea, gates);
        var origin = source.transform.position;
        var local = room.transform.InverseTransformPoint(origin);
        // The node the native lookup would take at the authored source: the pillar under the first MASK_NODE_GENERATION hit.
        var current = NodeUnderRay(voxel, origin, out var hitText);
        if (best == null)
        {
            Report(RuntimeLogLevel.Error, "source-unmoved", recipe, index, local, local, components.Count, 0, 0, gates.Count, hitText);
            return;
        }
        int bestGates = best.Gates.Count(gates.Contains);
        if (current != null && componentOf.TryGetValue(current.Pointer, out var currentComponent) && currentComponent.Gates.Count(gates.Contains) >= bestGates) return;

        AIGraph.AIG_VoxelNode? pick = null; float pickDistance = float.MaxValue; bool pickLinked = false;
        foreach (var candidate in inArea[best])
        {
            var at = room.transform.InverseTransformPoint(candidate.Position);
            bool linked = (candidate.Links?.Count ?? 0) >= 8;
            float distance = (at.x - local.x) * (at.x - local.x) + (at.z - local.z) * (at.z - local.z);
            if (pick == null || (linked && !pickLinked) || (linked == pickLinked && distance < pickDistance)) { pick = candidate; pickDistance = distance; pickLinked = linked; }
        }
        int mask = LayerManager.MASK_NODE_GENERATION;
        var target = pick!.Position + Vector3.up * Lift;
        if (!(Physics.Raycast(target, Vector3.down, out var hit, Reach, mask) && Math.Abs(hit.point.y - pick.Position.y) <= NodeTolerance))
            target = pick.Position + Vector3.up * .3f;
        source.transform.position = target;
        source.m_positionCalculated = false;
        Report(RuntimeLogLevel.Info, "source-moved", recipe, index, local, room.transform.InverseTransformPoint(target), components.Count, inArea[best].Count, bestGates, gates.Count, hitText);
    }

    // The components of one room's volume are the same for each of its areas' scans, so they are walked once.
    private static readonly Dictionary<(IntPtr, int), (List<Component>, Dictionary<IntPtr, Component>)> Walked = new();
    internal static void ForgetWalked() => Walked.Clear();
    private static (List<Component>, Dictionary<IntPtr, Component>) ComponentsOf(AIGraph.AIG_VoxelNodeVolume voxel)
    {
        var key = (voxel.Pointer, voxel.m_totalCount);
        if (Walked.TryGetValue(key, out var known)) return known;
        if (Walked.Count >= 8) Walked.Clear();
        var list = Components(voxel, out var map);
        return Walked[key] = (list, map);
    }

    private static int Rank(Component component, int inArea, HashSet<IntPtr> gates) => component.Gates.Count(gates.Contains) * 1_000_000 + Math.Min(inArea, 999_999);

    private static AIGraph.AIG_VoxelNode? NodeUnderRay(AIGraph.AIG_VoxelNodeVolume voxel, Vector3 origin, out string hitText)
    {
        hitText = "ray-missed";
        if (!Physics.Raycast(origin, Vector3.down, out var hit, Reach, LayerManager.MASK_NODE_GENERATION)) return null;
        hitText = hit.collider != null ? hit.collider.name : "unnamed";
        var corner = voxel.m_cornerOffsetRound;
        int gx = (int)Math.Floor((hit.point.x - corner.x) * voxel.m_premulVoxelSizeDiv), gz = (int)Math.Floor((hit.point.z - corner.z) * voxel.m_premulVoxelSizeDiv);
        if (gx < 0 || gz < 0 || gx >= voxel.m_countX || gz >= voxel.m_countZ) return null;
        var list = voxel.GetPillar(gx, gz)?.m_nodes;
        if (list == null) return null;
        AIGraph.AIG_VoxelNode? found = null; float gap = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            float d = Math.Abs(list[i].Position.y - hit.point.y);
            if (d <= .5f && d < gap) { found = list[i]; gap = d; }
        }
        return found;
    }

    /// <summary>Every voxel node of the volume grouped by the links between voxel nodes; a gate node reached through a link
    /// is recorded on the component but not walked into, so the neighbouring room's nodes never join it.</summary>
    private static List<Component> Components(AIGraph.AIG_VoxelNodeVolume voxel, out Dictionary<IntPtr, Component> componentOf)
    {
        var result = new List<Component>(); componentOf = new Dictionary<IntPtr, Component>();
        var stack = new Stack<AIGraph.AIG_VoxelNode>();
        for (int gx = 0; gx < voxel.m_countX; gx++)
            for (int gz = 0; gz < voxel.m_countZ; gz++)
            {
                var nodes = voxel.GetPillar(gx, gz)?.m_nodes;
                if (nodes == null) continue;
                for (int i = 0; i < nodes.Count; i++)
                {
                    var start = nodes[i];
                    if (componentOf.ContainsKey(start.Pointer)) continue;
                    var component = new Component(); result.Add(component);
                    componentOf[start.Pointer] = component; stack.Push(start);
                    while (stack.Count > 0)
                    {
                        var current = stack.Pop(); component.Size++; component.Nodes.Add(current);
                        var links = current.Links;
                        if (links == null) continue;
                        for (int l = 0; l < links.Count; l++)
                        {
                            var next = links[l];
                            if (next == null) continue;
                            if (next.BelongsToGate) { var gate = next.Gate; if (gate != null) component.Gates.Add(gate.Pointer); continue; }
                            var voxelNext = next.IsVoxelNode ? next.TryCast<AIGraph.AIG_VoxelNode>() : null;
                            if (voxelNext == null || componentOf.ContainsKey(voxelNext.Pointer)) continue;
                            componentOf[voxelNext.Pointer] = component; stack.Push(voxelNext);
                        }
                    }
                }
            }
        return result;
    }

    /// <summary>No walkable component of this area touches all its gates: the largest ones, each with its height range and the gates it does touch,
    /// and every gate's own link count, so the next log tells a floor split in two from a gate that linked to nothing.</summary>
    private static void ReportGateReach(RoomBuildRecipe recipe, int index, LG_Geomorph room, Dictionary<Component, List<AIGraph.AIG_VoxelNode>> inArea, HashSet<IntPtr> gates)
    {
        // Evidence, not a verdict: this walk stops at gate nodes and counts roofs, while the native cluster scan that follows crosses a passage's own
        // gate nodes. A door the native scan really misses is the Error (phase portal-miss); a room whose scan touches every door reported here only looked split.
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        var parts = inArea.OrderByDescending(pair => pair.Value.Count).Take(6).Select(pair =>
        {
            var heights = pair.Value.Select(node => room.transform.InverseTransformPoint(node.Position).y).ToArray();
            return FormattableString.Invariant($"{pair.Value.Count}n y{heights.Min():0.#}..{heights.Max():0.#} g{pair.Key.Gates.Count(gates.Contains)}");
        }).ToArray();
        // One value per component: a single value would be cut at the log's value length.
        var values = new List<RuntimeLogValue> { RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("phase", "gate-reach"),
            RuntimeLogValue.Of("area", recipe.Areas[index].Id), RuntimeLogValue.Of("gates", gates.Count) };
        for (int i = 0; i < parts.Length; i++) values.Add(RuntimeLogValue.Quote("component" + i, parts[i]));
        // log-values-max: 10
        MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.Aigraph,
            "No walkable component of the area touches all its gates; the largest components with their heights and the gates each touches.", values.ToArray());
    }

    private static void Report(RuntimeLogLevel level, string phase, RoomBuildRecipe recipe, int index, Vector3 from, Vector3 to, int components, int size, int gatesTouched, int gates, string hit)
        => MapLog.Record(level, RoomRecipeCodes.Aigraph,
            "The area's AIGraph source was checked against the voxel components; where it moved, it moved onto the component that touches the area's gates.",
            RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("phase", phase),
            RuntimeLogValue.Of("area", recipe.Areas[index].Id), RuntimeLogValue.Of("from", Format(from)), RuntimeLogValue.Of("to", Format(to)),
            RuntimeLogValue.Of("components", components), RuntimeLogValue.Of("componentSize", size),
            RuntimeLogValue.Of("gatesTouched", gatesTouched), RuntimeLogValue.Of("gates", gates), RuntimeLogValue.Of("hit", hit));

    private static string Format(Vector3 value) => FormattableString.Invariant($"({value.x:0.###},{value.y:0.###},{value.z:0.###})");
}
