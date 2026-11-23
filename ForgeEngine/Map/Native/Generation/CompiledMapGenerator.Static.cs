using System;
using System.Collections.Generic;
using System.Linq;
using ForgeMap.Generation;
using GameData;
using LevelGeneration;
using static ForgeMap.Generation.GenerationProgram;
using ForgeCore;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    private static void ValidateStaticRequests(GenerationProgram program)
    {
        foreach (var dimension in program.Dimensions)
        {
            if (!dimension.Enabled || dimension.Mode != DimensionKind.Static) continue;
            if (dimension.NativeAddress == 0 || dimension.DimensionBlock == null)
                throw new GenerationRefusal("gen-dimension-unsupported", dimension.Id + "/static: Reality is bootstrapped by LG_SetupFloor, not the extra-dimension static constructor");
            if (dimension.Zones.Length != 1)
                throw new GenerationRefusal("gen-static-zone-unsupported", dimension.Id + "/zones: LG_ZoneJob_CreateZoneForStaticLevel creates one native zone for the StaticLevel");
            var zone = program.Zones[dimension.Zones[0]];
            if (zone.Layer != 0 || zone.LocalIndex != 0 || zone.Source != null)
                throw new GenerationRefusal("gen-static-zone-unsupported", zone.Id + "/address: the native static constructor adds the sole zone to the main layer at local index zero");
            if (zone.Rooms.Length != 1)
                throw new GenerationRefusal("gen-static-room-unsupported", zone.Id + "/rooms: this generator registers the one native StaticLevel root; additional static rooms are not implemented");
            var room = program.Rooms[zone.Rooms[0]];
            if (dimension.RootRoom != room.Index || zone.RootRoom != room.Index || room.RootRole != RootKind.Dimension || room.ParentConnection != null)
                throw new GenerationRefusal("gen-static-room-unsupported", room.Id + "/root");
            foreach (var connection in program.Connections)
                if (connection.FromRoom == room.Index || connection.ToRoom == room.Index)
                    throw new GenerationRefusal("gen-static-room-unsupported", room.Id + "/connections: a one-root static zone has no native room-expansion edge");
            if (zone.ExpansionCount is { } expansions && expansions != 0)
                throw new GenerationRefusal("gen-static-room-unsupported", zone.Id + "/expansionCount: the native static root performs zero room expansions");
            if (room.Placement.Present && (room.Placement.X != 0 || room.Placement.Z != 0
                || room.Placement.OffsetX != 0 || room.Placement.OffsetZ != 0))
                throw new GenerationRefusal("gen-static-room-unsupported", room.Id + "/placement: the StaticLevel owns the complete dimension grid and its authored root marker must be the origin");
            if (room.Height is { } height && Math.Abs(height) > .0001f)
                throw new GenerationRefusal("gen-static-room-unsupported", room.Id + "/height: the native StaticLevel root is fixed to Dimension.GroundY");
            if (program.Resources[room.Resource].Category != "custom-geomorph")
                throw new GenerationRefusal("gen-static-room-unsupported", room.Id + "/resource: DimensionGeomorph is resolved only through ComplexResourceSet.GetCustomGeomorph");
        }
    }

    private static void ApplyStaticInputs(Run run)
    {
        foreach (int dimensionIndex in run.LevelProgram.Dimensions)
        {
            var dimension = run.Program.Dimensions[dimensionIndex];
            if (!dimension.Enabled || dimension.Mode != DimensionKind.Static) continue;
            var block = (DimensionDataBlock)NativeDataBlockTables.RequireBlock("Dimension",
                dimension.DimensionBlock ?? throw new GenerationRefusal("gen-reference-missing", dimension.Id + "/dimensionBlock"));
            var data = block.DimensionData ?? throw new GenerationRefusal("gen-reference-missing", dimension.Id + "/dimensionBlock");
            RequireResourceSetId(data.DimensionResourceSetID, dimension.ResourceSet, dimension.Id + "/resourceSet-input", "DimensionData.DimensionResourceSetID");
            var zone = run.Program.Zones[dimension.Zones[0]]; var room = run.Program.Rooms[dimension.RootRoom];
            string geomorph = run.Program.Resources[room.Resource].PrefabPath;
            bool originalStatic = data.IsStaticDimension; string originalGeomorph = data.DimensionGeomorph;
            int originalZone = data.StaticZoneSeed, originalMarker = data.StaticMarkerSeed, originalLight = data.StaticLightSeed;
            int nextZone = unchecked((int)zone.ZoneSeed.Apply(unchecked((uint)originalZone))),
                nextMarker = unchecked((int)zone.MarkerSeed.Apply(unchecked((uint)originalMarker))),
                nextLight = unchecked((int)zone.LightSeed.Apply(unchecked((uint)originalLight)));
            run.RestoreInputs.Add(() =>
            {
                if (data.IsStaticDimension != true && data.IsStaticDimension != originalStatic
                    || !string.Equals(data.DimensionGeomorph, geomorph, StringComparison.Ordinal) && !string.Equals(data.DimensionGeomorph, originalGeomorph, StringComparison.Ordinal)
                    || data.StaticZoneSeed != nextZone && data.StaticZoneSeed != originalZone
                    || data.StaticMarkerSeed != nextMarker && data.StaticMarkerSeed != originalMarker
                    || data.StaticLightSeed != nextLight && data.StaticLightSeed != originalLight)
                    throw new GenerationRefusal("gen-pin-mismatch", dimension.Id + "/static-input-ownership-changed");
                data.IsStaticDimension = originalStatic; data.DimensionGeomorph = originalGeomorph;
                data.StaticZoneSeed = originalZone; data.StaticMarkerSeed = originalMarker; data.StaticLightSeed = originalLight;
            });
            data.IsStaticDimension = true; data.DimensionGeomorph = geomorph; data.StaticZoneSeed = nextZone;
            data.StaticMarkerSeed = nextMarker; data.StaticLightSeed = nextLight;
            run.Record("static-inputs-before-dimension", dimension.Id, new
            {
                nativeAddress = dimension.NativeAddress,
                prefab = geomorph,
                zoneSeed = unchecked((uint)nextZone),
                markerSeed = unchecked((uint)nextMarker),
                lightSeed = unchecked((uint)nextLight),
                beforeCreateAllDimensions = true
            });
        }
    }

    private static void StaticZonesInjected(LG_Floor __instance, eDimensionIndex __0)
    {
        if (!Live(out var run) || run.Failed) return;
        try
        {
            if (run.Program.DimensionAt(run.LevelProgram, (int)__0) is not { } dimensionIndex) return;
            var definition = run.Program.Dimensions[dimensionIndex];
            if (!definition.Enabled || definition.Mode != DimensionKind.Static) return;
            if (!__instance.GetDimension(__0, out var dimension) || dimension == null || dimension.StaticLevel == null)
                throw new GenerationRefusal("gen-dimension-mismatch", definition.Id + "/static-readback");
            var dimensionData = dimension.DimensionData ?? throw new GenerationRefusal("gen-dimension-mismatch", definition.Id + "/static-data-readback");
            if (!dimensionData.IsStaticDimension || dimension.IsArenaDimension)
                throw new GenerationRefusal("gen-dimension-mismatch", definition.Id + "/static-flags");
            var resources = RequireDimensionResources(dimension, definition.ResourceSet, definition.Id + "/resourceSet-readback");
            var grid = dimension.Grid;
            if (grid.m_sizeX != definition.Grid.Size || grid.m_sizeZ != definition.Grid.Size || grid.m_cellDim != definition.Grid.CellSize)
                throw new GenerationRefusal("gen-dimension-mismatch", definition.Id + "/grid-readback");
            var zoneDefinition = run.Program.Zones[definition.Zones[0]]; int zoneIndex = zoneDefinition.Index;
            var nativeZones = __instance.allZones ?? throw new GenerationRefusal("gen-reference-missing", zoneDefinition.Id + "/native-zone");
            LG_Zone? zone = null; int matches = 0;
            foreach (var candidate in nativeZones)
                if (candidate != null && (int)candidate.DimensionIndex == (int)__0 && (int)candidate.Layer.m_type == zoneDefinition.Layer && (int)candidate.LocalIndex == zoneDefinition.LocalIndex)
                { zone = candidate; matches++; }
            if (matches != 1 || zone == null) throw new GenerationRefusal("gen-reference-missing", zoneDefinition.Id + "/native-zone");
            var root = dimension.StaticLevel; root.m_zone = zone; int roomIndex = definition.RootRoom;
            var room = run.Program.Rooms[roomIndex]; ResolveAssets(run); var asset = Asset(run, room.Resource);
            if (root.m_geoPrefab == null || root.m_geoPrefab.Pointer != asset.Pointer)
                throw new GenerationRefusal("gen-pin-mismatch", room.Id + "/static-prefab-readback");
            var originalAreas = root.m_areas?.ToArray() ?? throw new GenerationRefusal("gen-area-identity", room.Id);
            if (originalAreas.Length != run.Program.Resources[room.Resource].Areas.Length) throw new GenerationRefusal("gen-area-identity", room.Id);
            var seeds = new uint[originalAreas.Length]; for (int i = 0; i < seeds.Length; i++) seeds[i] = originalAreas[i].AreaSeed;
            var rules = run.Program.Areas(roomIndex, seeds, room.Resource);
            var ordered = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LG_Area>(originalAreas.Length);
            var uniqueAreas = new HashSet<IntPtr>();
            for (int i = 0; i < ordered.Length; i++)
            {
                int source = rules.Order[i]; var area = originalAreas[source];
                if (area == null || !uniqueAreas.Add(area.Pointer) || area.m_zone == null || area.m_zone.Pointer != zone.Pointer || area.AreaSeed != rules.Seeds[source])
                    throw new GenerationRefusal("gen-area-identity", room.Id + "/static-area-readback");
                ordered[i] = area;
            }
            var zoneAreas = zone.m_areas ?? throw new GenerationRefusal("gen-area-identity", room.Id + "/native-zone-areas");
            var nativeAreas = new HashSet<IntPtr>(); foreach (var area in zoneAreas)
                if (area == null || !nativeAreas.Add(area.Pointer)) throw new GenerationRefusal("gen-area-identity", room.Id + "/native-zone-areas");
            if (!nativeAreas.SetEquals(originalAreas.Select(area => area.Pointer))) throw new GenerationRefusal("gen-area-identity", room.Id + "/native-zone-areas");
            root.m_areas = ordered; zoneAreas.Clear(); foreach (var area in ordered) zoneAreas.Add(area);
            float coverage = 0; foreach (var area in zoneAreas) coverage += area.GetCoverage(); zone.m_coverage = coverage;
            var settings = zone.m_settings ?? throw new GenerationRefusal("gen-stage-invalid", zoneDefinition.Id + "/zone-settings");
            settings.m_minCoverage = zoneDefinition.CoverageMinimum; settings.m_maxCoverage = zoneDefinition.CoverageMaximum;
            if (zone.m_subSeed != unchecked((uint)dimensionData.StaticZoneSeed) || zone.m_markerSubSeed != unchecked((uint)dimensionData.StaticMarkerSeed)
                || zone.m_lightsSubSeed != unchecked((uint)dimensionData.StaticLightSeed))
                throw new GenerationRefusal("gen-pin-mismatch", zoneDefinition.Id + "/constructor-seed-readback");
            var tile = root.m_tile ?? throw new GenerationRefusal("gen-root-invalid", room.Id);
            var cells = new CellCoordinate[checked(grid.m_sizeX * grid.m_sizeZ)]; int cellIndex = 0;
            for (int x = 0; x < grid.m_sizeX; x++) for (int z = 0; z < grid.m_sizeZ; z++) cells[cellIndex++] = new(x, z);
            RepairRegistration(run, roomIndex, root, cells);
            int gridRegistration = 0; for (int i = grid.m_groupings.Count - 1; i >= 0; i--)
                if (grid.m_groupings[i] != null && grid.m_groupings[i].Pointer == tile.Pointer) { gridRegistration++; if (gridRegistration > 1) grid.m_groupings.RemoveAt(i); }
            if (gridRegistration == 0) grid.m_groupings.Add(tile);
            int dimensionRegistration = 0; for (int i = dimension.Tiles.Count - 1; i >= 0; i--)
                if (dimension.Tiles[i] != null && dimension.Tiles[i].Pointer == tile.Pointer) { dimensionRegistration++; if (dimensionRegistration > 1) dimension.Tiles.RemoveAt(i); }
            if (dimensionRegistration == 0) dimension.Tiles.Add(tile);
            double rotation = Yaw(root.transform.rotation);
            if (!RoomYaw.Same(run.Program.Rotation(roomIndex, rotation, run.Seed, _ => throw new GenerationRefusal("gen-static-room-unsupported", room.Id + "/rotation/sourceRoom")), rotation))
                throw new GenerationRefusal("gen-static-room-unsupported", room.Id + "/rotation-readback");
            if (room.Height is { } height && Math.Abs(root.transform.position.y - dimension.GroundY - height) > .01f)
                throw new GenerationRefusal("gen-static-room-unsupported", room.Id + "/height-readback");
            var placed = new Placed(room.Resource, root, cells[0], rotation, cells, originalAreas);
            run.Rooms[roomIndex] = placed; run.Zones[zoneIndex] = zone; run.EntryRooms[zoneIndex] = roomIndex; run.ExpansionCalls[zoneIndex] = 0;
            if (run.RegisteredZones[zoneIndex]) throw new GenerationRefusal("gen-stage-invalid", zoneDefinition.Id + "/duplicate-area-registration");
            run.RegisteredZones[zoneIndex] = true;
            if (!run.ReadDimensions[dimensionIndex])
            {
                run.ReadDimensions[dimensionIndex] = true; run.Record("dimensions", definition.Id, new
                {
                    nativeAddress = (int)__0,
                    sizeX = grid.m_sizeX,
                    sizeZ = grid.m_sizeZ,
                    cellSize = grid.m_cellDim,
                    groundY = dimension.GroundY,
                    resourceSet = resources.persistentID,
                    mode = "static",
                    arena = dimension.IsArenaDimension,
                    staticLevel = dimension.StaticLevel != null,
                    fullGridCells = cells.Length
                });
            }
            VerifyCells(run, roomIndex, placed);
            var locator = new RoomLocator(zoneDefinition.Id, (int)zone.DimensionIndex, (int)zone.Layer.m_type, (int)zone.LocalIndex,
                room.Author.Id, room.Author.Revision, run.Program.Resources[room.Resource].PrefabPath, room.Id);
            GeneratedRoomRegistry.Add(zone, locator, root); run.Locators[roomIndex] = locator;
            var authorResource = run.Program.Resources[room.SourceResource];
            var areaBindings = SelectAuthorAreaBindings(authorResource, room.AreaMap, originalAreas, root.m_areas.ToArray(), room.Id);
            var exitBindings = new GeneratedRoomRegistry.GeneratedExitBinding[authorResource.Exits.Length];
            for (int sourceIndex = 0; sourceIndex < exitBindings.Length; sourceIndex++)
                exitBindings[sourceIndex] = new(authorResource.Exits[sourceIndex].Id, PlugOnRoot(run, roomIndex, room.Resource, root, sourceIndex));
            GeneratedRoomRegistry.BindGeneratedRoom(run.Epoch, run.LevelReference, zone, locator, root, areaBindings, exitBindings);
            var authorAreas = AuthorAreaReadback(run, zone, locator, areaBindings);
            run.AuthorAreaReadbacks[roomIndex] = authorAreas;
            var authorExits = AuthorExitReadback(run, zone, locator, exitBindings);
            run.Program.VerifyStop(zoneIndex, zoneAreas.Count, coverage, zoneDefinition.CoverageMinimum, zoneDefinition.CoverageMaximum, 0);
            run.Record("rooms", room.Id, new
            {
                nativeRoot = root.Pointer.ToInt64(),
                dimension = (int)__0,
                layer = (int)zone.Layer.m_type,
                localZone = (int)zone.LocalIndex,
                prefab = run.Program.Resources[room.Resource].PrefabPath,
                prefabName = asset.name,
                x = root.transform.position.x,
                authorRoomId = locator.RoomId,
                authorRoomRevision = locator.Revision,
                authorRoomInstance = locator.InstanceId,
                authorAreas,
                authorExits,
                y = root.transform.position.y,
                z = root.transform.position.z,
                yaw = rotation,
                cells = cells.Select(cell => new { x = cell.X, z = cell.Z }).ToArray(),
                areas = root.m_areas.Length,
                areaSeeds = root.m_areas.Select(area => area.AreaSeed).ToArray(),
                areaNames = root.m_areas.Select(area => area.name).ToArray(),
                staticRoot = true
            });
            run.Record("areas", zoneDefinition.Id, new
            {
                dimension = (int)__0,
                layer = (int)zone.Layer.m_type,
                localZone = (int)zone.LocalIndex,
                areas = zoneAreas.Count,
                coverage,
                expansions = 0,
                minimum = settings.m_minCoverage,
                maximum = settings.m_maxCoverage,
                zoneSeed = zone.m_subSeed,
                markerSeed = zone.m_markerSubSeed,
                lightSeed = zone.m_lightsSubSeed,
                staticRoot = true
            });
            run.Record("root-only", zoneDefinition.Id, new { room = room.Id, expansions = 0, staticRoot = true });
        }
        catch (Exception error) { Refuse(run, error); }
    }
}
