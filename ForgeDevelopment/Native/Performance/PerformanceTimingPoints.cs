using System;
using System.Diagnostics;
using System.Threading;

namespace ForgeDevelopment.Native;

// Indices are resolved once during installation. No marker names, reflection,
// dictionaries or delegate construction occur on a timing callback.
internal enum TimingPoint
{
    Physics, PhysicsLate, AnimationBegin, AnimationEnd, BehaviourUpdate,
    BehaviourFixed, BehaviourLate, Renderers, RenderSubmission, Navigation,
    EnemyUpdate, EnemyFixed, EnemyMaster, EnemyMasterFixed,
    ExplicitGc, Initialization, EarlyUpdate, PreUpdateOther, PostLateUpdateOther, Count
}

[Flags]
internal enum PerformanceMark
{
    None = 0, WaveStart = 1, SpawnGroup = 2, WaveStop = 4,
    AlarmStart = 8, AlarmStop = 16, Manual = 32
}

internal sealed class TimingCounter
{
    private long _ticks, _calls;
    internal bool Available { get; private set; }
    internal string Reason { get; private set; } = "native_entry_not_installed";
    internal string Entry { get; private set; } = "unavailable";
    internal string Coverage { get; private set; } = "single_native_entry";
    internal string MissingEntries { get; private set; } = "none";
    internal void Installed(string entry) { Entry = entry; Reason = "none"; Available = true; }
    internal void AggregateCoverage(string missing)
    { Coverage = missing == "none" ? "all_leaf_work" : "partial_leaf_work"; MissingEntries = missing; }
    internal void Unavailable(string reason) { Available = false; Reason = reason; }
    internal void Add(long ticks) { Interlocked.Add(ref _ticks, ticks); Interlocked.Increment(ref _calls); }
    internal long Take(out long calls) { calls = Interlocked.Exchange(ref _calls, 0); return Interlocked.Exchange(ref _ticks, 0); }
}

internal sealed class PerformanceTimings
{
    internal readonly TimingCounter[] Points = new TimingCounter[(int)TimingPoint.Count];
    internal readonly uint MainThread;
    private int _marks, _groups, _waves;
    private long _observerTicks, _observerCalls;
    internal PerformanceTimings()
    {
        MainThread = ThreadCpuClock.CurrentThreadId();
        for (var i = 0; i < Points.Length; i++) Points[i] = new TimingCounter();
    }
    internal void Mark(PerformanceMark mark)
    {
        Interlocked.Or(ref _marks, (int)mark);
        if ((mark & PerformanceMark.SpawnGroup) != 0) Interlocked.Increment(ref _groups);
        if ((mark & PerformanceMark.WaveStart) != 0) Interlocked.Increment(ref _waves);
    }
    internal void ObserverCost(long ticks) { Interlocked.Add(ref _observerTicks, ticks); Interlocked.Increment(ref _observerCalls); }
    internal PerformanceMark TakeMarks(out int groups, out int waves)
    {
        groups = Interlocked.Exchange(ref _groups, 0); waves = Interlocked.Exchange(ref _waves, 0);
        return (PerformanceMark)Interlocked.Exchange(ref _marks, 0);
    }
    internal void TakeObserverCost(out long ticks, out long calls)
    { ticks = Interlocked.Exchange(ref _observerTicks, 0); calls = Interlocked.Exchange(ref _observerCalls, 0); }
    internal void Take(ref TimingValues values) { var calls = new TimingValues(); Take(ref values, ref calls); }
    internal void Take(ref TimingValues values, ref TimingValues calls)
    {
        for (var i = 0; i < Points.Length; i++)
        {
            values.Set((TimingPoint)i, Points[i].Take(out var count)); calls.Set((TimingPoint)i, count);
        }
    }
}

// A value in the queue owns its counters, rather than retaining a mutable array.
internal struct TimingValues
{
    private long _physics, _physicsLate, _animationBegin, _animationEnd, _behaviour,
        _fixed, _late, _renderers, _render, _navigation, _enemy, _enemyFixed,
        _enemyMaster, _enemyMasterFixed, _gc, _initialization, _early, _preOther, _postOther;
    internal void Set(TimingPoint point, long value)
    {
        switch (point)
        {
            case TimingPoint.Physics: _physics = value; break;
            case TimingPoint.PhysicsLate: _physicsLate = value; break;
            case TimingPoint.AnimationBegin: _animationBegin = value; break;
            case TimingPoint.AnimationEnd: _animationEnd = value; break;
            case TimingPoint.BehaviourUpdate: _behaviour = value; break;
            case TimingPoint.BehaviourFixed: _fixed = value; break;
            case TimingPoint.BehaviourLate: _late = value; break;
            case TimingPoint.Renderers: _renderers = value; break;
            case TimingPoint.RenderSubmission: _render = value; break;
            case TimingPoint.Navigation: _navigation = value; break;
            case TimingPoint.EnemyUpdate: _enemy = value; break;
            case TimingPoint.EnemyFixed: _enemyFixed = value; break;
            case TimingPoint.EnemyMaster: _enemyMaster = value; break;
            case TimingPoint.EnemyMasterFixed: _enemyMasterFixed = value; break;
            case TimingPoint.ExplicitGc: _gc = value; break;
            case TimingPoint.Initialization: _initialization = value; break;
            case TimingPoint.EarlyUpdate: _early = value; break;
            case TimingPoint.PreUpdateOther: _preOther = value; break;
            case TimingPoint.PostLateUpdateOther: _postOther = value; break;
        }
    }
    internal readonly long Get(TimingPoint point) => point switch
    {
        TimingPoint.Physics => _physics,
        TimingPoint.PhysicsLate => _physicsLate,
        TimingPoint.AnimationBegin => _animationBegin,
        TimingPoint.AnimationEnd => _animationEnd,
        TimingPoint.BehaviourUpdate => _behaviour,
        TimingPoint.BehaviourFixed => _fixed,
        TimingPoint.BehaviourLate => _late,
        TimingPoint.Renderers => _renderers,
        TimingPoint.RenderSubmission => _render,
        TimingPoint.Navigation => _navigation,
        TimingPoint.EnemyUpdate => _enemy,
        TimingPoint.EnemyFixed => _enemyFixed,
        TimingPoint.EnemyMaster => _enemyMaster,
        TimingPoint.EnemyMasterFixed => _enemyMasterFixed,
        TimingPoint.ExplicitGc => _gc,
        TimingPoint.Initialization => _initialization,
        TimingPoint.EarlyUpdate => _early,
        TimingPoint.PreUpdateOther => _preOther,
        TimingPoint.PostLateUpdateOther => _postOther,
        _ => 0
    };
    internal readonly double Milliseconds(TimingPoint point) => Get(point) * (1000.0 / Stopwatch.Frequency);
}
