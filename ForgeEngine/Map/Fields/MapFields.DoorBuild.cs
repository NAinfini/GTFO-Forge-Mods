using System.Collections.Generic;
using System.Linq;

namespace ForgeMap.Fields;

/// <summary>
/// Group E1, door build: the `forge.` door blocks of the map editor. Three fields are written into the game at
/// level generation by `Native/Fields/DoorBuildApplier.cs`:
///
/// - `forge.vanilla.door.gate` `ForceSecurityFlip` is written on the entrance gate before `LG_BuildGateJob.Build`.
///   `ForceOpenFromStart` initializes the security-door state at `LG_Factory.FactoryDone`, after native
///   lock/traversal setup; the native gate flag is ignored for zone-source gates. Target: the entrance door.
/// - `forge.vanilla.door.weaklock` `eWeakLockType` — the lock type `LG_WeakDoor.SetupWeakLock` is handed. A weak
///   door's own key is a per-run serial, so the target is the zone the weak doors stand in: every weak door of it.
/// </summary>
internal static partial class MapFieldSchema
{
    internal const string GateBlock = "forge.vanilla.door.gate";
    internal const string ForceOpenFromStart = "ForceOpenFromStart";
    internal const string ForceSecurityFlip = "ForceSecurityFlip";
    internal const string WeakLockBlock = "forge.vanilla.door.weaklock";
    internal const string WeakLockType = "eWeakLockType";

    private static MapFieldGroup DoorBuild() => new("door-build", new[]
    {
        new MapFieldBlock(GateBlock, MapFieldTargetKind.Door, new[]
        {
            new MapFieldPath(ForceOpenFromStart, MapFieldKind.Boolean),
            new MapFieldPath(ForceSecurityFlip, MapFieldKind.Boolean)
        }, null),
        new MapFieldBlock(WeakLockBlock, MapFieldTargetKind.Zone, new[]
        {
            new MapFieldPath(WeakLockType, MapFieldKind.NumericEnum, new[] { "0 None", "1 Melee", "2 Hackable" })
        }, null)
    });
}

/// <summary>The two gate values one zone's entrance gate is built with; a null value leaves the game's own.</summary>
internal readonly record struct DoorGateValues(bool? ForceOpenFromStart, bool? ForceSecurityFlip);

/// <summary>What the door-build applier writes in one level: the entrance gate values by the zone the gate leads
/// into, and the weak-lock type (the game's own `eWeakLockType` value) by the zone the weak doors stand in.</summary>
internal sealed record DoorBuildLevel(IReadOnlyDictionary<MapZoneKey, DoorGateValues> Gates,
    IReadOnlyDictionary<MapZoneKey, byte> WeakLocks);

/// <summary>The door-build values of every level the documents name, built once when the documents load. The
/// applier asks it for the level being generated; everything is keyed by value, so generation does no string
/// work beyond reading the level identity.</summary>
internal sealed class DoorBuildPlan
{
    private readonly Dictionary<MapLevelReference, DoorBuildLevel> _levels;

    private DoorBuildPlan(Dictionary<MapLevelReference, DoorBuildLevel> levels) => _levels = levels;

    internal bool IsEmpty => _levels.Count == 0;

    internal DoorBuildLevel? For(MapLevelReference? level)
        => level is { } key && _levels.TryGetValue(key, out var values) ? values : null;

    /// <summary>Joins already validated, disjoint owning levels; no document values are interpreted again.</summary>
    internal static DoorBuildPlan CombinePrepared(IEnumerable<DoorBuildPlan> parts)
        => new(parts.SelectMany(part => part._levels).ToDictionary(pair => pair.Key, pair => pair.Value));

    internal static DoorBuildPlan From(IEnumerable<MapFieldEntry> entries)
    {
        var gates = new Dictionary<MapLevelReference, Dictionary<MapZoneKey, DoorGateValues>>();
        var locks = new Dictionary<MapLevelReference, Dictionary<MapZoneKey, byte>>();
        foreach (var entry in entries)
        {
            if (entry.Block.Block == MapFieldSchema.GateBlock)
            {
                var zones = Level(gates, entry.Target.Level);
                zones.TryGetValue(entry.Target.ZoneKey, out var values);
                if (entry.Fields.TryGetValue(MapFieldSchema.ForceOpenFromStart, out var open)) values = values with { ForceOpenFromStart = open.Boolean };
                if (entry.Fields.TryGetValue(MapFieldSchema.ForceSecurityFlip, out var flip)) values = values with { ForceSecurityFlip = flip.Boolean };
                zones[entry.Target.ZoneKey] = values;
            }
            else if (entry.Block.Block == MapFieldSchema.WeakLockBlock
                && entry.Fields.TryGetValue(MapFieldSchema.WeakLockType, out var type))
                Level(locks, entry.Target.Level)[entry.Target.ZoneKey] = checked((byte)type.Integer);
        }
        var levels = new Dictionary<MapLevelReference, DoorBuildLevel>();
        foreach (var level in gates.Keys.Union(locks.Keys))
            levels[level] = new DoorBuildLevel(
                gates.TryGetValue(level, out var g) ? g : new Dictionary<MapZoneKey, DoorGateValues>(),
                locks.TryGetValue(level, out var l) ? l : new Dictionary<MapZoneKey, byte>());
        return new DoorBuildPlan(levels);
    }

    private static Dictionary<MapZoneKey, T> Level<T>(Dictionary<MapLevelReference, Dictionary<MapZoneKey, T>> table,
        MapLevelReference level)
    {
        if (!table.TryGetValue(level, out var zones)) table[level] = zones = new Dictionary<MapZoneKey, T>();
        return zones;
    }
}
