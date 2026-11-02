using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>Puffs a trail leaves along its path at a fixed rate: into a sprite sheet of the shared <see cref="SpritePool"/>, or into a
/// pack's <see cref="ParticleChannel"/>. All numbers are the owner's.</summary>
public sealed class TrailPuff
{
    /// <summary>Draw with this sheet (sprites) ...</summary>
    public SpriteSheet? Sheet { get; set; }
    /// <summary>... or emit into this channel (a Unity-authored system).</summary>
    public ParticleChannel? Channel { get; set; }
    public float PerSecond { get; set; } = 14f;
    /// <summary>Emission stops once the trail's progress reaches this (1: never).</summary>
    public float StopAt { get; set; } = 1f;
    /// <summary>Added to the point a puff starts from (m), and a random horizontal spread around it (m).</summary>
    public Vector3 Offset { get; set; }
    public float Spread { get; set; }
    /// <summary>Lift by this fraction of the puff's own size (a flame sprite standing on its base).</summary>
    public float LiftBySize { get; set; }
    /// <summary>Base velocity and a random range added per axis (±).</summary>
    public Vector3 Velocity { get; set; }
    public Vector3 VelocityJitter { get; set; }
    /// <summary>Start size (m), random fraction (±), growth with progress (size x (1 + growth x progress)) and end size over start size.</summary>
    public float Size { get; set; } = 0.5f;
    public float SizeJitter { get; set; }
    public float SizeGrowth { get; set; }
    public float SizeEnd { get; set; } = 2f;
    public float Life { get; set; } = 1f;
    public float LifeJitter { get; set; }
    public Color Colour0 { get; set; } = Color.white;
    public Color Colour1 { get; set; } = new(1f, 1f, 1f, 0f);
    public float Drag { get; set; }
    /// <summary>Sprite puffs: play the sheet's frames over the life.</summary>
    public bool Flip { get; set; }
    /// <summary>Sprite puffs: random turn rate up to this (radians per second, ±).</summary>
    public float Spin { get; set; }
    public int Priority { get; set; }
}

/// <summary>How a trail looks: a ribbon of the last positions, a flame and a glow riding its head, and puffs left along the way. Every
/// part is optional; the numbers are the owner's.</summary>
public sealed class TrailStyle
{
    /// <summary>Ribbon points (0: no ribbon), seconds between them, width (m) and colour at the head and the tail.</summary>
    public int RibbonPoints { get; set; }
    public float RibbonStep { get; set; } = 1f / 60f;
    public float RibbonWidth { get; set; } = 0.09f;
    public Color RibbonHead { get; set; } = Color.white;
    public Color RibbonTail { get; set; } = new(1f, 1f, 1f, 0f);
    public FxBlend RibbonBlend { get; set; } = FxBlend.Alpha;

    /// <summary>A flame streak riding the head, trailing back along the motion: its sheet (played at <see cref="FlameFps"/>), size,
    /// length per size, growth with progress and colour.</summary>
    public SpriteSheet? Flame { get; set; }
    public float FlameFps { get; set; } = 30f;
    public float FlameSize { get; set; } = 0.55f;
    public float FlameAspect { get; set; } = 2.2f;
    public float FlameGrowth { get; set; } = 1.3f;
    public Color FlameColour { get; set; } = new(1f, 0.85f, 0.6f, 1f);
    /// <summary>A glow riding the head.</summary>
    public SpriteSheet? Glow { get; set; }
    public float GlowSize { get; set; } = 0.9f;
    public Color GlowColour { get; set; } = new(1f, 0.6f, 0.25f, 0.7f);
    /// <summary>The head sprites sit this far behind the moving point (m) and flicker by this fraction.</summary>
    public float HeadBack { get; set; } = 0.25f;
    public float Flicker { get; set; } = 0.15f;

    public TrailPuff[] Puffs { get; set; } = Array.Empty<TrailPuff>();
}

/// <summary>One trail behind a moving thing (a thrown ball, a falling shell, a rocket). Rent it, <see cref="Move"/> it every frame,
/// <see cref="End"/> it; the ribbon collapses and the puffs already left finish on their own.</summary>
public sealed class Trail
{
    private readonly TrailStyle _style;
    private readonly RateDebt[] _debts;
    private readonly RibbonRing<Vector3>? _ring;
    private LineRenderer? _line;
    private Sprite _flame = Sprite.None, _glow = Sprite.None;
    private Vector3 _last, _direction = Vector3.down;
    private float _lastTime = -1f;
    internal bool Live;

    internal Trail(TrailStyle style)
    {
        _style = style;
        _debts = new RateDebt[style.Puffs.Length];
        if (style.RibbonPoints >= 2) _ring = new RibbonRing<Vector3>(style.RibbonPoints, style.RibbonStep);
    }

    internal TrailStyle Style => _style;
    public bool Alive => Live;

    internal void Begin(Vector3 start, LineRenderer? line)
    {
        Live = true;
        _last = start;
        _lastTime = -1f;
        _direction = Vector3.down;
        for (var i = 0; i < _debts.Length; i++) _debts[i].Reset();
        _line = line;
        if (_ring != null && line != null)
        {
            _ring.Reset(start, Scene.Now);
            line.gameObject.SetActive(true);
            for (var i = 0; i < _ring.Count; i++) line.SetPosition(i, start);
        }
        var s = _style;
        if (s.Flame != null)
        {
            var spawn = SpriteSpawn.Of(s.Flame, SpriteFace.Streak, start, 1f, s.FlameSize, s.FlameSize, s.FlameColour, s.FlameColour);
            spawn.Velocity = Vector3.down;
            spawn.Aspect = s.FlameAspect;
            spawn.Flip = true;
            spawn.FlipFps = s.FlameFps;
            spawn.FadeIn = 0f;
            _flame = SpritePool.Attach(in spawn);
        }
        if (s.Glow != null)
        {
            var spawn = SpriteSpawn.Of(s.Glow, SpriteFace.Camera, start, 1f, s.GlowSize, s.GlowSize, s.GlowColour, s.GlowColour);
            spawn.FadeIn = 0f;
            spawn.Cell = 0;
            _glow = SpritePool.Attach(in spawn);
        }
    }

    /// <summary>Every frame: the moving thing is at <paramref name="position"/>; <paramref name="progress"/> runs 0 to 1 over its
    /// journey (flames lengthen, puffs grow, and puffs with <see cref="TrailPuff.StopAt"/> stop).</summary>
    public void Move(Vector3 position, float progress)
    {
        if (!Live) return;
        var now = Scene.Now;
        var s = _style;
        var travel = position - _last;
        if (travel.sqrMagnitude > 1e-6f) _direction = travel.normalized;
        var behind = position - _direction * s.HeadBack;
        var flicker = 1f - s.Flicker + s.Flicker * Mathf.Sin(now * 53f);
        if (_flame.Alive)
        {
            SpritePool.Move(_flame, behind, _direction);
            SpritePool.Resize(_flame, s.FlameSize, s.FlameAspect * (1f + s.FlameGrowth * progress) * flicker);
        }
        if (_glow.Alive)
        {
            SpritePool.Move(_glow, behind, Vector3.zero);
            SpritePool.Resize(_glow, s.GlowSize * (1f - s.Flicker * 0.5f + s.Flicker * flicker), 1f);
        }
        if (_ring != null && _line != null && !_line.WasCollected)
        {
            _ring.Sample(position, now);
            for (var i = 0; i < _ring.Count; i++) _line.SetPosition(i, _ring[i]);
        }
        if (_lastTime >= 0f)
        {
            var dt = Math.Min(now - _lastTime, 0.25f);
            for (var p = 0; p < s.Puffs.Length; p++)
            {
                var puff = s.Puffs[p];
                if (progress >= puff.StopAt) { _debts[p].Reset(); continue; }
                var count = _debts[p].Take(puff.PerSecond, dt);
                for (var c = 0; c < count; c++) Puff(puff, Vector3.Lerp(_last, position, SpritePool.Rnd()), progress);
            }
        }
        _last = position;
        _lastTime = now;
    }

    private static void Puff(TrailPuff puff, Vector3 at, float progress)
    {
        var size = puff.Size * (1f + puff.SizeGrowth * progress) * (1f + SpritePool.Range(-puff.SizeJitter, puff.SizeJitter));
        var life = puff.Life * (1f + SpritePool.Range(-puff.LifeJitter, puff.LifeJitter));
        var angle = SpritePool.Rnd() * 6.2831853f;
        var spread = puff.Spread * Mathf.Sqrt(SpritePool.Rnd());
        var j = puff.VelocityJitter;
        var position = at + puff.Offset + new Vector3(Mathf.Cos(angle) * spread, size * puff.LiftBySize, Mathf.Sin(angle) * spread);
        var velocity = puff.Velocity + new Vector3(SpritePool.Range(-j.x, j.x), SpritePool.Range(-j.y, j.y), SpritePool.Range(-j.z, j.z));
        if (puff.Channel != null)
        {
            puff.Channel.Emit(position, velocity, size, life, puff.Colour0, puff.Priority);
            return;
        }
        if (puff.Sheet == null) return;
        var spawn = SpriteSpawn.Of(puff.Sheet, SpriteFace.Camera, position, life, size, size * puff.SizeEnd, puff.Colour0, puff.Colour1);
        spawn.Velocity = velocity;
        spawn.Drag = puff.Drag;
        spawn.Flip = puff.Flip;
        spawn.Spin = SpritePool.Range(-puff.Spin, puff.Spin);
        spawn.Priority = puff.Priority;
        SpritePool.Emit(in spawn);
    }

    /// <summary>The journey ended (touchdown, or it left early): no more puffs, the head sprites and the ribbon go.</summary>
    public void End() => Trails.Return(this);

    internal void Stop()
    {
        Live = false;
        SpritePool.Kill(_flame);
        SpritePool.Kill(_glow);
        _flame = _glow = Sprite.None;
        if (_line != null && !_line.WasCollected) _line.gameObject.SetActive(false);
    }

    internal LineRenderer? Line => _line;
}

/// <summary>The pool of trails: trails of the same style are reused, ribbons keep their line renderer.</summary>
public static class Trails
{
    private static readonly List<Trail> Live = new();
    private static readonly List<Trail> Free = new();

    /// <summary>A trail of <paramref name="style"/> starting at <paramref name="start"/>; null (logged once) when the library's shaders
    /// are not available.</summary>
    public static Trail? Rent(Vector3 start, TrailStyle style)
    {
        if (style == null) throw new ArgumentNullException(nameof(style));
        Trail? trail = null;
        for (var i = Free.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Free[i].Style, style)) { trail = Free[i]; Free.RemoveAt(i); break; }
        trail ??= new Trail(style);
        var line = trail.Line;
        if (style.RibbonPoints >= 2 && (line == null || line.WasCollected))
        {
            var material = SpriteSheet.SoftLine.Material(style.RibbonBlend);
            if (material == null) return null;
            var o = new GameObject("ForgeVisual.Trail");
            Object.DontDestroyOnLoad(o);
            line = Beams.Line(o, "ribbon", material);
            line.positionCount = style.RibbonPoints;
            line.widthMultiplier = style.RibbonWidth;
            line.startColor = style.RibbonHead;
            line.endColor = style.RibbonTail;
        }
        trail.Begin(start, line);
        Live.Add(trail);
        return trail;
    }

    internal static void Return(Trail trail)
    {
        if (!trail.Live) return;
        trail.Stop();
        Live.Remove(trail);
        Free.Add(trail);
    }

    internal static void ClearAll()
    {
        for (var i = Live.Count - 1; i >= 0; i--) Return(Live[i]);
    }
}
