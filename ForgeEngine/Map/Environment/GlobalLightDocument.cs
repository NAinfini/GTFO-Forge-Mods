using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace ForgeMap;

internal readonly record struct GlobalLightValue(float? Brightness, Vector3? Color)
{
    public bool Configured => Brightness.HasValue || Color.HasValue;
    public GlobalLightValue Over(GlobalLightValue parent) => new(Brightness ?? parent.Brightness, Color ?? parent.Color);
}
internal sealed record GlobalLightZone(int Dimension, int Layer, int Zone, GlobalLightValue Value);
internal sealed record GlobalLightLevel(MapLevelReference Level, GlobalLightValue Value, IReadOnlyList<GlobalLightZone> Zones);

/// <summary>Production reader for the same document downloaded by the Map authoring path.</summary>
internal static class GlobalLightDocument
{
    public const string FileName = "global-light.forge";
    public static IReadOnlyList<GlobalLightLevel> Parse(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        Shape(root, "schemaVersion", "levels");
        if (!FormatVersion.Matches(root)) throw new FormatException("global-light-schema");
        var rows = new List<GlobalLightLevel>(); var levels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("levels").EnumerateArray())
        {
            Shape(row, "level", "brightness", "color", "zones");
            string level = row.GetProperty("level").GetString() ?? throw new FormatException("global-light-level");
            if (string.IsNullOrWhiteSpace(level) || !levels.Add(level)) throw new FormatException("global-light-level");
            var zones = new List<GlobalLightZone>(); var addresses = new HashSet<(int, int, int)>();
            foreach (var zone in row.GetProperty("zones").EnumerateArray())
            {
                Shape(zone, "dimension", "layer", "zone", "brightness", "color");
                int dimension = zone.GetProperty("dimension").GetInt32(), layer = zone.GetProperty("layer").GetInt32(), index = zone.GetProperty("zone").GetInt32();
                var value = ReadValue(zone);
                if (dimension < 0 || layer < 0 || layer > 2 || index < 0 || !value.Configured || !addresses.Add((dimension, layer, index))) throw new FormatException("global-light-zone");
                zones.Add(new(dimension, layer, index, value));
            }
            rows.Add(new(MapLevelReference.TryParse(level) ?? throw new FormatException("global-light-level"), ReadValue(row), zones.AsReadOnly()));
        }
        return rows.AsReadOnly();
    }
    private static GlobalLightValue ReadValue(JsonElement row)
    {
        float? brightness = null; Vector3? color = null;
        if (row.TryGetProperty("brightness", out var number))
        {
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetSingle(out float value) || !float.IsFinite(value) || value < 0) throw new FormatException("global-light-brightness");
            brightness = value;
        }
        if (row.TryGetProperty("color", out var raw))
        {
            string? text = raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
            if (text == null || text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)) throw new FormatException("global-light-color");
            color = new Vector3((rgb >> 16) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
        }
        return new(brightness, color);
    }
    private static void Shape(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("global-light-object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (Array.IndexOf(fields, field.Name) < 0 || !seen.Add(field.Name)) throw new FormatException("global-light-field");
    }
}

internal static class GlobalLightCodes
{
    public const string Applied = "map.global-light-applied";
    public const string OverrideChanged = "map.global-light-override-changed";
    public const string Restored = "map.global-light-restored";
    public const string Refused = "map.global-light-refused";
}
