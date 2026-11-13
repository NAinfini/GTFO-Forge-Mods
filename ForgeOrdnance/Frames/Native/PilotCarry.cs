using Player;

namespace ForgeOrdnance.Frames;

/// <summary>A suit whose definition has <c>CarryHeavy</c> (the Taishan) lets its pilot carry the heavy objects of the game's in-level
/// carry slot (power cell, fog turbine, neonate and cargo containers, data sphere, matter wave projector, hardcases) on its back
/// instead of in both hands. The object stays in the carry slot: the game's pick-up state, drop, insertion and everything the object
/// does while carried (the fog turbine repels fog from its pick-up state, not from the hand) are unchanged. The special-ability key
/// (<see cref="FrameKeys.Special"/>, V by default) moves it between the hands and the back: from the hands the pilot wields the weapon
/// last held (the game's synced wield request, <c>PlayerSync.WantsToWieldSlot</c>, which the game refuses while the carry slot is in
/// hand until <see cref="Releases"/> lifts that block for this pilot), from the back the request wields the carry slot again. With a
/// weapon in hand the game's own weapon rules apply, so the pilot runs, switches and fires. Leaving the suit puts the object back in the
/// hands. The hands-and-back moves are <see cref="ForgeCore.CarrySlot"/>; this says who may. Local player only; nothing is sent beyond the game's own wield sync.</summary>
internal static class PilotCarry
{
    /// <summary>Asked by ForgeCore when the game reports the carry slot's wield block (<see cref="ForgeCore.GameHooks.WieldedSlotBlocked"/>):
    /// lifted for the local pilot of a heavy-carrying suit only (the suit's definition says so), so their synced wield request from the
    /// hands is accepted. Every other player and suit keeps the game's rule; the game's key handling (<c>PlayerInventoryLocal.CheckInput</c>),
    /// which tests the slot itself, stays off while the object is in hand.</summary>
    internal static bool Releases(PlayerInventoryBase inventory)
    {
        if (!LocalPilot.Piloting) return false;
        var mine = ForgeCore.Players.Local?.Inventory;
        return FrameRules.CarryBlockReleased(mine != null && mine.Pointer == inventory.Pointer, true, LocalPilot.CarriesHeavy);
    }

    /// <summary>Asked by ForgeCore when the game reads the carry slot as held (<see cref="ForgeCore.GameHooks.LocalItemHeld"/>, which the
    /// game's interactions ask): with the object on the back (the pilot of a heavy-carrying suit has another slot in hand) it reads as
    /// empty, so supplies, boxes, doors and terminals work. In the hands, or outside the suit, the game's rule stands.</summary>
    internal static bool OnBack(InventorySlot slot)
        => slot == InventorySlot.InLevelCarry && LocalPilot.Piloting && LocalPilot.CarriesHeavy
           && ForgeCore.Players.Local?.Inventory is { } inventory && inventory.WieldedSlot != InventorySlot.InLevelCarry;
}
