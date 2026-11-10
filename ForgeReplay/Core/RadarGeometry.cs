using System;

namespace ForgeReplay;

/// <summary>The geometry of a star radar chart in unit circle terms (x right, y up, the first axis at the top, clockwise): where an axis ends, the
/// corners of a polygon of scores, and a filled polygon as a texture's alpha. Drawn by <c>RadarChart</c>; knows nothing of the game.</summary>
internal static class RadarGeometry
{
    /// <summary>The end of axis <paramref name="axis"/> of <paramref name="axes"/> on the unit circle.</summary>
    internal static (float X, float Y) Axis(int axis, int axes)
    {
        var angle = MathF.PI / 2f - 2f * MathF.PI * axis / axes;
        return (MathF.Cos(angle), MathF.Sin(angle));
    }

    /// <summary>The corners of the polygon of <paramref name="scores"/> (0 centre to 1 rim, NaN for a corner with no value): the axes that have a
    /// value only, in order, so an empty axis is skipped and the shape closes over it instead of dipping to the centre.</summary>
    internal static (float X, float Y)[] Corners(ReadOnlySpan<float> scores)
    {
        var count = 0;
        foreach (var s in scores) if (!float.IsNaN(s)) count++;
        var corners = new (float X, float Y)[count];
        var at = 0;
        for (var i = 0; i < scores.Length; i++)
        {
            if (float.IsNaN(scores[i])) continue;
            var (x, y) = Axis(i, scores.Length);
            var s = Math.Clamp(scores[i], 0f, 1f);
            corners[at++] = (x * s, y * s);
        }
        return corners;
    }

    /// <summary>The alpha (0 to 255) of <paramref name="corners"/> filled, over a <paramref name="size"/> square that spans -1 to 1 on both axes, row 0 at the bottom
    /// (a texture's order); 3 x 3 samples a pixel keep the edge smooth. All 0 with fewer than three corners.</summary>
    internal static byte[] Fill((float X, float Y)[] corners, int size)
    {
        var alpha = new byte[size * size];
        if (corners.Length < 3) return alpha;
        const int grid = 3;
        for (var row = 0; row < size; row++)
            for (var col = 0; col < size; col++)
            {
                var hit = 0;
                for (var sy = 0; sy < grid; sy++)
                    for (var sx = 0; sx < grid; sx++)
                        if (Inside(corners, ((col + (sx + 0.5f) / grid) / size * 2f - 1f, (row + (sy + 0.5f) / grid) / size * 2f - 1f))) hit++;
                alpha[row * size + col] = (byte)(255 * hit / (grid * grid));
            }
        return alpha;
    }

    private static bool Inside((float X, float Y)[] polygon, (float X, float Y) p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            if ((polygon[i].Y > p.Y) != (polygon[j].Y > p.Y)
                && p.X < (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X) inside = !inside;
        return inside;
    }
}
