using System;
using System.Threading;

namespace ForgeDevelopment.Native;

internal struct PerformanceFrame
{
    internal long Sequence, Timestamp, DroppedBefore, MainAllocatedBytes, ObserverTicks, ObserverCalls;
    internal double CollectorMilliseconds;
    internal NativeFrameReading Clock;
    internal PerformanceScene Scene;
    internal TimingValues Timings;
    internal TimingValues TimingCalls;
    internal PerformanceMark Marks;
    internal int SpawnGroups, Waves, NetGc0, NetGc1, NetGc2;
    internal long Il2CppUsed, Il2CppHeap;
}

// One Unity producer, one writer consumer. Full queues drop a complete frame,
// never wait on disk I/O or a lock. Drops and lost marks are reported explicitly.
internal sealed class PerformanceFrameQueue
{
    private readonly PerformanceFrame[] _items;
    private readonly int _mask;
    private int _write, _read;
    internal PerformanceFrameQueue(int capacity = 4096)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new PerformanceFrame[capacity]; _mask = capacity - 1;
    }
    internal bool TryWrite(in PerformanceFrame value)
    {
        var index = _write;
        if (unchecked((uint)(index - Volatile.Read(ref _read))) >= _items.Length) return false;
        _items[index & _mask] = value;
        Volatile.Write(ref _write, unchecked(index + 1)); return true;
    }
    internal bool TryRead(out PerformanceFrame value)
    {
        var index = _read;
        if (index == Volatile.Read(ref _write)) { value = default; return false; }
        value = _items[index & _mask];
        Volatile.Write(ref _read, unchecked(index + 1)); return true;
    }
}
