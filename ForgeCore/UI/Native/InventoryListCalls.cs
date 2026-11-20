using System;
using HarmonyLib;
using Player;

namespace ForgeUI;

/// <summary>The weapon list's four calls (<c>PUI_Inventory.UpdateAllSlots</c>, <c>UpdateInfoForItem</c>, <c>UpdateAmmoForItem</c>,
/// <c>SetSlotAmmo</c>) that the hidden-item hold (<see cref="WeaponListHidden"/>), the gear names and the inventory readout all read: one
/// patch class per call (group <see cref="PatchGroup.ListCalls"/>, put on with any of the three), so each call is patched once. Before the
/// body the hold may skip it (it returns false only for the local list while a hidden item is in it); Harmony still runs the postfix
/// then, as it ran the other features' postfixes before. After the body the gear names draw the registered names (on the two calls that
/// set names) or the inventory readout marks its rows for the read at the end of the frame (on the two that set counts).
/// <c>UpdateInfoForItem</c> also stands for the row calls it inlines, which have no caller of their own in this build:
/// <c>PUI_InventoryItem.SetArchetypeName</c> and <c>SetDetailedName</c> (after it the readout marks its names and the gear names translate
/// the rows' name lines, then draw the packet names) and <c>SetAllShowFlags</c>; <c>SetSlotAmmo</c> stands for
/// <c>PUI_InventoryItem.SetAll</c>.</summary>
internal static class InventoryListCalls
{
    [PatchGroup(PatchGroup.ListCalls)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateAllSlots))]
    private static class AllSlots
    {
        private static bool Prefix(PUI_Inventory __instance)
        {
            try { return !WeaponListHidden.HoldsAll(__instance); }
            catch (Exception error)
            {
                Plugin.Error("ui-holding-weapon-list-hidden-item-failed", "Holding the weapon list for a hidden item failed: " + error);
                return true;
            }
        }

        private static void Postfix() => Named();
    }

    // PUI_Inventory.UpdateItemUI needs no patch: its native body only calls UpdateInfoForItem and UpdateAmmoForItem for the same
    // item, both patched here, and UpdateAmmoForItem sets no name.

    [PatchGroup(PatchGroup.ListCalls)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateInfoForItem))]
    private static class ItemInfo
    {
        private static bool Prefix(PUI_Inventory __instance, BackpackItem bpItem) => !WeaponListHidden.Holds(__instance, bpItem);

        private static void Postfix(PUI_Inventory __instance)
        {
            if (PatchGroups.Has(PatchGroup.Inventory)) UIKit.InventoryReading.MarkNamesDirty();
            if (PatchGroups.Has(PatchGroup.GearNames)) GearNames.ListRowsNamed(__instance);
            Named();
        }
    }

    [PatchGroup(PatchGroup.ListCalls)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.UpdateAmmoForItem))]
    private static class ItemAmmo
    {
        private static bool Prefix(PUI_Inventory __instance, BackpackItem bpItem) => !WeaponListHidden.Holds(__instance, bpItem);
        private static void Postfix() => Counted();
    }

    [PatchGroup(PatchGroup.ListCalls)]
    [HarmonyPatch(typeof(PUI_Inventory), nameof(PUI_Inventory.SetSlotAmmo))]
    private static class SlotAmmo
    {
        private static bool Prefix(PUI_Inventory __instance, InventorySlot slot)
        {
            try { return !WeaponListHidden.HoldsSlot(__instance, slot); }
            catch (Exception error)
            {
                Plugin.Error("ui-holding-weapon-lists-counts-hidden-item", "Holding the weapon list's counts for a hidden item failed: " + error);
                return true;
            }
        }

        private static void Postfix() => Counted();
    }

    private static void Named()
    {
        if (PatchGroups.Has(PatchGroup.GearNames)) GearNames.ListSlotNamed();
    }

    private static void Counted()
    {
        if (PatchGroups.Has(PatchGroup.Inventory)) UIKit.InventoryReading.MarkNumbersDirty();
    }
}
