using ForgeCore;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Room furnishings (I-RESOLVE): the models a room definition carries are one file per room, named by the SHA-256 of its
/// bytes (<c>forge/resources/&lt;sha256&gt;.furnishing.forge</c>: the room work's id and name and per model its id, room-local pose,
/// flattened content and optional <c>"collision": "native"</c>, read by the composed-model reader). The map's <c>room-furnishings.forge</c> names per placed room its id, level, room and furnishing hash, so a
/// furnishing two rooms or two maps use is one file. Each model becomes a composed placed model with id <c>&lt;model&gt;@&lt;room row&gt;</c>.</summary>
internal static class RoomFurnishing
{
    public const string FileNameOnly = "room-furnishings.forge";
    public const string FileExtension = ".furnishing.forge";
    public const string DocumentPath = "forge/" + FileNameOnly;
    private const string ResourceDirectory = "forge/resources/";
    private const int MaximumRooms = 4096, MaximumModels = 256;

    /// <summary>The package-relative path of the furnishing file with this hash.</summary>
    public static string FilePath(string sha) => ResourceDirectory + sha + FileExtension;

    /// <summary><paramref name="file"/> returns a furnishing file's bytes by hash (found through I-RESOLVE); a missing file throws there.</summary>
    public static IReadOnlyList<PlacedModel> ReadMany(string text, Func<string, byte[]> file)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!DocumentReading.Known(root, "schemaVersion", "rooms") || !FormatVersion.Matches(root)
            || !root.TryGetProperty("rooms", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > MaximumRooms)
            throw new FormatException("room furnishing references");
        var models = new JsonArray();
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (!DocumentReading.Known(row, "id", "level", "room", "sha256")
                || !DocumentReading.Text(row, "id", PlacedModelManifest.MaximumIdLength, out var id) || !PlacedModelAddress.IsId(id)
                || !DocumentReading.Text(row, "sha256", RoomLocator.RevisionChars, out var sha) || !PublishedResources.IsSha256(sha!)
                || !row.TryGetProperty("level", out var level) || !row.TryGetProperty("room", out var room))
                throw new FormatException("room furnishing reference");
            if (!files.TryGetValue(sha!, out var furnishing)) files.Add(sha!, furnishing = Read(sha!, file(sha!)));
            foreach (var model in furnishing)
            {
                var placed = (JsonObject)JsonNode.Parse(model)!;
                placed["id"] = placed["id"]!.GetValue<string>() + "@" + id;
                placed["level"] = JsonNode.Parse(level.GetRawText());
                placed["room"] = JsonNode.Parse(room.GetRawText());
                placed["collider"] = new JsonObject { ["center"] = new JsonArray(0, 0, 0), ["size"] = new JsonArray(0, 0, 0) };
                models.Add(placed);
            }
        }
        // The rows are composed placed models in every other respect, so the one composed-model reader admits them.
        if (!PlacedModelManifest.TryParseComposed(new JsonObject { ["schemaVersion"] = FormatVersion.Current, ["models"] = models }.ToJsonString(),
                out var placedModels, out var code, out var reason))
            throw new FormatException(code + ": " + reason);
        return placedModels!;
    }

    private static List<string> Read(string sha, byte[] bytes)
    {
        if (ForgeCore.Hash.Sha256Hex(bytes) != sha) throw new FormatException("furnishing file hash: " + sha);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!DocumentReading.Known(root, "schemaVersion", "id", "name", "models") || !FormatVersion.Matches(root)
            || !DocumentReading.Text(root, "id", RoomLocator.MaximumRoomIdChars, out _) || !DocumentReading.Text(root, "name", out _)
            || !root.TryGetProperty("models", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > MaximumModels)
            throw new FormatException("furnishing file: " + sha);
        var models = new List<string>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!DocumentReading.Known(row, "id", "position", "rotation", "scale", "content", "collision") || !DocumentReading.Text(row, "id", PlacedModelManifest.MaximumIdLength, out _))
                throw new FormatException("furnishing model: " + sha);
            models.Add(row.GetRawText());
        }
        return models;
    }
}
