using System;
using ForgeEngine.Framework;
using Gear;
using Player;

namespace ForgeWeapon.Native;

/// <summary>The read rows' native half: the magazine numbers of one equipment life, how many of one item a
/// holder's backpack carries. Every member re-reads the live objects on the call and caches nothing, and a read
/// that cannot be answered refuses with its own code rather than answering a substituted number — a plan reading
/// an ammunition count must never receive a zero the game never reported. The equipment read goes through the identity table's own current-check, so a
/// life this machine no longer answers for refuses instead of being read from whatever object its slot holds
/// now.</summary>
internal sealed class InventoryQuerySource : IInventoryQuerySource
{
    /// <summary>The equipment reference is not one this machine answers for right now: another world, an ended
    /// life, or a slot whose item was replaced.</summary>
    internal const string StaleEquipmentCode = "stale-equipment";
    /// <summary>The equipment life's item has no magazine: a tool, a device or an empty slot.</summary>
    internal const string NoMagazineCode = "no-magazine";
    /// <summary>The slot's own ammunition pool does not read, so the reserve is unknown rather than zero.</summary>
    internal const string NoPoolCode = "no-pool";
    /// <summary>The holder is not a player this machine can name an agent for.</summary>
    internal const string NoHolderCode = "holder-not-a-player";
    /// <summary>The item resource names no block this process can read.</summary>
    internal const string NoItemCode = "no-such-item";
    /// <summary>The holder's own backpack does not read, so nothing can be counted.</summary>
    internal const string NoBackpackCode = "no-backpack";

    private readonly EquipmentNativeAdapter _adapter;

    internal InventoryQuerySource(EquipmentNativeAdapter adapter)
        => _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    /// <summary>The clip the weapon has loaded, the capacity it reports and the rounds left in the pool its own
    /// slot draws from. The pool is read from the slot the item sits in rather than from a named ammunition type:
    /// that is the pool the reload rows already observe, so a query and a refill fact answer about one object.
    /// </summary>
    public bool TryEquipmentAmmo(EntityReference equipment, out EquipmentAmmo ammo, out string code)
    {
        ammo = new EquipmentAmmo(0, 0, 0);
        if (_adapter.EquippableOf(equipment) is not { } found)
        {
            code = StaleEquipmentCode;
            return false;
        }
        var weapon = found.Item.TryCast<BulletWeapon>();
        if (weapon == null)
        {
            code = NoMagazineCode;
            return false;
        }
        int clip = Safe(() => weapon.GetCurrentClip(), -1);
        int capacity = Safe(() => weapon.GetMaxClip(), -1);
        if (clip < 0 || capacity < 0)
        {
            code = NoMagazineCode;
            return false;
        }
        var storage = Safe<PlayerAmmoStorage?>(() => found.Backpack.AmmoStorage, null);
        var pool = storage == null
            ? null
            : Safe<InventorySlotAmmo?>(() => storage.GetInventorySlotAmmo((InventorySlot)found.SlotIndex), null);
        if (pool is not { } rounds)
        {
            code = NoPoolCode;
            return false;
        }
        int reserve = Safe(() => (int)rounds.BulletsInPack, -1);
        if (reserve < 0)
        {
            code = NoPoolCode;
            return false;
        }
        ammo = new EquipmentAmmo(clip, capacity, reserve);
        code = "";
        return true;
    }

    /// <summary>How many of one item the holder's backpack carries: the game's own pocket count of that id, which
    /// is what groups the same item across the pocket slots into one stack, plus the item's own declared slot when
    /// that slot holds the same id. A weapon, a pack or a piece of gear is not a pocket item at all, so without
    /// that second read it would be counted as nothing.</summary>
    public bool TryHeldCount(EntityReference holder, string resourceId, out int count, out string code)
    {
        count = 0;
        var player = _adapter.PlayerOf(holder);
        if (player == null)
        {
            code = NoHolderCode;
            return false;
        }
        if (!WeaponSupplyItems.TryResolve(resourceId, out uint itemId, out var slot))
        {
            code = NoItemCode;
            return false;
        }
        if (!PlayerBackpackManager.TryGetBackpack(player, out var backpack) || backpack == null)
        {
            code = NoBackpackCode;
            return false;
        }
        int held = Safe(() => backpack.CountPocketItem(itemId), -1);
        if (held < 0)
        {
            code = NoBackpackCode;
            return false;
        }
        // The pocket table's own slot, when that slot holds the item the resource id resolved to: the id compare is
        // what keeps a weapon or a pack in the same slot from being counted as the stack.
        var slots = backpack.Slots;
        int index = (int)slot;
        var slotItem = slots != null && index >= 0 && index < slots.Length ? slots[index] : null;
        if (slotItem != null && slotItem.ItemID == itemId) held++;
        count = held;
        code = "";
        return true;
    }

    /// <summary>One native read, with a failed call answered as the fallback. Every read here is a game member
    /// reached through interop, and a read that throws is exactly the case where a row must refuse rather than
    /// answer.</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception failure) { NativeCallFailure.Note("InventoryQuerySource.Safe", failure); return fallback; }
    }
}
