using ForgeCore;
using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

/// <summary>The vanilla carry body a custom mission item is built on. The base fixes the first-person shell and so
/// what the item can be inserted into; only the appearance layer is replaced.</summary>
internal enum CarryItemBase { PowerCell, Cargo, Hsu }

/// <summary>The author pose of the model on top of the automatic fit: Unity-local position, quaternion and scale.</summary>
internal sealed record CarryItemFit(IReadOnlyList<double> Position, IReadOnlyList<double> Rotation, IReadOnlyList<double> Scale);

/// <summary>Hand anchors in the item root's local frame. An absent hand keeps the base item's own anchor.</summary>
internal sealed record CarryItemGrip(IReadOnlyList<double>? Left, IReadOnlyList<double>? Right);

/// <summary>
/// One custom mission item on one level (contract M16): the native Item block `ItemId` (cloned from `Base`, so it spawns,
/// carries, inserts and registers in the terminal like the vanilla body) plus the locked model assembly that replaces the
/// body's look on every peer. `Content` is the shared compiled model content, read by the model runtime itself.
/// </summary>
internal sealed class CarryItem
{
    internal CarryItem(string id, MapLevelReference level, uint itemId, CarryItemBase itemBase, bool autoFit, CarryItemFit fit, CarryItemGrip? grip, bool noInsert, bool noInteract, JsonElement content)
    { Id = id; Level = level; ItemId = itemId; Base = itemBase; AutoFit = autoFit; Fit = fit; Grip = grip; NoInsert = noInsert; NoInteract = noInteract; Content = content; }

    public string Id { get; }
    public MapLevelReference Level { get; }
    public uint ItemId { get; }
    public CarryItemBase Base { get; }
    /// <summary>Scale the model uniformly into the base body's rendered bounds before the author's own transform.</summary>
    public bool AutoFit { get; }
    public CarryItemFit Fit { get; }
    public CarryItemGrip? Grip { get; }
    /// <summary>The item cannot be put into any device: its first-person shell looks for no insert target.</summary>
    public bool NoInsert { get; }
    /// <summary>While this item is in hand the player can use nothing else; dropping it stays available.</summary>
    public bool NoInteract { get; }
    public JsonElement Content { get; }
}

/// <summary>The document `plugins/&lt;package&gt;/forge/carry-items.forge`. A document that does not read is refused whole
/// with its own code, like the placed-model documents.</summary>
internal static class CarryItemManifest
{
    public const int MaximumItems = 256;
    public const string FileNameOnly = "carry-items.forge";

    /// <summary>The vanilla ItemDataBlock each base clones; also what the website's Item row is copied from.</summary>
    public static uint BaseItemId(CarryItemBase itemBase) => itemBase switch
    {
        CarryItemBase.PowerCell => 131,
        CarryItemBase.Cargo => 138,
        CarryItemBase.Hsu => 151,
        _ => throw new ArgumentOutOfRangeException(nameof(itemBase)),
    };

    public static bool IdIsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        for (int index = 0; index < id.Length; index++)
        {
            char c = id[index];
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' && index > 0)) return false;
        }
        return true;
    }

    public static bool TryParse(string text, out IReadOnlyList<CarryItem>? items, out string? code, out string? reason)
    {
        items = null; code = null; reason = null;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException error) { code = "carry-item-json"; reason = error.Message; return false; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { code = "carry-item-json"; reason = "The document root is not an object."; return false; }
            if (!DocumentReading.Known(root, out var unknown, "schemaVersion", "items"))
            { code = "carry-item-field"; reason = "Unknown field `" + unknown + "`."; return false; }
            if (!FormatVersion.Matches(root))
            { code = "carry-item-schema"; reason = "The document must declare schemaVersion " + FormatVersion.Current + "."; return false; }
            if (!root.TryGetProperty("items", out var rows) || rows.ValueKind != JsonValueKind.Array)
            { code = "carry-item-rows"; reason = "The document needs an items array."; return false; }
            if (rows.GetArrayLength() > MaximumItems)
            { code = "carry-item-limit"; reason = "The document carries " + rows.GetArrayLength() + " items; the cap is " + MaximumItems + "."; return false; }

            var parsed = new List<CarryItem>(rows.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryRow(row, out var item, out code, out reason)) return false;
                if (!seen.Add(item!.Level + "|" + item.Id))
                { code = "carry-item-duplicate"; reason = "Item `" + item.Id + "` is listed more than once for level " + item.Level + "."; return false; }
                parsed.Add(item);
            }
            items = Array.AsReadOnly(parsed.ToArray());
            return true;
        }
    }

    private static bool TryRow(JsonElement row, out CarryItem? item, out string? code, out string? reason)
    {
        item = null; code = "carry-item-row"; reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "An item row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "itemId", "base", "autoFit", "fit", "grip", "noInsert", "noInteract", "content"))
        { code = "carry-item-field"; reason = "Unknown item field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "id", 64, out var id) || !IdIsValid(id))
        { code = "carry-item-id"; reason = "An item id is 1 to 64 lowercase letters, digits or `-`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "carry-item-level"; reason = "An item needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!RuntimeJson.TryInteger(row, "itemId", 1, uint.MaxValue, out var itemId))
        { code = "carry-item-item"; reason = "`itemId` is the native Item block the item exports as."; return false; }
        if (!DocumentReading.Text(row, "base", 16, out var baseText) || !TryBase(baseText!, out var itemBase))
        { code = "carry-item-base"; reason = "A base is `power-cell`, `cargo` or `hsu`."; return false; }
        if (itemId == BaseItemId(itemBase))
        { code = "carry-item-item"; reason = "A custom item needs its own Item block, not its vanilla base."; return false; }
        bool autoFit = true;
        if (row.TryGetProperty("autoFit", out var fit))
        {
            if (fit.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            { code = "carry-item-fit"; reason = "`autoFit` is true or false."; return false; }
            autoFit = fit.GetBoolean();
        }
        if (!row.TryGetProperty("fit", out var fitRow) || fitRow.ValueKind != JsonValueKind.Object || !DocumentReading.Known(fitRow, out _, "position", "rotation", "scale")
            || !DocumentReading.Numbers(fitRow, "position", 3, -1000000, 1000000, out var position) || !DocumentReading.Numbers(fitRow, "rotation", 4, -1, 1, out var rotation)
            || !DocumentReading.Numbers(fitRow, "scale", 3, -1000000, 1000000, out var scale))
        { code = "carry-item-fit"; reason = "`fit` is the author pose: `position` (3), `rotation` quaternion (4) and `scale` (3)."; return false; }
        CarryItemGrip? grip = null;
        if (row.TryGetProperty("grip", out var gripRow))
        {
            if (gripRow.ValueKind != JsonValueKind.Object || !DocumentReading.Known(gripRow, out _, "left", "right"))
            { code = "carry-item-grip"; reason = "`grip` names `left` and `right` hand anchors."; return false; }
            double[]? left = null, right = null;
            if (gripRow.TryGetProperty("left", out _) && !DocumentReading.Numbers(gripRow, "left", 3, -1000, 1000, out left)
                || gripRow.TryGetProperty("right", out _) && !DocumentReading.Numbers(gripRow, "right", 3, -1000, 1000, out right))
            { code = "carry-item-grip"; reason = "A hand anchor is three finite numbers."; return false; }
            grip = new CarryItemGrip(left == null ? null : Array.AsReadOnly(left), right == null ? null : Array.AsReadOnly(right));
        }
        if (!Flag(row, "noInsert", out bool noInsert) || !Flag(row, "noInteract", out bool noInteract))
        { code = "carry-item-field"; reason = "`noInsert` and `noInteract` are true or false."; return false; }
        if (!row.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
        { code = "carry-item-content"; reason = "An item carries its compiled model content."; return false; }
        item = new CarryItem(id!, level, (uint)itemId, itemBase, autoFit, new CarryItemFit(Array.AsReadOnly(position!), Array.AsReadOnly(rotation!), Array.AsReadOnly(scale!)), grip, noInsert, noInteract, content.Clone());
        return true;
    }

    private static bool Flag(JsonElement row, string name, out bool value)
    {
        value = false;
        if (!row.TryGetProperty(name, out var field)) return true;
        if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = field.GetBoolean();
        return true;
    }

    private static bool TryBase(string text, out CarryItemBase itemBase)
    {
        switch (text)
        {
            case "power-cell": itemBase = CarryItemBase.PowerCell; return true;
            case "cargo": itemBase = CarryItemBase.Cargo; return true;
            case "hsu": itemBase = CarryItemBase.Hsu; return true;
            default: itemBase = default; return false;
        }
    }
}
