using System;
using System.Collections.Generic;
using ForgeMap.Generation;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    // Prefab m_plugs is a runtime cache, not the declared sockets. SetupAreas (RVA
    // 0x12ad100) replaces it, then LG_Area.Setup fills it. Resolve the actual hierarchy
    // once while resources are loaded; placement follows only these integer addresses.
    internal static int[][] ResolveExitAddresses(LG_Geomorph root, GenerationProgram.ResourceProgram resource)
    {
        var result = new int[resource.Exits.Length][];
        for (int exit = 0; exit < result.Length; exit++)
        {
            var descriptor = resource.Exits[exit]; string path = resource.Id + "/" + descriptor.Id;
            var parts = descriptor.HierarchyPath.Split('/');
            int first = parts[0] == root.transform.name ? 1 : 0;
            var address = new int[parts.Length - first]; var node = root.transform;
            for (int part = first; part < parts.Length; part++)
            {
                int found = -1;
                for (int child = 0; child < node.childCount; child++)
                {
                    if (node.GetChild(child).name != parts[part]) continue;
                    if (found >= 0) throw new GenerationRefusal("gen-exit-ambiguous", path);
                    found = child;
                }
                if (found < 0) throw new GenerationRefusal("gen-exit-missing", path);
                address[part - first] = found; node = node.GetChild(found);
            }
            if (node.GetComponent<LG_Plug>() == null) throw new GenerationRefusal("gen-exit-missing", path);
            result[exit] = address;
        }
        return result;
    }

    internal static int[] BindExitOrdinals(LG_Geomorph root, int[][] addresses,
        GenerationProgram.ResourceProgram resource, string path)
    {
        if (root.m_plugs == null) throw new GenerationRefusal("gen-stage-invalid", path + "/plugs-before-setup");
        var ordinalByPointer = new Dictionary<nint, int>();
        var ambiguousPointers = new HashSet<nint>();
        for (int ordinal = 0; ordinal < root.m_plugs.Count; ordinal++)
        {
            var plug = root.m_plugs[ordinal];
            if (plug == null) continue;
            if (!ordinalByPointer.TryAdd(plug.Pointer, ordinal)) ambiguousPointers.Add(plug.Pointer);
        }
        var ordinals = new int[addresses.Length]; Array.Fill(ordinals, -1);
        for (int exit = 0; exit < addresses.Length; exit++)
        {
            var node = root.transform;
            foreach (int child in addresses[exit])
            {
                if ((uint)child >= (uint)node.childCount) throw new GenerationRefusal("gen-exit-missing", path + "/" + resource.Exits[exit].Id);
                node = node.GetChild(child);
            }
            var plug = node.GetComponent<LG_Plug>() ?? throw new GenerationRefusal("gen-exit-missing", path + "/" + resource.Exits[exit].Id);
            if (ambiguousPointers.Contains(plug.Pointer))
                throw new GenerationRefusal("gen-exit-ambiguous", path + "/" + resource.Exits[exit].Id);
            if (!ordinalByPointer.TryGetValue(plug.Pointer, out ordinals[exit]))
                throw new GenerationRefusal("gen-exit-missing", path + "/" + resource.Exits[exit].Id);
        }
        return ordinals;
    }

    private static int[] RoomExitOrdinals(Run run, int room, int resource, LG_Geomorph root)
    {
        if (run.PlugOrdinals[room] is { } cached)
        {
            if (run.PlugRoots[room] != root.Pointer)
                throw new GenerationRefusal("gen-exit-mismatch", run.Program.Rooms[room].Id + "/plug-cache-owner");
            return cached;
        }
        var addresses = run.ExitAddresses[resource]
            ?? throw new GenerationRefusal("gen-stage-invalid", run.Program.Rooms[room].Id + "/plugs-not-resolved");
        var ordinals = BindExitOrdinals(root, addresses, run.Program.Resources[resource], run.Program.Rooms[room].Id);
        run.PlugRoots[room] = root.Pointer; run.PlugOrdinals[room] = ordinals;
        return ordinals;
    }
}
