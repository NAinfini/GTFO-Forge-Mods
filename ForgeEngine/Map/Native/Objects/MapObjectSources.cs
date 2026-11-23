using ForgeCore;
using System;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>One door source: zone entrances use the zone table, authored weak connections use the existing
/// room generation receipt. Neither names a door by a random serial or substitutes another door type.</summary>
internal sealed class DoorSource : IMapObjectSource
{
    private readonly OnceReporter _once;

    internal DoorSource(Action<string> report) => _once = new OnceReporter(report);

    public string Category => MapObjectCategories.Door;

    public MapObjectReference? Parse(string text) => MapObjectDoorAddress.TryParse(text);

    public MapObjectReference? TryAddress(object instance)
    {
        if (instance is LG_WeakDoor weak) return WeakDoorObservation.Address(weak);
        if (instance is not LG_SecurityDoor door) return null;
        var decision = DoorObservation.Decide(door);
        if (decision.Address is { } address) return address;
        Report("map-object door " + Kind(decision.Refusal) + Cause(decision.Refusal));
        return null;
    }

    /// <summary>A refusal from the level's own layout (not an entrance, not one of the zone's own placements) is a fact
    /// about the level and is written at info; one whose native members did not read is a failure and stays an error
    /// (<see cref="MapReport"/> tells the two apart by these phrases).</summary>
    internal static string Kind(MapObjectRefusal? refusal)
        => refusal is MapObjectRefusal.Unreadable or MapObjectRefusal.NoCoordinates ? "cannot be addressed: " : "is not addressable: ";

    private static string Cause(MapObjectRefusal? refusal) => refusal switch
    {
        MapObjectRefusal.NoCoordinates => "the zone it guards has no readable dimension, layer or local index.",
        MapObjectRefusal.Unreadable => "it does not read.",
        _ => "it is not the entrance gate of a zone this level knows."
    };
    public MapObjectObservation? Read(object instance)
        => instance is LG_SecurityDoor door ? DoorObservation.Read(door)
            : instance is LG_WeakDoor weak && WeakDoorObservation.Address(weak) != null
                ? new MapObjectObservation(true, new MapObjectDoorSnapshot(MapObjectDoorStatus.Name((int)weak.LastStatus),
                    (int)weak.LastStatus, MapObjectDoorStatus.Phase((int)weak.LastStatus), MapObjectDoorStatus.IsLocked((int)weak.LastStatus), ""), null)
                : null;

    public double[]? Position(object instance)
        => instance is LG_SecurityDoor door ? DoorObservation.Position(door)
            : instance is LG_WeakDoor weak && WeakDoorObservation.Address(weak) != null
                ? new double[] { weak.transform.position.x, weak.transform.position.y, weak.transform.position.z } : null;

    public bool IsCurrentAddress(object instance, MapObjectReference address)
        => DoorObservation.IsCurrentAddress(instance, address);

    public void Report(string message)
    {
        _once.Report(message, message);
    }
}

/// <summary>The terminal reader. It answers only for `LG_ComputerTerminal` instances. A terminal is addressed
/// by its zone and ordinary placement index, or by its zone and native serial when it owns an uplink puzzle.
/// Each form resolves through the table that owns it. Other objective terminals remain unaddressable, with
/// their cause reported once per cause.</summary>
internal sealed class TerminalSource : IMapObjectSource
{
    private readonly OnceReporter _once;

    internal TerminalSource(Action<string> report) => _once = new OnceReporter(report);

    public string Category => MapObjectCategories.Terminal;

    public MapObjectReference? Parse(string text) => MapObjectTerminalAddress.TryParse(text);

    public MapObjectReference? TryAddress(object instance)
    {
        if (instance is not LG_ComputerTerminal terminal) return null;
        var decision = TerminalObservation.Decide(terminal);
        if (decision.Address is { } address) return address;
        Report("map-object terminal " + DoorSource.Kind(decision.Refusal) + Cause(decision.Refusal));
        return null;
    }

    /// <summary>The cause as one fixed sentence per class, so a level with several unaddressable terminals of
    /// one class reports that class once instead of once per terminal.</summary>
    private static string Cause(MapObjectRefusal? refusal) => refusal switch
    {
        MapObjectRefusal.NotListedByZone => "its zone does not list it among the terminals it spawned.",
        MapObjectRefusal.SpecificTerminalSpawns => "the zone it stands in declares specific terminal spawns, "
            + "so its own terminal list is not the zone's whole placement order.",
        MapObjectRefusal.NoCoordinates => "the zone it was spawned in has no readable dimension, layer or local index.",
        MapObjectRefusal.Unreadable => "it does not read.",
        _ => "no zone of this level reports it as one of its own terminal placements."
    };

    public MapObjectObservation? Read(object instance)
        => instance is LG_ComputerTerminal terminal ? TerminalObservation.Read(terminal) : null;

    public double[]? Position(object instance)
        => instance is LG_ComputerTerminal terminal ? TerminalObservation.Position(terminal) : null;

    public bool IsCurrentAddress(object instance, MapObjectReference address)
        => instance is LG_ComputerTerminal terminal && TerminalObservation.IsCurrentAddress(terminal, address);

    public void Report(string message)
    {
        _once.Report(message, message);
    }
}
