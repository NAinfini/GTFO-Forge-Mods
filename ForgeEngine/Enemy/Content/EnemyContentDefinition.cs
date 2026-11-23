using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;

namespace ForgeEnemy.Content;

/// <summary><paramref name="Name"/> is the readable native name the website wrote; rows are found by <paramref name="PersistentId"/>.</summary>
internal sealed record EnemyContentBlock(string Id, string Type, uint SourceId, uint PersistentId, string Json, string Name);
internal sealed record EnemyContentSource(uint SourceId, IReadOnlyDictionary<string, uint> Links);
internal sealed record EnemyContentModel(string Id, uint PersistentId, string Json);

/// <summary>One atomic closure of enemy definitions, their native child groups and private populations.
/// All references resolve before publication, so self-birth and mutually spawning variants need no load-order trick.</summary>
internal sealed class EnemyContentDefinition
{
    internal IReadOnlyList<EnemyContentBlock> Blocks { get; private init; } = Array.Empty<EnemyContentBlock>();
    internal EnemyProfileDocument? Profile { get; private init; }
    internal IReadOnlyList<EnemyContentSource> Sources { get; private init; } = Array.Empty<EnemyContentSource>();
    internal IReadOnlyList<EnemyContentModel> Models { get; private init; } = Array.Empty<EnemyContentModel>();
    internal IReadOnlyList<ContentVanillaId> OriginalReferences { get; private init; } = Array.Empty<ContentVanillaId>();
    private static readonly IReadOnlyDictionary<string, string> Linked = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["EnemyBalancing"] = "BalancingDataId",
        ["EnemyMovement"] = "MovementDataId",
        ["EnemyBehavior"] = "BehaviorDataId",
        ["EnemyDetection"] = "DetectionDataId",
        ["EnemySFX"] = "SFXDataId"
    };
    private sealed record Row(string Id, string Type, uint Source, JsonElement Fields);

    internal static EnemyContentDefinition Read(string id, IReadOnlyDictionary<string, uint> issued, string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Keys(root, "schemaVersion", "blocks", "profiles", "models");
        if (!ForgeCore.FormatVersion.Matches(root)
            || !root.TryGetProperty("blocks", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
            throw new InvalidDataException("Expected compiled enemy content schema 1.0.0.");
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var block in array.EnumerateArray())
        {
            Keys(block, "id", "type", "sourceId", "fields");
            string type = block.GetProperty("type").GetString() ?? "", blockId = block.GetProperty("id").GetString() ?? "";
            bool allowedType = type is "Enemy" or "EnemyGroup" or "EnemyPopulation" or "SurvivalWaveSettings" or "SurvivalWavePopulation" || Linked.ContainsKey(type);
            string prefix = id + "#" + type;
            bool ownId = blockId == id ? type == "Enemy" : blockId == prefix || blockId.StartsWith(prefix + ":", StringComparison.Ordinal);
            if (!allowedType || !ownId
                || !block.GetProperty("sourceId").TryGetUInt32(out uint sourceId) || sourceId == 0 || sourceId >= ContentDataBlockIds.FirstWide
                || !rows.TryAdd(blockId, new Row(blockId, type, sourceId, block.GetProperty("fields"))))
                throw new InvalidDataException("Invalid compiled enemy block identity or source: " + blockId + " / " + type);
            var fields = block.GetProperty("fields");
            if (fields.ValueKind != JsonValueKind.Object || fields.TryGetProperty("persistentID", out _) || fields.TryGetProperty("Index", out _))
                throw new InvalidDataException("Enemy content cannot supply numeric block identity or index: " + blockId);
            ForgeCore.StrictJson.UniqueMembers(fields);
        }
        if (!rows.TryGetValue(id, out var primary) || primary.Type != "Enemy") throw new InvalidDataException("Compiled content has no primary Enemy block.");
        var ids = rows.Values.ToDictionary(row => row.Id, row => issued.TryGetValue(row.Id, out uint n) ? n
            : throw new InvalidDataException("No number was issued for " + row.Type + " " + row.Id + "."), StringComparer.Ordinal);
        var conflicts = ContentDataBlockIds.Conflicts(rows.Values.Select(row => new ContentIssuedBlock(row.Id, row.Type, ids[row.Id])));
        if (conflicts.Count != 0) throw new InvalidDataException("Enemy native ID collision: " + conflicts[0].Id + " / " + conflicts[0].Other);
        var originals = new List<ContentVanillaId>();
        var sources = new List<EnemyContentSource>();
        foreach (var enemy in rows.Values.Where(row => row.Type == "Enemy"))
        {
            var links = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var link in Linked)
            {
                if (!enemy.Fields.TryGetProperty(link.Value, out var reference))
                    throw new InvalidDataException(enemy.Id + " omits native source link " + link.Value);
                if (reference.ValueKind == JsonValueKind.Object)
                {
                    Keys(reference, "$ref", "id");
                    string target = reference.GetProperty("id").GetString() ?? "";
                    if (reference.GetProperty("$ref").GetString() != "block" || !rows.TryGetValue(target, out var authored) || authored.Type != link.Key)
                        throw new InvalidDataException(enemy.Id + " has an unknown " + link.Key + " reference: " + target);
                    links.Add(link.Value, authored.Source);
                }
                else
                {
                    if (!reference.TryGetUInt32(out uint original)) throw new InvalidDataException(enemy.Id + " has an invalid " + link.Value);
                    links.Add(link.Value, original);
                    if (original != 0) originals.Add(new(link.Key, original));
                }
            }
            sources.Add(new(enemy.Source, links));
        }
        JsonNode? Rewrite(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("$ref", out var reference))
                {
                    Keys(value, "$ref", "id");
                    if (value.EnumerateObject().Count() != 2 || reference.GetString() != "block"
                        || !ids.TryGetValue(value.GetProperty("id").GetString() ?? "", out uint target))
                        throw new InvalidDataException("Unknown compiled enemy block reference: " + value.GetRawText());
                    return JsonValue.Create(target);
                }
                var result = new JsonObject(); foreach (var child in value.EnumerateObject()) result[child.Name] = Rewrite(child.Value);
                return result;
            }
            if (value.ValueKind == JsonValueKind.Array)
            { var result = new JsonArray(); foreach (var child in value.EnumerateArray()) result.Add(Rewrite(child)); return result; }
            return JsonNode.Parse(value.GetRawText());
        }
        var native = new List<EnemyContentBlock>();
        foreach (var row in rows.Values.OrderBy(row => row.Type == "Enemy" ? 1 : 0).ThenBy(row => row.Id, StringComparer.Ordinal))
        {
            var fields = (JsonObject)Rewrite(row.Fields)!;
            string name = ForgeCore.StrictJson.BlockName(fields, row.Id);
            fields["persistentID"] = ids[row.Id];
            // The only new native reference family in this closure: a private population's member.
            if (row.Type == "EnemyPopulation" && fields["RoleDatas"] is JsonArray roles)
                foreach (var role in roles)
                    if (role?["Enemy"] is JsonValue member && member.TryGetValue<uint>(out uint enemyId)
                        && !rows.Values.Any(other => other.Type == "Enemy" && ids[other.Id] == enemyId))
                        originals.Add(new("Enemy", enemyId));
            native.Add(new(row.Id, row.Type, row.Source, ids[row.Id], fields.ToJsonString(), name));
        }
        var models = new List<EnemyContentModel>();
        if (root.TryGetProperty("models", out var modelValues))
        {
            if (modelValues.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Compiled enemy models must be an array.");
            var modelEnemies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in modelValues.EnumerateArray())
            {
                Keys(value, "enemy", "modelAssemblies", "nativeBindings", "resourceDefinitions", "resourceScope", "resourcePackages");
                var reference = value.GetProperty("enemy"); Keys(reference, "$ref", "id");
                string target = reference.GetProperty("id").GetString() ?? "";
                if (reference.GetProperty("$ref").GetString() != "block" || !rows.TryGetValue(target, out var enemy) || enemy.Type != "Enemy"
                    || !modelEnemies.Add(target)) throw new InvalidDataException("Unknown or repeated model enemy: " + target);
                models.Add(new(target, ids[target], value.GetRawText()));
            }
        }
        EnemyProfileDocument? profile = null;
        if (root.TryGetProperty("profiles", out var profileValues))
        {
            if (profileValues.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Compiled enemy profiles must be an array.");
            var enemies = new JsonArray(); var seen = new HashSet<uint>();
            foreach (var value in profileValues.EnumerateArray())
            {
                Keys(value, "enemy", "blocks", "sounds", "actions");
                var reference = value.GetProperty("enemy"); Keys(reference, "$ref", "id");
                string target = reference.GetProperty("id").GetString() ?? "";
                if (reference.GetProperty("$ref").GetString() != "block" || !rows.TryGetValue(target, out var enemy) || enemy.Type != "Enemy"
                    || !seen.Add(ids[target])) throw new InvalidDataException("Unknown or repeated profile enemy: " + target);
                var profileBlocks = value.GetProperty("blocks");
                if (profileBlocks.TryGetProperty(EnemyFieldSchema.ChildSpawning, out var spawning)
                    && spawning.TryGetProperty("Attacks", out var attacks) && attacks.ValueKind == JsonValueKind.Array)
                    foreach (var attack in attacks.EnumerateArray())
                        foreach (var (field, type) in new[] { ("Group", "EnemyGroup"), ("Population", "EnemyPopulation"), ("WaveSettings", "SurvivalWaveSettings"), ("WavePopulation", "SurvivalWavePopulation") })
                            if (attack.TryGetProperty(field, out var selected))
                            {
                                if (selected.ValueKind == JsonValueKind.Object)
                                {
                                    Keys(selected, "$ref", "id");
                                    string selectedId = selected.GetProperty("id").GetString() ?? "";
                                    if (selected.GetProperty("$ref").GetString() != "block" || !rows.TryGetValue(selectedId, out var selectedRow) || selectedRow.Type != type)
                                        throw new InvalidDataException(target + " has a missing/wrong-type " + field + " reference: " + selectedId);
                                }
                                else if (selected.TryGetUInt32(out uint original)) originals.Add(new(type, original));
                                else throw new InvalidDataException(target + " has an invalid " + field + " reference.");
                            }
                var profileRow = new JsonObject { ["enemyType"] = ids[target], ["blocks"] = Rewrite(profileBlocks) };
                if (value.TryGetProperty("sounds", out var sounds)) profileRow["sounds"] = JsonNode.Parse(sounds.GetRawText());
                if (value.TryGetProperty("actions", out var actions)) profileRow["actions"] = JsonNode.Parse(actions.GetRawText());
                enemies.Add(profileRow);
            }
            if (enemies.Count != 0)
            {
                var profileJson = new JsonObject { ["schemaVersion"] = ForgeCore.FormatVersion.Current, ["enemies"] = enemies };
                var parsed = EnemyBlockReader.Read(source, profileJson.ToJsonString());
                if (!parsed.Ok) throw new InvalidDataException(parsed.Rejection.ToString());
                profile = parsed.Document;
            }
        }
        return new EnemyContentDefinition { Blocks = native, Profile = profile, Sources = sources, Models = models, OriginalReferences = originals.Distinct().ToArray() };
    }
    private static void Keys(JsonElement value, params string[] allowed) => ForgeCore.StrictJson.OnlyKnown(value, "", allowed);
}
