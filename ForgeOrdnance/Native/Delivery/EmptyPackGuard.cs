using System;
using LevelGeneration;

namespace ForgeOrdnance;

/// <summary>Errors once (Plugin.Error keys by call site) when a resource pack is put in the level with no uses left, whoever put it there (a supply pod,
/// a player's drop or swap, a recalled checkpoint): a full pack is never empty, so the state that emptied it is in the line.</summary>
internal static class EmptyPackGuard
{
    internal static void Subscribe() => ForgeCore.GameHooks.PickupItemState.After((call, _) => Changed(call.Item, call.New, call.Old, call.Recall));

    private static void Changed(LG_PickupItem_Sync item, pPickupItemState state, pPickupItemState before, bool recall)
    {
        try
        {
            if (state.status != ePickupItemStatus.PlacedInLevel || state.custom.ammo > 0f) return;
            if (item.gameObject.GetComponentInChildren<Gear.ResourcePackPickup>(true) == null) return;
            // Kept on purpose, known and not to be fixed: calling a supply drop spawns its packs afterwards, and the state read at that moment is 0 points
            // (before already PlacedInLevel with 0), so this line appears for each pack of a call-in.
            Plugin.Error("A resource pack was put in the level with " + state.custom.ammo + " ammo points (before: " + before.status + " with " + before.custom.ammo
                + "; recall " + recall + ") at " + Plugin.Pos(state.placement.position) + ".");
        }
        catch (Exception error) { Plugin.Error("Checking a placed resource pack failed: " + error); }
    }
}
