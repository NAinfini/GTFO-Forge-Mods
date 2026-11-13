using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeOrdnance.Frames;

/// <summary>One moment of the opening or closing, <see cref="At"/> seconds into its clip: a sound of the whole suit
/// (<see cref="Sound"/>, null for none) and/or the puff of steam from the back's middle seam (<see cref="Steam"/>).</summary>
internal readonly record struct PlateEvent(float At, FrameSound? Sound, bool Steam = false);

/// <summary>The key moments of the model's "open" and "close" clips, read off the parts' motion (the clips are the only source).
/// The suit has dozens of moving plates and makes only three sounds for them, each from the suit itself: in the opening the
/// first part to unlatch (<see cref="FrameSound.Unseal"/>) and the moment the main body opens (<see cref="FrameSound.PlateSlide"/>, when half of
/// the moving parts have started to move); in the closing the moment the last part latches shut (<see cref="FrameSound.HatchClose"/>). The steam puffs from the back's middle
/// seam when the first of the <see cref="OpeningPart.Seam"/> parts unlatches.</summary>
internal static class PlateTimeline
{
    /// <summary>A part this close to its first pose (metres, degrees) has not moved.</summary>
    public const float StillMetres = 0.0005f, StillDegrees = 0.2f;

    /// <summary>The opening's events from the parts' offsets from shut, <paramref name="metres"/>[sample][part] and
    /// <paramref name="degrees"/>[sample][part], sampled every <paramref name="step"/> seconds; <paramref name="seam"/> marks the
    /// parts beside the back's middle seam.</summary>
    public static PlateEvent[] Opening(float[][] metres, float[][] degrees, bool[] seam, float step)
    {
        var parts = metres.Length > 0 ? metres[0].Length : 0;
        var first = float.MaxValue;
        var vent = float.MaxValue;
        var unlocks = new List<float>();
        for (var p = 0; p < parts; p++)
        {
            var unlock = Unlock(metres, degrees, p);
            if (unlock < 0) continue;
            first = Math.Min(first, unlock * step);
            if (seam[p]) vent = Math.Min(vent, unlock * step);
            unlocks.Add(unlock * step);
        }
        unlocks.Sort();
        var events = new List<PlateEvent>();
        if (first < float.MaxValue) events.Add(new PlateEvent(first, FrameSound.Unseal));
        if (vent < float.MaxValue) events.Add(new PlateEvent(vent, null, true));
        if (unlocks.Count > 0) events.Add(new PlateEvent(unlocks[unlocks.Count / 2], FrameSound.PlateSlide));
        return events.OrderBy(e => e.At).ToArray();
    }

    /// <summary>The closing's events from the parts' offsets from shut (as <see cref="Opening"/>): the moment the last part that
    /// moved is shut again.</summary>
    public static PlateEvent[] Closing(float[][] metres, float[][] degrees, float step)
    {
        var parts = metres.Length > 0 ? metres[0].Length : 0;
        var shut = -1f;
        for (var p = 0; p < parts; p++)
        {
            var start = -1;
            for (var k = 1; k < metres.Length && start < 0; k++)
                if (!Same(metres, degrees, p, k, 0)) start = k - 1;
            if (start < 0) continue;
            for (var k = start + 1; k < metres.Length; k++)
                if (metres[k][p] <= StillMetres && degrees[k][p] <= StillDegrees)
                {
                    shut = Math.Max(shut, k * step);
                    break;
                }
        }
        return shut < 0f ? Array.Empty<PlateEvent>() : new[] { new PlateEvent(shut, FrameSound.HatchClose) };
    }

    /// <summary>How many of <paramref name="events"/> (in time order) are at or before <paramref name="seconds"/>.</summary>
    public static int Passed(PlateEvent[] events, float seconds)
    {
        var count = 0;
        while (count < events.Length && events[count].At <= seconds) count++;
        return count;
    }

    /// <summary>The sample just before part <paramref name="p"/> first moves, or -1 when it never does.</summary>
    private static int Unlock(float[][] metres, float[][] degrees, int p)
    {
        for (var k = 0; k < metres.Length; k++)
            if (metres[k][p] > StillMetres || degrees[k][p] > StillDegrees) return Math.Max(0, k - 1);
        return -1;
    }

    private static bool Same(float[][] metres, float[][] degrees, int p, int a, int b)
        => Math.Abs(metres[a][p] - metres[b][p]) <= StillMetres && Math.Abs(degrees[a][p] - degrees[b][p]) <= StillDegrees;
}
