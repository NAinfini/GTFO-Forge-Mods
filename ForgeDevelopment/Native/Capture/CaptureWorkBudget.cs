using System;
using System.Diagnostics;

namespace ForgeDevelopment.Native;

/// <summary>What one frame may spend on the capture pump. A snapshot slice gets <see cref="SnapshotMillisecondsPerFrame"/>;
/// a native call cannot be split, so a slice that overruns is paid back by the snapshot pass's rest frames and the
/// average stays within that budget. The change pass gets its own <see cref="ChangeMillisecondsPerFrame"/> and runs only
/// on a frame whose snapshot slice left that much of the snapshot budget, so a frame never carries both in full. A
/// complete change sweep starts at most once per <see cref="ChangeSweepIntervalMilliseconds"/>; the work a budget leaves
/// over is kept for the next frame, never dropped. The capture-started record reports these numbers, so the number in
/// the record and the number enforced are the same one.</summary>
internal static class CaptureBudgets
{
    internal const int ChangeRowsPerFrame = 4096;
    internal const double ChangeMillisecondsPerFrame = 0.5;
    internal const int SnapshotMillisecondsPerFrame = 2;
    internal const int ChangeSweepIntervalMilliseconds = 100;
}

/// <summary>A cooperative work budget begins before enumeration, field reads, comparison and output.
/// The export pump restarts it after its separately measured initial native scope check.
/// One native call cannot be preempted; its overrun is included in ElapsedMilliseconds and stops further work.</summary>
internal sealed class CaptureWorkBudget
{
    private readonly Func<double> _clock;
    private double _started;
    private Func<bool>? _canContinue;
    private readonly int _maximumSteps;
    private readonly double _maximumMilliseconds;
    internal CaptureWorkBudget(int maximumSteps, double maximumMilliseconds, Func<double>? clock = null)
    {
        _maximumSteps = Math.Max(1, maximumSteps);
        _maximumMilliseconds = Math.Max(0.001, maximumMilliseconds);
        _clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        _started = _clock();
    }
    internal int Steps { get; private set; }
    internal double MaximumMilliseconds => _maximumMilliseconds;
    internal void Restart(Func<bool>? canContinue = null) { Steps = 0; _started = _clock(); _canContinue = canContinue; }
    internal double ElapsedMilliseconds => Math.Max(0, _clock() - _started);
    internal bool HasTime => ElapsedMilliseconds < _maximumMilliseconds && (_canContinue?.Invoke() ?? true);
    internal bool TryStep()
    {
        if (Steps >= _maximumSteps || !HasTime) return false;
        Steps++; return true;
    }
}
