using System;
using System.Collections.Generic;
using System.Globalization;
using AIGraph;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

internal sealed partial class MapPluginSession
{
    /// <summary>The level-object half, refused by name when it was never created. Every entry of the definition
    /// that answers through it reads it here rather than at composition time, because the definition is composed
    /// before the registration exists and the half is created after it.</summary>
    private LevelObjectModule LevelObjects()
        => Levels ?? throw new RuntimeContractException("level-object-unavailable", "No Map level-object half is installed.");

    /// <summary>The map-object half, refused by name when it was never created. The generator category's resource
    /// kind and value row read the tables it holds, and the definition is composed before the half exists, so
    /// every entry that answers through it reads it here rather than at composition time.</summary>
    private MapObjectModule MapObjectHalf()
        => MapObjects ?? throw new RuntimeContractException("map-object-unavailable", "No Map map-object half is installed.");

    /// <summary>The failure-rule ledger, refused by name when the session never built it: the definition is
    /// composed before the half exists, and a rule asked of a half that is not there is a refusal rather than a
    /// write nothing answers.</summary>
    private FailRuleActions FailRuleHalf()
        => FailRules ?? throw new RuntimeContractException(ResultCode.FailRuleUnavailable,
            "No Map failure-rule half is installed.");

    private SuccessScreenActions SuccessScreenHalf()
        => _successScreen ?? throw new RuntimeContractException(ResultCode.SuccessScreenUnavailable,
            "No Map success-screen half is installed.");

    /// <summary>Whether one `gtfo.map_object` reference is still one this session holds: an entrance-gate door, a
    /// terminal, or a weak door the door-terminal half recorded for this world. The namespace is answered by one
    /// predicate rather than one per category, because a plan that pins any map object asks the same question
    /// about the same namespace.</summary>
    private bool IsCurrentMapObject(EntityReference reference)
    {
        if (_disposed || reference.WorldEpoch != _kernel.WorldEpoch) return false;
        if (PortalWarpObservation.IsCurrent(reference)) return true;
        if (_doorTerminal != null && _doorTerminal.IsCurrentWeakDoor(reference)) return true;
        if (MapObjectDoorAddress.TryParse(Id(reference)) is { } door)
            return DoorObservation.ByAddress(door) is { } entrance && !entrance.WasCollected;
        if (MapObjectTerminalAddress.TryParse(Id(reference)) is { } terminal)
            return TerminalObservation.ByAddress(terminal) is { } instance && !instance.WasCollected;
        return false;
    }

    /// <summary>The address text one `gtfo.map_object` reference carries, or null when it is not a reference of
    /// that namespace at all.</summary>
    private static string? Id(EntityReference reference)
    {
        string prefix = EntityKinds.MapObject + ":";
        return reference.Id != null && reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? reference.Id[prefix.Length..] : null;
    }

    /// <summary>The one door the two door execute rows act on: an entrance gate of this level. The address is
    /// read with the category's own grammar and resolved through the same table the observation half reads, so a
    /// door action and a door fact can never disagree about which door an address names. A weak door has no
    /// entrance to open or close — its two states are the fact rows' — so it resolves to no instance here.</summary>
    private static object? DoorFor(EntityReference reference)
    {
        if (MapObjectDoorAddress.TryParse(Id(reference)) is not { } address) return null;
        if (MapObjectDoorAddress.ParseWeakSerial(address.Key) is not null) return null;
        return DoorObservation.Resolve(address);
    }

    /// <summary>One door read for the `forge.query.map.door_state` value row: the entrance door the reference
    /// names, its own last status, whether a lock holds it, the key it wants and the chained puzzle its lock
    /// holds. A reference no door of this world answers for yields no sample, which the row refuses by name
    /// rather than answering with a status the door never had.
    ///
    /// The puzzle id is its DataBlock definition id — the same id the scan resource provider publishes —
    /// so a plan can wire this output straight into `forge.action.map.scan_state`. A door
    /// whose lock holds no puzzle, or a door with no lock component, leaves the reading absent. A weak door (room
    /// or interior address) is read through the same resolver the door actions use: its own status, and whether
    /// one of its weak locks still holds; it has no key, puzzle or enemy wave.</summary>
    internal static DoorQueryContract.DoorSample? DoorStateSample(EntityReference reference)
    {
        if (MapObjectDoorAddress.TryParse(Id(reference)) is not { } address) return null;
        if (MapObjectDoorAddress.ParseWeakSerial(address.Key) is not null) return null;
        var resolved = DoorObservation.Resolve(address);
        if (resolved is LG_WeakDoor weak) return WeakDoorStateSample(weak);
        if (resolved is not LG_SecurityDoor door) return null;
        if (!DoorObservation.IsCurrentAddress(door, address)) return null;
        if (DoorObservation.Read(door)?.Door is not { } reading) return null;
        string? puzzle = null;
        var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        if (locks != null && !locks.WasCollected)
        {
            var instance = locks.ChainedPuzzleToSolve;
            puzzle = ScanInstanceResolver.ResourceId(instance);
        }
        // Whether the level set the door up as a blood door is its own reading: `ActiveEnemyWaveData` is the
        // wave data the level attached to this door, and `HasActiveEnemyWave` is the flag the game's own state
        // change reads to decide it is one. A door the level attached none to is not a blood door.
        bool bloody;
        try { bloody = door.ActiveEnemyWaveData is { } wave && wave.HasActiveEnemyWave; }
        catch (Exception containedError) { MapLog.Contained("map-plugin-session-resources.door-sample", containedError); return null; }
        return new DoorQueryContract.DoorSample(reading.Status, reading.Locked, reading.Key, bloody, puzzle);
    }

    private static DoorQueryContract.DoorSample? WeakDoorStateSample(LG_WeakDoor weak)
    {
        if (weak.WasCollected) return null;
        try
        {
            int status = (int)weak.LastStatus;
            bool locked = MapObjectDoorStatus.IsLocked(status);
            var locks = weak.WeakLocks;
            if (locks != null)
                for (var index = 0; index != locks.Length; index++)
                    locked |= locks[index] is { WasCollected: false } held && held.Status != eWeakLockStatus.Unlocked;
            return new DoorQueryContract.DoorSample(status, locked, null, false);
        }
        catch (Exception containedError) { MapLog.Contained("map-plugin-session-resources.door-sample", containedError); return null; }
    }

    /// <summary>The zone one weak door stands in: the level's own coordinates for it and the entity reference the
    /// row's `zone` port carries, read once. The gate the door was spawned on links the course node, and the node
    /// owns the zone; a door the level cannot walk to yields nothing rather than a guessed zone.</summary>
    private (ZoneCoordinates Coordinates, EntityReference Reference)? WeakDoorPlacement(LG_WeakDoor door)
    {
        if (door == null || door.WasCollected) return null;
        LG_Zone? zone;
        try
        {
            // The gate links navigation nodes, and only a course node stands in a zone: a node of another kind
            // carries no zone at all, so the walk takes the first node that really is one.
            var nodes = door.Gate?.m_nodes;
            zone = null;
            if (nodes != null)
                for (var index = 0; index != nodes.Count && zone == null; index++)
                    zone = nodes[index]?.TryCast<AIG_CourseNode>()?.m_zone;
        }
        catch (Exception containedError) { MapLog.Contained("map-plugin-session-resources.weak-door-placement", containedError); return null; }
        if (zone == null || zone.WasCollected) return null;
        return ZoneTable() is { } table && table.TryCoordinates(zone, out var coordinates)
            ? (coordinates, RuntimeZones.Reference(_kernel.WorldEpoch, coordinates.Dimension, coordinates.Layer, coordinates.Zone))
            : null;
    }

    /// <summary>The zone table of the world being read, or null before a world exists to read it from. The table
    /// is keyed by this kernel's own world epoch, so a level of another world can never answer for this one; a
    /// table built from no level at all is not a table this provider can place anything in, which is what the
    /// null answer carries to every reader of it.</summary>
    private ZoneIndex? ZoneTable()
        => ZoneIndex.Current(_kernel.WorldEpoch) is { HoldsLevel: true } table ? table : null;

    /// <summary>The zone one `gtfo.map_object` stands in, read from the zone coordinates its own address spells
    /// (`category/dimension/layer/zone/key`) and checked against the zone table of this world. An address that
    /// spells no zone (the compact spelling) or names a zone this level does not have places nothing, which the
    /// kernel's zone read answers as an unknown entity rather than as one outside every zone.</summary>
    private EntityReference? ZoneOfMapObject(EntityReference reference)
    {
        var parts = Id(reference)?.Split('/');
        if (parts is not { Length: 5 } || !MapObjectReference.IsCoordinate(parts[1])
            || !MapObjectReference.IsCoordinate(parts[2]) || !MapObjectReference.IsCoordinate(parts[3])) return null;
        var coordinates = new ZoneCoordinates(int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture));
        return ZoneTable()?.ZoneAt(coordinates) == null ? null
            : RuntimeZones.Reference(_kernel.WorldEpoch, coordinates.Dimension, coordinates.Layer, coordinates.Zone);
    }

    /// <summary>The zone an anchor stands in, as the kernel's own zone read answers it: the provider that owns
    /// the anchor's kind places it — a player is placed by the Player package from its own course node — and this
    /// half reads no other package's table. A read the owner could not make is refused with the owner's own code,
    /// never answered as an anchor standing outside every zone.</summary>
    private EntityReference? ZoneOfAnchor(EntityReference reference)
    {
        var answer = _kernel.ZoneOfEntity(reference);
        if (!answer.Answered)
            throw new RuntimeContractException(answer.Code, "The zone of " + reference.Id + " could not be read.");
        return answer.Zone;
    }

    /// <summary>The zone the level's own coordinates name, or null when this level has no single zone at them. The
    /// layer member is the vocabulary <see cref="ZoneSelectorContract.Layers"/> declares — the same three layers
    /// `LG_LayerType` names and the objective rows index — so a coordinate set written on the zone row and a layer
    /// an objective write names are one vocabulary. A layer outside the list is refused by name rather than mapped
    /// to the nearest one, and a world with no level zone table is refused rather than answered as "no such
    /// zone".</summary>
    private EntityReference? ZoneOfCoordinates(int dimension, string layer, int localIndex)
    {
        if (ZoneTable() is not { } zones)
            throw new RuntimeContractException(ZoneSelectorContract.AnchorUnavailableCode,
                "This world holds no level zone table to read.");
        var layerType = Array.IndexOf(ZoneSelectorContract.Layers, layer);
        if (layerType < 0)
            throw new RuntimeContractException(ZoneSelectorContract.LayerCode, "Unknown layer member: " + layer);
        var coordinates = new ZoneCoordinates(dimension, layerType, localIndex);
        return zones.ZoneAt(coordinates) == null
            ? null
            : RuntimeZones.Reference(_kernel.WorldEpoch, coordinates.Dimension, coordinates.Layer, coordinates.Zone);
    }

    /// <summary>Every zone of the level being read, as the `zone` resource kind's whole answer. A world that
    /// holds no level holds no zone: the kind has an owner and the owner has nothing to name, which is an empty
    /// table rather than a refusal — the read itself succeeded.</summary>
    private IReadOnlyList<ResourceRef> ZoneResources()
    {
        if (ZoneTable() is not { } zones) return Array.Empty<ResourceRef>();
        var resources = new List<ResourceRef>(zones.Zones().Count);
        foreach (var zone in zones.Zones())
            if (zones.TryCoordinates(zone, out var coordinates)) resources.Add(ZoneReference(coordinates));
        return resources;
    }

    /// <summary>The zone one resource id names right now, or null when this level has no single zone at those
    /// coordinates. The id is the level's own three coordinates, so an id of another shape or of another level
    /// names nothing here instead of being reinterpreted.</summary>
    private ResourceRef? ResolveZoneResource(string resourceId)
        => ZoneCoordinatesOf(resourceId) is { } coordinates && ZoneTable()?.ZoneAt(coordinates) != null
            ? ZoneReference(coordinates) : null;

    /// <summary>The three coordinates a zone id spells, or null when the text is not that.</summary>
    private static ZoneCoordinates? ZoneCoordinatesOf(string resourceId)
    {
        if (!EntityKinds.Is(resourceId, EntityKinds.Zone)) return null;
        var parts = resourceId[(EntityKinds.Zone.Length + 1)..].Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var dimension) || !int.TryParse(parts[1], out var layer)
            || !int.TryParse(parts[2], out var zone)) return null;
        return new ZoneCoordinates(dimension, layer, zone);
    }

    /// <summary>One live zone as its resource reference: the level's own three coordinates and the world they
    /// belong to, built from the one table entry every zone reader goes through.</summary>
    private ResourceRef ZoneReference(ZoneCoordinates coordinates)
        => new(RuntimeZones.ResourceKind, RuntimeZones.Id(coordinates.Dimension, coordinates.Layer, coordinates.Zone));

    /// <summary>Every objective layer this level can be asked about, as the `objective` resource kind's whole
    /// answer: its three objective layers and sixteen shared world-event condition slots. Layers use the `layer:` reference the
    /// level-event rows publish an objective under, so a state read and the trigger that announced the layer name
    /// the same place with the same text (ruling 124.3). These are compiled level slots, available before
    /// generation; an action or query still checks the active objective in the addressed slot.</summary>
    private static IReadOnlyList<ResourceRef> ObjectiveLayerResources()
    {
        var resources = new List<ResourceRef>(LevelObjectiveValueContract.Layers.Count + WorldEventContract.ConditionSlots);
        foreach (var layer in LevelObjectiveValueContract.Layers)
            resources.Add(ObjectiveReference(layer));
        for (int index = 0; index < WorldEventContract.ConditionSlots; index++)
            resources.Add(new ResourceRef(WorldEventContract.ConditionResourceKind, WorldEventContract.ConditionResourceId(index)));
        return resources;
    }

    /// <summary>The objective resource one id names, or null when it is neither a layer nor a condition slot this
    /// package writes. The id is the layer's own name, so
    /// `layer:main` names a layer; `world-event-condition:0` names the level's shared condition slot zero.</summary>
    private static ResourceRef? ResolveObjectiveLayerResource(string resourceId)
        => LevelObjectiveValueContract.LayerOf(resourceId) is { } layer
            ? ObjectiveReference(layer)
            : WorldEventContract.ConditionIndex(resourceId) is not null
                ? new ResourceRef(WorldEventContract.ConditionResourceKind, resourceId) : null;

    /// <summary>One layer as the objective resource the level-event rows publish it under.</summary>
    private static ResourceRef ObjectiveReference(string layer)
        => new(LevelObjectiveValueContract.ObjectiveResourceKind,
            LevelObjectiveValueContract.LayerReferencePrefix + layer);

    /// <summary>The native instance behind a map-object address, for the categories whose native owner can
    /// resolve one. Both owners can: a door is the entrance gate of the zone the address names, and a terminal
    /// is the terminal at that placement index in the zone the address names. A category with no such lookup
    /// answers nothing, and the read then simply does not happen.</summary>
    private static object? Resolve(string category, string address)
    {
        if (category == MapObjectCategories.Door)
            return MapObjectDoorAddress.TryParse(address) is { } door ? DoorObservation.Resolve(door) : null;
        if (category == MapObjectCategories.Terminal)
            return MapObjectTerminalAddress.TryParse(address) is { } terminal
                ? TerminalObservation.ByAddress(terminal) : null;
        return null;
    }

    /// <summary>The terminal one action recipient names, for the terminal action rows. The address is read back
    /// with the terminal category's own grammar and resolved through the same level table the observation half
    /// reads, so a terminal action and a terminal fact can never disagree about which terminal an address names.
    /// The kernel's own instance check stays the caller's: this answers the instance, never an identity.</summary>
    private static LG_ComputerTerminal? TerminalFor(EntityReference reference)
        => TerminalObjectActions.Address(reference) is { } address ? TerminalObservation.ByAddress(address) : null;

}
