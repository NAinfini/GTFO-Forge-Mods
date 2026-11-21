using System;
using System.Collections.Generic;

namespace ForgeDevelopment.Native;

// Fixed bucket space, sparse serialization. Upper bounds are .01 ms through
// 1 ms, .1 ms through 128 ms, 1 ms through 2048 ms, then powers of two.
// Zero has its own bucket; negative scene heights use reflected buckets.
internal sealed class PerformanceHistogram
{
    internal const int Limit = 3350;
    private readonly long[]? _buckets;
    private readonly List<int> _used = new();
    private readonly double _scale;
    internal double Scale => _scale;
    internal long Count { get; private set; }
    internal long Missing { get; private set; }
    internal double Sum { get; private set; }
    internal double Minimum { get; private set; } = double.PositiveInfinity;
    internal double Maximum { get; private set; } = double.NegativeInfinity;
    internal double First { get; private set; } = double.NaN;
    internal double Last { get; private set; } = double.NaN;
    internal string Reason { get; private set; } = "no_samples";

    internal PerformanceHistogram(double scale = 1, bool histogram = true)
    { _scale = scale; if (histogram) _buckets = new long[Limit * 2 + 1]; }

    internal static int Bucket(double value)
    {
        var absolute = Math.Abs(value);
        var id = absolute <= 1 ? (int)Math.Ceiling(absolute * 100)
            : absolute <= 128 ? 100 + (int)Math.Ceiling((absolute - 1) * 10)
            : absolute <= 2048 ? 1370 + (int)Math.Ceiling(absolute - 128)
            : 3290 + (int)Math.Ceiling(Math.Log2(absolute / 2048));
        return Math.Sign(value) * Math.Min(id, Limit);
    }

    internal static double Upper(int id)
    {
        var absolute = Math.Abs(id);
        var upper = absolute <= 100 ? absolute / 100.0
            : absolute <= 1370 ? 1 + (absolute - 100) / 10.0
            : absolute <= 3290 ? 128 + absolute - 1370
            : 2048 * Math.Pow(2, absolute - 3290);
        return Math.Sign(id) * upper;
    }

    internal void Add(double value, string reason = "nonfinite_or_unavailable")
    {
        Last = value;
        if (!double.IsFinite(value) || (_buckets != null && Math.Abs(value * _scale) > Upper(Limit)))
        { Missing++; Reason = reason; return; }
        if (Count++ == 0) First = value;
        Sum += value; Minimum = Math.Min(Minimum, value); Maximum = Math.Max(Maximum, value);
        if (_buckets == null) return;
        var bucket = Bucket(value * _scale) + Limit;
        if (_buckets[bucket]++ == 0) _used.Add(bucket);
    }

    internal double Percentile(double fraction)
    {
        if (Count == 0 || _buckets == null) return double.NaN;
        _used.Sort(); var rank = (long)Math.Ceiling(Count * fraction);
        foreach (var bucket in _used)
            if ((rank -= _buckets[bucket]) <= 0) return Upper(bucket - Limit) / _scale;
        return double.NaN;
    }

    internal IEnumerable<(int Id, long Count)> Buckets()
    {
        _used.Sort();
        foreach (var bucket in _used) yield return (bucket - Limit, _buckets![bucket]);
    }

    internal void Reset()
    {
        foreach (var bucket in _used) _buckets![bucket] = 0;
        _used.Clear(); Count = Missing = 0; Sum = 0;
        Minimum = double.PositiveInfinity; Maximum = double.NegativeInfinity;
        First = Last = double.NaN; Reason = "no_samples";
    }
}
