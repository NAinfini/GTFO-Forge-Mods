using System;

namespace ForgeVisual;

/// <summary>The time windows of sustained effects (<c>Fx.Hold</c>): a hold near a live, still-emitting instance of the same effect
/// moves that instance and keeps it emitting; an instance not held again within its window stops emitting, lets its particles finish
/// and is then free again. Fixed arrays per effect, no allocation.</summary>
internal sealed class HoldBook
{
    private readonly bool[] _playing, _emitting;
    private readonly float[] _until, _x, _y, _z;
    private readonly long[] _started;
    private long _sequence;

    internal HoldBook(int copies)
    {
        if (copies <= 0) throw new ArgumentOutOfRangeException(nameof(copies), "An effect keeps at least one copy.");
        _playing = new bool[copies];
        _emitting = new bool[copies];
        _until = new float[copies];
        _x = new float[copies];
        _y = new float[copies];
        _z = new float[copies];
        _started = new long[copies];
    }

    internal int Copies => _playing.Length;
    internal bool Playing(int copy) => _playing[copy];
    internal bool Emitting(int copy) => _emitting[copy];

    /// <summary>A playing, still-emitting copy within <paramref name="reach"/> metres of the point, or -1.</summary>
    internal int Near(float x, float y, float z, float reach)
    {
        var best = -1;
        var bestDistance = reach * reach;
        for (var i = 0; i < _playing.Length; i++)
        {
            if (!_playing[i] || !_emitting[i]) continue;
            var dx = _x[i] - x;
            var dy = _y[i] - y;
            var dz = _z[i] - z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d <= bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>A free copy, or the oldest playing one when all play (it restarts).</summary>
    internal int Take()
    {
        var oldest = 0;
        for (var i = 0; i < _playing.Length; i++)
        {
            if (!_playing[i]) return i;
            if (_started[i] < _started[oldest]) oldest = i;
        }
        return oldest;
    }

    /// <summary>Starts <paramref name="copy"/>; a sustained start (<paramref name="window"/> above 0) emits until now + window.</summary>
    internal void Start(int copy, float x, float y, float z, float now, float window)
    {
        _playing[copy] = true;
        _emitting[copy] = window > 0f;
        _until[copy] = window > 0f ? now + window : 0f;
        _started[copy] = ++_sequence;
        Place(copy, x, y, z);
    }

    /// <summary>A held copy keeps emitting for another window from now.</summary>
    internal void Extend(int copy, float x, float y, float z, float now, float window)
    {
        _until[copy] = now + window;
        Place(copy, x, y, z);
    }

    /// <summary>True when <paramref name="copy"/> has to stop emitting now (its window ran out); it keeps playing until its
    /// particles are gone (<see cref="Finished"/>).</summary>
    internal bool Expired(int copy, float now)
    {
        if (!_playing[copy] || !_emitting[copy] || now < _until[copy]) return false;
        _emitting[copy] = false;
        return true;
    }

    /// <summary>Stops emitting at once (the user ended the hold).</summary>
    internal void Stop(int copy) => _emitting[copy] = false;

    internal void Finished(int copy)
    {
        _playing[copy] = false;
        _emitting[copy] = false;
    }

    private void Place(int copy, float x, float y, float z)
    {
        _x[copy] = x;
        _y[copy] = y;
        _z[copy] = z;
    }
}

/// <summary>The last positions of a moving thing at a fixed sample rate, newest first: the points of a ribbon trail. The newest
/// point always follows the thing; a new point is pushed every <see cref="Step"/> seconds. Fixed storage.</summary>
internal sealed class RibbonRing<T> where T : struct
{
    private readonly T[] _ring;
    private int _head;
    private float _next;

    internal RibbonRing(int points, float step)
    {
        if (points < 2) throw new ArgumentOutOfRangeException(nameof(points), "A ribbon has at least two points.");
        if (!(step > 0f)) throw new ArgumentOutOfRangeException(nameof(step), "The sample step must be positive.");
        _ring = new T[points];
        Step = step;
    }

    internal int Count => _ring.Length;
    internal float Step { get; }

    /// <summary>Every point at <paramref name="start"/>.</summary>
    internal void Reset(T start, float now)
    {
        for (var i = 0; i < _ring.Length; i++) _ring[i] = start;
        _head = 0;
        _next = now + Step;
    }

    internal void Sample(T position, float now)
    {
        while (now >= _next)
        {
            _head = (_head + 1) % _ring.Length;
            _ring[_head] = position;
            _next += Step;
            // After a long stall the ribbon restarts its clock instead of filling every missed sample with one point.
            if (now - _next > 0.1f) _next = now + Step;
        }
        _ring[_head] = position;
    }

    /// <summary>Point <paramref name="i"/>: 0 is the newest.</summary>
    internal T this[int i] => _ring[(_head - i + _ring.Length * 2) % _ring.Length];
}

/// <summary>The numbers behind a ground marker's animation.</summary>
internal static class MarkerMath
{
    /// <summary>A hidden rim (the dim dashed line behind walls) is shown only near the mark: within its reach plus this across.</summary>
    internal const float RevealMargin = 3f, RevealHeight = 2.5f;

    /// <summary>How much of the hidden rim to draw for an eye <paramref name="across"/> metres from the centre on the floor plane and
    /// <paramref name="up"/> above or below it, fading over the last metre, so further rooms and the floors above and below never see it.</summary>
    internal static float Reveal(float across, float up, float reach)
        => Clamp01(reach + RevealMargin - across) * Clamp01(RevealHeight - Math.Abs(up));

    /// <summary>A mark sweeps on clockwise from north over <paramref name="seconds"/>.</summary>
    internal static float Sweep(float age, float seconds) => seconds <= 0f ? 1f : Clamp01(age / seconds);

    /// <summary>The ping's phase after <paramref name="seconds"/> at <paramref name="perSecond"/>, in [0, 1).</summary>
    internal static float Advance(float phase, float perSecond, float seconds)
    {
        var next = phase + Math.Max(0f, perSecond) * Math.Max(0f, seconds);
        return next - (float)Math.Floor(next);
    }

    /// <summary>The blink of a tick, 1 at the tick and fading.</summary>
    internal static float Blink(float sinceTick) => sinceTick < 0f ? 0f : (float)Math.Exp(-sinceTick * 7f);

    internal static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
