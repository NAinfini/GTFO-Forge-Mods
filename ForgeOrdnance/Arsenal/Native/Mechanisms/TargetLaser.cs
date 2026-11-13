using System;
using ForgeVisual;
using UnityEngine;
using Sprite = ForgeVisual.Sprite;

namespace ForgeOrdnance.Arsenal;

/// <summary>How a <see cref="TargetLaser"/> looks; the mod that shows it owns these numbers.</summary>
internal sealed class TargetLaserStyle
{
    /// <summary>The line: thin and even along its length.</summary>
    public BeamStyle Beam { get; set; } = new() { CoreWidth = 0.05f, CorePerMetre = 0.0016f, GlowWidth = 0.55f, CoreOpacity = 1f, GlowOpacity = 0.55f, Column = false };
    /// <summary>The bright dot on the target (default <see cref="SpriteSheet.SoftDot"/>) and its size (m).</summary>
    public SpriteSheet? Dot { get; set; }
    public float DotSize { get; set; } = 0.55f;
    /// <summary>A ring on the floor closing in from <see cref="RingFrom"/> to <see cref="RingTo"/> metres across as the lock fills; null for none.</summary>
    public SpriteSheet? Ring { get; set; }
    public float RingFrom { get; set; } = 7f;
    public float RingTo { get; set; } = 1.6f;
    /// <summary>The radius of the aim mark on the floor (m); 0 for none.</summary>
    public float MarkRadius { get; set; } = 0.9f;
    /// <summary>A light on the target: its reach (m) and strength; reach 0 for none.</summary>
    public float LightRange { get; set; } = 5f;
    public float LightStrength { get; set; } = 1.6f;
    /// <summary>How fast the shown point closes on the reported one (per second), and for how long it carries on along the reported
    /// velocity between two reports (s).</summary>
    public float Follow { get; set; } = 22f;
    public float CarrySeconds { get; set; } = 0.15f;
}

/// <summary>
/// A designator laser: a line straight down from high above onto a point that may move, with a bright dot where it touches, an aim mark
/// and a ring closing in on the floor under it, and a light. It only draws what it is told: <see cref="Aim"/> with each report of the point
/// (every peer can feed it from the same host data; the shown point follows smoothly and carries on between reports), <see cref="Lock"/> from
/// 0 (searching, dim) to 1 (locked, tight and fast), <see cref="Flare"/> at the moment the strike is released, <see cref="Draw"/> every
/// frame, <see cref="Release"/> when done. Choosing the target and deciding the hit are the caller's.
/// </summary>
internal sealed class TargetLaser
{
    private readonly Beam _line;
    private readonly TargetLaserStyle _style;
    private readonly Color _colour;
    private readonly Sprite _dot, _ring;
    private readonly PointLightSpec _lightSpec;
    private GroundMarker? _mark;
    private int _light;
    private Vector3 _point, _velocity, _shown;
    private float _top, _floor, _reported, _last, _flareAt = float.NegativeInfinity;
    private bool _aimed;

    private TargetLaser(Beam line, Color colour, TargetLaserStyle style, Sprite dot, Sprite ring)
    {
        _line = line;
        _colour = colour;
        _style = style;
        _dot = dot;
        _ring = ring;
        _lightSpec = new PointLightSpec(colour, style.LightRange, style.LightStrength, 0f, 0f);
    }

    /// <summary>0 searching to 1 locked.</summary>
    public float Lock { get; set; }
    /// <summary>Overall brightness.</summary>
    public float Opacity { get; set; } = 1f;
    /// <summary>The point as shown this frame.</summary>
    public Vector3 Shown => _shown;

    /// <summary>A laser in <paramref name="colour"/>, hidden until its first <see cref="Aim"/>; null (logged once) when the library's shaders
    /// are not available.</summary>
    public static TargetLaser? Create(Color colour, TargetLaserStyle style)
    {
        if (style == null) throw new ArgumentNullException(nameof(style));
        var line = Beams.Create(colour, style.Beam);
        if (line == null) return null;
        var dot = SpriteSpawn.Of(style.Dot ?? SpriteSheet.SoftDot, SpriteFace.Camera, Vec.Zero, 1f, 0f, 0f, colour, colour);
        dot.FadeIn = 0f;
        dot.Cell = 0;
        var ring = Sprite.None;
        if (style.Ring != null)
        {
            var spawn = SpriteSpawn.Of(style.Ring, SpriteFace.Floor, Vec.Zero, 1f, 0f, 0f, colour, colour);
            spawn.FadeIn = 0f;
            spawn.Cell = 0;
            ring = SpritePool.Attach(in spawn);
        }
        return new TargetLaser(line, colour, style, SpritePool.Attach(in dot), ring);
    }

    /// <summary>The newest report: the line comes down from the height <paramref name="top"/> onto <paramref name="point"/>, the floor under it
    /// is at <paramref name="floor"/>, and the point moves at <paramref name="velocity"/>.</summary>
    public void Aim(float top, Vector3 point, float floor, Vector3 velocity)
    {
        _top = top;
        _point = point;
        _floor = floor;
        _velocity = velocity;
        _reported = Time.time;
        if (_aimed) return;
        _aimed = true;
        _shown = point;
        _last = _reported;
    }

    /// <summary>The strike is released: the line flashes wide and white for a moment.</summary>
    public void Flare() => _flareAt = Time.time;

    /// <summary>Every frame.</summary>
    public void Draw(float now)
    {
        if (!_aimed) return;
        var dt = Mathf.Clamp(now - _last, 0f, 0.1f);
        _last = now;
        var target = _point + _velocity * Math.Min(now - _reported, _style.CarrySeconds);
        _shown += (target - _shown) * Math.Min(1f, dt * _style.Follow);
        var locked = Mathf.Clamp01(Lock);
        var flare = Mathf.Clamp01(1f - (now - _flareAt) / 0.25f);
        var pulse = 0.85f + 0.15f * Mathf.Sin(now * (10f + 34f * locked));
        var level = (0.55f + 0.45f * locked) * pulse * Opacity;
        _line.Opacity = level + 1.5f * flare;
        _line.Width = (1.15f - 0.35f * locked) * (1f + 3.5f * flare);
        _line.Hot = Color.Lerp(_colour, Color.white, 0.55f + 0.45f * flare);
        _line.Show(new Vector3(_shown.x, Math.Max(_top, _shown.y + 2f), _shown.z), _shown);
        var dotSize = _style.DotSize * (0.8f + 0.5f * locked) * pulse * (1f + 2f * flare);
        SpritePool.Move(_dot, _shown, Vec.Zero);
        SpritePool.Resize(_dot, dotSize, 1f);
        SpritePool.Recolour(_dot, new Color(_colour.r, _colour.g, _colour.b, Mathf.Clamp01(level + flare)));
        var floor = new Vector3(_shown.x, _floor + 0.06f, _shown.z);
        if (_style.Ring != null)
        {
            var closing = 1f - (1f - locked) * (1f - locked);
            SpritePool.Move(_ring, floor, Vec.Zero);
            SpritePool.Resize(_ring, Mathf.Lerp(_style.RingFrom, _style.RingTo, closing), 1f);
            SpritePool.Turn(_ring, now * (0.8f + 3f * locked));
            SpritePool.Recolour(_ring, new Color(_colour.r, _colour.g, _colour.b, (0.3f + 0.65f * locked) * Opacity));
        }
        if (_style.MarkRadius > 0f)
        {
            if (_mark == null || !_mark.Alive) _mark = GroundMarker.Place(floor, _style.MarkRadius, _colour, GroundMarkerStyle.Reticle);
            else _mark.Move(floor);
            if (_mark != null) _mark.Opacity = level;
        }
        if (_style.LightRange > 0f) PointLights.Hold(ref _light, _shown + Vec.Up * 0.3f, in _lightSpec, now, level + 2f * flare);
    }

    /// <summary>The laser is over: its line, dot, ring, mark and light go.</summary>
    public void Release()
    {
        _line.Release();
        SpritePool.Kill(_dot);
        SpritePool.Kill(_ring);
        _mark?.Remove();
        _mark = null;
        PointLights.Release(ref _light);
    }
}
