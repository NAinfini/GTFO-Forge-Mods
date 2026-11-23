using System;
using System.Globalization;
using GameData;
using Player;

namespace ForgeWeapon.Native;

/// <summary>The item half of the inventory rows: one authored `forge.resource.item` reference turned into the
/// game's own number <b>and</b> the slot the block itself declares. The number is `ItemDataBlock.persistentID` —
/// the value `pItemData.itemID_gearCRC` carries for a non-gear item — and the table it comes from is the game's own
/// block table (`ItemDataBlock.GetBlock`), which is the only authority on which item ids exist in this build.
///
/// <b>The id's spelling.</b> A resource id is the official persistent id as plain decimal text, which is the one
/// spelling this runtime already pins for an authored native block id (the enemy domain's `enemy-type` mount and
/// `v-e-type` read row both carry `EnemyDataBlock.persistentID` as decimal text). A text that is not a decimal id,
/// or an id no block answers, is refused by name; nothing is derived from a display name, an index or a checksum.
///
/// <b>Why the block's own slot matters.</b> `MasterAddItem` places the item in the slot its `pItemData` names: the
/// private `AddItem` body branches on that byte — `InventorySlot.InPocket` (7) appends one `PocketItem` to the
/// backpack's item-id group, and every other slot calls `PlayerBackpack.SpawnAndEquipItem(itemID, slot, data)` and
/// then `PlayerAmmoStorage.SetAmmo(GetAmmoTypeFromSlot(slot), data.custom.ammo)`. So this resolver answers the one
/// pair both halves of the give row need and the adapter decides what each slot may become:
///
/// - `ForgeWeapon.Native.InventoryActionAdapter.HandleGive` takes a pocket item, a consumable or a resource pack,
///   and refuses the gear and carry slots by name (`item-slot-unsupported`) instead of writing into a slot the
///   action cannot answer for.
/// - A negative give quantity removes pocket units through `TryMasterRemovePocketItemWithID`, or one current
///   consumable/pack instance through `MasterRemoveItem(Item, SNet_Player)`, followed by the recipient's slot readback.
///
/// Giving a gear-slot item would need the game's own gear path (the slot's `BackpackItem` is built by
/// `SpawnAndEquipItem` from a spawned `Item` instance, and an occupied slot is dropped by the caller's own body
/// first), which this batch does not implement and `evidence/inventory-actions.json` records as the named gap.
///
/// This half is registration input, not a second resource system: the runtime's own resource registry, when it
/// exists, is what a plan resolves against, and this delegate is what the package answers with until then. The
/// integration passes it to `InventoryActionAdapter.ItemResolver`.</summary>
internal static class WeaponSupplyItems
{
    /// <summary>The whole answer for one authored item reference: the game's id and the slot the block itself
    /// declares. False means this process cannot name that resource at all.</summary>
    internal static bool TryResolve(string resourceId, out uint itemId, out InventorySlot slot)
    {
        itemId = 0u;
        slot = InventorySlot.None;
        if (resourceId == null || resourceId.Length == 0 || resourceId.Length > 10) return false;
        if (!uint.TryParse(resourceId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0u) return false;
        ItemDataBlock? block;
        try { block = ItemDataBlock.GetBlock(id); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponSupplyItems.TryResolve", failure); return false; }
        if (block == null) return false;
        if (block.persistentID != id) return false;
        itemId = block.persistentID;
        slot = block.inventorySlot;
        return true;
    }
}
