using System;
using System.Collections.Generic;

namespace ForgeMap;

/// <summary>
/// The rules one authored fixed placement is read and chosen by, as data: which inventory slots can be placed and
/// through which native entry point, what an amount means for the slot it is written on, and the seed every
/// machine derives from a row's own id. The slot numbers are the game's own `Player.InventorySlot` ordinals; the
/// native half casts the block's slot to this number, so the mapping lives here once instead of in a second
/// switch beside it.
/// </summary>
internal static class FixedPlacement
{
    /// <summary>The native Single representation for room-local coordinates.</summary>
    public const double MaximumCoordinate = float.MaxValue;
    /// <summary>The native Single representation for authored yaw in degrees.</summary>
    public const double MaximumYawDegrees = float.MaxValue;
    /// <summary>The amount is carried by the native Single-valued custom ammo field.</summary>
    public const double MaximumAmount = float.MaxValue;
    /// <summary>The longest author id, matching the trigger-zone document's own bound.</summary>
    public const int MaximumIdChars = 128;
    /// <summary>Rows one package document may carry.</summary>
    public const int MaximumRows = 512;
    /// <summary>A sack's scale is written to the native Single-valued transform.</summary>
    public const double MaximumScale = float.MaxValue;
    /// <summary>The greatest dimension an authored landing may name: `eDimensionIndex.Dimension_20`, the last real
    /// dimension the game's own enum carries (`MAX_COUNT` is the count of them, not one of them).</summary>
    public const int MaximumDimension = 20;
    /// <summary>The player slots one dimension carries, and therefore the greatest number of authored landing rows
    /// one dimension may hold: a landing's row order inside its dimension is its player slot.</summary>
    public const int MaximumLandingSlots = 4;

    /// <summary>The inventory slot a resource pack is: `Player.InventorySlot.ResourcePack`.</summary>
    public const int ResourcePackSlot = 4;
    /// <summary>The inventory slot the fog repeller is: `Player.InventorySlot.InLevelCarry`.</summary>
    public const int InLevelCarrySlot = 8;
    /// <summary>The one item id that is a fog repeller (`ItemDataBlock` 133). It is the one placed item whose own
    /// global state has to be switched on after it is put down; every other id is placed and left alone.</summary>
    public const uint FogRepellerItemId = 133;
    /// <summary>The one item id a bulkhead door accepts, as the constant its own logic writes rather than a gate
    /// key item (`LG_BulkheadDoorController_Core.m_intUseKeyItem`). An authored bulkhead key is an ordinary item
    /// row carrying this id.</summary>
    public const uint BulkheadKeyItemId = 146;

    /// <summary>The one static-spawn block an authored fixed sack is: `StaticSpawn` 6, `Respawner_Sack`, whose
    /// `SpawnConstraints` is `AlwaysAndOnlySpawnInRespawnerZones`. The number is the block's own `persistentID`, so
    /// the prefab paths, the scale window and the two variants are all read off the block at placement time
    /// instead of being written into a document or this assembly.</summary>
    public const uint RespawnerSackBlockId = 6;

    /// <summary>The native `pItemData_Custom.ammo` a whole resource pack carries: the full-pool figure the game's
    /// own pool reads as capacity (a pack's capacity is displayed as `ammo / 100`), and the figure one vanilla
    /// resource container's own spawn body produces for a whole pool. A pack's native field is therefore not the
    /// authored share and has to be converted at the boundary, once.</summary>
    public const double NativeResourcePackFullAmmo = 100;

    /// <summary>
    /// One authored resource-pack share as the native ammo points `pItemData_Custom.ammo` carries. The document
    /// writes the share of a pool; the native field counts
    /// the same pool in whole points, so a share `s` is `100 * s` points and the pool the game then fills is that
    /// fraction of the receiver's own capacity. Nothing else converts: a consumable's native ammo is its whole
    /// number of uses and every other slot's is zero, so this is the only slot the factor applies to.
    /// </summary>
    public static double NativeAmmoForResourcePack(double share) => NativeResourcePackFullAmmo * share;

    /// <summary>Which native entry point builds one inventory slot's pickup, or null when the slot is not a
    /// pickup supported by this factory recipe. Slot None includes native commodities and the microdrive;
    /// the native pickup-mode table is checked separately, rather than inferred from this slot.</summary>
    public static FixedItemRoute? RouteOf(int inventorySlot) => inventorySlot switch
    {
        ResourcePackSlot => FixedItemRoute.ResourcePack,
        0 or 5 or 6 or 7 or InLevelCarrySlot or 9 => FixedItemRoute.Pickup,
        _ => null
    };

    /// <summary>Why one authored amount cannot be used on one inventory slot, or null when it can. The amount
    /// means the uses left on a consumable, the share of a resource pool a pack carries, and nothing at all on an
    /// item that is not consumed — so a number written where the slot has no room for it is refused rather than
    /// silently ignored, and the native half applies this test to the block's own slot.</summary>
    public static string? AmountRefusal(int inventorySlot, double amount)
    {
        if (!double.IsFinite(amount) || amount < 0 || amount > MaximumAmount)
            return "An amount is a finite number in [0, " + MaximumAmount + "].";
        switch (inventorySlot)
        {
            case ResourcePackSlot:
                return float.IsFinite((float)NativeAmmoForResourcePack(amount))
                    ? null
                    : "A resource pack's amount must produce finite native ammo points after multiplication by 100.";
            case 5 or 6:
                return Math.Floor(amount) == amount
                    ? null
                    : "A consumable's amount is the whole number of uses it has left.";
            default:
                return amount == 0 ? null : "This slot's items carry no amount; write 0.";
        }
    }

    /// <summary>
    /// The seed every machine derives for one authored row: an FNV-1a hash of the row's own id, so the number is
    /// the same on every peer of one level and differs between two rows. It is the row's identity and nothing
    /// else, because that is the only thing all peers share before they spawn anything; a counter or a machine's
    /// own random source would place a different item on each of them.
    /// </summary>
    public static uint SeedOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ForgeCore.Fnv.MixUtf8(ForgeCore.Fnv.Offset32, id);
    }

    /// <summary>The rows of one document that belong to one level, in the document's own order. The order is the
    /// document's because it is the same on every machine, which is what makes the placement sequence itself
    /// deterministic.</summary>
    public static IReadOnlyList<FixedItem> ItemsForLevel(IReadOnlyList<FixedItem> items, MapLevelReference level)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = new List<FixedItem>();
        foreach (var item in items) if (item.Level == level) rows.Add(item);
        return Array.AsReadOnly(rows.ToArray());
    }

    /// <summary>The enemy rows of one document that belong to one level, in the document's own order.</summary>
    public static IReadOnlyList<FixedEnemy> EnemiesForLevel(IReadOnlyList<FixedEnemy> enemies, MapLevelReference level)
    {
        ArgumentNullException.ThrowIfNull(enemies);
        var rows = new List<FixedEnemy>();
        foreach (var enemy in enemies) if (enemy.Level == level) rows.Add(enemy);
        return Array.AsReadOnly(rows.ToArray());
    }

    /// <summary>The sack rows of one install that belong to one level, in the documents' own order. That order is
    /// the same on every machine, which is what makes the placement sequence itself deterministic.</summary>
    public static IReadOnlyList<FixedSack> SacksForLevel(IReadOnlyList<FixedSack> sacks, MapLevelReference level)
    {
        ArgumentNullException.ThrowIfNull(sacks);
        var rows = new List<FixedSack>();
        foreach (var sack in sacks) if (sack.Level == level) rows.Add(sack);
        return Array.AsReadOnly(rows.ToArray());
    }

    /// <summary>The landing rows of one install that belong to one level, in the documents' own order. The order
    /// inside one dimension is the player slot a row is registered for, so it is preserved exactly as read.</summary>
    public static IReadOnlyList<FixedLanding> LandingsForLevel(IReadOnlyList<FixedLanding> landings, MapLevelReference level)
    {
        ArgumentNullException.ThrowIfNull(landings);
        var rows = new List<FixedLanding>();
        foreach (var landing in landings) if (landing.Level == level) rows.Add(landing);
        return Array.AsReadOnly(rows.ToArray());
    }

    /// <summary>The bulkhead-controller rows of one install that belong to one level, in the documents' own order.
    /// A level has at most one row per zone (the readers refuse a second), so the order never decides which
    /// controller a row moves.</summary>
    public static IReadOnlyList<FixedBulkheadController> BulkheadControllersForLevel(
        IReadOnlyList<FixedBulkheadController> controllers, MapLevelReference level)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        var rows = new List<FixedBulkheadController>();
        foreach (var controller in controllers) if (controller.Level == level) rows.Add(controller);
        return Array.AsReadOnly(rows.ToArray());
    }

    /// <summary>The bulkhead-key rows of one install that belong to one level, in the documents' own order. That
    /// order is the same on every machine, and it is also the order the rows claim their keys in, so the claim a
    /// later row reads is the one an earlier row of the same level made.</summary>
    public static IReadOnlyList<FixedBulkheadKey> BulkheadKeysForLevel(
        IReadOnlyList<FixedBulkheadKey> keys, MapLevelReference level)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var rows = new List<FixedBulkheadKey>();
        foreach (var key in keys) if (key.Level == level) rows.Add(key);
        return Array.AsReadOnly(rows.ToArray());
    }

    /// <summary>The one row of one level that names the controller of one zone, or null when the level has none.
    /// The zone is the controller's own terminal-node zone, which is the zone the game reads to decide which doors
    /// the controller opens; the readers keep one row per zone, so the answer is never ambiguous.</summary>
    public static FixedBulkheadController? BulkheadControllerInZone(IReadOnlyList<FixedBulkheadController> rows,
        int dimension, int layer, int localIndex)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows) if (row.InZone(dimension, layer, localIndex)) return row;
        return null;
    }

    /// <summary>The one layer a key row's wire spelling names, or false when it names none: the layer the key
    /// generated on, where `main` is one answer.</summary>
    public static bool TryKeyLayer(string text, out FixedBulkheadKeyLayer layer)
    {
        switch (text)
        {
            case "main": layer = FixedBulkheadKeyLayer.Main; return true;
            case "secondary": layer = FixedBulkheadKeyLayer.Secondary; return true;
            case "third": layer = FixedBulkheadKeyLayer.Third; return true;
            default: layer = FixedBulkheadKeyLayer.Main; return false;
        }
    }

    /// <summary>One key layer's own wire spelling, for a diagnostic that names the row it refused.</summary>
    public static string WireKey(FixedBulkheadKeyLayer layer) => layer switch
    {
        FixedBulkheadKeyLayer.Secondary => "secondary",
        FixedBulkheadKeyLayer.Third => "third",
        _ => "main"
    };

    /// <summary>
    /// Which of one level's remaining bulkhead keys one authored row claims: the first key, in the level's own
    /// registry order, that generated in the zone the row's room stands in; when none generated there, the first key
    /// that generated in the row's layer. The caller decides which keys are still open — one an
    /// earlier row of the same level already claimed is not one this row may claim again — and answers -1 when no
    /// open key of that layer is left, which is a row the placement refuses rather than guesses at. Several keys of
    /// one layer are ordinary, so this rule never refuses a row for a second candidate.
    /// </summary>
    public static int MatchBulkheadKey(IReadOnlyList<BulkheadKeyCandidate> keys, FixedBulkheadKeyLayer layer,
        int dimension, int localIndex)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var layerMatch = -1;
        foreach (var candidate in keys)
        {
            if (candidate.Layer != layer) continue;
            if (candidate.Dimension == dimension && candidate.LocalIndex == localIndex) return candidate.Index;
            if (layerMatch < 0) layerMatch = candidate.Index;
        }
        return layerMatch;
    }
}

/// <summary>One refused row of a placement document: the code, the reason, and the author's own id of the row
/// (the `id` the document wrote, empty when the row had none) so a refusal points back at the authored object.</summary>
internal readonly record struct FixedPlacementRefusal(string Code, string Reason, string AuthorId = "")
{
    /// <summary>The `id` text of a document row, empty when the row is not an object or has no text id.</summary>
    internal static string IdOf(System.Text.Json.JsonElement row)
        => row.ValueKind == System.Text.Json.JsonValueKind.Object && row.TryGetProperty("id", out var id)
            && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() ?? "" : "";
}
