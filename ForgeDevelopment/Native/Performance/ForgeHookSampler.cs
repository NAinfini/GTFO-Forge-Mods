using System;
using System.Diagnostics;
using System.Threading;

namespace ForgeDevelopment.Native;

internal readonly record struct ForgeHookReading(long Calls, long Ticks, long MaximumTicks, long Bytes, long MaximumBytes);
internal sealed class ForgeHookWindow
{
    internal readonly ForgeHookReading[] Inclusive, Exclusive;
    internal long Timestamp, Frame, Dropped, Gc0, Gc1, Gc2, GcMissedPauses;
    internal double GcPauseMilliseconds;
    internal int State;
    internal double Seconds;
    internal ForgeHookWindow(int count) { Inclusive = new ForgeHookReading[count]; Exclusive = new ForgeHookReading[count]; }
}

// Reads ForgeCore's hook counters (every Forge package's measured sections) with fixed enum indices, without boxing per sample.
internal sealed class ForgeHookSampler : IDisposable
{
    internal readonly string[] Names, Packages;
    private readonly ForgeHookWindow[] _windows = new ForgeHookWindow[16];
    private readonly long _interval;
    private int _write, _take;
    private long _start, _next, _dropped;
    private int _state = -1;
    private int _disposed;
    private int _gc0, _gc1, _gc2;
    private long _gcIndex;
    internal long DroppedWindows => Volatile.Read(ref _dropped);

    internal static ForgeHookSampler Start(float seconds) => new(seconds);
    private ForgeHookSampler(float seconds)
    {
        Names = Enum.GetNames(typeof(ForgeCore.HookSection)); Packages = new string[Names.Length];
        for (var i = 0; i < Names.Length; i++) Packages[i] = Package(Names[i]);
        for (var i = 0; i < _windows.Length; i++) _windows[i] = new ForgeHookWindow(Names.Length);
        _interval = (long)(seconds * Stopwatch.Frequency);
        _start = Stopwatch.GetTimestamp(); _next = _start + _interval;
        ResetGc(); ForgeCore.HookCounters.Reset(); ForgeCore.HookCounters.Enabled = true;
    }
    // Managed collections of this process in the window. The runtime reports the pause of the most recent collection only, so a
    // window with several collections counts the others in GcMissedPauses and its pause time is a lower bound.
    private void ResetGc()
    {
        _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        _gcIndex = GC.GetGCMemoryInfo(GCKind.Any).Index;
    }
    private void ReadGc(ForgeHookWindow window)
    {
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        window.Gc0 = g0 - _gc0; window.Gc1 = g1 - _gc1; window.Gc2 = g2 - _gc2;
        var info = GC.GetGCMemoryInfo(GCKind.Any); var advanced = info.Index - _gcIndex;
        double pause = 0;
        if (advanced > 0) foreach (var span in info.PauseDurations) pause += span.TotalMilliseconds;
        window.GcPauseMilliseconds = pause; window.GcMissedPauses = Math.Max(0, advanced - 1);
        _gc0 = g0; _gc1 = g1; _gc2 = g2; _gcIndex = info.Index;
    }
    private static ForgeHookReading Reading(ForgeCore.HookSample sample)
        => new(sample.Calls, sample.Ticks, sample.MaxTicks, sample.Bytes, sample.MaxBytes);
    private static string Package(string name)
        => name.StartsWith("Tick", StringComparison.Ordinal) || name.StartsWith("Runtime", StringComparison.Ordinal) || name.StartsWith("Trigger", StringComparison.Ordinal) ? "ForgeEngine"
            : name.StartsWith("Enemy", StringComparison.Ordinal) ? "ForgeEnemy"
            : name.StartsWith("Player", StringComparison.Ordinal) ? "ForgePlayer"
            : name.StartsWith("Weapon", StringComparison.Ordinal) || name.StartsWith("Sentry", StringComparison.Ordinal) ? "ForgeWeapon"
            : name.StartsWith("Map", StringComparison.Ordinal) ? "ForgeMap" : "ForgeDevelopment";

    internal bool Capture(long timestamp, int state, long frame, bool force = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        if (_state < 0) _state = state;
        if (!force && timestamp < _next && state == _state) return false;
        var index = _write;
        if (unchecked((uint)(index - Volatile.Read(ref _take))) >= _windows.Length)
        { _dropped++; ResetGc(); ForgeCore.HookCounters.Reset(); _start = timestamp; _next = timestamp + _interval; _state = state; return false; }
        var window = _windows[index & (_windows.Length - 1)];
        window.Timestamp = timestamp; window.State = _state; window.Frame = frame;
        window.Seconds = (timestamp - _start) / (double)Stopwatch.Frequency; window.Dropped = _dropped;
        for (var i = 0; i < Names.Length; i++) { window.Inclusive[i] = Reading(ForgeCore.HookCounters.Read((ForgeCore.HookSection)i)); window.Exclusive[i] = Reading(ForgeCore.HookCounters.ReadExclusive((ForgeCore.HookSection)i)); }
        ReadGc(window); ForgeCore.HookCounters.Reset(); _start = timestamp; _next = timestamp + _interval; _state = state;
        Volatile.Write(ref _write, unchecked(index + 1)); return true;
    }
    internal void Drain(Action<ForgeHookWindow> write)
    {
        var index = _take;
        while (index != Volatile.Read(ref _write))
        {
            write(_windows[index & (_windows.Length - 1)]);
            Volatile.Write(ref _take, index = unchecked(index + 1));
        }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) ForgeCore.HookCounters.Enabled = false; }
}
