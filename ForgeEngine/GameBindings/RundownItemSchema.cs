using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>Startup-only projection of the authoritative imported native schema. No inferred numeric ranges,
/// second schema, or arbitrary loaded-row inventory can establish vanilla/external Item provenance.</summary>
internal static class RundownContentSchema
{
    private static readonly Lazy<JsonDocument> Schema = new(() =>
    {
        using var stream = typeof(RundownContentSchema).Assembly.GetManifestResourceStream("ForgeEngine.NativeSchema")
            ?? throw new InvalidDataException("Rundown native reference schema is missing.");
        var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "infini-gtfo-native-schema" || root.GetProperty("version").GetString() != ForgeCore.FormatVersion.Current
            || root.GetProperty("types").ValueKind != JsonValueKind.Object || root.GetProperty("classes").ValueKind != JsonValueKind.Object
            || root.GetProperty("names").GetProperty("Item").GetProperty("rows").ValueKind != JsonValueKind.Array
            || root.GetProperty("enums").GetProperty("eGearComponent").GetProperty("members").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Rundown native reference schema is invalid.");
        return document;
    });
    /// <summary>The one embedded copy and the one parse of the native schema; the weapon admission reads it here too.</summary>
    internal static JsonElement Root => Schema.Value.RootElement;
    private static readonly Lazy<HashSet<uint>> OriginalItems = new(() => Schema.Value.RootElement.GetProperty("names")
        .GetProperty("Item").GetProperty("rows").EnumerateArray().Select(row => row[0].GetUInt32()).ToHashSet());
    private static readonly Lazy<HashSet<uint>> OriginalEnemies = new(() => Schema.Value.RootElement.GetProperty("names")
        .GetProperty("Enemy").GetProperty("rows").EnumerateArray().Select(row => row[0].GetUInt32()).ToHashSet());
    internal static bool IsOriginalItem(uint persistentId) => persistentId != 0 && OriginalItems.Value.Contains(persistentId);

    internal static void Validate(RundownPack pack)
    {
        var schema = Schema.Value.RootElement;
        var owned = pack.Blocks.ToDictionary(block => block.Id, block => block.Type, StringComparer.Ordinal);
        void NoExternal(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj.ContainsKey("$ref"))
                {
                    if (obj.Count != 1 || obj["$ref"] is not JsonValue marker || !marker.TryGetValue<string>(out var id)
                        || !owned.ContainsKey(id)) throw new InvalidDataException("External Item reference appears outside a typed Item field.");
                }
                else foreach (var property in obj) NoExternal(property.Value);
            }
            else if (node is JsonArray array) foreach (var value in array) NoExternal(value);
        }
        void Reference(JsonNode? value, string target)
        {
            if (value is JsonObject obj && obj.ContainsKey("$ref"))
            {
                if (obj.Count != 1 || obj["$ref"] is not JsonValue marker || !marker.TryGetValue<string>(out var id))
                    throw new InvalidDataException("Malformed typed Rundown reference.");
                if (owned.TryGetValue(id, out var type))
                {
                    if (type != target) throw new InvalidDataException("Rundown reference targets the wrong native type: " + target);
                }
                else throw new InvalidDataException("Rundown reference names no block of this package: " + id);
                return;
            }
            // Vanilla numbers remain legal. Every custom Item in a native field must retain its stable $ref;
            // merely matching an accepted, rejected or caller-invented number never grants dependency authority.
            if (target == "Item" && (value is not JsonValue number || !number.TryGetValue<uint>(out var nativeNumber)
                || nativeNumber > ushort.MaxValue || nativeNumber != 0 && !IsOriginalItem(nativeNumber)))
                throw new InvalidDataException("Numeric Item reference is not an original Item; use a stable reference to a package block.");
            if (target == "Enemy" && (value is not JsonValue enemyNumber || !enemyNumber.TryGetValue<uint>(out var nativeEnemy)
                || nativeEnemy != 0 && !OriginalEnemies.Value.Contains(nativeEnemy)))
                throw new InvalidDataException("Numeric Enemy reference is not original; use a stable reference to a package block.");
            NoExternal(value);
        }
        void Field(JsonNode? value, JsonElement field)
        {
            string kind = field.GetProperty("kind").GetString()!, type = field.GetProperty("type").GetString()!;
            if (kind == "ref") { Reference(value, type); return; }
            if (kind == "localizedText")
            {
                if (value is JsonObject text && text.ContainsKey("$ref")) Reference(value, "Text"); else NoExternal(value);
                return;
            }
            if (kind == "object" && value is JsonObject obj) { Container(obj, type); return; }
            if (kind == "list" && value is JsonArray array && field.TryGetProperty("item", out var item))
            { foreach (var child in array) Field(child, item); return; }
            NoExternal(value);
        }
        void Container(JsonObject value, string type)
        {
            if (!schema.GetProperty("types").TryGetProperty(type, out var container) && !schema.GetProperty("classes").TryGetProperty(type, out container))
            { NoExternal(value); return; }
            foreach (var property in value)
            {
                if (type == "PlayerOfflineGear" && property.Key == "GearJSON")
                { Gear(property.Value, pack, rewrite: false); continue; }
                var field = container.GetProperty("fields").EnumerateArray().FirstOrDefault(row => row.GetProperty("name").GetString() == property.Key);
                if (field.ValueKind == JsonValueKind.Undefined)
                {
                    if (container.GetProperty("fields").EnumerateArray().Any(row => string.Equals(row.GetProperty("name").GetString(), property.Key, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Noncanonical native field casing: " + property.Key);
                    NoExternal(property.Value);
                }
                else Field(property.Value, field);
            }
        }
        foreach (var block in pack.Blocks) Container(block.Data, block.Type);
    }
    /// <summary>GearJSON is a native serialized packet, not ordinary opaque prose. Only this real producer
    /// shape is decoded; materialization preserves a string for CellJSON and never mutates discovery data.</summary>
    internal static RundownBlock MaterializeGear(RundownBlock block, RundownPack pack)
    {
        if (block.Type != "PlayerOfflineGear" || !block.Data.TryGetPropertyValue("GearJSON", out var value)) return block;
        var gear = Gear(value, pack, rewrite: true);
        var data = JsonNode.Parse(block.Data.ToJsonString())!.AsObject();
        data["GearJSON"] = gear.ToJsonString();
        return block with { Data = data };
    }

    private static JsonObject Gear(JsonNode? value, RundownPack pack, bool rewrite)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
            throw new InvalidDataException("Rundown GearJSON must be a serialized native packet string.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        RundownPacks.RequireUniqueFields(document.RootElement);
        var gear = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 128 }) as JsonObject;
        if (gear == null || gear["Packet"] is not JsonObject packet || packet["Comps"] is not JsonObject components)
            throw new InvalidDataException("Invalid Rundown GearJSON Packet.Comps.");
        if (gear.Any(property => property.Key != "Packet" && string.Equals(property.Key, "Packet", StringComparison.OrdinalIgnoreCase))
            || packet.Any(property => property.Key != "Comps" && string.Equals(property.Key, "Comps", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Noncanonical native gear Packet/Comps casing.");
        var owned = pack.Blocks.ToDictionary(block => block.Id, block => block, StringComparer.Ordinal);
        var ownItems = new HashSet<uint>(pack.Blocks.Where(block => block.Type == "Item").Select(block => block.PersistentId));
        foreach (var property in components)
        {
            if (property.Key == "Length")
            {
                if (property.Value is not JsonValue length || !length.TryGetValue<int>(out int count) || count is < 1 or > 55)
                    throw new InvalidDataException("Invalid native gear component Length.");
                continue;
            }
            int index = 0;
            if (property.Key.Length is < 1 or > 2 || property.Key.Any(letter => letter is < 'a' or > 'z'))
                throw new InvalidDataException("Invalid native gear component slot.");
            foreach (char letter in property.Key) index = index * 26 + letter - 'a' + 1;
            if (index > 54 || property.Value is not JsonObject component || component.Count != 2
                || component["c"] is not JsonValue tag || !tag.TryGetValue<int>(out int number) || !component.ContainsKey("v"))
                throw new InvalidDataException("Invalid native gear component shape.");
            if (!TryComponentType(number, out var target)) throw new InvalidDataException("Unknown native gear component.");
            var reference = component["v"];
            if (reference is JsonObject marker)
            {
                if (target == null || marker.Count != 1 || marker["$ref"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id))
                    throw new InvalidDataException("Malformed or untyped native gear component reference.");
                uint resolved;
                if (owned.TryGetValue(id, out var block))
                {
                    if (block.Type != target) throw new InvalidDataException("Native gear component targets the wrong owned type: " + target);
                    resolved = block.PersistentId;
                }
                else throw new InvalidDataException("Native gear component names no block of this package: " + id);
                if (resolved > ushort.MaxValue) throw new InvalidDataException("Native gear component reference exceeds UInt16.");
                if (rewrite) component["v"] = resolved;
            }
            else
            {
                if (reference is not JsonValue numeric || !numeric.TryGetValue<uint>(out uint id) || id > ushort.MaxValue)
                    throw new InvalidDataException("Native gear component value must be UInt16 or a stable reference to a package block.");
                // Existing native packets contain numbers for their own Item. An external Item must retain
                // its stable marker; being declared, installed, or numerically equal is not sufficient.
                if (target == "Item" && id != 0 && !IsOriginalItem(id) && !ownItems.Contains(id))
                    throw new InvalidDataException("Numeric native gear Item is neither original nor package-owned.");
            }
        }
        foreach (var property in gear) if (property.Key != "Packet") NoGearMarkers(property.Value);
        foreach (var property in packet) if (property.Key != "Comps") NoGearMarkers(property.Value);
        return gear;
    }
    /// <summary>The data block type a gear packet component (<c>eGearComponent</c>) refers to; null for a component that holds no
    /// reference (None, FireMode, the *Type values). False when the schema has no such component.</summary>
    internal static bool TryComponentType(int component, out string? blockType)
    {
        blockType = null;
        var member = Schema.Value.RootElement.GetProperty("enums").GetProperty("eGearComponent").GetProperty("members").EnumerateArray()
            .FirstOrDefault(row => row.GetProperty("value").GetInt32() == component);
        if (member.ValueKind == JsonValueKind.Undefined) return false;
        string name = member.GetProperty("name").GetString()!;
        if (name is "None" or "FireMode" || name.EndsWith("Type", StringComparison.Ordinal)) return true;
        blockType = name switch
        {
            "Category" => "GearCategory",
            "BaseItem" => "Item",
            "AudioSetting" => "WeaponAudio",
            "MuzzleFlash" => "WeaponMuzzleFlash",
            "ShellCasing" => "WeaponShellCasing",
            "Pattern" => "GearPattern",
            "Palette" => "GearPalette",
            "ItemFPSSettings" => name,
            _ => name.StartsWith("Decal", StringComparison.Ordinal) ? "GearDecal"
                : name.EndsWith("Perk", StringComparison.Ordinal) || name.StartsWith("PlatformPerk", StringComparison.Ordinal) ? "GearPerk"
                : name.Contains("Attachment", StringComparison.Ordinal) ? "GearPartAttachment" : "Gear" + name
        };
        return true;
    }
    private static void NoGearMarkers(JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            if (obj.ContainsKey("$ref")) throw new InvalidDataException("Stable native gear reference is outside a typed component.");
            foreach (var property in obj) NoGearMarkers(property.Value);
        }
        else if (value is JsonArray array) foreach (var child in array) NoGearMarkers(child);
    }

}
