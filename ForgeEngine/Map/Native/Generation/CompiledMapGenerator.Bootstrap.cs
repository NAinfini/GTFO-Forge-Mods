using System;
using System.Collections.Generic;
using AIGraph;
using Expedition;
using ForgeMap.Generation;
using GameData;
using LevelGeneration;
using LevelGeneration.Core;
using UnityEngine;
using XXHashing;
using static ForgeMap.Generation.GenerationProgram;
using ForgeCore;
using NativeDimension = LevelGeneration.Dimension;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    internal readonly record struct BootstrapRoot(CellCoordinate Anchor, CellCoordinate[] Cells,
        double Rotation, float OffsetX, float OffsetZ);

    // Called once from the SetupFloor batch before the removed LG_SetupFloor job can be dequeued.
    // The frozen plan owns floor setup without invoking the vanilla selection methods.
    private static void BootstrapFloor(Run run, LG_SetupFloor job)
    {
        if (run.Failed) return;
        var floor = job.m_floor ?? throw new GenerationRefusal("gen-world-unavailable", run.Level + "/floor");
        if (Builder.CurrentFloor == null || Builder.CurrentFloor.Pointer != floor.Pointer)
            throw new GenerationRefusal("gen-stage-invalid", run.Level + "/setup-floor-owner");
        CaptureBulkheadKeyPool(run, floor);
        ResolveAssets(run);

        int realityIndex = run.Program.DimensionAt(run.LevelProgram, (int)eDimensionIndex.Reality)
            ?? throw new GenerationRefusal("gen-dimension-mismatch", run.Level + "/Reality");
        var realityDefinition = run.Program.Dimensions[realityIndex];
        if (!realityDefinition.Enabled || realityDefinition.Mode != DimensionKind.Native)
            throw new GenerationRefusal("gen-dimension-mismatch", realityDefinition.Id + "/Reality");
        int rootIndex = realityDefinition.RootRoom; var rootRoom = run.Program.Rooms[rootIndex];
        if (rootRoom.RootRole != RootKind.Elevator)
            throw new GenerationRefusal("gen-root-role-unsupported", rootRoom.Id + "/Reality");

        var layerData = Builder.LayerBuildDatas;
        if (layerData == null || layerData.Count == 0 || layerData[0]?.m_zoneBuildDatas == null || layerData[0].m_zoneBuildDatas.Count == 0)
            throw new GenerationRefusal("gen-reference-missing", rootRoom.Id + "/native-zone-seed");
        var firstZone = layerData[0].m_zoneBuildDatas[0];
        var sequence = new XXHashSequence(job.m_rnd.Seed.SubSeed(unchecked((uint)firstZone.SubSeed)), true);
        uint selectionSeed = sequence.NextSubSeed(); // Native selection consumed this even though the plan already froze the prefab.

        var set = RequirePinnedResourceSet(Builder.ComplexResourceSetBlock, realityDefinition.ResourceSet,
            realityDefinition.Id + "/resourceSet-input", "Builder.ComplexResourceSetBlock");
        var config = set.LevelGenConfig ?? throw new GenerationRefusal("gen-reference-missing", realityDefinition.Id + "/LevelGenConfig");
        if (config.GridSize != realityDefinition.Grid.Size || config.CellDimension != realityDefinition.Grid.CellSize)
            throw new GenerationRefusal("gen-pin-mismatch", realityDefinition.Id + "/grid-input");

        if (floor.m_dimensions == null || floor.m_indexToDimensionMap == null || floor.m_dimensionArenaMap == null
            || floor.m_createdDimensionSet == null)
            throw new GenerationRefusal("gen-stage-invalid", run.Level + "/floor-tables");
        if (floor.m_dimensions.Count != 0 || floor.m_indexToDimensionMap.Count != 0 || floor.m_dimensionArenaMap.Count != 0)
            throw new GenerationRefusal("gen-stage-invalid", run.Level + "/floor-already-setup");
        floor.m_floorType = LG_FloorType.StartFloor; floor.m_floorTransitionDirection = config.TransitionDirection;
        floor.m_dimensions.Clear(); floor.m_indexToDimensionMap.Clear(); floor.m_dimensionArenaMap.Clear();

        var realityGrid = CreateBootstrapGrid(run, floor, realityDefinition, rootRoom, floor.transform.position, true);
        var reality = new NativeDimension(realityGrid.Grid, eDimensionIndex.Reality, 0, null, Vector3.zero, false);
        var realityResources = RequireDimensionResources(reality, realityDefinition.ResourceSet, realityDefinition.Id + "/resourceSet-readback");
        var realityData = reality.DimensionData ?? throw new GenerationRefusal("gen-dimension-mismatch", realityDefinition.Id + "/dimension-data");
        realityData.SoundEventOnWarpTo = Builder.LevelGenExpedition.SoundEventOnWarpToReality;
        realityData.ObjectiveType = DimensionObjectiveType.Independent;
        double realityRotation = BootstrapRotation(run, rootIndex, Yaw(job.m_rotation), new bool[run.Program.Rooms.Length]);
        var realityRoot = RootPlacement(run, rootIndex, realityGrid.LocalCells, realityGrid.Grid, reality, realityRotation);
        var realityPrefab = Asset(run, rootRoom.Resource);
        var start = GOUtil.SpawnChildAndGetComp<LG_FloorTransition>(realityPrefab, realityRoot.Position,
            Quaternion.Euler(0, (float)realityRoot.Geometry.Rotation, 0));
        if (start == null) throw new GenerationRefusal("gen-resource-missing", rootRoom.Id + "/floor-transition");
        start.m_geoPrefab = realityPrefab; SetSubComplex(run, rootIndex, start);
        run.SettingUpRoom = rootIndex;
        try { start.SetupAreas(sequence.NextSubSeed()); }
        finally { run.SettingUpRoom = null; }
        start.SetPlaced();
        AttachDimensionComponent(start, reality, rootRoom.Id);
        AttachBootstrapTile(run, rootRoom.Id, reality, realityGrid.Grid, start, realityRoot.Geometry.Cells, false);
        floor.m_dimensions.Add(reality); floor.m_indexToDimensionMap[eDimensionIndex.Reality] = reality;
        run.Record("dimension-bootstrap", realityDefinition.Id, new
        {
            nativeAddress = 0,
            selectionSeed,
            size = realityGrid.Grid.m_sizeX,
            cellSize = realityGrid.Grid.m_cellDim,
            resourceSet = realityResources.persistentID,
            resourceOwner = "Builder.ComplexResourceSetBlock",
            root = rootRoom.Id,
            x = start.transform.position.x,
            y = start.transform.position.y,
            z = start.transform.position.z,
            yaw = realityRoot.Geometry.Rotation,
            cells = realityRoot.Geometry.Cells.Length
        });
        run.Record("elevator-rotation", rootRoom.Id, new { yaw = realityRoot.Geometry.Rotation });

        uint floorSeed = sequence.NextSubSeed(); var dimensions = new XXHashSequence(floorSeed, true); float y = 150f;
        foreach (int dimensionIndex in run.LevelProgram.Dimensions)
        {
            if (dimensionIndex == realityIndex) continue;
            var definition = run.Program.Dimensions[dimensionIndex];
            if (!definition.Enabled) continue;
            if (definition.Mode == DimensionKind.Arena || definition.NativeAddress == 22)
                throw new GenerationRefusal("gen-dimension-unsupported", definition.Id + "/arena-slots");
            if (definition.DimensionBlock is not { } blockId)
                throw new GenerationRefusal("gen-reference-missing", definition.Id + "/dimensionBlock");
            var block = (DimensionDataBlock)NativeDataBlockTables.RequireBlock("Dimension", blockId);
            var data = block.DimensionData ?? throw new GenerationRefusal("gen-reference-missing", definition.Id + "/dimensionBlock");
            RequireResourceSetId(data.DimensionResourceSetID, definition.ResourceSet, definition.Id + "/resourceSet-input", "DimensionData.DimensionResourceSetID");
            bool isStatic = definition.Mode == DimensionKind.Static;
            if (data.IsStaticDimension != isStatic)
                throw new GenerationRefusal("gen-pin-mismatch", definition.Id + "/mode-input");
            var address = (eDimensionIndex)definition.NativeAddress;
            if (floor.m_createdDimensionSet.Contains(address) || floor.m_indexToDimensionMap.ContainsKey(address))
                throw new GenerationRefusal("gen-dimension-mismatch", definition.Id + "/duplicate-native-address");
            y += data.VerticalExtentsDown; var position = new Vector3(0, 1, 0) * y;
            CreateBootstrapDimension(run, floor, definition, data, position, dimensions.NextSubSeed());
            y += data.VerticalExtentsUp;
        }

        NativeDimension.CalculateAllDimensionBounds();
        foreach (int dimensionIndex in run.LevelProgram.Dimensions)
        {
            var definition = run.Program.Dimensions[dimensionIndex];
            if (definition.Enabled) floor.InjectJobs((eDimensionIndex)definition.NativeAddress);
        }
        LG_Factory.InjectJob(new LG_BuildFloorJob(floor), (LG_Factory.BatchName)7);
    }

    private static void CreateBootstrapDimension(Run run, LG_Floor floor, DimensionProgram definition,
        DimensionData data, Vector3 position, uint seed)
    {
        int roomIndex = definition.RootRoom; var room = run.Program.Rooms[roomIndex];
        if (room.RootRole != RootKind.Dimension)
            throw new GenerationRefusal("gen-root-role-unsupported", room.Id + "/dimension");
        // The native CreateFloorTransition instantiates this prefab and takes its LG_FloorTransition, which only the elevator shafts and
        // the vanilla dimension roots carry; it ends in a bare NullReferenceException for any other geomorph (RVA 0x15CC9F0 dereferences the
        // spawn result without a check), so the root is refused by name before the native call.
        var rootPrefab = Asset(run, room.Resource);
        if (rootPrefab.GetComponent<LG_FloorTransition>() == null)
            throw new GenerationRefusal("gen-resource-missing", room.Id + "/floor-transition",
                "dimension root prefab " + rootPrefab.name + " has no LG_FloorTransition, which the native dimension transition requires");
        var prepared = CreateBootstrapGrid(run, floor, definition, room, position, false);
        var dimension = new NativeDimension(prepared.Grid, (eDimensionIndex)definition.NativeAddress,
            floor.m_dimensions.Count, data, position, false);
        var resources = RequireDimensionResources(dimension, definition.ResourceSet, definition.Id + "/resourceSet-readback");
        double rotation = BootstrapRotation(run, roomIndex, 0, new bool[run.Program.Rooms.Length]);
        var root = RootPlacement(run, roomIndex, prepared.LocalCells, prepared.Grid, dimension, rotation);
        var dimensionSequence = new XXHashSequence(seed, true); uint transitionSeed = dimensionSequence.NextSubSeed();
        run.SettingUpRoom = roomIndex;
        LG_FloorTransition transition;
        try
        {
            transition = floor.CreateFloorTransition(transitionSeed, dimension, root.Position,
                Quaternion.Euler(0, (float)root.Geometry.Rotation, 0), rootPrefab, data.IsStaticDimension);
        }
        finally { run.SettingUpRoom = null; }
        if (transition == null) throw new GenerationRefusal("gen-resource-missing", room.Id + "/floor-transition");
        if (transition.m_geoPrefab != null && transition.m_geoPrefab.Pointer != rootPrefab.Pointer)
            throw new GenerationRefusal("gen-pin-mismatch", room.Id + "/dimension-root-prefab");
        transition.m_geoPrefab = rootPrefab;
        // A room recipe root gets the start root's registration (RoomRecipeTransitionPlaced): door identities, sealing and readback.
        // Placed leaves a room it already holds alone, so a native SetPlaced inside CreateFloorTransition does not register it twice.
        if (run.Program.Resources[room.Resource].ForgeRoom) RoomRecipeHost.Placed(transition);
        SetSubComplex(run, roomIndex, transition); AttachDimensionComponent(transition, dimension, room.Id);
        var volume = transition.GetComponent<AIG_GeomorphNodeVolume>()
            ?? throw new GenerationRefusal("gen-resource-missing", room.Id + "/node-volume");
        transition.m_nodeVolume = volume; volume.ConstructVoxelNodeVolume(); transition.ScanChildrenAreas();
        bool isStatic = data.IsStaticDimension;
        AttachBootstrapTile(run, room.Id, dimension, prepared.Grid, transition, root.Geometry.Cells, isStatic);
        if (isStatic) { dimension.StaticLevel = transition; transition.SetupGates(); }
        floor.m_dimensions.Add(dimension); floor.m_indexToDimensionMap[(eDimensionIndex)definition.NativeAddress] = dimension;
        floor.m_createdDimensionSet.Add((eDimensionIndex)definition.NativeAddress);
        run.Record("dimension-bootstrap", definition.Id, new
        {
            nativeAddress = definition.NativeAddress,
            size = prepared.Grid.m_sizeX,
            cellSize = prepared.Grid.m_cellDim,
            resourceSet = resources.persistentID,
            resourceOwner = "Dimension.ResourceData",
            root = room.Id,
            x = transition.transform.position.x,
            y = transition.transform.position.y,
            z = transition.transform.position.z,
            yaw = root.Geometry.Rotation,
            cells = isStatic ? checked(prepared.Grid.m_sizeX * prepared.Grid.m_sizeZ) : root.Geometry.Cells.Length,
            staticDimension = isStatic
        });
    }

    private readonly record struct BootstrapGrid(LG_Grid Grid, CellCoordinate[] LocalCells);
    private readonly record struct BootstrapPlacement(BootstrapRoot Geometry, Vector3 Position);

    private static BootstrapGrid CreateBootstrapGrid(Run run, LG_Floor floor, DimensionProgram dimension,
        RoomProgram room, Vector3 center, bool reality)
    {
        var resource = run.Program.Resources[room.Resource]; var prefab = Asset(run, room.Resource);
        var prototype = prefab.GetComponent<LG_Geomorph>()
            ?? throw new GenerationRefusal("gen-resource-missing", room.Id + "/geomorph");
        LG_TileShapeType nativeShape = reality ? NativeShape(prototype, room.Id) : LG_TileShapeType.s1x1;
        float cell = dimension.Grid.CellSize; int size = dimension.Grid.Size;
        var origin = center - new Vector3(cell, 0, cell) * size * .5f;
        var native = floor.CreateTileShape(nativeShape, cell, ref origin)
            ?? throw new GenerationRefusal("gen-footprint-ambiguous", room.Id + "/native-shape");
        CellCoordinate[] local = resource.Footprint;
        if (local.Length == 0)
        {
            if (!reality)
            {
                var unused = Vector3.zero; native = floor.CreateTileShape(NativeShape(prototype, room.Id), cell, ref unused)
                    ?? throw new GenerationRefusal("gen-footprint-ambiguous", room.Id + "/native-shape");
            }
            if (native.m_sizeX <= 0 || native.m_sizeZ <= 0)
                throw new GenerationRefusal("gen-footprint-ambiguous", room.Id + "/resource/descriptor/footprint");
            local = DeclaredFootprints.Rectangle(native.m_sizeX, native.m_sizeZ);
        }
        var grid = CellGridBase<LG_Grid, LG_Tile, LG_Cell>.Create(cell, origin)
            ?? throw new GenerationRefusal("gen-stage-invalid", dimension.Id + "/grid-create");
        grid.SetupCells(size, size); grid.InititalizeCells();
        if (grid.m_sizeX != size || grid.m_sizeZ != size || grid.m_cellDim != cell)
            throw new GenerationRefusal("gen-dimension-mismatch", dimension.Id + "/grid-readback");
        return new(grid, local);
    }

    private static LG_TileShapeType NativeShape(LG_Geomorph prototype, string path)
    {
        if (!Enum.TryParse<LG_TileShapeType>(prototype.m_goShapeType.ToString(), true, out var shape)
            || shape is LG_TileShapeType.None or LG_TileShapeType.Custom)
            throw new GenerationRefusal("gen-footprint-ambiguous", path + "/resource/descriptor/footprint");
        return shape;
    }

    private static BootstrapPlacement RootPlacement(Run run, int roomIndex, CellCoordinate[] local, LG_Grid grid,
        NativeDimension dimension, double rotation)
    {
        var room = run.Program.Rooms[roomIndex]; var solved = SolveBootstrapRoot(local, room.Placement, grid.m_sizeX, rotation, room.Id);
        var position = grid.GetPosition(solved.Anchor.X, solved.Anchor.Z);
        position.x += solved.OffsetX * grid.m_cellDim + room.Placement.OffsetX;
        position.z += solved.OffsetZ * grid.m_cellDim + room.Placement.OffsetZ;
        position.y = dimension.GroundY + (room.Height ?? 0);
        return new(solved, position);
    }

    internal static BootstrapRoot SolveBootstrapRoot(CellCoordinate[] footprint, PlacementProgram placement,
        int gridSize, double rotation, string path)
    {
        if (footprint.Length == 0) throw new GenerationRefusal("gen-footprint-ambiguous", path + "/resource/descriptor/footprint");
        var anchor = placement.Present
            ? new CellCoordinate(checked(gridSize / 2 + placement.X), checked(gridSize / 2 + placement.Z))
            : new CellCoordinate(gridSize / 2, gridSize / 2);
        var cells = DeclaredFootprints.Place(footprint, anchor, rotation);
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (var cell in cells)
        {
            if (cell.X < 0 || cell.Z < 0 || cell.X >= gridSize || cell.Z >= gridSize)
                throw new GenerationRefusal("gen-grid-range", path + "/footprint");
            minX = Math.Min(minX, cell.X); maxX = Math.Max(maxX, cell.X);
            minZ = Math.Min(minZ, cell.Z); maxZ = Math.Max(maxZ, cell.Z);
        }
        return new(anchor, cells, rotation, (minX + maxX) / 2f - anchor.X, (minZ + maxZ) / 2f - anchor.Z);
    }

    private static double BootstrapRotation(Run run, int room, double native, bool[] visiting)
    {
        if ((uint)room >= (uint)visiting.Length || visiting[room])
            throw new GenerationRefusal("gen-rotation-invalid", run.Program.Rooms[room].Id + "/sourceRoom");
        if (run.Rooms[room] is { } placed) return placed.Rotation;
        visiting[room] = true;
        try { return run.Program.Rotation(room, native, run.Seed, source => BootstrapRotation(run, source, 0, visiting)); }
        finally { visiting[room] = false; }
    }

    private static void AttachDimensionComponent(LG_FloorTransition transition, NativeDimension dimension, string path)
    {
        var component = transition.GetComponent<LG_Dimension>() ?? transition.gameObject.AddComponent<LG_Dimension>();
        if (component == null) throw new GenerationRefusal("gen-resource-missing", path + "/dimension-component");
        dimension.DimensionLevel = component;
        if (transition.m_spawnPoints != null) foreach (var point in transition.m_spawnPoints) component.AddSpawnPoint(point);
    }

    private static void SetSubComplex(Run run, int room, LG_Geomorph root)
    {
        var value = (SubComplex)run.Program.Resources[run.Program.Rooms[room].Resource].SubComplex;
        if (!Enum.IsDefined(typeof(SubComplex), value)) throw new GenerationRefusal("gen-resource-subcomplex", run.Program.Rooms[room].Id);
        root.m_assignedSubComplexFromZone = value;
    }

    private static void AttachBootstrapTile(Run run, string path, NativeDimension dimension, LG_Grid grid,
        LG_Geomorph root, CellCoordinate[] authoredCells, bool fullGrid)
    {
        CellCoordinate[] owned;
        if (fullGrid)
        {
            owned = new CellCoordinate[checked(grid.m_sizeX * grid.m_sizeZ)]; int next = 0;
            for (int x = 0; x < grid.m_sizeX; x++) for (int z = 0; z < grid.m_sizeZ; z++) owned[next++] = new(x, z);
        }
        else owned = authoredCells;
        int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        foreach (var cell in owned) { minX = Math.Min(minX, cell.X); maxX = Math.Max(maxX, cell.X); minZ = Math.Min(minZ, cell.Z); maxZ = Math.Max(maxZ, cell.Z); }
        var shape = new LG_TileShape
        {
            m_type = LG_TileShapeType.Custom,
            m_gridPosition = new LG_GridPosition { x = minX, z = minZ },
            m_sizeX = maxX - minX + 1,
            m_sizeZ = maxZ - minZ + 1
        };
        var tile = LG_Tile.Create(grid, shape) ?? throw new GenerationRefusal("gen-stage-invalid", path + "/start-tile");
        NormalizeBootstrapCells(path, grid, tile, owned);
        tile.m_geoRoot = root; root.m_tile = tile;
        var dimensionRoot = dimension.DimensionRootTemp ?? throw new GenerationRefusal("gen-stage-invalid", path + "/dimension-root");
        root.transform.SetParent(dimensionRoot.transform);
        dimension.m_startTransition = root.TryCast<LG_FloorTransition>()
            ?? throw new GenerationRefusal("gen-resource-missing", path + "/floor-transition");
        dimension.m_startTile = tile;
        if (!dimension.Tiles.Contains(tile)) dimension.Tiles.Add(tile);
    }

    private static void NormalizeBootstrapCells(string path, LG_Grid grid, LG_Tile tile, CellCoordinate[] wanted)
    {
        var wantedSlots = new bool[checked(grid.m_sizeX * grid.m_sizeZ)];
        var nativeCells = new LG_Cell[wanted.Length];
        for (int i = 0; i < wanted.Length; i++)
        {
            var point = wanted[i];
            if (point.X < 0 || point.Z < 0 || point.X >= grid.m_sizeX || point.Z >= grid.m_sizeZ)
                throw new GenerationRefusal("gen-cell-missing", path);
            nativeCells[i] = grid.GetCell(point.X, point.Z) ?? throw new GenerationRefusal("gen-cell-missing", path);
            wantedSlots[checked(point.Z * grid.m_sizeX + point.X)] = true;
        }
        var listed = new HashSet<nint>();
        for (int i = 0; i < tile.m_cells.Count;)
        {
            var cell = tile.m_cells[i];
            bool inGrid = cell != null && cell.x >= 0 && cell.z >= 0 && cell.x < grid.m_sizeX && cell.z < grid.m_sizeZ;
            if (!inGrid || !wantedSlots[checked(cell!.z * grid.m_sizeX + cell.x)] || !listed.Add(cell.Pointer))
            {
                if (cell?.m_grouping?.Pointer == tile.Pointer) cell.m_grouping = null;
                tile.m_cells.RemoveAt(i);
                continue;
            }
            i++;
        }
        foreach (var cell in nativeCells)
        {
            if (cell.m_grouping != null && cell.m_grouping.Pointer != tile.Pointer)
                throw new GenerationRefusal("gen-cell-owner-conflict", path);
            bool contains = listed.Contains(cell.Pointer);
            if (cell.m_grouping == null)
            {
                if (contains) tile.m_cells.Remove(cell);
                tile.AddCell(cell); listed.Add(cell.Pointer);
            }
            else if (!contains) { tile.m_cells.Add(cell); listed.Add(cell.Pointer); }
        }
        int references = 0;
        for (int i = grid.m_groupings.Count - 1; i >= 0; i--)
            if (grid.m_groupings[i]?.Pointer == tile.Pointer && ++references > 1) grid.m_groupings.RemoveAt(i);
        if (references == 0) grid.m_groupings.Add(tile);
        if (tile.m_cells.Count != wanted.Length) throw new GenerationRefusal("gen-cell-owner-inconsistent", path);
        // AddCell is a native call: confirm the current grid lookup after the writes, not the earlier wrapper.
        foreach (var point in wanted) if (grid.GetCell(point.X, point.Z)?.m_grouping?.Pointer != tile.Pointer)
            throw new GenerationRefusal("gen-cell-owner-inconsistent", path);
    }
}
