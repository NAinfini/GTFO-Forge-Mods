using System;
using System.Collections.Generic;

namespace ForgeDevelopment.Native;

/// <summary>One cooperative collection-to-output pass. A small completed batch waits for the next frame when
/// collection used the remaining time. Output then spends that frame's same budget before any more native reads.</summary>
internal sealed class CaptureChangePass
{
    private readonly CaptureChangeDetector _detector;
    private readonly double _sweepIntervalMilliseconds;
    private readonly Func<double> _clock;
    private CaptureChangeResult? _pending;
    private CaptureChangeResult _scratch = new();
    private long _pendingTick;
    private double _sweepStarted, _nextSweep;

    /// <param name="sweepIntervalMilliseconds">The shortest time between the starts of two complete sweeps. A sweep that
    /// itself takes longer starts the next one at once; a quiet or small world rests in between instead of reading
    /// every row again on the very next frame. Zero sweeps continuously.</param>
    internal CaptureChangePass(CaptureChangeDetector detector, double sweepIntervalMilliseconds = 0, Func<double>? clock = null)
    {
        _detector = detector;
        _sweepIntervalMilliseconds = Math.Max(0, sweepIntervalMilliseconds);
        _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }
    internal double LastMilliseconds { get; private set; }
    internal int LastSteps { get; private set; }
    internal double PeakMilliseconds { get; private set; }
    internal long LastAllocatedBytes { get; private set; }
    internal long Frames { get; private set; }
    internal long BudgetStops { get; private set; }
    internal bool Pending => _pending != null;
    internal void Run(Func<IEnumerable<CaptureRow?>> source, long tick, CaptureWorkBudget budget,
        Action<CaptureChangeResult, long> write)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopped = false;
        try
        {
            if (_pending != null)
            {
                if (!budget.HasTime) return;
                write(_pending, _pendingTick); _pending = null;
            }
            if (!budget.HasTime) return;
            var starting = !_detector.IsScanning;
            if (starting)
            {
                var now = _clock();
                if (now < _nextSweep) return;
                _sweepStarted = now;
            }
            var result = _detector.Poll(source, budget, _scratch);
            if (!_detector.IsScanning) _nextSweep = _sweepStarted + _sweepIntervalMilliseconds;
            stopped = result.BudgetExhausted;
            if (result.ChangedRows.Count == 0) return;
            // Output owns this batch until the background serializer drains it.
            _scratch = new CaptureChangeResult();
            _pending = result; _pendingTick = tick;
            if (!budget.HasTime) return;
            write(_pending, _pendingTick); _pending = null;
        }
        finally
        {
            LastMilliseconds = budget.ElapsedMilliseconds; LastSteps = budget.Steps;
            PeakMilliseconds = Math.Max(PeakMilliseconds, LastMilliseconds);
            if (stopped || !budget.HasTime) BudgetStops++;
            LastAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore; Frames++;
        }
    }
    internal void Reset() { _pending = null; _nextSweep = 0; _detector.Reset(); }
}
