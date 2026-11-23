using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeWeapon.Native;

/// <summary><paramref name="Name"/> is the readable native name the website wrote (`<author>_<work>_<number>`); rows are found by
/// <paramref name="PersistentId"/>, never by name.</summary>
internal sealed record WeaponContentBlock(string Id, string Type, uint PersistentId, string Json, uint? SourceId, string Name);

/// <summary>Compiled data only. Reading never publishes native blocks or changes the active field snapshot.</summary>
internal sealed class WeaponContentDefinition
{
    internal IReadOnlyList<WeaponContentBlock> Blocks { get; private init; } = Array.Empty<WeaponContentBlock>();
    internal IReadOnlyList<ContentVanillaId> Originals { get; private init; } = Array.Empty<ContentVanillaId>();
    internal IReadOnlyList<WeaponOriginalAsset> OriginalResources { get; private init; } = Array.Empty<WeaponOriginalAsset>();
    internal WeaponFieldEntry[] Fields { get; private init; } = Array.Empty<WeaponFieldEntry>();
    internal NativeModelContent Models { get; private init; } = NativeModelContent.Empty;
    internal ForgeVisual.SurfaceAppearance? Appearance { get; private init; }
    internal IReadOnlyDictionary<GearPartSlot, ForgeVisual.SurfaceAppearance> PartAppearances { get; private init; }
        = new Dictionary<GearPartSlot, ForgeVisual.SurfaceAppearance>();
    /// <summary>The whole-gun table (<c>forge-gun-grips</c> schema 2, keyed by model assembly id): the listed assembly is the whole gun, mounted by
    /// ForgeVisual <c>GunMount</c> on the gun's holder instead of placed as an attachment.</summary>
    internal ForgeVisual.GunGrips? Grips { get; private init; }
    /// <summary>The game's grip animations the hands take in place of those of the gun's parts (null: the parts').</summary>
    internal string? LeftHandShape { get; private init; }
    internal string? RightHandShape { get; private init; }
    /// <summary>The model that replaces a part and carries its muzzle, left hand and sight (<c>forge-gun-part-points</c> schema 1, one row at most).</summary>
    internal ForgeVisual.GunPartPoints? PartPoints { get; private init; }
    /// <summary>The models that, under a whole gun, stay shown and move with the game node they hang on (assembly ids).</summary>
    internal IReadOnlySet<string> Followers { get; private init; } = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "PlayerOfflineGear", "GearCategory", "Archetype", "MeleeArchetype", "Recoil", "Item", "ItemFPSSettings",
        "WeaponAudio", "MeleeAnimationSet", "MeleeSFX", "GearSightPart", "FlashlightSettings"
    };
    // One block per type: a gun carries at most 9 of these (gear, category, archetype, recoil, item, FPS settings,
    // audio, sight, flashlight) and a melee at most 8 (gear, category, melee archetype, item, FPS settings,
    // animation set, sound set, sight), so the budget of 11 holds for the 12 types above.

    internal static WeaponContentDefinition Read(string id, IReadOnlyDictionary<string, uint> issued, string json, string source,
        Func<string, uint, JsonElement>? originalBlock = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Keys(root, "schemaVersion", "blocks", "originalReferences", "originalResources", "fields",
            "modelAssemblies", "nativeBindings", "resourceDefinitions", "resourceScope", "resourcePackages", "appearance", "partAppearances", "gunGrips", "gunHands", "gunPartPoints", "gunFollowers");
        if (!ForgeCore.FormatVersion.Matches(root))
            throw new InvalidDataException("Expected compiled weapon content schema 1.0.0.");
        var array = root.GetProperty("blocks");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is 0 or > 11)
            throw new InvalidDataException("Weapon block budget exceeded.");
        var rows = new Dictionary<string, (string Id, JsonElement Fields, uint? Source)>(StringComparer.Ordinal);
        string? primaryType = null;
        foreach (var row in array.EnumerateArray())
        {
            Keys(row, "id", "type", "sourceId", "fields");
            string type = row.GetProperty("type").GetString() ?? "";
            string ownId = row.GetProperty("id").GetString() ?? "";
            if (ownId == id)
            {
                if (primaryType != null || type is not ("PlayerOfflineGear" or "Item"))
                    throw new InvalidDataException("Weapon content requires exactly one legal primary block.");
                primaryType = type;
            }
            else if (ownId != id + "#" + type)
                throw new InvalidDataException("Invalid compiled weapon child identity: " + type);
            var fields = row.GetProperty("fields");
            uint? sourceId = null;
            if (row.TryGetProperty("sourceId", out var sourceValue))
            {
                if (!sourceValue.TryGetUInt32(out uint original) || original == 0
                    || original >= (ContentDataBlockIds.UsesUInt16(type) ? ContentDataBlockIds.First16 : ContentDataBlockIds.FirstWide))
                    throw new InvalidDataException("Invalid original weapon source: " + type);
                sourceId = original;
            }
            if (((type is "PlayerOfflineGear" or "GearCategory" or "Archetype" or "MeleeArchetype" or "Recoil") || ownId == id) && sourceId == null)
                throw new InvalidDataException("Weapon block omits its original template source: " + type);
            if (!Types.Contains(type) || !rows.TryAdd(type, (ownId, fields, sourceId))
                || fields.ValueKind != JsonValueKind.Object || fields.TryGetProperty("persistentID", out _) || fields.TryGetProperty("Index", out _))
                throw new InvalidDataException("Invalid compiled weapon block: " + type);
            ForgeCore.StrictJson.UniqueMembers(fields);
        }
        if (primaryType == null || !issued.TryGetValue(id, out uint persistentId))
            throw new InvalidDataException("Weapon primary native identity differs.");
        if (primaryType == "PlayerOfflineGear" ? !rows.ContainsKey("GearCategory")
            : rows.ContainsKey("PlayerOfflineGear") || rows.ContainsKey("GearCategory"))
            throw new InvalidDataException("Compiled weapon has an invalid gear or item shape.");
        var ids = rows.ToDictionary(row => row.Value.Id, row => issued.TryGetValue(row.Value.Id, out uint n) ? n
            : throw new InvalidDataException("No number was issued for " + row.Key + " " + row.Value.Id + "."), StringComparer.Ordinal);
        // Native tables have separate ID namespaces. Equal numbers in different types do not collide.
        if (rows.Select(row => (row.Key, ids[row.Value.Id])).Distinct().Count() != rows.Count)
            throw new InvalidDataException("Compiled weapon identities collide.");
        JsonNode? Rewrite(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("$ref", out var reference))
                {
                    Keys(value, "$ref", "id");
                    if (value.EnumerateObject().Count() != 2 || reference.GetString() != "block"
                        || !ids.TryGetValue(value.GetProperty("id").GetString() ?? "", out uint target))
                        throw new InvalidDataException("Unknown compiled weapon block reference.");
                    return JsonValue.Create(target);
                }
                var result = new JsonObject(); foreach (var child in value.EnumerateObject()) result[child.Name] = Rewrite(child.Value);
                return result;
            }
            if (value.ValueKind == JsonValueKind.Array)
            { var result = new JsonArray(); foreach (var child in value.EnumerateArray()) result.Add(Rewrite(child)); return result; }
            return JsonNode.Parse(value.GetRawText());
        }
        var blocks = new List<WeaponContentBlock>();
        foreach (var row in rows.OrderBy(row => row.Key == primaryType ? 1 : 0).ThenBy(row => row.Key, StringComparer.Ordinal))
        {
            var fields = (JsonObject)Rewrite(row.Value.Fields)!;
            string name = ForgeCore.StrictJson.BlockName(fields, row.Value.Id);
            fields["persistentID"] = ids[row.Value.Id];
            if (row.Key == "PlayerOfflineGear")
            {
                if (fields["GearJSON"] is not JsonObject gear) throw new InvalidDataException("GearJSON must carry compiled component references.");
                fields["GearJSON"] = gear.ToJsonString();
            }
            blocks.Add(new(row.Value.Id, row.Key, ids[row.Value.Id], fields.ToJsonString(), row.Value.Source, name));
        }
        // Original Text rows can occupy high UInt32 IDs (the vanilla sledgehammer title is one).
        // A reference is checked against the loaded native table before any publication.
        var originals = new List<ContentVanillaId>();
        var originalArray = root.GetProperty("originalReferences");
        if (originalArray.ValueKind != JsonValueKind.Array || originalArray.GetArrayLength() > 256)
            throw new InvalidDataException("Invalid weapon original-reference budget.");
        var seen = new HashSet<(string, uint)>();
        foreach (var original in originalArray.EnumerateArray())
        {
            Keys(original, "type", "id");
            string type = original.GetProperty("type").GetString() ?? "";
            if (type.Length is 0 or > 64 || !type.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') || !(type[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                || !original.GetProperty("id").TryGetUInt32(out uint number) || number == 0
                || (ContentDataBlockIds.UsesUInt16(type) && number > ushort.MaxValue) || !seen.Add((type, number)))
                throw new InvalidDataException("Invalid weapon original reference.");
            originals.Add(new(type, number));
        }
        root.TryGetProperty("fields", out var referencedFields);
        root.TryGetProperty("originalResources", out var originalResources);
        var assets = WeaponContentReferences.Check(array, rows.ToDictionary(row => row.Value.Id, row => row.Key, StringComparer.Ordinal), seen,
            referencedFields, originalResources, originalBlock, primaryType);
        WeaponFieldEntry[] entries = Array.Empty<WeaponFieldEntry>();
        if (root.TryGetProperty("fields", out var fieldDocument))
        {
            Keys(fieldDocument, "schemaVersion", "weapons");
            var weapons = fieldDocument.GetProperty("weapons");
            string selectorName = primaryType == "Item" ? "itemId" : "offlineId";
            var selectorKey = primaryType == "Item" ? WeaponFieldKey.Item : WeaponFieldKey.Offline;
            if (weapons.ValueKind != JsonValueKind.Array || weapons.GetArrayLength() != 1
                || !weapons[0].TryGetProperty(selectorName, out var selector) || selector.ValueKind != JsonValueKind.Object
                || selector.GetProperty("$ref").GetString() != "block" || selector.GetProperty("id").GetString() != id)
                throw new InvalidDataException("Weapon fields must select their own stable primary identity.");
            ForgeCore.StrictJson.UniqueMembers(fieldDocument);
            var rewritten = Rewrite(fieldDocument)!.ToJsonString();
            if (!WeaponFieldDocuments.TryParse(rewritten, source, out var parsed, out var reason, out var code)
                || parsed!.Length != 1 || parsed[0].Key != selectorKey || parsed[0].Id != persistentId)
                throw new InvalidDataException("Invalid weapon fields: " + code + " " + reason);
            entries = parsed;
            if (entries[0].FixedSlot != null)
            {
                var item = blocks.Single(block => block.Type == "Item" && block.Id == id);
                using var nativeItem = JsonDocument.Parse(item.Json);
                if (item.SourceId != 53 || !nativeItem.RootElement.TryGetProperty("inventorySlot", out var slot)
                    || !(slot.ValueKind == JsonValueKind.String && slot.GetString() == "HackingTool"
                        || slot.ValueKind == JsonValueKind.Number && slot.TryGetInt32(out int slotId) && slotId == 11))
                    throw new InvalidDataException("HackingTool replacement must preserve its original Item template and fixed inventory slot.");
            }
        }
        var appearance = root.TryGetProperty("appearance", out var appearanceValue)
            ? NativeModelContent.ReadAppearance(appearanceValue) : null;
        var partAppearances = new Dictionary<GearPartSlot, ForgeVisual.SurfaceAppearance>();
        if (root.TryGetProperty("partAppearances", out var partArray))
        {
            if (partArray.ValueKind != JsonValueKind.Array || partArray.GetArrayLength() > Enum.GetValues<GearPartSlot>().Length - 1)
                throw new InvalidDataException("Invalid weapon part-appearance budget.");
            foreach (var part in partArray.EnumerateArray())
            {
                Keys(part, "part", "appearance");
                if (part.GetProperty("part").ValueKind != JsonValueKind.String
                    || !Enum.TryParse<GearPartSlot>(part.GetProperty("part").GetString(), false, out var slot)
                    || !Enum.IsDefined(slot) || slot == GearPartSlot.BaseItem
                    || slot.ToString() != part.GetProperty("part").GetString()
                    || !partAppearances.TryAdd(slot, NativeModelContent.ReadAppearance(part.GetProperty("appearance"))))
                    throw new InvalidDataException("Invalid or duplicate weapon part appearance.");
            }
        }
        var models = NativeModelContent.Read(root, "weapon");
        ForgeVisual.GunGrips? grips = null;
        if (root.TryGetProperty("gunGrips", out var gripValue))
        {
            try { grips = ForgeVisual.GunGrips.Parse(gripValue.GetRawText()); }
            catch (Exception error) when (error is FormatException or KeyNotFoundException or InvalidOperationException)
            { throw new InvalidDataException("Invalid weapon whole-gun table: " + error.Message); }
        }
        // A model replaces at most one vanilla part, sits on that part (the whole gun on the gun's root) and is the only one replacing it;
        // the one replacing the whole gun is the one row of the hold table.
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in models.Assemblies)
        {
            if (assembly.Replaces is not { } part) continue;
            if ((part != "BaseItem" && !Enum.GetNames<GearPartSlot>().Contains(part)) || assembly.Host.WeaponPart != part
                    || assembly.Host.ChildPath.Count != 0 || !replaced.Add(part))
                throw new InvalidDataException("The model " + assembly.Id + " does not replace one part it sits on: " + part);
        }
        string? leftShape = null, rightShape = null;
        if (root.TryGetProperty("gunHands", out var handValue))
        {
            Keys(handValue, "left", "right");
            leftShape = handValue.TryGetProperty("left", out var left) ? left.GetString() : null;
            rightShape = handValue.TryGetProperty("right", out var right) ? right.GetString() : null;
        }
        ForgeVisual.GunPartPoints? partPoints = null;
        if (root.TryGetProperty("gunPartPoints", out var pointValue))
        {
            try { partPoints = ForgeVisual.GunPartPoints.Parse(pointValue.GetRawText()); }
            catch (Exception error) when (error is FormatException or KeyNotFoundException or InvalidOperationException)
            { throw new InvalidDataException("Invalid weapon part point table: " + error.Message); }
            var row = partPoints.Models.Count == 1 ? partPoints.Models.Keys.First() : null;
            if (row == null || !models.Assemblies.Any(assembly => assembly.Id == row && assembly.Replaces is not null and not "BaseItem"))
                throw new InvalidDataException("The part point table names one model that replaces one part of the gun.");
        }
        var followers = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("gunFollowers", out var followValue))
            foreach (var follower in followValue.EnumerateArray())
                if (!models.Assemblies.Any(assembly => assembly.Id == follower.GetString() && assembly.Replaces == null) || !followers.Add(follower.GetString()!))
                    throw new InvalidDataException("A model that follows a game node is one attached model of this gun, listed once.");
        var whole = models.Assemblies.FirstOrDefault(assembly => assembly.Replaces == "BaseItem");
        if ((grips == null) != (whole == null) || (grips != null && (grips.Models.Count != 1 || !grips.TryGet(whole!.Id, out _))))
            throw new InvalidDataException("The whole-gun hold table names exactly the model that replaces the whole gun.");
        return new()
        {
            Blocks = blocks,
            Originals = originals,
            OriginalResources = assets,
            Fields = entries,
            Models = models,
            Appearance = appearance,
            PartAppearances = partAppearances,
            Grips = grips,
            LeftHandShape = leftShape,
            RightHandShape = rightShape,
            PartPoints = partPoints,
            Followers = followers
        };
    }

    private static void Keys(JsonElement value, params string[] allowed) => ForgeCore.StrictJson.OnlyKnown(value, "", allowed);
}
