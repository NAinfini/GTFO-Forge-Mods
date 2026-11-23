using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeMap.Generation;

/// <summary>
/// Immutable, dense execution table built once at package admission. Source JSON remains available for
/// canonical evidence only; native generation consumes the typed rows below.
/// </summary>
internal sealed class GenerationProgram
{
    public enum RootKind { None, Elevator, Dimension, Zone }
    public enum DimensionKind { Native, Static, Arena }
    public enum RotationKind { Native, Absolute, Relative, Random }
    public enum EdgeKind { Internal, ZoneEntry }
    public enum DoorKind { Native, None, Weak, Security, Apex, Bulkhead }
    public enum HeightKind { Same, SingleDrop, DoubleDrop }
    public enum ExitTreatment { Open, Cap, Divider }
    public enum DecisionKind { Rotation, Door, Connection, Area }

    public readonly record struct Identity(string Id, string Revision);
    public readonly record struct BlockAddress(int Input, int Block);
    public readonly record struct GridProgram(int Size, float CellSize);
    public readonly record struct SeedProgram(bool Native, uint Value)
    {
        public uint Apply(uint native) => Native ? native : Value;
    }
    public readonly record struct ExitProgram(string Id, string HierarchyPath, float X, float Y, float Z, double Yaw);
    public sealed record ResourceProgram(int Index, string Id, string Revision, string Category, string PrefabPath,
        int SubComplex, uint ComplexResourceSet, CellCoordinate[] Footprint, Identity[] Areas, ExitProgram[] Exits,
        bool ForgeRoom = false);
    public sealed record LevelProgram(int Index, string Id, string Reference, int[] Dimensions, int[] Zones,
        int[] Rooms, int[] Connections, int[] UnpairedExits, int[] Links, int[] RequiredResources, uint[] RequiredResourceSets);
    public sealed record DimensionProgram(int Index, string Id, int Level, int NativeAddress, bool Enabled,
        DimensionKind Mode, int RootRoom, int[] Zones, int[] ExtraResources, uint ResourceSet,
        uint? DimensionBlock, GridProgram Grid);
    public readonly record struct ZoneSource(int Room, int Area, int Exit);
    public sealed record ZoneProgram(int Index, string Id, int Dimension, int Layer, int LocalIndex, int RootRoom,
        int[] Rooms, int[] Connections, ZoneSource? Source, uint Layout, SeedProgram ZoneSeed,
        SeedProgram MarkerSeed, SeedProgram LightSeed, float CoverageMinimum, float CoverageMaximum,
        int? AreaCount, int? ExpansionCount);
    public readonly record struct RotationProgram(RotationKind Kind, double Yaw, int SourceRoom, double[] Candidates);
    // X/Z select native grid cells. OffsetX/OffsetZ move only the root transform in dimension-world metres;
    // footprint placement and cell ownership never derive from that continuous translation.
    public readonly record struct PlacementProgram(bool Present, int X, int Z, float OffsetX, float OffsetZ);
    public sealed record RoomProgram(int Index, string Id, Identity Author, int Zone, int SourceResource,
        int Resource, RootKind RootRole, int? ParentConnection, int? SourceExit, PlacementProgram Placement,
        RotationProgram Rotation, float? Height, bool HeightFromSequence, int Ordinal, int[] AreaMap, int[] ExitMap,
        int[] AreaOrder, SeedProgram?[] AreaSeeds, string[] RemovedParts);
    public sealed record ConnectionProgram(int Index, string Id, int FromRoom, int FromExit, int ToRoom, int ToExit,
        int ActualFromExit, int ActualToExit, EdgeKind Edge, DoorKind Door, double? NoDoorProbability,
        HeightKind HeightFamily, int? Resource, int?[] DirectionResources);
    public sealed record UnpairedExitProgram(int Index, string Id, int Room, int Exit, int ActualExit,
        ExitTreatment Treatment, int? Resource);
    /// <summary>A loop between two free doorways that meet on the grid: outside the connection tree, it never changes which zone a room belongs to.</summary>
    public sealed record LinkProgram(int Index, string Id, int FromRoom, int FromExit, int ToRoom, int ToExit, bool Weak, int? Resource);

    private sealed record SequenceProgram(bool Cycle, int[] Resources, float[] Heights, JsonElement[] RoomElements);
    private sealed record AddressMap(int Resource, int[] Areas, int[] Exits);

    public MapGenerationPlan Plan { get; }
    public string Hash { get; }
    public ResourceProgram[] Resources { get; private set; } = Array.Empty<ResourceProgram>();
    public LevelProgram[] Levels { get; private set; } = Array.Empty<LevelProgram>();
    public DimensionProgram[] Dimensions { get; private set; } = Array.Empty<DimensionProgram>();
    public ZoneProgram[] Zones { get; private set; } = Array.Empty<ZoneProgram>();
    public RoomProgram[] Rooms { get; private set; } = Array.Empty<RoomProgram>();
    public ConnectionProgram[] Connections { get; private set; } = Array.Empty<ConnectionProgram>();
    public UnpairedExitProgram[] UnpairedExits { get; private set; } = Array.Empty<UnpairedExitProgram>();
    public LinkProgram[] Links { get; private set; } = Array.Empty<LinkProgram>();

    private readonly JsonElement[] snapshots;
    private SequenceProgram?[] roomSequences = Array.Empty<SequenceProgram?>();
    private SequenceProgram?[] connectionSequences = Array.Empty<SequenceProgram?>();
    private SequenceProgram?[] heightSequences = Array.Empty<SequenceProgram?>();
    private AddressMap[][] roomAddressMaps = Array.Empty<AddressMap[]>();
    private byte[][] randomTemplates = Array.Empty<byte[]>();

    public GenerationProgram(MapGenerationPlan plan, IReadOnlyDictionary<string, byte[]> inputs)
    {
        Plan = plan;
        Hash = ForgeCore.Hash.Sha256Hex(MapGenerationPlanCanonicalWriter.Write(plan));
        snapshots = new JsonElement[plan.NativeInputs.GetArrayLength()];
        foreach (var row in plan.NativeInputs.EnumerateArray())
        {
            string path = Text(row, "path");
            if (!inputs.TryGetValue(path, out var bytes)) throw new GenerationRefusal("gen-reference-missing", path);
            if (bytes.LongLength != row.GetProperty("byteLength").GetInt64()
                || ForgeCore.Hash.Sha256Hex(bytes) != Text(row, "sha256"))
                throw new GenerationRefusal("gen-pin-mismatch", path);
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                snapshots[Index(row, "index")] = doc.RootElement.Clone();
                foreach (var block in row.GetProperty("blocks").EnumerateArray())
                    _ = Block(new BlockAddress(Index(row, "index"), Index(block, "index")));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { throw new GenerationRefusal("gen-format", path); }
        }
        BuildFlat();
    }

    private void BuildFlat()
    {
        Resources = Plan.Resources.EnumerateArray().Select(row =>
        {
            int index = Index(row, "index");
            var descriptor = row.GetProperty("descriptor");
            var footprint = descriptor.GetProperty("footprint").EnumerateArray()
                .Select(cell => new CellCoordinate(Index(cell, "x"), Index(cell, "z"))).ToArray();
            var areas = descriptor.GetProperty("areas").EnumerateArray().Select(IdentityOf).ToArray();
            var exits = descriptor.GetProperty("exits").EnumerateArray().Select(exit =>
            {
                var position = exit.GetProperty("position");
                return new ExitProgram(Id(exit), Text(exit, "hierarchyPath"), position.GetProperty("x").GetSingle(),
                    position.GetProperty("y").GetSingle(), position.GetProperty("z").GetSingle(), exit.GetProperty("yaw").GetDouble());
            }).ToArray();
            return new ResourceProgram(index, Id(row), Text(row, "revision"), Text(row, "category"),
                Text(row, "prefabPath"), int.Parse(Text(row, "subComplex")),
                BlockId(row.GetProperty("complexResourceSet")), footprint, areas, exits, Text(row, "origin") == "forge-room");
        }).ToArray();

        int zoneCount = Plan.Zones.GetArrayLength();
        roomSequences = new SequenceProgram?[zoneCount];
        connectionSequences = new SequenceProgram?[zoneCount];
        heightSequences = new SequenceProgram?[zoneCount];
        foreach (var row in Plan.Sequences.EnumerateArray())
        {
            string kind = Text(row, "kind");
            var elements = row.GetProperty("elements");
            var sequence = kind switch
            {
                "room" => new SequenceProgram(row.GetProperty("cycle").GetBoolean(),
                    elements.EnumerateArray().Select(element => Index(element, "resource")).ToArray(),
                    Array.Empty<float>(), elements.EnumerateArray().Select(element => element.Clone()).ToArray()),
                "connection" => new SequenceProgram(row.GetProperty("cycle").GetBoolean(),
                    elements.EnumerateArray().Select(element => element.GetInt32()).ToArray(),
                    Array.Empty<float>(), Array.Empty<JsonElement>()),
                "height" => new SequenceProgram(row.GetProperty("cycle").GetBoolean(), Array.Empty<int>(),
                    elements.EnumerateArray().Select(element => element.GetSingle()).ToArray(), Array.Empty<JsonElement>()),
                _ => throw new GenerationRefusal("gen-format", Id(row) + "/kind")
            };
            int zone = Index(row, "zone");
            if (kind == "room") roomSequences[zone] = sequence;
            else if (kind == "connection") connectionSequences[zone] = sequence;
            else heightSequences[zone] = sequence;
        }

        Dimensions = Plan.Dimensions.EnumerateArray().Select(row =>
        {
            int index = Index(row, "index");
            var config = Block(row.GetProperty("resourceSet")).GetProperty("LevelGenConfig");
            int size = config.GetProperty("GridSize").GetInt32();
            float cell = config.GetProperty("CellDimension").GetSingle();
            if (size <= 0 || !float.IsFinite(cell) || cell <= 0 || (long)size * size > int.MaxValue)
                throw new GenerationRefusal("gen-grid-range", Id(row) + "/grid");
            var block = row.GetProperty("dimensionBlock");
            return new DimensionProgram(index, Id(row), Index(row, "level"), Index(row, "nativeAddress"),
                row.GetProperty("enabled").GetBoolean(), ParseDimension(Text(row, "mode")), Index(row, "rootRoom"),
                Range(row.GetProperty("zones")), row.GetProperty("extraResources").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                BlockId(row.GetProperty("resourceSet")), block.ValueKind == JsonValueKind.Null ? null : BlockId(block),
                new GridProgram(size, cell));
        }).ToArray();

        Zones = Plan.Zones.EnumerateArray().Select(row =>
        {
            int index = Index(row, "index");
            var source = row.GetProperty("source");
            ZoneSource? typedSource = source.ValueKind == JsonValueKind.Null ? null
                : new ZoneSource(Index(source, "room"), Index(source, "area"), Index(source, "exit"));
            var stop = row.GetProperty("stop");
            var coverage = stop.GetProperty("coverage");
            float minimum, maximum;
            if (coverage.ValueKind != JsonValueKind.Null)
            { minimum = coverage.GetProperty("minimum").GetSingle(); maximum = coverage.GetProperty("maximum").GetSingle(); }
            else
            {
                var settings = Block(row.GetProperty("layout")).GetProperty("Zones").EnumerateArray()
                    .Single(value => Index(value, "LocalIndex") == Index(row, "localIndex")).GetProperty("CoverageMinMax");
                minimum = settings.GetProperty("x").GetSingle(); maximum = settings.GetProperty("y").GetSingle();
            }
            var seeds = row.GetProperty("seeds");
            return new ZoneProgram(index, Id(row), Index(row, "dimension"), Index(row, "layer"), Index(row, "localIndex"),
                Index(row, "rootRoom"), Range(row.GetProperty("rooms")), Range(row.GetProperty("connections")), typedSource,
                BlockId(row.GetProperty("layout")), SeedOf(seeds.GetProperty("zone")), SeedOf(seeds.GetProperty("marker")),
                SeedOf(seeds.GetProperty("light")), minimum, maximum, NullableInt(stop.GetProperty("areaCount")),
                NullableInt(stop.GetProperty("expansionCount")));
        }).ToArray();

        Rooms = new RoomProgram[Plan.Rooms.GetArrayLength()];
        roomAddressMaps = new AddressMap[Rooms.Length][];
        foreach (var zone in Zones)
        {
            uint ordinal = 0;
            foreach (int roomIndex in zone.Rooms)
            {
                var row = Plan.Rooms[roomIndex];
                RootKind root = ParseRoot(Text(row, "rootRole"));
                int sourceResource = Index(row, "resource");
                int resource = root == RootKind.Elevator ? sourceResource : Select(roomSequences[zone.Index], ordinal, sourceResource);
                var maps = BuildAddressMaps(row, sourceResource, roomSequences[zone.Index]);
                roomAddressMaps[roomIndex] = maps;
                var areaMap = AddressMapFor(row, sourceResource, resource, maps, true);
                var exitMap = AddressMapFor(row, sourceResource, resource, maps, false);
                var rotation = row.GetProperty("rotation");
                RotationKind rotationKind = ParseRotation(Text(rotation, "mode"));
                double yaw = rotation.TryGetProperty("yaw", out var q) ? q.GetDouble() : 0;
                int sourceRoom = rotation.TryGetProperty("sourceRoom", out var sr) ? sr.GetInt32() : -1;
                double[] candidates = rotation.TryGetProperty("candidates", out var cs)
                    ? cs.EnumerateArray().Select(value => value.GetDouble()).ToArray() : Array.Empty<double>();
                var placement = row.GetProperty("placement");
                var typedPlacement = placement.ValueKind == JsonValueKind.Null ? new PlacementProgram(false, 0, 0, 0, 0)
                    : Text(placement, "space") == "dimension-grid"
                        ? new PlacementProgram(true, Index(placement, "x"), Index(placement, "z"),
                            placement.GetProperty("offsetX").GetSingle(), placement.GetProperty("offsetZ").GetSingle())
                        : throw new GenerationRefusal("gen-placement-unsolved", Id(row) + "/placement");
                bool heightFromSequence = row.GetProperty("height").ValueKind == JsonValueKind.Null && root != RootKind.Elevator;
                float? height = root == RootKind.Elevator ? null : row.GetProperty("height").ValueKind == JsonValueKind.Null
                    ? SelectHeight(heightSequences[zone.Index], ordinal) : row.GetProperty("height").GetSingle();
                int areaCount = Resources[sourceResource].Areas.Length;
                int[] order = Enumerable.Range(0, areaCount).Select(sourceIndex => areaMap[sourceIndex]).ToArray();
                var areaSeeds = new SeedProgram?[Resources[resource].Areas.Length];
                foreach (var rule in Plan.AreaRules.EnumerateArray().Where(rule => Index(rule, "room") == roomIndex))
                {
                    if (rule.GetProperty("sourceAreas").GetArrayLength() != areaCount)
                        throw new GenerationRefusal("gen-area-identity", Id(rule));
                    order = rule.GetProperty("order").EnumerateArray().Select(value => areaMap[value.GetInt32()]).ToArray();
                    for (int sourceIndex = 0; sourceIndex < areaCount; sourceIndex++)
                        areaSeeds[areaMap[sourceIndex]] = SeedOf(rule.GetProperty("seeds")[sourceIndex]);
                }
                Rooms[roomIndex] = new RoomProgram(roomIndex, Id(row), IdentityOf(row.GetProperty("room")), zone.Index,
                    sourceResource, resource, root, NullableInt(row.GetProperty("parentConnection")), NullableInt(row.GetProperty("sourceExit")),
                    typedPlacement, new RotationProgram(rotationKind, yaw, sourceRoom, candidates), height, heightFromSequence, (int)ordinal,
                    areaMap, exitMap, order, areaSeeds,
                    row.TryGetProperty("removedParts", out var removed) ? removed.EnumerateArray().Select(value => value.GetString()!).ToArray() : Array.Empty<string>());
                if (root != RootKind.Elevator) ordinal++;
            }
        }

        Connections = Plan.Connections.EnumerateArray().Select(row =>
        {
            int index = Index(row, "index"), fromRoom = Index(row, "fromRoom"), toRoom = Index(row, "toRoom");
            int fromExit = Index(row, "fromExit"), toExit = Index(row, "toExit");
            var directions = row.GetProperty("directions");
            int?[] directionResources = directions.ValueKind == JsonValueKind.Null ? new int?[4]
                : new[] { NullableInt(directions.GetProperty("forward")), NullableInt(directions.GetProperty("right")),
                    NullableInt(directions.GetProperty("backward")), NullableInt(directions.GetProperty("left")) };
            int? resource;
            if (directions.ValueKind != JsonValueKind.Null)
            {
                int actualExit = Rooms[fromRoom].ExitMap[fromExit];
                int direction = RoomYaw.Compass(Resources[Rooms[fromRoom].Resource].Exits[actualExit].Yaw);
                resource = directionResources[direction] ?? throw new GenerationRefusal("gen-connection-mismatch", Id(row) + "/directions/" + direction);
            }
            else
            {
                var sequence = connectionSequences[Rooms[toRoom].Zone];
                int start = Zones[Rooms[toRoom].Zone].Connections.Length == 0 ? index : Zones[Rooms[toRoom].Zone].Connections[0];
                int ordinal = index - start;
                resource = sequence is not null ? Select(sequence, checked((uint)ordinal), -1)
                    : Text(row, "selection") == "native" ? null : NullableInt(row.GetProperty("resource"));
            }
            return new ConnectionProgram(index, Id(row), fromRoom, fromExit, toRoom, toExit,
                Rooms[fromRoom].ExitMap[fromExit], Rooms[toRoom].ExitMap[toExit],
                Text(row, "edge") == "zone-entry" ? EdgeKind.ZoneEntry : EdgeKind.Internal,
                ParseDoor(Text(row, "door")), row.GetProperty("noDoorProbability").ValueKind == JsonValueKind.Null
                    ? null : row.GetProperty("noDoorProbability").GetDouble(), ParseHeight(Text(row, "heightFamily")),
                resource, directionResources);
        }).ToArray();

        UnpairedExits = Plan.UnpairedExits.EnumerateArray().Select(row =>
        {
            int room = Index(row, "room"), exit = Index(row, "exit");
            return new UnpairedExitProgram(Index(row, "index"), Id(row), room, exit, Rooms[room].ExitMap[exit],
                ParseTreatment(Text(row, "treatment")), NullableInt(row.GetProperty("resource")));
        }).ToArray();

        if (Plan.Root.TryGetProperty("links", out var linkRows))
            Links = linkRows.EnumerateArray().Select(row => new LinkProgram(Index(row, "index"), Id(row), Index(row, "fromRoom"), Index(row, "fromExit"),
                Index(row, "toRoom"), Index(row, "toExit"), Text(row, "door") == "weak", NullableInt(row.GetProperty("resource")))).ToArray();

        Levels = Plan.Levels.EnumerateArray().Select(row =>
        {
            int index = Index(row, "index");
            int[] dimensions = Range(row.GetProperty("dimensions"));
            int[] zones = dimensions.SelectMany(dimension => Dimensions[dimension].Zones).ToArray();
            int[] rooms = zones.SelectMany(zone => Zones[zone].Rooms).ToArray();
            int[] connections = Range(row.GetProperty("connections"));
            int[] exits = UnpairedExits.Where(exit => rooms.Contains(exit.Room)).Select(exit => exit.Index).ToArray();
            int[] links = Links.Where(link => rooms.Contains(link.ToRoom)).Select(link => link.Index).ToArray();
            var required = new HashSet<int>();
            foreach (int dimension in dimensions.Where(value => Dimensions[value].Enabled))
                foreach (int resource in Dimensions[dimension].ExtraResources) required.Add(resource);
            foreach (int room in rooms.Where(value => Dimensions[Zones[Rooms[value].Zone].Dimension].Enabled)) required.Add(Rooms[room].Resource);
            foreach (int connection in connections.Where(value => Dimensions[Zones[Rooms[Connections[value].ToRoom].Zone].Dimension].Enabled))
                if (Connections[connection].Resource is { } resource) required.Add(resource);
            foreach (int exit in exits.Where(value => Dimensions[Zones[Rooms[UnpairedExits[value].Room].Zone].Dimension].Enabled))
                if (UnpairedExits[exit].Resource is { } resource) required.Add(resource);
            foreach (int link in links.Where(value => Dimensions[Zones[Rooms[Links[value].ToRoom].Zone].Dimension].Enabled))
                if (Links[link].Resource is { } resource) required.Add(resource);
            uint[] sets = dimensions.Where(value => Dimensions[value].Enabled).Select(value => Dimensions[value].ResourceSet)
                .Concat(required.Select(resource => Resources[resource].ComplexResourceSet)).Distinct().ToArray();
            return new LevelProgram(index, Id(row), Text(row, "reference"), dimensions, zones, rooms, connections, exits, links,
                required.OrderBy(value => value).ToArray(), sets);
        }).ToArray();
        randomTemplates = new byte[checked(Rooms.Length * 4)][];
        foreach (var room in Rooms)
        {
            var zone = Zones[room.Zone]; var dimension = Dimensions[zone.Dimension]; var level = Levels[dimension.Level];
            for (int decision = 0; decision < 4; decision++)
            {
                string name = ((DecisionKind)decision) switch
                {
                    DecisionKind.Rotation => "rotation",
                    DecisionKind.Door => "door",
                    DecisionKind.Connection => "connection",
                    DecisionKind.Area => "area",
                    _ => throw new InvalidOperationException()
                };
                randomTemplates[room.Index * 4 + decision] = GenerationRandom.EncodeKey(new(0, level.Id, dimension.Id, zone.Id, room.Id, name, 0));
            }
        }
    }

    private AddressMap[] BuildAddressMaps(JsonElement room, int sourceResource, SequenceProgram? sequence)
    {
        if (sequence is null) return Array.Empty<AddressMap>();
        var source = Plan.Resources[sourceResource].GetProperty("descriptor");
        var result = new List<AddressMap>();
        foreach (var element in sequence.RoomElements)
        {
            int resource = Index(element, "resource");
            var matching = element.GetProperty("addresses").EnumerateArray()
                .Where(address => SameIdentity(address.GetProperty("source"), source)).ToArray();
            if (matching.Length == 0) continue;
            if (matching.Skip(1).Any(value => value.GetRawText() != matching[0].GetRawText()))
                throw new GenerationRefusal("gen-sequence-address-unsupported", Id(room) + "/addresses/ambiguous");
            var target = Resources[resource];
            int[] areas = source.GetProperty("areas").EnumerateArray().Select(sourceArea =>
            {
                var matches = matching[0].GetProperty("areas").EnumerateArray()
                    .Where(item => SameIdentity(item.GetProperty("source"), sourceArea)).ToArray();
                if (matches.Length != 1) throw new GenerationRefusal("gen-sequence-address-unsupported", Id(room) + "/areas/" + Id(sourceArea));
                return FindIdentity(target.Areas, IdentityOf(matches[0].GetProperty("target")), Id(room) + "/areas/" + Id(sourceArea));
            }).ToArray();
            int[] exits = source.GetProperty("exits").EnumerateArray().Select(sourceExit =>
            {
                var matches = matching[0].GetProperty("exits").EnumerateArray()
                    .Where(item => Text(item, "source") == Id(sourceExit)).ToArray();
                if (matches.Length != 1) throw new GenerationRefusal("gen-sequence-address-unsupported", Id(room) + "/exits/" + Id(sourceExit));
                string id = Text(matches[0], "target");
                int found = Array.FindIndex(target.Exits, exit => exit.Id == id);
                if (found < 0) throw new GenerationRefusal("gen-sequence-address-unsupported", Id(room) + "/exits/" + Id(sourceExit));
                return found;
            }).ToArray();
            result.Add(new AddressMap(resource, areas, exits));
        }
        return result.ToArray();
    }

    private int[] AddressMapFor(JsonElement room, int sourceResource, int selectedResource, AddressMap[] maps, bool areas)
    {
        int sourceCount = areas ? Resources[sourceResource].Areas.Length : Resources[sourceResource].Exits.Length;
        int targetCount = areas ? Resources[selectedResource].Areas.Length : Resources[selectedResource].Exits.Length;
        int[] map;
        if (selectedResource == sourceResource) map = Enumerable.Range(0, sourceCount).ToArray();
        else
        {
            var found = maps.Where(value => value.Resource == selectedResource).ToArray();
            if (found.Length != 1) throw new GenerationRefusal("gen-sequence-address-unsupported", Id(room) + (areas ? "/areas" : "/exits"));
            map = areas ? found[0].Areas : found[0].Exits;
        }
        if (map.Length != sourceCount || map.Distinct().Count() != targetCount)
            throw new GenerationRefusal("gen-sequence-address-unsupported", Id(room) + (areas ? "/areas" : "/exits"));
        return map;
    }

    public uint BlockId(JsonElement address) => BlockId(new BlockAddress(Index(address, "input"), Index(address, "block")));
    public uint BlockId(BlockAddress address) => Plan.NativeInputs[address.Input].GetProperty("blocks")[address.Block].GetProperty("id").GetUInt32();
    public JsonElement Block(JsonElement address) => Block(new BlockAddress(Index(address, "input"), Index(address, "block")));
    public JsonElement Block(BlockAddress address)
    {
        var definition = Plan.NativeInputs[address.Input].GetProperty("blocks")[address.Block];
        var found = snapshots[address.Input].GetProperty("blocks").EnumerateArray()
            .Where(row => Text(row, "kind") == Text(definition, "kind") && row.GetProperty("id").GetUInt32() == BlockId(address)).ToArray();
        if (found.Length != 1 || !found[0].TryGetProperty("data", out var data)
            || data.GetProperty("persistentID").GetUInt32() != BlockId(address))
            throw new GenerationRefusal("gen-reference-missing", "nativeInputs/" + address);
        return data;
    }

    public (int Size, float CellSize) Grid(int dimension)
        => (Dimensions[dimension].Grid.Size, Dimensions[dimension].Grid.CellSize);
    public float? Height(int room, uint ordinal)
        => Rooms[room].RootRole == RootKind.Elevator ? null : Rooms[room].HeightFromSequence
            ? SelectHeight(heightSequences[Rooms[room].Zone], ordinal) : Rooms[room].Height;
    public double Rotation(int room, double nativeYaw, uint buildSeed, Func<int, double> placedRotation)
    {
        var rotation = Rooms[room].Rotation;
        return rotation.Kind switch
        {
            RotationKind.Native => RoomYaw.Normalize(nativeYaw),
            RotationKind.Absolute => RoomYaw.Normalize(rotation.Yaw),
            RotationKind.Relative => RoomYaw.Normalize(placedRotation(rotation.SourceRoom) + rotation.Yaw),
            RotationKind.Random => RoomYaw.Normalize(rotation.Candidates[(int)(Random(room, DecisionKind.Rotation, buildSeed) % (uint)rotation.Candidates.Length)]),
            _ => throw new GenerationRefusal("gen-rotation-invalid", Rooms[room].Id)
        };
    }
    public uint Random(int room, DecisionKind decision, uint buildSeed, uint ordinal = 0)
    {
        int domain = (int)decision;
        if ((uint)domain >= 4) throw new ArgumentOutOfRangeException(nameof(decision));
        byte[] key = (byte[])randomTemplates[checked(room * 4 + domain)].Clone();
        int buildSeedOffset = 4 + Encoding.UTF8.GetByteCount(GenerationRandom.Prefix);
        BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(buildSeedOffset, 4), buildSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(key.Length - 4, 4), ordinal);
        return BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(key));
    }
    public uint Random(int room, string decision, uint buildSeed, uint ordinal = 0)
    {
        var row = Rooms[room]; var zone = Zones[row.Zone]; var dimension = Dimensions[zone.Dimension];
        return GenerationRandom.SubSeed(new(buildSeed, Levels[dimension.Level].Id, dimension.Id, zone.Id, row.Id, decision, ordinal));
    }
    public uint Seed(JsonElement seed, uint native) => SeedOf(seed).Apply(native);
    public bool? Door(int connection, uint buildSeed)
    {
        var row = Connections[connection];
        if (row.NoDoorProbability is not { } probability) return row.Door == DoorKind.Native ? null : row.Door != DoorKind.None;
        if (row.Edge == EdgeKind.ZoneEntry) throw new GenerationRefusal("gen-source-door-required", row.Id);
        return !(probability >= 1 || probability > 0
            && Random(row.ToRoom, DecisionKind.Door, buildSeed, (uint)connection) / 4294967296d < probability);
    }
    public int RoomArea(int room, int selectedResource, int sourceIndex) => Address(room, selectedResource, sourceIndex, true);
    public int RoomExit(int room, int selectedResource, int sourceIndex) => Address(room, selectedResource, sourceIndex, false);
    private int Address(int room, int selectedResource, int sourceIndex, bool areas)
    {
        int[] map = Map(room, selectedResource, areas);
        if ((uint)sourceIndex >= (uint)map.Length) throw new GenerationRefusal(areas ? "gen-source-area" : "gen-source-expander", Rooms[room].Id);
        return map[sourceIndex];
    }
    private int[] Map(int room, int selectedResource, bool areas)
    {
        var row = Rooms[room];
        if (selectedResource == row.Resource) return areas ? row.AreaMap : row.ExitMap;
        if (selectedResource == row.SourceResource)
            return Enumerable.Range(0, areas ? Resources[selectedResource].Areas.Length : Resources[selectedResource].Exits.Length).ToArray();
        var matches = roomAddressMaps[room].Where(value => value.Resource == selectedResource).ToArray();
        if (matches.Length != 1) throw new GenerationRefusal("gen-sequence-address-unsupported", row.Id + (areas ? "/areas" : "/exits"));
        return areas ? matches[0].Areas : matches[0].Exits;
    }
    public (int[] Order, uint[] Seeds) Areas(int room, IReadOnlyList<uint> nativeSeeds, int? selectedResource = null)
    {
        var row = Rooms[room]; int selected = selectedResource ?? row.Resource;
        int[] map = Map(room, selected, true);
        if (nativeSeeds.Count != Resources[selected].Areas.Length) throw new GenerationRefusal("gen-area-identity", row.Id + "/areas");
        int[] order = row.AreaOrder.Length == map.Length && selected == row.Resource
            ? row.AreaOrder : Enumerable.Range(0, map.Length).Select(source => map[source]).ToArray();
        uint[] seeds = nativeSeeds.ToArray();
        if (selected == row.Resource)
            for (int target = 0; target < row.AreaSeeds.Length; target++)
                if (row.AreaSeeds[target] is { } seed) seeds[target] = seed.Apply(nativeSeeds[target]);
        return (order, seeds);
    }
    public (float Minimum, float Maximum) Coverage(int zone) => (Zones[zone].CoverageMinimum, Zones[zone].CoverageMaximum);
    public void RequireSupportedDimensions()
    {
        foreach (var dimension in Dimensions)
        {
            if (dimension.NativeAddress == 0 && (!dimension.Enabled || dimension.Mode != DimensionKind.Native))
                throw new GenerationRefusal("gen-dimension-mismatch", dimension.Id + "/Reality: native Setup always creates the elevator dimension");
            if ((dimension.NativeAddress == 22 || dimension.Mode == DimensionKind.Arena) && dimension.Enabled)
                throw new GenerationRefusal("gen-dimension-unsupported", dimension.Id + "/arena-slots: native ARENA_DIMENSION expands into live addresses 20,19,18,17; a single address cannot identify these four worlds");
        }
    }
    public void RequireSupportedRequests()
    {
        foreach (var dimension in Dimensions)
            if (dimension.Grid.CellSize != 64f)
                throw new GenerationRefusal("gen-grid-cell-unsupported", $"dimensions[{dimension.Index}].grid");
        RequireSupportedDimensions();
        foreach (var room in Rooms)
            if (room.RootRole is not (RootKind.Elevator or RootKind.Dimension) && !room.Placement.Present)
                throw new GenerationRefusal("gen-placement-unsolved", room.Id + "/placement: website did not emit an absolute dimension-grid solution");
        RequireOfflinePlacements();
        foreach (var zone in Zones)
        {
            int actualAreas = 0, expansions = 0;
            foreach (int roomIndex in zone.Rooms)
            {
                var room = Rooms[roomIndex];
                actualAreas = checked(actualAreas + Resources[room.Resource].Areas.Length);
                if (room.RootRole is not (RootKind.Elevator or RootKind.Dimension)) expansions++;
            }
            if (zone.AreaCount is { } areaCount && areaCount != actualAreas)
                throw new GenerationRefusal("gen-area-count-unsatisfied", $"zones[{zone.Index}].stop.areaCount",
                    $"requested={areaCount}, actual={actualAreas}, difference={actualAreas - areaCount}");
            if (zone.ExpansionCount is { } expansionCount && expansionCount != expansions)
                throw new GenerationRefusal("gen-expansion-count-unsatisfied", $"zones[{zone.Index}].stop.expansionCount",
                    $"requested={expansionCount}, actual={expansions}");
        }
        RequireOfflineConnections();
    }
    private void RequireOfflinePlacements()
    {
        var occupied = new Dictionary<(int Dimension, int X, int Z), string>();
        foreach (var room in Rooms)
        {
            if (room.Placement.Present && (!float.IsFinite(room.Placement.OffsetX) || !float.IsFinite(room.Placement.OffsetZ)))
                throw new GenerationRefusal("gen-format", $"rooms[{room.Index}].placement");
            if (!room.Placement.Present || room.Rotation.Kind != RotationKind.Absolute) continue;
            var dimension = Dimensions[Zones[room.Zone].Dimension];
            string path = $"rooms[{room.Index}].placement";
            long anchorX = dimension.Grid.Size / 2L + room.Placement.X;
            long anchorZ = dimension.Grid.Size / 2L + room.Placement.Z;
            if (anchorX < int.MinValue || anchorX > int.MaxValue || anchorZ < int.MinValue || anchorZ > int.MaxValue)
                throw new GenerationRefusal("gen-grid-range", path);
            CellCoordinate[] cells;
            try { cells = Footprint(room.Resource, new((int)anchorX, (int)anchorZ), room.Rotation.Yaw); }
            catch (GenerationRefusal error) when (error.Code == "gen-grid-range")
            { throw new GenerationRefusal("gen-grid-range", path); }
            foreach (var cell in cells)
            {
                if (cell.X < 0 || cell.X >= dimension.Grid.Size || cell.Z < 0 || cell.Z >= dimension.Grid.Size)
                    throw new GenerationRefusal("gen-grid-range", path);
                var key = (dimension.Index, cell.X, cell.Z);
                if (occupied.TryGetValue(key, out string? owner) && owner != room.Id)
                    throw new GenerationRefusal("gen-room-overlap", path, "owner=" + owner);
                occupied[key] = room.Id;
            }
        }
    }
    private void RequireOfflineConnections()
    {
        // Each published native socket may sit up to 1 m off its cell-edge midpoint; a meeting pair gets both allowances.
        const double tolerance = 2;
        foreach (var connection in Connections)
        {
            var from = Rooms[connection.FromRoom]; var to = Rooms[connection.ToRoom];
            if (from.Rotation.Kind != RotationKind.Absolute || to.Rotation.Kind != RotationKind.Absolute) continue;
            var fromExit = Resources[from.Resource].Exits[connection.ActualFromExit];
            var toExit = Resources[to.Resource].Exits[connection.ActualToExit];
            if (!RoomYaw.Same(from.Rotation.Yaw + fromExit.Yaw + 180, to.Rotation.Yaw + toExit.Yaw))
                throw new GenerationRefusal("gen-exit-mismatch", $"connections[{connection.Index}].rotation");
            if (!from.Placement.Present || !to.Placement.Present) continue;
            var fromCells = Footprint(from.Resource, default, from.Rotation.Yaw);
            var toCells = Footprint(to.Resource, default, to.Rotation.Yaw);
            double fromCenterX = (fromCells.Min(cell => cell.X) + fromCells.Max(cell => cell.X)) / 2d;
            double fromCenterZ = (fromCells.Min(cell => cell.Z) + fromCells.Max(cell => cell.Z)) / 2d;
            double toCenterX = (toCells.Min(cell => cell.X) + toCells.Max(cell => cell.X)) / 2d;
            double toCenterZ = (toCells.Min(cell => cell.Z) + toCells.Max(cell => cell.Z)) / 2d;
            (double fromX, double fromZ) = RoomYaw.Turn(fromExit.X, fromExit.Z, from.Rotation.Yaw);
            (double toX, double toZ) = RoomYaw.Turn(toExit.X, toExit.Z, to.Rotation.Yaw);
            double cell = Dimensions[Zones[from.Zone].Dimension].Grid.CellSize;
            double dx = (from.Placement.X + fromCenterX - to.Placement.X - toCenterX) * cell
                + from.Placement.OffsetX - to.Placement.OffsetX + fromX - toX;
            double dz = (from.Placement.Z + fromCenterZ - to.Placement.Z - toCenterZ) * cell
                + from.Placement.OffsetZ - to.Placement.OffsetZ + fromZ - toZ;
            if (Math.Abs(dx) > tolerance || Math.Abs(dz) > tolerance)
                throw new GenerationRefusal("gen-exit-mismatch", $"connections[{connection.Index}].placement");
        }
    }
    public CellCoordinate[] Footprint(int resource, CellCoordinate anchor, double yaw)
        => Resources[resource].ForgeRoom ? new[] { anchor } : DeclaredFootprints.Place(Resources[resource].Footprint, anchor, yaw);
    public void VerifyStop(int zone, int actualAreas, float actualCoverage, float minimum, float maximum, int expansions)
    {
        var row = Zones[zone];
        if (row.AreaCount is { } areaCount && areaCount != actualAreas)
            throw new GenerationRefusal("gen-area-count-unsatisfied", $"{row.Id}/areaCount: requested={areaCount}, actual={actualAreas}, difference={actualAreas - areaCount}");
        if (!float.IsFinite(actualCoverage) || actualCoverage < minimum)
            throw new GenerationRefusal("gen-coverage-unsatisfied", $"{row.Id}/coverage: actual={actualCoverage:R}, minimum={minimum:R}, maximum={maximum:R}");
        if (row.ExpansionCount is { } expansionCount && expansionCount != expansions)
            throw new GenerationRefusal("gen-expansion-count-unsatisfied", $"{row.Id}/expansionCount: requested={expansionCount}, actual={expansions}");
    }
    public LevelProgram? Level(string reference)
    {
        foreach (var level in Levels) if (level.Reference == reference) return level;
        return null;
    }
    public int? DimensionAt(LevelProgram level, int nativeAddress)
    {
        int found = -1;
        foreach (int index in level.Dimensions) if (Dimensions[index].NativeAddress == nativeAddress)
        { if (found >= 0) throw new GenerationRefusal("gen-dimension-mismatch", level.Id + "/nativeAddress"); found = index; }
        return found < 0 ? null : found;
    }

    private static int[] Range(JsonElement range)
    {
        int start = Index(range, "start"), count = Index(range, "count");
        return Enumerable.Range(start, count).ToArray();
    }
    private static int Select(SequenceProgram? sequence, uint ordinal, int fallback)
        => sequence is null ? fallback : sequence.Resources[GenerationSequence.Index(sequence.Resources.Length, sequence.Cycle, ordinal)];
    private static float? SelectHeight(SequenceProgram? sequence, uint ordinal)
        => sequence is null ? null : sequence.Heights[GenerationSequence.Index(sequence.Heights.Length, sequence.Cycle, ordinal)];
    private static int? NullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    private static SeedProgram SeedOf(JsonElement seed) => Text(seed, "mode") == "native" ? new(true, 0)
        : new(false, Text(seed, "type") == "uint32" ? seed.GetProperty("value").GetUInt32()
            : unchecked((uint)seed.GetProperty("value").GetInt32()));
    private static Identity IdentityOf(JsonElement row) => new(Id(row), Text(row, "revision"));
    private static bool SameIdentity(JsonElement a, JsonElement b) => IdentityOf(a) == IdentityOf(b);
    private static int FindIdentity(Identity[] values, Identity target, string path)
    {
        int found = Array.FindIndex(values, value => value == target);
        if (found < 0 || Array.FindLastIndex(values, value => value == target) != found)
            throw new GenerationRefusal("gen-sequence-address-unsupported", path);
        return found;
    }
    private static RootKind ParseRoot(string value) => value switch
    {
        "none" => RootKind.None,
        "elevator" => RootKind.Elevator,
        "dimension" => RootKind.Dimension,
        "zone" => RootKind.Zone,
        _ => throw new GenerationRefusal("gen-format", "rootRole/" + value)
    };
    private static DimensionKind ParseDimension(string value) => value switch
    {
        "native" => DimensionKind.Native,
        "static" => DimensionKind.Static,
        "arena" => DimensionKind.Arena,
        _ => throw new GenerationRefusal("gen-format", "dimension/" + value)
    };
    private static RotationKind ParseRotation(string value) => value switch
    {
        "native" => RotationKind.Native,
        "absolute" => RotationKind.Absolute,
        "relative" => RotationKind.Relative,
        "random" => RotationKind.Random,
        _ => throw new GenerationRefusal("gen-format", "rotation/" + value)
    };
    private static DoorKind ParseDoor(string value) => value switch
    {
        "native" => DoorKind.Native,
        "none" => DoorKind.None,
        "weak" => DoorKind.Weak,
        "security" => DoorKind.Security,
        "apex" => DoorKind.Apex,
        "bulkhead" => DoorKind.Bulkhead,
        _ => throw new GenerationRefusal("gen-format", "door/" + value)
    };
    private static HeightKind ParseHeight(string value) => value switch
    {
        "same" => HeightKind.Same,
        "single-drop" => HeightKind.SingleDrop,
        "double-drop" => HeightKind.DoubleDrop,
        _ => throw new GenerationRefusal("gen-format", "height/" + value)
    };
    private static ExitTreatment ParseTreatment(string value) => value switch
    {
        "open" => ExitTreatment.Open,
        "cap" => ExitTreatment.Cap,
        "divider" => ExitTreatment.Divider,
        _ => throw new GenerationRefusal("gen-format", "exit/" + value)
    };
    public static string Text(JsonElement row, string key) => row.GetProperty(key).GetString()!;
    public static string Id(JsonElement row) => Text(row, "id");
    public static int Index(JsonElement row, string key) => row.GetProperty(key).GetInt32();
}
