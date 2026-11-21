using System;
using System.Globalization;

namespace ForgeDevelopment.Native;

internal readonly record struct NativeTimingSample(bool Available, long Nanoseconds, int Blocks, string Reason)
{
    internal static NativeTimingSample Unavailable(string reason) => new(false, 0, 0, reason);
}

internal readonly record struct FramePartitionSummary(
    bool Available, int UnavailableFrames, int ClampedFrames, string Reason,
    FrameSummary CpuMain, FrameSummary MainWork, FrameSummary PresentWait, FrameSummary Other,
    string CpuMainObservations, string MainWorkObservations, string PresentWaitObservations, string OtherObservations)
{
    private string Duration(double value) => Available ? value.ToString("0.###", CultureInfo.InvariantCulture) : "unavailable";

    // The actual production TSV fields, also exercised by the offline report
    // pipeline. Formatting happens at summary boundaries, not while sampling.
    internal (string Key, string Value)[] Fields(string state, int frame) => new[]
    {
        ("measurement_schema", PerformanceFrameProbe.Schema),
        ("state", state), ("frame", frame.ToString(CultureInfo.InvariantCulture)),
        ("status", Available ? "samples" : "unavailable"), ("reason", Reason),
        ("samples", MainWork.Samples.ToString(CultureInfo.InvariantCulture)),
        ("unavailable_frames", UnavailableFrames.ToString(CultureInfo.InvariantCulture)),
        ("clamped_frames", ClampedFrames.ToString(CultureInfo.InvariantCulture)),
        ("dropped_samples", MainWork.DroppedSamples.ToString(CultureInfo.InvariantCulture)),
        ("main_thread_source", ThreadCpuClock.Source),
        ("present_wait_source", NativePresentWait.Source),
        ("other_source", "matching_Update_wall_minus_main_CPU_minus_measured_present_blocking"),
        ("gpu_execution_ms", "unavailable"), ("gpu_execution_reason", "FrameTiming_native_type_stripped"),
        ("cpu_main_thread_status", CpuMain.Samples > 0 ? "samples" : "unavailable"),
        ("cpu_main_thread_samples", CpuMain.Samples.ToString(CultureInfo.InvariantCulture)),
        ("cpu_main_thread_dropped_samples", CpuMain.DroppedSamples.ToString(CultureInfo.InvariantCulture)),
        ("cpu_main_thread_average_ms", CpuMain.Samples > 0 ? CpuMain.AverageMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) : "unavailable"),
        ("cpu_main_thread_observations_ms", CpuMainObservations),
        ("cpu_main_work_average_ms", Duration(MainWork.AverageMilliseconds)),
        ("cpu_main_work_p50_ms", Duration(MainWork.P50Milliseconds)),
        ("cpu_main_work_p95_ms", Duration(MainWork.P95Milliseconds)),
        ("cpu_main_work_p99_ms", Duration(MainWork.P99Milliseconds)),
        ("cpu_main_work_observations_ms", MainWorkObservations),
        ("gpu_present_wait_average_ms", Duration(PresentWait.AverageMilliseconds)),
        ("gpu_present_wait_p50_ms", Duration(PresentWait.P50Milliseconds)),
        ("gpu_present_wait_p95_ms", Duration(PresentWait.P95Milliseconds)),
        ("gpu_present_wait_p99_ms", Duration(PresentWait.P99Milliseconds)),
        ("gpu_present_wait_observations_ms", PresentWaitObservations),
        ("other_average_ms", Duration(Other.AverageMilliseconds)),
        ("other_p50_ms", Duration(Other.P50Milliseconds)),
        ("other_p95_ms", Duration(Other.P95Milliseconds)),
        ("other_p99_ms", Duration(Other.P99Milliseconds)),
        ("other_observations_ms", OtherObservations)
    };
}

// CPU is OS-accounted execution. Present wait is the inspected native wait
// function's wall time minus its CPU execution, avoiding double counting.
// Remaining wall time can include scheduling, other waits or unmeasured work.
internal sealed class PerformanceFrameProbe
{
    internal const string Schema = "perfprobe-1";
    private readonly PerformanceSampleWindow _cpuMain = new(8192), _mainWork = new(8192),
        _presentWait = new(8192), _other = new(8192);
    private int _unavailable, _clamped;
    private string _reason = "no_completed_frame";

    internal void Add(double frameMilliseconds, NativeTimingSample main, NativeTimingSample wait, double threshold)
    {
        if (!main.Available) { Missing(main.Reason); return; }
        if (main.Blocks <= 0 || main.Nanoseconds < 0) { Missing("thread_cpu:invalid_counter"); return; }
        var mainMilliseconds = main.Nanoseconds / 1_000_000.0;
        _cpuMain.Add(mainMilliseconds, threshold);
        if (!wait.Available) { Missing(wait.Reason); return; }
        if (wait.Nanoseconds < 0 || wait.Blocks < 0 || (wait.Blocks == 0 && wait.Nanoseconds != 0))
        { Missing("present_wait:invalid_sample"); return; }
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds <= 0)
        { Missing("frame:invalid_duration"); return; }
        var waitMilliseconds = wait.Nanoseconds / 1_000_000.0;
        var covered = mainMilliseconds + waitMilliseconds;
        // OS CPU accounting resolution and native boundaries can disagree.
        // Preserve raw CPU, reject large disagreement, count small clamps.
        if (covered > frameMilliseconds + Math.Max(0.25, frameMilliseconds * 0.05))
        { Missing("cpu_plus_present_wait:exceeds_wall_interval_or_counter_resolution"); return; }
        if (covered > frameMilliseconds)
        {
            if (waitMilliseconds > frameMilliseconds) { Missing("present_wait:exceeds_frame_duration"); return; }
            mainMilliseconds = frameMilliseconds - waitMilliseconds;
            _clamped++;
        }
        _mainWork.Add(mainMilliseconds, threshold);
        _presentWait.Add(waitMilliseconds, threshold);
        _other.Add(Math.Max(0, frameMilliseconds - mainMilliseconds - waitMilliseconds), threshold);
    }

    private void Missing(string reason) { _unavailable++; _reason = reason; }

    internal FramePartitionSummary Take()
    {
        _cpuMain.TryTake(out var cpuMain, out var cpuObservations);
        var available = _mainWork.TryTake(out var mainWork, out var workObservations);
        _presentWait.TryTake(out var presentWait, out var waitObservations);
        _other.TryTake(out var other, out var otherObservations);
        var summary = new FramePartitionSummary(available, _unavailable, _clamped,
            available && _unavailable == 0 ? "none" : _reason,
            cpuMain, mainWork, presentWait, other, cpuObservations, workObservations, waitObservations, otherObservations);
        _unavailable = _clamped = 0;
        _reason = "no_completed_frame";
        return summary;
    }
}
