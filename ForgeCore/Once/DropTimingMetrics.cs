using System;

namespace ForgeCore;

// Monotonic, whole-frame intervals during generation, independent of timeScale/vsync.
// No Unity reads, strings, collections or allocation on the frame/job path.
public sealed class DropTimingMetrics
{
    private long _last, _total, _maximum;
    public bool Active { get; private set; }
    public int Frames { get; private set; }
    public int Jobs { get; private set; }
    public double MeanFrameMs => Frames == 0 ? double.NaN : _total * 1000.0 / Frequency / Frames;
    public double MaxFrameMs => Frames == 0 ? double.NaN : _maximum * 1000.0 / Frequency;
    private readonly long Frequency;

    public DropTimingMetrics(long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        Frequency = frequency;
    }

    public void Start(long now)
    {
        Active = true; _last = now; _total = _maximum = 0; Frames = Jobs = 0;
    }

    public void Reset() { Active = false; _last = _total = _maximum = 0; Frames = Jobs = 0; }

    public void JobStarted() { if (Active) Jobs++; }

    public void Frame(long now)
    {
        if (!Active) return;
        var ticks = now - _last;
        if (ticks <= 0) return;
        _last = now; _total += ticks; _maximum = Math.Max(_maximum, ticks); Frames++;
    }

    // The final partial interval is included and identified in the stage record;
    // it must not disappear just because completion happened before this frame's Update.
    public void Finish(long now) { Frame(now); Active = false; }
}
