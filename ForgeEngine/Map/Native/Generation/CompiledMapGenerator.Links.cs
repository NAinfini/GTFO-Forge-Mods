using System;
using ForgeMap.Generation;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>Loops from the plan's `links` table: two free room plugs that meet on the grid. The plan pairs them, as it pairs a
/// planned connection; after LG_Floor.PrepZone they are made a real passage, and the native plug job builds the author's choice
/// (no door or a weak door, an optional pinned connector) like any other connection. A loop never changes which zone a room
/// belongs to: the gate only joins the two areas the pairing already named.</summary>
internal static partial class CompiledMapGenerator
{
    private static bool LinkPlaced(Run run, int index)
    {
        var link = run.RoomLinks[index];
        return run.Rooms[link.FromRoom] != null && run.Rooms[link.ToRoom] != null;
    }

    /// <summary>Pairs a loop's two free ends as soon as both rooms stand, where a planned connection pairs its ends: the course graph
    /// reads the pair at PrepareZones, so a pair made at batch Plugs is too late. Ends another connection owns stay untouched; LinkPlugs refuses them.</summary>
    private static void PairLinks(Run run)
    {
        for (int index = 0; index < run.RoomLinks.Length; index++)
        {
            if (!LinkPlaced(run, index)) continue;
            var link = run.RoomLinks[index];
            var a = Plug(run, link.FromRoom, link.FromExit); var b = Plug(run, link.ToRoom, link.ToExit);
            if (a.m_pariedWith != null || b.m_pariedWith != null || FindPlugBinding(run, a.Pointer.ToInt64()).HasValue || FindPlugBinding(run, b.Pointer.ToInt64()).HasValue) continue;
            PairPlanned(a, b);
            run.Record("pairs", link.Id, new { from = PlugPose(a), to = PlugPose(b) });
        }
    }

    /// <summary>LG_Floor.PrepZone (0x15D0D40) blocks a plug pair between two zones unless one end is the zone source, and a blocked
    /// plug gets no course portal from AIG_CourseGraph.CreateGraph (0x1A05670). A loop is authored as a real passage whichever zones it
    /// joins, so both ends are set Connected right after PrepZone decided them.</summary>
    internal static void ZonePrepared()
    {
        if (!Live(out var run) || run.Failed) return;
        try
        {
            for (int index = 0; index < run.RoomLinks.Length; index++)
            {
                if (!LinkPlaced(run, index)) continue;
                var link = run.RoomLinks[index];
                var a = Plug(run, link.FromRoom, link.FromExit); var b = Plug(run, link.ToRoom, link.ToExit);
                if (a.m_pariedWith?.Pointer != b.Pointer) continue;
                if (a.ExpanderStatus == LG_ZoneExpanderStatus.Connected && b.ExpanderStatus == LG_ZoneExpanderStatus.Connected) continue;
                run.Record("link-status", link.Id, new { a = a.ExpanderStatus.ToString(), b = b.ExpanderStatus.ToString() });
                a.ExpanderStatus = LG_ZoneExpanderStatus.Connected; b.ExpanderStatus = LG_ZoneExpanderStatus.Connected;
            }
        }
        catch (Exception error) { Refuse(run, error); }
    }

    /// <summary>Both ends' native plug pointers, two slots per link (zero for a link that was not placed). A loop end that a planned
    /// connection or a finished exit already owns, or one paired with another plug, is refused: building it would silently
    /// replace another decision.</summary>
    private static long[] LinkPlugs(Run run)
    {
        var ends = new long[run.RoomLinks.Length * 2];
        for (int index = 0; index < run.RoomLinks.Length; index++)
        {
            if (!LinkPlaced(run, index)) continue;
            var link = run.RoomLinks[index];
            var a = Plug(run, link.FromRoom, link.FromExit); var b = Plug(run, link.ToRoom, link.ToExit);
            if (a.m_pariedWith?.Pointer != b.Pointer || b.m_pariedWith?.Pointer != a.Pointer) throw new GenerationRefusal("gen-exit-mismatch", link.Id + "/unpaired");
            long first = a.Pointer.ToInt64(), second = b.Pointer.ToInt64();
            if (FindPlugBinding(run, first).HasValue || FindPlugBinding(run, second).HasValue || Array.IndexOf(ends, first) >= 0 || Array.IndexOf(ends, second) >= 0)
                throw new GenerationRefusal("gen-exit-occupied", link.Id);
            ends[index * 2] = first; ends[index * 2 + 1] = second;
        }
        return ends;
    }
}
