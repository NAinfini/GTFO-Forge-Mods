using System;
using System.Collections.Generic;
using ForgeMap.Generation;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    private static void RegisterRoomAreas(LG_ZoneJob_CreateExpandFromData job, LG_Zone zone, LG_Geomorph room, string path)
    {
        var registered = zone.m_areas ?? throw new GenerationRefusal("gen-area-identity", path + "/zone-areas");
        var unique = new HashSet<IntPtr>();
        // Check the whole room before native AddArea can overwrite an owner or publish a partial room.
        foreach (var area in room.m_areas)
        {
            if (area == null || area.WasCollected || !unique.Add(area.Pointer))
                throw new GenerationRefusal("gen-area-identity", path + "/area-duplicate");
            int references = GeneratedRoomRegistry.AreaReferences(zone, area);
            if (area.m_geomorph?.Pointer != room.Pointer || area.m_zone != null && area.m_zone.Pointer != zone.Pointer
                || references != (area.m_zone == null ? 0 : 1))
                throw new GenerationRefusal("gen-area-identity", path + "/area-owner", GeneratedRoomRegistry.AreaOwnerDetail(zone, room, area));
        }
        foreach (var area in room.m_areas)
        {
            // AddArea RVA 0x16556d0 writes Area.m_zone, appends zone.m_areas and adds GetCoverage.
            // The native first-zone Init has already added the elevator's first Area; do not count it twice.
            if (!registered.Contains(area)) job.AddArea(zone, area);
            if (area.m_zone?.Pointer != zone.Pointer || GeneratedRoomRegistry.AreaReferences(zone, area) != 1)
                throw new GenerationRefusal("gen-area-identity", path + "/area-owner-readback", GeneratedRoomRegistry.AreaOwnerDetail(zone, room, area));
            if (!zone.m_potentialExpansionAreas.Contains(area)) zone.m_potentialExpansionAreas.Add(area);
        }
    }
}
