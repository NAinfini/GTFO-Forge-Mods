using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace ForgeDevelopment.Native;

internal readonly record struct NativeFrameReading(double FrameMilliseconds, NativeTimingSample Cpu, NativeTimingSample PresentWait, long WaitCalls);

// The calibrated cycle counter excludes blocked/unscheduled time. Wall and CPU
// readings cover successive Update intervals. This is an estimate in time units,
// not GetThreadTimes' scheduler-tick accounting or a nominal CPU MHz conversion.
internal sealed class NativeFrameClock
{
    private long _wall;
    private ulong _cpu;
    private uint _thread;
    private bool _ready;

    internal void Reset()
    {
        _thread = ThreadCpuClock.CurrentThreadId();
        _ready = ThreadCpuClock.TryRead(out _cpu);
        _wall = Stopwatch.GetTimestamp();
        NativePresentWait.Take();
    }

    internal NativeFrameReading Read()
    {
        var wall = Stopwatch.GetTimestamp();
        var wait = NativePresentWait.Take();
        var milliseconds = (wall - _wall) * 1000.0 / Stopwatch.Frequency;
        if (ThreadCpuClock.CurrentThreadId() != _thread || !ThreadCpuClock.TryRead(out var cpu))
        {
            _ready = false; _wall = wall;
            return new(milliseconds, NativeTimingSample.Unavailable(ThreadCpuClock.Reason == "none" ? "thread_cycle_read_failed_or_wrong_thread" : ThreadCpuClock.Reason), wait.Sample, wait.Calls);
        }
        if (!_ready || cpu < _cpu)
        {
            _ready = true; _cpu = cpu; _wall = wall;
            return new(milliseconds, NativeTimingSample.Unavailable("thread_cpu_counter_warmup_or_reset"), wait.Sample, wait.Calls);
        }
        var nanoseconds = (long)(cpu - _cpu) * 100;
        _cpu = cpu; _wall = wall;
        return new(milliseconds, new(true, nanoseconds, 1, "none"), wait.Sample, wait.Calls);
    }
}

internal static class ThreadCpuClock
{
    private static bool _initialized;
    internal static double CyclesPerSecond { get; private set; }
    internal static double ResolutionNanoseconds { get; private set; } = double.NaN;
    internal static double CalibrationSpread { get; private set; } = double.NaN;
    internal static string Reason { get; private set; } = "thread_cycle_clock_not_calibrated";
    internal const string Source = "Windows_QueryThreadCycleTime;invariant_TSC_rate_measured_against_QPC;estimated_execution_time";

    [DllImport("kernel32.dll", EntryPoint = "QueryThreadCycleTime", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    internal static extern uint CurrentThreadId();

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        // Microsoft explicitly warns that a cycle clock need not have a stable
        // frequency. Accept only a CPU advertising an invariant TSC, then
        // measure its rate, rather than assuming QPC or advertised core MHz.
        if (!X86Base.IsSupported || (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax < 0x80000007
            || (X86Base.CpuId(unchecked((int)0x80000007), 0).Edx & (1 << 8)) == 0)
        { Reason = "thread_cycle_time_requires_invariant_TSC"; return; }
        // Calibrate the invariant hardware timer, NOT a busy thread's cycles
        // divided by wall time: preemption biases that ratio even when several
        // samples agree. The same reference timer keeps advancing while this
        // thread is off CPU. Executable memory exists only during calibration.
        var code = new byte[] { 0x0F, 0xAE, 0xE8, 0x0F, 0x31, 0x48, 0xC1, 0xE2, 0x20, 0x48, 0x09, 0xD0, 0x0F, 0xAE, 0xE8, 0xC3 }; // LFENCE; RDTSC; combine EDX:EAX; LFENCE; RET.
        var memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)code.Length, 0x3000, 0x04);
        if (memory == IntPtr.Zero) { Reason = "cycle_rate_reader_allocation_failed"; return; }
        try
        {
            Marshal.Copy(code, 0, memory, code.Length);
            if (!VirtualProtect(memory, (UIntPtr)code.Length, 0x20, out _) || !FlushInstructionCache(new IntPtr(-1), memory, (UIntPtr)code.Length))
            { Reason = "cycle_rate_reader_protection_failed"; return; }
            var read = Marshal.GetDelegateForFunctionPointer<ReadCycles>(memory);
            var rates = new double[8];
            var duration = Math.Max(1, Stopwatch.Frequency / 500); // 2 ms, startup only.
            for (var i = -2; i < rates.Length; i++)
            {
                var start = Stopwatch.GetTimestamp(); var before = read();
                while (Stopwatch.GetTimestamp() - start < duration) { }
                var after = read(); var end = Stopwatch.GetTimestamp();
                if (after <= before) { Reason = "invariant_TSC_regressed_or_migrated_unsynchronized"; return; }
                if (i >= 0) rates[i] = (after - before) * (double)Stopwatch.Frequency / (end - start);
            }
            Array.Sort(rates);
            CyclesPerSecond = (rates[3] + rates[4]) / 2;
            CalibrationSpread = (rates[^1] - rates[0]) / CyclesPerSecond;
            if (!double.IsFinite(CyclesPerSecond) || CyclesPerSecond <= 0 || CalibrationSpread > 0.005)
            { Reason = "invariant_TSC_QPC_rate_calibration_unstable"; return; }
            GC.KeepAlive(read);
        }
        finally { if (!VirtualFree(memory, UIntPtr.Zero, 0x8000)) Reason = "cycle_rate_reader_release_failed"; }
        if (Reason == "cycle_rate_reader_release_failed") return;
        ulong step = ulong.MaxValue;
        if (!QueryThreadCycleTime(new IntPtr(-2), out var previous)) { Reason = "QueryThreadCycleTime_failed"; return; }
        for (var i = 0; i < 128; i++)
        {
            if (!QueryThreadCycleTime(new IntPtr(-2), out var current) || current < previous)
            { Reason = "thread_cycle_resolution_read_failed"; return; }
            if (current > previous) step = Math.Min(step, current - previous);
            previous = current;
        }
        ResolutionNanoseconds = step * (1_000_000_000.0 / CyclesPerSecond);
        Reason = ResolutionNanoseconds < 1_000_000 ? "none" : "thread_cycle_resolution_not_submillisecond";
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong ReadCycles();
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr at, UIntPtr size, uint allocation, uint protection);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualProtect(IntPtr at, UIntPtr size, uint protection, out uint previous);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr at, UIntPtr size);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(IntPtr at, UIntPtr size, uint free);

    internal static bool TryRead(out ulong ticks)
    {
        Initialize();
        ticks = 0;
        if (Reason != "none" || !QueryThreadCycleTime(new IntPtr(-2), out var cycles)) return false;
        ticks = (ulong)(cycles * (10_000_000.0 / CyclesPerSecond)); // 100 ns time units.
        return true;
    }
}
