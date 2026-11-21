using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AIGraph;
using ChainedPuzzles;
using Enemies;
using LevelGeneration;
using Player;
using SNetwork;
using TMPro;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ForgeDevelopment.Native;

/// <summary>Snapshot section selection. Full and differential collection share the row builders;
/// the monitor enforces one cooperative budget around both cursors.</summary>
internal sealed class CaptureOptions
{
    internal CaptureOptions(string reason, long tick)
    {
        Reason = reason;
        Tick = tick;
    }

    internal string Reason { get; }
    internal long Tick { get; }
    /// <summary>Rows one section may add. A section over its cap says so in the snapshot's notes.</summary>
    internal int MaxRowsPerSection { get; init; } = int.MaxValue;
    internal int MaxTexts { get; init; } = int.MaxValue;
    internal bool IncludeText { get; init; } = true;
    internal bool IncludeEnvironment { get; init; } = true;
    internal bool IncludeNavMarkers { get; init; } = true;
    /// <summary>The snapshot-only detail sections: session players, weapons, deployables, thrown items, waves, enemy
    /// detail and scan routes. The per-frame change pass never diffs them, so it leaves them off.</summary>
    internal bool IncludeDetail { get; init; } = true;
    /// <summary>The account loadout list. It can hold hundreds of rows and only changes in the lobby.</summary>
    internal bool IncludeLoadout { get; init; } = true;
}

/// <summary>
/// The world half of the capture: it reads the live level and answers rows. Every member read here exists in this
/// build's interop metadata. A member a future build renames reads as null rather than throwing, because a snapshot
/// that dies on a renamed property costs the session's evidence.
/// </summary>
internal static partial class CaptureWorld
{
    private const string Where = "ForgeDevelopment.Native.CaptureWorld";

    internal static IEnumerable<int> CollectSlices(CaptureOptions options, CaptureSnapshot snapshot)
        => CaptureReadDiagnostics.InSnapshot(snapshot, CollectWorldSlices(options, snapshot));

    private static IEnumerable<int> CollectWorldSlices(CaptureOptions options, CaptureSnapshot snapshot)
    {
        var floor = CurrentFloor();
        var zones = floor?.allZones;
        var zoneOf = new Dictionary<int, LG_Zone>();
        if (zones != null) foreach (var step in MapNodesToZones(zones, zoneOf)) yield return step;
        snapshot.Section(CaptureSections.Level, "GameStateManager + RundownManager + LG_LevelBuilder.Current.m_currentFloor")
            .Rows.Add(LevelRow(floor, options));
        if (zones == null || zones.Count == 0) snapshot.Notes.Add("no generated floor: zone-derived sections are empty");
        else foreach (var step in FillZones(snapshot, options, zones)) yield return step;
        foreach (var step in FillObjects(snapshot, options, floor, zoneOf)) yield return step;
    }

    private static LG_Floor? CurrentFloor()
    {
        try
        {
            var builder = LG_LevelBuilder.Current;
            if (builder == null || builder.WasCollected) return null;
            var floor = builder.m_currentFloor;
            return floor == null || floor.WasCollected ? null : floor;
        }
        catch (Exception error) { Log("floor", error); return null; }
    }

    private static CaptureRow LevelRow(LG_Floor? floor, CaptureOptions options)
    {
        var row = new CaptureRow("level", "level/current", "current level");
        row.Field("gameState", Safe(0, static _ => GameStateManager.CurrentStateName.ToString()));
        row.Field("inExpedition", Safe(0, static _ => GameStateManager.IsInExpedition.ToString()));
        row.Field("inAllowedInfectionState", Safe(0, static _ => GameStateManager.IsInAllowedInfectionState.ToString()));
        row.Field("minutesInLevel", Safe(0, static _ => GameStateManager.MinutesInLevel.ToString()));
        row.Field("rundownKey", Safe(0, static _ => RundownManager.ActiveRundownKey));
        row.Field("expeditionKey", Safe(0, static _ => RundownManager.ActiveExpeditionUniqueKey));
        row.Field("expedition", Safe(0, static _ => DescribeExpedition(RundownManager.ActiveExpedition)));
        row.Field("expeditionStarted", Safe(0, static _ => RundownManager.ExpeditionIsStarted.ToString()));
        row.Field("sessionGUID", Safe(0, static _ => RundownManager.SessionGUID));
        row.Field("isMaster", Safe(0, static _ => SNet.IsMaster.ToString()));
        row.Field("localSlot", Safe(0, static _ => SNet.LocalPlayer == null ? null : SNet.LocalPlayer.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture)));
        row.Field("checkpoint", Safe(0, static _ => RecReflect.Describe(RecReflect.ReadPath(CheckpointManager.Current, "m_stateReplicator.State"))));
        row.Field("floorId", Safe(floor, static floor => floor == null ? null : floor.ID.ToString(CultureInfo.InvariantCulture)));
        row.Field("floorType", Safe(floor, static floor => floor == null ? null : floor.m_floorType.ToString()));
        row.Field("isBuilt", Safe(floor, static floor => floor == null ? null : floor.IsBuilt.ToString()));
        row.Field("zoneCount", Safe(floor, static floor => floor?.allZones == null ? null : floor!.allZones.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("dimensions", Safe(floor, static floor => floor?.m_dimensions == null ? null : floor!.m_dimensions.Count.ToString(CultureInfo.InvariantCulture)));
        if (options.IncludeDetail)
        {
            // The identity a level mount is matched against, read the way the game stores it: the rundown the
            // game was told to load and the replicated expedition choice. The choice's player member names the
            // Steam account that made it and is never read.
            row.Field("rundownIdToLoad", Safe(0, static _ => global::Globals.Global.RundownIdToLoad.ToString(CultureInfo.InvariantCulture)));
            row.Field("activeRundownIds", Safe(0, static _ => global::Globals.Global.ActiveRundownIds == null ? "none" : string.Join(",", global::Globals.Global.ActiveRundownIds)));
            row.Field("activeRundownType", Safe(0, static _ => RundownManager.GetActiveExpeditionData().rundownType.ToString()));
            row.Field("activeTier", Safe(0, static _ => RundownManager.GetActiveExpeditionData().tier.ToString()));
            row.Field("activeExpeditionIndex", Safe(0, static _ => RundownManager.GetActiveExpeditionData().expeditionIndex.ToString(CultureInfo.InvariantCulture)));
        }
        return row;
    }

    private static IEnumerable<int> FillZones(CaptureSnapshot snapshot, CaptureOptions options, Il2CppSystem.Collections.Generic.List<LG_Zone> zones)
    {
        var section = snapshot.Section(CaptureSections.Zones, "LG_Floor.allZones");
        for (var i = 0; i != zones.Count; i++)
        {
            yield return 0;
            if (section.Rows.Count >= options.MaxRowsPerSection) { Note(snapshot, section, zones.Count); break; }
            var zone = zones[i];
            if (zone == null || zone.WasCollected) continue;
            var row = new CaptureRow("zone", "zone/" + Safe(zone, static zone => zone.ID.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture)),
                Safe(zone, static zone => zone.AliasName) ?? "");
            row.Field("localIndex", Safe(zone, static zone => zone.LocalIndex.ToString()));
            row.Field("alias", Safe(zone, static zone => zone.Alias.ToString(CultureInfo.InvariantCulture)));
            row.Field("layer", Safe(zone, static zone => zone.Layer == null ? null : zone.Layer.m_type.ToString()));
            row.Field("dimension", Safe(zone, static zone => zone.DimensionIndex.ToString()));
            row.Field("position", Safe(zone, static zone => Vec(zone.Position)));
            row.Field("areas", Safe(zone, static zone => zone.m_areas == null ? null : zone.m_areas.Count.ToString(CultureInfo.InvariantCulture)));
            row.Field("courseNodes", Safe(zone, static zone => zone.m_courseNodes == null ? null : zone.m_courseNodes.Count.ToString(CultureInfo.InvariantCulture)));
            row.Field("terminals", Safe(zone, static zone => zone.TerminalsSpawnedInZone == null ? null : zone.TerminalsSpawnedInZone.Count.ToString(CultureInfo.InvariantCulture)));
            row.Field("lights", Safe(zone, static zone => zone.m_lightsInZone == null ? null : zone.m_lightsInZone.Length.ToString(CultureInfo.InvariantCulture)));
            row.Field("entranceGateType", Safe(zone, static zone => zone.m_sourceGate == null ? "none" : zone.m_sourceGate.Type.ToString()));
            row.Field("entranceDoor", Safe(zone, static zone => zone.m_sourceGate == null || zone.m_sourceGate.SpawnedDoor == null ? "none" : RecReflect.Describe(zone.m_sourceGate.SpawnedDoor)));
            row.Field("buildStatus", Safe(zone, static zone => zone.m_buildStatus.ToString()));
            section.Rows.Add(row);
        }
    }

    private static IEnumerable<int> FillObjects(CaptureSnapshot snapshot, CaptureOptions options, LG_Floor? floor, Dictionary<int, LG_Zone> zoneOf)
    {
        // The level's own hierarchy is walked once and classified. A door, terminal or pickup the game disabled is
        // still a child of the zone it was built in, which is exactly the object a snapshot is asked about, while a
        // scene search without inactive objects never answers it.
        var level = new LevelObjectSet();
        foreach (var step in LevelObjects(floor, level)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Doors, "LG_Floor hierarchy: LG_SecurityDoor", level.Doors, zoneOf, DoorRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Doors, "LG_Floor hierarchy: LG_WeakDoor", level.WeakDoors, zoneOf, WeakDoorRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Terminals, "LG_Floor hierarchy: LG_ComputerTerminal", level.Terminals, zoneOf, TerminalRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Generators, "LG_Floor hierarchy: LG_PowerGenerator_Core", level.Generators, zoneOf, GeneratorRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Generators, "LG_Floor hierarchy: LG_PowerGeneratorCluster", level.Clusters, zoneOf, ClusterRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Generators, "LG_Floor hierarchy: LG_HSUActivator_Core", level.HsuActivators, zoneOf, HsuActivatorRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Containers, "LG_Floor hierarchy: LG_ResourceContainer_Storage", level.Containers, zoneOf, ContainerRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Containers, "LG_Floor hierarchy: LG_WeakResourceContainer", level.WeakContainers, zoneOf, WeakContainerRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Pickups, "LG_Floor hierarchy: ItemInLevel", level.Pickups, zoneOf,
            (item, zone) => PickupRow(item, zone, options.IncludeDetail))) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Objectives, "LG_Floor hierarchy: LG_LevelExitGeo", level.Exits, zoneOf, ExitRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.ChainedPuzzles, "LG_Floor hierarchy: ChainedPuzzleInstance", level.Puzzles, zoneOf, PuzzleRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Players, "PlayerManager.PlayerAgentsInLevel", PlayerAgents(), zoneOf, PlayerRow)) yield return step;
        // Enemies and nav markers are spawned at run time instead of being built into the level, and this build's
        // interop answers no inactive-including scene search, so they are asked of the active scene.
        var enemies = LiveEnemies();
        if (enemies != null) ObserveChangeEnemies(enemies);
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Enemies, "Object.FindObjectsOfType<EnemyAgent>", enemies, zoneOf, EnemyRow)) yield return step;
        if (options.IncludeEnvironment)
        {
            yield return 0;
            foreach (var step in Fill(snapshot, options, CaptureSections.Environment, "LG_Floor hierarchy: LG_Light", level.Lights, zoneOf, LightRow)) yield return step;
        }
        if (options.IncludeNavMarkers)
        {
            yield return 0;
            foreach (var step in Fill(snapshot, options, CaptureSections.NavMarkers, "Object.FindObjectsOfType<NavMarker>", Objects<NavMarker>(), zoneOf, NavMarkerRow)) yield return step;
        }
        if (options.IncludeText) foreach (var step in FillHudText(snapshot, options)) yield return step;
        if (options.IncludeDetail) foreach (var step in FillDetail(snapshot, options, level, zoneOf)) yield return step;
    }

    /// <summary>One component of each family the level owns, filled by a single walk of the floor hierarchy. The
    /// families are disjoint: a component lands in the first list whose type it is, which is why the walk tests them in
    /// one order rather than once per family.</summary>
    private sealed class LevelObjectSet
    {
        internal List<LG_SecurityDoor> Doors { get; } = new();
        internal List<LG_WeakDoor> WeakDoors { get; } = new();
        internal List<LG_ComputerTerminal> Terminals { get; } = new();
        internal List<LG_PowerGenerator_Core> Generators { get; } = new();
        internal List<LG_PowerGeneratorCluster> Clusters { get; } = new();
        internal List<LG_HSUActivator_Core> HsuActivators { get; } = new();
        internal List<LG_ResourceContainer_Storage> Containers { get; } = new();
        internal List<LG_WeakResourceContainer> WeakContainers { get; } = new();
        internal List<ItemInLevel> Pickups { get; } = new();
        internal List<LG_LevelExitGeo> Exits { get; } = new();
        internal List<ChainedPuzzleInstance> Puzzles { get; } = new();
        internal List<LG_DimensionPortal> Portals { get; } = new();
        internal List<LG_Light> Lights { get; } = new();
    }

    private static IEnumerable<int> LevelObjects(LG_Floor? floor, LevelObjectSet level)
    {
        if (floor == null || floor.WasCollected) yield break;
        // One typed native search per family, hierarchy order, inactive included. Walking every component wrapped
        // each unrelated component in a managed object just to test its type.
        var root = floor.transform;
        AddAll(level.Doors, root); yield return 0;
        AddAll(level.WeakDoors, root); yield return 0;
        AddAll(level.Terminals, root); yield return 0;
        AddAll(level.Generators, root); yield return 0;
        AddAll(level.Clusters, root); yield return 0;
        AddAll(level.HsuActivators, root); yield return 0;
        AddAll(level.Containers, root); yield return 0;
        AddAll(level.WeakContainers, root); yield return 0;
        AddAll(level.Pickups, root); yield return 0;
        AddAll(level.Exits, root); yield return 0;
        AddAll(level.Puzzles, root); yield return 0;
        AddAll(level.Portals, root); yield return 0;
        AddAll(level.Lights, root);
    }

    private static void AddAll<T>(List<T> list, Transform root) where T : UnityEngine.Component
    {
        foreach (var value in root.GetComponentsInChildren<T>(true))
            if (value != null && !value.WasCollected) list.Add(value);
    }

    private static IEnumerable<int> Fill<T>(CaptureSnapshot snapshot, CaptureOptions options, string sectionName, string source,
        IReadOnlyList<T>? objects, Dictionary<int, LG_Zone> zoneOf, Func<T, LG_Zone?, CaptureRow> row) where T : UnityObject
    {
        var section = snapshot.Section(sectionName, source);
        if (objects == null) yield break;
        for (var i = 0; i != objects.Count; i++)
        {
            yield return 0;
            if (section.Rows.Count >= options.MaxRowsPerSection) { Note(snapshot, section, objects.Count); break; }
            var item = objects[i];
            if (item == null || item.WasCollected) continue;
            var zone = ZoneOf(item, zoneOf);
            var built = row(item, zone);
            if (zone != null && built.Zone == null) built.Zone = zone.AliasName;
            built.Field("active", Active(item));
            section.Rows.Add(built);
        }
    }

    /// <summary>Whether an object was switched on when its row was written. Recording an inactive door or terminal is
    /// only useful if the row says it was inactive, so every object row carries this one field.</summary>
    private static string Active(UnityObject value)
        => Safe(value, static value => value is Component component ? component.gameObject.activeInHierarchy.ToString() : "n/a") ?? "unreadable";

    private static CaptureRow DoorRow(LG_SecurityDoor door, LG_Zone? zone)
    {
        var row = new CaptureRow("door", "door/security/" + Safe(door, static door => door.m_serialNumber.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(door, static door => RecReflect.Describe(door)) ?? "?");
        row.Field("doorType", Safe(door, static door => door.DoorType.ToString()));
        row.Field("status", Safe(door, static door => door.LastStatus.ToString()));
        row.Field("lastState", Safe(door, static door => door.m_lastState.status.ToString()));
        row.Field("interactionAllowed", Safe(door, static door => door.InteractionAllowed.ToString()));
        row.Field("inAnimation", Safe(door, static door => door.InAnimation.ToString()));
        row.Field("requiresKey", Safe(door, static door => door.RequiresKey().ToString()));
        row.Field("keyItem", Safe(door, static door => door.m_keyItem == null ? "none" : door.m_keyItem.PublicName));
        row.Field("overrideCode", Safe(door, static door => door.m_overrideCode));
        row.Field("chainedPuzzle", Safe(door, static door => door.m_checkpointPuzzle == null ? "none" : RecReflect.Describe(door.m_checkpointPuzzle)));
        row.Field("linkedTerminal", Safe(door, static door => door.LinkedComputerTerminal == null ? "none" : door.LinkedComputerTerminal.PublicName));
        row.Field("linksToLayer", Safe(door, static door => door.LinksToLayerType.ToString()));
        row.Field("linkedZone", Safe(door, static door => RecReflect.Describe(door.LinkedToZoneData)));
        row.Field("alarmWave", Safe(door, static door => door.ActiveEnemyWaveData == null ? "none" : RecReflect.Describe(door.ActiveEnemyWaveData)));
        row.Field("checkpointDoor", Safe(door, static door => door.IsCheckpointDoor.ToString()));
        row.Field("openedDuringPlay", Safe(door, static door => door.Gate == null ? null : door.Gate.HasBeenOpenedDuringPlay.ToString()));
        row.Field("position", Safe(door, static door => Vec(door.transform.position)));
        row.Field("glueRel", Safe(door, static door => door.m_lastState.glueRel.ToString(CultureInfo.InvariantCulture)));
        CaptureDoorTopologyNative.AppendDoor(row, door);
        return row;
    }

    private static CaptureRow WeakDoorRow(LG_WeakDoor door, LG_Zone? zone)
    {
        var row = new CaptureRow("door", "door/weak/" + Safe(door, static door => door.m_serialNumber.ToString(CultureInfo.InvariantCulture), "?"), "weak door");
        row.Field("entity", Safe(door, static door => EntityOf(door)));
        row.Field("doorType", Safe(door, static door => door.DoorType.ToString()));
        row.Field("status", Safe(door, static door => door.LastStatus.ToString()));
        row.Field("requiresKey", Safe(door, static door => door.RequiresKey().ToString()));
        row.Field("interactionAllowed", Safe(door, static door => door.InteractionAllowed.ToString()));
        row.Field("openedDuringPlay", Safe(door, static door => door.Gate == null ? null : door.Gate.HasBeenOpenedDuringPlay.ToString()));
        row.Field("glueVolume", Safe(door, static door => door.m_destruction == null ? null : door.m_destruction.GetAttachedGlueVolume().ToString(CultureInfo.InvariantCulture)));
        row.Field("health", Safe(door, static door => CaptureDoorIdentity.Health(door)?.ToString(CultureInfo.InvariantCulture)));
        row.Field("destroyed", Safe(door, static door => RecReflect.ReadPath(door.m_destruction, "m_isDestroyed")?.ToString()));
        CaptureDoorTopologyNative.AppendDoor(row, door);
        row.Field("position", Safe(door, static door => Vec(door.transform.position)));
        return row;
    }

    private static CaptureRow TerminalRow(LG_ComputerTerminal terminal, LG_Zone? zone)
    {
        var row = new CaptureRow("terminal", "terminal/" + Safe(terminal, static terminal => terminal.SyncID.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(terminal, static terminal => terminal.PublicName) ?? "terminal");
        row.Field("state", Safe(terminal, static terminal => terminal.CurrentStateName.ToString()));
        row.Field("key", Safe(terminal, static terminal => terminal.ItemKey));
        row.Field("passwordProtected", Safe(terminal, static terminal => terminal.IsPasswordProtected.ToString()));
        row.Field("hasPasswordPart", Safe(terminal, static terminal => terminal.HasPasswordPart.ToString()));
        row.Field("heldPassword", Safe(terminal, static terminal => terminal.HeldPasswordPart));
        row.Field("woType", Safe(terminal, static terminal => terminal.WardenObjectiveType.ToString()));
        row.Field("puzzleSolved", Safe(terminal, static terminal => terminal.PuzzleSolved.ToString()));
        row.Field("activeObjectiveItem", Safe(terminal, static terminal => terminal.IsActiveWardenObjectiveItem.ToString()));
        row.Field("chainPuzzle", Safe(terminal, static terminal => terminal.ChainedPuzzleForWardenObjective == null ? "none" : RecReflect.Describe(terminal.ChainedPuzzleForWardenObjective)));
        row.Field("gatherCommand", Safe(terminal, static terminal => terminal.GatherTerminalCommand));
        row.Field("customLine", Safe(terminal, static terminal => terminal.CustomLineText));
        row.Field("showInputLine", Safe(terminal, static terminal => terminal.ShowInputLine.ToString()));
        row.Field("showCustomLine", Safe(terminal, static terminal => terminal.ShowCustomLine.ToString()));
        row.Field("logVisible", Safe(terminal, static terminal => terminal.IsLogVisible(terminal.ItemKey).ToString()));
        row.Field("position", Safe(terminal, static terminal => Vec(terminal.transform.position)));
        return row;
    }

    private static CaptureRow GeneratorRow(LG_PowerGenerator_Core generator, LG_Zone? zone)
    {
        var row = new CaptureRow("generator", "generator/" + Safe(generator, static generator => generator.m_serialNumber.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(generator, static generator => generator.PublicName) ?? "generator");
        row.Field("status", Safe(generator, static generator => RecReflect.ReadPath(generator.m_stateReplicator, "State.status")?.ToString()));
        row.Field("changedBy", Safe(generator, static generator => RecReflect.ReadPath(generator.m_stateReplicator, "State.playerThatChangedTheState")?.ToString()));
        row.Field("key", Safe(generator, static generator => generator.m_itemKey));
        row.Field("wardenObjective", Safe(generator, static generator => generator.m_isWardenObjective.ToString()));
        row.Field("objectiveSolved", Safe(generator, static generator => generator.ObjectiveItemSolved.ToString()));
        row.Field("chainIndex", Safe(generator, static generator => generator.WardenObjectiveChainIndex.ToString(CultureInfo.InvariantCulture)));
        row.Field("linkedDoor", Safe(generator, static generator => generator.LinkedSecurityDoor == null ? "none" : RecReflect.Describe(generator.LinkedSecurityDoor)));
        row.Field("position", Safe(generator, static generator => Vec(generator.transform.position)));
        return row;
    }

    private static CaptureRow ClusterRow(LG_PowerGeneratorCluster cluster, LG_Zone? zone)
    {
        var row = new CaptureRow("generatorCluster", "generatorCluster/" + Safe(cluster, static cluster => cluster.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "generator cluster");
        row.Field("generators", Safe(cluster, static cluster => cluster.m_generators == null ? null : cluster.m_generators.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("chainedPuzzle", Safe(cluster, static cluster => cluster.m_chainedPuzzleMidObjective == null ? "none" : RecReflect.Describe(cluster.m_chainedPuzzleMidObjective)));
        row.Field("position", Safe(cluster, static cluster => Vec(cluster.transform.position)));
        return row;
    }

    private static CaptureRow HsuActivatorRow(LG_HSUActivator_Core activator, LG_Zone? zone)
    {
        var row = new CaptureRow("hsuActivator", "hsuActivator/" + Safe(activator, static activator => activator.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "HSU activator");
        row.Field("status", Safe(activator, static activator => RecReflect.ReadPath(activator.m_stateReplicator, "State.status")?.ToString()));
        row.Field("position", Safe(activator, static activator => Vec(activator.transform.position)));
        return row;
    }

    private static CaptureRow ContainerRow(LG_ResourceContainer_Storage container, LG_Zone? zone)
    {
        var row = new CaptureRow("container", "container/" + Safe(container, static container => container.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "resource container");
        row.Field("position", Safe(container, static container => Vec(container.transform.position)));
        row.Field("items", Safe(container, static container => container.m_numberOfItems.ToString(CultureInfo.InvariantCulture)));
        return row;
    }

    private static CaptureRow WeakContainerRow(LG_WeakResourceContainer container, LG_Zone? zone)
    {
        var row = new CaptureRow("container", "container/weak/" + Safe(container, static container => container.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "weak resource container");
        row.Field("locked", Safe(container, static container => container.IsLocked().ToString()));
        row.Field("position", Safe(container, static container => Vec(container.transform.position)));
        return row;
    }

    private static CaptureRow PickupRow(ItemInLevel item, LG_Zone? zone, bool detail)
    {
        var row = new CaptureRow("pickup", "pickup/" + Safe(item, static item => item.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), item.GetType().Name);
        row.Field("publicName", Safe(item, static item => RecReflect.ReadPath(item, "PublicName") as string));
        row.Field("status", Safe(item, static item => item.GetSyncComponent()?.GetCurrentState().status.ToString()));
        row.Field("pickedUpBy", Safe(item, static item => PickupPlayer(item)));
        var objectiveItem = item.TryCast<CarryItemPickup_Core>();
        if (objectiveItem != null)
        {
            row.Field("solved", Safe(objectiveItem, static objectiveItem => objectiveItem.ObjectiveItemSolved.ToString()));
            row.Field("chainIndex", Safe(objectiveItem, static objectiveItem => objectiveItem.WardenObjectiveChainIndex.ToString(CultureInfo.InvariantCulture)));
        }
        var terminalItem = PickupTerminalItem(item, objectiveItem);
        if (terminalItem != null) row.Field("key", Safe(terminalItem, static terminalItem => terminalItem.TerminalItemKey));
        row.Field("container", Safe(item, static item => item.container == null ? "none" : RecReflect.Describe(item.container)));
        row.Field("courseNode", Safe(item, static item => item.CourseNode == null ? null : item.CourseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("position", Safe(item, static item => Vec(item.transform.position)));
        if (detail)
        {
            // The same pair the backpack rows carry, so a ground pickup and a held item compare field by field.
            row.Field("itemIdGearCrc", Safe(item, static item => item.pItemData.itemID_gearCRC.ToString(CultureInfo.InvariantCulture)));
            row.Field("dataBlockId", Safe(item, static item => item.ItemDataBlock == null ? "none" : item.ItemDataBlock.persistentID.ToString(CultureInfo.InvariantCulture)));
        }
        return row;
    }

    private static string? PickupPlayer(ItemInLevel item)
    {
        var sync = item.GetSyncComponent();
        if (sync == null) return "unreadable:missing-sync";
        var state = sync.GetCurrentState();
        if (state.status != ePickupItemStatus.PickedUp) return "none";
        return state.pPlayer.TryGetPlayer(out var player) ? RecReflect.Describe(player)
            : "unresolved:" + state.pPlayer.lookup.ToString(CultureInfo.InvariantCulture);
    }

    private static iTerminalItem? PickupTerminalItem(ItemInLevel item, CarryItemPickup_Core? carry)
    {
        if (carry != null) return carry.m_terminalItem;
        if (item.TryCast<Gear.ResourcePackPickup>() is { } resource) return resource.m_terminalItem;
        if (item.TryCast<KeyItemPickup_Core>() is { } key) return key.m_terminalItem;
        if (item.TryCast<GenericSmallPickupItem_Core>() is { } generic) return generic.m_terminalItem;
        return null;
    }

    private static CaptureRow ExitRow(LG_LevelExitGeo exit, LG_Zone? zone)
    {
        var row = new CaptureRow("exit", "exit/" + Safe(exit, static exit => exit.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "level exit");
        row.Field("position", Safe(exit, static exit => Vec(exit.transform.position)));
        return row;
    }

    private static CaptureRow PuzzleRow(ChainedPuzzleInstance puzzle, LG_Zone? zone)
    {
        var row = new CaptureRow("chainedPuzzle", "chainedPuzzle/" + Safe(puzzle, static puzzle => puzzle.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "chained puzzle");
        row.Field("puzzles", Safe(puzzle, static puzzle => puzzle.NRofPuzzles().ToString(CultureInfo.InvariantCulture)));
        row.Field("state", Safe(puzzle, static puzzle => RecReflect.Describe(RecReflect.ReadPath(puzzle, "m_stateReplicator.State"))));
        row.Field("survivalWaveId", Safe(puzzle, static puzzle => puzzle.GetSetSurvivalWaveID().ToString(CultureInfo.InvariantCulture)));
        row.Field("position", Safe(puzzle, static puzzle => Vec(puzzle.transform.position)));
        return row;
    }

    private static CaptureRow PlayerRow(PlayerAgent player, LG_Zone? zone)
    {
        var row = new CaptureRow("player", "player/" + Safe(player, static player => player.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(player, static player => player.PlayerName) ?? "player");
        row.Field("slot", Safe(player, static player => player.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture)));
        row.Field("entity", Safe(player, static player => EntityOf(player)));
        row.Field("pointer", Safe(player, static player => player.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)));
        row.Field("local", Safe(player, static player => player.IsLocallyOwned.ToString()));
        row.Field("alive", Safe(player, static player => player.Alive.ToString()));
        row.Field("health", Safe(player, static player => player.Damage == null ? null : player.Damage.Health.ToString(CultureInfo.InvariantCulture)));
        row.Field("healthMax", Safe(player, static player => player.Damage == null ? null : player.Damage.HealthMax.ToString(CultureInfo.InvariantCulture)));
        row.Field("infection", Safe(player, static player => player.Damage == null ? null : player.Damage.Infection.ToString(CultureInfo.InvariantCulture)));
        row.Field("air", Safe(player, static player => player.Damage == null ? null : player.Damage.Air.ToString(CultureInfo.InvariantCulture)));
        row.Field("grabbed", Safe(player, static player => player.Damage == null ? null : player.Damage.GrabbedAtAll.ToString()));
        row.Field("cannotDie", Safe(player, static player => player.Damage == null ? null : player.Damage.CannotDie.ToString()));
        row.Field("ignoreAllDamage", Safe(player, static player => player.Damage == null ? null : player.Damage.IgnoreAllDamage.ToString()));
        row.Field("position", Safe(player, static player => Vec(player.Position)));
        row.Field("dimension", Safe(player, static player => player.DimensionIndex.ToString()));
        row.Field("gameObjectId", Safe(player, static player => player.GameObjectID.ToString(CultureInfo.InvariantCulture)));
        row.Field("courseNode", Safe(player, static player => player.CourseNode == null ? null : player.CourseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("wielded", Safe(player, static player => player.Inventory == null || player.Inventory.WieldedItem == null ? "none" : RecReflect.Describe(player.Inventory.WieldedItem)));
        row.Field("wieldedSlot", Safe(player, static player => player.Inventory == null ? null : player.Inventory.WieldedSlot.ToString()));
        row.Field("flashlight", Safe(player, static player => player.Inventory == null ? null : player.Inventory.FlashlightEnabled.ToString()));
        row.Field("nativeNoise", Safe(player, static player => player.m_noise.ToString()));
        row.Field("nativeNoiseTimestamp", Safe(player, static player => player.m_noiseTimestamp.ToString(CultureInfo.InvariantCulture)));
        row.Field("backpack", Safe(player, static player => BackpackOf(player)));
        row.Field("ammo", Safe(player, static player => AmmoOf(player)));
        row.Field("slotsLoaded", Safe(player, static player => SlotsLoaded(player)));
        row.Field("fpWielded", Safe(player, static player => player.FPItemHolder == null || player.FPItemHolder.WieldedItem == null ? "none" : RecReflect.Describe(player.FPItemHolder.WieldedItem)));
        row.Field("syncModel", Safe(player, static player => RecReflect.Describe(player.PlayerSyncModel)));
        row.Field("playerSync", Safe(player, static player => RecReflect.Describe(player.Sync)));
        return row;
    }

    /// <summary>The backpack is reached through the manager, not the agent: the agent has no backpack member, and the
    /// manager's lookup is the game's own answer for a player's inventory.</summary>
    private static string BackpackOf(PlayerAgent player)
    {
        if (player.Owner == null) return "none";
        if (!PlayerBackpackManager.TryGetBackpack(player.Owner, out var backpack) || backpack == null) return "none";
        var storage = backpack.AmmoStorage;
        var ammo = storage == null ? "none" : RecReflect.Describe(storage);
        return RecReflect.Describe(backpack) + " ammo=" + ammo;
    }

    private static CaptureRow EnemyRow(EnemyAgent enemy, LG_Zone? zone)
    {
        var row = new CaptureRow("enemy", "enemy/" + Safe(enemy, static enemy => enemy.GlobalID.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(enemy, static enemy => enemy.EnemyData == null ? null : enemy.EnemyData.name) ?? "enemy");
        row.Field("typeId", Safe(enemy, static enemy => enemy.EnemyDataID.ToString(CultureInfo.InvariantCulture)));
        row.Field("alive", Safe(enemy, static enemy => enemy.Alive.ToString()));
        row.Field("health", Safe(enemy, static enemy => enemy.Damage == null ? null : enemy.Damage.Health.ToString(CultureInfo.InvariantCulture)));
        row.Field("healthMax", Safe(enemy, static enemy => enemy.Damage == null ? null : enemy.Damage.HealthMax.ToString(CultureInfo.InvariantCulture)));
        row.Field("tagged", Safe(enemy, static enemy => enemy.IsTagged.ToString()));
        row.Field("wasTagged", Safe(enemy, static enemy => enemy.WasTagged.ToString()));
        row.Field("tagMarker", Safe(enemy, static enemy => enemy.m_tagMarker == null ? "none" : RecReflect.Describe(enemy.m_tagMarker)));
        row.Field("tagTimer", Safe(enemy, static enemy => enemy.EnemyTaggedTimer.ToString(CultureInfo.InvariantCulture)));
        row.Field("scannerColor", Safe(enemy, static enemy => Color(enemy.m_scannerColor)));
        row.Field("behaviourState", Safe(enemy, static enemy => RecReflect.ReadPath(enemy.AI, "m_behaviour.m_currentStateName")?.ToString()));
        row.Field("hibernating", Safe(enemy, static enemy => Hibernating(enemy.AI).ToString()));
        row.Field("target", Safe(enemy, static enemy => RecReflect.Describe(RecReflect.ReadPath(enemy.AI, "m_lastAgentDetected"))));
        row.Field("navGoal", Safe(enemy, static enemy => RecReflect.Describe(RecReflect.ReadPath(enemy.AI, "NavmeshAgentGoal"))));
        row.Field("position", Safe(enemy, static enemy => Vec(enemy.Position)));
        row.Field("courseNode", Safe(enemy, static enemy => enemy.CourseNode == null ? null : enemy.CourseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("sizeMultiplier", Safe(enemy, static enemy => enemy.SizeMultiplier.ToString(CultureInfo.InvariantCulture)));
        row.Field("limbs", Safe(enemy, static enemy => enemy.Damage == null || enemy.Damage.DamageLimbs == null ? null : enemy.Damage.DamageLimbs.Length.ToString(CultureInfo.InvariantCulture)));
        row.Field("glue", Safe(enemy, static enemy => enemy.Damage == null ? null : enemy.Damage.AttachedGlueVolume.ToString(CultureInfo.InvariantCulture)));
        row.Field("onFire", Safe(enemy, static enemy => enemy.Damage == null ? null : enemy.Damage.OnFire.ToString()));
        row.Field("heatLevel", Safe(enemy, static enemy => enemy.Damage == null ? null : enemy.Damage.m_heatLevel.ToString(CultureInfo.InvariantCulture)));
        // The AI's own target, next to the last detected agent above: a behaviour edge is a change of this pair, and
        // the change pass records it on the frame it happened.
        row.Field("aiTarget", Safe(enemy, static enemy => enemy.AI == null || enemy.AI.Target == null ? "none" : RecReflect.Describe(enemy.AI.Target.m_agent)));
        row.Field("aiTargetValid", Safe(enemy, static enemy => enemy.AI == null ? null : enemy.AI.IsTargetValid.ToString()));
        row.Field("entity", Safe(enemy, static enemy => EntityOf(enemy)));
        row.Field("aiTargetDetectionType", Safe(enemy, static enemy => enemy.AI?.Target?.m_detectionType.ToString()));
        var bossPhase = Safe(enemy, static enemy => enemy.AI == null || enemy.AI.m_behaviour == null ? null
            : enemy.AI.m_behaviour.TryCast<SquidBossBehaviour>()?.Phase.ToString(CultureInfo.InvariantCulture));
        if (bossPhase != null) row.Field("bossPhase", bossPhase);
        return row;
    }

    private static CaptureRow LightRow(LG_Light light, LG_Zone? zone)
    {
        var row = new CaptureRow("light", "light/" + Safe(light, static light => light.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), light.GetType().Name);
        row.Field("intensity", Safe(light, static light => light.GetIntensity().ToString(CultureInfo.InvariantCulture)));
        row.Field("color", Safe(light, static light => RecReflect.Describe(RecReflect.ReadPath(light, "m_color"))));
        row.Field("position", Safe(light, static light => Vec(light.GetPosition())));
        return row;
    }

    private static CaptureRow NavMarkerRow(NavMarker marker, LG_Zone? zone)
    {
        var row = new CaptureRow("navMarker", "navMarker/" + Safe(marker, static marker => marker.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), "nav marker");
        row.Field("tracked", Safe(marker, static marker => RecReflect.Describe(marker.m_trackingObj)));
        row.Field("visible", Safe(marker, static marker => marker.IsVisible.ToString()));
        row.Field("trackingDimension", Safe(marker, static marker => RecReflect.Describe(marker.TrackingDimension)));
        row.Field("signInfo", Safe(marker, static marker => marker.m_signInfo == null ? null : marker.m_signInfo.text));
        row.Field("title", Safe(marker, static marker => marker.m_title == null ? null : marker.m_title.text));
        row.Field("titleText", Safe(marker, static marker => marker.m_title == null ? null : marker.m_title.text));
        row.Field("playerName", Safe(marker, static marker => marker.m_playerName == null ? null : marker.m_playerName.text));
        row.Field("distance", Safe(marker, static marker => marker.m_distance == null ? null : marker.m_distance.text));
        row.Field("position", Safe(marker, static marker => Vec(marker.transform.position)));
        return row;
    }

    /// <summary>The GUI texts. Every non-empty TMP text the active scene answers is recorded with its path, final
    /// text, colour and position, because a HUD number or a broadcast line is only verifiable through the text the
    /// game ended up showing, not through the field some code wrote. Hidden states are covered by the `enabled` flag
    /// next to `active`, which is the pair a reader needs to tell a text that was switched off from one never drawn.
    /// </summary>
    private static IEnumerable<int> FillHudText(CaptureSnapshot snapshot, CaptureOptions options)
    {
        var section = snapshot.Section(CaptureSections.HudText, "Object.FindObjectsOfType<TMP_Text>");
        var texts = Objects<TMP_Text>();
        if (texts == null) yield break;
        for (var i = 0; i != texts.Length; i++)
        {
            yield return 0;
            if (section.Rows.Count >= options.MaxTexts) { Note(snapshot, section, texts.Length); break; }
            var text = texts[i];
            if (text == null || text.WasCollected) continue;
            var content = Safe(text, static text => text.text);
            if (string.IsNullOrEmpty(content)) continue;
            var row = new CaptureRow("hudText", "hudText/" + Path(text.transform), Safe(text, static text => text.name) ?? "text");
            row.Field("text", content);
            row.Field("color", Safe(text, static text => Color(text.color)));
            row.Field("enabled", Safe(text, static text => text.enabled.ToString()));
            row.Field("position", Safe(text, static text => Vec(text.transform.position)));
            row.Field("scale", Safe(text, static text => Vec(text.transform.lossyScale)));
            row.Field("active", Active(text));
            section.Rows.Add(row);
        }
    }

    private static IReadOnlyList<PlayerAgent>? PlayerAgents()
    {
        try
        {
            var agents = PlayerManager.PlayerAgentsInLevel;
            if (agents == null) return null;
            var list = new List<PlayerAgent>(agents.Count);
            for (var i = 0; i != agents.Count; i++)
            {
                var agent = agents[i];
                if (agent != null) list.Add(agent);
            }
            return list;
        }
        catch (Exception error) { Log("players", error); return null; }
    }

    private static IEnumerable<int> MapNodesToZones(Il2CppSystem.Collections.Generic.List<LG_Zone> zones, Dictionary<int, LG_Zone> map)
    {
        for (var i = 0; i != zones.Count; i++)
        {
            yield return 0;
            var zone = zones[i];
            if (zone == null || zone.WasCollected) continue;
            var nodes = zone.m_courseNodes;
            if (nodes == null) continue;
            for (var n = 0; n != nodes.Count; n++)
            {
                yield return 0;
                var node = nodes[n];
                if (node == null || node.WasCollected) continue;
                map[node.NodeID] = zone;
            }
        }

    }

    private static LG_Zone? ZoneOf(UnityObject item, Dictionary<int, LG_Zone> zoneOf)
    {
        if (item is EnemyAgent enemy) return ZoneOfNode(enemy.CourseNode, zoneOf);
        // Every object this walks is a component; the cast keeps the walk off System.Object, whose alias the file
        // imports for the generic constraint above.
        var transform = item is Component component ? component.transform : null;
        while (transform != null)
        {
            var zone = transform.GetComponent<LG_Zone>();
            if (zone != null && !zone.WasCollected) return zone;
            transform = transform.parent;
        }
        return null;
    }

    private static LG_Zone? ZoneOfNode(AIG_CourseNode? node, Dictionary<int, LG_Zone> zoneOf)
        => node != null && zoneOf.TryGetValue(node.NodeID, out var owner) ? owner : null;

    /// <summary>Every object of one family in the loaded scene. This build's interop exposes no `includeInactive`
    /// overload of `FindObjectsOfType`, so a family reached this way is the active one; the level's own objects are
    /// walked through its hierarchy instead, which is where a disabled terminal or door still exists.</summary>
    private static T[]? Objects<T>() where T : UnityObject
    {
        try { return UnityObject.FindObjectsOfType<T>(); }
        catch (Exception error) { Log("find:" + typeof(T).Name, error); return null; }
    }

    private static string Path(Transform transform)
    {
        var text = new StringBuilder(64);
        while (transform != null)
        {
            text.Insert(0, "/" + transform.name);
            transform = transform.parent;
        }
        return text.ToString();
    }

    private static void Note(CaptureSnapshot snapshot, CaptureSection section, int available)
        => snapshot.Notes.Add(section.Name + ": " + available + " objects, capped at " + section.Rows.Count);

    private static string DescribeExpedition(GameData.ExpeditionInTierData? expedition)
    {
        if (expedition == null) return "none";
        var active = RundownManager.GetActiveExpeditionData();
        return active.expeditionIndex.ToString(CultureInfo.InvariantCulture) + "/" + active.tier
            + " " + expedition.Descriptive.PublicName;
    }

    // One string per value: the parts are formatted into the handler's pooled buffer, not into strings of their own.
    private static string Vec(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.x:0.###},{value.y:0.###},{value.z:0.###}");

    private static string Color(Color value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.r:0.###},{value.g:0.###},{value.b:0.###},{value.a:0.###}");

    private static bool Hibernating(EnemyAI? ai)
    {
        if (ai == null) return false;
        // A failed read is reported by the Safe wrapper at the call site, never turned into "awake".
        return ai.IsHibernating(out _, out _);
    }


    /// <summary>The state goes in as an argument and the lambda is static, so a read allocates no closure: the delegate
    /// is built once per call site and kept. A row builder passes the object it reads.</summary>
    private static string? Safe<TState>(TState state, Func<TState, string?> read, [CallerLineNumber] int line = 0,
        [CallerArgumentExpression("read")] string expression = "", [CallerMemberName] string caller = "")
        => CaptureReadDiagnostics.ReadWorld(state, read, expression, caller, line);

    /// <summary>Preserve the caller's fallback and its original read expression, including through this overload.</summary>
    private static string Safe<TState>(TState state, Func<TState, string?> read, string fallback,
        [CallerArgumentExpression("read")] string expression = "", [CallerMemberName] string caller = "", [CallerLineNumber] int line = 0)
        => CaptureReadDiagnostics.ReadWorld(state, read, expression, caller, line) ?? fallback;

    private static string? Safe(Func<string?> read, [CallerLineNumber] int line = 0,
        [CallerArgumentExpression("read")] string expression = "", [CallerMemberName] string caller = "")
        => CaptureReadDiagnostics.ReadWorld(read, expression, caller, line);

    /// <summary>Preserve the caller's fallback and its original read expression, including through this overload.</summary>
    private static string Safe(Func<string?> read, string fallback, [CallerArgumentExpression("read")] string expression = "",
        [CallerMemberName] string caller = "", [CallerLineNumber] int line = 0)
        => CaptureReadDiagnostics.ReadWorld(read, expression, caller, line) ?? fallback;

    private static void Log(string what, Exception error, [CallerMemberName] string caller = "", [CallerLineNumber] int line = 0)
        => CaptureReadDiagnostics.Report(Where + ":" + what, what, error, caller: caller, line: line);
}
