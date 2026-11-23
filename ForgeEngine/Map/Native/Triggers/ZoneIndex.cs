using System;
using System.Collections.Generic;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Why a native instance carries no address this provider can name. One cause per refusal: a source
/// reports the cause, and these are the whole set of ways an address is refused.</summary>
internal enum MapObjectRefusal
{
    /// <summary>The instance, or a member the address is read from, did not read at all.</summary>
    Unreadable,
    /// <summary>The door is not the entrance gate of any zone in this level.</summary>
    NotAnEntrance,
    /// <summary>The door's entrance zone has no readable dimension, layer or local index.</summary>
    NoCoordinates,
    /// <summary>No zone of this level reports the terminal as one of its own placements.</summary>
    NoOwningZone,
    /// <summary>The zone does not list the terminal among the terminals it spawned.</summary>
    NotListedByZone,
    /// <summary>The zone declares specific terminal spawns, so its own terminal list is not its whole order.</summary>
    SpecificTerminalSpawns
}

/// <summary>Either the address of a native instance or the one reason it has none. A refusal carries no
/// address, so a source cannot report one cause while publishing another.</summary>
internal readonly record struct MapObjectAddressDecision(MapObjectReference? Address, MapObjectRefusal? Refusal)
{
    internal static MapObjectAddressDecision Addressed(MapObjectReference address) => new(address, null);
    internal static MapObjectAddressDecision Refused(MapObjectRefusal refusal) => new(null, refusal);
}

/// <summary>
/// The one place the native level is turned into the coordinates a map-object address is written from. A zone
/// exposes its own three coordinates, so the whole table is rebuilt from the level's own dimensions, layers and
/// zones rather than assembled per object: a door carries no zone of its own, and a terminal's zone is read
/// through its spawn node. Enumerating the dimension lists rather than the floor's flat zone list is what makes
/// an address in a dimension the flat list may not carry resolve at all.
///
/// The table is keyed by the world epoch and discarded when generation finishes: early callbacks can see
/// a partially populated world. Everything it holds is a native instance of one world. There is exactly one table,
/// because every reader that needs a coordinate asks this type instead of walking the native graph its own way.
/// </summary>
internal sealed class ZoneIndex
{
    /// <summary>The world this table belongs to, or null when the table holds no level.</summary>
    private readonly long? _epoch;
    // Interop proxies do not override CLR Equals(object). Re-reading a native property can
    // create a different managed wrapper for the same instance; key this world's native identity.
    private readonly Dictionary<IntPtr, ZoneCoordinates> _byZone;
    /// <summary>Coordinates exactly one zone in this level owns. Two zones that answer to one address would
    /// make that address ambiguous, so neither of them is reachable through it.</summary>
    private readonly Dictionary<ZoneCoordinates, LG_Zone> _zoneAt;
    private readonly Dictionary<IntPtr, LG_Zone> _doorOwners;
    private readonly Dictionary<IntPtr, LG_Zone> _terminalOwners;
    /// <summary>The entrance gates more than one zone claims. A door that is two zones' entrance names no
    /// zone, so it is not a door this provider can address.</summary>
    private readonly HashSet<IntPtr> _contestedEntrances;
    /// <summary>Every zone of the level in its own dimension, layer and floor order, which is the order this
    /// provider enumerates its `zone` resources in. A zone that has no readable coordinates is not in it: an
    /// unaddressable zone is not a resource this provider can name.</summary>
    private readonly List<LG_Zone> _zones;
    private readonly List<LG_Zone> _nativeZones;
    private readonly Dictionary<ZoneCoordinates, IReadOnlyList<LG_Zone>> _scopedZones = new();

    private ZoneIndex(long? epoch, Dictionary<IntPtr, ZoneCoordinates> byZone, Dictionary<ZoneCoordinates, LG_Zone> zoneAt,
        Dictionary<IntPtr, LG_Zone> doorOwners, Dictionary<IntPtr, LG_Zone> terminalOwners,
        HashSet<IntPtr> contestedEntrances,
        List<LG_Zone> zones, List<LG_Zone> nativeZones)
    {
        _epoch = epoch;
        _byZone = byZone;
        _zoneAt = zoneAt;
        _doorOwners = doorOwners;
        _terminalOwners = terminalOwners;
        _contestedEntrances = contestedEntrances;
        _zones = zones;
        _nativeZones = nativeZones;
    }

    private static ZoneIndex? Cached;

    /// <summary>Whether this table was built from a level that could be enumerated at all. Every read of a table
    /// that was not answers "no coordinates", which is the honest reading outside a level; a caller that must
    /// tell "this world has no level to read" from "this anchor is not in one of its zones" asks this instead of
    /// comparing a zone list, because both are empty lists from a level whose zones none of them read.</summary>
    internal bool HoldsLevel => _epoch != null;

    /// <summary>The current level's table, rebuilt when the world changed. A level that cannot be enumerated —
    /// the builder is between levels, or its floor does not read — yields the empty table rather than an
    /// exception: every addressable reader then answers "no coordinates", which is the honest reading outside
    /// a level. A table read with no world behind it is not remembered, because "no world" is not a world: the
    /// key would then be the same for every level a caller stands up without one, and the next world would be
    /// answered from the level that is no longer there.</summary>
    internal static ZoneIndex Current(long? epoch)
    {
        var cached = Cached;
        if (cached != null && cached._epoch == epoch && epoch != null) return cached;
        var built = Build(epoch);
        if (epoch != null) Cached = built;
        return built;
    }

    /// <summary>Drops a generation-stage snapshot at FactoryDone. World changes also invalidate the cache
    /// through its epoch key; a new world cannot answer from the old world's objects.</summary>
    internal static void Reset() => Cached = null;

    /// <summary>The zone an entrance gate belongs to, or null when this door is not a zone's own entrance
    /// gate. The key is the gate's spawned door rather than its own object, because the door is what the game
    /// hands a callback.</summary>
    internal LG_Zone? ZoneOfDoor(LG_SecurityDoor door)
        => door != null && !door.WasCollected && !_contestedEntrances.Contains(door.Pointer)
            && _doorOwners.TryGetValue(door.Pointer, out var zone) ? zone : null;

    /// <summary>The zone a terminal was spawned in, or null when the terminal is unknown to this world.</summary>
    internal LG_Zone? ZoneOfTerminal(LG_ComputerTerminal terminal)
        => terminal != null && !terminal.WasCollected && _terminalOwners.TryGetValue(terminal.Pointer, out var zone)
            ? zone : null;

    internal bool TryCoordinates(LG_Zone? zone, out ZoneCoordinates coordinates)
    {
        coordinates = default;
        if (zone == null || zone.WasCollected) return false;
        return _byZone.TryGetValue(zone.Pointer, out coordinates);
    }

    /// <summary>Every zone of this level this provider can name, in the floor's own order. The list is the `zone`
    /// resource kind's whole answer: a zone whose coordinates do not read is left out rather than named with a
    /// guessed address, because an address no reader could resolve again would be a reference to nothing.</summary>
    internal IReadOnlyList<LG_Zone> Zones() => _zones;

    /// <summary>The actual per-dimension native zone occurrences, including zones without readable address
    /// coordinates. Generation collection must not omit their area children or collapse repeated occurrences.</summary>
    internal IReadOnlyList<LG_Zone> NativeZones() => _nativeZones;

    /// <summary>Retains every matching zone in the original order, including contested coordinates.
    /// The caller still checks each native lifetime; this cache belongs to this generation snapshot.</summary>
    internal IReadOnlyList<LG_Zone> Zones(ZoneCoordinates coordinates)
    {
        if (_scopedZones.TryGetValue(coordinates, out var matched)) return matched;
        var zones = new List<LG_Zone>();
        foreach (var zone in _zones)
            if (_byZone.TryGetValue(zone.Pointer, out var current) && current == coordinates) zones.Add(zone);
        _scopedZones.Add(coordinates, zones);
        return zones;
    }

    /// <summary>The zone one coordinate triple names, or null when this level has no single zone at it. The
    /// lookup is the same table an address is resolved through, so a zone a resource names is the zone a door or a
    /// terminal of that address resolves to.</summary>
    internal LG_Zone? ZoneAt(ZoneCoordinates coordinates)
        => _zoneAt.TryGetValue(coordinates, out var zone) ? zone : null;

    /// <summary>The terminal an address names: the zone its coordinates identify and the terminal at that
    /// placement index inside it. Coordinates no single zone owns, and an index past the zone's last terminal,
    /// both answer nothing rather than a neighbouring terminal.</summary>
    internal LG_ComputerTerminal? TerminalAt(ZoneCoordinates coordinates, int placementIndex)
    {
        if (placementIndex < 0 || !_zoneAt.TryGetValue(coordinates, out var zone)) return null;
        var terminals = zone.TerminalsSpawnedInZone;
        return terminals != null && placementIndex < terminals.Count ? terminals[placementIndex] : null;
    }

    /// <summary>The zero-based position of a terminal in its own zone's terminal list, or null when the
    /// terminal is not in the list of the zone it was spawned in.</summary>
    internal int? PlacementOf(LG_ComputerTerminal terminal)
    {
        if (ZoneOfTerminal(terminal) is not { } zone) return null;
        var terminals = zone.TerminalsSpawnedInZone;
        if (terminals == null) return null;
        for (var i = 0; i != terminals.Count; i++) if (terminals[i] == terminal) return i;
        return null;
    }

    /// <summary>The terminal the game's own sync id names. A native callback carries that id and nothing else,
    /// so the id is the way in; it is never the way an address is written.</summary>
    private static ZoneIndex Build(long? epoch)
    {
        var byZone = new Dictionary<IntPtr, ZoneCoordinates>();
        var zoneAt = new Dictionary<ZoneCoordinates, LG_Zone>();
        var doorOwners = new Dictionary<IntPtr, LG_Zone>();
        var terminalOwners = new Dictionary<IntPtr, LG_Zone>();
        var contested = new HashSet<IntPtr>();
        var zones = new List<LG_Zone>();
        var nativeZones = new List<LG_Zone>();
        var builder = LG_LevelBuilder.Current;
        var floor = builder == null || builder.WasCollected ? null : builder.m_currentFloor;
        // The level's own dimension list is what the zones are enumerated from, not the floor's flat zone list:
        // a dimension that the flat list does not carry — an extra or static dimension — still owns the layers
        // and zones the level built, and an authored locator in such a dimension is answered from exactly the
        // list the dimension itself holds. The two lists are the same zones for the main dimension, so every
        // address this table already answered is answered the same way.
        var dimensions = floor == null || floor.WasCollected ? null : floor.m_dimensions;
        if (dimensions != null)
            for (var d = 0; d != dimensions.Count; d++)
            {
                var dimension = dimensions[d];
                if (dimension == null) continue;
                var layers = dimension.Layers;
                if (layers == null) continue;
                for (var l = 0; l != layers.Count; l++)
                {
                    var layer = layers[l];
                    if (layer == null || layer.WasCollected) continue;
                    var layerZones = layer.m_zones;
                    if (layerZones == null) continue;
                    for (var z = 0; z != layerZones.Count; z++) Add(layerZones[z]);
                }
            }
        return new ZoneIndex(dimensions == null ? null : epoch, byZone, zoneAt, doorOwners, terminalOwners,
            contested, zones, nativeZones);

        // One zone of the level, whether or not its own coordinates read. The terminal list is read for every
        // zone that has one: a terminal is placed by its zone, and a zone with an unusable entrance still owns
        // the terminals standing in it.
        void Add(LG_Zone zone)
        {
            if (zone == null || zone.WasCollected) return;
            nativeZones.Add(zone);
            if (Coordinates(zone) is { } coordinates)
            {
                byZone[zone.Pointer] = coordinates;
                zones.Add(zone);
                if (zoneAt.TryGetValue(coordinates, out var other)) zoneAt.Remove(coordinates);
                else zoneAt[coordinates] = zone;
                var entrance = Entrance(zone);
                if (entrance != null && !doorOwners.TryAdd(entrance.Pointer, zone)) contested.Add(entrance.Pointer);
            }
            var terminals = zone.TerminalsSpawnedInZone;
            if (terminals == null) return;
            for (var t = 0; t != terminals.Count; t++)
            {
                var terminal = terminals[t];
                if (terminal == null || terminal.WasCollected) continue;
                terminalOwners[terminal.Pointer] = zone;
            }
        }
    }

    /// <summary>The zone's own three coordinates. A zone whose layer object or dimension does not read has
    /// none, which leaves the zone's objects unaddressed instead of addressed at a guessed coordinate.</summary>
    internal static ZoneCoordinates? Coordinates(LG_Zone zone)
    {
        if (zone == null || zone.WasCollected) return null;
        var layer = zone.m_layer;
        if (layer == null || layer.WasCollected) return null;
        return new ZoneCoordinates((int)zone.m_dimensionIndex, (int)layer.m_type, (int)zone.LocalIndex);
    }

    /// <summary>How many specific terminal spawns the zone's own data block declares, or null when that block
    /// cannot be read. A zone that declares any places terminals outside its own terminal list, so the list is
    /// not the zone's whole placement order and no terminal of that zone gets an address.</summary>
    internal static int? SpecificTerminalPlacements(LG_Zone zone)
    {
        if (zone == null || zone.WasCollected) return null;
        var settings = zone.m_settings;
        if (settings == null || settings.WasCollected) return null;
        var data = settings.m_zoneData;
        if (data == null || data.WasCollected) return null;
        var specifics = data.SpecificTerminalSpawnDatas;
        return specifics == null ? 0 : specifics.Count;
    }

    /// <summary>The zone a door guards: the zone whose own source gate spawned it. A door no zone's gate
    /// spawned is no zone's entrance, and a door two zones claim names no single zone.</summary>
    internal LG_SecurityDoor? DoorAt(ZoneCoordinates coordinates)
        => _zoneAt.TryGetValue(coordinates, out var zone) ? Entrance(zone) : null;

    /// <summary>The door a zone is entered through. A zone with no entrance gate — the zone the level starts
    /// in — has none, and neither has a gate whose door the game has not spawned yet.</summary>
    internal static LG_SecurityDoor? Entrance(LG_Zone zone)
    {
        var gate = zone == null || zone.WasCollected ? null : zone.m_sourceGate;
        if (gate == null || gate.WasCollected) return null;
        var spawned = gate.SpawnedDoor;
        if (spawned == null || spawned.WasCollected) return null;
        return spawned.TryCast<LG_SecurityDoor>();
    }
}

/// <summary>One zone's address coordinates, exactly as an address writes them.</summary>
internal readonly record struct ZoneCoordinates(int Dimension, int Layer, int Zone);
