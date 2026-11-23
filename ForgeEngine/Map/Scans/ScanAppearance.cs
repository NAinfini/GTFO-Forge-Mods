using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeMap;

internal enum ScanPlayerRule { Any, Range, Team }
internal sealed record ScanPlayers(ScanPlayerRule Rule, int Minimum = 1, int Maximum = 4)
{
    public bool Allows(int count) => count > 0 && count <= 4 && (Rule != ScanPlayerRule.Range || count >= Minimum && count <= Maximum);
    // PlayerRequirement enum: None=0, All=1, Solo=2 (native dump 609756).
    public int NativeRequirement => Rule == ScanPlayerRule.Team ? 1 : Rule == ScanPlayerRule.Range && Minimum == 1 && Maximum == 1 ? 2 : 0;
}
internal sealed record ScanColor(int Mode, float R, float G, float B, float A);
internal enum ScanShapeKind { Circle, Hexagon, Square, Rectangle, Ellipse, Heart }
internal readonly record struct ScanShapePoint(double X, double Z);
internal sealed class ScanShape
{
    private readonly ScanShapePoint[] _hitOutline;
    internal ScanShape(ScanShapeKind kind, double width, double depth, double yaw)
    {
        Kind = kind; Width = width; Depth = depth; Yaw = yaw;
        var count = kind is ScanShapeKind.Square or ScanShapeKind.Rectangle ? 4 : kind == ScanShapeKind.Hexagon ? 6 : 96;
        var points = new ScanShapePoint[count];
        var w = width / 2; var d = depth / 2;
        if (count == 4) points = new[] { new ScanShapePoint(-w, -d), new ScanShapePoint(w, -d), new ScanShapePoint(w, d), new ScanShapePoint(-w, d) };
        else for (var i = 0; i < count; i++)
        {
            var a = i * 2 * Math.PI / count;
            points[i] = kind == ScanShapeKind.Heart
                ? new ScanShapePoint(16 * Math.Pow(Math.Sin(a), 3), 13 * Math.Cos(a) - 5 * Math.Cos(2 * a) - 2 * Math.Cos(3 * a) - Math.Cos(4 * a))
                : new ScanShapePoint(w * Math.Cos(a), d * Math.Sin(a));
        }
        if (kind == ScanShapeKind.Heart)
        {
            var min = points.Min(p => p.Z); var max = points.Max(p => p.Z);
            for (var i = 0; i < count; i++) points[i] = new ScanShapePoint(points[i].X * w / 16, (points[i].Z - (min + max) / 2) * depth / (max - min));
            Array.Reverse(points);
        }
        Outline = Array.AsReadOnly(points);
        // Keep the polygon test in unit coordinates: a fixed metre tolerance would turn tiny shapes into spheres.
        _hitOutline = points.Select(p => new ScanShapePoint(p.X / w, p.Z / d)).ToArray();
        Radius = kind is ScanShapeKind.Circle or ScanShapeKind.Ellipse ? Math.Max(w, d) : points.Max(p => Math.Sqrt(p.X * p.X + p.Z * p.Z));
    }
    public ScanShapeKind Kind { get; }
    public double Width { get; }
    public double Depth { get; }
    public double Yaw { get; }
    public double Radius { get; }
    public IReadOnlyList<ScanShapePoint> Outline { get; }
    /// <summary>World horizontal projection plus the native three-dimensional coarse gate. No per-tick allocation.</summary>
    public bool Contains(double dx, double dy, double dz, double worldYaw)
    {
        if (dx * dx + dy * dy + dz * dz > Radius * Radius) return false;
        var a = Yaw + worldYaw; var c = Math.Cos(a); var s = Math.Sin(a);
        var x = (c * dx - s * dz) / (Width / 2); var z = (s * dx + c * dz) / (Depth / 2);
        if (Kind is ScanShapeKind.Circle or ScanShapeKind.Ellipse) return x * x + z * z <= 1;
        var inside = false;
        for (int i = 0, j = _hitOutline.Length - 1; i < _hitOutline.Length; j = i++)
        {
            var p = _hitOutline[i]; var q = _hitOutline[j];
            var cross = (x - p.X) * (q.Z - p.Z) - (z - p.Z) * (q.X - p.X);
            if (Math.Abs(cross) <= 1e-9 && x >= Math.Min(p.X, q.X) - 1e-9 && x <= Math.Max(p.X, q.X) + 1e-9 && z >= Math.Min(p.Z, q.Z) - 1e-9 && z <= Math.Max(p.Z, q.Z) + 1e-9) return true;
            if ((p.Z > z) != (q.Z > z) && x < (q.X - p.X) * (z - p.Z) / (q.Z - p.Z) + p.X) inside = !inside;
        }
        return inside;
    }
}

internal static class ScanAppearanceReading
{
    internal static bool TryRead(JsonElement row, ScanCoverageMode coverage, double? radius, out ScanPlayers? players,
        out IReadOnlyList<ScanColor>? colors, out ScanShape? shape, out string? code, out string? reason)
    {
        players = null; colors = null; shape = null; code = "scan-coverage-players"; reason = "A scan carries an explicit any, range or team player rule.";
        if (!row.TryGetProperty("players", out var p) || p.ValueKind != JsonValueKind.Object
            || !DocumentReading.Text(p, "rule", out var rule)) return false;
        if (rule is "any" or "team")
        {
            if (!DocumentReading.Known(p, out _, "rule")) return false;
            players = new ScanPlayers(rule == "any" ? ScanPlayerRule.Any : ScanPlayerRule.Team);
        }
        else if (rule == "range")
        {
            if (!DocumentReading.Known(p, out _, "rule", "min", "max") || !RuntimeJson.TryInteger(p, "min", 1, 4, out var min)
                || !RuntimeJson.TryInteger(p, "max", min, 4, out var max)) return false;
            players = new ScanPlayers(ScanPlayerRule.Range, (int)min, (int)max);
        }
        else return false;
        if (row.TryGetProperty("colors", out var list))
        {
            code = "scan-coverage-colors"; reason = "Colours need unique native modes 1..6 and four finite Single RGBA channels.";
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 6) return false;
            var parsed = new List<ScanColor>(); var seen = new HashSet<long>();
            foreach (var color in list.EnumerateArray())
            {
                if (color.ValueKind != JsonValueKind.Object || !DocumentReading.Known(color, out _, "mode", "color")
                    || !RuntimeJson.TryInteger(color, "mode", 1, 6, out var mode) || !seen.Add(mode)
                    || !DocumentReading.Numbers(color, "color", 4, -float.MaxValue, float.MaxValue, out var rgba)) return false;
                parsed.Add(new ScanColor((int)mode, (float)rgba![0], (float)rgba[1], (float)rgba[2], (float)rgba[3]));
            }
            colors = parsed.AsReadOnly();
        }
        if (row.TryGetProperty("shape", out var sh))
        {
            code = "scan-coverage-shape"; reason = "A shape needs positive finite dimensions, horizontal yaw and its circumscribed radius; room/zone coverage is exclusive.";
            if (coverage != ScanCoverageMode.Radius || sh.ValueKind != JsonValueKind.Object
                || !DocumentReading.Known(sh, out _, "kind", "width", "depth", "yaw") || !DocumentReading.Text(sh, "kind", out var kind)
                || !Enum.TryParse<ScanShapeKind>(kind, true, out var native) || !Enum.IsDefined(typeof(ScanShapeKind), native)
                || kind != native.ToString().ToLowerInvariant()
                || !DocumentReading.Number(sh, "width", 0, float.MaxValue, out var width) || (float)width <= 0
                || !DocumentReading.Number(sh, "depth", 0, float.MaxValue, out var depth) || (float)depth <= 0
                || !DocumentReading.Number(sh, "yaw", -float.MaxValue, float.MaxValue, out var yaw)) return false;
            if (native is ScanShapeKind.Circle or ScanShapeKind.Square or ScanShapeKind.Hexagon or ScanShapeKind.Heart && width != depth) return false;
            shape = new ScanShape(native, width, depth, yaw);
            if ((float)shape.Radius <= 0 || !float.IsFinite((float)shape.Radius * (float)shape.Radius) || radius == null || (float)radius.Value <= 0 || Math.Abs(radius.Value - shape.Radius) > shape.Radius * 1e-6) return false;
        }
        code = null; reason = null; return true;
    }
}
