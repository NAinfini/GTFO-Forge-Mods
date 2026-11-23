using System;
using System.Globalization;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Landing into a chosen zone with the game's own team warp (event 7 or 8). The native event never reads
/// <c>LocalIndex</c>: it lands on the world event object its <c>WorldEventObjectFilter</c> names (results[PlayerSlotIndex]
/// when there is one per player, else the first), or else on the dimension's spawn point, and for Reality on the
/// player's last position there. An authored "land in zone" therefore writes the filter
/// <c>forge-zone-landing:&lt;dimension&gt;/&lt;layer&gt;/&lt;localIndex&gt;;</c> (the website codec), and just before the native
/// body reads it the named object is put in that zone's first area and registered the way the native collection job
/// does (LG_WorldEventObject.Setup, WorldEventManager.RegisterWorldEventObject). The warp itself, its point (the area's
/// node closest to the object) and its replication stay native. Every machine runs the event, so every machine makes
/// the same object by the same name; nothing is sent. The object belongs to the area and goes with the level.</summary>
internal static class ZoneLandingObjects
{
    internal const string Prefix = "forge-zone-landing:";

    private static readonly LG_WorldEventObject[] Found = new LG_WorldEventObject[1];

    internal static void Ensure(WardenObjectiveEventData? data)
    {
        if (data == null || data.Type is not (eWardenObjectiveEventType.DimensionWarpTeam or eWardenObjectiveEventType.DimensionFlashTeam)) return;
        var filter = data.WorldEventObjectFilter;
        if (filter == null || !filter.StartsWith(Prefix, StringComparison.Ordinal)) return;
        try
        {
            if (WorldEventManager.GetWorldEventObjectsNonAlloc(filter, Found) > 0) return;
            if (Parse(filter) is not { } at) { Refuse(filter, "filter-unreadable"); return; }
            if (!ForgeCore.Level.TryZone(at.Dimension, at.Layer, at.LocalIndex, out var zone) || zone == null) { Refuse(filter, "zone-missing"); return; }
            var area = zone.m_areas != null && zone.m_areas.Count > 0 ? zone.m_areas[0] : null;
            if (area == null || area.WasCollected || !ForgeCore.Level.TryAreaCentre(zone, 0, out var centre)) { Refuse(filter, "area-missing"); return; }
            var obj = new GameObject(filter);
            obj.transform.SetParent(area.transform, false);
            obj.transform.position = centre;
            var host = obj.AddComponent<LG_WorldEventObject>();
            host.Setup(area);
            WorldEventManager.RegisterWorldEventObject(host);
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ZoneLanding, "Zone landing object registered for a native team warp.",
                RuntimeLogValue.Quote("filter", filter), RuntimeLogValue.Of("x", (double)centre.x), RuntimeLogValue.Of("y", (double)centre.y),
                RuntimeLogValue.Of("z", (double)centre.z));
        }
        catch (Exception error) { Refuse(filter, "exception:" + error.GetType().Name); }
    }

    /// <summary><c>forge-zone-landing:d/l/z;</c> to its three indices, or null.</summary>
    internal static (int Dimension, int Layer, int LocalIndex)? Parse(string filter)
    {
        var end = filter.IndexOf(';', Prefix.Length);
        if (end < 0) return null;
        var parts = filter.Substring(Prefix.Length, end - Prefix.Length).Split('/');
        if (parts.Length != 3) return null;
        var n = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return (n[0], n[1], n[2]);
    }

    private static void Refuse(string filter, string reason) =>
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ZoneLandingRefused,
            "The zone landing object was not made; the native team warp keeps its own landing.",
            RuntimeLogValue.Quote("filter", filter), RuntimeLogValue.Of("reason", reason));
}
