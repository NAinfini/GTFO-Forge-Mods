using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Read-only evidence for where a zone's source exit stands. The game marks it in LG_ZoneJob_CreateExpandFromData.Build
/// (RVA 0x16561B0): after ExpandZone (called at RVA 0x1659034) returns, Build stores its own candidate exit in zone.m_sourceExpander
/// when that field is null (RVA 0x16590D5 / 0x165914B), and Done (RVA 0x16596E8) sets m_isZoneSource on zone.m_sourceExpander
/// unconditionally. LG_Floor.SetupZoneForExpansion has no native caller. Nothing here changes a field or an argument.</summary>
internal static partial class CompiledMapGenerator
{
    /// <summary>The factory batch that last became active (set by the batch-activation postfix on every level).</summary>
    private static LG_Factory.BatchName CurrentBatch;
    /// <summary>What the compiled ExpandZone prefix leaves behind: the exit the game handed it, the exit the plan uses, and the two
    /// native fields native code reads after it.</summary>
    private static void RecordExpandZoneExit(Run run, int index, LG_ZoneJob_CreateExpandFromData job, LG_Zone zone, LG_ZoneExpander handed, LG_Plug source)
        => MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation, "Compiled ExpandZone returned.",
            RuntimeLogValue.Of("kind", "expand-zone-exit"), RuntimeLogValue.Quote("zone", run.Program.Zones[index].Id),
            RuntimeLogValue.Of("handedExpander", handed == null ? 0L : handed.Pointer.ToInt64()), RuntimeLogValue.Of("handedMarked", handed != null && handed.m_isZoneSource),
            RuntimeLogValue.Of("planSource", source.Pointer.ToInt64()), RuntimeLogValue.Of("planSourceMarked", source.m_isZoneSource),
            RuntimeLogValue.Of("buildFromExpander", job.m_buildFromExpander == null ? 0L : job.m_buildFromExpander.Pointer.ToInt64()),
            RuntimeLogValue.Of("zoneSourceExpander", zone.m_sourceExpander == null ? 0L : zone.m_sourceExpander.Pointer.ToInt64()),
            RuntimeLogValue.Of("mainStatus", job.m_mainStatus.ToString()), RuntimeLogValue.Of("batch", CurrentBatch.ToString()));

    /// <summary>At <paramref name="phase"/>: each zone's m_sourceExpander and its mark, then every exit of the start room of a zone with no
    /// entry from another zone (HasPlanEntry) and of its partner: the exit, its mark, its gate and the gate's mark and type.</summary>
    private static void RecordZoneSources(Run run, string phase)
    {
        try
        {
            foreach (int index in run.LevelProgram.Zones)
            {
                var zone = run.Zones[index]; var source = zone?.m_sourceExpander;
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation, "A zone's source exit at a generation phase.",
                    RuntimeLogValue.Of("kind", "zone-source"), RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Quote("zone", run.Program.Zones[index].Id),
                    RuntimeLogValue.Of("hasEntry", HasPlanEntry(run, index)),
                    RuntimeLogValue.Of("sourceExpander", source == null ? 0L : source.Pointer.ToInt64()), RuntimeLogValue.Of("sourceMarked", source != null && source.m_isZoneSource),
                    RuntimeLogValue.Quote("sourceRoom", source == null ? "" : RoomOf(run, source.Pointer)));
                if (HasPlanEntry(run, index)) continue;
                int rootIndex = run.Program.Zones[index].RootRoom;
                var plugs = run.Rooms[rootIndex]?.Root.m_plugs;
                if (plugs == null) continue;
                for (int i = 0; i < plugs.Count; i++)
                {
                    var plug = plugs[i];
                    if (plug == null) continue;
                    var gate = plug.m_gate; var partner = plug.m_pariedWith;
                    MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationNavigation, "An exit of a zone start room with no entry at a generation phase.",
                        RuntimeLogValue.Of("kind", "start-room-exit"), RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Quote("room", run.Program.Rooms[rootIndex].Id),
                        RuntimeLogValue.Of("plug", plug.Pointer.ToInt64()), RuntimeLogValue.Of("plugMarked", plug.m_isZoneSource),
                        RuntimeLogValue.Of("partner", partner == null ? 0L : partner.Pointer.ToInt64()), RuntimeLogValue.Of("partnerMarked", partner != null && partner.m_isZoneSource),
                        RuntimeLogValue.Quote("partnerRoom", partner == null ? "" : RoomOf(run, partner.Pointer)),
                        RuntimeLogValue.Of("gate", gate == null ? 0L : gate.Pointer.ToInt64()), RuntimeLogValue.Of("gateMarked", gate != null && gate.m_isZoneSource),
                        RuntimeLogValue.Of("gateType", gate == null ? -1L : (long)gate.Type));
                }
            }
        }
        catch (Exception error) { MapLog.Contained("compiled-map-generator.zone-source-readback", error); }
    }

    /// <summary>The plan room whose geomorph holds the exit, or "" when none of this run's rooms does.</summary>
    private static string RoomOf(Run run, IntPtr expander)
    {
        for (int room = 0; room < run.Rooms.Length; room++)
        {
            var plugs = run.Rooms[room]?.Root.m_plugs;
            if (plugs == null) continue;
            for (int i = 0; i < plugs.Count; i++) if (plugs[i]?.Pointer == expander) return run.Program.Rooms[room].Id;
        }
        return "";
    }
}
