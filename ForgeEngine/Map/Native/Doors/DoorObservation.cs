using System;
using System.Globalization;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// One door read through its own native members on build 20403457. Nothing here is synthesized: the status is
/// the door's `LastStatus`, the lock reading is the door's own `eDoorStatus` plus the lock component's own
/// key requirement, and the address is the coordinates of the zone this door is the entrance gate of. A door
/// the level generation did not make a zone's entrance — a weak door, a node door, a decorative door — carries
/// no zone. A bulkhead is addressed by the target zone whose source gate spawned it, including layer roots.
/// `Decide` is the single reading behind both the address and the reason a source reports, so a refusal cannot
/// describe a different cause than the one that refused the door. A member that cannot be read makes the
/// reading unavailable instead of reporting a substitute.
/// </summary>
internal static class DoorObservation
{
    // The lock component's `OnDoorState` runs before the door's own `LastStatus` takes the new value, so a postfix on it reads the
    // previous status: an unlock reads as "still locked" and a lock reads as "still unlocked", which the lock fact then publishes
    // inverted or drops as a repeat. While a lock fact is read for such a callback the status the callback carries is the door's
    // status for that transition.
    private static (LG_SecurityDoor Door, eDoorStatus Status)? _callback;

    internal static void WithCallbackStatus(LG_SecurityDoor door, eDoorStatus status, Action read)
    {
        _callback = (door, status);
        try { read(); }
        finally { _callback = null; }
    }

    internal static object? Resolve(MapObjectReference address)
        => MapObjectDoorAddress.ParseWeakRoom(address.Key) != null || InteriorDoorIdentity.ParseKey(address.Key) != null
            ? WeakDoorObservation.ByAddress(address) : ByAddress(address);

    internal static bool IsCurrentAddress(object instance, MapObjectReference address)
        => instance switch
        {
            LG_SecurityDoor security => IsCurrentAddress(security, address),
            LG_WeakDoor weak => !weak.WasCollected && WeakDoorObservation.ByAddress(address)?.Pointer == weak.Pointer,
            _ => false
        };

    /// <summary>The door a lock callback was carried for, or null when the lock holds no door yet.</summary>
    internal static LG_SecurityDoor? Door(LG_SecurityDoor_Locks locks)
    {
        if (locks == null || locks.WasCollected) return null;
        var door = locks.m_door;
        return door == null || door.WasCollected ? null : door;
    }

    /// <summary>The one decision about whether a door can be addressed, and the reason when it cannot. The
    /// address and the reason come from one reading, so the cause a source reports is the cause that actually
    /// refused the door instead of a second, drifting guess. A door the level generation did not make a zone's
    /// entrance — a weak door, a node door, a decorative door — has no zone whose entrance it is. A bulkhead
    /// uses the same target-zone identity as every other entrance.</summary>
    internal static MapObjectAddressDecision Decide(LG_SecurityDoor door)
    {
        if (door == null || door.WasCollected) return MapObjectAddressDecision.Refused(MapObjectRefusal.Unreadable);
        var zone = ZoneIndex.Current(MapHost.Session?.WorldEpoch).ZoneOfDoor(door);
        if (zone == null) return MapObjectAddressDecision.Refused(MapObjectRefusal.NotAnEntrance);
        if (ZoneIndex.Coordinates(zone) is not { } coordinates) return MapObjectAddressDecision.Refused(MapObjectRefusal.NoCoordinates);
        var address = MapObjectDoorAddress.Create(coordinates.Dimension, coordinates.Layer, coordinates.Zone);
        return address == null ? MapObjectAddressDecision.Refused(MapObjectRefusal.NoCoordinates)
            : MapObjectAddressDecision.Addressed(address);
    }

    /// <summary>The door an address names: the zone its own coordinates identify, and that zone's entrance
    /// gate. Reads only the grammar the address is written in — its category and the fixed `security` key — so
    /// a foreign address or a key that is not this category's is refused.</summary>
    internal static LG_SecurityDoor? ByAddress(MapObjectReference address)
    {
        if (address.Category != MapObjectDoorAddress.Category || address.Key != MapObjectDoorAddress.Security)
            return null;
        var index = ZoneIndex.Current(MapHost.Session?.WorldEpoch);
        if (!int.TryParse(address.Dimension, NumberStyles.None, CultureInfo.InvariantCulture, out int dimension)
            || !int.TryParse(address.Layer, NumberStyles.None, CultureInfo.InvariantCulture, out int layer)
            || !int.TryParse(address.Zone, NumberStyles.None, CultureInfo.InvariantCulture, out int zone))
            return null;
        var door = index.DoorAt(new ZoneCoordinates(dimension, layer, zone));
        return door != null && Decide(door).Address == address ? door : null;
    }

    /// <summary>Whether the door still reads as the address it was resolved through. A door whose entrance
    /// zone changed under the same reference is no longer that door.</summary>
    internal static bool IsCurrentAddress(LG_SecurityDoor door, MapObjectReference address)
    {
        var now = Decide(door).Address;
        return now != null && now == address;
    }

    /// <summary>The door's own world position, or null when it does not read. A snapshot carries three finite
    /// coordinates, so this provider publishes the door's real position or no observation at all.</summary>
    internal static double[]? Position(LG_SecurityDoor door)
        => door == null || door.WasCollected ? null : DoorTerminalDerivations.Position(door.transform);

    internal static MapObjectObservation? Read(LG_SecurityDoor door)
    {
        if (door == null || door.WasCollected) return null;
        var before = Decide(door).Address;
        if (before == null) return null;
        int status = _callback is { } callback && ReferenceEquals(callback.Door, door) ? (int)callback.Status : (int)door.LastStatus;
        string state = MapObjectDoorStatus.Name(status);
        // Both facts are read from the members that own them: the status says whether a lock holds the door,
        // the lock component says which key it currently wants. The lock slot is the component the door was
        // set up with; an interop cast is how the interface slot becomes the concrete component.
        string key = "";
        bool locked = MapObjectDoorStatus.IsLocked(status);
        var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        if (locks != null && !locks.WasCollected)
        {
            locked |= locks.m_lockedWithNoKey;
            var keyItem = locks.m_gateKeyItemNeeded;
            if (keyItem != null && !keyItem.WasCollected)
            {
                key = keyItem.PublicName;
                if (string.IsNullOrEmpty(key)) key = keyItem.DataBlockID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                locked = true;
            }
        }
        // Native reads can trigger teardown or replacement; a changed instance never yields an observation.
        if (door.WasCollected || Decide(door).Address != before) return null;
        return new MapObjectObservation(true, new MapObjectDoorSnapshot(state, status,
            MapObjectDoorStatus.Phase(status), locked, key), null);
    }
}
