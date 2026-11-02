using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeCore;

namespace ForgeOrdnance;

/// <summary>One line of an <c>Item</c> payload: <see cref="Count"/> pickups of one item data block.</summary>
internal readonly record struct ItemEntry(uint ItemId, int Count, float AmmoRatio);

/// <summary>The <c>Items</c> list of the built-in <c>Item</c> payload, which arrives in the drop pod:
/// <c>"Items": [ { "Item": 101 | "name", "Count": 1..4, "AmmoRatio": 0..1 }, ... ]</c>. The pod holds
/// <see cref="MaxPickups"/> pickups, so the counts together may not exceed that.</summary>
internal static class ItemList
{
    /// <summary>The drop pod has two pickup anchors on each side of its divider.</summary>
    public const int MaxPickups = 4;

    private static readonly string[] EntryFields = { "Item", "Count", "AmmoRatio" };

    /// <summary>Reads and checks the list. <paramref name="resolve"/> turns a persistentID or block name into the block's
    /// persistentID, or returns null when the install has no such block.</summary>
    public static ItemEntry[] Parse(JsonElement payload, Func<uint?, string?, uint?> resolve)
    {
        PayloadReader.OnlyKnown(payload, "Items");
        if (!payload.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new StrictJsonException("Items", "is required: a list of { \"Item\": id or name, \"Count\": 1.." + MaxPickups + ", \"AmmoRatio\": 0..1 }.");
        var entries = new List<ItemEntry>();
        var total = 0;
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var at = "Items[" + index++ + "].";
            StrictJson.OnlyKnown(item, at, EntryFields);
            var (id, name) = StrictJson.Reference(item, at, "Item");
            var block = resolve(id, name)
                ?? throw new StrictJsonException(at + "Item", "no item data block " + (id.HasValue ? id.Value.ToString() : "'" + name + "'") + " in this install.");
            var count = (int)StrictJson.Integer(item, at, "Count", 1, 1, MaxPickups);
            total += count;
            entries.Add(new ItemEntry(block, count, (float)StrictJson.Number(item, at, "AmmoRatio", 1, 0, 1)));
        }
        if (entries.Count == 0) throw new StrictJsonException("Items", "needs at least one item.");
        if (total > MaxPickups) throw new StrictJsonException("Items", "holds " + total + " pickups; the drop pod carries at most " + MaxPickups + ".");
        return entries.ToArray();
    }

    /// <summary>The item of each pickup in pod order (anchor 1 first), counts expanded.</summary>
    public static uint[] Expand(IReadOnlyList<ItemEntry> entries, out float[] ammoRatios)
    {
        var ids = new List<uint>();
        var ratios = new List<float>();
        foreach (var e in entries)
            for (var i = 0; i < e.Count; i++) { ids.Add(e.ItemId); ratios.Add(e.AmmoRatio); }
        ammoRatios = ratios.ToArray();
        return ids.ToArray();
    }

    /// <summary>A whole resource pack in the game's own ammo points, and the points one use costs: the game fills a container's pack
    /// in whole uses of <see cref="PackUsePoints"/> (<c>LG_ResourceContainer_Storage.SpawnResourcePack</c>) and prints
    /// <c>ammo / 100</c> as its capacity.</summary>
    public const float FullPackPoints = 100f, PackUsePoints = 20f;

    /// <summary>The native <c>pItemData_Custom.ammo</c> of a pickup holding <paramref name="ratio"/> of its full amount: a consumable
    /// (<paramref name="consumableUses"/> above 0) counts whole uses, a resource pack whole steps of <see cref="PackUsePoints"/> out of
    /// <see cref="FullPackPoints"/>. A ratio above 0 never rounds down to an empty pickup.</summary>
    public static float AmmoPoints(int consumableUses, float ratio)
    {
        var step = consumableUses > 0 ? 1f : PackUsePoints;
        var full = consumableUses > 0 ? consumableUses : FullPackPoints;
        if (ratio <= 0f) return 0f;
        return Math.Max(step, MathF.Round(ratio * full / step, MidpointRounding.AwayFromZero) * step);
    }
}
