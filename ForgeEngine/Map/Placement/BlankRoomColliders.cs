using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

/// <summary>A collider the room carries of its own (an imported geomorph's floors, walls and props, Web/src/forge/room/room-colliders.ts),
/// built by RoomRecipeColliders as a node on <c>Layer</c> (a TagManager world layer) under <c>Area</c>'s props with the part pose.
/// <c>box</c> is the 4 m unit box standing on its pivot, as an air wall; <c>sphere</c> / <c>capsule</c> Unity's own with <c>Center</c>,
/// <c>Radius</c>, <c>Height</c> and <c>Direction</c>; <c>mesh</c> the collision mesh <c>Mesh</c> names.</summary>
internal sealed record RoomBuildCollider(string Id, string Layer, string Shape, string Area, float[] Position, float[] Rotation, float[] Scale,
    float[]? Center = null, float Radius = 0, float Height = 0, int Direction = 1, string? Mesh = null);
/// <summary>A collision mesh: a vanilla one the game finds again (<c>Prefab</c>, optionally through the LG_PrefabSpawner at name path
/// <c>Spawner</c> to its prefab, then child indices <c>Node</c> to a MeshCollider whose mesh is <c>Name</c> with <c>Vertices</c> vertices),
/// or the package's own geometry (<c>Positions</c>, <c>Indices</c>).</summary>
internal sealed record RoomBuildCollisionMesh(string Id, string? Name = null, int Vertices = 0, string? Prefab = null, string? Spawner = null,
    int[]? Node = null, float[]? Positions = null, int[]? Indices = null);

internal sealed partial record RoomBuildRecipe
{
    /// <summary>The physics layers a room's own collider may stand on (room-colliders.ts roomColliderLayers).</summary>
    public static readonly IReadOnlyList<string> ColliderLayers = new[] { "Default", "Ignore Raycast", "Water", "Dynamic", "InvisibleWall", "Debris",
        "ProjectileBlocker", "MoverHelper", "Default_NoGraph", "Default_BlockGraph", "NoCollision_BlockGraph", "Elevator" };
    /// <summary>Format bounds against damaged input (room-colliders.ts): meshes per room, base64 characters per package mesh array.</summary>
    public const int MaximumCollisionMeshes = 4096, MaximumCollisionMeshBase64 = 4 << 20;
    public IReadOnlyList<RoomBuildCollider> Colliders { get; private init; } = Array.Empty<RoomBuildCollider>();
    public IReadOnlyList<RoomBuildCollisionMesh> CollisionMeshes { get; private init; } = Array.Empty<RoomBuildCollisionMesh>();

    private static IReadOnlyList<RoomBuildCollisionMesh> ReadCollisionMeshes(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumCollisionMeshes) throw new FormatException("blank room collision meshes");
        var meshes = new List<RoomBuildCollisionMesh>(rows.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("source", out var source)) throw new FormatException("blank room collision mesh");
            var id = Text(row, "id", 64);
            if (!Token(id) || !ids.Add(id)) throw new FormatException("blank room collision mesh identity");
            if (source.ValueKind == JsonValueKind.String && source.GetString() == "package")
            {
                Fields(row, "id", "source", "positions", "indices");
                var positions = Floats(Base64(row, "positions"));
                var indices = Ints(Base64(row, "indices"));
                if (positions.Length == 0 || positions.Length % 3 != 0 || indices.Length == 0 || indices.Length % 3 != 0
                    || positions.Any(x => !float.IsFinite(x)) || indices.Any(i => (uint)i >= (uint)(positions.Length / 3)))
                    throw new FormatException("blank room collision mesh geometry");
                meshes.Add(new(id, Positions: positions, Indices: indices));
                continue;
            }
            if (source.ValueKind != JsonValueKind.String || source.GetString() != "game") throw new FormatException("blank room collision mesh source");
            Fields(row, "id", "source", "key", "name", "vertices", "donor");
            Text(row, "key", 128);
            var name = Text(row, "name", 256, exact: true);
            var vertices = row.GetProperty("vertices");
            if (vertices.ValueKind != JsonValueKind.Number || !vertices.TryGetInt32(out var count) || count <= 0) throw new FormatException("blank room collision mesh vertices");
            var donor = row.GetProperty("donor");
            OptionalFields(donor, new[] { "prefab", "node", "loadedWith" }, "spawner");
            string prefab = Text(donor, "prefab"), loadedWith = Text(donor, "loadedWith");
            string? spawner = donor.TryGetProperty("spawner", out _) ? Text(donor, "spawner", MaximumNodeChars, exact: true) : null;
            var nodeText = donor.GetProperty("node");
            if (!AssetPath(prefab) || !AssetPath(loadedWith) || nodeText.ValueKind != JsonValueKind.String) throw new FormatException("blank room collision mesh donor");
            var path = nodeText.GetString()!;
            var node = path.Length == 0 ? Array.Empty<int>() : path.Split('/').Select(step => int.TryParse(step, out var index) && index is >= 0 and <= 99999 && step == index.ToString() ? index : -1).ToArray();
            if (node.Length > 64 || node.Any(index => index < 0)) throw new FormatException("blank room collision mesh node");
            meshes.Add(new(id, name, count, prefab, spawner, node));
        }
        return meshes.AsReadOnly();
    }

    private static IReadOnlyList<RoomBuildCollider> ReadColliders(JsonElement rows, HashSet<string> areas, HashSet<string> taken, IReadOnlyList<RoomBuildCollisionMesh> meshes)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumParts) throw new FormatException("blank room colliders");
        var meshIds = new HashSet<string>(meshes.Select(mesh => mesh.Id), StringComparer.Ordinal);
        var colliders = new List<RoomBuildCollider>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("shape", out var shapeValue) || shapeValue.ValueKind != JsonValueKind.String)
                throw new FormatException("blank room collider");
            var shape = shapeValue.GetString()!;
            string[] fields = shape switch
            {
                "box" => new[] { "id", "layer", "shape", "area", "position", "rotation", "scale" },
                "mesh" => new[] { "id", "layer", "shape", "area", "position", "rotation", "scale", "mesh" },
                "sphere" => new[] { "id", "layer", "shape", "area", "position", "rotation", "scale", "center", "radius" },
                "capsule" => new[] { "id", "layer", "shape", "area", "position", "rotation", "scale", "center", "radius", "height", "direction" },
                _ => throw new FormatException("blank room collider shape"),
            };
            Fields(row, fields);
            string id = Text(row, "id", 64), layer = Text(row, "layer", 64), area = Text(row, "area");
            if (!Token(id) || !taken.Add(id)) throw new FormatException("blank room collider identity");
            if (!ColliderLayers.Contains(layer, StringComparer.Ordinal)) throw new FormatException("blank room collider layer");
            if (!areas.Contains(area)) throw new FormatException("blank room collider area");
            var scale = Vector(row, "scale");
            if (scale.Any(x => x == 0)) throw new FormatException("blank room collider scale");
            var collider = new RoomBuildCollider(id, layer, shape, area, Vector(row, "position"), Vector(row, "rotation"), scale);
            if (shape is "sphere" or "capsule")
            {
                var radius = Number(row, "radius");
                if (radius <= 0) throw new FormatException("blank room collider radius");
                collider = collider with { Center = Vector(row, "center"), Radius = radius };
            }
            if (shape == "capsule")
            {
                var height = Number(row, "height"); var direction = row.GetProperty("direction");
                if (height < 0 || direction.ValueKind != JsonValueKind.Number || !direction.TryGetInt32(out var axis) || axis is < 0 or > 2)
                    throw new FormatException("blank room collider capsule");
                collider = collider with { Height = height, Direction = axis };
            }
            if (shape == "mesh")
            {
                var mesh = Text(row, "mesh", 64);
                if (!meshIds.Contains(mesh)) throw new FormatException("blank room collider mesh");
                collider = collider with { Mesh = mesh };
            }
            colliders.Add(collider);
        }
        return colliders.AsReadOnly();
    }

    private static byte[] Base64(JsonElement row, string key)
    {
        var element = row.GetProperty(key);
        var text = element.ValueKind == JsonValueKind.String ? element.GetString()! : "";
        if (text.Length == 0 || text.Length > MaximumCollisionMeshBase64 || text.Length % 4 != 0) throw new FormatException("blank room collision mesh data: " + key);
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { throw new FormatException("blank room collision mesh data: " + key); }
    }
    /// <summary>Little-endian float32 and uint32 arrays, as the converter packs them.</summary>
    private static float[] Floats(byte[] bytes)
    {
        if (bytes.Length % 4 != 0) throw new FormatException("blank room collision mesh data: positions");
        var values = new float[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++) values[i] = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4)));
        return values;
    }
    private static int[] Ints(byte[] bytes)
    {
        if (bytes.Length % 4 != 0) throw new FormatException("blank room collision mesh data: indices");
        var values = new int[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
            values[i] = value <= int.MaxValue ? (int)value : -1;
        }
        return values;
    }
}
