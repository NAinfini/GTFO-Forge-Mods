using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeWeapon.Native;

internal readonly record struct WeaponOriginalAsset(string Kind, string Name, string Bundle = "", string Shard = "");

/// <summary>Admission-only reference checking against the same imported game schema the website uses.</summary>
internal static class WeaponContentReferences
{
    // Same native table readers as GearPartExtraAssets.GeneralData; enum values are read from the schema.
    private static readonly IReadOnlyDictionary<string, string> AddedPartTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["FrontPart"] = "GearFrontPart",
        ["ReceiverPart"] = "GearReceiverPart",
        ["StockPart"] = "GearStockPart",
        ["SightPart"] = "GearSightPart",
        ["MagPart"] = "GearMagPart",
        ["FlashlightPart"] = "GearFlashlightPart",
        ["ToolMainPart"] = "GearToolMainPart",
        ["ToolGripPart"] = "GearToolGripPart",
        ["ToolDeliveryPart"] = "GearToolDeliveryPart",
        ["ToolPayloadPart"] = "GearToolPayloadPart",
        ["ToolTargetingPart"] = "GearToolTargetingPart",
        ["ToolScreenPart"] = "GearToolScreenPart",
        ["MeleeHeadPart"] = "GearMeleeHeadPart",
        ["MeleeNeckPart"] = "GearMeleeNeckPart",
        ["MeleeHandlePart"] = "GearMeleeHandlePart",
        ["MeleePommelPart"] = "GearMeleePommelPart",
    };

    internal static IReadOnlyList<WeaponOriginalAsset> Check(JsonElement blocks, IReadOnlyDictionary<string, string> owned,
        HashSet<(string, uint)> declared, JsonElement fieldDocument, JsonElement originalResources, Func<string, uint, JsonElement>? originalBlock, string primaryType)
    {
        var schema = ForgeEngine.GameBindings.RundownContentSchema.Root;
        var used = new HashSet<(string, uint)>();
        var pending = new Queue<(string Type, uint Id)>();
        var expanded = new HashSet<(string, uint)>();
        var assets = new HashSet<WeaponOriginalAsset>();
        var variants = new Dictionary<(string Type, uint Id), HashSet<string>>();
        bool inOriginalClosure = false;
        string originalPath = "";
        string EnumName(string type, JsonElement value)
        {
            var row = schema.GetProperty("enums").GetProperty(type).GetProperty("members").EnumerateArray().FirstOrDefault(member =>
                value.ValueKind == JsonValueKind.String ? member.GetProperty("name").GetString() == value.GetString()
                    : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && member.GetProperty("value").GetInt32() == number);
            string? name = row.ValueKind == JsonValueKind.Undefined ? null : row.GetProperty("name").GetString();
            if (name == null || type == "AssetBundleName" && name == "None") throw new InvalidDataException("weapon-content-closure-asset: invalid " + type);
            return name;
        }
        void GeneralAssets(JsonElement general, string fieldPath)
        {
            string[] selectors = { "Model", "AssetBundle", "BundleShard" };
            if (selectors.Any(name => general.TryGetProperty(name, out _)))
            {
                var missing = selectors.Where(name => !general.TryGetProperty(name, out _)).ToArray();
                if (missing.Length > 0) throw new InvalidDataException("weapon-content-closure-asset: Missing native resource selector: "
                    + string.Join(", ", missing.Select(name => originalPath + "." + (fieldPath.Length == 0 ? "" : fieldPath + ".") + name)));
            }
            if (!general.TryGetProperty("Model", out var model) || model.ValueKind != JsonValueKind.String
                || model.GetString() is not string path || !path.StartsWith("Assets/", StringComparison.Ordinal)
                || !path.EndsWith(".prefab", StringComparison.Ordinal) || path.Any(char.IsControl)
                || !general.TryGetProperty("AssetBundle", out var bundle) || !general.TryGetProperty("BundleShard", out var shard))
                throw new InvalidDataException("weapon-content-closure-asset: invalid native prefab selectors.");
            string bundleName = EnumName("AssetBundleName", bundle), shardName = EnumName("AssetBundleShard", shard);
            assets.Add(new("prefab", path, bundleName, shardName));
            assets.Add(new("shard", shardName, bundleName));
            assets.Add(new("bundle", bundleName));
        }
        void Original(string type, uint id)
        {
            if (!schema.GetProperty("names").TryGetProperty(type, out var index)
                || !index.GetProperty("rows").EnumerateArray().Any(row => row[0].GetUInt32() == id))
                throw new InvalidDataException("Weapon reference is outside the imported original catalog: " + type + "/" + id);
        }
        void Reference(JsonElement value, string target)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$ref", out var marker))
            {
                if (value.EnumerateObject().Count() != 2 || marker.ValueKind != JsonValueKind.String || marker.GetString() != "block"
                    || !value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !owned.TryGetValue(id.GetString()!, out var actual) || actual != target)
                    throw new InvalidDataException("Weapon reference targets the wrong native type: " + target);
                return;
            }
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out uint number)
                || (ForgeEngine.Framework.ContentDataBlockIds.UsesUInt16(target) && number > ushort.MaxValue))
                throw new InvalidDataException("Invalid numeric weapon reference: " + target);
            if (number != 0)
            {
                if (!declared.Contains((target, number))) throw new InvalidDataException("Undeclared original weapon reference: " + target + "/" + number);
                Original(target, number);
                used.Add((target, number));
                if (inOriginalClosure) pending.Enqueue((target, number));
            }
        }
        void NoReferences(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("$ref", out _)) throw new InvalidDataException("Weapon reference appears outside a typed native field.");
                foreach (var property in value.EnumerateObject()) NoReferences(property.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) NoReferences(child);
        }
        JsonElement Fields(string type)
        {
            if (!schema.GetProperty("types").TryGetProperty(type, out var container) && !schema.GetProperty("classes").TryGetProperty(type, out container))
                throw new InvalidDataException("Unknown weapon native reference container: " + type);
            return container.GetProperty("fields");
        }
        void Field(JsonElement value, JsonElement field, string path)
        {
            string kind = field.GetProperty("kind").GetString()!, type = field.GetProperty("type").GetString()!;
            if (kind == "ref") { Reference(value, type); return; }
            // LocalizedText also accepts inline text, which does not name a Text table row.
            if (kind == "localizedText")
            {
                if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$ref", out _)) Reference(value, "Text");
                else NoReferences(value);
                return;
            }
            if (kind == "object" && value.ValueKind == JsonValueKind.Object) { Container(value, type, path); return; }
            if (kind == "list" && value.ValueKind == JsonValueKind.Array && field.TryGetProperty("item", out var item))
            { int index = 0; foreach (var child in value.EnumerateArray()) Field(child, item, path + "." + index++); return; }
            NoReferences(value);
        }
        void Container(JsonElement value, string type, string path = "")
        {
            var fields = Fields(type);
            if (inOriginalClosure && type == "GearPartGeneralData") GeneralAssets(value, path);
            foreach (var property in value.EnumerateObject())
            {
                if (type == "PlayerOfflineGear" && property.Name == "GearJSON") { Gear(property.Value); continue; }
                var found = fields.EnumerateArray().FirstOrDefault(field => field.GetProperty("name").GetString() == property.Name);
                if (found.ValueKind == JsonValueKind.Undefined) NoReferences(property.Value);
                else Field(property.Value, found, path.Length == 0 ? property.Name : path + "." + property.Name);
            }
        }
        void Gear(JsonElement gear)
        {
            if (gear.ValueKind != JsonValueKind.Object || !gear.TryGetProperty("Packet", out var packet) || packet.ValueKind != JsonValueKind.Object
                || !packet.TryGetProperty("Comps", out var components) || components.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid compiled weapon gear packet.");
            int categories = 0;
            foreach (var property in components.EnumerateObject())
            {
                var value = property.Value;
                if (value.ValueKind != JsonValueKind.Object) { NoReferences(value); continue; }
                if (!value.TryGetProperty("c", out var component) || !component.TryGetInt32(out int number) || !value.TryGetProperty("v", out var reference))
                    throw new InvalidDataException("Invalid compiled weapon gear component.");
                // A component the schema does not know carries no reference here (null), exactly like a non-reference one.
                ForgeEngine.GameBindings.RundownContentSchema.TryComponentType(number, out var target);
                if (target == null) NoReferences(reference); else Reference(reference, target);
                if (target == "GearCategory")
                {
                    categories++;
                    if (reference.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Authored gear must use its own category.");
                }
                foreach (var extra in value.EnumerateObject()) if (extra.Name is not ("c" or "v")) NoReferences(extra.Value);
            }
            if (categories != 1) throw new InvalidDataException("Authored gear must select exactly one owned category.");
            foreach (var property in gear.EnumerateObject()) if (property.Name != "Packet") NoReferences(property.Value);
            foreach (var property in packet.EnumerateObject()) if (property.Name != "Comps") NoReferences(property.Value);
        }
        string VariantEnum(string type)
        {
            var list = Fields(type).EnumerateArray().Single(field => field.GetProperty("name").GetString() == "GeneralDatas");
            var selector = Fields(list.GetProperty("item").GetProperty("type").GetString()!).EnumerateArray()
                .Single(field => field.GetProperty("name").GetString() == "Type" && field.GetProperty("kind").GetString() == "enum");
            return selector.GetProperty("type").GetString()!;
        }
        string? VariantName(string enumType, JsonElement value)
        {
            var member = schema.GetProperty("enums").GetProperty(enumType).GetProperty("members").EnumerateArray().FirstOrDefault(row =>
                value.ValueKind == JsonValueKind.String ? row.GetProperty("name").GetString() == value.GetString()
                    : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && row.GetProperty("value").GetInt32() == number);
            return member.ValueKind == JsonValueKind.Undefined ? null : member.GetProperty("name").GetString();
        }
        void SelectedVariants(JsonElement value, string type, uint id)
        {
            if (!variants.TryGetValue((type, id), out var selected)) return;
            string enumType = VariantEnum(type);
            InvalidDataException Invalid(string reason) => new("weapon-content-closure-reference: " + type + "/" + id
                + " GeneralDatas Variant " + string.Join(", ", selected.OrderBy(name => name, StringComparer.Ordinal)) + ": " + reason);
            if (!value.TryGetProperty("GeneralDatas", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw Invalid("missing native variant list.");
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Null) continue; // Native selection skips null entries.
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("Type", out var selector)
                    || !(selector.ValueKind == JsonValueKind.String || selector.ValueKind == JsonValueKind.Number && selector.TryGetInt32(out _)))
                    throw Invalid("invalid native Type for " + enumType + ".");
                // Unselected enum values do not prevent the native consumer selecting a valid model.
                // Their Data still goes through the same reference/resource closure below.
                string? name = VariantName(enumType, selector);
                if (name == null || !selected.Contains(name)) continue;
                if (!found.Add(name)) continue; // The native consumer selects the first matching row.
                if (!entry.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Object)
                    throw Invalid("missing native Data for " + name + ".");
            }
            foreach (string name in selected)
                if (!found.Contains(name)) throw Invalid("no native model for " + name + ".");
        }
        void AddedParts(JsonElement parts)
        {
            try
            {
                if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() > 16)
                    throw new InvalidDataException("Invalid added-part list.");
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("PartType", out var component)
                        || !component.TryGetInt32(out int number) || !part.TryGetProperty("PartID", out var reference)
                        || reference.ValueKind != JsonValueKind.Number || !reference.TryGetUInt32(out uint id) || id == 0)
                        throw new InvalidDataException("Added part requires a native component and positive original ID.");
                    var member = schema.GetProperty("enums").GetProperty("eGearComponent").GetProperty("members").EnumerateArray()
                        .FirstOrDefault(row => row.GetProperty("value").GetInt32() == number);
                    string? name = member.ValueKind == JsonValueKind.Undefined ? null : member.GetProperty("name").GetString();
                    if (name == null || !AddedPartTypes.TryGetValue(name, out var target) || !schema.GetProperty("types").TryGetProperty(target, out _))
                        throw new InvalidDataException("Added part component has no supported native model table.");
                    Reference(reference, target);
                    if (HasVariant(target))
                    {
                        string enumType = VariantEnum(target);
                        if (!part.TryGetProperty("Variant", out var variant) || variant.ValueKind != JsonValueKind.String
                            || VariantName(enumType, variant) is not string selected)
                            throw new InvalidDataException(target + "/" + id + " requires a Variant member of " + enumType + ".");
                        if (!variants.TryGetValue((target, id), out var selectedVariants))
                            variants.Add((target, id), selectedVariants = new HashSet<string>(StringComparer.Ordinal));
                        selectedVariants.Add(selected);
                    }
                    pending.Enqueue((target, id));
                    foreach (var field in part.EnumerateObject()) if (field.Name != "PartID") NoReferences(field.Value);
                }
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException)
            {
                // The existing installer returns weapon-content-invalid and preserves this precise detail code.
                throw new InvalidDataException("weapon-content-added-part-reference: " + error.Message);
            }
        }
        foreach (var block in blocks.EnumerateArray())
        {
            string type = block.GetProperty("type").GetString()!;
            if (block.TryGetProperty("sourceId", out var source)) Original(type, source.GetUInt32());
            Container(block.GetProperty("fields"), type);
        }
        if (fieldDocument.ValueKind != JsonValueKind.Undefined)
            foreach (var weapon in fieldDocument.GetProperty("weapons").EnumerateArray())
            {
                Reference(weapon.GetProperty(primaryType == "Item" ? "itemId" : "offlineId"), primaryType);
                foreach (var block in weapon.GetProperty("blocks").EnumerateObject())
                    foreach (var field in block.Value.EnumerateObject())
                    {
                        if (block.Name == "forge.etc.Sentry" && field.Name == "ArchetypeID") Reference(field.Value, "Archetype");
                        else if (block.Name == "forge.vanilla.flashlight" && field.Name == "m_settingsID") Reference(field.Value, "FlashlightSettings");
                        else if (block.Name == "forge.gearPartCustomization.Block" && field.Name == "AddedParts") AddedParts(field.Value);
                        else NoReferences(field.Value);
                    }
            }
        while (pending.Count > 0)
        {
            var next = pending.Dequeue();
            if (!expanded.Add(next)) continue;
            if (expanded.Count > 256) throw new InvalidDataException("weapon-content-closure-budget: too many original blocks.");
            if (originalBlock == null) throw new InvalidDataException("weapon-content-closure-source-missing: " + next.Type + "/" + next.Id);
            inOriginalClosure = true;
            originalPath = next.Type + "/" + next.Id;
            try
            {
                var value = originalBlock(next.Type, next.Id);
                if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected original native block.");
                SelectedVariants(value, next.Type, next.Id);
                Container(value, next.Type);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException)
            {
                if (error.Message.StartsWith("weapon-content-closure-", StringComparison.Ordinal)) throw;
                throw new InvalidDataException("weapon-content-closure-reference: " + next.Type + "/" + next.Id + ": " + error.Message);
            }
        }
        var declaredAssets = new HashSet<WeaponOriginalAsset>();
        if (originalResources.ValueKind != JsonValueKind.Undefined)
        {
            if (originalResources.ValueKind != JsonValueKind.Array || originalResources.GetArrayLength() > 768)
                throw new InvalidDataException("weapon-content-closure-asset: invalid resource list.");
            foreach (var resource in originalResources.EnumerateArray())
            {
                if (resource.ValueKind != JsonValueKind.Object || !resource.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("weapon-content-closure-asset: missing resource kind.");
                string? tag = kind.GetString();
                var keys = resource.EnumerateObject().Select(property => property.Name).ToArray();
                string[] expected = tag switch
                {
                    "prefab" => new[] { "kind", "path", "bundle", "shard" },
                    "bundle" => new[] { "kind", "name" },
                    "shard" => new[] { "kind", "bundle", "name" },
                    _ => Array.Empty<string>()
                };
                if (keys.Length != expected.Length || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Except(expected, StringComparer.Ordinal).Any()
                    || resource.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException("weapon-content-closure-asset: wrong resource type or fields.");
                var asset = tag switch
                {
                    "prefab" => new WeaponOriginalAsset(tag, resource.GetProperty("path").GetString()!, resource.GetProperty("bundle").GetString()!, resource.GetProperty("shard").GetString()!),
                    "bundle" => new WeaponOriginalAsset(tag, resource.GetProperty("name").GetString()!),
                    _ => new WeaponOriginalAsset(tag!, resource.GetProperty("name").GetString()!, resource.GetProperty("bundle").GetString()!)
                };
                if (!declaredAssets.Add(asset)) throw new InvalidDataException("weapon-content-closure-asset: duplicate installed-game resource declaration: " + AssetKey(asset));
            }
        }
        foreach (var asset in assets.Except(declaredAssets).OrderBy(AssetKey, StringComparer.Ordinal))
            throw new InvalidDataException("weapon-content-closure-asset: missing or changed installed-game resource declaration: " + AssetKey(asset));
        foreach (var asset in declaredAssets.Except(assets).OrderBy(AssetKey, StringComparer.Ordinal))
            throw new InvalidDataException("weapon-content-closure-unused: unused installed-game resource declaration: " + AssetKey(asset));
        if (!used.SetEquals(declared)) throw new InvalidDataException((expanded.Count > 0 ? "weapon-content-closure-unused: " : "") + "Weapon declares an unused original reference.");
        return declaredAssets.OrderBy(row => row.Kind, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
    }

    private static bool HasVariant(string type) => type is "GearToolDeliveryPart" or "GearToolPayloadPart" or "GearToolTargetingPart";

    private static string AssetKey(WeaponOriginalAsset asset) => asset.Kind switch
    {
        "prefab" => JsonSerializer.Serialize(new { kind = asset.Kind, path = asset.Name, bundle = asset.Bundle, shard = asset.Shard }),
        "shard" => JsonSerializer.Serialize(new { kind = asset.Kind, bundle = asset.Bundle, name = asset.Name }),
        _ => JsonSerializer.Serialize(new { kind = asset.Kind, name = asset.Name })
    };

    /// <summary>Admission-only projection of loaded tables. Do not serialize Unity objects or computed members.</summary>
    internal static JsonElement ProjectOriginalBlock(object value, string type)
    {
        // Preserve the selector beside Data only for the schema's actual tool-variant entry classes.
        var schema = ForgeEngine.GameBindings.RundownContentSchema.Root;
        var variantEntries = schema.GetProperty("types").EnumerateObject().Where(table => HasVariant(table.Name))
            .Select(table => table.Value.GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("name").GetString() == "GeneralDatas")
                .GetProperty("item").GetProperty("type").GetString()!).ToHashSet(StringComparer.Ordinal);
        object? Member(object owner, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var ownerType = owner.GetType();
            if (ownerType.GetField(name, flags) is FieldInfo field) return field.GetValue(owner);
            if (ownerType.GetProperty(name, flags) is PropertyInfo property) return property.GetValue(owner);
            throw new InvalidDataException("weapon-content-closure-type: native member missing: " + type + "." + name);
        }
        JsonNode? Field(object? child, JsonElement field)
        {
            if (child == null) return null;
            string kind = field.GetProperty("kind").GetString()!, target = field.GetProperty("type").GetString()!;
            if (kind == "ref") return JsonValue.Create(Convert.ToUInt32(child, System.Globalization.CultureInfo.InvariantCulture));
            if (kind == "localizedText") return child is string text ? JsonValue.Create(text) : JsonValue.Create(Convert.ToUInt32(Member(child, "Id"), System.Globalization.CultureInfo.InvariantCulture));
            if (kind == "object") return Container(child, target);
            if (kind == "list" && field.TryGetProperty("item", out var item))
            {
                var result = new JsonArray();
                int count = Convert.ToInt32(Member(child, "Count"), System.Globalization.CultureInfo.InvariantCulture);
                if (count is < 0 or > 16384) throw new InvalidDataException("weapon-content-closure-budget: native list too large.");
                var indexer = child.GetType().GetProperty("Item") ?? throw new InvalidDataException("weapon-content-closure-type: native list indexer missing.");
                for (int index = 0; index < count; index++) result.Add(Field(indexer.GetValue(child, new object[] { index }), item));
                return result;
            }
            return JsonValue.Create(child.ToString());
        }
        JsonObject Container(object owner, string containerType)
        {
            var schema = ForgeEngine.GameBindings.RundownContentSchema.Root;
            if (!schema.GetProperty("types").TryGetProperty(containerType, out var container) && !schema.GetProperty("classes").TryGetProperty(containerType, out container))
                throw new InvalidDataException("weapon-content-closure-type: unknown native container " + containerType);
            var result = new JsonObject();
            foreach (var field in container.GetProperty("fields").EnumerateArray())
            {
                string name = field.GetProperty("name").GetString()!, kind = field.GetProperty("kind").GetString()!;
                bool list = kind == "list" && field.TryGetProperty("item", out var item) && item.GetProperty("kind").GetString() is "ref" or "localizedText" or "object";
                if (kind is "ref" or "localizedText" or "object" || list
                    || kind == "enum" && name == "Type" && variantEntries.Contains(containerType)
                    || containerType == "GearPartGeneralData" && name is "Model" or "AssetBundle" or "BundleShard")
                    result[name] = Field(Member(owner, name), field);
            }
            return result;
        }
        using var document = JsonDocument.Parse(Container(value, type).ToJsonString());
        return document.RootElement.Clone();
    }
}
