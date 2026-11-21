using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeDevelopment.Native;

internal readonly record struct TraversalPoint(float X, float Y, float Z)
{
    internal bool Finite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
    internal float Distance(TraversalPoint other) => MathF.Sqrt((X - other.X) * (X - other.X)
        + (Y - other.Y) * (Y - other.Y) + (Z - other.Z) * (Z - other.Z));
    internal float[] Array => new[] { X, Y, Z };
}

// Pure input and ordered-arrival rules; native motion and observation stay in AuthoringTraversal.
internal sealed record AuthoringTraversalRoute(int Dimension, TraversalPoint[] Waypoints, float Speed, float Tolerance)
{
    internal static AuthoringTraversalRoute Parse(string json)
    {
        if (json == null || json.Length > 8192) throw new ArgumentException("traversal-route-size");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string[] keys = { "schemaVersion", "dimension", "waypoints", "speed", "tolerance" };
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("traversal-route-object");
        var supplied = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (supplied.Length != keys.Length || keys.Any(k => supplied.Count(p => p == k) != 1))
            throw new ArgumentException("traversal-route-fields");
        if (root.GetProperty("schemaVersion").GetString() != ForgeCore.FormatVersion.Current) throw new ArgumentException("traversal-route-version");
        int dimension = root.GetProperty("dimension").GetInt32();
        float speed = root.GetProperty("speed").GetSingle(), tolerance = root.GetProperty("tolerance").GetSingle();
        if (!float.IsFinite(speed) || speed < .1f || speed > 2.5f
            || !float.IsFinite(tolerance) || tolerance < .05f || tolerance > .5f)
            throw new ArgumentException("traversal-route-speed-or-tolerance");
        var points = root.GetProperty("waypoints");
        if (points.ValueKind != JsonValueKind.Array || points.GetArrayLength() < 2 || points.GetArrayLength() > 16)
            throw new ArgumentException("traversal-route-waypoints");
        var route = new List<TraversalPoint>();
        float length = 0;
        foreach (var row in points.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 3)
                throw new ArgumentException("traversal-route-point");
            var p = new TraversalPoint(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle());
            if (!p.Finite) throw new ArgumentException("traversal-route-nonfinite");
            if (route.Count > 0)
            {
                float segment = p.Distance(route[^1]);
                if (segment <= 2 * tolerance || segment > 8f) throw new ArgumentException("traversal-route-segment");
                length += segment;
                if (length > 40f) throw new ArgumentException("traversal-route-length");
            }
            route.Add(p);
        }
        return new(dimension, route.ToArray(), speed, tolerance);
    }

    internal int Arrive(int next, TraversalPoint position)
    {
        if (!position.Finite) throw new InvalidOperationException("traversal-position-nonfinite");
        if (next < 0 || next > Waypoints.Length) throw new ArgumentOutOfRangeException(nameof(next));
        // One observed position proves at most the next arrival, never an unobserved intermediate point.
        return next < Waypoints.Length && position.Distance(Waypoints[next]) <= Tolerance ? next + 1 : next;
    }

    internal float Step(float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0 || deltaTime > .1f)
            throw new InvalidOperationException("traversal-native-delta-time-out-of-range");
        float step = Speed * deltaTime;
        if (step > .25f) throw new InvalidOperationException("traversal-input-step-too-large");
        return step;
    }
}
