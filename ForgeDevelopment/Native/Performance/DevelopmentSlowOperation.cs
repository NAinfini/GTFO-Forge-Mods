using System;
using System.Diagnostics;
using System.Reflection;

namespace ForgeDevelopment.Native;

/// <summary>Only performance collection enables these main-thread observations. No file or game reads
/// happen on the fast path; a slow operation carries its real frame, managed allocation and CLR GC deltas.</summary>
internal readonly struct DevelopmentSlowOperation : IDisposable
{
    private readonly string? _operation;
    private readonly MethodBase? _target;
    private readonly long _started, _allocated;
    private readonly int _gc0, _gc1, _gc2;

    internal static DevelopmentSlowOperation Measure(string operation, MethodBase? target = null)
        => PerformanceDiagnostics.Enabled ? new(operation, target) : default;

    private DevelopmentSlowOperation(string operation, MethodBase? target)
    {
        _operation = operation; _target = target;
        _allocated = GC.GetAllocatedBytesForCurrentThread();
        _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        _started = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (_operation == null) return;
        var elapsed = Stopwatch.GetTimestamp() - _started;
        if (elapsed <= Stopwatch.Frequency / 50) return;
        var micros = (long)(elapsed * (1_000_000d / Stopwatch.Frequency));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
        var gc0 = GC.CollectionCount(0) - _gc0;
        var gc1 = GC.CollectionCount(1) - _gc1;
        var gc2 = GC.CollectionCount(2) - _gc2;
        var target = _target == null ? "" : _target.DeclaringType?.FullName + "." + _target.Name;
        RecDiag.Record(false, "development.periodic-slow", "development.periodic-slow operation=" + _operation
            + " elapsedMicros=" + micros + " frame=" + RecTime.Frame + " target=" + target,
            new("operation", _operation), new("elapsedMicros", micros), new("frame", RecTime.Frame),
            new("allocatedBytes", allocated), new("gc0", gc0), new("gc1", gc1), new("gc2", gc2), new("target", target));
    }
}
