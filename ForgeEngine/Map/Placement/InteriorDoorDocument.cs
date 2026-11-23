using ForgeCore;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

internal sealed record RoomDoorwayPose(double X, double Y, double Z, double ForwardX, double ForwardY, double ForwardZ);
internal sealed record InteriorDoorRow(MapLevelReference Level, string Id, string RoomInstance,
    RoomLocator Room, string DoorId, RoomDoorwayPose Doorway)
{
    public MapObjectReference Address => new(MapObjectCategories.Door,
        Room.Dimension.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Room.Layer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Room.LocalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), InteriorDoorIdentity.Key(RoomInstance, DoorId));
}

/// <summary>Generation selectors admitted atomically with their owning Rundown, not a second state service.</summary>
internal static class InteriorDoorDocument
{
    public const string FileName = "interior-doors.forge";
    public static IReadOnlyList<InteriorDoorRow> Parse(string text)
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Fields(root, "schemaVersion", "entries");
        if (!FormatVersion.Matches(root)
            || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw Invalid("Expected schemaVersion 1.0.0 and entries.");
        var result = new List<InteriorDoorRow>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in entries.EnumerateArray())
        {
            Fields(row, "level", "id", "room", "doorway");
            if (!DocumentReading.Text(row, "level", 128, out var levelText) || MapLevelReference.TryParse(levelText) is not { } level
                || !DocumentReading.Text(row, "id", 8192, out var id) || InteriorDoorIdentity.ParseObjectId(id!) is not { } pair
                || !DocumentReading.AuthorId(pair.Room) || !ids.Add(level + "/" + id)) throw Invalid("Invalid or repeated level/door identity.");
            if (!row.TryGetProperty("room", out var roomJson)
                || !RoomLocator.TryParse(roomJson, out var room, out _, out var reason)) throw Invalid("Invalid room locator.");
            if (room!.InstanceId != null && room.InstanceId != pair.Room) throw Invalid("Door and room instance identities differ.");
            if (!row.TryGetProperty("doorway", out var pose)) throw Invalid("Missing doorway.");
            Fields(pose, "id", "position", "forward");
            if (!DocumentReading.Text(pose, "id", 512, out var door) || door != pair.Door
                || !DocumentReading.Numbers(pose, "position", 3, -float.MaxValue, float.MaxValue, out var position)
                || !DocumentReading.Numbers(pose, "forward", 3, -1, 1, out var forward)) throw Invalid("Doorway identity or pose is invalid.");
            var f = forward!;
            if (Math.Abs(f[0] * f[0] + f[1] * f[1] + f[2] * f[2] - 1) > .0001) throw Invalid("Doorway facing must be a unit vector.");
            var p = position!;
            result.Add(new(level, id!, pair.Room, room, door!, new(p[0], p[1], p[2], f[0], f[1], f[2])));
        }
        return result.AsReadOnly();
    }
    private static void Fields(JsonElement row, params string[] names)
    {
        if (row.ValueKind != JsonValueKind.Object || !DocumentReading.Known(row, out _, names)) throw Invalid("Unknown field or non-object row.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in row.EnumerateObject()) if (!seen.Add(property.Name)) throw Invalid("Repeated JSON field: " + property.Name);
    }
    private static FormatException Invalid(string reason) => new("interior-door-document: " + reason);
}
