using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LevelGeneration;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeDevelopment.Native;

// Read-only navigation readbacks for drive sessions. Each call is bounded (sample count, result rows) so it
// stays inside one frame, and none of them changes game state: they only query the NavMesh, the AI node graph
// and the physics scene.
internal static class AuthoringNavReadback
{
    private const int MaxPoints = 400, MaxRows = 80;

    /// <summary>{"from":[x,y,z],"to":[x,y,z]?,"step":0.5,"maxDistance":0.6}: NavMesh.SamplePosition at one point, or along the segment.</summary>
    internal static string Sample(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Spec(document, "from", "to", "step", "maxDistance");
        var from = Point(root, "from");
        var to = root.TryGetProperty("to", out _) ? Point(root, "to") : from;
        float step = Number(root, "step", 0.5f), reach = Number(root, "maxDistance", 0.6f);
        var length = Vector3.Distance(from, to);
        var count = length < 1e-4f ? 1 : Math.Min(MaxPoints, (int)Math.Floor(length / Math.Max(step, 0.05f)) + 1);
        var rows = new List<object>();
        for (var i = 0; i < count; i++)
        {
            var at = count == 1 ? from : Vector3.Lerp(from, to, length < 1e-4f ? 0 : Math.Min(1f, i * Math.Max(step, 0.05f) / length));
            var hit = ForgeCore.Level.TrySnap(at, reach, out var found);
            rows.Add(new { point = Array3(at), hit, hitPoint = hit ? Array3(found) : null, distance = hit ? (float?)Vector3.Distance(at, found) : null });
        }
        return Serialize(new { schemaVersion = "1.0.0", maxDistance = reach, step, truncated = length >= 1e-4f && length / Math.Max(step, 0.05f) + 1 > MaxPoints, rows });
    }

    /// <summary>{"from":[..],"to":[..],"snap":1.0}: snaps both ends onto the NavMesh (snap 0 = use them as given), then NavMesh.CalculatePath.</summary>
    internal static string Path(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Spec(document, "from", "to", "snap");
        var from = Point(root, "from");
        var to = Point(root, "to");
        var snap = Number(root, "snap", 1f);
        var start = from;
        var goal = to;
        var snappedStart = snap <= 0 || ForgeCore.Level.TrySnap(from, snap, out start);
        var snappedGoal = snap <= 0 || ForgeCore.Level.TrySnap(to, snap, out goal);
        if (!snappedStart || !snappedGoal)
            return Serialize(new { schemaVersion = "1.0.0", status = "EndpointNotOnNavMesh", snappedStart, snappedGoal });
        var path = new NavMeshPath();
        var calculated = NavMesh.CalculatePath(start, goal, -1, path);
        var corners = path.corners;
        var length = 0f;
        for (var i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
        return Serialize(new
        {
            schemaVersion = "1.0.0",
            calculated,
            status = path.status.ToString(),
            start = Array3(start),
            goal = Array3(goal),
            length,
            corners = corners.Take(MaxRows).Select(corner => Array3(corner)).ToArray(),
            cornerCount = corners.Length
        });
    }

    /// <summary>{"points":[[..],..],"heightSens":2.5}: per point the geomorph node volume, the voxel node AIG_VoxelNodeVolume.GetNode finds
    /// within heightSens, its ClusterID, and the course node at that position.</summary>
    internal static string Voxel(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Spec(document, "points", "heightSens");
        var sens = Number(root, "heightSens", 2.5f);
        var rows = new List<object>();
        if (!root.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("drive.nav.missing-field:points ([[x,y,z],...])");
        foreach (var element in points.EnumerateArray().Take(MaxRows))
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
                throw new ArgumentException("drive.nav.point-required:points ([x,y,z])");
            var at = new Vector3(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());
            var volumeFound = ForgeCore.StackedVolumes.Find(eDimensionIndex.Reality, at, out var volume) && volume != null;
            object? node = null;
            if (volumeFound && volume!.m_voxelNodeVolume != null && volume.m_voxelNodeVolume.GetNode(at, sens, out var voxel, false) && voxel != null)
                node = new { position = Array3(voxel.Position), clusterId = (int)voxel.ClusterID, traversable = voxel.IsTraversable, belongsToGate = voxel.BelongsToGate };
            var courseFound = Dimension.TryGetCourseNodeFromPos(at, out var course) && course != null;
            rows.Add(new
            {
                point = Array3(at),
                volume = volumeFound ? volume!.name : null,
                volumePosition = volumeFound ? Array3(volume!.Position) : null,
                volumeSize = volumeFound ? Array3(volume!.Size) : null,
                node,
                courseNode = courseFound ? (int?)course!.NodeID : null
            });
        }
        return Serialize(new { schemaVersion = "1.0.0", heightSens = sens, rows });
    }

    /// <summary>{"points":[[..],..],"player":true,"sample":2}: the course graph at each point (and at the local player when asked):
    /// the course node the native lookup gives (AIG_CourseNode.TryGetCourseNode), its zone and area, and every portal of that node
    /// with the opposite node, gate, zone-portal mark, progression lock and traversable state. Then, for every ordered pair of
    /// points, whether the second node is reachable over all portals and over the portals an enemy may take
    /// (EnemyCourseNavigation.TryFindNodeTowardGoalNode skips IsProgressionLocked portals), with the enemy route's node ids.</summary>
    internal static string Course(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Spec(document, "points", "player", "sample");
        var sample = Number(root, "sample", 2f);
        var at = new List<Vector3>();
        if (root.TryGetProperty("points", out var points))
        {
            if (points.ValueKind != JsonValueKind.Array) throw new ArgumentException("drive.nav.point-required:points ([[x,y,z],...])");
            foreach (var element in points.EnumerateArray().Take(MaxRows))
            {
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
                    throw new ArgumentException("drive.nav.point-required:points ([x,y,z])");
                at.Add(new Vector3(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle()));
            }
        }
        var dimension = eDimensionIndex.Reality;
        if (root.TryGetProperty("player", out var wantsPlayer) && wantsPlayer.ValueKind == JsonValueKind.True)
        {
            var player = Player.PlayerManager.GetLocalPlayerAgent() ?? throw new InvalidOperationException("drive.nav.no-local-player");
            at.Add(player.Position);
            dimension = player.DimensionIndex;
        }
        if (at.Count == 0) throw new ArgumentException("drive.nav.missing-field:points");
        var nodes = new AIGraph.AIG_CourseNode?[at.Count];
        var rows = new List<object>();
        for (var i = 0; i < at.Count; i++)
        {
            nodes[i] = AIGraph.AIG_CourseNode.TryGetCourseNode(dimension, at[i], sample, out var found) && found != null ? found : null;
            var node = nodes[i];
            rows.Add(new
            {
                point = Array3(at[i]),
                node = node?.NodeID,
                zone = node?.m_zone == null ? null : $"{(int)node.m_zone.DimensionIndex}/{(int)node.m_zone.Layer.m_type}/{(int)node.m_zone.LocalIndex}",
                area = node?.m_area == null ? null : (node.m_area.m_geomorph == null ? "?" : node.m_area.m_geomorph.name) + "/" + node.m_area.name,
                ownsCluster = node != null && node.m_nodeCluster?.m_courseNode?.Pointer == node.Pointer,
                portals = node == null ? null : Portals(node)
            });
        }
        var routes = new List<object>();
        for (var i = 0; i < nodes.Length; i++)
            for (var j = 0; j < nodes.Length; j++)
            {
                if (i == j || nodes[i] == null || nodes[j] == null) continue;
                routes.Add(new
                {
                    from = i,
                    to = j,
                    graphReachable = Route(nodes[i]!, nodes[j]!, false) != null,
                    enemyRoute = Route(nodes[i]!, nodes[j]!, true)
                });
            }
        return Serialize(new { schemaVersion = "1.0.0", sample, rows, routes });
    }

    private static object[] Portals(AIGraph.AIG_CourseNode node)
    {
        var list = node.m_portals;
        if (list == null) return Array.Empty<object>();
        var rows = new List<object>();
        for (var i = 0; i < list.Count && rows.Count < MaxRows; i++)
        {
            var portal = list[i];
            if (portal == null) continue;
            var gate = portal.Gate;
            rows.Add(new
            {
                to = portal.GetOppositeNode(node)?.NodeID,
                gate = gate == null ? null : gate.name,
                gateType = gate == null ? null : gate.Type.ToString(),
                zonePortal = gate != null && portal.IsZonePortal,
                progressionLocked = gate != null && portal.IsProgressionLocked,
                traversable = gate != null && portal.IsTraversable,
                door = gate?.SpawnedDoor == null ? null : gate.SpawnedDoor.ToString()
            });
        }
        return rows.ToArray();
    }

    /// <summary>Breadth-first node ids from <paramref name="start"/> to <paramref name="goal"/>, or null when unreachable; with
    /// <paramref name="enemy"/> a progression-locked portal is not taken, as the enemy course navigation does not take it.</summary>
    private static int[]? Route(AIGraph.AIG_CourseNode start, AIGraph.AIG_CourseNode goal, bool enemy)
    {
        var previous = new Dictionary<IntPtr, AIGraph.AIG_CourseNode?> { [start.Pointer] = null };
        var queue = new Queue<AIGraph.AIG_CourseNode>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Pointer == goal.Pointer)
            {
                var path = new List<int>();
                for (AIGraph.AIG_CourseNode? step = node; step != null; step = previous[step.Pointer]) path.Add(step.NodeID);
                path.Reverse();
                return path.ToArray();
            }
            var portals = node.m_portals;
            if (portals == null) continue;
            for (var i = 0; i < portals.Count; i++)
            {
                var portal = portals[i];
                if (portal == null || enemy && portal.Gate != null && portal.IsProgressionLocked) continue;
                var next = portal.GetOppositeNode(node);
                if (next == null || previous.ContainsKey(next.Pointer)) continue;
                previous[next.Pointer] = node;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>{"center":[..],"radius":3,"limit":60}: colliders (triggers included) and NavMeshObstacles inside a sphere, nearest first.</summary>
    internal static string Near(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Spec(document, "center", "radius", "limit");
        var center = Point(root, "center");
        var radius = Number(root, "radius", 3f);
        var limit = Math.Min(MaxRows, (int)Number(root, "limit", 60f));
        var colliders = Physics.OverlapSphere(center, radius, -1, QueryTriggerInteraction.Collide)
            .Where(collider => collider != null && !collider.WasCollected)
            .Select(collider => (collider, distance: Vector3.Distance(collider.bounds.center, center)))
            .OrderBy(row => row.distance).Take(limit)
            .Select(row => new
            {
                name = row.collider.name,
                path = PathOf(row.collider.transform),
                layer = LayerMask.LayerToName(row.collider.gameObject.layer),
                position = Array3(row.collider.bounds.center),
                size = Array3(row.collider.bounds.size),
                enabled = row.collider.enabled,
                isTrigger = row.collider.isTrigger,
                distance = row.distance
            }).ToArray();
        var obstacles = UnityEngine.Object.FindObjectsOfType<NavMeshObstacle>()
            .Where(obstacle => obstacle != null && !obstacle.WasCollected)
            .Select(obstacle => (obstacle, distance: Vector3.Distance(obstacle.transform.TransformPoint(obstacle.center), center)))
            .Where(row => row.distance <= radius + Math.Max(row.obstacle.size.magnitude, row.obstacle.radius))
            .OrderBy(row => row.distance).Take(limit)
            .Select(row => new
            {
                name = row.obstacle.name,
                path = PathOf(row.obstacle.transform),
                layer = LayerMask.LayerToName(row.obstacle.gameObject.layer),
                position = Array3(row.obstacle.transform.TransformPoint(row.obstacle.center)),
                shape = row.obstacle.shape.ToString(),
                size = Array3(row.obstacle.size),
                radius = row.obstacle.radius,
                carving = row.obstacle.carving,
                enabled = row.obstacle.enabled && row.obstacle.gameObject.activeInHierarchy,
                distance = row.distance
            }).ToArray();
        return Serialize(new { schemaVersion = "1.0.0", radius, colliders, obstacles });
    }

    private static string PathOf(Transform? transform)
    {
        var parts = new List<string>();
        for (var depth = 0; transform != null && depth < 6; depth++, transform = transform.parent) parts.Add(transform.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>The spec as an object with only the named fields; anything else fails naming the field and the accepted ones, so a
    /// misspelt spec (`position` for `from`) is answered instead of failing on a missing dictionary key.</summary>
    private static JsonElement Spec(JsonDocument document, params string[] accepted)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("drive.spec-object-required");
        foreach (var property in root.EnumerateObject())
            if (Array.IndexOf(accepted, property.Name) < 0)
                throw new ArgumentException("drive.nav.unknown-field:" + property.Name + " (accepted: " + string.Join(",", accepted) + ")");
        return root;
    }

    private static Vector3 Point(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p)) throw new ArgumentException("drive.nav.missing-field:" + name);
        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3 || p.EnumerateArray().Any(part => part.ValueKind != JsonValueKind.Number))
            throw new ArgumentException("drive.nav.point-required:" + name + " ([x,y,z])");
        return new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
    }

    private static float Number(JsonElement root, string name, float fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetSingle() : fallback;

    private static float[] Array3(Vector3 v) => new[] { MathF.Round(v.x, 3), MathF.Round(v.y, 3), MathF.Round(v.z, 3) };

    private static string Serialize(object value) => JsonSerializer.Serialize(value);
}
