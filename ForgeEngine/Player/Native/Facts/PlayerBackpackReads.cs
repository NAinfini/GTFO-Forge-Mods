using System;
using Player;

namespace ForgePlayer.Native;

internal readonly record struct PlayerBackpackSample(IntPtr Backpack, IntPtr Item, IntPtr Instance, PlayerBackpackSlot Value);

internal static class PlayerBackpackReads
{
    internal static bool TryRead(PlayerAgent agent, int slot, out PlayerBackpackSample sample, out string code)
    {
        sample = default;
        code = "backpack-slot-unreadable";
        if (slot < 0 || slot > byte.MaxValue || !Enum.IsDefined(typeof(InventorySlot), (InventorySlot)slot))
        { code = "backpack-slot-invalid"; return false; }
        try
        {
            if (!Capture(agent, (InventorySlot)slot, out var first) || !Capture(agent, (InventorySlot)slot, out var second)
                || first != second) return false;
            sample = first; code = ""; return true;
        }
        catch (Exception error) { PlayerCaughtFailures.Capture("backpack-read", error); return false; }
    }

    private static bool Capture(PlayerAgent agent, InventorySlot slot, out PlayerBackpackSample sample)
    {
        sample = default;
        var owner = agent.Owner;
        if (owner == null || !PlayerBackpackManager.TryGetBackpack(owner, out var backpack) || backpack == null
            || backpack.Owner?.Pointer != owner.Pointer) return false;
        bool exists = backpack.TryGetBackpackItem(slot, out var item);
        if (!exists)
        {
            sample = new(backpack.Pointer, IntPtr.Zero, IntPtr.Zero, new(false, null, null, null, false));
            return true;
        }
        if (item == null || item.Pointer == IntPtr.Zero) return false;
        var instance = item.Instance;
        var gear = item.GearIDRange;
        int status = (int)item.Status;
        if (status is < 0 or > 1) return false;
        if (instance != null && instance.Pointer != IntPtr.Zero && backpack.GetBackpackSlot(instance) != slot) return false;
        sample = new(backpack.Pointer, item.Pointer, instance?.Pointer ?? IntPtr.Zero,
            new(true, item.ItemID, gear?.GetChecksum(), status, item.IsLoaded));
        return agent.Owner?.Pointer == owner.Pointer;
    }
}
