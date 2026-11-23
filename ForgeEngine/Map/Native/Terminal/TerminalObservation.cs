using System.Globalization;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// One terminal read through its own native members on build 20403457. The address is the coordinates of the
/// zone the terminal was spawned in plus its ordinary placement index, or its native serial for an uplink.
/// Ordinary placements use the zone's terminal list; uplinks use their spawn node and the manager's table. Every
/// state value is the terminal's own `CurrentStateName`. The terminal's own sync id is not part of the
/// address: the terminal manager hands it out while the level is being built, and it is only ever used to find
/// the terminal a native callback was carried for. `Decide` is the single reading behind both the address and
/// the reason a source reports.
/// </summary>
internal static class TerminalObservation
{
    /// <summary>The one decision about whether a terminal can be addressed, and the reason when it cannot. The
    /// address and the reason come from one reading, so a source reports the cause that actually refused the
    /// terminal. A zone that declares specific terminal spawns places terminals outside its own terminal list,
    /// so the position in that list is not the zone's whole placement order: no terminal of such a zone is
    /// addressed, because a partial order would silently rename the terminals it does not cover.</summary>
    internal static MapObjectAddressDecision Decide(LG_ComputerTerminal terminal)
    {
        if (terminal == null || terminal.WasCollected) return MapObjectAddressDecision.Refused(MapObjectRefusal.Unreadable);
        var index = ZoneIndex.Current(MapHost.Session?.WorldEpoch);
        // The uplink puzzle belongs to this terminal; its serial identifies it even when the objective places
        // it outside the ordinary terminal list. The spawn node must still belong to this world's zone table.
        if (HasProgressPuzzle(terminal))
        {
            if (!index.TryCoordinates(terminal.SpawnNode?.m_zone, out var location))
                return MapObjectAddressDecision.Refused(MapObjectRefusal.NoCoordinates);
            var uplink = MapObjectTerminalAddress.CreateUplink(location.Dimension, location.Layer, location.Zone, terminal.m_serialNumber);
            return uplink == null ? MapObjectAddressDecision.Refused(MapObjectRefusal.Unreadable)
                : MapObjectAddressDecision.Addressed(uplink);
        }
        var zone = index.ZoneOfTerminal(terminal);
        if (zone == null) return MapObjectAddressDecision.Refused(MapObjectRefusal.NoOwningZone);
        if (index.PlacementOf(terminal) is not { } placement)
            return MapObjectAddressDecision.Refused(MapObjectRefusal.NotListedByZone);
        if (ZoneIndex.SpecificTerminalPlacements(zone) is > 0)
            return MapObjectAddressDecision.Refused(MapObjectRefusal.SpecificTerminalSpawns);
        if (ZoneIndex.Coordinates(zone) is not { } coordinates)
            return MapObjectAddressDecision.Refused(MapObjectRefusal.NoCoordinates);
        var address = MapObjectTerminalAddress.Create(coordinates.Dimension, coordinates.Layer, coordinates.Zone,
            placement);
        return address == null ? MapObjectAddressDecision.Refused(MapObjectRefusal.NoCoordinates)
            : MapObjectAddressDecision.Addressed(address);
    }

    internal static MapObjectReference? Address(LG_ComputerTerminal terminal) => Decide(terminal).Address;

    /// <summary>Whether the terminal still reads as the address it was resolved through. A terminal that was
    /// spawned into another zone, or moved to another position in its zone's list, is no longer that
    /// terminal.</summary>
    internal static bool IsCurrentAddress(LG_ComputerTerminal terminal, MapObjectReference address)
    {
        var now = Address(terminal);
        return now != null && now == address;
    }

    /// <summary>The terminal's own world position, or null when it does not read. A snapshot carries three
    /// finite coordinates, so this provider publishes the terminal's real position or no observation at all.</summary>
    internal static double[]? Position(LG_ComputerTerminal terminal)
        => terminal == null || terminal.WasCollected ? null : DoorTerminalDerivations.Position(terminal.transform);

    internal static MapObjectObservation? Read(LG_ComputerTerminal terminal)
    {
        if (terminal == null || terminal.WasCollected) return null;
        var before = Address(terminal);
        if (before == null) return null;
        int state = (int)terminal.CurrentStateName;
        string name = MapObjectTerminalState.Name(state);
        bool active = MapObjectTerminalState.SessionActive(state);
        // A native read can trigger teardown or replacement; a changed instance never yields an observation.
        if (terminal.WasCollected || Address(terminal) != before) return null;
        return new MapObjectObservation(true, null, new MapObjectTerminalSnapshot(name, state, active));
    }

    /// <summary>The terminal an address names: the zone its coordinates identify and the terminal at that
    /// placement index inside it, or the unique uplink with that zone and serial in the terminal manager.</summary>
    internal static LG_ComputerTerminal? ByAddress(MapObjectReference address)
    {
        if (address.Category == MapObjectCategories.Uplink)
        {
            var manager = LG_ComputerTerminalManager.Current;
            var terminals = manager == null || manager.WasCollected ? null : manager.m_terminals;
            if (terminals == null) return null;
            LG_ComputerTerminal? found = null;
            foreach (var pair in terminals)
            {
                var terminal = pair.Value;
                if (terminal == null || terminal.WasCollected || !HasProgressPuzzle(terminal) || Address(terminal) != address) continue;
                if (found != null) return null; // An ambiguous native serial never picks the first terminal.
                found = terminal;
            }
            return found;
        }
        if (address.Category != MapObjectCategories.Terminal) return null;
        var index = ZoneIndex.Current(MapHost.Session?.WorldEpoch);
        if (!int.TryParse(address.Dimension, NumberStyles.None, CultureInfo.InvariantCulture, out int dimension)
            || !int.TryParse(address.Layer, NumberStyles.None, CultureInfo.InvariantCulture, out int layer)
            || !int.TryParse(address.Zone, NumberStyles.None, CultureInfo.InvariantCulture, out int zone)
            || !int.TryParse(address.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int placement))
            return null;
        return index.TerminalAt(new ZoneCoordinates(dimension, layer, zone), placement);
    }

    /// <summary>The terminal the game's own sync id names. The id is what a native entry point carries — the
    /// command entry is handed one and nothing else — so the native manager resolves that exact id; the id
    /// itself is never part of an author address.</summary>
    internal static LG_ComputerTerminal? BySyncId(uint syncId)
    {
        var manager = LG_ComputerTerminalManager.Current;
        if (manager == null || manager.WasCollected) return null;
        // The native entry returns a concrete terminal wrapper. Generic TryGetValue's out-value
        // marshalling invokes __Canon..ctor(IntPtr) on the shipped .NET 6 / IL2CPP combination.
        var terminal = LG_ComputerTerminalManager.GetTerminal(syncId);
        return terminal != null && !terminal.WasCollected && terminal.SyncID == syncId ? terminal : null;
    }

    private static bool HasProgressPuzzle(LG_ComputerTerminal terminal)
        => terminal.UplinkPuzzle != null || terminal.TimedSequencePuzzle != null;
}
