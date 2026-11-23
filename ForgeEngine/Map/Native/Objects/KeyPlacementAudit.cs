using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// Resolves, once the level finished generating, which of its keys stand nowhere. The game's own key distribution
/// builds every key at the world origin with no parent and only afterwards places it into a container's keycard slot;
/// a key that never reached a container keeps the origin, which is the drop area. Such a key is a card floating at the
/// centre of the elevator landing, and no error says so, so this audit names each one.
///
/// A registered bulkhead key generated in a zone with an explicit authored task-container pool first gets one final
/// native keycard placement through that pool. This closes the case where the original distribution never reaches
/// `PlaceKeyCard`; every other key remains read-only.
///
/// A key is audited when the level lists it: every bulkhead key in `ProgressionObjective_BulkheadKey`'s registry and
/// every key a zone's entrance gate asks for through `m_gateKeyItemNeeded`. The two sets overlap only where the game
/// registers a key twice, and a key is reported once by its native identity. A key is unplaced when its pickup's
/// transform has no parent: a pickup in a container is a child of the container's keycard slot, a pickup the Forge
/// placement moved is a child of the level's placement root or of a course node, and every one of those has a parent.
/// Apart from that explicit task-container completion, the audit reads the native frame and writes nothing to it.
/// </summary>
internal static class KeyPlacementAudit
{
    private static long _auditedEpoch;

    /// <summary>A host migration keeps the level: the audit already made stands under the new world number.</summary>
    internal static void FollowWorld(long from, long to)
    {
        if (_auditedEpoch == from) _auditedEpoch = to;
    }

    /// <summary>Audits this world's keys once; a second call for the same world reports nothing, because the audit
    /// belongs to the generation stage and a later call would only repeat its answer.</summary>
    internal static int Run(long epoch)
    {
        if (epoch == 0 || epoch == _auditedEpoch) return 0;
        _auditedEpoch = epoch;
        var zones = ZoneIndex.Current(epoch).Zones();
        var registered = ProgressionObjective_BulkheadKey.m_allBulkheadKeysInLevel;
        // FactoryDoneHooks calls the session's Place first. Its world/level receipt and the authored task
        // containers therefore exist before this completion path can select a slot.
        if (registered != null && MapHost.Session?.Placements is { } placements)
            for (var index = 0; index != registered.Count; index++)
                if (registered[index] is { } key) placements.TryPlaceUnplacedBulkheadKey(epoch, key);
        var reported = new HashSet<System.IntPtr>();
        var unplaced = 0;
        foreach (var zone in zones)
        {
            if (ZoneIndex.Entrance(zone) is not { } door || door.WasCollected) continue;
            var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
            var key = locks == null || locks.WasCollected ? null : locks.m_gateKeyItemNeeded;
            if (key != null && Report("door", key, zone, reported)) unplaced++;
        }
        if (registered != null)
            for (var index = 0; index != registered.Count; index++)
                if (registered[index] is { } key && Report("bulkhead", key, key.SpawnZone, reported)) unplaced++;
        return unplaced;
    }

    private static bool Report(string kind, GateKeyItem key, LG_Zone? zone, HashSet<System.IntPtr> reported)
    {
        if (key.WasCollected) return false;
        var pickup = key.keyPickupCore;
        if (pickup == null || pickup.WasCollected) return false;
        var transform = pickup.transform;
        if (transform == null || transform.WasCollected) return false;
        if (transform.parent != null && !transform.parent.WasCollected) return false;
        if (!reported.Add(key.Pointer)) return false;
        var coordinates = zone == null || zone.WasCollected ? null : ZoneIndex.Coordinates(zone);
        var where = coordinates is { } found
            ? found.Dimension.ToString(CultureInfo.InvariantCulture) + "/" + found.Layer.ToString(CultureInfo.InvariantCulture)
                + "/" + found.Zone.ToString(CultureInfo.InvariantCulture)
            : "<none>";
        var position = FixedPlacementSession.Show(transform.position);
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.KeyUnplaced,
            "kind=" + kind + " zone=" + where + " block=" + key.DataBlockID.ToString(CultureInfo.InvariantCulture)
            + " pos=" + position + ": the level generated this key but it stands in no container and has no parent, "
            + "so it keeps the position the generator built it at",
            RuntimeLogValue.Of("kind", kind), RuntimeLogValue.Quote("zone", where),
            RuntimeLogValue.Of("block", (long)key.DataBlockID), RuntimeLogValue.Quote("pos", position));
        return true;
    }
}
