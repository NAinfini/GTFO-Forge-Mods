using System.Globalization;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The weak door of an authored room's incoming connection. Compiled generation supplies the actual
/// paired plug/gate receipt. Serials and traversal ordinals never select a door.</summary>
internal static class WeakDoorObservation
{
    internal static LG_WeakDoor? ByAddress(MapObjectReference address)
    {
        if (InteriorDoorIdentity.ParseKey(address.Key) != null) return InteriorDoorObservation.Current?.ByAddress(address);
        var instance = MapObjectDoorAddress.ParseWeakRoom(address.Key);
        if (address.Category != MapObjectCategories.Door || instance == null) return null;
        var scope = new ZoneCoordinates(int.Parse(address.Dimension, CultureInfo.InvariantCulture),
            int.Parse(address.Layer, CultureInfo.InvariantCulture), int.Parse(address.Zone, CultureInfo.InvariantCulture));
        var session = MapHost.Session;
        var zone = ZoneIndex.Current(session?.WorldEpoch).ZoneAt(scope);
        if (zone == null) return Refuse(address, "weak-door-zone-missing");
        if (session != null && LevelIdentity.Read() is { } level
            && GeneratedRoomRegistry.GeneratedConnection(session.WorldEpoch, level, zone, instance) is { } generated)
        {
            if (generated.To.Dimension != scope.Dimension || generated.To.Layer != scope.Layer
                || generated.To.LocalIndex != scope.Zone)
                return Refuse(address, "weak-door-room-mismatch");
            return Weak(address, zone, generated.Gate);
        }
        return Refuse(address, "weak-door-room-unbound");
    }

    private static LG_WeakDoor? Weak(MapObjectReference address, LG_Zone zone, LG_Gate? gate)
    {
        if (gate == null || gate.WasCollected || gate.SpawnedDoor == null) return Refuse(address, "weak-door-gate-unbuilt");
        var weak = gate.SpawnedDoor.TryCast<LG_WeakDoor>();
        if (weak == null) return Refuse(address, "weak-door-kind-mismatch");
        if (weak.WasCollected || weak.gameObject == null || weak.Gate == null || weak.Gate.Pointer != gate.Pointer)
            return Refuse(address, "weak-door-instance-stale");
        if (gate.m_linksFrom?.m_zone?.Pointer != zone.Pointer || gate.m_linksTo?.m_zone?.Pointer != zone.Pointer)
            return Refuse(address, "weak-door-zone-mismatch");
        return weak;
    }

    internal static MapObjectReference? Address(LG_WeakDoor weak)
    {
        if (InteriorDoorObservation.Current?.Address(weak) is { } interior) return interior;
        if (weak == null || weak.WasCollected || weak.Gate == null) return null;
        var zone = weak.Gate.m_linksTo?.m_zone;
        var session = MapHost.Session;
        if (zone == null || !ZoneIndex.Current(session?.WorldEpoch).TryCoordinates(zone, out var coordinates)) return null;
        if (session != null && LevelIdentity.Read() is { } level)
            foreach (var row in GeneratedRoomRegistry.GeneratedConnections(session.WorldEpoch, level, zone))
            {
                if (row.Gate == null || row.Gate.Pointer != weak.Gate.Pointer) continue;
                var address = MapObjectDoorAddress.CreateWeakRoom(coordinates.Dimension, coordinates.Layer,
                    coordinates.Zone, row.To.InstanceId!);
                return address != null && ByAddress(address)?.Pointer == weak.Pointer ? address : null;
            }
        return null;
    }

    private static LG_WeakDoor? Refuse(MapObjectReference address, string reason)
    {
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.WeakDoorRefused, "Authored weak door could not be bound.",
            RuntimeLogValue.Of("address", address.ToString()), RuntimeLogValue.Of("reason", reason));
        return null;
    }
}
