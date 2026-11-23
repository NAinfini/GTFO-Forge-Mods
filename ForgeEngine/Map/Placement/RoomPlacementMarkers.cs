using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

/// <summary>What a function point is built from: a MarkerProducer class and a marker datablock. The website names it for every function point.</summary>
internal sealed record RoomMarkerSource(string Producer, uint Block);
internal sealed record RoomPlacementMarker(string Id, string Area, float[] Position, float Yaw, string Category, string? Key, RoomMarkerSource? Source = null);

internal sealed partial record RoomBuildRecipe
{
    public const int MaximumMarkers = 65536;

    private static readonly HashSet<string> MarkerCategories = new(StringComparer.Ordinal)
    {
        "terminal", "generator", "generator-cluster", "container", "container-secure", "container-cluster",
        "small-item", "big-item", "hsu", "hsu-cluster", "bulkhead-controller", "disinfection", "shuttlebox", "event"
    };

    private static IReadOnlyList<RoomPlacementMarker> ReadMarkers(JsonElement rows, HashSet<string> areas)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumMarkers)
            throw new FormatException("blank room markers");
        var markers = new List<RoomPlacementMarker>(rows.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            OptionalFields(row, new[] { "id", "area", "position", "yaw", "category" }, "key", "source");
            string id = Text(row, "id"), area = Text(row, "area"), category = Text(row, "category");
            if (!Token(id) || id.Length > 64 || !ids.Add(id)) throw new FormatException("blank room marker identity");
            if (!areas.Contains(area)) throw new FormatException("blank room marker area");
            if (!MarkerCategories.Contains(category)) throw new FormatException("blank room marker category");
            bool hasKey = row.TryGetProperty("key", out _);
            if (category == "event" && !hasKey) throw new FormatException("blank room event marker key");
            if (category != "event" && hasKey) throw new FormatException("blank room function marker key");
            string? key = hasKey ? Text(row, "key") : null;
            if (key != null && key.IndexOfAny(ControlCharacters) >= 0) throw new FormatException("blank room event marker key");
            RoomMarkerSource? source = null;
            if (row.TryGetProperty("source", out var sourceRow))
            {
                Fields(sourceRow, "producer", "block");
                string producer = Text(sourceRow, "producer");
                if (category == "event" || !producer.EndsWith("MarkerProducer", StringComparison.Ordinal) || !sourceRow.GetProperty("block").TryGetUInt32(out var block) || block == 0)
                    throw new FormatException("blank room marker source");
                source = new(producer, block);
            }
            else if (category != "event") throw new FormatException("blank room marker source");
            markers.Add(new(id, area, Vector(row, "position"), Number(row, "yaw"), category, key, source));
        }
        return markers.AsReadOnly();
    }

    private static readonly char[] ControlCharacters = Enumerable.Range(0, 32).Select(value => (char)value).Append((char)127).ToArray();
}
