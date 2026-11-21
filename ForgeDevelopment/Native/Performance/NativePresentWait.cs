using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgeDevelopment.Native;

internal static class NativePresentWait
{
    internal const string PlayerSha256 = "D5A3739835FFFCE0A1ED797E6CC52A5DF4EFAC0F44169792BEE60C1F69245EB2";
    internal const int FunctionRva = 0x4F1B70;
    internal const int BeginFunctionRva = 0x4EA210;
    internal const string Source = "UnityPlayer_native_graphics_frame_dispatches_rva_0x4EA210_and_0x4F1B70;enclose_Gfx_wait_0x500850_and_existing_hooks;main_thread_wall_minus_calibrated_CPU;nested_scopes_counted_once";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void WaitDelegate(IntPtr device);
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeBoolean();
    private static IDisposable? _beginHook, _endHook;
    private static bool _enabled, _observed, _hasCpu;
    private static long _blockedNanoseconds, _calls, _started;
    private static ulong _cpu;
    private static int _depth;
    private static string _reason = "wait_hook_not_initialized";
    internal static bool ProfilerSupported() => IL2CPP.ResolveICall<NativeBoolean>("UnityEngine.Profiling.Profiler::get_supported")();
    internal static string Start(PerformanceTimings? timings = null)
    {
        if (_endHook != null) return _reason;
        try
        {
            if (Application.unityVersion != "2019.4.21f1") return _reason = "unsupported_player_version_or_pointer_size";
            var module = NativeModuleFingerprint.Verified("UnityPlayer.dll", PlayerSha256, out var reason);
            if (module == IntPtr.Zero) return _reason = reason;
            ThreadCpuClock.Initialize();
            if (ThreadCpuClock.Reason != "none") return _reason = ThreadCpuClock.Reason;
            timings ??= new PerformanceTimings();
            _observed = false;
            _endHook = NativeTimingRegistration.Observe("UnityPlayer.dll", PlayerSha256, IntPtr.Add(module, FunctionRva), timings, null, 3);
            _beginHook = NativeTimingRegistration.Observe("UnityPlayer.dll", PlayerSha256, IntPtr.Add(module, BeginFunctionRva), timings, null, 3);
            _enabled = true;
            return _reason = "none";
        }
        catch (Exception error)
        {
            var failure = "present_wait_unavailable:" + error.GetBaseException().Message;
            try { Stop(); } catch (Exception cleanup) { failure += ";cleanup_pending:" + cleanup.GetBaseException().Message; }
            return _reason = failure;
        }
    }
    internal static void Begin()
    {
        if (_depth++ != 0) return;
        _started = Stopwatch.GetTimestamp();
        _hasCpu = ThreadCpuClock.TryRead(out _cpu);
    }
    internal static void End()
    {
        if (--_depth != 0) return;
        var elapsed = Stopwatch.GetTimestamp() - _started;
        if (_hasCpu && ThreadCpuClock.TryRead(out var after) && after >= _cpu) Record(elapsed, after - _cpu);
        else _reason = "present_wait_thread_cpu_unavailable";
    }
    // Remove CPU spent in this wrapper from the blocking span to avoid counting
    // it twice alongside the full-frame calibrated thread-cycle execution.
    internal static void Record(long wallTicks, ulong cpuTicks)
    {
        var elapsed = (long)(wallTicks * (1_000_000_000.0 / Stopwatch.Frequency));
        _blockedNanoseconds += Math.Max(0, elapsed - (long)cpuTicks * 100);
        _calls++;
        _observed = true;
    }

    internal static (NativeTimingSample Sample, long Calls) Take()
    {
        var sample = _enabled && _reason == "none" && _observed
            ? new NativeTimingSample(true, _blockedNanoseconds, (int)Math.Min(int.MaxValue, _calls), "none")
            : NativeTimingSample.Unavailable(_reason == "none" ? "graphics_frame_end_not_observed_yet" : _reason);
        var calls = _calls; _blockedNanoseconds = _calls = 0;
        return (sample, calls);
    }

    internal static void Stop()
    {
        _enabled = false;
        var failures = new System.Collections.Generic.List<Exception>();
        try { _beginHook?.Dispose(); _beginHook = null; } catch (Exception error) { failures.Add(error); }
        try { _endHook?.Dispose(); _endHook = null; } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("present_wait_cleanup_pending", failures);
        _reason = "wait_hook_stopped"; _blockedNanoseconds = _calls = 0;
    }
}
