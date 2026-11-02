using Player;

namespace ForgeOrdnanceFrames;

/// <summary>A suit whose definition has <c>CarryHeavy</c> (the Taishan) lets its pilot carry the heavy objects of the game's in-level
/// carry slot (power cell, fog turbine, neonate and cargo containers, data sphere, matter wave projector, hardcases) on its back
/// instead of in both hands. The object stays in the carry slot: the game's pick-up state, drop, insertion and everything the object
/// does while carried (the fog turbine repels fog from its pick-up state, not from the hand) are unchanged. The special-ability key
/// (<see cref="FrameKeys.Special"/>, V by default) moves it between the hands and the back: from the hands the pilot wields the weapon
/// last held (the game's synced wield request, <c>PlayerSync.WantsToWieldSlot</c>, which the game refuses while the carry slot is in
/// hand until <see cref="Releases"/> lifts that block for this pilot), from the back the request wields the carry slot again. With a
/// weapon in hand the game's own weapon rules apply, so the pilot runs, switches and fires. Leaving the suit puts the object back in the
/// hands. Local player only; nothing is sent beyond the game's own wield sync.</summary>
internal static class PilotCarry
{
    private static readonly InventorySlot[] Hands = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearClass, InventorySlot.GearMelee };
    private const float AskEvery = 0.5f;
    private static InventorySlot _hand = InventorySlot.GearStandard;
    private static float _askAgainAt;

    /// <summary>What the HUD line says: null nothing is carried, false the object is on the back, true it is in the hands.</summary>
    internal static bool? InHands { get; private set; }

    /// <summary>Every frame the local pilot of a heavy-carrying suit is under control: remembers the weapon in hand and what to show.</summary>
    internal static void Track(PlayerAgent agent)
    {
        var inventory = agent.Inventory;
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (inventory == null || backpack == null) { InHands = null; return; }
        var wielded = inventory.WieldedSlot;
        if (wielded != InventorySlot.InLevelCarry) _hand = wielded;
        InHands = HasItem(backpack, InventorySlot.InLevelCarry) ? wielded == InventorySlot.InLevelCarry : null;
    }

    /// <summary>The ability key was pressed: hands to back, or back to hands.</summary>
    internal static void Toggle(PlayerAgent agent, float now)
    {
        var inventory = agent.Inventory;
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (inventory == null || backpack == null || now < _askAgainAt || !HasItem(backpack, InventorySlot.InLevelCarry)) return;
        if (inventory.WieldedSlot != InventorySlot.InLevelCarry) { Wield(agent, InventorySlot.InLevelCarry, now); return; }
        if (HasItem(backpack, _hand)) { Wield(agent, _hand, now); return; }
        foreach (var slot in Hands)
            if (HasItem(backpack, slot)) { Wield(agent, slot, now); return; }
    }

    /// <summary>Asked by ForgeCore when the game reports the carry slot's wield block (<see cref="ForgeCore.GameHooks.WieldedSlotBlocked"/>):
    /// lifted for the local pilot of a heavy-carrying suit only (the suit's definition says so), so their synced wield request from the
    /// hands is accepted. Every other player and suit keeps the game's rule; the game's key handling (<c>PlayerInventoryLocal.CheckInput</c>),
    /// which tests the slot itself, stays off while the object is in hand.</summary>
    internal static bool Releases(PlayerInventoryBase inventory)
    {
        if (!LocalPilot.Piloting) return false;
        var mine = PlayerManager.GetLocalPlayerAgent()?.Inventory;
        return FrameRules.CarryBlockReleased(mine != null && mine.Pointer == inventory.Pointer, true, LocalPilot.CarriesHeavy);
    }

    /// <summary>Asked by ForgeCore when the game reads the carry slot as held (<see cref="ForgeCore.GameHooks.LocalItemHeld"/>, which the
    /// game's interactions ask): with the object on the back (the pilot of a heavy-carrying suit has another slot in hand) it reads as
    /// empty, so supplies, boxes, doors and terminals work. In the hands, or outside the suit, the game's rule stands.</summary>
    internal static bool OnBack(InventorySlot slot)
        => slot == InventorySlot.InLevelCarry && LocalPilot.Piloting && LocalPilot.CarriesHeavy
           && PlayerManager.GetLocalPlayerAgent()?.Inventory is { } inventory && inventory.WieldedSlot != InventorySlot.InLevelCarry;
    /// <summary>The object is still to be put back in the hands (the suit was left): <see cref="Settle"/> keeps asking until the game wields it.</summary>
    internal static bool Pending { get; private set; }

    /// <summary>The suit is left (any way): the object is back in the hands, where the game drops and delivers it. The wield request is
    /// the game's own and it ignores one made while the leaving still holds the inventory, so <see cref="Settle"/> repeats it until it holds.</summary>
    internal static void GiveBack()
    {
        InHands = null;
        Pending = true;
        _askAgainAt = 0f;
    }

    /// <summary>Every frame while the object is pending: done once the game wields it, or nothing is carried, or the player is gone.</summary>
    internal static void Settle(float now)
    {
        if (!Pending) return;
        var agent = PlayerManager.GetLocalPlayerAgent();
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (agent == null || agent.WasCollected || !agent.Alive || backpack == null || agent.Inventory == null
            || !HasItem(backpack, InventorySlot.InLevelCarry) || agent.Inventory.WieldedSlot == InventorySlot.InLevelCarry) { Pending = false; return; }
        if (now >= _askAgainAt) Wield(agent, InventorySlot.InLevelCarry, now);
    }

    private static bool HasItem(PlayerBackpack backpack, InventorySlot slot)
        => backpack.TryGetBackpackItem(slot, out var item) && item != null && item.IsLoaded;

    private static void Wield(PlayerAgent agent, InventorySlot slot, float now)
    {
        _askAgainAt = now + AskEvery;
        agent.Sync.WantsToWieldSlot(slot);
    }
}
