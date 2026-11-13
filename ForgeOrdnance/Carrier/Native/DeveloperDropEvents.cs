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

/// <summary>Turns a drop's place into a world point on the level being played, through ForgeCore's level lookups.</summary>
internal static class DropPlacement
{
    /// <summary>Null with <paramref name="point"/> set, or the reason the place does not exist in this level.</summary>
    internal static string? Resolve(DropWhere where, out Vec3 point)
    {
        point = default;
        if (!ForgeCore.Level.HasDimension(where.Dimension)) return "dimension " + where.Dimension + " does not exist in this level";
        if (!ForgeCore.Level.TryZone(where.Dimension, where.Layer, where.LocalIndex, out var zone) || zone == null)
            return "there is no zone " + where.LocalIndex + " in layer " + where.Layer + " of dimension " + where.Dimension;
        var p = where.Point;
        if (p.IsWorld) { point = new Vec3(p.X, p.Y, p.Z); return null; }
        var areas = ForgeCore.Level.AreaCount(zone);
        if (p.Area >= areas) return "zone " + where.LocalIndex + " (layer " + where.Layer + ", dimension " + where.Dimension + ") has " + areas + " area(s); Area " + p.Area + " does not exist";
        if (!ForgeCore.Level.TryAreaCentre(zone, p.Area, out var centre))
            throw new InvalidOperationException("Area " + p.Area + " of zone " + where.LocalIndex + " (layer " + where.Layer + ", dimension " + where.Dimension + ") has no course node.");
        point = new Vec3(centre.x + p.X, centre.y + p.Y, centre.z + p.Z);
        return null;
    }
}
