using System;

namespace ForgeOrdnance.Arsenal;

/// <summary>Whether a candidate target can be reached (a path to it, short enough, in sight), asked in order of distance.</summary>
internal interface IReach { bool Reaches(int index); }

/// <summary>Whether the straight move between two points runs into the level (wall, closed door, anything solid).</summary>
internal interface IBlocker { bool Blocked(float ax, float ay, float az, float bx, float by, float bz); }

internal enum RouteStep { Moving, Arrived, Blocked }

/// <summary>The cutting beam's floor point and the path it follows, on plain numbers so it is testable. The corners come from
/// the game's navigation mesh (<c>Begin</c>, then <c>Add</c> for every corner after the start); the point walks them at a constant
/// speed and before every move asks the blocker about that very move, so it never crosses anything solid even where the path and
/// the level disagree. Buffers are fixed: nothing allocates while the beam moves.</summary>
internal sealed class BeamRoute
{
    /// <summary>Most corners one route holds; a longer path is refused rather than cut short.</summary>
    public const int Capacity = 48;

    private readonly float[] _x = new float[Capacity], _y = new float[Capacity], _z = new float[Capacity];
    private int _count, _next;

    public float X, Y, Z;
    /// <summary>Metres walked by the last <see cref="Step{T}"/>.</summary>
    public float Moved;

    public BeamRoute(float x, float y, float z) { X = x; Y = y; Z = z; }

    public int Count => _count;

    /// <summary>Forgets the corners; the point stays where it is.</summary>
    public void Begin() { _count = 0; _next = 0; }

    /// <summary>Appends a corner; false (and the route is cleared) when the path has more corners than fit.</summary>
    public bool Add(float x, float y, float z)
    {
        if (_count == Capacity) { Begin(); return false; }
        _x[_count] = x; _y[_count] = y; _z[_count] = z;
        _count++;
        return true;
    }

    /// <summary>Walks up to <paramref name="distance"/> metres along the corners. Each move is first offered to the blocker; a
    /// blocked move is not made and the point stays where it is.</summary>
    public RouteStep Step<T>(float distance, ref T blocker) where T : IBlocker
    {
        Moved = 0f;
        while (distance > 1e-6f)
        {
            if (_next >= _count) return RouteStep.Arrived;
            var dx = _x[_next] - X;
            var dz = _z[_next] - Z;
            var left = MathF.Sqrt(dx * dx + dz * dz);
            if (left <= 1e-4f) { Y = _y[_next]; _next++; continue; }
            var move = MathF.Min(distance, left);
            var f = move / left;
            var ny = Y + (_y[_next] - Y) * f;
            if (blocker.Blocked(X, Y, Z, X + dx * f, ny, Z + dz * f)) return RouteStep.Blocked;
            X += dx * f; Y = ny; Z += dz * f;
            Moved += move;
            distance -= move;
            if (move >= left - 1e-4f) _next++;
        }
        return _next >= _count ? RouteStep.Arrived : RouteStep.Moving;
    }
}
