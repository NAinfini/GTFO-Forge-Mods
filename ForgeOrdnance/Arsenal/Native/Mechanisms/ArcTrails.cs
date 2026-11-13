using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>Self-ending curved trails, on ForgeVisual's <see cref="Trails"/>: moves into ForgeVisual whole when a second package needs it.</summary>
internal static class ArcTrails
{
    private const int MaxArcs = 24;

    private sealed class Flight
    {
        internal Trail Trail = null!;
        internal Vector3 From, Control, To;
        internal float Start, Duration;
    }

    private static readonly List<Flight> Arcs = new();

    /// <summary>A trail of <paramref name="style"/> that swings from <paramref name="from"/> onto <paramref name="to"/> along a curve (a
    /// quadratic Bezier bent out to one side and up, the side and bend following <paramref name="seed"/> so every peer given the same
    /// numbers draws the same curve), moving at about <paramref name="speed"/> metres a second for at least <paramref name="minTime"/> and
    /// at most <paramref name="maxTime"/> seconds, then ends by itself. Purely cosmetic: it decides nothing. At most 24 are drawn at once;
    /// more are dropped.</summary>
    internal static void Start(Vector3 from, Vector3 to, uint seed, TrailStyle style, float speed, float minTime, float maxTime)
    {
        if (Arcs.Count >= MaxArcs || speed <= 0f || !float.IsFinite(from.x + from.y + from.z + to.x + to.y + to.z)) return;
        var trail = Trails.Rent(from, style);
        if (trail == null) return;
        var line = to - from;
        var length = Vec.Length(line);
        var side = Vector3.Cross(line.normalized, Vec.Up);
        if (Vec.SqrLength(side) < 0.01f) side = Vector3.right;
        side.Normalize();
        var bend = Mathf.Clamp(length * 0.35f, 1.5f, 9f);
        var control = (from + to) * 0.5f + side * (((seed & 1u) == 0 ? 1f : -1f) * bend) + Vec.Up * (bend * 0.35f);
        Arcs.Add(new Flight { Trail = trail, From = from, Control = control, To = to, Start = Time.time, Duration = Mathf.Clamp(length / speed, minTime, maxTime) });
    }

    /// <summary>Every frame; <paramref name="now"/> is the frame's <c>Time.time</c>.</summary>
    internal static void Tick(float now)
    {
        for (var i = Arcs.Count - 1; i >= 0; i--)
        {
            var a = Arcs[i];
            var u = Mathf.Clamp01((now - a.Start) / a.Duration);
            var r = 1f - u;
            if (a.Trail.Alive) a.Trail.Move(r * r * a.From + 2f * r * u * a.Control + u * u * a.To, u);
            if (u < 1f && a.Trail.Alive) continue;
            a.Trail.End();
            Arcs.RemoveAt(i);
        }
    }

    /// <summary>The level is over: ForgeVisual has taken the trails back, the flights go too.</summary>
    internal static void Clear() => Arcs.Clear();
}
