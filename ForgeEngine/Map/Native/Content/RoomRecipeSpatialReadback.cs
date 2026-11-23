using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Read native output only; never supplies synthetic nodes, portals, clusters or noise rules.</summary>
internal static class RoomRecipeSpatialReadback
{
    /// <summary>Per placed recipe room: each authored part's native OnBuild result, or its placed clone.</summary>
    private static readonly Dictionary<IntPtr, Dictionary<string, UnityEngine.GameObject>> BuiltParts = new();
    /// <summary>The LG_PrefabSpawner.OnBuild postfix. A cloned authored spawner still carries its part id as its name
    /// until native destroys it after the build. Never throws into the native trampoline.</summary>
    internal static void Built(LG_PrefabSpawner spawner, UnityEngine.GameObject? result)
    {
        var area = spawner.m_area; var room = area?.m_geomorph;
        if (room == null || result == null) return;
        var recipe = RoomRecipeHost.RecipeFor(area);
        if (recipe == null) return;
        string id = spawner.gameObject.name;
        if (!recipe.HasPart(id)) return; // seal panels and preserved donor spawners are not authored parts
        if (!BuiltParts.TryGetValue(room.Pointer, out var parts)) BuiltParts.Add(room.Pointer, parts = new(StringComparer.Ordinal));
        parts[id] = result;
    }
    /// <summary>Immediate developer inspection of the same generation receipts, not another registry or loader.
    /// ForgeDevelopment reads it through the public MapReadOnlyViews; Map never depends on that assembly.
    /// Missing builds remain absent so the observer can report their exact authored ids.</summary>
    internal static (string Recipe, IReadOnlyDictionary<string, UnityEngine.GameObject> Parts)? ReadBuiltParts(LG_Geomorph room)
    {
        var recipe = room.m_areas.Length == 0 ? null : RoomRecipeHost.RecipeFor(room.m_areas[0]);
        if (recipe == null) return null;
        return (JsonSerializer.Serialize(recipe, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            PartsOf(room, recipe).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
    /// <summary>Spawner parts report through OnBuild. Every other cloned vanilla object (a marker producer, a baked mesh,
    /// a light, a collider) never builds: it is placed when its clone stands under its area with its part id right after
    /// native placed the room, before marker producers or later jobs consume it.</summary>
    internal static void Placed(LG_Geomorph room, RoomBuildRecipe recipe) => PartsOf(room, recipe);
    /// <summary>The object of every authored part native has built or placed so far, by part id.</summary>
    internal static Dictionary<string, UnityEngine.GameObject> InstancesOf(LG_Geomorph room, RoomBuildRecipe recipe)
        => PartsOf(room, recipe).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static Dictionary<string, UnityEngine.GameObject> PartsOf(LG_Geomorph room, RoomBuildRecipe recipe)
    {
        if (!BuiltParts.TryGetValue(room.Pointer, out var parts)) BuiltParts.Add(room.Pointer, parts = new(StringComparer.Ordinal));
        foreach (var part in recipe.Parts)
        {
            if (parts.ContainsKey(part.Id)) continue;
            int index = recipe.AreaIndex(part.Area);
            var placed = index < 0 || index >= room.m_areas.Length ? null : room.m_areas[index].transform.Find("EnvProps/" + part.Id);
            if (placed != null && placed.GetComponent<LG_PrefabSpawner>() == null)
                parts[part.Id] = placed.gameObject;
        }
        return parts;
    }
    /// <summary>Per course node: how many portals the native cluster scan touched (`CoursePortalsHit.Count`) when it ran
    /// `MatchWithPortals`. The scan data dies with its job, so this postfix is the only place the count can be read.</summary>
    private static readonly Dictionary<IntPtr, int> PortalsHit = new();
    /// <summary>Which portals of that node the scan touched, so a miss names the doorway instead of only counting it.</summary>
    private static readonly Dictionary<IntPtr, HashSet<IntPtr>> PortalsHitIdentity = new();
    // Copy only diagnostic values while the native scan job still exists. A failed result cluster is not
    // matched to the course node unless RoomRecipeHost.AcceptPartialScan keeps it, so FactoryDone cannot recover its size or its source from that node.
    private sealed record ScanReceipt(bool Matched, int Nodes, int SourceLinks, string SourcePosition, string Bounds, string Error, UnityEngine.Vector3 Low = default, UnityEngine.Vector3 High = default, HashSet<(int, int)>? Visited = null);
    private static readonly Dictionary<IntPtr, ScanReceipt> Scans = new();
    private sealed record SourceReceipt(bool Resolved, bool Hit, string Origin, string Position, string Collider, int Layer, string Reason);
    private static readonly Dictionary<IntPtr, SourceReceipt> Sources = new();
    /// <summary>Copy the original GetSourceNode probe before another job overwrites its static hit. Build 20403457
    /// rays down 6 m with MASK_NODE_GENERATION; its Raycast clears the out hit on a miss. A normal false return
    /// therefore means either no floor hit or no voxel near the hit. Never repeats physics or changes the scan.</summary>
    internal static void SourceResolved(AIGraph.AIG_NodeClusterTools.NodeClusterScanData scan, bool resolved)
    {
        var node = scan.m_sourceCourseNode; var room = node?.m_area?.m_geomorph;
        if (node == null || room == null || RoomRecipeHost.RecipeFor(node.m_area) == null) return;
        try
        {
            var hit = AIGraph.AIG_NodeClusterTools.NodeClusterScanData.s_hit;
            var collider = hit.collider;
            bool found = collider != null;
            Sources[node.Pointer] = new(resolved, found,
                Position(room.transform.InverseTransformPoint(scan.m_graphSource.transform.position)),
                found ? Position(room.transform.InverseTransformPoint(hit.point)) : "unavailable",
                found ? Cut(collider!.name, 96) : "unavailable", found ? collider!.gameObject.layer : -1,
                resolved ? "resolved" : found ? "voxel-node-unavailable" : "down-ray-missed");
        }
        catch (Exception error)
        {
            Sources[node.Pointer] = new(resolved, false, "unavailable", "unavailable", "unavailable", -1, "read-error:" + error.GetType().Name);
        }
    }
    internal static void Scanned(AIGraph.AIG_NodeClusterTools.NodeClusterScanData scan, bool matched)
    {
        var node = scan.m_sourceCourseNode; var hit = scan.CoursePortalsHit;
        if (node == null || hit == null) return;
        PortalsHit[node.Pointer] = hit.Count;
        var identity = new HashSet<IntPtr>();
        for (int index = 0; index < hit.Count; index++) if (hit[index] != null) identity.Add(hit[index].Pointer);
        PortalsHitIdentity[node.Pointer] = identity;
        var room = node.m_area?.m_geomorph;
        if (room == null || RoomRecipeHost.RecipeFor(node.m_area) == null) return;
        try
        {
            var source = scan.m_sourceNode;
            var built = scan.m_clusterResult?.m_nodes;
            UnityEngine.Vector3? low = null, high = null; var visited = new HashSet<(int, int)>();
            if (built != null) for (int index = 0; index < built.Count; index++)
            {
                var position = room.transform.InverseTransformPoint(built[index].Position);
                visited.Add(((int)Math.Floor(built[index].Position.x), (int)Math.Floor(built[index].Position.z)));
                low = low is { } min ? new(Math.Min(min.x, position.x), Math.Min(min.y, position.y), Math.Min(min.z, position.z)) : position;
                high = high is { } max ? new(Math.Max(max.x, position.x), Math.Max(max.y, position.y), Math.Max(max.z, position.z)) : position;
            }
            Scans[node.Pointer] = new(matched, built?.Count ?? -1, source?.Links?.Count ?? -1,
                source == null ? "unavailable" : Position(room.transform.InverseTransformPoint(source.Position)),
                low is { } minimum && high is { } maximum ? Position(minimum) + ".." + Position(maximum) : "empty", "none", low ?? default, high ?? default, visited);
        }
        catch (Exception error)
        {
            Scans[node.Pointer] = new(matched, -1, -1, "unavailable", "unavailable", error.GetType().Name);
        }
    }
    /// <summary>The gates of this node whose portal the native scan did not touch, as "gate:other area".</summary>
    private static string MissedPortals(AIGraph.AIG_CourseNode? node)
    {
        try { return MissedPortalsUnchecked(node); }
        catch (Exception exception) { return "unreadable:" + exception.GetType().Name; }
    }
    /// <summary>For every portal the scan missed: the gate's position and the colliders within 4 m of it at walking height, so a miss
    /// can be told from a wall, a prop or a door frame standing in the way. Read only; bounded to 12 colliders per portal.</summary>
    private static void MissedPortalObstacles(LG_Geomorph room, AIGraph.AIG_CourseNode? node, RoomBuildRecipe recipe, int index)
    {
        try
        {
            if (node?.m_portals == null || !PortalsHitIdentity.TryGetValue(node.Pointer, out var hit)) return;
            for (int portalIndex = 0; portalIndex < node.m_portals.Count; portalIndex++)
            {
                var portal = node.m_portals[portalIndex];
                if (portal == null || hit.Contains(portal.Pointer) || portal.m_gate == null) continue;
                var center = portal.m_gate.transform.position;
                var found = UnityEngine.Physics.OverlapSphere(center + UnityEngine.Vector3.up * 1f, 4f);
                int shown = 0;
                foreach (var collider in found)
                {
                    if (collider.isTrigger || shown >= 12) continue;
                    var chain = new System.Text.StringBuilder(); var walk = collider.transform;
                    for (int depth = 0; walk != null && depth < 3; depth++, walk = walk.parent) chain.Append(Cut(walk.name, 18)).Append('<');
                    // One row per collider: one list would be cut at the log's value length, after the first collider.
                    // log-values-max: 9 -- resource, phase, area, portal index, gate position, collider chain, layer, centre and size.
                    MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.NavigationReadback, "Solid collider within 4 m of a gate the cluster scan never reached; room-local.",
                        RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("phase", "portal-obstacles"),
                        RuntimeLogValue.Of("area", index < recipe.Areas.Count ? recipe.Areas[index].Id : "unresolved"), RuntimeLogValue.Of("portalIndex", portalIndex),
                        RuntimeLogValue.Of("gatePosition", Position(room.transform.InverseTransformPoint(center))), RuntimeLogValue.Quote("collider", chain.ToString()),
                        RuntimeLogValue.Of("layer", collider.gameObject.layer), RuntimeLogValue.Of("center", Position(room.transform.InverseTransformPoint(collider.bounds.center))),
                        RuntimeLogValue.Of("size", Position(collider.bounds.size)));
                    shown++;
                }
            }
        }
        catch (Exception error) { MapLog.Contained("room-recipe-spatial-readback.missed-portal-obstacles", error); }
    }
    private static string Cut(string text, int length) => text.Length > length ? text.Substring(0, length) : text;
    private static string MissedPortalsUnchecked(AIGraph.AIG_CourseNode? node)
    {
        if (node?.m_portals == null || !PortalsHitIdentity.TryGetValue(node.Pointer, out var hit)) return "unscanned";
        var missed = new List<string>();
        for (int index = 0; index < node.m_portals.Count; index++)
        {
            var portal = node.m_portals[index];
            if (portal == null || hit.Contains(portal.Pointer)) continue;
            var other = portal.m_nodeA != null && portal.m_nodeA.Pointer == node.Pointer ? portal.m_nodeB : portal.m_nodeA;
            missed.Add(Cut(portal.m_gate?.name ?? "no-gate", 26) + ":" + Cut(other?.m_area?.name ?? "no-area", 14));
        }
        return missed.Count == 0 ? "none" : string.Join(";", missed);
    }
    /// <summary>Parts a room instance left out on purpose (<see cref="RoomRecipeHost.RemoveParts"/>): not built, and not a failure.</summary>
    private static readonly Dictionary<IntPtr, HashSet<string>> RemovedParts = new();
    internal static void Removed(LG_Geomorph room, string[] parts) => RemovedParts[room.Pointer] = new(parts, StringComparer.Ordinal);
    internal static bool IsRemoved(LG_Geomorph room, string part) => RemovedParts.TryGetValue(room.Pointer, out var parts) && parts.Contains(part);
    internal static void Clear() { RemovedParts.Clear(); BuiltParts.Clear(); PortalsHit.Clear(); PortalsHitIdentity.Clear(); Scans.Clear(); Sources.Clear(); }
    /// <summary>One record per donor: authored parts, parts native built, and the colliders and active mesh renderers those builds
    /// carry once the factory is done (a whole cloned prefab counts what its nested spawners built inside it). A part that never built is a structural
    /// failure; the collider count is evidence only, never a gate.</summary>
    internal static bool CheckParts(LG_Geomorph room, RoomBuildRecipe recipe, List<RuntimeLogValue>? failure = null)
    {
        var built = PartsOf(room, recipe);
        var locator = GeneratedRoomRegistry.LocatorFor(room);
        bool valid = true, reported = false;
        foreach (var donor in recipe.Parts.GroupBy(part => part.Prefab, StringComparer.Ordinal))
        {
            int parts = 0, placed = 0, colliders = 0, renderers = 0;
            foreach (var part in donor)
            {
                if (IsRemoved(room, part.Id)) continue;
                parts++;
                if (built.TryGetValue(part.Id, out var output) && output != null && !output.WasCollected)
                {
                    placed++;
                    colliders += output.GetComponentsInChildren<UnityEngine.Collider>(true).Length;
                    renderers += output.GetComponentsInChildren<UnityEngine.MeshRenderer>(false).Length;
                }
                else if (!reported)
                {
                    reported = true;
                    failure?.AddRange(new[] { RuntimeLogValue.Quote("part", part.Id), RuntimeLogValue.Of("partReadback",
                        "expectedPosition=" + Position(part.Position) + ";actualPosition=not-built;deviation=unavailable;built=False/True") });
                }
            }
            bool donorValid = placed == parts;
            if (!donorValid) valid = false;
            if (MapLog.Logs(RuntimeLogLevel.Info)) MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.Parts,
                "Native spawner builds of one donor's authored parts; collision behavior requires game checks.",
                RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("phase", "factory-done"),
                RuntimeLogValue.Of("roomInstance", locator?.InstanceId ?? "unregistered"),
                RuntimeLogValue.Of("sourcePrefab", donor.Key), RuntimeLogValue.Of("parts", parts),
                RuntimeLogValue.Of("built", placed), RuntimeLogValue.Of("colliders", colliders), RuntimeLogValue.Of("renderers", renderers), RuntimeLogValue.Of("valid", donorValid));
        }
        return valid;
    }
    private static string Position(float[] position) => FormattableString.Invariant($"({position[0]:0.###},{position[1]:0.###},{position[2]:0.###})");
    private static string Position(UnityEngine.Vector3 position) => FormattableString.Invariant($"({position.x:0.###},{position.y:0.###},{position.z:0.###})");
    private static void NavigationDetail(LG_Geomorph room, LG_Area area, RoomBuildRecipe recipe, int index, RuntimeLogLevel level, bool valid)
    {
        if (!MapLog.Logs(level)) return;
        try { NavigationDetailUnchecked(room, area, recipe, index, level, valid); }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, RoomRecipeCodes.NavigationReadback, "Native navigation diagnostic read failed; structural refusal is unchanged.",
                RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("area", index < recipe.Areas.Count ? recipe.Areas[index].Id : "unresolved"),
                RuntimeLogValue.Of("phase", "read-failed"), RuntimeLogValue.Of("readError", error.GetType().Name));
        }
    }
    private static void NavigationDetailUnchecked(LG_Geomorph room, LG_Area area, RoomBuildRecipe recipe, int index, RuntimeLogLevel level, bool valid)
    {
        var node = area.m_courseNode;
        ScanReceipt? scan = null;
        if (node != null) Scans.TryGetValue(node.Pointer, out scan);
        var identity = GeneratedRoomRegistry.LocatorFor(room);
        var definition = index < recipe.Areas.Count ? recipe.Areas[index] : null;
        var common = new[] { RuntimeLogValue.Of("resource", recipe.ResourceKey),
            RuntimeLogValue.Of("roomInstance", identity?.InstanceId ?? "unregistered"),
            RuntimeLogValue.Of("area", definition?.Id ?? "unresolved") };
        MapLog.Record(level, RoomRecipeCodes.NavigationReadback, valid ? "Native navigation scan readback of a valid area; coordinates are room-local." : "Native navigation scan failed structural readback; coordinates are room-local.",
            common.Concat(new[] {
                RuntimeLogValue.Of("phase", "scan"), RuntimeLogValue.Of("valid", valid), RuntimeLogValue.Of("scanObserved", scan != null),
                RuntimeLogValue.Of("matched", scan?.Matched ?? false), RuntimeLogValue.Of("scanNodes", scan?.Nodes ?? -1),
                RuntimeLogValue.Of("sourceLinks", scan?.SourceLinks ?? -1), RuntimeLogValue.Of("sourceNode", scan?.SourcePosition ?? "unavailable"),
                RuntimeLogValue.Of("scanBounds", scan?.Bounds ?? "unavailable"), RuntimeLogValue.Of("readError", scan?.Error ?? "unobserved"),
                RuntimeLogValue.Of("graphSource", definition == null ? "unresolved" : Position(definition.GraphSource)),
                RuntimeLogValue.Of("portals", node?.m_portals?.Count ?? -1),
                RuntimeLogValue.Of("portalsHit", node != null && PortalsHit.TryGetValue(node.Pointer, out var count) ? count : -1)
            }).ToArray());
        SourceReceipt? source = null;
        if (node != null) Sources.TryGetValue(node.Pointer, out source);
        MapLog.Record(level, RoomRecipeCodes.NavigationReadback, "Original native source probe, captured before its static hit is reused; coordinates are room-local.",
            common.Concat(new[] {
                RuntimeLogValue.Of("phase", "source"), RuntimeLogValue.Of("sourceObserved", source != null),
                RuntimeLogValue.Of("sourceResolved", source?.Resolved ?? false), RuntimeLogValue.Of("origin", source?.Origin ?? "unavailable"),
                RuntimeLogValue.Of("rayHit", source?.Hit ?? false), RuntimeLogValue.Of("hitPosition", source?.Position ?? "unavailable"),
                RuntimeLogValue.Of("hitCollider", source?.Collider ?? "unavailable"), RuntimeLogValue.Of("hitLayer", source?.Layer ?? -1),
                RuntimeLogValue.Of("sourceFailure", source?.Reason ?? "unobserved")
            }).ToArray());
        VoxelRun(room, area, node, definition, common, level);
        if (scan != null && scan.Nodes > 0) BeyondScan(room, scan, common, level);
        if (node?.m_portals == null) return;
        for (int portalIndex = 0; portalIndex < node.m_portals.Count; portalIndex++)
        {
            var portal = node.m_portals[portalIndex];
            if (portal == null) continue;
            var gate = portal.m_gate;
            var other = portal.m_nodeA?.Pointer == node.Pointer ? portal.m_nodeB : portal.m_nodeA;
            // Portal identity, its native position and the connector gate are independent observations.
            // An exterior portal may be between rooms: never compare its position to a socket as an equality gate.
            MapLog.Record(level, RoomRecipeCodes.NavigationReadback, valid ? "Native portal and gate readback for a valid area; coordinates are room-local." : "Native portal and gate readback for the failed area; coordinates are room-local.",
                common.Concat(new[] { RuntimeLogValue.Of("phase", "portal"), RuntimeLogValue.Of("portalIndex", portalIndex),
                    RuntimeLogValue.Of("hit", PortalsHitIdentity.TryGetValue(node.Pointer, out var touched) && touched.Contains(portal.Pointer)),
                    RuntimeLogValue.Quote("gate", gate?.name ?? "missing"), RuntimeLogValue.Quote("otherArea", other?.m_area?.name ?? "missing"),
                    RuntimeLogValue.Of("portalPosition", Position(room.transform.InverseTransformPoint(portal.Position))),
                    RuntimeLogValue.Of("gatePosition", gate == null ? "unavailable" : Position(room.transform.InverseTransformPoint(gate.transform.position))),
                    RuntimeLogValue.Of("gateForward", gate == null ? "unavailable" : Position(room.transform.InverseTransformDirection(gate.transform.forward))),
                    RuntimeLogValue.Of("gateNodes", gate?.m_nodes?.Count ?? -1),
                    RuntimeLogValue.Of("gateNodeY", GateNodeHeights(room, gate)),
                    RuntimeLogValue.Of("needsBorderLinks", gate?.m_needsBorderLinks ?? false),
                    RuntimeLogValue.Of("gateType", gate == null ? "missing" : gate.Type.ToString()),
                    RuntimeLogValue.Of("traversable", portal.IsTraversable)
                }).ToArray());
        }
    }
    /// <summary>Lowest and highest room-local height of the connector gate's own graph nodes: tells a gate whose nodes sit off the
    /// area's floor (a raised connector) from one the scan could reach.</summary>
    private static string GateNodeHeights(LG_Geomorph room, LG_Gate? gate)
    {
        var nodes = gate?.m_nodes;
        if (nodes == null || nodes.Count == 0) return "none";
        float low = float.MaxValue, high = float.MinValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] == null) continue;
            float y = room.transform.InverseTransformPoint(nodes[i].Position).y;
            if (y < low) low = y; if (y > high) high = y;
        }
        return FormattableString.Invariant($"{low:0.##}..{high:0.##}");
    }
    /// <summary>Voxel nodes the native volume holds along the line from the area's graph source to each of its portals (1 m steps,
    /// "distance:nodes"), the world frame of the room and the volume, and the first collider found where the line has no node.
    /// Tells a missing floor, a blocked voxel and a volume that stops short apart; read only.</summary>
    private static void VoxelRun(LG_Geomorph room, LG_Area area, AIGraph.AIG_CourseNode? node, RoomBuildArea? definition, RuntimeLogValue[] common, RuntimeLogLevel level)
    {
        var voxel = room.m_nodeVolume?.m_voxelNodeVolume;
        if (voxel == null || voxel.WasCollected || node?.m_portals == null || definition == null) return;
        var corner = voxel.m_cornerOffsetRound;
        var frame = FormattableString.Invariant($"roomWorld={room.transform.position.x:0.##},{room.transform.position.y:0.##},{room.transform.position.z:0.##};yaw={room.transform.eulerAngles.y:0.#};corner={corner.x:0.##},{corner.z:0.##};premul={voxel.m_premulVoxelSizeDiv:0.####};count={voxel.m_countX}x{voxel.m_countZ};y={voxel.m_minY}..{voxel.m_maxY}");
        for (int portalIndex = 0; portalIndex < node.m_portals.Count; portalIndex++)
        {
            var gate = node.m_portals[portalIndex]?.m_gate;
            if (gate == null) continue;
            var from = room.transform.TransformPoint(new UnityEngine.Vector3(definition.GraphSource[0], 0.05f, definition.GraphSource[2]));
            var to = new UnityEngine.Vector3(gate.transform.position.x, from.y, gate.transform.position.z);
            float length = UnityEngine.Vector3.Distance(from, to);
            string owners = Owners(room, node, from, to);
            // "distance:lowest-node links/pillar nodes" only where the pillar is empty/outside or its lowest node has fewer than 8 links.
            var samples = new System.Text.StringBuilder(); var clusters = new System.Text.StringBuilder(); string blocker = "none"; int shown = 0, ok = 0;
            for (float distance = 0; distance <= length && distance < 48; distance += 1f)
            {
                var point = UnityEngine.Vector3.MoveTowards(from, to, distance);
                int nodes, links = -1; int cluster = -1;
                try
                {
                    int gx = (int)Math.Floor((point.x - corner.x) * voxel.m_premulVoxelSizeDiv), gz = (int)Math.Floor((point.z - corner.z) * voxel.m_premulVoxelSizeDiv);
                    var list = gx < 0 || gz < 0 || gx >= voxel.m_countX || gz >= voxel.m_countZ ? null : voxel.GetPillar(gx, gz)?.m_nodes;
                    nodes = gx < 0 || gz < 0 || gx >= voxel.m_countX || gz >= voxel.m_countZ ? -2 : list?.Count ?? 0;
                    if (list != null && list.Count > 0) { var low = list[0]; for (int i = 1; i < list.Count; i++) if (list[i].Position.y < low.Position.y) low = list[i]; links = low.Links?.Count ?? -1; cluster = low.ClusterID; }
                }
                catch (Exception) { nodes = -3; }
                if (distance < 20) clusters.Append(cluster).Append(',');
                if (nodes > 0 && links >= 8) { ok++; continue; }
                if (shown++ < 12) samples.Append((int)distance).Append(':').Append(links).Append('/').Append(nodes).Append(' ');
                if (blocker == "none")
                {
                    var found = UnityEngine.Physics.OverlapSphere(point + UnityEngine.Vector3.up * 0.6f, 1.2f);
                    blocker = found.Length == 0 ? "no-collider@" + (int)distance : found.Length + "@" + (int)distance;
                    foreach (var collider in found.Take(10))
                    {
                        var chain = new System.Text.StringBuilder(); var walk = collider.transform;
                        for (int depth = 0; walk != null && depth < 4; depth++, walk = walk.parent) chain.Append(Cut(walk.name, 28)).Append('<');
                        var local = room.transform.InverseTransformPoint(collider.bounds.center); var size = collider.bounds.size;
                        // log-values-max: 10 -- the three common values and the seven collider fields.
                        MapLog.Record(level, RoomRecipeCodes.NavigationReadback, "Collider within 1.2 m of the first pillar with fewer than 8 links; room-local bounds.",
                            common.Concat(new[] { RuntimeLogValue.Of("phase", "voxel-collider"), RuntimeLogValue.Of("portalIndex", portalIndex),
                                RuntimeLogValue.Of("chain", chain.ToString()), RuntimeLogValue.Of("layer", collider.gameObject.layer),
                                RuntimeLogValue.Of("center", Position(local)), RuntimeLogValue.Of("size", Position(size)), RuntimeLogValue.Of("trigger", collider.isTrigger) }).ToArray());
                    }
                }
            }
            // log-values-max: 12 -- the three common values and the nine voxel-line fields.
            MapLog.Record(level, RoomRecipeCodes.NavigationReadback, "Voxel pillars along the line from the area's graph source to one portal; only pillars with no node or fewer than 8 links are listed (-2 outside the volume).",
                common.Concat(new[] { RuntimeLogValue.Of("phase", "voxel-line"), RuntimeLogValue.Of("portalIndex", portalIndex),
                    RuntimeLogValue.Of("frame", frame), RuntimeLogValue.Of("lineLength", (int)length), RuntimeLogValue.Of("fullLinkPillars", ok),
                    RuntimeLogValue.Of("anomalies", samples.Length == 0 ? "none" : samples.ToString().Trim()),
                    RuntimeLogValue.Of("clusterIds", clusters.ToString()), RuntimeLogValue.Of("volumeOwners", owners), RuntimeLogValue.Of("firstBlocker", blocker) }).ToArray());
        }
    }
    /// <summary>The solid colliders in a slab just outside each side of the area the native scan reached (room-local axes, 1 m thick,
    /// 0.3-2.5 m above the floor): what stopped the scan there. Floors and ceilings are skipped; read only.</summary>
    private static void BeyondScan(LG_Geomorph room, ScanReceipt scan, RuntimeLogValue[] common, RuntimeLogLevel level)
    {
        var low = scan.Low; var high = scan.High;
        var sides = new (string Name, UnityEngine.Vector3 Center, UnityEngine.Vector3 Half)[] {
            ("+x", new(high.x + 1f, 1.4f, (low.z + high.z) / 2), new(.5f, 1.1f, (high.z - low.z) / 2 + .5f)),
            ("-x", new(low.x - 1f, 1.4f, (low.z + high.z) / 2), new(.5f, 1.1f, (high.z - low.z) / 2 + .5f)),
            ("+z", new((low.x + high.x) / 2, 1.4f, high.z + 1f), new((high.x - low.x) / 2 + .5f, 1.1f, .5f)),
            ("-z", new((low.x + high.x) / 2, 1.4f, low.z - 1f), new((high.x - low.x) / 2 + .5f, 1.1f, .5f)) };
        foreach (var side in sides)
        {
            var found = UnityEngine.Physics.OverlapBox(room.transform.TransformPoint(side.Center), side.Half, room.transform.rotation);
            var seen = new HashSet<string>(); int shown = 0;
            foreach (var collider in found)
            {
                var size = collider.bounds.size;
                if (size.y < .5f || collider.isTrigger) continue;
                var chain = new System.Text.StringBuilder(); var walk = collider.transform;
                for (int depth = 0; walk != null && depth < 3; depth++, walk = walk.parent) chain.Append(Cut(walk.name, 26)).Append('<');
                if (!seen.Add(chain.ToString()) || shown++ >= 6) continue;
                // log-values-max: 9 -- the three common values and the six scan-edge fields.
                MapLog.Record(level, RoomRecipeCodes.NavigationReadback, "Solid collider just outside the area the native scan reached; room-local bounds.",
                    common.Concat(new[] { RuntimeLogValue.Of("phase", "scan-edge"), RuntimeLogValue.Of("side", side.Name),
                        RuntimeLogValue.Of("chain", chain.ToString()), RuntimeLogValue.Of("layer", collider.gameObject.layer),
                        RuntimeLogValue.Of("center", Position(room.transform.InverseTransformPoint(collider.bounds.center))), RuntimeLogValue.Of("size", Position(size)) }).ToArray());
            }
            // log-values-max: 6 -- the three common values and the three empty-side fields.
            if (shown == 0) MapLog.Record(level, RoomRecipeCodes.NavigationReadback, "No solid collider just outside this side of the scanned area.",
                common.Concat(new[] { RuntimeLogValue.Of("phase", "scan-edge"), RuntimeLogValue.Of("side", side.Name), RuntimeLogValue.Of("chain", "none") }).ToArray());
        }
    }
    /// <summary>Which geomorph node volume the native world lookup (AIG_GeomorphNodeVolume.TryGetGeomorphVolume, the one the native
    /// cross-volume linking and portal queries use) returns at the graph source, halfway, the portal and 1.5 m past it.
    /// own / none / other:name. Read only.</summary>
    private static string Owners(LG_Geomorph room, AIGraph.AIG_CourseNode node, UnityEngine.Vector3 from, UnityEngine.Vector3 to)
    {
        var direction = (to - from); direction = direction.sqrMagnitude < .01f ? room.transform.forward : direction.normalized;
        var points = new[] { from, (from + to) / 2, to, to + direction * 1.5f };
        var text = new System.Text.StringBuilder();
        foreach (var point in points)
        {
            string owner;
            try
            {
                var dimension = node.m_dimension?.DimensionIndex ?? eDimensionIndex.Reality;
                owner = !ForgeCore.StackedVolumes.Find(dimension, point, out var found) || found == null ? "none"
                    : room.m_nodeVolume != null && found.Pointer == room.m_nodeVolume.Pointer ? "own"
                    : "other:" + Cut(found.transform.name, 18) + "@" + FormattableString.Invariant($"{found.transform.position.x:0},{found.transform.position.z:0}");
            }
            catch (Exception error) { owner = "err:" + error.GetType().Name; }
            text.Append(owner).Append(';');
        }
        return text.ToString();
    }
    private static string PositionReadback(float[]? expected, UnityEngine.Vector3? actual)
    {
        if (actual is not { } position) return "expected=" + (expected == null ? "unresolved" : Position(expected)) + ";actual=unavailable;deviation=unavailable";
        string found = FormattableString.Invariant($"({position.x:0.###},{position.y:0.###},{position.z:0.###})");
        if (expected == null) return "expected=unresolved;actual=" + found + ";deviation=unavailable";
        double dx = position.x - expected[0], dy = position.y - expected[1], dz = position.z - expected[2];
        return FormattableString.Invariant($"expected={Position(expected)};actual={found};deviation={Math.Sqrt(dx * dx + dy * dy + dz * dz):0.###}");
    }
    /// <summary>Two levels. The return value is the structural level: the area has no course node or voxels, a gate has no identity, portal or
    /// node; a level with that cannot be entered. Everything else the native scan got wrong is appended to <paramref name="report"/>
    /// as one sentence naming the area or door, and the level goes on.</summary>
    internal static bool Check(LG_Geomorph room, RoomBuildRecipe recipe, List<RuntimeLogValue>? failure = null, List<string>? report = null)
    {
        var nodes = new HashSet<IntPtr>(); var clusters = new HashSet<IntPtr>();
        bool valid = true;
        var locator = GeneratedRoomRegistry.LocatorFor(room);
        for (int index = 0; index < room.m_areas.Length; index++)
        {
            var area = room.m_areas[index];
            var node = area.m_courseNode; var cluster = node?.m_nodeCluster;
            bool nodeMatches = node?.m_area != null && node.m_area.Pointer == area.Pointer;
            bool clusterMatches = node != null && cluster?.m_courseNode != null && cluster.m_courseNode.Pointer == node.Pointer;
            var volume = room.m_nodeVolume;
            bool volumeMatches = volume != null && !volume.WasCollected && cluster != null && !cluster.WasCollected
                && cluster.m_nodeVolume != null && !cluster.m_nodeVolume.WasCollected && cluster.m_nodeVolume.Pointer == volume.Pointer;
            bool voxelsReady = volume != null && !volume.WasCollected && volume.m_voxelNodeVolume != null && !volume.m_voxelNodeVolume.WasCollected;
            // Why the cluster is not this node's: the scan assigned none ("missing"), it handed over the cluster an earlier
            // area already holds ("shared"), or the cluster names another course node ("foreign").
            string clusterState = cluster == null ? "missing" : clusters.Contains(cluster.Pointer) ? "shared"
                : cluster.m_courseNode?.Pointer != node?.Pointer ? "foreign" : "own";
            int portals = node?.m_portals?.Count ?? -1, portalsHit = node != null && PortalsHit.TryGetValue(node.Pointer, out var hit) ? hit : -1;
            bool distinctNode = node != null && nodes.Add(node.Pointer), distinctCluster = cluster != null && clusters.Add(cluster.Pointer);
            bool areaStructural = nodeMatches && distinctNode && voxelsReady;
            bool areaValid = areaStructural && clusterMatches && distinctCluster && volumeMatches;
            if (areaStructural && (!areaValid || portalsHit != portals))
                report?.Add($"area {(index < recipe.Areas.Count ? recipe.Areas[index].Id : "unresolved")}: cluster={clusterState}, scan touched {portalsHit}/{portals} doors, missed {MissedPortals(node)}");
            if (!areaValid)
            {
                NavigationDetail(room, area, recipe, index, RuntimeLogLevel.Error, false);
                if (valid) failure?.AddRange(new[] {
                    RuntimeLogValue.Of("area", index < recipe.Areas.Count ? recipe.Areas[index].Id : "unresolved"),
                    // actual/expected pairs keep the full first mismatch within I-DIAG's 16-value/256-character bounds.
                    RuntimeLogValue.Of("areaReadback", $"cluster={clusterState}/own;nodeArea={nodeMatches}/True;clusterNode={clusterMatches}/True;"
                        + $"clusterVolume={volumeMatches}/True;voxels={voxelsReady}/True;distinctNode={distinctNode}/True;distinctCluster={distinctCluster}/True;"
                        + $"portalsHit={portalsHit}/{portals}"),
                    RuntimeLogValue.Of("sourcePosition", PositionReadback(index < recipe.Areas.Count ? recipe.Areas[index].GraphSource : null,
                        area.m_groupSource?.transform.localPosition)) });
                if (!areaStructural) valid = false;
            }
            // A missed door's verdict is its one portal-miss Error and the build report below; these rows only explain it.
            else NavigationDetail(room, area, recipe, index, RuntimeLogLevel.Info, true);
            if (MapLog.Logs(RuntimeLogLevel.Info)) MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.Aigraph,
                "Native area and navigation cluster identity readback.",
                RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("phase", "area-readback"),
                RuntimeLogValue.Of("roomInstance", locator?.InstanceId ?? "unregistered"),
                RuntimeLogValue.Of("zoneAuthorId", locator?.ZoneAuthorId ?? "unregistered"),
                RuntimeLogValue.Of("area", index < recipe.Areas.Count ? recipe.Areas[index].Id : "unresolved"),
                RuntimeLogValue.Of("actualArea", area.UID.ToString()),
                RuntimeLogValue.Of("nodeAreaMatches", nodeMatches), RuntimeLogValue.Of("clusterNodeMatches", clusterMatches),
                RuntimeLogValue.Of("clusterVolumeMatches", volumeMatches), RuntimeLogValue.Of("voxelsReady", voxelsReady),
                RuntimeLogValue.Of("distinctNodes", distinctNode), RuntimeLogValue.Of("distinctClusters", distinctCluster),
                RuntimeLogValue.Of("portals", portals), RuntimeLogValue.Of("portalsHit", portalsHit), RuntimeLogValue.Of("clusterState", clusterState),
                RuntimeLogValue.Of("valid", areaValid));
            const RuntimeLogLevel missLevel = RuntimeLogLevel.Error;
            if (portalsHit != portals && MapLog.Logs(missLevel))
            {
                MapLog.Record(missLevel, RoomRecipeCodes.Aigraph,
                    "Native cluster scan touched fewer portals than the course node holds; each missed portal is named as gate:neighbouring area.",
                    RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("phase", "portal-miss"),
                    RuntimeLogValue.Of("roomInstance", locator?.InstanceId ?? "unregistered"),
                    RuntimeLogValue.Of("area", index < recipe.Areas.Count ? recipe.Areas[index].Id : "unresolved"),
                    RuntimeLogValue.Of("portals", portals), RuntimeLogValue.Of("portalsHit", portalsHit),
                    RuntimeLogValue.Quote("missed", MissedPortals(node)));
                MissedPortalObstacles(room, node, recipe, index);
            }
        }
        var gates = RoomRecipeDoorIdentity.AuthoredGates(room).ToArray();
        if (gates.Length != recipe.InteriorLinks.Count())
        {
            if (valid) failure?.AddRange(new[] { RuntimeLogValue.Of("gates", gates.Length),
                RuntimeLogValue.Of("gatesExpected", recipe.InteriorLinks.Count()) });
            valid = false;
        }
        foreach (var gate in gates)
        {
            var receipt = RoomRecipeDoorIdentity.Resolve(gate);
            var from = gate.m_linksFrom?.m_courseNode; var to = gate.m_linksTo?.m_courseNode; var portal = gate.CoursePortal;
            bool portalNodesMatch = from != null && to != null && portal?.m_nodeA != null && portal.m_nodeB != null
                && ((portal.m_nodeA.Pointer == from.Pointer && portal.m_nodeB.Pointer == to.Pointer)
                    || (portal.m_nodeA.Pointer == to.Pointer && portal.m_nodeB.Pointer == from.Pointer));
            bool fromHasPortal = from?.m_portals != null && portal != null && from.m_portals.Contains(portal);
            bool toHasPortal = to?.m_portals != null && portal != null && to.m_portals.Contains(portal);
            bool connected = receipt != null && from != null && to != null && from.Pointer != to.Pointer
                && portal != null && portal.m_gate != null && portal.m_gate.Pointer == gate.Pointer
                && portalNodesMatch
                && from.m_nodeCluster != null && to.m_nodeCluster != null && from.m_nodeCluster.Pointer != to.m_nodeCluster.Pointer
                && fromHasPortal && toHasPortal;
            bool physicalDoor = receipt?.Door.Kind == "door";
            bool gateStructural = receipt != null && from != null && to != null && from.Pointer != to.Pointer
                && portal != null && portal.m_gate != null && portal.m_gate.Pointer == gate.Pointer && portalNodesMatch;
            connected &= physicalDoor ? gate.SpawnedDoor != null : gate.SpawnedDoor == null && gate.TryCast<LG_FreePassageGate>() != null && gate.m_isTraversable;
            if (!connected)
            {
                if (gateStructural) report?.Add($"door {receipt?.Door.Name ?? gate.name} ({receipt?.Door.FromArea}-{receipt?.Door.ToArea}): "
                    + $"clusters distinct={from?.m_nodeCluster != null && to?.m_nodeCluster != null && from.m_nodeCluster.Pointer != to.m_nodeCluster.Pointer}, "
                    + $"both nodes list the portal={fromHasPortal && toHasPortal}, door/passage built={(physicalDoor ? gate.SpawnedDoor != null : gate.TryCast<LG_FreePassageGate>() != null)}, traversable={gate.m_isTraversable}");
                if (valid && !gateStructural) failure?.AddRange(new[] {
                    RuntimeLogValue.Quote("link", receipt?.Door.Name ?? gate.name),
                    RuntimeLogValue.Of("linkReadback", $"receipt={receipt != null}/True;nodes={from != null && to != null && from.Pointer != to.Pointer}/True;"
                        + $"clusters={from?.m_nodeCluster != null && to?.m_nodeCluster != null && from.m_nodeCluster.Pointer != to.m_nodeCluster.Pointer}/True;"
                        + $"portalGate={portal?.m_gate != null && portal.m_gate.Pointer == gate.Pointer}/True;portalNodes={portalNodesMatch}/True;"
                        + $"fromPortal={fromHasPortal}/True;toPortal={toHasPortal}/True;door={gate.SpawnedDoor != null}/{physicalDoor};"
                        + $"passage={gate.TryCast<LG_FreePassageGate>() != null}/{!physicalDoor};traversable={gate.m_isTraversable}/{(physicalDoor ? "n/a" : "True")};start={gate.m_isTraversableFromStart}/{(physicalDoor ? "n/a" : "True")}"),
                    RuntimeLogValue.Of("linkPosition", PositionReadback(receipt?.Door.Position, room.transform.InverseTransformPoint(gate.transform.position))) });
                if (!gateStructural) valid = false;
            }
            if (MapLog.Logs(RuntimeLogLevel.Info)) MapLog.Record(RuntimeLogLevel.Info, RoomRecipeCodes.Aigraph,
                "Native interior gate spatial readback; sound and enemy behavior require game checks.",
                RuntimeLogValue.Of("resource", recipe.ResourceKey), RuntimeLogValue.Of("door", receipt?.Door.Name ?? "unresolved"),
                RuntimeLogValue.Of("kind", receipt?.Door.Kind ?? "unresolved"),
                RuntimeLogValue.Of("roomInstance", locator?.InstanceId ?? "unregistered"), RuntimeLogValue.Of("zoneAuthorId", locator?.ZoneAuthorId ?? "unregistered"),
                RuntimeLogValue.Of("size", receipt?.Door.Size ?? "unresolved"),
                RuntimeLogValue.Of("fromArea", receipt?.Door.FromArea ?? "unresolved"),
                RuntimeLogValue.Of("toArea", receipt?.Door.ToArea ?? "unresolved"), RuntimeLogValue.Of("distinctNodes", from != null && to != null && from.Pointer != to.Pointer),
                RuntimeLogValue.Of("distinctClusters", from?.m_nodeCluster != null && to?.m_nodeCluster != null && from.m_nodeCluster.Pointer != to.m_nodeCluster.Pointer),
                RuntimeLogValue.Of("portalMatchesGate", portal?.m_gate != null && portal.m_gate.Pointer == gate.Pointer),
                RuntimeLogValue.Of("doorSpawned", gate.SpawnedDoor != null), RuntimeLogValue.Of("traversableFromStart", gate.m_isTraversableFromStart),
                RuntimeLogValue.Of("traversable", gate.m_isTraversable), RuntimeLogValue.Of("valid", connected));
        }
        return valid;
    }
}
