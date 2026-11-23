using System;
using ForgeCore;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

/// <summary>One authored part: a clone of the vanilla object at <c>Node</c> inside the vanilla prefab <c>Prefab</c> (an
/// LG_PrefabSpawner, a marker producer, a baked mesh, a light, a collider or any other object, with its whole subtree),
/// placed under <c>Area</c> with the authored transform. A path segment <c>name#k</c> is the k-th (0-based) of the siblings
/// sharing that name. <c>Parent</c> names the part this one hangs on: once both are built the game parents this part's object to that
/// part's (keeping the authored pose), so it moves and turns with it. <c>Appearance</c> is the JSON text of the author's colour, glow and
/// visibility of the part (the website's ModelPartAppearance); the game half reads it with the one surface-look reader and paints the built
/// object (RoomRecipeLooks). No executable code or serialized Unity asset enters this document.</summary>
internal sealed record RoomBuildPart(string Id, string Prefab, string Node, string Area, float[] Position, float[] Rotation, float[] Scale, string? Parent = null,
    string? Appearance = null);
/// <summary>The object prototypes one vanilla prefab must supply before its resource arrays expand them.</summary>
internal sealed record RoomBuildDonor(string Prefab, IReadOnlyList<string> Nodes);
internal sealed partial record RoomBuildRecipe(string Id, string Revision, string Template, IReadOnlyList<uint> ResourceSetIds,
    IReadOnlyList<RoomBuildPart> Parts)
{
    public const int MaximumTextChars = 256;
    /** A part's vanilla object path: deep baked marker trees reach several hundred characters. */
    public const int MaximumNodeChars = 1024;
    /// <summary>The node path that names the prefab root itself: a whole vanilla prefab (a marker composition) cloned as one part.</summary>
    public const string PrefabRootNode = ".";
    /// <summary>Format bound against damaged input, not an authoring limit.</summary>
    public const int MaximumParts = 65536;
    /// <summary>The single-cell geomorph whose LG_Geomorph and node volume every authored room starts from.</summary>
    public const string BlankTemplate = "Assets/AssetPrefabs/Complex/Mining/Geomorphs/Storage/geo_64x64_mining_storage_tutorial_VS_01.prefab";
    /// <summary>The template's own flat 4 m wall panel, cloned once per 4 m of doorway width to close an exit the native generator leaves unpaired.</summary>
    public const string SealWallSpawner = "Area A/EnvProps/LG_PrefabSpawner_BuildingPart_StorageWall_Metal_4x4_b (12)";
    /// <summary>Where the seal panels of an unused doorway of this size stand along its width (metres from the socket):
    /// a 4x4 doorway takes one panel, an 8x4 doorway two side by side.</summary>
    public static IReadOnlyList<float> SealPanelOffsets(string size) => size switch
    {
        "4x4" => new[] { 0f },
        "8x4" => new[] { -2f, 2f },
        _ => throw new ArgumentException("seal panel size " + size, nameof(size)),
    };
    /// <summary>SubComplex.Storage (dump.cs), the tutorial donor's value an exit takes when its recipe names none.</summary>
    public const int StorageSubComplex = 2;
    public string ResourceKey => "Assets/Forge/Rooms/" + Id + "/" + Revision + ".prefab";
    /// <summary>The exact installed input, for comparison across peers; not a navigation checksum.</summary>
    public string Sha256 { get; private init; } = "";
    public BlankRoomDesign? Design { get; private init; }
    /// <summary>The room's readable work name, carried by its room file.</summary>
    public string? Name { get; private init; }
    /// <summary>A room file in <c>forge/resources/</c> is named by the SHA-256 of its bytes and this extension (I-RESOLVE).</summary>
    public const string FileExtension = ".room.forge";

    public IReadOnlyList<RoomBuildArea> Areas { get; private init; } = Array.Empty<RoomBuildArea>();
    public IReadOnlyList<RoomPlacementMarker> Markers { get; private init; } = Array.Empty<RoomPlacementMarker>();
    public IReadOnlyList<RoomBuildDoor> BuildDoorways { get; private init; } = Array.Empty<RoomBuildDoor>();
    public IReadOnlyList<RoomBuildDoor> InteriorDoorways { get; private init; } = Array.Empty<RoomBuildDoor>();
    /// <summary>The Unity OffMeshLinks the room carries (vanilla OffLink_Jump objects), built under the room root.</summary>
    public IReadOnlyList<RoomBuildJumpLink> JumpLinks { get; private init; } = Array.Empty<RoomBuildJumpLink>();
    public IReadOnlyList<RoomBuildLadder> Ladders { get; private init; } = Array.Empty<RoomBuildLadder>();
    /// <summary>The recipe's air walls, built as vanilla builds its invisible walls (RoomRecipeAirWalls).</summary>
    public IReadOnlyList<RoomBuildAirWall> AirWalls { get; private init; } = Array.Empty<RoomBuildAirWall>();
    public IEnumerable<RoomBuildDoor> InteriorLinks => InteriorDoorways;
    /// <summary>Every vanilla prefab this room clones objects from, the template first (it always supplies the seal wall).</summary>
    public IReadOnlyList<RoomBuildDonor> Donors { get; private init; } = Array.Empty<RoomBuildDonor>();
    /// <summary>The highest authored part pivot above the room floor. The game holds no part bounds; it sizes the
    /// navigation volume from this.</summary>
    public float Height => Parts.Count == 0 ? 0 : Math.Max(0, Parts.Max(part => part.Position[1]));
    public bool AllowsResourceSet(uint id) => ResourceSetIds.Contains(id);
    private readonly HashSet<string> partIds = new(Parts.Select(part => part.Id), StringComparer.Ordinal);
    public bool HasPart(string id) => partIds.Contains(id);
    public int AreaIndex(string id)
    {
        for (int i = 0; i < Areas.Count; i++) if (Areas[i].Id == id) return i;
        return -1;
    }

    /// <summary>A package's <c>room-build.forge</c> lists per room the hash of its room file plus this package's own resource-set
    /// numbers and level hosts; the room file holds the rest of the recipe, so one room used by two maps is one file.
    /// <paramref name="room"/> returns a room file's bytes by hash (found through I-RESOLVE); a missing file throws there.</summary>
    public static IReadOnlyList<RoomBuildRecipe> ReadMany(string text, Func<string, byte[]> room)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Fields(root, "schemaVersion", "rooms");
        var version = root.GetProperty("schemaVersion");
        var rows = root.GetProperty("rooms");
        if (version.ValueKind != JsonValueKind.String || version.GetString() != FormatVersion.Current
            || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 1024)
            throw new FormatException("room recipe collection");
        var recipes = rows.EnumerateArray().Select(row =>
        {
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("hosts", out _)) Fields(row, "sha256", "resourceSetIds", "hosts");
            else Fields(row, "sha256", "resourceSetIds");
            var sha = Text(row, "sha256");
            if (sha.Length != 64 || sha.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new FormatException("room file hash");
            var bytes = room(sha);
            if (ForgeCore.Hash.Sha256Hex(bytes) != sha) throw new FormatException("room file hash: " + sha);
            if (System.Text.Json.Nodes.JsonNode.Parse(bytes) is not System.Text.Json.Nodes.JsonObject body
                || body.ContainsKey("resourceSetIds") || body.ContainsKey("hosts")) throw new FormatException("room file: " + sha);
            body["resourceSetIds"] = System.Text.Json.Nodes.JsonNode.Parse(row.GetProperty("resourceSetIds").GetRawText());
            if (row.TryGetProperty("hosts", out var hosts)) body["hosts"] = System.Text.Json.Nodes.JsonNode.Parse(hosts.GetRawText());
            return Read(body.ToJsonString());
        }).ToArray();
        if (recipes.Select(row => row.ResourceKey).Distinct(StringComparer.Ordinal).Count() != recipes.Length) throw new FormatException("duplicate room recipe");
        RequireUniqueFunctionalHosts(recipes);
        return recipes;
    }
    public static RoomBuildRecipe Read(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.String || version.GetString() != FormatVersion.Current)
            throw new FormatException("room recipe version");
        var recipe = ReadBlank(root);
        return recipe with { Sha256 = ForgeCore.Hash.Sha256Hex(text) };
    }
    private static IReadOnlyList<RoomBuildDonor> DonorsOf(string template, IReadOnlyList<RoomBuildPart> parts)
    {
        var order = new List<string> { template };
        var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal) { [template] = new() { SealWallSpawner } };
        foreach (var part in parts)
        {
            if (!paths.TryGetValue(part.Prefab, out var list)) { paths.Add(part.Prefab, list = new()); order.Add(part.Prefab); }
            if (!list.Contains(part.Node, StringComparer.Ordinal)) list.Add(part.Node);
        }
        return order.Select(prefab => new RoomBuildDonor(prefab, paths[prefab].AsReadOnly())).ToArray();
    }
    private static uint[] ReadResourceSets(JsonElement root)
    {
        var sets = root.GetProperty("resourceSetIds");
        if (sets.ValueKind != JsonValueKind.Array || sets.GetArrayLength() is < 1 or > 1024) throw new FormatException("room resource sets");
        var setIds = sets.EnumerateArray().Select(row => row.ValueKind == JsonValueKind.Number && row.TryGetUInt32(out var n) ? n : 0).ToArray();
        if (setIds.Any(n => n == 0) || setIds.Distinct().Count() != setIds.Length) throw new FormatException("room resource set");
        return setIds;
    }
    private static bool Token(string value) => value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '_');
    /// <summary>A path below the prefab root, or <see cref="PrefabRootNode"/> for the root itself.</summary>
    private static bool HierarchyPath(string path) => path == PrefabRootNode || ForgeCore.PosixPath.IsRelative(path);
    private static bool AssetPath(string path) => path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".prefab", StringComparison.Ordinal)
        && ForgeCore.PosixPath.IsRelative(path);
    /// <summary>`exact` keeps surrounding spaces: a node path addresses vanilla objects by name, and some names end in a space.</summary>
    private static string Text(JsonElement row, string key, int maximum = MaximumTextChars, bool exact = false)
    {
        var element = row.GetProperty(key);
        var value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && (exact || value == value.Trim())
            ? value : throw new FormatException("room recipe text: " + key);
    }
    private static float Number(JsonElement row, string key)
    {
        var element = row.GetProperty(key);
        if (element.ValueKind != JsonValueKind.Number) throw new FormatException("room recipe number: " + key);
        var value = element.GetSingle();
        return float.IsFinite(value) ? value : throw new FormatException("room recipe number: " + key);
    }
    private static float[] Vector(JsonElement row, string key) => VectorOf(row.GetProperty(key), key);
    private static float[] VectorOf(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) throw new FormatException("room recipe vector: " + key);
        var result = value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetSingle() : float.NaN).ToArray();
        if (result.Any(x => !float.IsFinite(x))) throw new FormatException("room recipe vector: " + key);
        return result;
    }
    private static void Fields(JsonElement row, params string[] fields)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new FormatException("room recipe object");
        if (StrictJson.FirstUnknownOrRepeated(row, fields) != null) throw new FormatException("room recipe field");
        if (row.EnumerateObject().Count() != fields.Length) throw new FormatException("room recipe missing field");
    }
}
