using ForgeCore;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

/// <summary>
/// The one extraction of a level that is not the elevator (contract §4.5 M13), with the optional radius of the level's
/// own extraction scan. A point row carries its authored room and Area identities and the point and facing in that
/// room's prefab-root frame: the level's single native `LG_LevelExitGeo` host is attached there. A room row
/// (<see cref="RoomExit"/>) carries only the room: the vanilla `LG_LevelExitGeo` the room's own geomorph already
/// holds is the host, with its own scan align and exit Area. A level without a row keeps the native elevator.
/// </summary>
internal sealed record ExtractionPoint(MapLevelReference Level, RoomLocator Locator, string AreaId,
    IReadOnlyList<double> Position, double YawDegrees, double? Radius)
{
    /// <summary>The room's own vanilla exit is the extraction: no Area, point or facing is authored.</summary>
    internal bool RoomExit { get; init; }
}

internal static class ExtractionPointManifest
{
    public const string FileNameOnly = "extraction-point.forge";
    public const int MaximumRows = 512;

    public static bool TryParse(string text, out IReadOnlyList<ExtractionPoint>? rows, out string? reason)
    {
        rows = null; reason = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !DocumentReading.Known(root, out _, "schemaVersion", "points")
                || !FormatVersion.Matches(root)
                || !root.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() > MaximumRows)
            { reason = "Expected schemaVersion 1.0.0 and at most 512 extraction points."; return false; }
            var parsed = new List<ExtractionPoint>();
            var levels = new HashSet<MapLevelReference>();
            foreach (var point in points.EnumerateArray())
            {
                // The format only grows: a row without `mode` is a point; `mode: "room"` is the room's own vanilla exit,
                // which carries no Area, position or facing.
                if (point.ValueKind != JsonValueKind.Object
                    || !DocumentReading.Text(point, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level
                    || !point.TryGetProperty("room", out var roomElement) || !RoomLocator.TryParse(roomElement, out var locator, out _, out _)
                    || locator!.InstanceId == null)
                { reason = "Invalid extraction level or authored room identity."; return false; }
                var roomExit = point.TryGetProperty("mode", out _);
                string? areaId = null;
                double[]? position = null;
                double yaw = 0;
                if (roomExit)
                {
                    if (!DocumentReading.Known(point, out _, "level", "room", "mode", "radius")
                        || !DocumentReading.Text(point, "mode", out var mode) || mode != "room")
                    { reason = "A room extraction row is {level, room, mode: \"room\", radius?}."; return false; }
                }
                else if (!DocumentReading.Known(point, out _, "level", "room", "areaId", "position", "yaw_u", "radius")
                    || !DocumentReading.Text(point, "areaId", 2048, out areaId)
                    || !Single(point, out position)
                    || !DocumentReading.Number(point, "yaw_u", -float.MaxValue, float.MaxValue, out yaw)
                    || !float.IsFinite((float)yaw))
                { reason = "Invalid extraction point Area identity, finite position or finite facing."; return false; }
                double? radius = null;
                if (point.TryGetProperty("radius", out var radiusElement))
                {
                    if (radiusElement.ValueKind != JsonValueKind.Number || !radiusElement.TryGetDouble(out var value) || !ValidRadius(value))
                    { reason = "An extraction radius is a positive number whose square is a finite Single."; return false; }
                    radius = value;
                }
                // A level has one extraction. A second row would be a second host, which the level cannot carry.
                if (!levels.Add(level)) { reason = "A level has at most one extraction point."; return false; }
                var row = new ExtractionPoint(level, locator, areaId ?? "", Array.AsReadOnly(position ?? Array.Empty<double>()), yaw, radius);
                parsed.Add(row with { RoomExit = roomExit });
            }
            rows = parsed.AsReadOnly(); return true;
        }
        catch (JsonException error) { reason = error.Message; return false; }
    }

    private static bool Single(JsonElement row, out double[]? position)
    {
        if (!DocumentReading.Numbers(row, "position", 3, -float.MaxValue, float.MaxValue, out position)) return false;
        foreach (var axis in position!) if (!float.IsFinite((float)axis)) return false;
        return true;
    }

    /// <summary>CP_PlayerScanner keeps both the radius and its square as Singles; the square must stay finite.</summary>
    internal static bool ValidRadius(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return false;
        var single = (float)value;
        return float.IsFinite(single) && single > 0 && float.IsFinite(single * single);
    }
}
