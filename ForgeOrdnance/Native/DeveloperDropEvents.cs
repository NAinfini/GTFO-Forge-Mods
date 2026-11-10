using System;
using GameData;
using HarmonyLib;

namespace ForgeOrdnance;

/// <summary>
/// The level event that fires a developer drop: a vanilla <c>WardenObjectiveEventData</c> whose <c>Type</c> is
/// <see cref="DeveloperDrops.EventType"/> and whose <c>WorldEventObjectFilter</c> names a drop of the level's <c>Drops</c>. It rides
/// the game's own event chain (door, scan, terminal command, objective and zone events all go through the one-event
/// <c>WardenObjectiveManager.CheckAndExecuteEventsOnTrigger</c>): the game's trigger and delay rule decides when it fires, the host
/// places the drop. The game has no meaning for this type, so the event is taken here on every peer and not passed on.
/// </summary>
[HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.CheckAndExecuteEventsOnTrigger),
    new[] { typeof(WardenObjectiveEventData), typeof(eWardenObjectiveEventTrigger), typeof(bool), typeof(float) })]
internal static class DeveloperDropEvents
{
    [HarmonyPrefix]
    private static bool Prefix(WardenObjectiveEventData __0, eWardenObjectiveEventTrigger __1, bool __2, float __3)
    {
        try
        {
            var e = __0;
            if (e == null || (int)e.Type != DeveloperDrops.EventType) return true;
            if (!DeveloperDrops.Fires((int)e.Type, (int)e.Trigger, (int)__1, __2, e.Delay, __3, out var wait)) return false;
            var place = new DropWhere((int)e.DimensionIndex, (int)e.Layer, (int)e.LocalIndex, new DropPoint(-1, e.Position.x, e.Position.y, e.Position.z));
            Plugin.Session.FireEventDrop(e.WorldEventObjectFilter ?? "", place, wait);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Ordnance drop event failed: " + error); }
        return false;
    }
}

/// <summary>The built level as <see cref="DropPlacement"/> reads it, through ForgeCore's level lookups.</summary>
internal sealed class BuiltLevel : IDropLevel
{
    internal static readonly BuiltLevel Instance = new();

    public bool HasDimension(int dimension) => ForgeCore.Level.HasDimension(dimension);

    public bool TryZone(int dimension, int layer, int localIndex, out int areaCount)
    {
        areaCount = 0;
        if (!ForgeCore.Level.TryZone(dimension, layer, localIndex, out var zone) || zone == null) return false;
        areaCount = ForgeCore.Level.AreaCount(zone);
        return true;
    }

    public Vec3 AreaCentre(int dimension, int layer, int localIndex, int area)
    {
        if (!ForgeCore.Level.TryZone(dimension, layer, localIndex, out var zone) || zone == null || !ForgeCore.Level.TryAreaCentre(zone, area, out var centre))
            throw new InvalidOperationException("Area " + area + " of zone " + localIndex + " (layer " + layer + ", dimension " + dimension + ") has no course node.");
        return new Vec3(centre.x, centre.y, centre.z);
    }
}
