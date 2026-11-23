using System;
using System.Collections.Generic;
using ForgeMap.Generation;
using LevelGeneration;
using static ForgeMap.Generation.GenerationProgram;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    // CleanupFailedZoneBuilds is the first unoccupied native boundary reached after a
    // first-zone Init could not pop an expander.  The caller has already performed the
    // native Init writes; changing the status here leaves the next Build to execute the
    // game's complete Done branch.
    private static void RootZoneCleanup(LG_ZoneJob_CreateExpandFromData __instance)
    {
        if (!Live(out var run) || run.Failed) return;
        try
        {
            var nativeZone = __instance.m_zone;
            if (nativeZone == null || ZoneIndex(run, nativeZone) is not { } zoneIndex || run.Zones[zoneIndex] != null) return;
            var zone = run.Program.Zones[zoneIndex]; int rootIndex = zone.RootRoom;
            var room = run.Program.Rooms[rootIndex];
            if (room.RootRole is not (RootKind.Elevator or RootKind.Dimension)) return;
            // A root room with no door at all has nothing native can grow the zone from, so its zone always ends here, whatever
            // other rooms it holds (they stand at their own places). A root with doors ends here only when it is the whole zone.
            bool closedRoot = room.ExitMap.Length == 0;
            if (!closedRoot && (zone.Rooms.Length != 1 || zone.Rooms[0] != rootIndex)) return;
            var status = __instance.m_mainStatus;
            if (status is not (LG_ZoneJob_CreateExpandFromData.MainStatus.Failed or LG_ZoneJob_CreateExpandFromData.MainStatus.Done)) return;

            // A Failed callback is accepted only after the real first-zone Init completed.
            // Do not turn an arbitrary failed job into a successful authored zone.
            // Native Init (Build RVA 0x16561B0) sets m_buildFromZone to the zone, m_startArea to the start tile's first area,
            // AddArea's it and copies it to m_buildFromArea. A Failed status is written right after BlockAndCleanFailedAreasFromZone
            // (RVA 0x1655920), which moves every area of the zone into an LG_FailedZoneBuild: Area.m_zone = null, zone.m_areas
            // cleared, m_coverage = 0. CleanupFailedZoneBuilds (RVA 0x165A800) only unblocks those areas and leaves the job
            // fields alone, so on Failed the start area has no zone; PlaceZone's native AddArea gives it back below.
            var start = __instance.m_startArea; var current = __instance.m_buildFromArea;
            bool failed = status == LG_ZoneJob_CreateExpandFromData.MainStatus.Failed;
            var currentZone = current?.m_zone;
            if (__instance.m_buildFromZone?.Pointer != nativeZone.Pointer || start == null || current == null
                || start.Pointer != current.Pointer || (failed ? currentZone != null && currentZone.Pointer != nativeZone.Pointer : currentZone?.Pointer != nativeZone.Pointer))
                throw new GenerationRefusal("gen-stage-invalid", zone.Id + "/root-native-init");
            var dimension = nativeZone.Dimension;
            var nativeRoot = dimension.m_startTile?.m_geoRoot
                ?? throw new GenerationRefusal("gen-root-invalid", room.Id + "/native-root");
            ResolveAssets(run); var asset = Asset(run, room.Resource);
            if (nativeRoot.m_geoPrefab == null || nativeRoot.m_geoPrefab.Pointer != asset.Pointer)
                throw new GenerationRefusal("gen-pin-mismatch", room.Id + "/root-prefab-readback");

            LG_Plug? source = null; LG_Area? sourceArea = null;
            for (int exit = 0; exit < room.ExitMap.Length; exit++)
            {
                var candidate = PlugOnRoot(run, rootIndex, room.Resource, nativeRoot, exit);
                var area = candidate.OriginalParentArea;
                if (area == null || !__instance.IsExpanderFree(candidate)) continue;
                bool owned = false; foreach (var rootArea in nativeRoot.m_areas)
                    if (rootArea.Pointer == area.Pointer) { owned = true; break; }
                if (!owned) throw new GenerationRefusal("gen-area-identity", room.Id + "/root-source-area");
                source = candidate; sourceArea = area; break;
            }
            // A root room with no door at all has nothing to grow a zone from and nothing attaches to it: the zone grows from
            // its native start area. A door that exists but is not free is refused.
            if (closedRoot)
            {
                bool owned = false; foreach (var rootArea in nativeRoot.m_areas)
                    if (rootArea.Pointer == start.Pointer) { owned = true; break; }
                if (!owned) throw new GenerationRefusal("gen-area-identity", room.Id + "/root-source-area");
                sourceArea = start;
            }
            else if (source == null || sourceArea == null)
                throw new GenerationRefusal("gen-source-unavailable", room.Id + "/root-only-exit");

            var bounds = run.Program.Coverage(zoneIndex);
            var settings = nativeZone.m_settings ?? throw new GenerationRefusal("gen-stage-invalid", zone.Id + "/zone-settings");
            if (__instance.m_zoneSettings == null || __instance.m_zoneSettings.Pointer != settings.Pointer)
                throw new GenerationRefusal("gen-stage-invalid", zone.Id + "/zone-settings-owner");
            // Native Failed temporarily writes min=1 before cleanup. Restore only the frozen
            // authored bounds, then let RegisterZone verify the real Area/GetCoverage totals.
            settings.m_minCoverage = bounds.Minimum; settings.m_maxCoverage = bounds.Maximum;
            run.ExpansionCalls[zoneIndex] = 0;
            var zoneAreas = nativeZone.m_areas ?? throw new GenerationRefusal("gen-area-identity", zone.Id);
            float registeredCoverage = 0; var registered = new HashSet<IntPtr>();
            foreach (var area in zoneAreas)
            {
                if (area == null || !registered.Add(area.Pointer) || area.m_zone?.Pointer != nativeZone.Pointer)
                    throw new GenerationRefusal("gen-area-identity", room.Id + "/native-root-registration");
                registeredCoverage += area.GetCoverage();
            }
            if (registeredCoverage != nativeZone.m_coverage)
                throw new GenerationRefusal("gen-coverage-unsatisfied", zone.Id + "/native-root-readback");
            var floor = Builder.CurrentFloor ?? throw new GenerationRefusal("gen-world-unavailable", room.Id + "/floor");
            PlaceZone(run, zoneIndex, floor, nativeZone, source, sourceArea,
                run.Program.Random(rootIndex, DecisionKind.Area, run.Seed), __instance);
            RegisterZone(run, zoneIndex, __instance, nativeZone);
            if (nativeZone.m_sourceExpander?.Pointer != source?.Pointer || sourceArea.m_zone?.Pointer != nativeZone.Pointer)
                throw new GenerationRefusal("gen-source-unavailable", room.Id + "/root-only-source-readback");
            if (source != null) __instance.m_buildFromExpander = source;
            __instance.m_buildFromArea = sourceArea;
            __instance.m_buildFromZone = sourceArea.m_zone;

            if (status == LG_ZoneJob_CreateExpandFromData.MainStatus.Failed)
            {
                __instance.m_mainStatus = LG_ZoneJob_CreateExpandFromData.MainStatus.Done;
                __instance.m_subStatus = LG_ZoneJob_CreateExpandFromData.SubStatus.Done;
            }
            run.Record("root-only-native", zone.Id, new
            {
                room = room.Id,
                nativeInit = true,
                nativeDone = status == LG_ZoneJob_CreateExpandFromData.MainStatus.Done,
                nativeDonePending = status == LG_ZoneJob_CreateExpandFromData.MainStatus.Failed,
                areas = (nativeZone.m_areas ?? throw new GenerationRefusal("gen-area-identity", zone.Id)).Count,
                coverage = nativeZone.m_coverage,
                source = source?.Pointer.ToInt64() ?? 0L,
                closed = closedRoot
            });
        }
        catch (Exception error) { Refuse(run, error); }
    }

    // Build's Done (RVA 0x16596E8) marks zone.m_sourceExpander, the exit a zone grows from, as its zone source, and
    // LG_Floor.PrepZone (RVA 0x15D0D40, batch PrepareZones) dereferences both linked areas of every expander
    // so marked; a marked source without them ends in a NullReferenceException on every PrepZones.Build and the
    // batch never advances. A source expander provides them only when it links both areas, and a plug also needs
    // a live reciprocal partner that links both areas. An incomplete base expander or half a pair cannot, so the
    // mark that native Done wrote is released before the batch runs: the exit is then an ordinary free exit,
    // which PrepZone and the plug jobs handle. This is any zone whose first room has no door attached: ExpandZone
    // grows it from the parent's free plug the game picked, and nothing pairs with that plug.
    private static void ReleaseUnpairedRootSources(Run run)
    {
        foreach (int zoneIndex in run.LevelProgram.Zones)
        {
            var zone = run.Program.Zones[zoneIndex];
            if (run.Zones[zoneIndex]?.m_sourceExpander is not { } source || !source.m_isZoneSource || IsCompletePair(source)) continue;
            source.m_isZoneSource = false;
            run.Record("root-source-released", zone.Id, new
            {
                room = run.Program.Rooms[zone.RootRoom].Id,
                source = source.Pointer.ToInt64(),
                sourceType = source.GetType().Name,
                paired = source is LG_Plug { m_pariedWith: not null },
                linksFrom = source.m_linksFrom != null,
                linksTo = source.m_linksTo != null
            });
        }
    }

    // PrepZone dereferences both linked areas; a base expander that carries them is enough. A plug additionally
    // has to name a live reciprocal partner: an unpaired plug is the nominal free exit this release exists for.
    private static bool IsCompletePair(LG_ZoneExpander source)
    {
        if (source.m_linksFrom == null || source.m_linksTo == null) return false;
        if (source is not LG_Plug plug) return true;
        var other = plug.m_pariedWith;
        return other is { WasCollected: false } && other.m_pariedWith?.Pointer == plug.Pointer
            && other.m_linksFrom != null && other.m_linksTo != null;
    }
}
