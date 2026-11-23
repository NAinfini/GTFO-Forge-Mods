using AIGraph;
using System.Collections.Generic;
using ForgeMap.Generation;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    /// <summary>The Area pointer is frozen when its room publishes the Registry receipt. The CourseNode is filled
    /// on the existing batch 25 barrier, after batch 6 PrepareZones created and attached the node. The same
    /// object is retained by the room readback, so serialization can never preserve the earlier null.</summary>
    internal sealed class AuthorAreaReadbackRow
    {
        internal AuthorAreaReadbackRow(string idValue, string revisionValue, long areaPointer, long zonePointer)
        { id = idValue; revision = revisionValue; nativeArea = areaPointer; nativeZone = zonePointer; }

        public string id { get; }
        public string revision { get; }
        public long nativeArea { get; }
        public long nativeZone { get; }
        public long? nativeCourseNode { get; private set; }

        internal void Complete(long pointer)
        {
            if (nativeCourseNode is { } prior && prior != pointer)
                throw new GenerationRefusal("gen-area-identity", id + "/course-node-rebind");
            nativeCourseNode = pointer;
        }
    }

    internal static GeneratedRoomRegistry.GeneratedAreaBinding[] SelectAuthorAreaBindings(
        GenerationProgram.ResourceProgram authorResource, int[] areaMap, IReadOnlyList<LG_Area> originalAreas,
        IReadOnlyList<LG_Area> activeAreas, string path)
    {
        var active = new HashSet<nint>();
        foreach (var area in activeAreas)
            if (area == null || area.WasCollected || !active.Add(area.Pointer))
                throw new GenerationRefusal("gen-area-identity", path + "/active-author-areas");
        var matched = new HashSet<nint>(); var bindings = new List<GeneratedRoomRegistry.GeneratedAreaBinding>(active.Count);
        for (int sourceIndex = 0; sourceIndex < authorResource.Areas.Length; sourceIndex++)
        {
            if ((uint)sourceIndex >= (uint)areaMap.Length || (uint)areaMap[sourceIndex] >= (uint)originalAreas.Count)
                throw new GenerationRefusal("gen-area-identity", path + "/author-area-map");
            var selected = originalAreas[areaMap[sourceIndex]];
            if (!active.Contains(selected.Pointer)) continue;
            if (!matched.Add(selected.Pointer)) throw new GenerationRefusal("gen-area-identity", path + "/author-area-map");
            var identity = authorResource.Areas[sourceIndex]; bindings.Add(new(identity.Id, identity.Revision, selected));
        }
        if (!matched.SetEquals(active)) throw new GenerationRefusal("gen-area-identity", path + "/active-author-areas");
        return bindings.ToArray();
    }

    /// <summary>Reads the just-published Area identity through the shared Registry. This happens after placement
    /// selection and stores the mutable DTO references held by the room diagnostic.</summary>
    private static AuthorAreaReadbackRow[] AuthorAreaReadback(Run run, LG_Zone zone, RoomLocator locator,
        GeneratedRoomRegistry.GeneratedAreaBinding[] bindings)
    {
        var rows = new AuthorAreaReadbackRow[bindings.Length];
        for (var index = 0; index != bindings.Length; index++)
        {
            var binding = bindings[index];
            var area = GeneratedRoomRegistry.FindArea(run.Epoch, run.LevelReference, zone, locator,
                binding.Id, binding.Revision, out var refusal);
            if (area == null || area.Pointer != binding.Area.Pointer)
                throw new GenerationRefusal("gen-area-identity", locator.InstanceId + "/" + binding.Id + ":" + refusal);
            if (GeneratedRoomRegistry.AreaReferences(zone, area) != 1)
                throw new GenerationRefusal("gen-area-identity", locator.InstanceId + "/author-zone-membership",
                    GeneratedRoomRegistry.AreaOwnerDetail(zone, binding.Area.m_geomorph!, area));
            rows[index] = new(binding.Id, binding.Revision, area.Pointer.ToInt64(), area.m_zone!.Pointer.ToInt64());
        }
        return rows;
    }

    private static object[] AuthorExitReadback(Run run, LG_Zone zone, RoomLocator locator,
        GeneratedRoomRegistry.GeneratedExitBinding[] bindings)
    {
        var rows = new object[bindings.Length];
        for (var index = 0; index != bindings.Length; index++)
        {
            var binding = bindings[index];
            var plug = GeneratedRoomRegistry.FindExit(run.Epoch, run.LevelReference, zone, locator,
                binding.Id, out var refusal);
            if (plug == null || plug.Pointer != binding.Plug.Pointer)
                throw new GenerationRefusal("gen-exit-mismatch", locator.InstanceId + "/" + binding.Id + ":" + refusal);
            rows[index] = new { id = binding.Id, nativePlug = plug.Pointer.ToInt64() };
        }
        return rows;
    }

    internal static void FinalizeAuthorAreaReadback(long epoch, MapLevelReference level, LG_Zone zone,
        RoomLocator locator, LG_Geomorph root, AuthorAreaReadbackRow row, string path)
    {
        var area = GeneratedRoomRegistry.FindArea(epoch, level, zone, locator, row.id, row.revision, out var refusal);
        if (area == null || area.WasCollected || area.Pointer.ToInt64() != row.nativeArea || area.m_geomorph == null
            || area.m_geomorph.Pointer != root.Pointer || area.m_zone == null || area.m_zone.Pointer != zone.Pointer)
            throw new GenerationRefusal("gen-area-identity", path + "/author-area:" + refusal);
        AIG_CourseNode? node = area.m_courseNode;
        if (node == null || node.WasCollected || node.m_area == null || node.m_area.Pointer != area.Pointer)
            throw new GenerationRefusal("gen-area-identity", path + "/author-course-node");
        row.Complete(node.Pointer.ToInt64());
    }

    private static (int Rooms, int Areas) FinalizeAuthorAreaReadbacks(Run run)
    {
        int rooms = 0, areas = 0;
        foreach (int index in run.LevelProgram.Rooms)
        {
            if (!EnabledRoom(run, index)) continue;
            var definition = run.Program.Rooms[index];
            var placed = run.Rooms[index] ?? throw new GenerationRefusal("gen-reference-missing", definition.Id + "/author-room");
            var locator = run.Locators[index] ?? throw new GenerationRefusal("gen-reference-missing", definition.Id + "/author-locator");
            var zone = placed.Root.m_zone ?? throw new GenerationRefusal("gen-area-identity", definition.Id + "/author-zone");
            var rows = run.AuthorAreaReadbacks[index]; rooms++;
            foreach (var row in rows)
            {
                FinalizeAuthorAreaReadback(run.Epoch, run.LevelReference, zone, locator, placed.Root, row, definition.Id);
                areas++;
            }
        }
        return (rooms, areas);
    }
}
