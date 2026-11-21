using System;

namespace ForgeDevelopment.Native;

/// <summary>What the level factory showed at one sample: how many job instances it has created, which batch and job it is
/// in. Plain values so the progress decision has no game dependency and no allocation.</summary>
internal readonly record struct FactorySample(uint JobInstances, int Batch, long Job);

[Flags]
internal enum FactoryProgress { None = 0, BatchChanged = 1, Heartbeat = 2, Stalled = 4 }

/// <summary>Decides, from periodic samples of the native factory, when to say "still working" and when to say
/// "no progress". Progress is any change of job-instance count, batch or current job; a job that keeps returning
/// false therefore shows as a stall, while many short jobs never do. Stall reports are rate bounded: the first
/// one comes after <see cref="StallSeconds"/> without progress and each repeat doubles the wait up to
/// <see cref="MaxRepeatSeconds"/>, so a factory that stays stuck produces about one line a minute.</summary>
internal sealed class FactoryProgressTracker
{
    internal const double StallSeconds = 30, MaxRepeatSeconds = 60, HeartbeatSeconds = 10;
    private bool _started;
    private FactorySample _last;
    private uint _baseline;
    private double _begin, _batchBegin, _progressAt, _nextHeartbeat, _nextStall, _repeatSeconds;

    internal int Batch => _last.Batch;
    internal int StallReports { get; private set; }
    /// <summary>Jobs the factory created since the first sample of this generation. The native counter itself
    /// never resets within a session, so its raw value is the whole session's total and says nothing about this level.</summary>
    internal uint JobsCreated => _last.JobInstances >= _baseline ? _last.JobInstances - _baseline : _last.JobInstances;
    internal double Elapsed(double now) => now - _begin;
    internal double InBatch(double now) => now - _batchBegin;
    internal double SinceProgress(double now) => now - _progressAt;

    internal void Reset() { _started = false; StallReports = 0; }

    internal FactoryProgress Observe(double now, in FactorySample sample)
    {
        if (!_started)
        {
            _started = true; _last = sample; _baseline = sample.JobInstances; StallReports = 0;
            _begin = _batchBegin = _progressAt = now;
            _nextHeartbeat = now + HeartbeatSeconds; _nextStall = now + StallSeconds; _repeatSeconds = StallSeconds;
            return FactoryProgress.BatchChanged;
        }
        var result = FactoryProgress.None;
        if (sample.Batch != _last.Batch) { result |= FactoryProgress.BatchChanged; _batchBegin = now; }
        if (sample != _last)
        {
            _last = sample; _progressAt = now; StallReports = 0;
            _nextStall = now + StallSeconds; _repeatSeconds = StallSeconds;
        }
        else if (now >= _nextStall)
        {
            result |= FactoryProgress.Stalled; StallReports++;
            _repeatSeconds = Math.Min(MaxRepeatSeconds, _repeatSeconds * 2);
            _nextStall = now + _repeatSeconds;
        }
        if (now >= _nextHeartbeat) { result |= FactoryProgress.Heartbeat; _nextHeartbeat = now + HeartbeatSeconds; }
        return result;
    }
}
