using System;
using System.Collections.Generic;
using AIGraph;
using ForgeEngine.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Task objects the author placed in the map (zone terminals; zone, door-lock and objective generators; HSUs;
/// bulkhead controllers; generator clusters) are built by the game's own marker builder directly at the authored pose.
/// Nothing is cloned and nothing is moved afterwards, and the room needs no marker of the object's function.
///
/// Three steps, all on every machine from the same documents:
/// 1. Pin: when the native distribution picks the node of the row's request, the node becomes the course node of the
///    authored room's area nearest the authored point (<see cref="Pin"/>). The request keeps that node from then on.
/// 2. Claim: when <c>LG_PopulateFunctionMarkersInZoneJob.BuildGenericFunctionItems</c> is about to consume the request,
///    the row's own identity is matched against the queue head (<see cref="Claim"/>), and one marker point is built at
///    the authored pose the way a room's own point is made: a marker producer of the block's complex on an object at the
///    pose and the game's <c>LG_MarkerSpawner(producer, zone, area, seed, isSubMarker)</c>.
/// 3. Hand over: the job asks the request's area for a marker of the function
///    (<c>LG_Area.GetAndConsumeRandomMarkerSpawner</c>, which draws no random numbers); while the claim is current, that
///    one call answers the authored point (<see cref="Hand"/>). The job then draws the same numbers as a native success.
///
/// The point is never added to the area's marker groups, so no other request can draw it, and no other point of the area
/// is removed. The builder (<c>LG_FunctionMarkerBuilder.BuildMarkerSpawnerRec</c>, build 20403457) reads only the point's
/// own fields and adds only the sub-markers it creates to the area's groups.</summary>
internal static class FixedFunctionPoints
{
    internal const string NotPinned = "fixed-object-request-not-pinned";

    private sealed class Point
    {
        internal Point(FixedRow row, string role, ExpeditionFunction function, IntPtr zone, Func<LG_DistributeItem, bool> matches)
        { Row = row; Role = role; Function = function; Zone = zone; Matches = matches; }
        internal readonly FixedRow Row;
        internal readonly string Role;
        internal readonly ExpeditionFunction Function;
        internal readonly IntPtr Zone;
        internal readonly Func<LG_DistributeItem, bool> Matches;
        internal AIG_CourseNode? Node;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal string? Reason;
        internal bool Claimed;
        internal LG_MarkerSpawner? Spawner;
        internal bool Handed;
    }

    private static long _world = long.MinValue;
    private static readonly List<Point> Points = new();
    private static Point? _current;

    /// <summary>The objective request being distributed (<c>FindFunctionMarkerInZoneAndUseAsWardenObjective</c>), if any.</summary>
    internal static (int Layer, int Chain, ExpeditionFunction Function)? Objective;
    /// <summary>The function of the detailed per-zone distribution job being built, or <c>None</c>.</summary>
    internal static ExpeditionFunction Detailed;
    /// <summary>Whether <c>LG_Distribute_ProgressionPuzzles.Build</c> is running (door-lock generators).</summary>
    internal static bool Progression;

    internal static void Clear()
    {
        _world = long.MinValue;
        Points.Clear();
        _current = null;
        Objective = null;
        Detailed = ExpeditionFunction.None;
        Progression = false;
    }

    private static void Observe(long world)
    {
        if (_world == world) return;
        Clear();
        _world = world;
    }

    private static Point? Find(FixedRow row)
    {
        foreach (var point in Points) if (ReferenceEquals(point.Row, row)) return point;
        return null;
    }

    internal static ExpeditionFunction Function(FixedTaskObjectKind kind) => kind switch
    {
        FixedTaskObjectKind.Generator => ExpeditionFunction.PowerGenerator,
        FixedTaskObjectKind.Hsu => ExpeditionFunction.HydroStatisUnit,
        FixedTaskObjectKind.GeneratorCluster => ExpeditionFunction.GeneratorCluster,
        _ => ExpeditionFunction.Terminal
    };

    /// <summary>Pins one row's request to the authored room's area nearest the authored point, once per world. A row
    /// whose room or area cannot be named pins nothing; its reason is reported when the row is verified.</summary>
    internal static AIG_CourseNode? Pin(long world, FixedRow row, string role, LG_Zone zone, ExpeditionFunction function,
        Func<LG_DistributeItem, bool> matches)
    {
        Observe(world);
        if (Find(row) is { } known) return known.Node;
        var point = new Point(row, role, function, zone.Pointer, matches);
        Points.Add(point);
        if (!RoomResolver.TryPlace(world, row.Locator, out var room, out var refusal)) { point.Reason = refusal ?? "room-unresolved"; return null; }
        var pose = row.WorldPose(room!.Pose);
        point.Position = new Vector3((float)pose.Position[0], (float)pose.Position[1], (float)pose.Position[2]);
        point.Rotation = new Quaternion((float)pose.Rotation[0], (float)pose.Rotation[1], (float)pose.Rotation[2], (float)pose.Rotation[3]);
        var best = float.MaxValue;
        if (zone.m_areas != null)
            foreach (var area in zone.m_areas)
            {
                if (area == null || area.WasCollected || area.m_geomorph == null
                    || area.m_geomorph.GetInstanceID() != room.GeomorphInstanceId
                    || area.m_courseNode is not { WasCollected: false } node) continue;
                var distance = Distance(area, point.Position);
                if (distance < best) { best = distance; point.Node = node; }
            }
        if (point.Node == null) { point.Reason = "fixed-object-room-area-missing"; return null; }
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, ZonePlacementReport.LogCode, "Fixed object request pinned to the authored room's area nearest the authored point.",
                RuntimeLogValue.Of("phase", "request-pin"), RuntimeLogValue.Quote("id", row.Id), RuntimeLogValue.Of("role", role),
                RuntimeLogValue.Of("function", function.ToString()), RuntimeLogValue.Of("reason", "none"),
                RuntimeLogValue.Of("zone", $"{row.Locator.Dimension}:{row.Locator.Layer}:{row.Locator.LocalIndex}"),
                RuntimeLogValue.Of("area", point.Node.m_area?.name ?? "none"));
        return point.Node;
    }

    /// <summary>Pins the next objective row of the objective request being distributed: the lowest unpinned row of that
    /// layer, chain and function standing in this zone.</summary>
    internal static AIG_CourseNode? PinObjective(long world, IReadOnlyList<FixedItem> rows, LG_Zone zone)
    {
        if (Objective is not { } objective || ZoneIndex.Coordinates(zone) is not { } at) return null;
        Observe(world);
        FixedItem? next = null;
        foreach (var row in rows)
            if (row.TaskObject is { Scope: FixedTaskObjectScope.Objective, Receiver: false } role
                && role.Kind is FixedTaskObjectKind.Generator or FixedTaskObjectKind.Hsu
                && Function(role.Kind) == objective.Function && role.Layer == objective.Layer && role.ChainIndex == objective.Chain
                && row.Locator.Dimension == at.Dimension && row.Locator.Layer == at.Layer && row.Locator.LocalIndex == at.Zone
                && Find(row) == null && (next == null || role.Index < next.TaskObject!.Index)) next = row;
        if (next == null) return null;
        // The claim also requires the request's area to be the pinned one; rows pinned to one area claim in pin order.
        var chain = objective.Chain;
        return Pin(world, next, next.TaskObject!.Key(next.Locator), zone, objective.Function,
            item => item.m_isWardenObjective && item.m_wardenObjectiveChainIndex == chain);
    }

    /// <summary>The objective request that reuses a distribution item the zone already holds
    /// (<see cref="FixedObjectiveExistingDistribution"/>): the next objective row is pinned as for a drawn request and the
    /// item is given the pinned node, so the claim matches it in the pinned area. Null when no row stands in this zone.</summary>
    internal static AIG_CourseNode? PinExisting(long world, IReadOnlyList<FixedItem> rows, LG_Zone zone, LG_DistributeItem item)
    {
        if (PinObjective(world, rows, zone) is not { } node) return null;
        item.m_assignedNode = node;
        return node;
    }

    /// <summary>The zone's generator cluster row, which the site writes at most once per zone.</summary>
    internal static AIG_CourseNode? PinCluster(long world, IReadOnlyList<FixedItem> rows, LG_Zone zone)
    {
        if (ZoneIndex.Coordinates(zone) is not { } at) return null;
        foreach (var row in rows)
            if (row.TaskObject is { Kind: FixedTaskObjectKind.GeneratorCluster } role && row.Locator.Dimension == at.Dimension
                && row.Locator.Layer == at.Layer && row.Locator.LocalIndex == at.Zone)
                return Pin(world, row, role.Key(row.Locator), zone, ExpeditionFunction.GeneratorCluster, _ => true);
        return null;
    }

    /// <summary>A door-lock generator. Build 20403457's <c>LG_Distribute_ProgressionPuzzles.Build</c> (RVA 0x14479C0) calls
    /// <c>AIG_CoursePortal.GetNodeForOppositeZone(zone)</c> on the locked zone's source gate portal (ISIL 211-219), passes the returned
    /// node unchanged as the node of <c>new LG_DistributeItem(6, amount, node, gate)</c> (ISIL 220-231), writes the door's gate
    /// (+0x38) to the request's m_assignedGate (+0x28, ISIL 234-238) and enqueues it in that node's zone's GenericFunctionItems
    /// (ISIL 240-). So the returned node is the request's node, and the pin stays in that zone.</summary>
    internal static AIG_CourseNode? PinDoor(long world, IReadOnlyList<FixedItem> rows, AIG_CoursePortal portal, LG_Zone locked,
        AIG_CourseNode? native)
    {
        if (!Progression || native?.m_zone is not { } zone || ZoneIndex.Coordinates(locked) is not { } at
            || MapObjectDoorAddress.Create(at.Dimension, at.Layer, at.Zone) is not { } door || portal.m_gate is not { } gate) return null;
        var pointer = gate.Pointer;
        foreach (var row in rows)
            if (row.TaskObject is { Kind: FixedTaskObjectKind.Generator, Scope: FixedTaskObjectScope.Door } role && role.TargetDoor == door)
                return Pin(world, row, role.Key(row.Locator), zone, ExpeditionFunction.PowerGenerator, item => item.m_assignedGate?.Pointer == pointer);
        return null;
    }

    /// <summary>Called with the head of the queue the job is about to consume. Claims the first unclaimed point whose
    /// row this request is and builds its marker point.</summary>
    internal static void Claim(LG_PopulateFunctionMarkersInZoneJob job, LG_DistributeItem? item)
    {
        _current = null;
        if (item == null || item.WasCollected || job.m_zone == null || item.m_assignedNode?.m_area is not { } area) return;
        Point? point = null;
        foreach (var candidate in Points)
            if (!candidate.Claimed && candidate.Node != null && candidate.Zone == job.m_zone.Pointer && candidate.Function == item.m_function
                && candidate.Node.m_area?.Pointer == area.Pointer && candidate.Matches(item)) { point = candidate; break; }
        if (point == null) return;
        point.Claimed = true;
        if (Block(point.Function) is not { } block) point.Reason = "fixed-object-prefab-not-loaded";
        else
        {
            var holder = new GameObject("fixed-" + point.Function);
            holder.transform.SetParent(area.transform, false);
            holder.transform.SetPositionAndRotation(point.Position, point.Rotation);
            if (RoomRecipeMarkers.AddProducer(holder, block.Producer, block.Id) is { } producer)
            {
                producer.m_disableRotationWhenFunction = true;
                var spawner = new LG_MarkerSpawner(producer, job.m_zone, area, Seed(point.Row.Id), false);
                if (spawner.HasFunction(point.Function)) point.Spawner = spawner;
            }
            if (point.Spawner == null) { UnityEngine.Object.Destroy(holder); point.Reason = "fixed-object-point-refused"; }
        }
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, ZonePlacementReport.LogCode, point.Spawner == null
                    ? "Fixed object request claimed but no point was built; the request keeps the native draw."
                    : "Fixed object request claimed; the native builder builds it at the authored point.",
                RuntimeLogValue.Of("phase", "request-claim"), RuntimeLogValue.Quote("id", point.Row.Id), RuntimeLogValue.Of("role", point.Role),
                RuntimeLogValue.Of("function", point.Function.ToString()), RuntimeLogValue.Of("reason", point.Reason ?? "none"),
                RuntimeLogValue.Of("zone", $"{point.Row.Locator.Dimension}:{point.Row.Locator.Layer}:{point.Row.Locator.LocalIndex}"),
                RuntimeLogValue.Of("area", area.name ?? "none"), RuntimeLogValue.Of("isWardenObjective", item.m_isWardenObjective),
                RuntimeLogValue.Of("chain", item.m_wardenObjectiveChainIndex));
        if (point.Spawner != null) _current = point;
    }

    internal static void Release() => _current = null;

    /// <summary>The one marker draw of the claimed request: its area and function answer the authored point.</summary>
    internal static bool Hand(LG_Area area, ExpeditionFunction function, ref LG_MarkerSpawner result)
    {
        if (_current is not { Handed: false, Spawner: { } spawner } point || point.Function != function
            || point.Node?.m_area?.Pointer != area.Pointer) return false;
        point.Handed = true;
        result = spawner;
        return true;
    }

    /// <summary>The object the native builder built at this row's point, or the reason there is none.</summary>
    internal static string? Built(long world, FixedRow row, out GameObject? spawned)
    {
        spawned = null;
        if (_world != world || Find(row) is not { } point) return NotPinned;
        if (point.Reason != null) return point.Reason;
        if (!point.Handed) return "fixed-object-point-unused";
        if (point.Spawner?.m_spawnedGO is not { WasCollected: false } built) return "fixed-object-point-built-nothing";
        spawned = built;
        return null;
    }

    /// <summary>How far the point is from what the area draws; an area that draws nothing counts from its own origin.</summary>
    private static float Distance(LG_Area area, Vector3 point)
    {
        var best = (area.transform.position - point).sqrMagnitude;
        foreach (var renderer in area.GetComponentsInChildren<Renderer>(false))
            if (renderer != null) best = Math.Min(best, renderer.bounds.SqrDistance(point));
        return best;
    }

    /// <summary>The first enabled Mining, Tech or Service marker block, by table order, every one of whose compositions of
    /// the function has its prefab loaded, with a producer class of the block's own table (a producer reads its id from
    /// the table of its complex).</summary>
    private static (uint Id, string Producer)? Block(ExpeditionFunction function)
    {
        foreach (var block in MiningMarkerDataBlock.GetAllBlocks())
            if (block != null && block.internalEnabled && Offers(block.GetCompositions(), function)) return (block.persistentID, "MiningDigSiteMarkerProducer");
        foreach (var block in TechMarkerDataBlock.GetAllBlocks())
            if (block != null && block.internalEnabled && Offers(block.GetCompositions(), function)) return (block.persistentID, "TechLabMarkerProducer");
        foreach (var block in ServiceMarkerDataBlock.GetAllBlocks())
            if (block != null && block.internalEnabled && Offers(block.GetCompositions(), function)) return (block.persistentID, "ServiceFloodwaysMarkerProducer");
        return null;
    }

    private static bool Offers(Il2CppSystem.Collections.Generic.List<MarkerComposition>? rows, ExpeditionFunction function)
    {
        if (rows == null) return false;
        var any = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not { } row || row.function != function) continue;
            if (string.IsNullOrEmpty(row.prefab) || ForgeCore.GameAssets.Get<GameObject>(row.prefab) is not { WasCollected: false }) return false;
            any = true;
        }
        return any;
    }

    /// <summary>The same seed on every machine: FNV-1a of the row's id.</summary>
    private static uint Seed(string id)
    {
        var hash = 2166136261u;
        foreach (var c in id) hash = (hash ^ c) * 16777619u;
        return hash;
    }
}

/// <summary>The marker draw of a claimed request (<see cref="FixedFunctionPoints.Hand"/>); every other draw runs natively.</summary>
[HarmonyPatch(typeof(LG_Area), nameof(LG_Area.GetAndConsumeRandomMarkerSpawner))]
internal static class FixedFunctionPointHandover
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(LG_Area __instance, ExpeditionFunction func, ref LG_MarkerSpawner __result)
        => !FixedFunctionPoints.Hand(__instance, func, ref __result);
}

/// <summary>Names the objective request being distributed, so its node pick pins the matching objective row.</summary>
[HarmonyPatch(typeof(LG_Distribute_WardenObjective), nameof(LG_Distribute_WardenObjective.FindFunctionMarkerInZoneAndUseAsWardenObjective))]
internal static class FixedObjectiveRequest
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(LG_Distribute_WardenObjective __instance, ExpeditionFunction func, int chainIndex)
        => FixedFunctionPoints.Objective = __instance.m_layer is { } layer ? ((int)layer.m_type, chainIndex, func) : null;

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    internal static void Finalizer() => FixedFunctionPoints.Objective = null;
}

/// <summary>The objective request that reuses a distribution the zone already holds. Build 20403457's
/// <c>FindFunctionMarkerInZoneAndUseAsWardenObjective</c> first calls <c>TryGetExistingZoneFunctionDistribution</c>
/// (ISIL 077); when the zone pre-allocated items of the function (an objective HSU in a zone whose <c>HSUsInZone</c> is
/// above zero), it marks the found item as the objective (ISIL 254, 260) and never draws a node through
/// <c>GetRandomNodeFromZoneForFunction</c>. The found item's node is then the request's node, so the objective row is
/// pinned here: the item is assigned the pinned node, and the claim and handover run as for a drawn request.</summary>
[HarmonyPatch(typeof(LG_DistributionJobUtils), nameof(LG_DistributionJobUtils.TryGetExistingZoneFunctionDistribution))]
internal static class FixedObjectiveExistingDistribution
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_Zone zone, ref LG_DistributeItem foundDist, ref AIG_CourseNode foundDistNode, bool __result)
    {
        if (!__result || FixedFunctionPoints.Objective == null || zone == null || foundDist == null || MapHost.Session is not { } session) return;
        AIG_CourseNode? pinned = null;
        var dist = foundDist;
        session.Build(session.Placements, placements =>
            pinned = FixedFunctionPoints.PinExisting(session.WorldEpoch, placements.GeneratorRows, zone, dist), "fixed object existing distribution");
        if (pinned != null) foundDistNode = pinned;
    }
}

/// <summary>The door-lock generator request's node (<see cref="FixedFunctionPoints.PinDoor"/>); outside the progression
/// puzzle build this returns at once.</summary>
[HarmonyPatch(typeof(AIG_CoursePortal), nameof(AIG_CoursePortal.GetNodeForOppositeZone))]
internal static class FixedDoorGeneratorNode
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(AIG_CoursePortal __instance, LG_Zone zone, ref AIG_CourseNode __result)
    {
        if (!FixedFunctionPoints.Progression || MapHost.Session is not { } session || zone == null) return;
        AIG_CourseNode? pinned = null;
        var native = __result;
        session.Build(session.Placements, placements =>
            pinned = FixedFunctionPoints.PinDoor(session.WorldEpoch, placements.GeneratorRows, __instance, zone, native), "door generator point");
        if (pinned != null) __result = pinned;
    }
}
