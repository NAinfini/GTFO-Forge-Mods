using System;
using System.Collections.Generic;
using ForgeEngine.GameBindings;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Generation receipts, keyed by the actual zone object and the author's room instance.
/// A duplicate prefab never falls back to a prefab-only search.</summary>
internal static class GeneratedRoomRegistry
{
    internal sealed record GeneratedAreaBinding(string Id, string Revision, LG_Area Area);
    internal sealed record GeneratedExitBinding(string Id, LG_Plug Plug);

    internal sealed class GeneratedConnectionReceipt
    {
        internal GeneratedConnectionReceipt(string id, RoomLocator from, RoomLocator to, LG_Plug fromPlug, LG_Plug toPlug)
        {
            Id = id;
            From = from;
            To = to;
            FromPlug = fromPlug;
            ToPlug = toPlug;
        }

        internal string Id { get; }
        internal RoomLocator From { get; }
        internal RoomLocator To { get; }
        internal LG_Plug FromPlug { get; }
        internal LG_Plug ToPlug { get; }
        internal GameObject? Instance { get; private set; }
        internal LG_Gate? Gate { get; private set; }

        internal void Complete(GameObject instance, LG_Gate? gate)
        {
            if (Instance != null && (Instance.Pointer != instance.Pointer || Gate?.Pointer != gate?.Pointer))
                throw new InvalidOperationException("room.receipt-connection-rebind: " + Id);
            Instance = instance;
            Gate = gate;
        }
    }

    private sealed class RoomReceipt
    {
        internal RoomReceipt(RoomLocator locator, LG_Geomorph room)
        {
            Locator = locator;
            Room = room;
        }

        internal RoomLocator Locator;
        internal readonly LG_Geomorph Room;
        internal long? WorldEpoch;
        internal MapLevelReference? Level;
        internal GeneratedAreaBinding[]? Areas;
        internal GeneratedExitBinding[]? Exits;
        internal GeneratedConnectionReceipt? GeneratedConnection;
    }

    private static readonly Dictionary<(IntPtr Zone, string Instance), RoomReceipt> Rooms = new();

    internal static void Clear() => Rooms.Clear();

    /// <summary>The generation run moved to another world number: the same rooms, now of that world.</summary>
    internal static void FollowWorld(long from, long to)
    {
        foreach (var receipt in Rooms.Values)
            if (receipt.WorldEpoch == from) receipt.WorldEpoch = to;
    }
    internal static void Add(LG_Zone zone, RoomLocator locator, LG_Geomorph room)
    {
        var key = (zone.Pointer, locator.InstanceId!);
        if (Rooms.TryGetValue(key, out var prior))
        {
            if (prior.Room.Pointer != room.Pointer || Find(zone, locator)?.Pointer != room.Pointer)
                throw new InvalidOperationException("room.receipt-duplicate: " + locator);
        }
        else Rooms.Add(key, new RoomReceipt(locator, room));
    }

    internal static string? InstanceIdFor(LG_Geomorph room)
    {
        string? found = null;
        foreach (var row in Rooms)
            if (row.Value.Room != null && !row.Value.Room.WasCollected && row.Value.Room.Pointer == room.Pointer)
            {
                if (found != null) throw new InvalidOperationException("room.receipt-ambiguous-receipt");
                found = row.Key.Instance;
            }
        return found;
    }

    internal static RuntimeMapRoomReceipt? Snapshot(LG_Geomorph room)
    {
        RuntimeMapRoomReceipt? found = null;
        foreach (var row in Rooms)
            if (row.Value.Room != null && !row.Value.Room.WasCollected && row.Value.Room.Pointer == room.Pointer)
            {
                if (found != null) throw new InvalidOperationException("room.receipt-ambiguous-receipt");
                var value = row.Value.Locator;
                var locator = new RuntimeMapRoomLocator(value.ZoneAuthorId, value.Dimension, value.Layer, value.LocalIndex,
                    value.RoomId, value.Revision, value.SourcePrefab);
                found = new RuntimeMapRoomReceipt(row.Value.Room, row.Key.Instance, locator);
            }
        return found;
    }

    /// <summary>Attaches the generation plan's room, Area and exit identities to one actual native room. The
    /// receipt is scoped to the exact level and Runtime world. Registration is atomic: a duplicate author identity,
    /// reused native pointer or foreign native owner rejects the whole set instead of publishing a partial table.</summary>
    internal static void BindGeneratedRoom(long worldEpoch, MapLevelReference level, LG_Zone zone,
        RoomLocator locator, LG_Geomorph room, IReadOnlyList<GeneratedAreaBinding> areas,
        IReadOnlyList<GeneratedExitBinding> exits)
    {
        if (worldEpoch <= 0) throw new InvalidOperationException("room.receipt-world-unavailable");
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(exits);
        var receipt = Receipt(zone, locator, room);
        var areaCopy = new GeneratedAreaBinding[areas.Count];
        var identities = new HashSet<(string Id, string Revision)>();
        var pointers = new HashSet<IntPtr>();
        for (var index = 0; index != areas.Count; index++)
        {
            var binding = areas[index] ?? throw new InvalidOperationException("room.receipt-area-null");
            var area = binding.Area;
            if (string.IsNullOrEmpty(binding.Id) || string.IsNullOrEmpty(binding.Revision))
                throw new InvalidOperationException("room.receipt-area-identity");
            if (!identities.Add((binding.Id, binding.Revision)))
                throw new InvalidOperationException("room.receipt-area-duplicate-author: " + binding.Id);
            if (area == null || area.WasCollected || !pointers.Add(area.Pointer))
                throw new InvalidOperationException("room.receipt-area-duplicate-native: " + binding.Id);
            if (area.m_zone == null || area.m_zone.Pointer != zone.Pointer || area.m_geomorph == null
                || area.m_geomorph.Pointer != room.Pointer || AreaReferences(zone, area) != 1)
                throw new InvalidOperationException("room.receipt-area-owner-mismatch: " + binding.Id + " "
                    + AreaOwnerDetail(zone, room, area) + " level=" + level + " worldEpoch=" + worldEpoch);
            foreach (var existing in Rooms.Values)
                if (existing.Areas != null)
                    foreach (var prior in existing.Areas)
                        if (prior.Area.Pointer == area.Pointer && (existing != receipt
                            || prior.Id != binding.Id || prior.Revision != binding.Revision))
                            throw new InvalidOperationException("room.receipt-area-duplicate-native: " + binding.Id);
            areaCopy[index] = binding;
        }
        var exitCopy = new GeneratedExitBinding[exits.Count];
        var exitIds = new HashSet<string>(StringComparer.Ordinal);
        var exitPointers = new HashSet<IntPtr>();
        for (var index = 0; index != exits.Count; index++)
        {
            var binding = exits[index] ?? throw new InvalidOperationException("room.receipt-exit-null");
            var plug = binding.Plug;
            if (string.IsNullOrEmpty(binding.Id) || !exitIds.Add(binding.Id))
                throw new InvalidOperationException("room.receipt-exit-duplicate-author: " + binding.Id);
            var owner = plug?.OriginalParentArea;
            if (plug == null || plug.WasCollected || !exitPointers.Add(plug.Pointer) || !OwnsPlug(room, plug))
                throw new InvalidOperationException("room.receipt-exit-duplicate-native: " + binding.Id);
            if (owner == null || owner.WasCollected || owner.m_geomorph == null || owner.m_geomorph.Pointer != room.Pointer)
                throw new InvalidOperationException("room.receipt-exit-owner-mismatch: " + binding.Id);
            foreach (var existing in Rooms.Values)
                if (existing.Exits != null)
                    foreach (var prior in existing.Exits)
                        if (prior.Plug.Pointer == plug.Pointer && (existing != receipt || prior.Id != binding.Id))
                            throw new InvalidOperationException("room.receipt-exit-duplicate-native: " + binding.Id);
            exitCopy[index] = binding;
        }
        if (receipt.Areas != null || receipt.Exits != null || receipt.WorldEpoch != null || receipt.Level != null)
        {
            if (receipt.WorldEpoch != worldEpoch || receipt.Level != level || receipt.Areas == null
                || receipt.Exits == null || !SameAreas(receipt.Areas, areaCopy) || !SameExits(receipt.Exits, exitCopy))
                throw new InvalidOperationException("room.receipt-generation-rebind: " + locator);
            return;
        }
        receipt.WorldEpoch = worldEpoch;
        receipt.Level = level;
        receipt.Areas = areaCopy;
        receipt.Exits = exitCopy;
    }

    internal static int AreaReferences(LG_Zone zone, LG_Area area)
    {
        int count = 0;
        if (zone.m_areas != null)
            foreach (var candidate in zone.m_areas) if (candidate?.Pointer == area.Pointer) count++;
        return count;
    }

    internal static string AreaOwnerDetail(LG_Zone zone, LG_Geomorph room, LG_Area area)
        => "expectedZone=" + ZoneIdentity(zone) + " actualZone=" + ZoneIdentity(area.m_zone)
            + " nativeArea=0x" + area.Pointer.ToInt64().ToString("x")
            + " expectedRoom=0x" + room.Pointer.ToInt64().ToString("x")
            + " actualRoom=" + (area.m_geomorph == null ? "null" : "0x" + area.m_geomorph.Pointer.ToInt64().ToString("x"))
            + " zoneReferences=" + AreaReferences(zone, area);

    private static string ZoneIdentity(LG_Zone? zone)
        => zone == null ? "null" : zone.WasCollected ? "collected@0x" + zone.Pointer.ToInt64().ToString("x")
            : "{pointer=0x" + zone.Pointer.ToInt64().ToString("x") + ",dimension=" + (int)zone.DimensionIndex
                + ",layer=" + (zone.m_layer == null ? "null" : ((int)zone.m_layer.m_type).ToString()) + ",localIndex=" + (int)zone.LocalIndex + "}";

    /// <summary>Returns one exact authored Area receipt. Failure names the missing or ambiguous identity; display
    /// letters, Area names and native traversal order are never substitutes.</summary>
    internal static LG_Area? FindArea(long worldEpoch, MapLevelReference level, LG_Zone zone, RoomLocator locator,
        string id, string revision, out string? refusal)
    {
        refusal = null;
        if (locator.InstanceId == null || !Rooms.TryGetValue((zone.Pointer, locator.InstanceId), out var receipt)
            || !SameLocator(receipt.Locator, locator))
        { refusal = "authored-area-room-unbound"; return null; }
        return AreaOf(receipt, worldEpoch, level, zone, id, revision, out refusal);
    }

    /// <summary>Resolves the selector spelling exported by wave anchors. The room instance and Area id are looked
    /// up only inside the exact level/world receipt; the locked revisions stay on that receipt and are never
    /// guessed from a display name.</summary>
    internal static LG_Area? FindArea(long worldEpoch, MapLevelReference level, LG_Zone zone,
        string roomInstance, string areaId, out RoomLocator? locator, out string? refusal)
    {
        locator = null;
        refusal = null;
        if (!Rooms.TryGetValue((zone.Pointer, roomInstance), out var receipt))
        { refusal = "authored-area-room-unbound"; return null; }
        if (AreaOf(receipt, worldEpoch, level, zone, areaId, null, out refusal) is not { } found) return null;
        locator = receipt.Locator;
        return found;
    }

    /// <summary>The one Area of <paramref name="receipt"/> bound as <paramref name="id"/> (at <paramref name="revision"/>
    /// when one is asked for), checked to be of this world and still that room's Area of <paramref name="zone"/>.</summary>
    private static LG_Area? AreaOf(RoomReceipt receipt, long worldEpoch, MapLevelReference level, LG_Zone zone, string id,
        string? revision, out string? refusal)
    {
        refusal = null;
        if (receipt.Areas == null) { refusal = "authored-area-unbound"; return null; }
        if (!Current(receipt, worldEpoch, level)) { refusal = "authored-area-stale-world"; return null; }
        LG_Area? found = null;
        foreach (var binding in receipt.Areas)
        {
            if (binding.Id != id || revision != null && binding.Revision != revision) continue;
            if (found != null) { refusal = "authored-area-ambiguous"; return null; }
            found = binding.Area;
        }
        if (found == null) { refusal = "authored-area-missing"; return null; }
        if (found.WasCollected || found.m_zone == null || found.m_zone.Pointer != zone.Pointer
            || found.m_geomorph == null || found.m_geomorph.Pointer != receipt.Room.Pointer)
        { refusal = "authored-area-stale"; return null; }
        return found;
    }

    internal static LG_Plug? FindExit(long worldEpoch, MapLevelReference level, LG_Zone zone, RoomLocator locator,
        string id, out string? refusal)
    {
        refusal = null;
        if (locator.InstanceId == null || !Rooms.TryGetValue((zone.Pointer, locator.InstanceId), out var receipt)
            || !SameLocator(receipt.Locator, locator))
        { refusal = "authored-exit-room-unbound"; return null; }
        if (receipt.Exits == null) { refusal = "authored-exit-unbound"; return null; }
        if (!Current(receipt, worldEpoch, level)) { refusal = "authored-exit-stale-world"; return null; }
        LG_Plug? found = null;
        foreach (var binding in receipt.Exits)
        {
            if (binding.Id != id) continue;
            if (found != null) { refusal = "authored-exit-ambiguous"; return null; }
            found = binding.Plug;
        }
        if (found == null) { refusal = "authored-exit-missing"; return null; }
        if (found.WasCollected || !OwnsPlug(receipt.Room, found))
        { refusal = "authored-exit-stale"; return null; }
        return found;
    }

    /// <summary>Registers the exact paired plugs of one generated author connection on its target-room receipt.
    /// The connector object and gate are filled later by <see cref="CompleteConnection"/> after native build.</summary>
    internal static void BindConnection(LG_Zone targetZone, string id,
        RoomLocator from, LG_Geomorph fromRoom, LG_Plug fromPlug,
        RoomLocator to, LG_Geomorph toRoom, LG_Plug toPlug)
    {
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("room.receipt-connection-identity");
        var target = Receipt(targetZone, to, toRoom);
        var source = Receipt(from, fromRoom);
        if (target.WorldEpoch == null || target.Level == null || !SameScope(source, target))
            throw new InvalidOperationException("room.receipt-connection-scope-mismatch: " + id);
        if (fromPlug == null || fromPlug.WasCollected || toPlug == null || toPlug.WasCollected
            || fromPlug.m_pariedWith == null || fromPlug.m_pariedWith.Pointer != toPlug.Pointer
            || toPlug.m_pariedWith == null || toPlug.m_pariedWith.Pointer != fromPlug.Pointer
            || !OwnsPlug(fromRoom, fromPlug) || !OwnsPlug(toRoom, toPlug))
            throw new InvalidOperationException("room.receipt-connection-plug-mismatch: " + id);
        foreach (var receipt in Rooms.Values)
            if (receipt.GeneratedConnection is { } existing)
            {
                if (existing.Id == id && receipt != target)
                    throw new InvalidOperationException("room.receipt-connection-duplicate-author: " + id);
                if ((existing.FromPlug.Pointer == fromPlug.Pointer || existing.FromPlug.Pointer == toPlug.Pointer
                    || existing.ToPlug.Pointer == fromPlug.Pointer || existing.ToPlug.Pointer == toPlug.Pointer)
                    && receipt != target)
                    throw new InvalidOperationException("room.receipt-connection-duplicate-native: " + id);
            }
        if (target.GeneratedConnection != null)
        {
            var prior = target.GeneratedConnection;
            if (prior.Id != id || !SameLocator(prior.From, from) || !SameLocator(prior.To, to)
                || prior.FromPlug.Pointer != fromPlug.Pointer || prior.ToPlug.Pointer != toPlug.Pointer)
                throw new InvalidOperationException("room.receipt-connection-rebind: " + id);
            return;
        }
        target.GeneratedConnection = new GeneratedConnectionReceipt(id, from, to, fromPlug, toPlug);
    }

    internal static void CompleteConnection(LG_Zone targetZone, RoomLocator targetRoom, string id,
        GameObject instance, LG_Gate? gate)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var receipt = Receipt(targetZone, targetRoom, Find(targetZone, targetRoom)
            ?? throw new InvalidOperationException("room.receipt-connection-room-unbound: " + id));
        var connection = receipt.GeneratedConnection;
        if (connection == null || connection.Id != id)
            throw new InvalidOperationException("room.receipt-connection-unbound: " + id);
        if (instance.WasCollected || gate != null && gate.WasCollected)
            throw new InvalidOperationException("room.receipt-connection-stale: " + id);
        if (gate != null && connection.FromPlug.m_gate?.Pointer != gate.Pointer
            && connection.ToPlug.m_gate?.Pointer != gate.Pointer)
            throw new InvalidOperationException("room.receipt-connection-gate-mismatch: " + id);
        connection.Complete(instance, gate);
    }

    internal static GeneratedConnectionReceipt? GeneratedConnection(long worldEpoch, MapLevelReference level,
        LG_Zone zone, string targetInstance)
        => Rooms.TryGetValue((zone.Pointer, targetInstance), out var receipt) && Current(receipt, worldEpoch, level)
            ? Live(receipt.GeneratedConnection) : null;

    internal static GeneratedConnectionReceipt? GeneratedConnection(long worldEpoch, MapLevelReference level,
        string id, out string? refusal)
    {
        refusal = null;
        GeneratedConnectionReceipt? found = null;
        var stale = false;
        foreach (var receipt in Rooms.Values)
        {
            var connection = receipt.GeneratedConnection;
            if (connection == null || connection.Id != id) continue;
            if (!Current(receipt, worldEpoch, level)) { stale = true; continue; }
            connection = Live(connection);
            if (connection == null) { refusal = "authored-connection-stale"; return null; }
            if (found != null) { refusal = "authored-connection-ambiguous"; return null; }
            found = connection;
        }
        if (found != null) return found;
        refusal = stale ? "authored-connection-stale-world" : "authored-connection-missing";
        return null;
    }

    internal static IEnumerable<GeneratedConnectionReceipt> GeneratedConnections(long worldEpoch,
        MapLevelReference level, LG_Zone zone)
    {
        foreach (var row in Rooms)
            if (row.Key.Zone == zone.Pointer && Current(row.Value, worldEpoch, level)
                && Live(row.Value.GeneratedConnection) is { } connection)
                yield return connection;
    }

    internal static LG_Geomorph? Find(LG_Zone zone, RoomLocator locator)
    {
        if (locator.InstanceId == null || !Rooms.TryGetValue((zone.Pointer, locator.InstanceId), out var receipt)) return null;
        var saved = receipt.Locator;
        if (saved.ZoneAuthorId != locator.ZoneAuthorId || saved.Dimension != locator.Dimension || saved.Layer != locator.Layer
            || saved.LocalIndex != locator.LocalIndex || saved.RoomId != locator.RoomId || saved.Revision != locator.Revision
            || saved.SourcePrefab != locator.SourcePrefab) return null;
        return receipt.Room == null || receipt.Room.WasCollected ? null : receipt.Room;
    }

    internal static LG_Geomorph? Find(long worldEpoch, MapLevelReference? level, LG_Zone zone, RoomLocator locator)
    {
        if (locator.InstanceId == null || !Rooms.TryGetValue((zone.Pointer, locator.InstanceId), out var receipt)
            || !SameLocator(receipt.Locator, locator)) return null;
        if (receipt.WorldEpoch != null && (level == null || !Current(receipt, worldEpoch, level.Value))) return null;
        return receipt.Room == null || receipt.Room.WasCollected ? null : receipt.Room;
    }
    internal static RoomLocator? LocatorFor(LG_Geomorph room)
    {
        RoomLocator? found = null;
        foreach (var row in Rooms.Values)
            if (row.Room != null && !row.Room.WasCollected && row.Room.Pointer == room.Pointer)
            {
                if (found != null) throw new InvalidOperationException("room.receipt-ambiguous-receipt");
                found = row.Locator;
            }
        return found;
    }

    private static RoomReceipt Receipt(LG_Zone zone, RoomLocator locator, LG_Geomorph room)
    {
        if (locator.InstanceId == null || !Rooms.TryGetValue((zone.Pointer, locator.InstanceId), out var receipt)
            || receipt.Room.Pointer != room.Pointer || !SameLocator(receipt.Locator, locator))
            throw new InvalidOperationException("room.receipt-identity-mismatch: " + locator);
        return receipt;
    }

    private static RoomReceipt Receipt(RoomLocator locator, LG_Geomorph room)
    {
        RoomReceipt? found = null;
        foreach (var receipt in Rooms.Values)
            if (receipt.Room.Pointer == room.Pointer && SameLocator(receipt.Locator, locator))
            {
                if (found != null) throw new InvalidOperationException("room.receipt-ambiguous-receipt");
                found = receipt;
            }
        return found ?? throw new InvalidOperationException("room.receipt-identity-mismatch: " + locator);
    }

    private static bool SameLocator(RoomLocator? left, RoomLocator right)
        => left != null && left.ZoneAuthorId == right.ZoneAuthorId && left.Dimension == right.Dimension
            && left.Layer == right.Layer && left.LocalIndex == right.LocalIndex && left.RoomId == right.RoomId
            && left.Revision == right.Revision && left.SourcePrefab == right.SourcePrefab
            && left.InstanceId == right.InstanceId;

    private static bool Current(RoomReceipt receipt, long worldEpoch, MapLevelReference level)
        => receipt.WorldEpoch == worldEpoch && receipt.Level == level && receipt.Room != null && !receipt.Room.WasCollected;

    private static bool SameScope(RoomReceipt left, RoomReceipt right)
        => left.WorldEpoch != null && left.WorldEpoch == right.WorldEpoch && left.Level != null && left.Level == right.Level;

    private static bool OwnsPlug(LG_Geomorph room, LG_Plug plug)
    {
        foreach (var candidate in room.m_plugs)
            if (candidate != null && !candidate.WasCollected && candidate.Pointer == plug.Pointer) return true;
        return false;
    }

    private static GeneratedConnectionReceipt? Live(GeneratedConnectionReceipt? receipt)
        => receipt != null && !receipt.FromPlug.WasCollected && !receipt.ToPlug.WasCollected
            && (receipt.Instance == null || !receipt.Instance.WasCollected)
            && (receipt.Gate == null || !receipt.Gate.WasCollected
                && (receipt.FromPlug.m_gate?.Pointer == receipt.Gate.Pointer
                    || receipt.ToPlug.m_gate?.Pointer == receipt.Gate.Pointer)) ? receipt : null;

    private static bool SameAreas(IReadOnlyList<GeneratedAreaBinding> left, IReadOnlyList<GeneratedAreaBinding> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index != left.Count; index++)
            if (left[index].Id != right[index].Id || left[index].Revision != right[index].Revision
                || left[index].Area.Pointer != right[index].Area.Pointer) return false;
        return true;
    }

    private static bool SameExits(IReadOnlyList<GeneratedExitBinding> left, IReadOnlyList<GeneratedExitBinding> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index != left.Count; index++)
            if (left[index].Id != right[index].Id || left[index].Plug.Pointer != right[index].Plug.Pointer) return false;
        return true;
    }
}
