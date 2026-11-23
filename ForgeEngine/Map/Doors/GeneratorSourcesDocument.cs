using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeMap;

internal sealed record GeneratorSourceRow(MapLevelReference Level, MapObjectReference Address, DoorLockGeneratorPlacement Placement);
/** The native placement's provenance for every authored generator, including rules without a door consumer. */
internal static class GeneratorSourcesDocument
{
    public const string FileName = "generator-sources.forge";
    public static IReadOnlyList<GeneratorSourceRow> Parse(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); var root = doc.RootElement;
        Fields(root, "schemaVersion", "generators"); if (!FormatVersion.Matches(root)) throw new FormatException("door-lock-generator-source");
        var result = new List<GeneratorSourceRow>(); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("generators").EnumerateArray())
        {
            Fields(row, "level", "address", "placement");
            var level = MapLevelReference.TryParse(row.GetProperty("level").GetString()) ?? throw new FormatException("door-lock-level");
            var address = MapObjectGeneratorAddress.TryParse(row.GetProperty("address").GetString()) ?? throw new FormatException("door-lock-generator-source");
            var source = row.GetProperty("placement"); Fields(source, "index", "count", "placementId");
            var index = source.GetProperty("index").GetInt32(); var count = source.GetProperty("count").GetInt32(); var author = source.GetProperty("placementId").GetString();
            if (author == null || index < 0 || count <= index || MapObjectGeneratorAddress.CreateAuthor(int.Parse(address.Dimension, CultureInfo.InvariantCulture), int.Parse(address.Layer, CultureInfo.InvariantCulture), int.Parse(address.Zone, CultureInfo.InvariantCulture), author) != address
                || !identities.Add(level + "\0" + address)) throw new FormatException("door-lock-generator-source");
            result.Add(new(level, address, new(index, count, author)));
        }
        return result.AsReadOnly();
    }
    private static void Fields(JsonElement row, params string[] names)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new FormatException("door-lock-generator-source"); if (StrictJson.FirstUnknownOrRepeated(row, names) != null) throw new FormatException("door-lock-generator-source");
    }
}
