using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ForgePerformance.LoadShedding;

// What the cuts do when a level starts (classifying thousands of decorations and lights, changing graphics settings) is queued
// instead of run in one go, and the queue is drained a few milliseconds per frame. The level starts being set up as soon as it is
// built, while the elevator is still arriving, so that the pieces that cannot be split (each scan of the whole scene is one native
// call) stall the elevator ride, not the first frames in the level. A piece not reached yet is simply not applied: the object or
// setting stays vanilla until then, and no cut needs the whole level at once. A piece that throws is reported and the rest go on.
internal sealed class LevelWork
{
    // The most a frame spends on the queue once the level runs, and before that, in the elevator.
    internal const double LevelSliceMilliseconds = 2, ElevatorSliceMilliseconds = 12;
    private readonly Queue<(string Name, Action Run)> _items = new();
    private readonly Action<string> _report;

    internal LevelWork(Action<string> report) => _report = report;

    internal void Add(string name, Action run) => _items.Enqueue((name, run));
    internal void Clear() => _items.Clear();

    // At least one piece per call, then until the slice is used. A piece guards itself (SetupCut); one that throws is reported here.
    internal void Run(double sliceMilliseconds)
    {
        if (_items.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        do
        {
            var (name, run) = _items.Dequeue();
            try { run(); }
            catch (Exception error) { _report("level-start.failed piece=" + name + " " + error.GetType().Name + ": " + error.Message); }
        }
        while (_items.Count != 0 && (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency < sliceMilliseconds);
    }
}
