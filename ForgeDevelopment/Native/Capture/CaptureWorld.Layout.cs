using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIGraph;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The level layout: what the generator decided and what does not change afterwards. For each zone that is its nav
/// name and the data block overrides behind it, its coverage, its terminals in the zone's own order and its entrance
/// door's native class. It also covers each zone's areas beside its course nodes, index by index, the dimension zones
/// the floor's zone list does not hold, and the respawner sacks the level placed. It is read once per built level and
/// on the snapshot hotkey, never per frame, because it walks every area of every zone and the floor hierarchy.
/// </summary>
internal static partial class CaptureWorld
{
    private const int MaxTerminalOrder = 64;
    private const int MaxStaticSpawns = 32;
    private const int MaxRespawnerGroups = 100;
    private const int MaxRespawnerParents = 8;
    private const int MaxUnmatchedVisualNames = 8;

    internal static IEnumerable<int> CollectLayoutSlices(List<CaptureSnapshot> records, string reason, long tick, int rowsPerRecord)
    {
        var generationId = RuntimeDiagnostics.GenerationId;
        void RequireGeneration()
        {
            if (generationId != RuntimeDiagnostics.GenerationId)
                throw new InvalidOperationException("Layout generation changed during acquisition.");
        }
        var floorRecord = new CaptureSnapshot(reason, "layout/floor", tick);
        records.Add(floorRecord);
        var floor = CurrentFloor();
        if (floor == null)
        {
            floorRecord.Notes.Add("no generated floor: the layout is empty");
            yield break;
        }
        var listed = new HashSet<IntPtr>();
        var zones = LayoutZones(floor, listed);
        var doorClasses = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i != zones.Count; i++)
        {
            yield return 0;
            RequireGeneration();
            var (zone, inAllZones) = zones[i];
            var doorClass = Safe(() => EntranceDoorClass(zone)) ?? "unreadable";
            doorClasses.TryGetValue(doorClass, out var seen);
            doorClasses[doorClass] = seen + 1;
            foreach (var step in ZoneLayout(records, reason, tick, zone, inAllZones, i, doorClass, Math.Max(1, rowsPerRecord)))
            { yield return step; RequireGeneration(); }
        }
        var section = floorRecord.Section(CaptureSections.Layout, "LG_Floor.allZones + LG_Floor.m_dimensions[].Layers[].m_zones");
        section.Rows.Add(FloorLayoutRow(zones, doorClasses));
        FillDimensions(section, floor, listed);
        var respawners = new CaptureSnapshot(reason, "layout/respawners", tick);
        records.Add(respawners);
        foreach (var step in FillRespawners(respawners, floor)) { yield return step; RequireGeneration(); }
        // Inventory every native gate, including same-zone/internal gates. Entrance doors
        // alone cannot describe the area's graph or a door's two endpoints.
        CaptureSnapshot? gateRecord = null;
        var gateRows = 0;
        var totalGateRows = 0;
        foreach (var gate in CaptureDoorTopologyNative.GateRows(floor))
        {
            yield return 0;
            RequireGeneration();
            if (gate == null) continue;
            if (gateRecord == null || gateRows == Math.Max(1, rowsPerRecord))
            {
                gateRecord = new CaptureSnapshot(reason, "layout/gates/" + records.Count.ToString(CultureInfo.InvariantCulture), tick);
                records.Add(gateRecord); gateRows = 0;
            }
            gateRecord.Section(CaptureSections.Layout, "native gate fields + same-generation area/course-node/portal references").Rows.Add(gate);
            gateRows++; totalGateRows++;
        }
        section.Rows.Add(new CaptureRow("gateTopologyInventory", "layout/gates", "native gate inventory")
            .Field("gateCount", totalGateRows.ToString(CultureInfo.InvariantCulture))
            .Field("sessionId", RuntimeDiagnostics.SessionId).Field("generationId", RuntimeDiagnostics.GenerationId)
            .Field("source", "floor hierarchy including inactive gates + completed BuildGateJob references"));
        yield break;
    }

    /// <summary>Every zone of the floor: the floor's own list first, then any zone a dimension layer holds that the
    /// list does not. <paramref name="listed"/> receives the pointers of the floor list's zones.</summary>
    private static List<(LG_Zone Zone, bool InAllZones)> LayoutZones(LG_Floor floor, HashSet<IntPtr> listed)
    {
        var zones = new List<(LG_Zone, bool)>();
        var seen = new HashSet<IntPtr>();
        try
        {
            var all = floor.allZones;
            if (all != null)
                for (var i = 0; i != all.Count; i++)
                {
                    var zone = all[i];
                    if (zone == null || zone.WasCollected || !seen.Add(zone.Pointer)) continue;
                    listed.Add(zone.Pointer);
                    zones.Add((zone, true));
                }
            var dimensions = floor.m_dimensions;
            if (dimensions != null)
                for (var d = 0; d != dimensions.Count; d++)
                {
                    var layers = dimensions[d]?.Layers;
                    if (layers == null) continue;
                    for (var l = 0; l != layers.Count; l++)
                    {
                        var layerZones = layers[l]?.m_zones;
                        if (layerZones == null) continue;
                        for (var z = 0; z != layerZones.Count; z++)
                        {
                            var zone = layerZones[z];
                            if (zone == null || zone.WasCollected || !seen.Add(zone.Pointer)) continue;
                            zones.Add((zone, false));
                        }
                    }
                }
        }
        catch (Exception error) { Log("layout_zones", error); }
        return zones;
    }

    private static CaptureRow FloorLayoutRow(List<(LG_Zone Zone, bool InAllZones)> zones, SortedDictionary<string, int> doorClasses)
    {
        var listed = 0;
        foreach (var entry in zones)
            if (entry.InAllZones) listed++;
        var row = new CaptureRow("floorLayout", "layout/floor", "floor layout");
        row.Field("allZones", listed.ToString(CultureInfo.InvariantCulture));
        row.Field("layoutZones", zones.Count.ToString(CultureInfo.InvariantCulture));
        row.Field("zonesOutsideAllZones", (zones.Count - listed).ToString(CultureInfo.InvariantCulture));
        // The game's area letter table, next to the letter each area row reports.
        row.Field("areaChars", Safe(() => LG_Area.m_areaChars == null ? "none" : string.Join(",", LG_Area.m_areaChars)));
        row.Field("areaCharsLength", Safe(() => LG_Area.m_areaChars == null ? "0" : LG_Area.m_areaChars.Length.ToString(CultureInfo.InvariantCulture)));
        var classes = new StringBuilder();
        foreach (var entry in doorClasses)
        {
            if (classes.Length != 0) classes.Append(';');
            classes.Append(entry.Key).Append('=').Append(entry.Value.ToString(CultureInfo.InvariantCulture));
        }
        row.Field("entranceDoorClasses", classes.ToString());
        return row;
    }

    private static void FillDimensions(CaptureSection section, LG_Floor floor, HashSet<IntPtr> listed)
    {
        Il2CppSystem.Collections.Generic.List<Dimension>? dimensions;
        try { dimensions = floor.m_dimensions; }
        catch (Exception error) { Log("layout_dimensions", error); return; }
        if (dimensions == null) return;
        for (var d = 0; d != dimensions.Count; d++)
        {
            var dimension = dimensions[d];
            if (dimension == null) continue;
            int layerCount = 0, zoneCount = 0, outside = 0;
            try
            {
                var layers = dimension.Layers;
                layerCount = layers == null ? 0 : layers.Count;
                for (var l = 0; l != layerCount; l++)
                {
                    var layerZones = layers![l]?.m_zones;
                    if (layerZones == null) continue;
                    for (var z = 0; z != layerZones.Count; z++)
                    {
                        var zone = layerZones[z];
                        if (zone == null || zone.WasCollected) continue;
                        zoneCount++;
                        if (!listed.Contains(zone.Pointer)) outside++;
                    }
                }
            }
            catch (Exception error) { Log("layout_dimension", error); }
            var row = new CaptureRow("dimension", "layout/dimension/" + Safe(() => dimension.DimensionIndex.ToString(), d.ToString(CultureInfo.InvariantCulture)), "dimension");
            row.Field("dimensionIndex", Safe(() => dimension.DimensionIndex.ToString()));
            row.Field("arena", Safe(() => dimension.IsArenaDimension.ToString()));
            row.Field("main", Safe(() => dimension.IsMainDimension.ToString()));
            row.Field("layers", layerCount.ToString(CultureInfo.InvariantCulture));
            row.Field("zones", zoneCount.ToString(CultureInfo.InvariantCulture));
            row.Field("zonesNotInAllZones", outside.ToString(CultureInfo.InvariantCulture));
            section.Rows.Add(row);
        }
    }

    private static IEnumerable<int> ZoneLayout(List<CaptureSnapshot> records, string reason, long tick, LG_Zone zone, bool inAllZones, int index,
        string doorClass, int rowsPerRecord)
    {
        var id = Safe(() => zone.ID.ToString(CultureInfo.InvariantCulture), "i" + index.ToString(CultureInfo.InvariantCulture));
        var alias = Safe(() => zone.AliasName);
        Il2CppSystem.Collections.Generic.List<LG_Area>? areas = null;
        Il2CppSystem.Collections.Generic.List<AIG_CourseNode>? nodes = null;
        try
        {
            areas = zone.m_areas;
            nodes = zone.m_courseNodes;
        }
        catch (Exception error) { Log("layout_zone_lists", error); }
        var areaCount = areas == null ? 0 : areas.Count;
        var nodeCount = nodes == null ? 0 : nodes.Count;
        // The two lists are compared index by index, so the rows run to the longer one and a missing partner is
        // written as none rather than skipped.
        var pairs = Math.Max(areaCount, nodeCount);
        var parts = Math.Max(1, (pairs + rowsPerRecord - 1) / rowsPerRecord);
        for (var part = 0; part != parts; part++)
        {
            yield return 0;
            var scope = part == 0 ? "layout/zone/" + id : "layout/zone/" + id + "#" + part.ToString(CultureInfo.InvariantCulture);
            var record = new CaptureSnapshot(reason, scope, tick);
            if (part == 0)
                record.Section(CaptureSections.Layout, "LG_Zone + LG_ZoneSettings.m_zoneData")
                    .Rows.Add(ZoneLayoutRow(zone, id, inAllZones, doorClass, areas, nodes, areaCount, nodeCount));
            var section = record.Section(CaptureSections.Areas, "LG_Zone.m_areas[i] beside LG_Zone.m_courseNodes[i]");
            var end = Math.Min(pairs, (part + 1) * rowsPerRecord);
            for (var i = part * rowsPerRecord; i < end; i++)
            {
                yield return 0;
                var area = areas != null && i < areaCount ? areas[i] : null;
                var node = nodes != null && i < nodeCount ? nodes[i] : null;
                var row = AreaRow(id, i, area, node);
                row.Zone = alias;
                section.Rows.Add(row);
            }
            if (parts > 1)
                record.Notes.Add("areas: part " + (part + 1).ToString(CultureInfo.InvariantCulture) + " of " + parts.ToString(CultureInfo.InvariantCulture)
                    + ", " + pairs.ToString(CultureInfo.InvariantCulture) + " index pairs in the zone");
            records.Add(record);
        }
    }

    private static CaptureRow ZoneLayoutRow(LG_Zone zone, string id, bool inAllZones, string doorClass,
        Il2CppSystem.Collections.Generic.List<LG_Area>? areas, Il2CppSystem.Collections.Generic.List<AIG_CourseNode>? nodes,
        int areaCount, int nodeCount)
    {
        var row = new CaptureRow("zoneLayout", "zone/" + id, Safe(() => zone.AliasName) ?? "");
        row.Field("inAllZones", inAllZones.ToString());
        row.Field("localIndex", Safe(() => zone.LocalIndex.ToString()));
        row.Field("alias", Safe(() => zone.Alias.ToString(CultureInfo.InvariantCulture)));
        row.Field("layer", Safe(() => zone.Layer == null ? null : zone.Layer.m_type.ToString()));
        row.Field("dimension", Safe(() => zone.DimensionIndex.ToString()));
        // The name the game built, read through NavInfo, the public accessor of the private m_navInfo, beside the
        // data block overrides that asked for it.
        row.Field("navUid", Safe(() => zone.NavInfo == null ? "none" : zone.NavInfo.UID.ToString(CultureInfo.InvariantCulture)));
        row.Field("navNumber", Safe(() => zone.NavInfo == null ? "none" : zone.NavInfo.Number.ToString(CultureInfo.InvariantCulture)));
        row.Field("navUseNumber", Safe(() => zone.NavInfo == null ? "none" : zone.NavInfo.UseNumber.ToString()));
        row.Field("navPrefixLong", Safe(() => zone.NavInfo == null ? "none" : zone.NavInfo.PrefixLong));
        row.Field("navPrefixShort", Safe(() => zone.NavInfo == null ? "none" : zone.NavInfo.PrefixShort));
        row.Field("navSuffix", Safe(() => zone.NavInfo == null ? "none" : zone.NavInfo.Suffix));
        var data = ZoneData(zone);
        row.Field("aliasOverride", Safe(() => data == null ? "none" : data.AliasOverride.ToString(CultureInfo.InvariantCulture)));
        row.Field("overrideAliasPrefix", Safe(() => data == null ? "none" : data.OverrideAliasPrefix.ToString()));
        row.Field("aliasPrefixOverride", Safe(() => data == null ? "none" : data.AliasPrefixOverride));
        row.Field("aliasPrefixShortOverride", Safe(() => data == null ? "none" : data.AliasPrefixShortOverride));
        // The coverage the zone was built to, beside the data block range it was asked for and the areas' own sums,
        // so the unit of each number is read off one row.
        row.Field("coverage", Safe(() => zone.m_coverage.ToString(CultureInfo.InvariantCulture)));
        row.Field("minCoverage", Safe(() => zone.m_settings == null ? "none" : zone.m_settings.m_minCoverage.ToString(CultureInfo.InvariantCulture)));
        row.Field("maxCoverage", Safe(() => zone.m_settings == null ? "none" : zone.m_settings.m_maxCoverage.ToString(CultureInfo.InvariantCulture)));
        row.Field("coverageMinMax", Safe(() => data == null ? "none"
            : data.CoverageMinMax.x.ToString(CultureInfo.InvariantCulture) + "," + data.CoverageMinMax.y.ToString(CultureInfo.InvariantCulture)));
        row.Field("areaVoxelCoverageSum", Safe(() => VoxelCoverageSum(areas)));
        row.Field("terminalPlacements", Safe(() => data == null ? "none" : data.TerminalPlacements == null ? "0" : data.TerminalPlacements.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("specificTerminalSpawns", Safe(() => data == null ? "none" : data.SpecificTerminalSpawnDatas == null ? "0" : data.SpecificTerminalSpawnDatas.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("terminalCount", Safe(() => zone.TerminalsSpawnedInZone == null ? "none" : zone.TerminalsSpawnedInZone.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("terminalOrder", Safe(() => TerminalOrder(zone)));
        row.Field("staticSpawns", Safe(() => data == null ? "none" : StaticSpawns(data)));
        row.Field("entranceGateType", Safe(() => zone.m_sourceGate == null ? "none" : zone.m_sourceGate.Type.ToString()));
        row.Field("entranceDoorClass", doorClass);
        row.Field("entranceDoorType", Safe(() => EntranceDoor(zone) is { } door ? door.DoorType.ToString() : "none"));
        row.Field("entranceDoorPointer", Safe(() => EntranceDoor(zone) is { } door ? "0x" + door.Pointer.ToString("X") : "none"));
        row.Field("areas", areaCount.ToString(CultureInfo.InvariantCulture));
        row.Field("courseNodes", nodeCount.ToString(CultureInfo.InvariantCulture));
        row.Field("pairedCount", Safe(() => PairedCount(areas, nodes)));
        row.Field("geomorphs", Safe(() => GeomorphCount(areas)));
        return row;
    }

    private static CaptureRow AreaRow(string zoneId, int index, LG_Area? area, AIG_CourseNode? node)
    {
        var row = new CaptureRow("zoneArea", "zone/" + zoneId + "/area/" + index.ToString(CultureInfo.InvariantCulture),
            area == null ? "none" : Safe(() => area.name) ?? "area");
        row.Field("index", index.ToString(CultureInfo.InvariantCulture));
        row.Field("areaUid", Safe(() => area == null ? "none" : area.UID.ToString(CultureInfo.InvariantCulture)));
        row.Field("letter", Safe(() => area == null || area.m_navInfo == null ? "none" : area.m_navInfo.Suffix));
        row.Field("navPrefixLong", Safe(() => area == null || area.m_navInfo == null ? "none" : area.m_navInfo.PrefixLong));
        row.Field("navNumber", Safe(() => area == null || area.m_navInfo == null ? "none" : area.m_navInfo.Number.ToString(CultureInfo.InvariantCulture)));
        row.Field("size", Safe(() => area == null ? "none" : area.m_size.ToString()));
        row.Field("geoArea", Safe(() => area == null ? "none" : area.m_geoArea));
        row.Field("geomorph", Safe(() => area == null || area.m_geomorph == null ? "none" : area.m_geomorph.name));
        row.Field("voxelCoverage", Safe(() => area == null ? "none" : area.VoxelCoverage.ToString(CultureInfo.InvariantCulture)));
        row.Field("areaNode", Safe(() => area == null || area.m_courseNode == null ? "none" : area.m_courseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("zoneNode", Safe(() => node == null ? "none" : node.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("zoneNodeAreaUid", Safe(() => node == null || node.m_area == null ? "none" : node.m_area.UID.ToString(CultureInfo.InvariantCulture)));
        row.Field("paired", Safe(() => Paired(area, node).ToString()));
        CaptureDoorTopologyNative.AppendArea(row, area, node);
        return row;
    }

    /// <summary>Whether the two lists agree at one index in both directions: the area's node is the zone's node at
    /// that index, and that node's area is the area.</summary>
    private static bool Paired(LG_Area? area, AIG_CourseNode? node)
        => area != null && node != null && area.m_courseNode != null && node.m_area != null
            && area.m_courseNode.Pointer == node.Pointer && node.m_area.Pointer == area.Pointer;

    private static string PairedCount(Il2CppSystem.Collections.Generic.List<LG_Area>? areas, Il2CppSystem.Collections.Generic.List<AIG_CourseNode>? nodes)
    {
        if (areas == null || nodes == null) return "0";
        var count = 0;
        for (var i = 0; i < areas.Count && i < nodes.Count; i++)
            if (Paired(areas[i], nodes[i])) count++;
        return count.ToString(CultureInfo.InvariantCulture);
    }

    private static string VoxelCoverageSum(Il2CppSystem.Collections.Generic.List<LG_Area>? areas)
    {
        if (areas == null) return "none";
        var sum = 0f;
        for (var i = 0; i != areas.Count; i++)
        {
            var area = areas[i];
            if (area != null && !area.WasCollected) sum += area.VoxelCoverage;
        }
        return sum.ToString(CultureInfo.InvariantCulture);
    }

    private static string GeomorphCount(Il2CppSystem.Collections.Generic.List<LG_Area>? areas)
    {
        if (areas == null) return "none";
        var geomorphs = new HashSet<IntPtr>();
        for (var i = 0; i != areas.Count; i++)
        {
            var area = areas[i];
            if (area != null && !area.WasCollected && area.m_geomorph != null) geomorphs.Add(area.m_geomorph.Pointer);
        }
        return geomorphs.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static GameData.ExpeditionZoneData? ZoneData(LG_Zone zone)
    {
        try { return zone.m_settings == null ? null : zone.m_settings.m_zoneData; }
        catch (Exception error) { Log("layout_zone_data", error); return null; }
    }

    /// <summary>The zone's terminals in the zone list's own order, as index:SyncID/serial/key, with /reactor on a
    /// terminal bound to a reactor. The order is the question, so it is written as one ordered text.</summary>
    private static string? TerminalOrder(LG_Zone zone)
    {
        var terminals = zone.TerminalsSpawnedInZone;
        if (terminals == null) return null;
        var text = new StringBuilder();
        var shown = Math.Min(terminals.Count, MaxTerminalOrder);
        for (var i = 0; i != shown; i++)
        {
            if (i != 0) text.Append(';');
            text.Append(i.ToString(CultureInfo.InvariantCulture)).Append(':');
            var terminal = terminals[i];
            if (terminal == null || terminal.WasCollected)
            {
                text.Append("null");
                continue;
            }
            text.Append(terminal.SyncID.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(terminal.m_serialNumber.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(terminal.ItemKey);
            if (terminal.ConnectedReactor != null) text.Append("/reactor");
        }
        if (terminals.Count > shown) text.Append(";+").Append((terminals.Count - shown).ToString(CultureInfo.InvariantCulture));
        return text.Length == 0 ? "empty" : text.ToString();
    }

    /// <summary>The zone's static spawn requests as id:count:weight, in the data block's order.</summary>
    private static string StaticSpawns(GameData.ExpeditionZoneData data)
    {
        var containers = data.StaticSpawnDataContainers;
        if (containers == null) return "none";
        var text = new StringBuilder();
        var shown = Math.Min(containers.Count, MaxStaticSpawns);
        for (var i = 0; i != shown; i++)
        {
            if (i != 0) text.Append(';');
            var container = containers[i];
            if (container == null)
            {
                text.Append("null");
                continue;
            }
            text.Append(container.StaticSpawnDataId.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(container.Count.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(container.DistributionWeightType.ToString());
        }
        if (containers.Count > shown) text.Append(";+").Append((containers.Count - shown).ToString(CultureInfo.InvariantCulture));
        return text.Length == 0 ? "empty" : text.ToString();
    }

    private static iLG_Door_Core? EntranceDoor(LG_Zone zone)
    {
        var gate = zone.m_sourceGate;
        if (gate == null) return null;
        var door = gate.SpawnedDoor;
        return door == null || door.WasCollected ? null : door;
    }

    /// <summary>The native class of the zone's entrance door. The gate hands the door out as an interface, so only
    /// the IL2CPP class says whether it is a weak door, a security door or something else.</summary>
    private static string EntranceDoorClass(LG_Zone zone)
        => EntranceDoor(zone) is { } door ? NativeClass(door) : zone.m_sourceGate == null ? "no-gate" : "no-door";

    private sealed class RespawnerGroup
    {
        internal RespawnerGroup(string zone, string name, string smallSound, string scale)
        {
            Zone = zone;
            Name = name;
            SmallSound = smallSound;
            Scale = scale;
        }

        internal string Zone { get; }
        internal string Name { get; }
        internal string SmallSound { get; }
        internal string Scale { get; }
        internal int Count { get; set; }
        internal List<string> Parents { get; } = new();
        internal bool ParentsTruncated { get; set; }
    }

    /// <summary>
    /// The respawner sacks the level placed, counted by prefab name, zone, sound size and world scale. They are found
    /// by name under the floor, which includes inactive ones. The active scene's EnemyRespawnerVisual components are
    /// counted beside them, so a sack whose name does not match still shows up as a difference rather than as nothing.
    /// </summary>
    private static IEnumerable<int> FillRespawners(CaptureSnapshot record, LG_Floor floor)
    {
        var section = record.Section(CaptureSections.Respawners,
            "LG_Floor hierarchy: GameObjects named respawner* + Object.FindObjectsOfType<EnemyRespawnerVisual>");
        var groups = new Dictionary<string, RespawnerGroup>(StringComparer.Ordinal);
        var matchedVisuals = new HashSet<IntPtr>();
        var noZones = new Dictionary<int, LG_Zone>();
        var instances = 0;
        {
            foreach (var component in ForgeCore.GameFloor.Components(floor.transform))
            {
                yield return 0;
                if (component == null || component.WasCollected) continue;
                var transform = component.TryCast<Transform>();
                if (transform == null || !IsRespawnerRoot(transform)) continue;
                instances++;
                var visual = transform.GetComponent<EnemyRespawnerVisual>();
                if (visual != null) matchedVisuals.Add(visual.Pointer);
                var zone = ZoneOf(transform, noZones);
                var zoneName = zone == null ? "none" : Safe(() => zone.AliasName, "unreadable");
                var name = PrefabName(transform.name);
                var smallSound = visual == null ? "no-visual" : Safe(() => visual.m_playSmallSound.ToString(), "unreadable");
                var scale = Safe(() => Vec(transform.lossyScale), "unreadable");
                var key = zoneName + "|" + name + "|" + smallSound + "|" + scale;
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = new RespawnerGroup(zoneName, name, smallSound, scale);
                group.Count++;
                var parent = transform.parent == null ? "none" : PrefabName(transform.parent.name);
                if (group.Parents.Contains(parent)) continue;
                if (group.Parents.Count < MaxRespawnerParents) group.Parents.Add(parent);
                else group.ParentsTruncated = true;
            }
        }
        var ordered = new List<RespawnerGroup>(groups.Values);
        ordered.Sort((left, right) =>
        {
            var order = string.CompareOrdinal(left.Zone, right.Zone);
            if (order == 0) order = string.CompareOrdinal(left.Name, right.Name);
            if (order == 0) order = string.CompareOrdinal(left.Scale, right.Scale);
            return order == 0 ? string.CompareOrdinal(left.SmallSound, right.SmallSound) : order;
        });
        section.Rows.Add(RespawnerSummary(instances, ordered.Count, matchedVisuals));
        var unlisted = 0;
        for (var i = 0; i != ordered.Count; i++)
        {
            yield return 0;
            var group = ordered[i];
            if (i >= MaxRespawnerGroups)
            {
                unlisted += group.Count;
                continue;
            }
            var row = new CaptureRow("respawnerGroup", "respawners/" + i.ToString(CultureInfo.InvariantCulture), group.Name);
            row.Zone = group.Zone;
            row.Field("prefab", group.Name);
            row.Field("smallSound", group.SmallSound);
            row.Field("scale", group.Scale);
            row.Field("count", group.Count.ToString(CultureInfo.InvariantCulture));
            row.Field("parents", string.Join(";", group.Parents) + (group.ParentsTruncated ? ";+" : ""));
            section.Rows.Add(row);
        }
        if (ordered.Count > MaxRespawnerGroups)
            record.Notes.Add("respawners: " + ordered.Count.ToString(CultureInfo.InvariantCulture) + " groups, capped at "
                + MaxRespawnerGroups.ToString(CultureInfo.InvariantCulture) + "; " + unlisted.ToString(CultureInfo.InvariantCulture)
                + " instances are in the groups not listed");

    }

    private static CaptureRow RespawnerSummary(int instances, int groups, HashSet<IntPtr> matchedVisuals)
    {
        var row = new CaptureRow("respawnerSummary", "respawners/summary", "respawner inventory");
        row.Field("instances", instances.ToString(CultureInfo.InvariantCulture));
        row.Field("groups", groups.ToString(CultureInfo.InvariantCulture));
        var visuals = Objects<EnemyRespawnerVisual>();
        row.Field("sceneVisuals", visuals == null ? "unreadable" : visuals.Length.ToString(CultureInfo.InvariantCulture));
        var unmatched = 0;
        var names = new List<string>();
        if (visuals != null)
            foreach (var visual in visuals)
            {
                if (visual == null || visual.WasCollected || matchedVisuals.Contains(visual.Pointer)) continue;
                unmatched++;
                var name = Safe(() => PrefabName(visual.gameObject.name), "unreadable");
                if (names.Count < MaxUnmatchedVisualNames && !names.Contains(name)) names.Add(name);
            }
        row.Field("sceneVisualsNotMatchedByName", visuals == null ? "unreadable" : unmatched.ToString(CultureInfo.InvariantCulture));
        row.Field("unmatchedVisualNames", string.Join(";", names));
        return row;
    }

    /// <summary>A sack is recognised by its prefab name, which is what the test point counts by. A child whose name
    /// starts the same way is part of the same sack, not a second one.</summary>
    private static bool IsRespawnerRoot(Transform transform)
    {
        if (!PrefabName(transform.name).StartsWith("respawner", StringComparison.OrdinalIgnoreCase)) return false;
        var parent = transform.parent;
        return parent == null || !PrefabName(parent.name).StartsWith("respawner", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrefabName(string? name)
    {
        const string Clone = "(Clone)";
        if (string.IsNullOrEmpty(name)) return "";
        var trimmed = name.Trim();
        while (trimmed.EndsWith(Clone, StringComparison.Ordinal)) trimmed = trimmed[..^Clone.Length].TrimEnd();
        return trimmed;
    }
}
