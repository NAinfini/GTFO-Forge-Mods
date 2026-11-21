using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ForgeDevelopment.Native;

// Shared sampling only. Each package supplies its own physical registration path.
internal static class NativeTimingRegistration
{
    internal static Func<string, string, IntPtr, NativeTimingSampleObserver, int, IDisposable>? Register { get; set; }
    internal static IDisposable Observe(string module, string hash, IntPtr target, PerformanceTimings timings,
        TimingCounter? counter, int abi)
        => (Register ?? throw new InvalidOperationException("native_timing_registration_unavailable"))
            (module, hash, target, new NativeTimingSampleObserver(timings, counter, abi == 3), abi);
}

internal sealed class NativeTimingSampleObserver
{
    private readonly PerformanceTimings _timings;
    private readonly TimingCounter? _counter;
    private readonly bool _graphics;
    internal NativeTimingSampleObserver(PerformanceTimings timings, TimingCounter? counter, bool graphics)
    { _timings = timings; _counter = counter; _graphics = graphics; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long Enter(IntPtr arg0)
    {
        var start = Stopwatch.GetTimestamp();
        if (ThreadCpuClock.CurrentThreadId() != _timings.MainThread) return 0;
        if (_graphics) NativePresentWait.Begin();
        return start;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Exit(long token, long originalStart, long originalEnd)
    {
        if (token == 0) return;
        if (_graphics) NativePresentWait.End();
        _counter?.Add(originalEnd - originalStart);
        _timings.ObserverCost((originalStart - token) + (Stopwatch.GetTimestamp() - originalEnd));
    }
}
