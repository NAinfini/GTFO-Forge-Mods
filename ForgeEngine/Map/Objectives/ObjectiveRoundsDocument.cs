using ForgeCore;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

/// <summary>One uplink round's overrides of the three waits the native verification prints (all optional) and the
/// scene scan that must finish before the verification counts.</summary>
internal sealed record UplinkRoundRow(int Round, float? StartSeconds, float? CompleteSeconds, float? RestoreSeconds, uint? EndScan);

/// <summary>One timed-terminal round's overrides of the sequence's single time values, the zones its receiver is picked
/// from and the scene scan that must finish before the confirmation counts.</summary>
internal sealed record TimedRoundRow(int Round, float? Seconds, float? ConfirmSeconds, IReadOnlyList<int> ReceiverZones, uint? EndScan);

/// <summary>One reactor wave verified by an event instead of a code, with the line shown where the code would be.</summary>
internal sealed record ReactorWaveRow(int Wave, string Text);

/// <summary>What one objective block changes per round. Rows name only what differs: an objective without a row, or a
/// round without a value, stays entirely native.</summary>
internal sealed record ObjectiveRoundsRow(MapLevelReference Level, uint Objective, string ScanPrompt, bool NoRepeat,
    IReadOnlyList<UplinkRoundRow> Uplink, IReadOnlyList<TimedRoundRow> Timed, IReadOnlyList<ReactorWaveRow> Reactor);

/// <summary>`forge/objective-rounds.forge`: the per-round authoring that native objective data has no field for.</summary>
internal static class ObjectiveRoundsDocument
{
    public const string FileName = "objective-rounds.forge";
    private const double MaximumSeconds = 86400;

    public static IReadOnlyList<ObjectiveRoundsRow> Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !DocumentReading.Known(root, "schemaVersion", "objectives")
            || !FormatVersion.Matches(root)
            || !root.TryGetProperty("objectives", out var objectives) || objectives.ValueKind != JsonValueKind.Array)
            throw new FormatException("objective-rounds-document");
        var rows = new List<ObjectiveRoundsRow>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in objectives.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !DocumentReading.Known(row, "level", "objective", "scanPrompt", "noRepeat", "uplink", "timed", "reactor")
                || !DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level
                || !RuntimeInteger(row, "objective", 1, uint.MaxValue, out var objective)
                || !identities.Add(level + "\0" + objective))
                throw new FormatException("objective-rounds-document");
            string prompt = row.TryGetProperty("scanPrompt", out _) && DocumentReading.Text(row, "scanPrompt", 1024, out var promptText) ? promptText! : "";
            bool noRepeat = row.TryGetProperty("noRepeat", out var repeat) && repeat.ValueKind == JsonValueKind.True;
            rows.Add(new ObjectiveRoundsRow(level, (uint)objective, prompt, noRepeat, Uplink(row), Timed(row), Reactor(row)));
        }
        return rows.AsReadOnly();
    }

    private static IReadOnlyList<UplinkRoundRow> Uplink(JsonElement owner)
    {
        var rows = new List<UplinkRoundRow>();
        var seen = new HashSet<int>();
        foreach (var row in Rows(owner, "uplink"))
        {
            if (!DocumentReading.Known(row, "round", "startSeconds", "completeSeconds", "restoreSeconds", "endScan"))
                throw new FormatException("objective-rounds-document");
            int round = Index(row, "round", seen);
            rows.Add(new UplinkRoundRow(round, Seconds(row, "startSeconds"), Seconds(row, "completeSeconds"), Seconds(row, "restoreSeconds"), Scan(row)));
        }
        return rows.AsReadOnly();
    }

    private static IReadOnlyList<TimedRoundRow> Timed(JsonElement owner)
    {
        var rows = new List<TimedRoundRow>();
        var seen = new HashSet<int>();
        foreach (var row in Rows(owner, "timed"))
        {
            if (!DocumentReading.Known(row, "round", "seconds", "confirmSeconds", "receiverZones", "endScan"))
                throw new FormatException("objective-rounds-document");
            int round = Index(row, "round", seen);
            var zones = new List<int>();
            if (row.TryGetProperty("receiverZones", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array) throw new FormatException("objective-rounds-document");
                foreach (var zone in list.EnumerateArray())
                {
                    if (zone.ValueKind != JsonValueKind.Number || !zone.TryGetInt32(out var index) || index < 0 || zones.Contains(index))
                        throw new FormatException("objective-rounds-document");
                    zones.Add(index);
                }
            }
            rows.Add(new TimedRoundRow(round, Seconds(row, "seconds"), Seconds(row, "confirmSeconds"), zones.AsReadOnly(), Scan(row)));
        }
        return rows.AsReadOnly();
    }

    private static IReadOnlyList<ReactorWaveRow> Reactor(JsonElement owner)
    {
        var rows = new List<ReactorWaveRow>();
        var seen = new HashSet<int>();
        foreach (var row in Rows(owner, "reactor"))
        {
            if (!DocumentReading.Known(row, "wave", "text") || !DocumentReading.Text(row, "text", 1024, out var text))
                throw new FormatException("objective-rounds-document");
            rows.Add(new ReactorWaveRow(Index(row, "wave", seen), text!));
        }
        return rows.AsReadOnly();
    }

    private static IEnumerable<JsonElement> Rows(JsonElement owner, string field)
    {
        if (!owner.TryGetProperty(field, out var rows)) yield break;
        if (rows.ValueKind != JsonValueKind.Array) throw new FormatException("objective-rounds-document");
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new FormatException("objective-rounds-document");
            yield return row;
        }
    }

    private static int Index(JsonElement row, string field, HashSet<int> seen)
    {
        if (!RuntimeInteger(row, field, 0, 1023, out var index) || !seen.Add((int)index))
            throw new FormatException("objective-rounds-document");
        return (int)index;
    }

    private static float? Seconds(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out _)) return null;
        if (!DocumentReading.Number(row, field, 0, MaximumSeconds, out var value)) throw new FormatException("objective-rounds-document");
        return (float)value;
    }

    private static uint? Scan(JsonElement row)
    {
        if (!row.TryGetProperty("endScan", out _)) return null;
        if (!RuntimeInteger(row, "endScan", 1, uint.MaxValue, out var scan)) throw new FormatException("objective-rounds-document");
        return (uint)scan;
    }

    private static bool RuntimeInteger(JsonElement row, string field, long minimum, long maximum, out long value)
        => ForgeEngine.Framework.RuntimeJson.TryInteger(row, field, minimum, maximum, out value);
}
