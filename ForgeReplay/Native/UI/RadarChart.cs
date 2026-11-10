using System;
using ForgeCore;
using ForgeUI;
using UnityEngine;

namespace ForgeReplay;

/// <summary>A star radar chart drawn on a <see cref="HudCanvas"/> from numbers alone: a grid of rings and axes, and any number of polygons laid over each
/// other, each given as its scores (0 centre to 1 rim, NaN for an axis with no value), a colour, whether it is filled (translucent) and how thick its outline
/// is. A polygon's filled shape is made into a sprite once, the first time it is wanted filled with those scores; changing which polygon is filled or
/// bold afterwards only changes what is drawn, and drawing is a handful of lines and sprites that allocate nothing. Filled polygons are drawn over the
/// unfilled ones. Only the replay draws a radar, so it lives here, not in ForgeUI.</summary>
internal sealed class RadarChart
{
    private const int FillSize = 128;
    private const float FillAlpha = 0.3f;

    private sealed class Layer
    {
        internal float[] Scores = Array.Empty<float>();
        internal Color Colour;
        internal bool Filled;
        internal float Line = 2.2f;
        internal (float X, float Y)[] Corners = Array.Empty<(float, float)>();
        internal Sprite? Fill;
    }

    private readonly System.Collections.Generic.List<Layer> _layers = new();
    private int _axes;

    /// <summary>Sets polygon number <paramref name="layer"/> to <paramref name="scores"/> (one per axis). Its corners are worked out, and its filled shape made, only
    /// when the scores differ from the last call's; colour, fill and outline may change freely.</summary>
    internal void Set(int layer, ReadOnlySpan<float> scores, Color colour, bool filled = true, float line = 2.2f)
    {
        while (_layers.Count <= layer) _layers.Add(new Layer());
        var l = _layers[layer];
        l.Colour = colour; l.Filled = filled; l.Line = line;
        if (_axes != scores.Length) { _axes = scores.Length; foreach (var other in _layers) Reset(other); }
        if (!SameScores(l.Scores, scores))
        {
            l.Scores = scores.ToArray();
            l.Corners = RadarGeometry.Corners(scores);
            Release(l);
        }
        if (filled && l.Fill == null && l.Corners.Length >= 3) l.Fill = MakeFill(l.Corners);
    }

    private static Sprite MakeFill((float X, float Y)[] corners)
    {
        var alpha = RadarGeometry.Fill(corners, FillSize);
        var texture = new Texture2D(FillSize, FillSize, TextureFormat.RGBA32, false) { name = "ForgeReplay.Radar", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        var pixels = new Color32[alpha.Length];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, alpha[i]);
        Il2CppArrays.Upload(texture, pixels);
        texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, FillSize, FillSize), new Vector2(0.5f, 0.5f), FillSize);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    /// <summary>Takes every polygon away (and the sprites with them).</summary>
    internal void Clear()
    {
        foreach (var l in _layers) Reset(l);
        _layers.Clear();
        _axes = 0;
    }

    private static bool SameScores(float[] a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (!(a[i] == b[i] || float.IsNaN(a[i]) && float.IsNaN(b[i]))) return false;
        return true;
    }

    private static void Reset(Layer l) { l.Scores = Array.Empty<float>(); l.Corners = Array.Empty<(float, float)>(); Release(l); }

    private static void Release(Layer l)
    {
        if (l.Fill == null) return;
        var texture = l.Fill.texture;
        UnityEngine.Object.Destroy(l.Fill);
        UnityEngine.Object.Destroy(texture);
        l.Fill = null;
    }

    /// <summary>Draws the grid (<paramref name="rings"/> rings) and every polygon centred on (<paramref name="x"/>, <paramref name="y"/>) with the rim <paramref name="radius"/>
    /// from it. An axis a polygon has no value for gets a small hollow mark on the rim in that polygon's colour. <paramref name="labels"/> (one per axis, may be null)
    /// stand beyond the rim, <paramref name="px"/> tall.</summary>
    internal void Draw(HudCanvas c, float x, float y, float radius, Color grid, string[]? labels = null, float px = 16f, Color? labelColour = null, int rings = 4)
    {
        if (_axes < 3) return;
        var faint = new Color(grid.r, grid.g, grid.b, grid.a * 0.55f);
        for (var ring = 1; ring <= rings; ring++)
        {
            var r = radius * ring / rings;
            for (var i = 0; i < _axes; i++)
            {
                var (ax, ay) = RadarGeometry.Axis(i, _axes); var (bx, by) = RadarGeometry.Axis((i + 1) % _axes, _axes);
                c.Line(x + ax * r, y + ay * r, x + bx * r, y + by * r, ring == rings ? 1.6f : 1f, ring == rings ? grid : faint);
            }
        }
        for (var i = 0; i < _axes; i++)
        {
            var (ax, ay) = RadarGeometry.Axis(i, _axes);
            c.Line(x, y, x + ax * radius, y + ay * radius, 1f, faint);
        }
        for (var pass = 0; pass < 2; pass++)
            foreach (var l in _layers)
            {
                if (l.Filled != (pass == 1)) continue;
                if (l.Fill != null) c.Sprite(l.Fill, x, y, 2f * radius, 2f * radius, new Color(l.Colour.r, l.Colour.g, l.Colour.b, l.Colour.a * FillAlpha));
                var n = l.Corners.Length;
                for (var i = 0; n >= 2 && i < (n == 2 ? 1 : n); i++)
                {
                    var a = l.Corners[i]; var b = l.Corners[(i + 1) % n];
                    c.Line(x + a.X * radius, y + a.Y * radius, x + b.X * radius, y + b.Y * radius, l.Line, l.Colour);
                }
                if (n == 1) c.Rect(x + l.Corners[0].X * radius, y + l.Corners[0].Y * radius, 6f, 6f, l.Colour);
                for (var i = 0; i < l.Scores.Length; i++)
                {
                    if (!float.IsNaN(l.Scores[i])) continue;
                    var (ax, ay) = RadarGeometry.Axis(i, _axes);
                    c.Frame(x + ax * radius, y + ay * radius, 9f, 9f, 0f, new Color(l.Colour.r, l.Colour.g, l.Colour.b, l.Colour.a * 0.7f));
                }
            }
        if (labels == null) return;
        for (var i = 0; i < _axes && i < labels.Length; i++)
        {
            var (ax, ay) = RadarGeometry.Axis(i, _axes);
            var align = Math.Abs(ax) < 0.2f ? HudAlign.Centre : ax > 0 ? HudAlign.Left : HudAlign.Right;
            c.Text(labels[i], x + ax * (radius + 12f), y + ay * (radius + px * 0.9f), px, labelColour ?? grid, align);
        }
    }
}
