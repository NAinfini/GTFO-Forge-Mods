using System;
using System.Collections.Generic;

namespace ForgeMap;

internal readonly record struct SpacePoint(double X, double Y, double Z);
internal delegate SpacePoint? NavigationPointSampler(int dimension, SpacePoint candidate);
internal sealed record ForwardPositionResult(IReadOnlyList<double[]> Positions, int ActualCount, bool Exhausted);

/// <summary>One bounded query; randomness is explicit data, with no timer or persistent random state.</summary>
internal static class ForwardPositions
{
    public const int MaximumCount = 128, MaximumAttempts = 4096;
    public static ForwardPositionResult Find(int dimension, SpacePoint origin, SpacePoint direction, double near,
        double far, double width, int count, int attempts, long seed, bool sector, NavigationPointSampler sample)
    {
        if (!Finite(origin) || !Finite(direction) || !double.IsFinite(near) || !double.IsFinite(far) || !double.IsFinite(width)
            || near < 0 || far < near || width < 0 || sector && (width == 0 || width > 180)
            || count < 0 || count > MaximumCount || attempts < 0 || attempts > MaximumAttempts)
            throw new ArgumentException("forward-position-request-invalid");
        double length = Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        if (!double.IsFinite(length) || length < 1e-9) throw new ArgumentException("forward-direction-invalid");
        double fx = direction.X / length, fz = direction.Z / length;
        var positions = new List<double[]>(count);
        var draws = 0UL;
        double Next() => ForgeCore.DeterministicRandom.Unit(unchecked((ulong)seed), draws++);
        for (int i = 0; i < attempts && positions.Count < count; i++)
        {
            double longitudinal, lateral;
            if (sector)
            {
                double radius = Math.Sqrt(near * near + Next() * (far * far - near * near));
                double angle = (Next() - .5) * width * Math.PI / 180;
                longitudinal = radius * Math.Cos(angle); lateral = radius * Math.Sin(angle);
            }
            else { longitudinal = near + Next() * (far - near); lateral = (Next() - .5) * width; }
            var candidate = new SpacePoint(origin.X + fx * longitudinal + fz * lateral, origin.Y,
                origin.Z + fz * longitudinal - fx * lateral);
            if (sample(dimension, candidate) is not { } point || !Finite(point)) continue;
            // Native projection may move a candidate. Check the answer, not just the point sent to the sampler.
            double x = point.X - origin.X, z = point.Z - origin.Z;
            double ahead = x * fx + z * fz, side = x * fz - z * fx;
            double distance = Math.Sqrt(x * x + z * z);
            if (sector ? distance < near || distance > far || ahead < 0
                    || Math.Abs(Math.Atan2(side, ahead)) > width * Math.PI / 360
                : ahead < near || ahead > far || Math.Abs(side) > width / 2) continue;
            bool duplicate = false;
            foreach (var old in positions)
                if (Math.Abs(old[0] - point.X) < .05 && Math.Abs(old[1] - point.Y) < .05 && Math.Abs(old[2] - point.Z) < .05)
                { duplicate = true; break; }
            if (!duplicate) positions.Add(new[] { point.X, point.Y, point.Z });
        }
        return new(positions, positions.Count, positions.Count < count);
    }
    private static bool Finite(SpacePoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
}
