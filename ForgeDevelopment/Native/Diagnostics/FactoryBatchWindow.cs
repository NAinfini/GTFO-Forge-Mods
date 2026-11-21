using System;

namespace ForgeDevelopment.Native;

internal readonly record struct FactoryBatchDuration(int Batch, double MinimumMs, double MaximumMs);

/// <summary>Bounds a native batch's duration from actual sample times. A delayed frame widens the
/// interval instead of turning the nominal sampling period into a false precision claim.</summary>
internal sealed class FactoryBatchWindow
{
    private double _earliestEntry, _latestEntry, _lastSample;
    private int _batch;
    private bool _begun, _observed;

    internal void Begin(double now)
    {
        _earliestEntry = _lastSample = now;
        _begun = true;
        _observed = false;
    }

    internal FactoryBatchDuration? Observe(double now, int batch)
    {
        if (!_begun) return null;
        if (now < _lastSample) throw new ArgumentOutOfRangeException(nameof(now));
        if (!_observed)
        {
            _observed = true;
            _batch = batch;
            _latestEntry = _lastSample = now;
            return null;
        }
        FactoryBatchDuration? completed = null;
        if (batch != _batch)
        {
            completed = new(_batch, Math.Max(0, _lastSample - _latestEntry) * 1000,
                (now - _earliestEntry) * 1000);
            _earliestEntry = _lastSample;
            _latestEntry = now;
            _batch = batch;
        }
        _lastSample = now;
        return completed;
    }
}
