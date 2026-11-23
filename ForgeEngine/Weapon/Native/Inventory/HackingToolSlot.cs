using System;
using System.Collections.Generic;
using GameData;
using Gear;
using HarmonyLib;
using Player;

namespace ForgeWeapon.Native;

/// <summary>The fixed tool is assembled as gear, outside the three selectable loadout pools. Replace only
/// this call's BaseItem; native assembly, storage, ownership and callbacks still run on every peer.</summary>
internal static class HackingToolSlot
{
    internal static GearIDRange Select(WeaponFieldSet fields, InventorySlot slot, GearIDRange requested)
    {
        if (slot != InventorySlot.HackingTool || requested.GetCompID(eGearComponent.BaseItem) != 53) return requested;
        WeaponFieldEntry? selected = null;
        foreach (var entry in fields.Entries)
        {
            if (entry.FixedSlot != "HackingTool") continue;
            if (selected != null)
                throw new InvalidOperationException("weapon-fixed-slot-conflict: " + selected.Source + " / " + entry.Source);
            selected = entry;
        }
        if (selected == null) return requested;
        var item = ItemDataBlock.GetBlock(selected.Id);
        if (item == null || item.persistentID != selected.Id || item.inventorySlot != InventorySlot.HackingTool)
            throw new InvalidOperationException("weapon-fixed-slot-item-invalid: " + selected.Source);
        var authored = new GearIDRange(requested.ToJSON());
        authored.SetCompID(eGearComponent.BaseItem, selected.Id, false);
        authored.PublicGearName = item.publicName;
        if (authored.GetCompID(eGearComponent.BaseItem) != selected.Id)
            throw new InvalidOperationException("weapon-fixed-slot-identity-unconfirmed: " + selected.Source);
        return authored;
    }
}

[HarmonyPatch(typeof(PlayerBackpack), nameof(PlayerBackpack.SpawnAndEquipGearAsync))]
internal static class HackingToolSlotSpawn
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(InventorySlot __0, ref GearIDRange __1)
    {
        if (__0 != InventorySlot.HackingTool || __1 == null) return;
        var session = WeaponNativeSession.Offer(WeaponFeature.FixedTool);
        if (session == null) return;
        __1 = session.Guard(static (current, request) =>
        {
            try { return HackingToolSlot.Select(current.Fields, request.Slot, request.Gear); }
            catch (Exception error)
            {
                current.Withdraw(WeaponFeature.FixedTool, error.GetBaseException().Message);
                return request.Gear;
            }
        }, (Slot: __0, Gear: __1), __1);
    }
}

internal static partial class WeaponNativeHooks
{
    static partial void NativeFixedToolHookTypes(List<Type> types) => types.Add(typeof(HackingToolSlotSpawn));
}
