using ForgeCore;
using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

/// <summary>Authored native scene hosts. Puzzle execution and completion remain SpecificChainPuzzleSpawnData.</summary>
internal sealed record ScanPlacement(MapLevelReference Level, string Id, string Filter, uint ChainedPuzzleId, RoomLocator Locator, IReadOnlyList<double> Position);

internal static class ScanPlacementManifest
{
    public const string FileNameOnly = "scan-placements.forge";
    public static bool TryParse(string text, out IReadOnlyList<ScanPlacement>? rows, out string? reason)
    {
        rows = null; reason = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !DocumentReading.Known(root, out _, "schemaVersion", "scans")
                || !FormatVersion.Matches(root)
                || !root.TryGetProperty("scans", out var scans) || scans.ValueKind != JsonValueKind.Array || scans.GetArrayLength() > ScanCoverage.MaximumRows)
            { reason = "Expected schemaVersion 1.0.0 and at most 512 scans."; return false; }
            var parsed = new List<ScanPlacement>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var filters = new HashSet<string>(StringComparer.Ordinal);
            var puzzles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var scan in scans.EnumerateArray())
            {
                if (scan.ValueKind != JsonValueKind.Object || !DocumentReading.Known(scan, out _, "level", "id", "filter", "chainedPuzzleId", "locator", "p_u")
                    || !DocumentReading.Text(scan, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level
                    || !DocumentReading.Text(scan, "id", out var id) || !DocumentReading.AuthorId(id!)
                    || !DocumentReading.Text(scan, "filter", out var filter) || !ValidFilter(filter!)
                    || !RuntimeJson.TryInteger(scan, "chainedPuzzleId", 1, uint.MaxValue, out var puzzle)
                    || !scan.TryGetProperty("locator", out var locatorElement) || !RoomLocator.TryParse(locatorElement, out var locator, out _, out _)
                    || !DocumentReading.Numbers(scan, "p_u", 3, -float.MaxValue, float.MaxValue, out var position)
                    || !ids.Add(level + "/" + id) || !filters.Add(level + "/" + filter) || !puzzles.Add(level + "/" + puzzle))
                { reason = "Invalid or duplicate scan identity, preset, room locator or finite position."; return false; }
                parsed.Add(new ScanPlacement(level, id!, filter!, (uint)puzzle, locator!, Array.AsReadOnly(position!)));
            }
            rows = parsed.AsReadOnly(); return true;
        }
        catch (JsonException error) { reason = error.Message; return false; }
    }

    private static bool ValidFilter(string value)
    {
        if (!value.StartsWith("ForgeScan_", StringComparison.Ordinal) || value.Length is <= 10 or > 210) return false;
        foreach (var c in value.AsSpan(10))
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')) return false;
        return true;
    }
}
