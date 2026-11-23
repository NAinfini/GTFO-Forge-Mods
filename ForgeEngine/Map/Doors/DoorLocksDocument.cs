using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

internal sealed record DoorLockGeneratorPlacement(int Index, int Count, string PlacementId);
internal sealed record DoorLockCondition(string Id, string Kind, string Label, MapObjectReference Address, string? Command);
internal sealed record DoorLockRow(MapLevelReference Level, MapObjectReference Door, string Rule, string PlanId,
    IReadOnlyList<DoorLockCondition> Conditions, JsonElement? KeyPuzzle, string RemainingTemplate, string KeyLabel, string? KeyConditionId = null);
internal static class DoorLocksCodes
{
    public const string Completed = "map.door-condition-completed";
    public const string Ready = "map.door-conditions-ready";
    public const string KeyEnabled = "map.door-key-enabled";
    public const string Refused = "map.door-condition-refused";
    public const string AlarmChanged = "map.door-alarm-changed";
    public const string AlarmFailed = "map.door-alarm-failed";
}
/** Configuration only. Completion state belongs exclusively to the compiled plan's level variables. */
internal static class DoorLocksDocument
{
    public const string FileName = "door-locks.forge";
    public static IReadOnlyList<DoorLockRow> Parse(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        Fields(root, "schemaVersion", "doors");
        if (!FormatVersion.Matches(root)) throw new FormatException("door-lock-document");
        var doors = root.GetProperty("doors");
        if (doors.ValueKind != JsonValueKind.Array) throw new FormatException("door-lock-document");
        var result = new List<DoorLockRow>(); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in doors.EnumerateArray())
        {
            Fields(row, "level", "door", "rule", "planId", "conditions", "keyPuzzle", "remainingTemplate", "keyLabel", "keyConditionId");
            var level = MapLevelReference.TryParse(Text(row, "level")) ?? throw new FormatException("door-lock-level");
            var door = MapObjectDoorAddress.TryParse(Text(row, "door")) ?? throw new FormatException("door-lock-door-missing");
            var rule = Text(row, "rule"); if (rule is not ("all" or "any")) throw new FormatException("door-lock-rule");
            if (!identities.Add(level + "\0" + door)) throw new FormatException("door-lock-duplicate");
            var conditions = new List<DoorLockCondition>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var condition in row.GetProperty("conditions").EnumerateArray())
            {
                Fields(condition, "id", "kind", "label", "address", "command");
                var id = Text(condition, "id"); if (!ids.Add(id)) throw new FormatException("door-lock-condition-duplicate");
                var kind = Text(condition, "kind");
                if (kind == "key") throw new FormatException("door-lock-multiple-keys");
                if (kind is not ("generator" or "terminal")) throw new FormatException("door-lock-condition");
                var address = kind == "generator" ? MapObjectGeneratorAddress.TryParse(Text(condition, "address")) : MapObjectTerminalAddress.TryParse(Text(condition, "address"));
                if (address == null || address.Category != kind) throw new FormatException("door-lock-source");
                if (kind == "generator" && MapObjectGeneratorAddress.IsGroupKey(address.Key)) throw new FormatException("door-lock-generator-not-independent");
                var command = kind == "terminal" ? Text(condition, "command") : null;
                if (command != null && command.Any(char.IsWhiteSpace)) throw new FormatException("door-lock-command");
                conditions.Add(new(id, kind, Text(condition, "label"), address, command));
            }
            var keyConditionId = row.TryGetProperty("keyConditionId", out _) ? Text(row, "keyConditionId") : null;
            if (conditions.Count == 0 && keyConditionId == null) throw new FormatException("door-lock-condition");
            JsonElement? key = null;
            if (row.TryGetProperty("keyPuzzle", out var puzzle))
            {
                Fields(puzzle, "PuzzleType", "CustomText", "PlacementCount", "ZonePlacementData");
                if (rule != "all" || puzzle.GetProperty("PuzzleType").GetInt32() != 1 || puzzle.GetProperty("PlacementCount").GetInt32() != 1) throw new FormatException("door-lock-multiple-keys");
                if (keyConditionId == null || conditions.Count == 0) throw new FormatException("door-lock-key-missing");
                var places = puzzle.GetProperty("ZonePlacementData");
                if (places.ValueKind != JsonValueKind.Array || places.GetArrayLength() == 0) throw new FormatException("door-lock-key-placement");
                foreach (var place in places.EnumerateArray())
                {
                    Fields(place, "LocalIndex", "Weights");
                    if (place.GetProperty("LocalIndex").GetInt32() < 0) throw new FormatException("door-lock-key-placement");
                    var weights = place.GetProperty("Weights"); Fields(weights, "Start", "Middle", "End");
                    foreach (var name in new[] { "Start", "Middle", "End" }) if (!weights.GetProperty(name).TryGetSingle(out var number) || !float.IsFinite(number) || number < 0) throw new FormatException("door-lock-key-placement");
                }
                key = puzzle.Clone();
            }
            var template = Text(row, "remainingTemplate"); if (!template.Contains("{conditions}", StringComparison.Ordinal)) throw new FormatException("door-lock-document");
            result.Add(new(level, door, rule, Text(row, "planId"), conditions.AsReadOnly(), key, template, Text(row, "keyLabel"), keyConditionId));
        }
        return result.AsReadOnly();
    }
    private static string Text(JsonElement row, string name)
    {
        var value = row.GetProperty(name); var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text) ? text : throw new FormatException("door-lock-document");
    }
    private static void Fields(JsonElement row, params string[] fields)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new FormatException("door-lock-document");
        if (StrictJson.FirstUnknownOrRepeated(row, fields) != null) throw new FormatException("door-lock-document");
    }
}
