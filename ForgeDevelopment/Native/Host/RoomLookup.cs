using System;
using System.Collections.Generic;
using System.Globalization;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>The rooms of the landed level by the authored instance id Forge Engine's Map domain writes into map-generation.forge,
/// asked through the Engine's author read (forge-engine-not-installed without it). The P1 route
/// and the console's <c>teleport room=</c> both stand a player in a room-local frame (metres, the room's own axes).</summary>
internal static class RoomLookup
{
    internal static Dictionary<string, LG_Geomorph> Rooms()
    {
        var floor = Builder.Current?.m_currentFloor ?? throw new InvalidOperationException("p1.floor-unavailable");
        var rooms = new Dictionary<string, LG_Geomorph>(StringComparer.Ordinal);
        var seen = new HashSet<IntPtr>();
        void Visit(LG_Zone? zone)
        {
            if (zone == null || zone.WasCollected || zone.m_areas == null) return;
            for (var i = 0; i < zone.m_areas.Count; i++)
            {
                var geomorph = zone.m_areas[i]?.m_geomorph;
                if (geomorph == null || geomorph.WasCollected || !seen.Add(geomorph.Pointer)) continue;
                var id = EngineLookup.Call<string?>("RoomInstanceId", geomorph);
                if (id != null && !rooms.TryAdd(id, geomorph)) throw new InvalidOperationException("p1.room-id-ambiguous:" + id);
            }
        }
        if (floor.allZones != null) foreach (var zone in floor.allZones) Visit(zone);
        if (floor.m_dimensions != null) foreach (var dimension in floor.m_dimensions)
            if (dimension?.Layers != null) foreach (var layer in dimension.Layers)
                if (layer?.m_zones != null) foreach (var zone in layer.m_zones) Visit(zone);
        return rooms;
    }

    /// <summary>The world pose of a room-local point: <c>dimension;x,y,z</c>, the two things a teleport spec needs.</summary>
    internal static string WorldPoint(string roomId, float x, float y, float z)
    {
        if (!Rooms().TryGetValue(roomId, out var room)) throw new ArgumentException("console.room-missing:" + roomId);
        var dimension = room.m_zone?.DimensionIndex ?? throw new InvalidOperationException("console.room-zone-missing:" + roomId);
        var at = room.transform.TransformPoint(new Vector3(x, y, z));
        return (int)dimension + ";" + at.x.ToString("R", CultureInfo.InvariantCulture) + "," + at.y.ToString("R", CultureInfo.InvariantCulture)
            + "," + at.z.ToString("R", CultureInfo.InvariantCulture);
    }
}
