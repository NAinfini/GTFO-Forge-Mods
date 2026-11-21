using System;

namespace ForgeDevelopment.Native;

// Runs on the writer, not the game thread. Only completed finite intervals enter
// the bounded history. The current frame never raises its own threshold.
internal sealed class PerformanceHitchDetector
{
    internal const double FloorMilliseconds = 50, MedianMultiplier = 2.5;
    internal const int HistoryFrames = 120;
    private readonly double[] _history = new double[HistoryFrames], _sorted = new double[HistoryFrames];
    private int _count, _next;
    internal void Reset() { _count = _next = 0; }
    internal double Observe(double milliseconds)
    {
        var median = _count == 0 ? 0 : (_sorted[(_count - 1) / 2] + _sorted[_count / 2]) / 2;
        var threshold = Math.Max(FloorMilliseconds, MedianMultiplier * median);
        if (!double.IsFinite(milliseconds) || milliseconds <= 0) return threshold;
        if (_count == HistoryFrames)
        {
            var remove = Array.BinarySearch(_sorted, 0, _count, _history[_next]);
            Array.Copy(_sorted, remove + 1, _sorted, remove, _count - remove - 1);
            _count--;
        }
        var insert = Array.BinarySearch(_sorted, 0, _count, milliseconds);
        if (insert < 0) insert = ~insert;
        Array.Copy(_sorted, insert, _sorted, insert + 1, _count - insert);
        _sorted[insert] = milliseconds; _count++;
        _history[_next] = milliseconds; _next = (_next + 1) % HistoryFrames;
        return threshold;
    }
}
