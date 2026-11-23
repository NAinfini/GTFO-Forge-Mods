using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

internal sealed record RoomBuildDoor(string Name, float[] Position, float[] Forward, float Yaw, string Size = "8x4")
{
    public string Kind { get; init; } = "door";
    /// <summary>The other two Euler degrees of the door, applied Y·X·Z like Quaternion.Euler(Pitch, Yaw, Roll). Zero on a door that stands upright.</summary>
    public float Pitch { get; init; }
    public float Roll { get; init; }
    /// <summary>LG_Plug.m_subComplex of an exit socket; an interior gate is always Storage.</summary>
    public int SubComplex { get; init; } = RoomBuildRecipe.StorageSubComplex;
    /// <summary>The area the socket or gate belongs to: an exit's own area, an interior link's linksFrom.</summary>
    public string? FromArea { get; init; }
    /// <summary>Set only on interior links.</summary>
    public string? ToArea { get; init; }
    /// <summary>The authored door of an interior doorway (see <see cref="BlankRoomInteriorDoor"/>); null is the native roll.</summary>
    public string? DoorKind { get; init; }
}
/// <summary><c>SubComplex</c> is LG_Plug.m_subComplex of the exit's socket (Storage, the tutorial donor's, when the recipe names none).</summary>
/// <summary>A doorway: <c>Position</c> is room-local metres (any real value, y is the doorway floor), <c>Yaw</c>, <c>Pitch</c> and <c>Roll</c> Euler degrees with
/// the outward direction (sin yaw cos pitch, -sin pitch, cos yaw cos pitch). <c>Seal</c> is null for the vanilla dead-end cap when the doorway stays unpaired, <c>open</c> to leave it open,
/// otherwise a plug-cap prefab path.</summary>
internal sealed record RoomBuildExit(string Id, float Yaw, float[] Position, string Size, string Area, int SubComplex = RoomBuildRecipe.StorageSubComplex, string? Seal = null, float Pitch = 0, float Roll = 0);
/// <summary>A Unity OffMeshLink between two room-local points. <c>Area</c> is the NavMesh area index (OffMeshLinkArea: 2 Jump, 3 Ladder, 4 PlayerBot).</summary>
internal sealed record RoomBuildJumpLink(string Id, float[] Start, float[] End, bool Bidirectional, int Area);
/// <summary>A vanilla LG_Ladder. <c>Top</c> is the room-local point of the floor it reaches; null lets the ladder place its own default top.</summary>
internal sealed record RoomBuildLadder(string Id, string Area, float[] Position, float[] Rotation, float Width, bool EnemyOnly, float[]? Top);
/// <summary>A collider the game builds itself, as vanilla builds its invisible walls: one non-trigger BoxCollider, no renderer, no
/// script. <c>PlayersOnly</c> picks the physics layer (see RoomRecipeAirWalls); the transform is a part's, the box is 4 m cubed
/// standing on its pivot.</summary>
internal sealed record RoomBuildAirWall(string Id, bool PlayersOnly, string Area, float[] Position, float[] Rotation, float[] Scale);
internal sealed record BlankRoomDesign(string? Function, IReadOnlyList<RoomBuildExit> Exits,
    IReadOnlyList<BlankRoomInteriorDoor> InteriorDoors);
internal sealed partial record RoomBuildRecipe
{
    /// <summary>Schema 1.0.0: the website exports every authored part, placement marker, area with its floor samples, exit and
    /// the interior links. The game derives nothing from segments: it checks the structure and builds exactly that.</summary>
    private static RoomBuildRecipe ReadBlank(JsonElement root)
    {
        OptionalFields(root, new[] { "schemaVersion", "id", "revision", "template", "resourceSetIds", "design" }, "hosts", "name");
        var id = Text(root, "id"); var revision = Text(root, "revision"); var template = Text(root, "template");
        if (!Token(id) || revision.Length != 64 || revision.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || template != BlankTemplate)
            throw new FormatException("blank room identity");
        var setIds = ReadResourceSets(root);
        var value = root.GetProperty("design");
        OptionalFields(value, new[] { "parts", "areas", "exits", "markers" }, "interiorDoors", "jumpLinks", "ladders", "function", "airWalls", "colliders", "collisionMeshes");

        string? function = value.TryGetProperty("function", out _) ? Text(value, "function") : null;
        // Extraction is the level's authored point (extraction-point.forge), not a room function. A root recipe stands in for a floor
        // transition: `start` the level's elevator, `dimension` another dimension's root (RoomRecipeFunctions).
        if (function is not (null or "start" or "dimension")) throw new FormatException("blank room function");
        var areas = ReadAreas(value.GetProperty("areas"));
        var areaIds = new HashSet<string>(areas.Select(area => area.Id), StringComparer.Ordinal);
        var parts = ReadParts(value.GetProperty("parts"), areaIds);
        var markers = ReadMarkers(value.GetProperty("markers"), areaIds);
        var airWalls = value.TryGetProperty("airWalls", out var wallRows) ? ReadAirWalls(wallRows, areaIds, parts) : Array.Empty<RoomBuildAirWall>();
        var collisionMeshes = value.TryGetProperty("collisionMeshes", out var meshRows) ? ReadCollisionMeshes(meshRows) : Array.Empty<RoomBuildCollisionMesh>();
        var colliders = value.TryGetProperty("colliders", out var colliderRows)
            ? ReadColliders(colliderRows, areaIds, new HashSet<string>(parts.Select(part => part.Id).Concat(airWalls.Select(wall => wall.Id)), StringComparer.Ordinal), collisionMeshes)
            : Array.Empty<RoomBuildCollider>();
        var linkIds = new HashSet<string>(StringComparer.Ordinal);
        var exits = ReadExits(value.GetProperty("exits"), areaIds, linkIds);
        var doors = value.TryGetProperty("interiorDoors", out var doorRows) ? ReadInteriorDoors(doorRows, areaIds, linkIds) : Array.Empty<BlankRoomInteriorDoor>();
        var jumpLinks = value.TryGetProperty("jumpLinks", out var linkRows) ? ReadJumpLinks(linkRows, linkIds) : Array.Empty<RoomBuildJumpLink>();
        var ladders = value.TryGetProperty("ladders", out var ladderRows) ? ReadLadders(ladderRows, linkIds, areaIds) : Array.Empty<RoomBuildLadder>();
        var doorways = exits.Select(exit => Exit(exit)).ToArray();
        return new RoomBuildRecipe(id, revision, template, setIds, parts)
        {
            Name = root.TryGetProperty("name", out _) ? Text(root, "name") : null,
            Hosts = ReadHosts(root, function, id, revision),
            Areas = areas,
            Markers = markers,
            BuildDoorways = doorways,
            InteriorDoorways = doors.Select(InteriorDoorway).ToArray(),
            JumpLinks = jumpLinks,
            Ladders = ladders,
            AirWalls = airWalls,
            Colliders = colliders,
            CollisionMeshes = collisionMeshes,
            Donors = DonorsOf(template, parts),
            Design = new(function, exits, doors)
        };
    }
    private static IReadOnlyList<RoomBuildAirWall> ReadAirWalls(JsonElement rows, HashSet<string> areas, IReadOnlyList<RoomBuildPart> parts)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumParts) throw new FormatException("blank room air walls");
        var walls = new List<RoomBuildAirWall>(rows.GetArrayLength());
        var ids = new HashSet<string>(parts.Select(part => part.Id), StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            Fields(row, "id", "mode", "area", "position", "rotation", "scale");
            string id = Text(row, "id"), mode = Text(row, "mode"), area = Text(row, "area");
            if (!Token(id) || !ids.Add(id)) throw new FormatException("blank room air wall identity");
            if (mode is not ("all" or "players")) throw new FormatException("blank room air wall mode");
            if (!areas.Contains(area)) throw new FormatException("blank room air wall area");
            var scale = Vector(row, "scale");
            if (scale.Any(x => x == 0)) throw new FormatException("blank room air wall scale");
            walls.Add(new(id, mode == "players", area, Vector(row, "position"), Vector(row, "rotation"), scale));
        }
        return walls.AsReadOnly();
    }
    private static IReadOnlyList<RoomBuildPart> ReadParts(JsonElement rows, HashSet<string> areas)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > MaximumParts) throw new FormatException("blank room parts");
        var parts = new List<RoomBuildPart>(rows.GetArrayLength()); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            OptionalFields(row, new[] { "id", "prefab", "node", "area", "position", "rotation", "scale" }, "parent", "appearance");
            string part = Text(row, "id"), prefab = Text(row, "prefab"), node = Text(row, "node", MaximumNodeChars, exact: true), area = Text(row, "area");
            string? parent = row.TryGetProperty("parent", out _) ? Text(row, "parent") : null;
            if (!Token(part) || !ids.Add(part) || !AssetPath(prefab) || !HierarchyPath(node) || parent != null && (!Token(parent) || parent == part)) throw new FormatException("blank room part identity");
            if (!areas.Contains(area)) throw new FormatException("blank room part area");
            var position = Vector(row, "position"); var rotation = Vector(row, "rotation"); var scale = Vector(row, "scale");
            // A negative axis mirrors the part, as vanilla geomorphs do with their spawners; only a zero axis flattens it.
            if (scale.Any(x => x == 0)) throw new FormatException("blank room part scale");
            // Kept as text: the game half reads it with the one surface-look reader every host shares (RoomRecipeLooks).
            string? appearance = null;
            if (row.TryGetProperty("appearance", out var look))
                appearance = look.ValueKind == JsonValueKind.Object ? look.GetRawText() : throw new FormatException("blank room part appearance");
            parts.Add(new(part, prefab, node, area, position, rotation, scale, parent, appearance));
        }
        // A part hangs on a part of this room, and the hanging never closes into a ring.
        var hosts = parts.ToDictionary(part => part.Id, part => part.Parent, StringComparer.Ordinal);
        foreach (var (id, parent) in hosts)
        {
            if (parent != null && !hosts.ContainsKey(parent)) throw new FormatException("blank room part parent");
            int steps = 0;
            for (var at = parent; at != null; at = hosts[at]) if (at == id || ++steps > hosts.Count) throw new FormatException("blank room part parent ring");
        }
        return parts.AsReadOnly();
    }
    private static IReadOnlyList<RoomBuildExit> ReadExits(JsonElement rows, HashSet<string> areas, HashSet<string> ids)
    {
        if (rows.ValueKind != JsonValueKind.Array) throw new FormatException("blank room exits");
        var exits = new List<RoomBuildExit>();
        foreach (var row in rows.EnumerateArray())
        {
            OptionalFields(row, new[] { "id", "position", "yaw", "size", "area" }, "subComplex", "seal", "pitch", "roll");
            var exit = new RoomBuildExit(Text(row, "id"), Number(row, "yaw"), Vector(row, "position"), Text(row, "size"), Text(row, "area"),
                row.TryGetProperty("subComplex", out _) ? SubComplexOf(row) : StorageSubComplex, row.TryGetProperty("seal", out _) ? Text(row, "seal") : null,
                row.TryGetProperty("pitch", out _) ? Number(row, "pitch") : 0, row.TryGetProperty("roll", out _) ? Number(row, "roll") : 0);
            if (!Token(exit.Id) || exit.Id.Length > 64 || !ids.Add(exit.Id)) throw new FormatException("blank room door id");
            if (!float.IsFinite(exit.Yaw) || !float.IsFinite(exit.Pitch) || !float.IsFinite(exit.Roll)) throw new FormatException("blank room exit rotation");
            if (exit.Seal != null && exit.Seal != SealOpen && !AssetPath(exit.Seal)) throw new FormatException("blank room exit seal");
            if (exit.Position.Any(value => !float.IsFinite(value) || Math.Abs(value) > 1e5f)) throw new FormatException("blank room exit position");
            if (exit.Size is not ("4x4" or "8x4")) throw new FormatException("blank room door size");
            if (!areas.Contains(exit.Area)) throw new FormatException("blank room exit area");
            exits.Add(exit);
        }
        return exits.AsReadOnly();
    }
    /// <summary>SubComplex.DigSite (0) to Mining_Portal (12), the values dump.cs defines.</summary>
    private static int SubComplexOf(JsonElement row)
    {
        var element = row.GetProperty("subComplex");
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value is < 0 or > 12) throw new FormatException("blank room exit sub complex");
        return value;
    }
    private static IReadOnlyList<RoomBuildJumpLink> ReadJumpLinks(JsonElement rows, HashSet<string> ids)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumParts) throw new FormatException("blank room jump links");
        var links = new List<RoomBuildJumpLink>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            Fields(row, "id", "start", "end", "bidirectional", "area");
            var id = Text(row, "id");
            var both = row.GetProperty("bidirectional");
            var area = row.GetProperty("area");
            if (!Token(id) || id.Length > 64 || !ids.Add(id)) throw new FormatException("blank room jump link id");
            if (both.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("blank room jump link direction");
            if (area.ValueKind != JsonValueKind.Number || !area.TryGetInt32(out var areaIndex) || areaIndex is < 0 or > 31) throw new FormatException("blank room jump link area");
            links.Add(new(id, Vector(row, "start"), Vector(row, "end"), both.GetBoolean(), areaIndex));
        }
        return links.AsReadOnly();
    }
    private static IReadOnlyList<RoomBuildLadder> ReadLadders(JsonElement rows, HashSet<string> ids, HashSet<string> areas)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumParts) throw new FormatException("blank room ladders");
        var ladders = new List<RoomBuildLadder>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            OptionalFields(row, new[] { "id", "area", "position", "rotation", "width", "enemyOnly" }, "top");
            string id = Text(row, "id"), area = Text(row, "area");
            var enemyOnly = row.GetProperty("enemyOnly"); float width = Number(row, "width");
            if (!Token(id) || id.Length > 64 || !ids.Add(id) || !areas.Contains(area)) throw new FormatException("blank room ladder identity");
            if (enemyOnly.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || !(width >= 0)) throw new FormatException("blank room ladder");
            ladders.Add(new(id, area, Vector(row, "position"), Vector(row, "rotation"), width, enemyOnly.GetBoolean(), row.TryGetProperty("top", out _) ? Vector(row, "top") : null));
        }
        return ladders.AsReadOnly();
    }
    public const string SealOpen = "open";
    /// <summary>A geomorph socket at the authored position and facing.</summary>
    private static RoomBuildDoor Exit(RoomBuildExit exit)
    {
        return new("Exit " + exit.Id, (float[])exit.Position.Clone(), Facing(exit.Yaw, exit.Pitch), exit.Yaw, exit.Size) { FromArea = exit.Area, SubComplex = exit.SubComplex, Pitch = exit.Pitch, Roll = exit.Roll };
    }
    /// <summary>The direction a door looks: +Z turned by Quaternion.Euler(pitch, yaw, 0). Rounded to 1e-6 so the quarter turns come out as exact axes, as the vanilla sockets' forwards are.</summary>
    private static float[] Facing(float yaw, float pitch)
    {
        double y = yaw * Math.PI / 180, x = pitch * Math.PI / 180;
        return new[] { (float)Math.Round(Math.Sin(y) * Math.Cos(x), 6) + 0f, (float)Math.Round(-Math.Sin(x), 6) + 0f, (float)Math.Round(Math.Cos(y) * Math.Cos(x), 6) + 0f };
    }
    private static void OptionalFields(JsonElement row, string[] required, params string[] optional)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new FormatException("blank room object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in row.EnumerateObject())
            if ((!required.Contains(field.Name, StringComparer.Ordinal) && !optional.Contains(field.Name, StringComparer.Ordinal)) || !seen.Add(field.Name))
                throw new FormatException("blank room field");
        if (required.Any(field => !seen.Contains(field))) throw new FormatException("blank room missing field");
    }
}
