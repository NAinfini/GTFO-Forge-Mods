using System;
using System.Threading;

namespace ForgeDevelopment.Native;

// One main-thread producer. The writer takes bounded, coherent snapshots without
// blocking sampling; dropped queue entries still contribute to these totals.
internal sealed class CollectorMetrics
{
    private int _version;
    private long _frames, _ticks, _minimumTicks = long.MaxValue, _maximumTicks;
    private long _allocated, _minimumAllocated = long.MaxValue, _maximumAllocated, _missingAllocated, _dropped;
    internal void Record(long ticks, long allocated, bool dropped)
    {
        Interlocked.Increment(ref _version);
        _frames++; _ticks += ticks; _minimumTicks = Math.Min(_minimumTicks, ticks); _maximumTicks = Math.Max(_maximumTicks, ticks);
        if (allocated < 0) _missingAllocated++;
        else { _allocated += allocated; _minimumAllocated = Math.Min(_minimumAllocated, allocated); _maximumAllocated = Math.Max(_maximumAllocated, allocated); }
        if (dropped) _dropped++;
        Interlocked.Increment(ref _version);
    }
    internal bool TryRead(out CollectorSnapshot snapshot)
    {
        for (var i = 0; i < 3; i++)
        {
            var version = Volatile.Read(ref _version);
            if ((version & 1) != 0) continue;
            snapshot = new(_frames, _ticks, _minimumTicks, _maximumTicks, _allocated, _minimumAllocated, _maximumAllocated, _missingAllocated, _dropped);
            Thread.MemoryBarrier();
            if (version == Volatile.Read(ref _version)) return true;
        }
        snapshot = default; return false;
    }
}

internal readonly record struct CollectorSnapshot(long Frames, long Ticks, long MinimumTicks, long MaximumTicks,
    long Allocated, long MinimumAllocated, long MaximumAllocated, long MissingAllocated, long Dropped);
