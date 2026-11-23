using System;
using System.Collections.Generic;
using System.Linq;
using ForgeMap.Generation;
using ForgeEngine.Framework;
using GameData;
using Expedition;
using LevelGeneration;
using LevelGeneration.Core;
using UnityEngine;
using static ForgeMap.Generation.GenerationProgram;
using ForgeCore;
using Object = UnityEngine.Object;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    private sealed record PreparedRoom(int Index, int Resource, GameObject Prefab, CellCoordinate Anchor,
        double Rotation, CellCoordinate[] Cells, Vector3 Position, LG_Geomorph? Existing);

    private static void ResolveAssets(Run run)
    {
        if (run.AssetsResolved) return;
        foreach (int resource in run.LevelProgram.RequiredResources)
        {
            var row = run.Program.Resources[resource];
            if (row.ForgeRoom) PrepareRecipeTemplate(row);
            try
            {
                var prefab = ForgeCore.GameAssets.Get<GameObject>(row.PrefabPath);
                run.Assets[resource] = prefab != null ? prefab : throw new GenerationRefusal("gen-resource-unloaded", row.Id);
                if (NativePlugHeights.Find(row.PrefabPath) is { } measured)
                { run.ConnectorFamilies[resource] = measured.Family; run.ConnectorRises[resource] = (float)measured.Rise; }
                if (row.Exits.Length == 0) { run.ExitAddresses[resource] = Array.Empty<int[]>(); continue; }
                var root = prefab.GetComponent<LG_Geomorph>() ?? throw new GenerationRefusal("gen-resource-missing", row.Id + "/geomorph");
                run.ExitAddresses[resource] = ResolveExitAddresses(root, row);
                run.Record("resource-exits", row.Id, new
                {
                    prefab = row.PrefabPath,
                    exits = row.Exits.Length,
                    addresses = run.ExitAddresses[resource],
                    prefabCachedPlugs = root.m_plugs?.Count ?? -1,
                    source = "prefab-hierarchy",
                    beforeSetupAreas = true
                });
            }
            catch (GenerationRefusal) { throw; }
            catch (Exception containedError) { MapLog.Contained("compiled-map-generator-rooms.resolve-assets", containedError); throw new GenerationRefusal("gen-resource-unloaded", row.Id); }
        }
        run.AssetsResolved = true;
    }
    /// <summary>A Forge room is built from its recipe once its frozen set and donor are loaded, and registered under
    /// its resource key in that set and the loaded-asset lookup, exactly as the native custom-geomorph lookup sees it.
    /// The recipe host owns construction and refusal reasons; generation stops on the first one.</summary>
    private static void PrepareRecipeTemplate(GenerationProgram.ResourceProgram row)
    {
        if (!RoomRecipeHost.IsRecipe(row.PrefabPath)) throw new GenerationRefusal("gen-resource-missing", row.Id + "/recipe");
        ComplexResourceSetDataBlock set;
        try { set = (ComplexResourceSetDataBlock)NativeDataBlockTables.RequireBlock("ComplexResourceSet", row.ComplexResourceSet); }
        catch (Exception containedError) { MapLog.Contained("compiled-map-generator-rooms.prepare-recipe-template", containedError); throw new GenerationRefusal("gen-reference-missing", row.Id + "/complexResourceSet"); }
        try { RoomRecipeHost.Prepare(set, row.PrefabPath); }
        catch (Exception error) { throw new GenerationRefusal("gen-resource-unloaded", row.Id + "/recipe", error.GetBaseException().Message); }
    }
    private static GameObject Asset(Run run, int resource)
    {
        if (!run.AssetsResolved) throw new GenerationRefusal("gen-stage-invalid", run.Program.Resources[resource].Id + "/assets-not-resolved");
        return run.Assets[resource] ?? throw new GenerationRefusal("gen-resource-unloaded", run.Program.Resources[resource].Id);
    }
    /// <summary>Yaw (degrees about +Y, [0,360)) of a native rotation, to a thousandth of a degree so a float quarter turn reads exactly.</summary>
    private static double Yaw(Quaternion rotation) => RoomYaw.Normalize(Math.Round(rotation.eulerAngles.y, 3));

    private static void PlaceZone(Run run, int zoneIndex, LG_Floor floor, LG_Zone zone, LG_Plug? source, LG_Area sourceArea, uint seed,
        LG_ZoneJob_CreateExpandFromData job)
    {
        ResolveAssets(run);
        var definition = run.Program.Zones[zoneIndex];
        var settings = zone.m_settings ?? throw new GenerationRefusal("gen-stage-invalid", definition.Id + "/zone-settings");
        var dimension = zone.Dimension; var grid = dimension.Grid; var dimensionDefinition = run.Program.Dimensions[definition.Dimension];
        if (!dimensionDefinition.Enabled || dimension.IsArenaDimension != (dimensionDefinition.Mode == DimensionKind.Arena) || dimension.StaticLevel != null)
            throw new GenerationRefusal("gen-dimension-mismatch", dimensionDefinition.Id + "/mode-readback");
        var resources = RequireDimensionResources(dimension, dimensionDefinition.ResourceSet, definition.Id + "/resourceSet-readback");
        var expectedGrid = dimensionDefinition.Grid;
        if (grid.m_sizeX != expectedGrid.Size || grid.m_sizeZ != expectedGrid.Size || grid.m_cellDim != expectedGrid.CellSize)
            throw new GenerationRefusal("gen-dimension-mismatch", definition.Id + "/grid-readback");
        if (!run.ReadDimensions[definition.Dimension])
        {
            run.ReadDimensions[definition.Dimension] = true;
            run.Record("dimensions", dimensionDefinition.Id, new
            {
                nativeAddress = (int)dimension.DimensionIndex,
                sizeX = grid.m_sizeX,
                sizeZ = grid.m_sizeZ,
                cellSize = grid.m_cellDim,
                groundY = dimension.GroundY,
                resourceSet = resources.persistentID,
                arena = dimension.IsArenaDimension,
                staticLevel = dimension.StaticLevel != null
            });
        }
        if (definition.Source is { } declared)
        {
            var parent = run.Rooms[declared.Room] ?? throw new GenerationRefusal("gen-source-unavailable", definition.Id + "/source/room");
            if (Plug(run, declared.Room, declared.Exit).Pointer != source?.Pointer)
                throw new GenerationRefusal("gen-source-unavailable", definition.Id + "/source/exit");
            int area = run.Program.Rooms[declared.Room].AreaMap[declared.Area];
            if ((uint)area >= (uint)parent.OriginalAreas.Length || parent.OriginalAreas[area].Pointer != sourceArea.Pointer)
                throw new GenerationRefusal("gen-source-unavailable", definition.Id + "/source/area");
        }
        int rootIndex = definition.RootRoom;
        // Only a zone whose root room has no door at all has no source: it grows from its native start area, and its other rooms
        // stand at their own places.
        bool closedRoot = source == null && run.Program.Rooms[rootIndex].ExitMap.Length == 0;
        if (!closedRoot && (source == null || source.m_pariedWith != null || source.IsZoneBuildBlocked()))
            throw new GenerationRefusal("gen-exit-occupied", definition.Id + "/source");

        var prepared = new PreparedRoom?[run.Program.Rooms.Length];
        var ordered = new PreparedRoom[definition.Rooms.Length];
        var reservations = new bool[checked(grid.m_sizeX * grid.m_sizeZ)];
        int preparedCount = 0;
        foreach (int index in definition.Rooms)
        {
            var row = run.Program.Rooms[index]; int resource = row.Resource; var resourceRow = run.Program.Resources[resource];
            var prefab = Asset(run, resource); var prototype = prefab.GetComponent<LG_Geomorph>();
            if (prototype == null) throw new GenerationRefusal("gen-resource-missing", row.Id + "/geomorph");
            LG_Geomorph? existing = null;
            if (index == rootIndex && row.RootRole is RootKind.Elevator or RootKind.Dimension)
            {
                var candidate = dimension.m_startTile?.m_geoRoot ?? dimension.StaticLevel;
                if (candidate?.m_geoPrefab?.Pointer == prefab.Pointer) existing = candidate;
                else throw new GenerationRefusal("gen-root-role-unsupported", row.Id + "/elevator-selection");
            }
            // The native yaw is the source door's facing; a closed zone has no source door, so its other rooms take their authored yaw.
            double nativeYaw = existing != null ? Yaw(existing.transform.rotation) : source != null ? Yaw(Quaternion.LookRotation(source.m_forward)) : 0;
            double rotation = run.Program.Rotation(index, nativeYaw, run.Seed,
                previous => prepared[previous]?.Rotation ?? run.Rooms[previous]?.Rotation
                    ?? throw new GenerationRefusal("gen-source-unavailable", row.Id + "/rotation/sourceRoom"));
            CellCoordinate[] localCells;
            if (resourceRow.Footprint.Length != 0) localCells = resourceRow.Footprint;
            else
            {
                if (!Enum.TryParse<LG_TileShapeType>(prototype.m_goShapeType.ToString(), true, out var kind) || kind is LG_TileShapeType.None or LG_TileShapeType.Custom)
                    throw new GenerationRefusal("gen-footprint-ambiguous", row.Id + "/resource/descriptor/footprint");
                var unused = Vector3.zero; var nativeShape = floor.CreateTileShape(kind, grid.m_cellDim, ref unused);
                localCells = DeclaredFootprints.Rectangle(nativeShape.m_sizeX, nativeShape.m_sizeZ);
            }
            CellCoordinate anchor;
            if (row.Placement.Present)
                anchor = new(checked(grid.m_sizeX / 2 + row.Placement.X), checked(grid.m_sizeZ / 2 + row.Placement.Z));
            else if (existing != null)
            {
                var nativeCells = existing.m_tile?.m_cells;
                if (nativeCells == null || nativeCells.Count == 0) throw new GenerationRefusal("gen-cell-missing", row.Id + "/native-root");
                var offsets = run.Program.Footprint(resource, new(0, 0), rotation); int minX = int.MaxValue, minZ = int.MaxValue;
                foreach (var cell in nativeCells) { minX = Math.Min(minX, cell.x); minZ = Math.Min(minZ, cell.z); }
                anchor = new(minX - offsets.Min(cell => cell.X), minZ - offsets.Min(cell => cell.Z));
            }
            else throw new GenerationRefusal("gen-placement-unsolved", row.Id + "/placement");

            var cells = run.Program.Footprint(resource, anchor, rotation);
            foreach (var cell in cells)
            {
                if (cell.X < 0 || cell.Z < 0 || cell.X >= grid.m_sizeX || cell.Z >= grid.m_sizeZ)
                    throw new GenerationRefusal("gen-grid-range", row.Id + "/footprint");
                var native = grid.GetCell(cell.X, cell.Z);
                int slot = checked(cell.Z * grid.m_sizeX + cell.X);
                if (native == null) throw new GenerationRefusal("gen-cell-missing", row.Id);
                if (native.m_grouping != null && native.m_grouping.Pointer != existing?.m_tile?.Pointer || reservations[slot])
                    throw new GenerationRefusal("gen-cell-owner-conflict", row.Id);
                reservations[slot] = true;
            }
            float x = (cells.Min(cell => cell.X) + cells.Max(cell => cell.X)) / 2f, z = (cells.Min(cell => cell.Z) + cells.Max(cell => cell.Z)) / 2f;
            var position = grid.GetPosition(anchor.X, anchor.Z);
            position.x += (x - anchor.X) * grid.m_cellDim + row.Placement.OffsetX;
            position.z += (z - anchor.Z) * grid.m_cellDim + row.Placement.OffsetZ;
            position.y = dimension.GroundY + (row.Height ?? 0);
            if (existing != null && (Vector3.Distance(existing.transform.position, position) > .01f || !RoomYaw.Same(Yaw(existing.transform.rotation), rotation)))
                throw new GenerationRefusal("gen-root-role-unsupported", row.Id + "/existing-root-transform");
            var request = new PreparedRoom(index, resource, prefab, anchor, rotation, cells, position, existing);
            prepared[index] = request; ordered[preparedCount++] = request;
        }

        var managedTiles = new HashSet<nint>();
        foreach (var request in ordered)
            if (request.Existing?.m_tile is { } existingTile) managedTiles.Add(existingTile.Pointer);
        var gridTiles = new HashSet<nint>();
        for (int i = grid.m_groupings.Count - 1; i >= 0; i--)
        {
            var current = grid.m_groupings[i];
            if (current != null && !gridTiles.Add(current.Pointer) && managedTiles.Contains(current.Pointer))
                grid.m_groupings.RemoveAt(i);
        }
        var dimensionTiles = new HashSet<nint>();
        for (int i = dimension.Tiles.Count - 1; i >= 0; i--)
        {
            var current = dimension.Tiles[i];
            if (current != null && !dimensionTiles.Add(current.Pointer) && managedTiles.Contains(current.Pointer))
                dimension.Tiles.RemoveAt(i);
        }
        foreach (var request in ordered)
        {
            var row = run.Program.Rooms[request.Index]; var resource = run.Program.Resources[request.Resource];
            var shape = new LG_TileShape
            {
                m_type = LG_TileShapeType.Custom,
                m_gridPosition = new LG_GridPosition { x = request.Cells.Min(cell => cell.X), z = request.Cells.Min(cell => cell.Z) },
                m_sizeX = request.Cells.Max(cell => cell.X) - request.Cells.Min(cell => cell.X) + 1,
                m_sizeZ = request.Cells.Max(cell => cell.Z) - request.Cells.Min(cell => cell.Z) + 1
            };
            var tile = request.Existing?.m_tile ?? new LG_Tile { m_shape = shape };
            if (tile.m_cells == null || request.Existing == null && tile.m_cells.Count != 0)
                throw new GenerationRefusal("gen-cell-owner-inconsistent", row.Id + "/native-tile-initialization");
            var instance = request.Existing?.gameObject ?? Object.Instantiate<GameObject>(request.Prefab, request.Position,
                Quaternion.Euler(0, (float)request.Rotation, 0), floor.transform);
            var root = request.Existing ?? instance.GetComponent<LG_Geomorph>() ?? throw new GenerationRefusal("gen-resource-missing", row.Id);
            root.m_geoPrefab = request.Prefab; root.m_zone = zone; root.m_tile = tile;
            // A sequence may swap the room for another resource, whose parts these ids do not name.
            if (resource.ForgeRoom && request.Resource == row.SourceResource && row.RemovedParts.Length != 0) RoomRecipeHost.RemoveParts(root, row.Id, resource.PrefabPath, row.RemovedParts);
            var subComplex = (SubComplex)resource.SubComplex;
            if (!Enum.IsDefined(typeof(SubComplex), subComplex)) throw new GenerationRefusal("gen-resource-subcomplex", row.Id);
            root.m_assignedSubComplexFromZone = subComplex; tile.m_geoRoot = root;
            if (request.Existing == null)
            {
                HsuPrefabCreated(run, row.Id, instance);
                HsuRoomSetup(run, row.Id, "setup-areas-started");
                run.SettingUpRoom = request.Index;
                try { root.SetupAreas(request.Index == rootIndex ? seed : run.Program.Random(request.Index, DecisionKind.Area, run.Seed)); }
                finally { run.SettingUpRoom = null; }
                HsuRoomSetup(run, row.Id, "setup-areas-returned");
            }
            var areas = root.m_areas;
            if (areas.Length != resource.Areas.Length) throw new GenerationRefusal("gen-area-identity", row.Id);
            var nativeSeeds = new uint[areas.Length]; for (int i = 0; i < areas.Length; i++) nativeSeeds[i] = areas[i].AreaSeed;
            var rules = run.Program.Areas(request.Index, nativeSeeds, request.Resource);
            var originalAreas = areas.ToArray();
            var reordered = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LG_Area>(areas.Length);
            for (int i = 0; i < areas.Length; i++)
            {
                int sourceIndex = rules.Order[i]; reordered[i] = areas[sourceIndex];
                if (reordered[i].AreaSeed != rules.Seeds[sourceIndex])
                    throw new GenerationRefusal("gen-pin-mismatch", row.Id + "/area-seed-before-setup");
            }
            root.m_areas = reordered; root.SetPlaced(); if (request.Existing == null) run.ExpansionCalls[zoneIndex]++;
            // Same registration the native PlaceRoot postfix gives a recipe room: door identities, sealing and readback.
            if (resource.ForgeRoom && request.Existing == null) RoomRecipeHost.Placed(root);
            // A vanilla room the author moved off its cells reaches into cells the game holds no room for ("Borken cell", ladders and
            // special nodes without a volume): the height-aware lookup answers its own volume over its own footprint, a cell high.
            else if (!resource.ForgeRoom && (row.Placement.OffsetX != 0 || row.Placement.OffsetZ != 0))
            {
                float wide = (request.Cells.Max(cell => cell.X) - request.Cells.Min(cell => cell.X) + 1) * grid.m_cellDim / 2, deep = (request.Cells.Max(cell => cell.Z) - request.Cells.Min(cell => cell.Z) + 1) * grid.m_cellDim / 2;
                if (Math.Round(request.Rotation / 90) % 2 != 0) (wide, deep) = (deep, wide);
                StackedVolumes.Register(zone.DimensionIndex, new(-wide, -grid.m_cellDim / 2, -deep), new(wide, grid.m_cellDim / 2, deep), root);
            }
            if (gridTiles.Add(tile.Pointer)) grid.m_groupings.Add(tile);
            if (dimensionTiles.Add(tile.Pointer)) dimension.Tiles.Add(tile);
            RepairRegistration(run, request.Index, root, request.Cells);
            var placed = new Placed(request.Resource, root, request.Anchor, request.Rotation, request.Cells, originalAreas);
            run.Rooms[request.Index] = placed; VerifyCells(run, request.Index, placed);
            var locator = new RoomLocator(definition.Id, (int)zone.DimensionIndex, (int)zone.Layer.m_type, (int)zone.LocalIndex,
                row.Author.Id, row.Author.Revision, resource.PrefabPath, row.Id);
            // SetupAreas binds geomorph/seed, not zone (Area.Setup RVA 0x147f2f0).
            // Native AddArea owns zone membership/coverage. Finish it before publishing identities or pairing plugs.
            RegisterRoomAreas(job, zone, root, row.Id);
            GeneratedRoomRegistry.Add(zone, locator, root);
            run.Locators[request.Index] = locator;
            var generatedAreas = root.m_areas.ToArray();
            var authorResource = run.Program.Resources[row.SourceResource];
            var areaBindings = SelectAuthorAreaBindings(authorResource, row.AreaMap, originalAreas, generatedAreas, row.Id);
            var exitBindings = new GeneratedRoomRegistry.GeneratedExitBinding[authorResource.Exits.Length];
            for (int sourceIndex = 0; sourceIndex < exitBindings.Length; sourceIndex++)
                exitBindings[sourceIndex] = new(authorResource.Exits[sourceIndex].Id, PlugOnRoot(run, request.Index, request.Resource, root, sourceIndex));
            GeneratedRoomRegistry.BindGeneratedRoom(run.Epoch, run.LevelReference, zone, locator, root, areaBindings, exitBindings);
            var authorAreas = AuthorAreaReadback(run, zone, locator, areaBindings);
            run.AuthorAreaReadbacks[request.Index] = authorAreas;
            var authorExits = AuthorExitReadback(run, zone, locator, exitBindings);
            var areaSeeds = new uint[generatedAreas.Length]; var areaNames = new string[generatedAreas.Length];
            for (int i = 0; i < generatedAreas.Length; i++)
            { areaSeeds[i] = generatedAreas[i].AreaSeed; areaNames[i] = generatedAreas[i].name; }
            run.Record("rooms", row.Id, new
            {
                nativeRoot = root.Pointer.ToInt64(),
                dimension = (int)zone.DimensionIndex,
                layer = (int)zone.Layer.m_type,
                localZone = (int)zone.LocalIndex,
                prefab = resource.PrefabPath,
                prefabName = request.Prefab.name,
                authorRoomId = locator.RoomId,
                authorRoomRevision = locator.Revision,
                authorRoomInstance = locator.InstanceId,
                authorAreas,
                authorExits,
                x = root.transform.position.x,
                y = root.transform.position.y,
                z = root.transform.position.z,
                yaw = request.Rotation,
                cells = request.Cells.Select(cell => new { x = cell.X, z = cell.Z }).ToArray(),
                areas = generatedAreas.Length,
                areaSeeds,
                areaNames
            });
        }

        int entry = rootIndex;
        foreach (var request in ordered) if (request.Existing == null) { entry = request.Index; break; }
        run.EntryRooms[zoneIndex] = entry; var rootRoom = (run.Rooms[entry] ?? throw new GenerationRefusal("gen-reference-missing", definition.Id + "/entry")).Root;
        foreach (int connectionIndex in run.LevelProgram.Connections)
        {
            var connection = run.Program.Connections[connectionIndex];
            if (run.Program.Rooms[connection.ToRoom].Zone != zoneIndex || run.Rooms[connection.FromRoom] == null || run.Rooms[connection.ToRoom] == null) continue;
            Pair(run, connectionIndex);
        }
        PairLinks(run);
        foreach (var request in ordered)
        {
            if (request.Existing != null) continue; var row = run.Program.Rooms[request.Index];
            // A room may have no door, or no door attached: it stands at its own place and nothing grows into it.
            if (row.ParentConnection is not { } parentConnection) { run.Record("expansion-source", row.Id, new { targetRoom = row.Id, standalone = true }); continue; }
            var link = run.Program.Connections[parentConnection]; int owner = link.FromRoom;
            var ownerRoom = run.Rooms[owner] ?? throw new GenerationRefusal("gen-source-unavailable", row.Id + "/expansion-source");
            var targetRoom = run.Rooms[request.Index] ?? throw new GenerationRefusal("gen-source-unavailable", row.Id + "/expansion-source");
            var port = Plug(run, owner, link.FromExit); var area = port.OriginalParentArea;
            int areaIndex = Array.FindIndex(ownerRoom.OriginalAreas, item => item.Pointer == area?.Pointer);
            if (areaIndex < 0 || port.m_pariedWith?.Pointer != Plug(run, request.Index, link.ToExit).Pointer)
                throw new GenerationRefusal("gen-source-unavailable", row.Id + "/expansion-source");
            run.Record("expansion-source", row.Id, new
            {
                sourceRoom = run.Program.Rooms[owner].Id,
                sourceArea = areaIndex,
                targetRoom = row.Id,
                nativeSourceRoot = ownerRoom.Root.Pointer.ToInt64(),
                nativeTargetRoot = targetRoom.Root.Pointer.ToInt64(),
                nativeArea = area!.Pointer.ToInt64(),
                nativeSourcePlug = port.Pointer.ToInt64()
            });
        }
        bool rootOnly = true; foreach (var request in ordered) if (request.Existing == null) { rootOnly = false; break; }
        if (rootOnly) run.Record("root-only", definition.Id, new { room = run.Program.Rooms[rootIndex].Id, expansions = 0 });
        foreach (int exitIndex in run.LevelProgram.UnpairedExits)
        {
            var exit = run.Program.UnpairedExits[exitIndex];
            if (run.Program.Rooms[exit.Room].Zone == zoneIndex) RegisterPlug(run, Plug(run, exit.Room, exit.Exit), -1, exitIndex);
        }
        // Build (RVA 0x16561B0) fills a null zone.m_sourceExpander with its own candidate exit after ExpandZone returns and Done marks
        // it a zone source unconditionally (a null one is a NullReferenceException), so the plan's source is stored here.
        zone.m_sourceExpander = source; settings.CustomGeomorphInstance = rootRoom.gameObject; run.Zones[zoneIndex] = zone;
        foreach (var request in ordered)
            if (request.Existing == null) QueueCustomGeomorphLogic(run, run.Program.Rooms[request.Index].Id, run.Rooms[request.Index]!.Root.gameObject);
        // The job the native LG_BuildGeomorphJob.Build (0x1276800) gives a plug, its only creator of either: an LG_BuildCustomPlugJob for a
        // custom plug (branch 0x181276C72), else an LG_BuildPlugJob. LG_BuildPlugJob copies the zone-source mark Done wrote onto the gate it
        // binds (0x1278736, 0x1278C85); the custom job (0x14816F0) never does. Every elevator shaft exit is a custom plug, so as in the game
        // the start zone's source gate carries no mark and is no progression-locked portal to enemy navigation.
        if (source?.m_pariedWith != null)
            LG_Factory.InjectJob(source.m_isCustom ? new LG_BuildCustomPlugJob(source) : new LG_BuildPlugJob(source, seed), LG_Factory.BatchName.Plugs);
    }

    /// <summary>Whether the plan gives the zone an entry from another zone (it is not grown from the dimension's own start room).</summary>
    private static bool HasPlanEntry(Run run, int zoneIndex)
    {
        var definition = run.Program.Zones[zoneIndex]; var root = run.Program.Rooms[definition.RootRoom];
        return !(definition.Source == null && root.ParentConnection == null && root.RootRole is RootKind.Elevator or RootKind.Dimension);
    }

    /// <summary>The native custom-geomorph placement (LG_Floor.FindExternalArea, RVA 0x15CD4C0) hands every iLG_CustomGeomorphLogic of
    /// the room it placed SpawnedOnFloor, then queues its LG_CustomGeomorphBuildJob (batch CustomGeomorphBuildJob) and
    /// LG_CustomGeomorphPostCullingJob (CustomGeomorphPostCullingJob); it is the only code that creates either job. ExpandZone
    /// replaces that placement, so a room of the plan gets the same calls here. Without them the HSU activator room never runs
    /// OnLateBuildJob, its machine is never registered as an objective game object, and WO_ActivateSmallHSU, which reads them after
    /// the CustomGeomorphBuildJob batch, finds no HSUActivator machine; reactor and dimension portal rooms are the same case.</summary>
    private static void QueueCustomGeomorphLogic(Run run, string roomId, GameObject room)
    {
        int count = 0;
        foreach (var logic in room.GetComponentsInChildren<iLG_CustomGeomorphLogic>())
        {
            logic.SpawnedOnFloor();
            var build = new LG_CustomGeomorphBuildJob(logic);
            LG_Factory.InjectJob(build, LG_Factory.BatchName.CustomGeomorphBuildJob);
            LG_Factory.InjectJob(new LG_CustomGeomorphPostCullingJob(logic), LG_Factory.BatchName.CustomGeomorphPostCullingJob);
            TrackHsuJob(run, roomId, room, logic, build);
            count++;
        }
        if (count == 0) return;
        int buildBatch = (int)LG_Factory.BatchName.CustomGeomorphBuildJob, postCullingBatch = (int)LG_Factory.BatchName.CustomGeomorphPostCullingJob;
        run.Record("custom-logic", roomId, new { components = count, buildBatch, postCullingBatch });
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationCustomLogic, "Custom geomorph room logic queued as the native placement does",
            RuntimeLogValue.Of("room", roomId), RuntimeLogValue.Of("components", count),
            RuntimeLogValue.Of("buildBatch", buildBatch), RuntimeLogValue.Of("postCullingBatch", postCullingBatch));
    }

    private static void RepairRegistration(Run run, int index, LG_Geomorph root, CellCoordinate[] expected)
    {
        var row = run.Program.Rooms[index]; var tile = root.m_tile ?? throw new GenerationRefusal("gen-root-invalid", row.Id);
        var zone = root.m_zone ?? throw new GenerationRefusal("gen-root-invalid", row.Id); var grid = zone.Dimension.Grid;
        var wanted = expected.ToHashSet();
        foreach (var cell in tile.m_cells)
            if (cell == null || !wanted.Contains(new(cell.x, cell.z)) || cell.m_grouping != null && cell.m_grouping.Pointer != tile.Pointer)
                throw new GenerationRefusal("gen-cell-owner-conflict", row.Id);
        var nativeCells = new LG_Cell[expected.Length];
        for (int i = 0; i < expected.Length; i++)
        {
            var point = expected[i];
            var cell = grid.GetCell(point.X, point.Z);
            if (cell == null || cell.m_grouping != null && cell.m_grouping.Pointer != tile.Pointer)
                throw new GenerationRefusal("gen-cell-owner-conflict", row.Id);
            nativeCells[i] = cell;
        }
        var unique = new HashSet<CellCoordinate>();
        for (int i = tile.m_cells.Count - 1; i >= 0; i--) if (!unique.Add(new(tile.m_cells[i].x, tile.m_cells[i].z))) tile.m_cells.RemoveAt(i);
        for (int i = 0; i < expected.Length; i++)
        {
            var point = expected[i]; var cell = nativeCells[i];
            if (cell.m_grouping == null) { if (unique.Contains(point)) tile.m_cells.Remove(cell); tile.AddCell(cell); }
            else if (!unique.Contains(point)) tile.m_cells.Add(cell);
        }
    }

    private static void VerifyCells(Run run, int index, Placed room)
    {
        var row = run.Program.Rooms[index]; var root = room.Root; var tile = root.m_tile;
        if (tile == null || tile.m_geoRoot == null || tile.m_geoRoot.Pointer != root.Pointer || root.m_zone == null)
            throw new GenerationRefusal("gen-root-invalid", row.Id);
        var grid = root.m_zone.Dimension.Grid; int gridReferences = 0, dimensionReferences = 0;
        foreach (var value in grid.m_groupings) if (value != null && value.Pointer == tile.Pointer) gridReferences++;
        foreach (var value in root.m_zone.Dimension.Tiles) if (value != null && value.Pointer == tile.Pointer) dimensionReferences++;
        if (gridReferences != 1 || dimensionReferences != 1) throw new GenerationRefusal("gen-cell-owner-inconsistent", row.Id + "/tile-registration");
        var actual = new HashSet<CellCoordinate>();
        foreach (var cell in tile.m_cells)
            if (cell == null || cell.m_grouping == null || cell.m_grouping.Pointer != tile.Pointer || !actual.Add(new(cell.x, cell.z)))
                throw new GenerationRefusal("gen-cell-owner-inconsistent", row.Id);
        if (!actual.SetEquals(room.Cells)) throw new GenerationRefusal("gen-cell-owner-inconsistent", row.Id);
        foreach (var cell in room.Cells) if (grid.GetCell(cell.X, cell.Z)?.m_grouping?.Pointer != tile.Pointer)
            throw new GenerationRefusal("gen-cell-owner-inconsistent", row.Id);
        run.Record("ownership", row.Id, new { requested = room.Cells.Length, unique = actual.Count, root = true, bidirectional = true });
    }

    private static LG_Plug Plug(Run run, int room, int sourceExit)
    {
        var placed = run.Rooms[room] ?? throw new GenerationRefusal("gen-source-unavailable", run.Program.Rooms[room].Id);
        return PlugOnRoot(run, room, placed.Resource, placed.Root, sourceExit);
    }
    private static LG_Plug PlugOnRoot(Run run, int room, int resource, LG_Geomorph root, int sourceExit)
    {
        int exit = run.Program.Rooms[room].ExitMap[sourceExit]; var descriptor = run.Program.Resources[resource].Exits[exit];
        var ordinals = RoomExitOrdinals(run, room, resource, root);
        if ((uint)exit >= (uint)ordinals.Length || (uint)ordinals[exit] >= (uint)root.m_plugs.Count)
            throw new GenerationRefusal("gen-exit-missing", run.Program.Rooms[room].Id + "/" + descriptor.Id);
        // The plug's own transform is the declared socket. A static dimension's root left m_position at the world origin
        // (game run: expected 0,0,-32, read 0,-250,0 in root space), so reading that field reported a false mismatch.
        var found = root.m_plugs[ordinals[exit]]; var actual = root.transform.InverseTransformPoint(found.transform.position);
        if (Math.Abs(actual.x - descriptor.X) > .01f || Math.Abs(actual.y - descriptor.Y) > .01f || Math.Abs(actual.z - descriptor.Z) > .01f)
            throw new GenerationRefusal("gen-exit-mismatch", run.Program.Rooms[room].Id + "/" + descriptor.Id + "/position",
                "exit " + exit + " ordinal " + ordinals[exit] + " of " + root.m_plugs.Count + " plugs, root " + root.name + ": expected "
                + descriptor.X + "," + descriptor.Y + "," + descriptor.Z + " actual " + actual.x + "," + actual.y + "," + actual.z);
        // A Forge room's socket is built from the exported yaw (outward = sin yaw, 0, cos yaw), so the plug must face that way in room space.
        if (run.Program.Resources[resource].ForgeRoom)
        {
            var facing = root.transform.InverseTransformDirection(found.transform.forward);
            // A door looking straight up or down has no horizontal direction to read.
            double actualYaw = RoomYaw.OfDirection(facing.x, facing.z);
            if (Math.Abs(facing.y) < .999f && !RoomYaw.Same(actualYaw, descriptor.Yaw))
                throw new GenerationRefusal("gen-exit-mismatch", run.Program.Rooms[room].Id + "/" + descriptor.Id + "/yaw",
                    "exit " + exit + ": expected yaw " + descriptor.Yaw + " actual " + actualYaw);
        }
        return found;
    }
    /// <summary>What pairing looked at: the socket's place and facing, and the area it opens from.</summary>
    private static string PlugPose(LG_Plug plug) => plug.transform.position + " euler " + plug.transform.eulerAngles + " forward " + plug.m_forward + " area " + (plug.OriginalParentArea?.name ?? "none");
    /// <summary>The plan already names the two doors of a connection, so they are paired directly: the two fields LG_Plug.TryPair
    /// (0x13640D0) writes after its distance and m_dir checks. LG_Plug.SetPlaced (0x1363F10) derives m_dir from the door's side of the room
    /// origin, which is wrong for a door turned to 45 degrees or standing on another side than it faces; after pairing m_dir has no
    /// reader (FindExternalArea and GetExternalCell serve native expansion, which the plan replaces).</summary>
    private static void PairPlanned(LG_Plug a, LG_Plug b)
    {
        a.m_pariedWith = b; b.m_pariedWith = a;
        a.m_linksTo = b.m_linksFrom; b.m_linksTo = a.m_linksFrom;
    }
    private static void Pair(Run run, int index)
    {
        var row = run.Program.Connections[index]; var from = Plug(run, row.FromRoom, row.FromExit); var to = Plug(run, row.ToRoom, row.ToExit);
        if (from.m_pariedWith != null || to.m_pariedWith != null)
            throw new GenerationRefusal("gen-exit-mismatch", row.Id, "from " + PlugPose(from) + " to " + PlugPose(to));
        PairPlanned(from, to);
        run.Record("pairs", row.Id, new { from = PlugPose(from), to = PlugPose(to) });
        var fromRoom = run.Rooms[row.FromRoom] ?? throw new GenerationRefusal("gen-source-unavailable", row.Id + "/fromRoom");
        var toRoom = run.Rooms[row.ToRoom] ?? throw new GenerationRefusal("gen-source-unavailable", row.Id + "/toRoom");
        var fromLocator = run.Locators[row.FromRoom] ?? throw new GenerationRefusal("gen-reference-missing", row.Id + "/fromLocator");
        var toLocator = run.Locators[row.ToRoom] ?? throw new GenerationRefusal("gen-reference-missing", row.Id + "/toLocator");
        var targetZone = toRoom.Root.m_zone ?? throw new GenerationRefusal("gen-area-identity", row.Id + "/targetZone");
        GeneratedRoomRegistry.BindConnection(targetZone, row.Id, fromLocator, fromRoom.Root, from, toLocator, toRoom.Root, to);
        RegisterPlug(run, from, index, -1); RegisterPlug(run, to, index, -1);
    }
    private static void RegisterPlug(Run run, LG_Plug plug, int connection, int exit)
    {
        long pointer = plug.Pointer.ToInt64();
        for (int i = 0; i < run.PlugBindingCount; i++)
            if (run.PlugBindings[i].Pointer == pointer)
            {
                if (run.PlugBindings[i].Connection != connection || run.PlugBindings[i].Exit != exit)
                    throw new GenerationRefusal("gen-exit-ambiguous", pointer.ToString());
                return;
            }
        if (run.PlugBindingCount >= run.PlugBindings.Length) throw new GenerationRefusal("gen-reference-missing", "plug-bindings/capacity");
        run.PlugBindings[run.PlugBindingCount++] = new(pointer, connection, exit); run.PlugsSorted = false;
    }
}
