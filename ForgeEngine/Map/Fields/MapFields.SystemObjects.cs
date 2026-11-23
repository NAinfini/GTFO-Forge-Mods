using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ForgeMap.Fields;

/// <summary>
/// Groups C1 and C2, the map editor's system objects: the level sentry, a zone's item spawn rules and the elevator
/// cargo. The website writes them into `forge/map-fields/system-objects.forge`, and each is written into the game
/// through the game's own entry points (<see cref="SystemObjectPlan"/>):
///
/// - a zone's item spawn rules become extra vanilla distribution jobs of that zone
///   (`Native/Fields/ItemSpawnApplier.cs`), on every peer while the level is generated;
/// - elevator cargo preserves native generation, selectively filters this generation's queue entries and appends authored roots
///   (`Native/Fields/ElevatorCargoApplier.cs`); the complete native movement loop places them later;
/// - a level sentry is the vanilla gear spawn a deployed sentry is (`Native/Fields/LevelSentrySpawner.cs`), on the host once
///   the level is there.
///
/// The object the map editor places at a point (the sentry) is a `ZoneObject` target carrying the room locator;
/// `Position` is the prefab root's Unity-local metres in that room and `Rotation` its Euler degrees.
///
/// Three map editor objects are deliberately not here, because the game or another Forge half already owns them:
/// - the shuttlebox is the vanilla `Dumbwaiter` function: the website writes a zone's shuttleboxes straight into that
///   zone's `ExpeditionZoneData.DumbwaiterPlacements`, which the game itself reads (`LG_Distribute_DumbwaitersPerZone`),
///   and its runtime half is the `shuttlebox` map-object category;
/// - the security sensor is a trigger zone (`forge/trigger-zones.forge`): a player-only sphere judged at the vanilla
///   `LG_CollisionWorldEventTrigger` cadence, with its range drawn in the game (`TriggerZoneDisplay`);
/// - the dimension warp's carry rules are the destination dimension's own `DimensionData.ForbidCarryItemWarps` and
///   `LeaveDeployablesOnWarp`, which the website writes into that dimension's DimensionDataBlock.
/// </summary>
internal static partial class MapFieldSchema
{
    internal const string LevelSentryBlock = "forge.eosext.LevelSpawnedSentry";
    internal const string ResourceSpawnBlock = "forge.itemSpawn.ResourceSpawns";
    internal const string PreferEmptyBlock = "forge.itemSpawn.PreferEmpty";
    internal const string ForceSpawnBlock = "forge.itemSpawn.ForceSpawn";
    internal const string ElevatorBlock = "forge.advancements.Elevator";

    private static MapFieldPath Flag(string path) => new(path, MapFieldKind.Boolean);
    private static MapFieldPath Vector(string path) => new(path, MapFieldKind.Vector3);

    private static MapFieldGroup SystemObjects() => new("system-objects", new[]
    {
        new MapFieldBlock(LevelSentryBlock, MapFieldTargetKind.ZoneObject, new[]
        {
            Vector("Position"), Vector("Rotation"),
            new MapFieldPath("PlayerOfflineGearDBId", MapFieldKind.Integer),
            new MapFieldPath("InitialAmmo", MapFieldKind.Number),
            new MapFieldPath("AmmoCap", MapFieldKind.Number)
        }, null),
        new MapFieldBlock(ResourceSpawnBlock, MapFieldTargetKind.ZoneObject, new[]
        {
            new MapFieldPath("PackType", MapFieldKind.Enum, SystemObjectPlan.ResourcePackTypes.Keys.ToArray()),
            new MapFieldPath("Amount", MapFieldKind.Number)
        }, null),
        new MapFieldBlock(PreferEmptyBlock, MapFieldTargetKind.ZoneObject, new[]
        {
            new MapFieldPath("Count", MapFieldKind.Integer)
        }, null),
        new MapFieldBlock(ForceSpawnBlock, MapFieldTargetKind.ZoneObject, new[]
        {
            new MapFieldPath("ItemID", MapFieldKind.Text),
            new MapFieldPath("Count", MapFieldKind.Integer),
            new MapFieldPath("PlacementWeights", MapFieldKind.NumberList, Array.Empty<string>(), 3)
        }, null),
        new MapFieldBlock(ElevatorBlock, MapFieldTargetKind.Level, new[]
        {
            Flag("DisableElevatorCargo"), Flag("OverrideCargoItems"),
            new MapFieldPath("CargoItems", MapFieldKind.IntegerList)
        }, null)
    });
}

/// <summary>One zone's extra resource packs: <see cref="Copies"/> vanilla resource distribution jobs of
/// <see cref="PackType"/> (the game's `eResourceContainerSpawnType`), each distributing <see cref="Amount"/> times
/// the expedition balance's own per-zone amount of that pack kind.</summary>
internal sealed record ResourceSpawnRule(string Object, MapZoneKey Zone, int PackType, string PackName, double Amount, int Copies);

/// <summary>One item a zone always gets: one vanilla pickup distribution job of <see cref="Count"/> items of
/// <see cref="ItemId"/> placed with the zone placement weights start / middle / end.</summary>
internal sealed record ForceSpawnRule(string Object, MapZoneKey Zone, uint ItemId, int Count, float Start, float Middle, float End);

/// <summary>One level sentry: the room it stands in, its room-local point and Euler degrees, the offline gear block
/// it is built from and its ammo as the sentry's own relative figures.</summary>
internal sealed record LevelSentryRow(string Object, RoomLocator Room, IReadOnlyList<double> Position, IReadOnlyList<double> Rotation,
    uint GearId, float InitialAmmo, float AmmoCap);

/// <summary>The level's cargo policy: exclude this generation's native cargo entries, optionally enqueue <see cref="Items"/>
/// in authored order, and retain the native loop's slot progression and saturation.</summary>
internal sealed record ElevatorCargoRule(bool Disable, IReadOnlyList<uint> Items);

/// <summary>What one level's system objects write into the game.</summary>
internal sealed class SystemObjectLevel
{
    internal List<ResourceSpawnRule> ResourceSpawns { get; } = new();
    internal List<ForceSpawnRule> ForceSpawns { get; } = new();
    internal List<LevelSentryRow> Sentries { get; } = new();
    internal ElevatorCargoRule? Cargo { get; set; }

    internal bool HasItemSpawns => ResourceSpawns.Count != 0 || ForceSpawns.Count != 0;
}

/// <summary>
/// The system objects of every level the documents name, built once when the documents load. Each map editor object
/// is composed from the entries that carry its own id; an object whose fields do not compose into one thing the game
/// can build — a missing required field, an out-of-range value — is refused whole as `map.field-object` by its own
/// id and never written with a guessed value.
/// </summary>
internal sealed class SystemObjectPlan
{
    /// <summary>A sentry's ammo figures when an entry leaves them out: the catalog's own defaults.</summary>
    internal const float DefaultInitialAmmo = 0.5f, DefaultAmmoCap = 1f;

    /// <summary>The four pack kinds a zone rule may add, as the game's own `eResourceContainerSpawnType` members. It
    /// lives on this class and not on the partial schema, whose static fields initialize in no defined order across
    /// its files while the schema's field table is built from this one.</summary>
    internal static readonly IReadOnlyDictionary<string, int> ResourcePackTypes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Health"] = 0,
        ["AmmoWeapon"] = 1,
        ["AmmoTool"] = 2,
        ["Disinfection"] = 9
    };

    private readonly Dictionary<MapLevelReference, SystemObjectLevel> _levels;

    private SystemObjectPlan(Dictionary<MapLevelReference, SystemObjectLevel> levels) => _levels = levels;

    internal bool IsEmpty => _levels.Count == 0;
    internal bool AnyItemSpawns => _levels.Values.Any(level => level.HasItemSpawns);
    internal bool AnyCargo => _levels.Values.Any(level => level.Cargo != null);
    internal bool AnySentries => _levels.Values.Any(level => level.Sentries.Count != 0);

    internal SystemObjectLevel? For(MapLevelReference? level)
        => level is { } key && _levels.TryGetValue(key, out var found) ? found : null;

    /// <summary>Joins already validated, disjoint owning levels; no document values are interpreted again.</summary>
    internal static SystemObjectPlan CombinePrepared(IEnumerable<SystemObjectPlan> parts)
        => new(parts.SelectMany(part => part._levels).ToDictionary(pair => pair.Key, pair => pair.Value));

    internal static SystemObjectPlan From(IEnumerable<MapFieldEntry> entries, Action<MapDiagnostic> report)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(report);
        var levels = new Dictionary<MapLevelReference, SystemObjectLevel>();
        SystemObjectLevel Level(MapLevelReference key) => levels.TryGetValue(key, out var found) ? found : levels[key] = new SystemObjectLevel();
        // One object is every entry that carries its id; the entries keep document order inside it.
        var objects = entries.Where(entry => entry.Target.Kind == MapFieldTargetKind.ZoneObject)
            .GroupBy(entry => (entry.Target.Level, entry.Target.Object));
        foreach (var group in objects)
        {
            var parts = group.ToArray();
            var fields = new Dictionary<string, (MapFieldEntry Entry, MapFieldValue Value)>(StringComparer.Ordinal);
            foreach (var entry in parts)
                foreach (var (path, value) in entry.Fields)
                    fields[entry.Block.Block + "." + path] = (entry, value);
            var first = parts[0];
            var refuse = new Action<string>(reason => report(new MapDiagnostic(MapLogCodes.FieldObject, "file=" + first.Source + " object=" + first.Target.Object
                + " level=" + first.Target.Level + ": " + reason, first.Source)));
            if (parts.Any(p => p.Block.Block == MapFieldSchema.ResourceSpawnBlock))
            {
                if (Resource(first.Target, fields, refuse) is { } rule) Level(first.Target.Level).ResourceSpawns.Add(rule);
            }
            else if (parts.Any(p => p.Block.Block == MapFieldSchema.PreferEmptyBlock))
                refuse("`" + MapFieldSchema.PreferEmptyBlock + "` belongs to a `" + MapFieldSchema.ResourceSpawnBlock + "` rule, and this object has none.");
            if (parts.Any(p => p.Block.Block == MapFieldSchema.ForceSpawnBlock)
                && Force(first.Target, fields, refuse) is { } force) Level(first.Target.Level).ForceSpawns.Add(force);
            if (parts.FirstOrDefault(p => p.Block.Block == MapFieldSchema.LevelSentryBlock) is { } sentryEntry
                && Sentry(sentryEntry, fields, refuse) is { } sentry) Level(first.Target.Level).Sentries.Add(sentry);
        }
        foreach (var entry in entries.Where(entry => entry.Block.Block == MapFieldSchema.ElevatorBlock))
        {
            var refuse = new Action<string>(reason => report(new MapDiagnostic(MapLogCodes.FieldObject, "file=" + entry.Source + " object=elevator-cargo level="
                + entry.Target.Level + ": " + reason, entry.Source)));
            if (Cargo(entry, refuse) is { } cargo) Level(entry.Target.Level).Cargo = cargo;
        }
        foreach (var key in levels.Where(pair => !pair.Value.HasItemSpawns && pair.Value.Sentries.Count == 0 && pair.Value.Cargo == null)
                     .Select(pair => pair.Key).ToArray())
            levels.Remove(key);
        return new SystemObjectPlan(levels);
    }

    private static ResourceSpawnRule? Resource(MapFieldTarget target,
        IReadOnlyDictionary<string, (MapFieldEntry Entry, MapFieldValue Value)> fields, Action<string> refuse)
    {
        if (!fields.TryGetValue(MapFieldSchema.ResourceSpawnBlock + ".PackType", out var pack)
            || !ResourcePackTypes.TryGetValue(pack.Value.Text ?? "", out int type))
        { refuse("`PackType` is required."); return null; }
        if (!fields.TryGetValue(MapFieldSchema.ResourceSpawnBlock + ".Amount", out var amount)
            || !(amount.Value.Number > 0) || amount.Value.Number > float.MaxValue)
        { refuse("`Amount` is required and must be a positive finite multiplier."); return null; }
        long copies = fields.TryGetValue(MapFieldSchema.PreferEmptyBlock + ".Count", out var count) ? count.Value.Integer : 1;
        if (copies < 1 || copies > int.MaxValue) { refuse("`Count` must be a positive 32-bit integer."); return null; }
        return new ResourceSpawnRule(target.Object, target.ZoneKey, type, pack.Value.Text!, amount.Value.Number, (int)copies);
    }

    private static ForceSpawnRule? Force(MapFieldTarget target,
        IReadOnlyDictionary<string, (MapFieldEntry Entry, MapFieldValue Value)> fields, Action<string> refuse)
    {
        if (!fields.TryGetValue(MapFieldSchema.ForceSpawnBlock + ".ItemID", out var item)
            || !uint.TryParse(item.Value.Text, NumberStyles.None, CultureInfo.InvariantCulture, out uint itemId) || itemId == 0)
        { refuse("`ItemID` must be a positive item block id."); return null; }
        if (!fields.TryGetValue(MapFieldSchema.ForceSpawnBlock + ".Count", out var count) || count.Value.Integer < 1 || count.Value.Integer > int.MaxValue)
        { refuse("`Count` is required and must be a positive 32-bit integer."); return null; }
        if (!fields.TryGetValue(MapFieldSchema.ForceSpawnBlock + ".PlacementWeights", out var weights)
            || weights.Value.Numbers is not { Count: 3 } w || w.Any(x => x < 0 || x > float.MaxValue) || w.Sum() <= 0)
        { refuse("`PlacementWeights` is required: three non-negative weights, not all zero."); return null; }
        return new ForceSpawnRule(target.Object, target.ZoneKey, itemId, (int)count.Value.Integer, (float)w[0], (float)w[1], (float)w[2]);
    }

    private static LevelSentryRow? Sentry(MapFieldEntry entry,
        IReadOnlyDictionary<string, (MapFieldEntry Entry, MapFieldValue Value)> fields, Action<string> refuse)
    {
        const string block = MapFieldSchema.LevelSentryBlock;
        if (entry.Room is not { } room) { refuse("A level sentry is placed at a point: `target.room` is required."); return null; }
        if (!fields.TryGetValue(block + ".Position", out var position)) { refuse("`Position` is required."); return null; }
        var rotation = fields.TryGetValue(block + ".Rotation", out var turned) ? turned.Value.Numbers! : new double[3];
        if (!fields.TryGetValue(block + ".PlayerOfflineGearDBId", out var gear) || gear.Value.Integer <= 0 || gear.Value.Integer > uint.MaxValue)
        { refuse("`PlayerOfflineGearDBId` must be a positive gear block id."); return null; }
        float initial = fields.TryGetValue(block + ".InitialAmmo", out var ammo) ? (float)ammo.Value.Number : DefaultInitialAmmo;
        float cap = fields.TryGetValue(block + ".AmmoCap", out var capped) ? (float)capped.Value.Number : DefaultAmmoCap;
        if (!float.IsFinite(initial) || !float.IsFinite(cap) || initial < 0 || cap < 0) { refuse("`InitialAmmo` and `AmmoCap` are non-negative finite multiples of the owner's class pool."); return null; }
        return new LevelSentryRow(entry.Target.Object, room, position.Value.Numbers!, rotation, (uint)gear.Value.Integer, initial, cap);
    }

    /// <summary>The level's cargo, or null when the entry leaves the vanilla cargo alone. Leaving the cargo out wins
    /// over a replacement list, because an empty cage is what the author asked for either way.</summary>
    private static ElevatorCargoRule? Cargo(MapFieldEntry entry, Action<string> refuse)
    {
        bool Flag(string path) => entry.Fields.TryGetValue(path, out var value) && value.Boolean;
        if (Flag("DisableElevatorCargo")) return new ElevatorCargoRule(true, Array.Empty<uint>());
        if (!Flag("OverrideCargoItems")) return null;
        var items = entry.Fields.TryGetValue("CargoItems", out var list) ? list.Integers ?? Array.Empty<long>() : Array.Empty<long>();
        if (items.Count == 0 || items.Any(id => id <= 0 || id > uint.MaxValue))
        { refuse("`CargoItems` must be at least one positive item block id when `OverrideCargoItems` is set."); return null; }
        return new ElevatorCargoRule(false, items.Select(id => (uint)id).ToArray());
    }
}
