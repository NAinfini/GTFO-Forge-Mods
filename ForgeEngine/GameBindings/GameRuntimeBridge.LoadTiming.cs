using System.Diagnostics;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

// Observed by the existing host Update and level lifecycle. No second clock or scheduler.
internal static partial class GameRuntimeBridge
{
    private static readonly ForgeCore.DropTimingMetrics FirstFrames = new(Stopwatch.Frequency);
    private static RuntimeKernel? _firstFrameKernel;
    private static long _firstFrameWorld, _firstFrameStarted;

    internal static void BeginFirstFrameTiming(RuntimeKernel kernel, long now)
    {
        ResetFirstFrameTiming();
        if (!kernel.RuntimeLogs(RuntimeLogLevel.Info)) return;
        _firstFrameKernel = kernel;
        _firstFrameWorld = kernel.WorldEpoch;
        _firstFrameStarted = now;
        FirstFrames.Start(now);
    }

    internal static void ResetFirstFrameTiming()
    {
        FirstFrames.Reset();
        _firstFrameKernel = null;
    }

    internal static void SampleFirstFrameTiming(RuntimeKernel? kernel, long now)
    {
        if (!FirstFrames.Active) return;
        if (kernel == null || !ReferenceEquals(kernel, _firstFrameKernel) || kernel.WorldEpoch != _firstFrameWorld)
        { ResetFirstFrameTiming(); return; }
        FirstFrames.Frame(now);
        if (FirstFrames.Frames < 120) return;
        FirstFrames.Finish(now);
        _firstFrameKernel = null;
        kernel.LogRuntime(RuntimeLogLevel.Info, "level.load-work", new RuntimeLogFacts
        {
            Detail = "First 120 host Update intervals after level entry; includes the initial partial interval.",
            Values = new[]
            {
                RuntimeLogValue.Of("work", "first-frames"), RuntimeLogValue.Of("phase", "in-level"),
                RuntimeLogValue.Of("frames", FirstFrames.Frames),
                RuntimeLogValue.Of("elapsedMs", (now - _firstFrameStarted) * 1000.0 / Stopwatch.Frequency),
                RuntimeLogValue.Of("frameMeanMs", FirstFrames.MeanFrameMs),
                RuntimeLogValue.Of("frameMaxMs", FirstFrames.MaxFrameMs)
            }
        });
    }
}
