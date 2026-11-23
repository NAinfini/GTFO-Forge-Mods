using System;
using System.Collections.Generic;
using AIGraph;
using GameData;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Build-time provenance, not a scene search. Each weighted-node call consumes one placement row,
/// including a failed row. Successful calls append native distribution requests in that same order. The
/// BuildGenericFunctionItems consumes exactly the front request and calls the builder synchronously; its
/// LG_PowerGenerator_Core.Setup receives the body. Pinned native call edges
/// and loop evidence are in generator-placement-identity-20403457.json. No work here runs per tick.</summary>
internal static class GeneratorPlacementBuild
{
    internal const string PlacementLogCode = "map.generator-placement";
    internal sealed record Slot(long World, IntPtr Zone, int Index, int Count);
    internal sealed class Build
    {
        internal readonly LG_Distribute_DetailedPlacedFunctionPerZone Job;
        internal readonly long World;
        internal readonly IntPtr[] Before;
        internal readonly List<(int Index, IntPtr Node)> Selected = new();
        internal readonly Build? Previous;
        internal int Queries;
        internal bool Invalid;
        internal Build(long world, LG_Distribute_DetailedPlacedFunctionPerZone job, IntPtr[] before, Build? previous)
        { World = world; Job = job; Before = before; Previous = previous; }
    }

    private static long _world = long.MinValue;
    private static Build? _building;
    private static Slot? _spawning;
    private static readonly Dictionary<IntPtr, int> Counts = new();
    private static readonly Dictionary<IntPtr, Slot> Requests = new();
    private static readonly Dictionary<(IntPtr Zone, int Index), LG_PowerGenerator_Core?> Spawned = new();
    private static readonly Dictionary<IntPtr, Slot> Bodies = new();
    // Requests the native spawn job actually took from the front of its queue. A recorded request that is not here was
    // never consumed (no marker took it), which is a different failure from a request that was consumed and built nothing.
    private static readonly HashSet<(IntPtr Zone, int Index)> Consumed = new();

    internal static void Clear()
    {
        _world = long.MinValue;
        _building = null;
        _spawning = null;
        // FixedFunctionPoints keeps its own world and is cleared by the session; this world change comes from the first
        // generator job and must not wipe the terminal and cluster pins the earlier terminal distribution already made.
        Counts.Clear(); Requests.Clear(); Spawned.Clear(); Bodies.Clear(); Consumed.Clear();
    }

    private static void Observe(long epoch)
    {
        if (_world == epoch) return;
        Clear(); _world = epoch;
    }

    private static List<LG_DistributeItem> Queue(LG_Distribute_DetailedPlacedFunctionPerZone job)
    {
        var result = new List<LG_DistributeItem>();
        var queue = job.m_zone?.DistributionData?.GenericFunctionItems?.m_itemQueue;
        if (queue != null) foreach (var item in queue) result.Add(item);
        return result;
    }

    internal static Build? Begin(long epoch, LG_Distribute_DetailedPlacedFunctionPerZone job)
    {
        Observe(epoch);
        if (job.m_func != ExpeditionFunction.PowerGenerator || job.m_distQueue != eDistributionQueue.GenericFunctionItems
            || job.m_zone == null || job.m_placementData == null) return null;
        var queue = Queue(job);
        var before = new IntPtr[queue.Count];
        for (var i = 0; i < before.Length; i++) before[i] = queue[i].Pointer;
        var state = new Build(epoch, job, before, _building);
        _building = state;
        return state;
    }

    internal static void Selected(LG_Zone zone, ZonePlacementWeights weights, AIG_CourseNode? node, bool success)
    {
        if (_building is not { } state) return;
        var index = state.Queries++;
        if (zone.Pointer != state.Job.m_zone.Pointer || index >= state.Job.m_placementData.Count
            || state.Job.m_placementData[index].PlacementWeights?.Pointer != weights.Pointer)
        { state.Invalid = true; return; }
        if (success)
        {
            if (node == null || node.WasCollected) state.Invalid = true;
            else state.Selected.Add((index, node.Pointer));
        }
    }

    /// <summary>A fixed generator's native request is pinned to its authored room (<see cref="FixedFunctionPoints.Pin"/>);
    /// the native request, its ordinal and the builder pipeline stay. A row that cannot be pinned keeps the native pick.</summary>
    internal static bool PinNode(IReadOnlyList<FixedItem> rows, LG_Zone zone, ZonePlacementWeights weights, out AIG_CourseNode? node)
    {
        node = null;
        if (_building is not { } build || build.Job.m_zone.Pointer != zone.Pointer
            || build.Queries >= build.Job.m_placementData.Count
            || build.Job.m_placementData[build.Queries].PlacementWeights?.Pointer != weights.Pointer
            || ZoneIndex.Coordinates(zone) is not { } address) return false;
        FixedItem? row = null;
        foreach (var candidate in rows)
        {
            if (candidate.TaskObject is not { Kind: FixedTaskObjectKind.Generator, Scope: FixedTaskObjectScope.Zone } role
                || role.Index != build.Queries || role.Count != build.Job.m_placementData.Count
                || candidate.Locator.Dimension != address.Dimension || candidate.Locator.Layer != address.Layer
                || candidate.Locator.LocalIndex != address.Zone) continue;
            if (row != null) return false;
            row = candidate;
        }
        if (row == null) return false;
        var index = build.Queries;
        node = FixedFunctionPoints.Pin(build.World, row, row.TaskObject!.Key(row.Locator), zone, ExpeditionFunction.PowerGenerator,
            item => Requests.TryGetValue(item.Pointer, out var slot) && slot.Zone == zone.Pointer && slot.Index == index);
        return node != null;
    }

    /// <summary>A zone's generator cluster request. LG_Distribute_FunctionPerZone.Build asks
    /// LG_DistributionJobUtils.GetRandomNodeFromZoneForFunction (0x1446871 to 0x144ed20, build 20403457), which picks an
    /// area by AreaDistributionWeights built from area size alone (ctor 0x1472ce0 reads LG_Area.m_size, never a marker);
    /// BuildGenericFunctionItems then asks only that area for a marker (0x1366555 to LG_Area.GetAndConsumeRandomMarkerSpawner)
    /// and drops the request when it has none. When the picked area has no cluster marker, the request moves to one of the
    /// zone's areas that has, chosen by the same native random value, so host and clients pick the same area. A zone with
    /// no cluster marker keeps the native pick and is reported by <see cref="ZonePlacementReport"/>. A cluster the author
    /// placed is pinned instead (<see cref="FixedFunctionPoints.PinCluster"/>).</summary>
    internal static AIG_CourseNode? ClusterNode(LG_Zone zone, float randomValue, AIG_CourseNode? picked)
    {
        if (picked?.m_area is { WasCollected: false } pickedArea && HasClusterMarker(pickedArea)) return picked;
        var hosts = new List<AIG_CourseNode>();
        if (zone.m_areas != null)
            foreach (var area in zone.m_areas)
                if (area != null && !area.WasCollected && area.m_courseNode is { WasCollected: false } node
                    && HasClusterMarker(area)) hosts.Add(node);
        if (hosts.Count == 0) return picked;
        var chosen = hosts[Math.Clamp((int)(randomValue * hosts.Count), 0, hosts.Count - 1)];
        if (MapLog.Logs(RuntimeLogLevel.Info) && ZoneIndex.Coordinates(zone) is { } at)
            MapLog.Record(RuntimeLogLevel.Info, ZonePlacementReport.LogCode,
                "Zone generator cluster request moved to an area that has a cluster marker.",
                RuntimeLogValue.Of("phase", "request-pin"), RuntimeLogValue.Of("function", nameof(ExpeditionFunction.GeneratorCluster)),
                RuntimeLogValue.Of("reason", "none"), RuntimeLogValue.Of("zone", $"{at.Dimension}:{at.Layer}:{at.Zone}"),
                RuntimeLogValue.Of("area", chosen.m_area?.name ?? "none"));
        return chosen;
    }

    private static bool HasClusterMarker(LG_Area area)
    {
        var groups = area.m_markerSpawnerGroups;
        var function = (int)ExpeditionFunction.GeneratorCluster;
        return groups != null && groups.Length > function && groups[function]?.m_spawners is { Count: > 0 };
    }

    /// <summary>A fixed terminal's native request is pinned to its authored room too. LG_Distribute_TerminalsPerZone.Build asks the same
    /// weighted node query once per TerminalPlacements entry (xref 0x1449f1e to 0x1450380, build 20403457) and stores the entry on the
    /// request (m_terminalPlacementData). The entry is found by its weights object, so a skipped or reordered entry never pins another
    /// terminal's room, and the claim matches the request by that same entry.</summary>
    internal static bool PinTerminalNode(long world, IReadOnlyList<FixedItem> rows, LG_Zone zone, ZonePlacementWeights weights,
        out AIG_CourseNode? node)
    {
        node = null;
        var placements = zone.m_settings?.m_zoneData?.TerminalPlacements;
        if (placements == null || ZoneIndex.Coordinates(zone) is not { } address) return false;
        var index = -1;
        for (var i = 0; i < placements.Count; i++)
        {
            if (placements[i]?.PlacementWeights?.Pointer != weights.Pointer) continue;
            if (index >= 0) return false;
            index = i;
        }
        if (index < 0) return false;
        FixedItem? row = null;
        foreach (var candidate in rows)
        {
            if (candidate.TaskObject is not { Kind: FixedTaskObjectKind.Terminal, Scope: FixedTaskObjectScope.Zone } role
                || role.Index != index || role.Count != placements.Count
                || candidate.Locator.Dimension != address.Dimension || candidate.Locator.Layer != address.Layer
                || candidate.Locator.LocalIndex != address.Zone) continue;
            if (row != null) return false;
            row = candidate;
        }
        if (row == null) return false;
        var entry = placements[index].Pointer;
        node = FixedFunctionPoints.Pin(world, row, row.TaskObject!.Key(row.Locator), zone, ExpeditionFunction.Terminal,
            item => item.m_terminalPlacementData?.Pointer == entry);
        return node != null;
    }

    /// <summary>A bulkhead controller's request comes from the detailed per-zone distribution of its function: build 20403457's
    /// LG_GenerateDistributionDataJob.Build (RVA 0x15D83E0, ISIL 298-308) constructs LG_Distribute_DetailedPlacedFunctionPerZone with
    /// func 2 (BulkheadDoorController) and queue 1 (PrioritizedFunctionItems), and that job's Build (RVA 0x1445A90) takes the node from
    /// LG_DistributionJobUtils.TryGetWeightedNodeFromZone (ISIL 096, out value at stack 0xA0) and passes it with m_func (+0x20, ISIL 121)
    /// to the LG_DistributeItem constructor (ISIL 131). A zone carries at most one controller, so the zone's row is the request's row.</summary>
    internal static bool PinControllerNode(long world, IReadOnlyList<FixedBulkheadController> rows, LG_Zone zone, out AIG_CourseNode? node)
    {
        node = null;
        if (FixedFunctionPoints.Detailed != ExpeditionFunction.BulkheadDoorController || ZoneIndex.Coordinates(zone) is not { } at
            || FixedPlacement.BulkheadControllerInZone(rows, at.Dimension, at.Layer, at.Zone) is not { } row) return false;
        node = FixedFunctionPoints.Pin(world, row, "controller/" + row.ZoneText, zone, ExpeditionFunction.BulkheadDoorController, _ => true);
        return node != null;
    }
    internal static void InvalidateCurrent()
    {
        if (_building is { } state) state.Invalid = true;
    }

    internal static void Complete(Build? state, bool success)
    {
        if (state == null || state.World != _world) return;
        var zone = state.Job.m_zone.Pointer;
        var count = state.Job.m_placementData.Count;
        var queue = Queue(state.Job);
        var valid = success && !state.Invalid && state.Queries == count && !Counts.ContainsKey(zone)
            && queue.Count == state.Before.Length + state.Selected.Count;
        for (var i = 0; valid && i < state.Before.Length; i++) valid = queue[i].Pointer == state.Before[i];
        var seen = new HashSet<IntPtr>();
        for (var i = 0; valid && i < state.Selected.Count; i++)
        {
            var request = queue[state.Before.Length + i];
            var selected = state.Selected[i];
            valid = request != null && !request.WasCollected && seen.Add(request.Pointer) && !Requests.ContainsKey(request.Pointer)
                && request.m_function == ExpeditionFunction.PowerGenerator && !request.m_isWardenObjective
                && request.m_assignedNode?.Pointer == selected.Node
                && request.m_markerSeedOffset == state.Job.m_placementData[selected.Index].MarkerSeedOffset;
        }
        Counts[zone] = valid ? count : -1;
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, PlacementLogCode, "Generator distribution request provenance readback.",
                RuntimeLogValue.Of("phase", "request-build"), RuntimeLogValue.Of("buildWorld", state.World),
                RuntimeLogValue.Of("zone", zone.ToInt64()), RuntimeLogValue.Of("count", count),
                RuntimeLogValue.Of("queries", state.Queries), RuntimeLogValue.Of("selected", state.Selected.Count),
                RuntimeLogValue.Of("queueBefore", state.Before.Length), RuntimeLogValue.Of("queueAfter", queue.Count),
                RuntimeLogValue.Of("valid", valid), RuntimeLogValue.Of("reason", valid ? "none"
                    : !success ? "native-build-false" : state.Invalid ? "node-query-invalid"
                    : state.Queries != count ? "node-query-count" : "request-readback-mismatch"));
        if (valid) for (var i = 0; i < state.Selected.Count; i++)
            Requests.Add(queue[state.Before.Length + i].Pointer, new Slot(_world, zone, state.Selected[i].Index, count));
    }

    internal static void End(Build? state)
    {
        if (state != null && ReferenceEquals(_building, state)) _building = state.Previous;
    }

    internal static Slot? BeginSpawn(LG_DistributeItem request)
    {
        var previous = _spawning;
        _spawning = Requests.TryGetValue(request.Pointer, out var slot) ? slot : null;
        if (_spawning != null) Consumed.Add((_spawning.Zone, _spawning.Index));
        if (request.m_function == ExpeditionFunction.PowerGenerator)
            RecordSlot("request-consumed", _spawning, request.Pointer, IntPtr.Zero,
                _spawning == null ? "request-untracked" : "none");
        return previous;
    }

    internal static Slot? BeginSpawn(LG_PopulateFunctionMarkersInZoneJob job, eDistributionQueue queue)
    {
        var previous = _spawning;
        _spawning = null;
        // This is the native FIFO request the patched job consumes, not a generator found in the scene.
        var requests = queue == eDistributionQueue.GenericFunctionItems ? job.m_distributionData?.GenericFunctionItems?.m_itemQueue : null;
        if (requests != null) foreach (var request in requests)
        {
            BeginSpawn(request);
            break;
        }
        return previous;
    }

    internal static void EndSpawn(Slot? previous) => _spawning = previous?.World == _world ? previous : null;

    internal static void SpawnedCore(LG_PowerGenerator_Core generator)
    {
        if (generator == null || generator.WasCollected) return;
        RecordSlot("core-setup", _spawning, IntPtr.Zero, generator.Pointer,
            _spawning == null ? "no-active-request" : _spawning.World != _world ? "spawn-world-mismatch" : "none");
        if (_spawning is not { } slot || slot.World != _world) return;
        var key = (slot.Zone, slot.Index);
        if (Bodies.TryGetValue(generator.Pointer, out var owner) && owner != slot)
        {
            Spawned[(owner.Zone, owner.Index)] = null;
            Spawned[key] = null;
            return;
        }
        Bodies[generator.Pointer] = slot;
        if (Spawned.TryGetValue(key, out var existing))
        {
            if (existing == null || existing.Pointer != generator.Pointer) Spawned[key] = null;
        }
        else Spawned.Add(key, generator);
    }

    private static void RecordSlot(string phase, Slot? slot, IntPtr request, IntPtr core, string reason)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Record(RuntimeLogLevel.Info, PlacementLogCode, "Generator native request and built body provenance.",
            RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("reason", reason),
            _world == long.MinValue ? RuntimeLogValue.Of("buildWorld", "unavailable") : RuntimeLogValue.Of("buildWorld", _world), slot == null ? RuntimeLogValue.Of("slotWorld", "unavailable") : RuntimeLogValue.Of("slotWorld", slot.World),
            RuntimeLogValue.Of("zone", slot?.Zone.ToInt64() ?? 0), RuntimeLogValue.Of("index", slot?.Index ?? -1),
            RuntimeLogValue.Of("count", slot?.Count ?? 0), RuntimeLogValue.Of("request", request.ToInt64()),
            RuntimeLogValue.Of("core", core.ToInt64()), RuntimeLogValue.Of("tracked", slot != null));
    }

    internal static bool Resolve(long epoch, LG_Zone zone, FixedTaskObjectRole role,
        out LG_PowerGenerator_Core? generator, out string refusal)
    {
        generator = null;
        refusal = "";
        if (_world != epoch) { refusal = "fixed-object-placement-world"; return false; }
        if (!Counts.TryGetValue(zone.Pointer, out var count)) { refusal = "fixed-object-placement-request-missing"; return false; }
        if (count < 0) { refusal = "fixed-object-placement-provenance-invalid"; return false; }
        if (count != role.Count) { refusal = "fixed-object-placement-count"; return false; }
        var key = (zone.Pointer, role.Index);
        if (!Spawned.TryGetValue(key, out var found))
        {
            // Name the stage this exact placement stopped at: the node query produced no request for it (the authored
            // room could not be pinned, or the native query failed), the request was never taken by a spawn job (no
            // PowerGenerator marker consumed it), or it was taken and built no generator.
            var requested = false;
            foreach (var slot in Requests.Values)
                if (slot.Zone == key.Item1 && slot.Index == key.Item2) { requested = true; break; }
            refusal = !requested ? "fixed-object-placement-node-unresolved"
                : !Consumed.Contains(key) ? "fixed-object-placement-request-unconsumed"
                : "fixed-object-placement-request-built-nothing";
            return false;
        }
        if (found == null) { refusal = "fixed-object-placement-ambiguous"; return false; }
        if (found.WasCollected) { refusal = "fixed-object-placement-collected"; return false; }
        generator = found;
        return true;
    }
}

[HarmonyPatch(typeof(LG_Distribute_DetailedPlacedFunctionPerZone), nameof(LG_Distribute_DetailedPlacedFunctionPerZone.Build))]
internal static class GeneratorPlacementRequests
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(LG_Distribute_DetailedPlacedFunctionPerZone __instance, out GeneratorPlacementBuild.Build? __state)
    {
        FixedFunctionPoints.Detailed = __instance.m_func;
        __state = MapHost.Session is { } session ? GeneratorPlacementBuild.Begin(session.WorldEpoch, __instance) : null;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(GeneratorPlacementBuild.Build? __state, bool __result)
        => GeneratorPlacementBuild.Complete(__state, __result);

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    internal static void Finalizer(GeneratorPlacementBuild.Build? __state)
    {
        FixedFunctionPoints.Detailed = ExpeditionFunction.None;
        GeneratorPlacementBuild.End(__state);
    }
}

[HarmonyPatch(typeof(LG_DistributionJobUtils), nameof(LG_DistributionJobUtils.TryGetWeightedNodeFromZone))]
internal static class GeneratorPlacementNodeResult
{
    // The decoded native query only writes fresh weights/random state and its out value.
    // Run it once before narrowing this exact authored placement; preserve its exceptions.
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Zone zone, ZonePlacementWeights placementWeights, ref AIG_CourseNode foundNode, ref bool __result)
    {
        var session = MapHost.Session;
        if (session == null)
        {
            GeneratorPlacementBuild.Selected(zone, placementWeights, foundNode, __result);
            ZonePlacementReport.Drawn(zone, placementWeights, foundNode, __result);
            return;
        }
        var selected = foundNode;
        var selectedResult = __result;
        session.Build(session.Placements, placements =>
        {
            try
            {
                var node = selected;
                var success = selectedResult;
                if (GeneratorPlacementBuild.PinNode(placements.GeneratorRows, zone, placementWeights, out var pinned)
                    || GeneratorPlacementBuild.PinTerminalNode(session.WorldEpoch, placements.GeneratorRows, zone, placementWeights, out pinned)
                    || GeneratorPlacementBuild.PinControllerNode(session.WorldEpoch, placements.ControllerRows, zone, out pinned))
                {
                    node = pinned!;
                    success = true;
                }
                GeneratorPlacementBuild.Selected(zone, placementWeights, node, success);
                selected = node;
                selectedResult = success;
            }
            catch
            {
                // A failed author lookup/readback cannot make the native query fail or leave an addressable
                // partial provenance record. The session logs the refusal; both native result values survive.
                GeneratorPlacementBuild.InvalidateCurrent();
                throw;
            }
        }, "generator placement node");
        foundNode = selected;
        __result = selectedResult;
        ZonePlacementReport.Drawn(zone, placementWeights, selected, selectedResult);
    }

}

[HarmonyPatch(typeof(LG_DistributionJobUtils), nameof(LG_DistributionJobUtils.GetRandomNodeFromZoneForFunction))]
internal static class GeneratorClusterZoneNode
{
    // The native pick runs first and keeps its weight and random-state writes. An objective request or a cluster the author
    // placed is pinned to its authored room; an unauthored cluster pick the builder would drop moves to an area with a cluster marker.
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Zone zone, ExpeditionFunction func, float randomValue, ref AIG_CourseNode __result)
    {
        if (zone == null || MapHost.Session is not { } session
            || FixedFunctionPoints.Objective == null && func != ExpeditionFunction.GeneratorCluster) return;
        AIG_CourseNode? placed = null;
        session.Build(session.Placements, placements => placed = func == ExpeditionFunction.GeneratorCluster
            ? FixedFunctionPoints.PinCluster(session.WorldEpoch, placements.GeneratorRows, zone)
            : FixedFunctionPoints.PinObjective(session.WorldEpoch, placements.GeneratorRows, zone), "fixed object point");
        if (placed != null) __result = placed;
        else if (func == ExpeditionFunction.GeneratorCluster && GeneratorPlacementBuild.ClusterNode(zone, randomValue, __result) is { } node) __result = node;
    }
}

[HarmonyPatch(typeof(LG_PowerGenerator_Core), nameof(LG_PowerGenerator_Core.Setup))]
internal static class GeneratorPlacementCore
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_PowerGenerator_Core __instance) => GeneratorPlacementBuild.SpawnedCore(__instance);
}
