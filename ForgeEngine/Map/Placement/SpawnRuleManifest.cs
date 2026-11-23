using ForgeCore;
using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeMap;

/// <summary>What a random spawn rule generates: vanilla's zone big-pickup distribution row, one (group type, difficulty) pair of a zone's
/// random enemy list, or the author's random placed-model rule.</summary>
internal enum SpawnRuleKind { Item, Enemy, Model }

/// <summary>The zone a vanilla-spawned rule belongs to, in the native coordinates every map address uses.</summary>
internal sealed record SpawnRuleZone(int Dimension, int Layer, int Zone);

/// <summary>
/// One random spawn rule of one level (contract M16): `Expected` instances are produced by the level's own
/// generation, and `Attached` of them (0 to `Expected`) carry the rule's behaviours. An item rule is told apart at
/// the vanilla big-pickup call by its zone and item id (the website keeps one row per zone and item, so the pair is
/// unique); an enemy rule by its zone and the (group type, difficulty) pair of the zone's random enemy list entry, which is what the
/// spawned group's data block carries (the website keeps one rule per zone and pair). A model rule has neither: ForgeMap
/// itself places its instances on free big-item spots of its `Zones`, and `Models` names the `Expected` composed
/// placements (one per piece it may place, in order) that stand in for those instances until a spot is chosen for each.
/// </summary>
internal sealed record SpawnRule(string Id, MapLevelReference Level, SpawnRuleKind Kind, SpawnRuleZone? Zone,
    uint ItemId, int GroupType, int Difficulty, int Expected, int Attached,
    IReadOnlyList<SpawnRuleZone>? Zones = null, IReadOnlyList<string>? Models = null);

/// <summary>The document `plugins/&lt;package&gt;/forge/spawn-rules.forge` and its row-by-row reading: a row that does
/// not read is refused with its own code and left out, like every other placement document.</summary>
internal sealed record SpawnRuleDocument(IReadOnlyList<SpawnRule> Rules, IReadOnlyList<FixedPlacementRefusal> Refusals);

internal static class SpawnRuleManifest
{
    public const int MaximumRules = 512;
    public const int MaximumExpected = 4096;
    public const int MaximumModelZones = 64;
    public const string FileName = "spawn-rules.forge";

    /// <summary>The rule id as it appears in the `item-rule` / `enemy-rule` / `model-rule` address: ASCII letters,
    /// digits and `-`, 1 to 64 characters, so the same text is a valid address key on both sides.</summary>
    public static bool IdIsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        foreach (var c in id)
            if (!(c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c == '-')) return false;
        return true;
    }

    public static bool TryParse(string text, out SpawnRuleDocument? document, out string? code, out string? reason)
    {
        document = null; code = null; reason = null;
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(text); }
        catch (JsonException error) { code = "spawn-rule-json"; reason = error.Message; return false; }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { code = "spawn-rule-json"; reason = "The document's root is not an object."; return false; }
            if (!DocumentReading.Known(root, out var unknown, "schemaVersion", "rules"))
            { code = "spawn-rule-field"; reason = "Unknown field `" + unknown + "`."; return false; }
            if (!FormatVersion.Matches(root))
            { code = "spawn-rule-schema"; reason = "The document must declare schemaVersion " + FormatVersion.Current + "."; return false; }
            if (!root.TryGetProperty("rules", out var rows) || rows.ValueKind != JsonValueKind.Array)
            { code = "spawn-rule-rows"; reason = "The document carries no `rules` array."; return false; }
            if (rows.GetArrayLength() > MaximumRules)
            { code = "spawn-rule-limit"; reason = "The document carries " + rows.GetArrayLength() + " rules; the cap is " + MaximumRules + "."; return false; }

            var rules = new List<SpawnRule>();
            var refusals = new List<FixedPlacementRefusal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                index++;
                if (!TryRule(row, out var rule, out var rowCode, out var rowReason))
                {
                    refusals.Add(new FixedPlacementRefusal(rowCode!, "row " + index.ToString(CultureInfo.InvariantCulture) + ": " + rowReason));
                    continue;
                }
                if (!seen.Add(rule!.Id))
                {
                    refusals.Add(new FixedPlacementRefusal("spawn-rule-duplicate",
                        "row " + index.ToString(CultureInfo.InvariantCulture) + ": rule `" + rule.Id + "` is claimed more than once; the later row is left out."));
                    continue;
                }
                rules.Add(rule);
            }
            document = new SpawnRuleDocument(Array.AsReadOnly(rules.ToArray()), Array.AsReadOnly(refusals.ToArray()));
            return true;
        }
    }

    private static bool TryRule(JsonElement row, out SpawnRule? rule, out string? code, out string? reason)
    {
        rule = null; code = "spawn-rule-row"; reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "A rule row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "kind", "zone", "zones", "models", "itemId", "groupType", "difficulty", "expected", "attached"))
        { code = "spawn-rule-field"; reason = "Unknown rule field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "id", 64, out var id) || !IdIsValid(id))
        { code = "spawn-rule-id"; reason = "A rule id is 1 to 64 ASCII letters, digits or `-`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "spawn-rule-level"; reason = "A rule needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!DocumentReading.Text(row, "kind", 16, out var kindText) || !TryKind(kindText!, out var kind))
        { code = "spawn-rule-kind"; reason = "A rule kind is `item`, `enemy` or `model`."; return false; }
        if (!RuntimeJson.TryInteger(row, "expected", 1, MaximumExpected, out var expected))
        { code = "spawn-rule-expected"; reason = "`expected` is the number of instances the rule produces, 1 to " + MaximumExpected + "."; return false; }
        if (!RuntimeJson.TryInteger(row, "attached", 0, expected, out var attached))
        { code = "spawn-rule-attached"; reason = "`attached` is how many of the instances carry behaviours, 0 to `expected`."; return false; }

        SpawnRuleZone? zone = null; long itemId = 0, groupType = 0, difficulty = 0;
        List<SpawnRuleZone>? zones = null; List<string>? models = null;
        if (kind == SpawnRuleKind.Model)
        {
            if (row.TryGetProperty("zone", out _) || row.TryGetProperty("itemId", out _)
                || row.TryGetProperty("groupType", out _) || row.TryGetProperty("difficulty", out _))
            { code = "spawn-rule-model-fields"; reason = "A model rule carries `zones` and `models`, and no single zone, item or enemy group."; return false; }
            if (!row.TryGetProperty("zones", out var zoneRows) || zoneRows.ValueKind != JsonValueKind.Array
                || zoneRows.GetArrayLength() is < 1 or > MaximumModelZones)
            { code = "spawn-rule-zones"; reason = "A model rule lists 1 to " + MaximumModelZones + " zones to place its instances in."; return false; }
            zones = new List<SpawnRuleZone>();
            foreach (var zoneRow in zoneRows.EnumerateArray())
            {
                if (!TryZone(zoneRow, out var listed) || zones.Contains(listed!))
                { code = "spawn-rule-zones"; reason = "Each listed zone is a distinct `dimension`, `layer` (0-2) and `zone`."; return false; }
                zones.Add(listed!);
            }
            if (!row.TryGetProperty("models", out var modelRows) || modelRows.ValueKind != JsonValueKind.Array || modelRows.GetArrayLength() != expected)
            { code = "spawn-rule-models"; reason = "A model rule names exactly `expected` placed models, one per instance it may place."; return false; }
            models = new List<string>();
            foreach (var modelRow in modelRows.EnumerateArray())
            {
                string? modelId = modelRow.ValueKind == JsonValueKind.String ? modelRow.GetString() : null;
                if (!PlacedModelAddress.IsId(modelId) || models.Contains(modelId!))
                { code = "spawn-rule-models"; reason = "Each placed model of a model rule is a distinct placed-model id."; return false; }
                models.Add(modelId!);
            }
        }
        else
        {
            if (row.TryGetProperty("zones", out _) || row.TryGetProperty("models", out _))
            { code = "spawn-rule-field"; reason = "Only a model rule carries `zones` and `models`."; return false; }
            if (!row.TryGetProperty("zone", out var zoneElement) || !TryZone(zoneElement, out zone))
            { code = "spawn-rule-zone"; reason = "An item or enemy rule names its zone by `dimension`, `layer` (0-2) and `zone`."; return false; }
            if (kind == SpawnRuleKind.Item)
            {
                if (!RuntimeJson.TryInteger(row, "itemId", 1, uint.MaxValue, out itemId) || row.TryGetProperty("groupType", out _) || row.TryGetProperty("difficulty", out _))
                { code = "spawn-rule-item"; reason = "An item rule names the vanilla item id it distributes and no enemy group."; return false; }
            }
            else if (!RuntimeJson.TryInteger(row, "groupType", 0, 6, out groupType) || !RuntimeJson.TryInteger(row, "difficulty", 0, 7, out difficulty)
                || row.TryGetProperty("itemId", out _))
            { code = "spawn-rule-entry"; reason = "An enemy rule names the group type (0-6) and difficulty (0-7) of its entry in the zone's random enemy list and no item."; return false; }
        }
        rule = new SpawnRule(id!, level, kind, zone, (uint)itemId, (int)groupType, (int)difficulty, (int)expected, (int)attached,
            zones == null ? null : Array.AsReadOnly(zones.ToArray()), models == null ? null : Array.AsReadOnly(models.ToArray()));
        return true;
    }

    private static bool TryZone(JsonElement element, out SpawnRuleZone? zone)
    {
        zone = null;
        if (element.ValueKind != JsonValueKind.Object || !DocumentReading.Known(element, out _, "dimension", "layer", "zone")
            || !DocumentReading.Coordinate(element, "dimension", 0, int.MaxValue, out var dimension)
            || !DocumentReading.Coordinate(element, "layer", 0, 2, out var layer)
            || !DocumentReading.Coordinate(element, "zone", 0, int.MaxValue, out var zoneIndex)) return false;
        zone = new SpawnRuleZone(dimension, layer, zoneIndex);
        return true;
    }

    private static bool TryKind(string text, out SpawnRuleKind kind)
    {
        switch (text)
        {
            case "item": kind = SpawnRuleKind.Item; return true;
            case "enemy": kind = SpawnRuleKind.Enemy; return true;
            case "model": kind = SpawnRuleKind.Model; return true;
            default: kind = SpawnRuleKind.Item; return false;
        }
    }
}
