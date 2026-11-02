using System;
using LevelGeneration;

namespace ForgeOrdnance;

/// <summary>The settled replicated pickup transition, on each peer; attempted/cancelled interactions stay silent.</summary>
internal static class SupplyPickupSound
{
    internal static void Changed(pPickupItemState oldState, pPickupItemState newState, bool isRecall)
    {
        try
        {
            if (isRecall || oldState.status != ePickupItemStatus.PlacedInLevel || newState.status != ePickupItemStatus.PickedUp) return;
            var position = oldState.placement.position;
            if (!Deliveries.IsSupplyPosition(position)) return;
            OrdnanceCues.At("supply.pickup", position);
        }
        catch (Exception error) { Plugin.Error("Supply pickup sound failed: " + error); }
    }
}
