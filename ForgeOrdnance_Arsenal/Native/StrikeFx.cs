using System;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>
/// The strike effects themselves, one method per moment: what each draws is a set of sprites for ForgeVisual's shared pool
/// (<see cref="SpritePool"/>) and flat marks for its decal rings, with Arsenal's own sheets, colours, sizes and timings. Each states its
/// particle budget (live sprites at the peak, plus long-lived decals from their own rings). They only read the numbers they are
/// given, so every peer shows the same event from the same host data; the one thing that must agree between peers (where a cluster
/// bomblet's sparklets land) is decided by the caller from the call's seed. Purely visual and local.
/// </summary>
internal static class StrikeFx
{
    private const float Deg = 0.01745329f;
    /// <summary>The soft smoke sheet is faint by itself (its alpha peaks low); every smoke tint's alpha is multiplied by this and
    /// the blend clamps it at 1.</summary>
    private const float SmokeGain = 2.4f;
    private const int ScorchCount = 6, MarkCount = 48;

    private static readonly Color Smoke0 = new(0.74f, 0.72f, 0.7f, 0.7f), Smoke1 = new(0.78f, 0.77f, 0.75f, 0f);
    private static readonly Color Soot0 = new(0.52f, 0.5f, 0.48f, 0.85f), Soot1 = new(0.66f, 0.64f, 0.6f, 0f);
    private static readonly Color Dust0 = new(0.7f, 0.62f, 0.5f, 0.75f), Dust1 = new(0.72f, 0.65f, 0.54f, 0f);
    private static readonly Color Hot0 = new(1f, 0.85f, 0.55f, 1f), Hot1 = new(1f, 0.4f, 0.08f, 0f);
    private static readonly Color Scorch0 = new(0.035f, 0.03f, 0.028f, 0.82f), Scorch1 = new(0.05f, 0.045f, 0.04f, 0f);
    private static readonly Color White = new(1f, 1f, 1f, 1f);
    private static readonly SpriteEnd PopAt = Pop;

    private static ArsenalSheets _sheets = null!;
    private static DecalRing? _scorches, _marks;

    internal static bool Ready => _sheets != null;

    internal static void Init(ArsenalSheets sheets) => _sheets = sheets;

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static float Rnd() => SpritePool.Rnd();

    private static float Range(float low, float high) => SpritePool.Range(low, high);

    private static Vector3 Radial(float angle) => new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

    private static SpriteSpawn Quad(SpriteSheet sheet, SpriteFace face, Vector3 pos, Vector3 vel, float life, float size0, float size1, Color c0, Color c1,
        float delay = 0f, float drag = 0f, float gravity = 0f, float spin = 0f)
    {
        var s = SpriteSpawn.Of(sheet, face, pos, life, size0, size1, c0, c1);
        s.Velocity = vel;
        s.Delay = delay;
        s.Drag = drag;
        s.Gravity = gravity;
        s.Spin = spin;
        return s;
    }

    /// <summary>A smoke puff, its tints brightened by <see cref="SmokeGain"/>, playing the whole flipbook over its life.</summary>
    private static void Smoke(Vector3 pos, Vector3 vel, float life, float size0, float size1, Color c0, Color c1, float delay = 0f, float drag = 0f, float spin = 0f)
    {
        c0.a *= SmokeGain;
        c1.a *= SmokeGain;
        var s = Quad(_sheets.Smoke, SpriteFace.Camera, pos, vel, life, size0, size1, c0, c1, delay, drag, 0f, spin);
        s.Flip = true;
        SpritePool.Emit(in s);
    }

    /// <summary>A flipbook sprite playing the first <paramref name="frames"/> frames (0 = all) over its life.</summary>
    private static void Flipbook(SpriteSheet sheet, Vector3 pos, Vector3 vel, float life, float size0, float size1, Color c0, Color c1,
        float delay = 0f, float drag = 0f, float spin = 0f, int frames = 0)
    {
        var s = Quad(sheet, SpriteFace.Camera, pos, vel, life, size0, size1, c0, c1, delay, drag, 0f, spin);
        s.Flip = true;
        s.FlipFrames = frames;
        SpritePool.Emit(in s);
    }

    private static void Ring(Vector3 pos, Vector3 normal, float life, float size0, float size1, Color c0, Color c1)
    {
        var s = Quad(_sheets.Ring, SpriteFace.Floor, pos, Vector3.zero, life, size0, size1, c0, c1);
        s.Normal = normal;
        s.FadeIn = 0.02f;
        SpritePool.Emit(in s);
    }

    private static void Flash(Vector3 at, Color colour, float range, float peak, float life)
    {
        if (Plugin.ExtraLights.Value) PointLights.Flash(at, new PointLightSpec(colour, range, peak, life, 0f), Time.time);
    }

    /// <summary>A long-lived flat mark from the ring of scorches or of beam burns (the oldest of its own ring is replaced, so
    /// marks never starve the transient sprites and never grow).</summary>
    private static void Decal(bool beamMark, Vector3 pos, Vector3 normal, float life, float size, Color c0, Color c1, float hold)
    {
        var ring = beamMark ? _marks ??= SpritePool.CreateDecals(MarkCount) : _scorches ??= SpritePool.CreateDecals(ScorchCount);
        if (ring == null) return;
        var s = SpriteSpawn.Of(_sheets.Scorch, SpriteFace.Floor, pos, life, size, size, c0, c1);
        s.Normal = normal;
        s.Hold = hold;
        s.FadeIn = 0.15f;
        ring.Place(in s);
    }

    /// <summary>Makes the decal rings now (level load) instead of in the first strike's frame.</summary>
    internal static void Prepare()
    {
        _scorches ??= SpritePool.CreateDecals(ScorchCount);
        _marks ??= SpritePool.CreateDecals(MarkCount);
    }

    /// <summary>Draws one barely visible, tiny sprite of every sheet at <paramref name="at"/> (in front of the camera) for a moment, so
    /// the first real strike does not pay for the GPU's first use of each material in its landing frame.</summary>
    internal static void Warm(Vector3 at) => SpritePool.Warm(at, _sheets.All);

    /// <summary>Forgets the decal rings (ForgeVisual rebuilt its pool); the next mark makes new ones.</summary>
    internal static void Reset() => _scorches = _marks = null;

    private static Vector3 Settle(Vector3 at, out Vector3 normal) => SpritePool.Settle(at, out normal);

    // ---- shell falling: smoke trail and tail flame ---------------------------------------------------------------------

    /// <summary>The look of a falling shell's trail at <paramref name="scale"/>: a looping flame flipbook, base at the shell, tip
    /// trailing up the way it came, a glow, and a grey smoke band (a bomblet's band is a quarter as dense: a cluster strike has ten
    /// falling at once).</summary>
    internal static TrailStyle ShellTrail(float scale) => new()
    {
        Flame = _sheets.Flame, FlameFps = 30f, FlameSize = 0.55f * scale, FlameAspect = 2.2f, FlameGrowth = 1.3f, FlameColour = new Color(1f, 0.85f, 0.6f, 1f),
        Glow = _sheets.Glow, GlowSize = 0.9f * scale, GlowColour = new Color(1f, 0.6f, 0.25f, 0.7f),
        HeadBack = 0.25f * scale, Flicker = 0.15f,
        Puffs = new[]
        {
            new TrailPuff
            {
                Sheet = _sheets.Smoke, PerSecond = scale < 0.6f ? 1f / 0.28f : 1f / 0.07f, Offset = Vector3.up * (0.95f * scale),
                Velocity = new Vector3(0f, 0.3f, 0f), VelocityJitter = new Vector3(0.25f, 0.2f, 0.25f),
                Size = 0.75f * scale, SizeEnd = 1.9f / 0.75f, Life = 1.25f, LifeJitter = 0.12f,
                Colour0 = new Color(Smoke0.r, Smoke0.g, Smoke0.b, Smoke0.a * SmokeGain), Colour1 = new Color(Smoke1.r, Smoke1.g, Smoke1.b, 0f),
                Drag = 1.2f, Flip = true, Spin = 25f * Deg,
            },
        },
    };

    // ---- big blast: shockwave, fireball, smoke column, scorch ----------------------------------------------------------

    /// <summary>The big shell's explosion: a white shockwave ring running out over the floor, a flash, a flipbook explosion with
    /// a fireball inside it, a column of smoke that climbs to about 8 m and mushrooms out at the top, a dust ring and a scorch
    /// mark that stays. Peak about 1 flash + 1 explosion + 1 fireball + 1 ring + 8 dust + 14 column = 26 sprites for 4 s, plus 1
    /// scorch of its ring of 6 (stays 24 s).</summary>
    internal static void BigBlast(Vector3 at, float radius)
    {
        var ground = Settle(at, out var normal);
        var s = Mathf.Clamp(radius, 2f, 9f);
        Flash(at + Vector3.up * 1.2f, new Color(1f, 0.7f, 0.35f), s * 2.6f, 7f, 0.5f);
        Flipbook(_sheets.Flash, ground + Vector3.up * s * 0.35f, Vector3.zero, 0.4f, s * 1.6f, s * 3.4f, White, new Color(1f, 0.8f, 0.5f, 0f));
        // The explosion flipbook (fireball into smoke) and, over its first second, a hotter fireball inside it.
        Flipbook(_sheets.Blast, ground + Vector3.up * s * 0.9f, Vector3.up * 0.4f, 0.9f, s * 2.6f, s * 3.2f, White, new Color(1f, 0.75f, 0.5f, 0f), 0f, 0.5f, Range(-6f, 6f) * Deg, 7);
        Flipbook(_sheets.Fireball, ground + Vector3.up * s * 0.55f, Vector3.up * 0.8f, 1.1f, s * 1.5f, s * 2.6f, new Color(1f, 0.85f, 0.6f, 1f), new Color(1f, 0.55f, 0.25f, 0f), 0f, 0.8f, Range(-20f, 20f) * Deg);
        Ring(ground, normal, 0.6f, 0.8f, s * 4.4f, new Color(1f, 0.97f, 0.9f, 0.95f), new Color(1f, 0.93f, 0.85f, 0f));
        for (var i = 0; i < 8; i++)
        {
            var a = i * 0.7854f + Range(-0.2f, 0.2f);
            Smoke(ground + Radial(a) * (0.35f * s) + Vector3.up * 0.5f, Radial(a) * (s * Range(1.4f, 2.0f)) + Vector3.up * 0.4f, Range(1.2f, 1.7f),
                0.8f * s, 1.9f * s, Dust0, Dust1, 0f, 1.9f, Range(-20f, 20f) * Deg);
        }
        const int puffs = 14;
        for (var i = 0; i < puffs; i++)
        {
            var f = i / (float)(puffs - 1);
            var height = Mathf.Lerp(1.8f, 8f, f);
            var vel = Vector3.up * (height * 0.9f);
            // The top of the column mushrooms: its last puffs also push outward.
            if (f > 0.6f) vel += Radial(Rnd() * 6.2832f) * ((f - 0.55f) * 6.5f);
            Smoke(ground + Radial(Rnd() * 6.2832f) * Range(0f, 0.4f) + Vector3.up * 0.4f, vel, Range(3.4f, 4.2f),
                s * 0.65f, s * (1.15f + 0.9f * f), Soot0, Soot1, 0.3f + 0.04f * i, 0.9f, Range(-12f, 12f) * Deg);
        }
        Decal(false, ground, normal, 24f, s * 1.7f, Scorch0, Scorch1, 0.65f);
    }

    // ---- small blast: dust ring and sparks -----------------------------------------------------------------------------

    /// <summary>The small shell's explosion: a fireball flipbook (no smoke frames), a soft dust ring, a low dust ring, a short burst of
    /// sparks, a faint scorch. Peak 1 explosion + 1 flash + 8 dust + 8 sparks + 2 smoke = 20 sprites for 1.5 s, plus 1 scorch.
    /// A cluster bomblet's blast (<paramref name="cluster"/>, ten of them within a second or two) is the fireball, 2 dust and 2 sparks:
    /// 5 sprites, no flash light and no scorch (the strike has one of each at its centre, <see cref="ClusterCentre"/>).</summary>
    internal static void SmallBlast(Vector3 at, float radius, bool cluster = false)
    {
        var ground = Settle(at, out var normal);
        var s = Mathf.Clamp(radius, 1.5f, 6f) / 4.5f;
        var dust = cluster ? 2 : 8;
        var sparks = cluster ? 2 : 8;
        var soot = cluster ? 0 : 2;
        if (!cluster) Flash(at + Vector3.up * 0.8f, new Color(1f, 0.72f, 0.4f), 6f * s, 5f, 0.35f);
        if (!cluster) Flipbook(_sheets.Flash, ground + Vector3.up * 0.7f * s, Vector3.zero, 0.25f, 1.2f * s, 3f * s, White, new Color(1f, 0.8f, 0.5f, 0f));
        Flipbook(_sheets.Fireball, ground + Vector3.up * 1.2f * s, Vector3.up * 0.5f, 0.8f, 2.6f * s, 4.2f * s, new Color(1f, 0.85f, 0.6f, 1f), new Color(1f, 0.5f, 0.2f, 0f), 0f, 0.5f, Range(-30f, 30f) * Deg);
        for (var i = 0; i < dust; i++)
        {
            var a = i * 6.2832f / dust + Range(-0.25f, 0.25f);
            Smoke(ground + Radial(a) * (0.3f * s) + Vector3.up * 0.25f, Radial(a) * (3.4f * s * Range(0.8f, 1.2f)), Range(0.9f, 1.2f),
                0.5f * s, 1.5f * s, Dust0, Dust1, 0f, 2.3f, Range(-20f, 20f) * Deg);
        }
        for (var i = 0; i < sparks; i++)
        {
            var a = Rnd() * 6.2832f;
            Spark(ground + Vector3.up * 0.15f, Radial(a) * Range(3f, 7f) * s + Vector3.up * Range(2.5f, 6f), Range(0.35f, 0.6f));
        }
        for (var i = 0; i < soot; i++)
            Smoke(ground + Vector3.up * 0.4f, new Vector3(Range(-0.4f, 0.4f), Range(0.8f, 1.6f), Range(-0.4f, 0.4f)), 1.3f,
                0.6f * s, 1.7f * s, Soot0, Soot1, 0.3f, 1.1f);
        if (!cluster) Decal(false, ground, normal, 12f, 1.9f * s, Scorch0, Scorch1, 0.6f);
    }

    /// <summary>The one light flash and one scorch of a whole cluster strike, at its centre: a short warm flash and a mark of the
    /// strike's size that stays 12 s.</summary>
    internal static void ClusterCentre(Vector3 at, float radius)
    {
        var ground = Settle(at, out var normal);
        Flash(at + Vector3.up * 1.2f, new Color(1f, 0.72f, 0.4f), 9f, 4f, 0.4f);
        Decal(false, ground, normal, 12f, Mathf.Clamp(radius, 1.5f, 6f) * 1.6f, Scorch0, Scorch1, 0.6f);
    }

    /// <summary>One glowing ember that arcs and fades.</summary>
    private static void Spark(Vector3 pos, Vector3 vel, float life)
    {
        var s = Quad(_sheets.Streak, SpriteFace.Streak, pos, vel, life, 0.08f, 0.04f, Hot0, Hot1, 0f, 0.4f, 9.8f);
        s.Stretch = 0.05f;
        SpritePool.Emit(in s);
    }

    /// <summary>The end of a sparklet: a small burst of fire. 1 sprite for 0.5 s.</summary>
    private static void Pop(Vector3 at, float scale)
    {
        Flipbook(_sheets.Fireball, at + Vector3.up * 0.45f * scale, new Vector3(Range(-0.3f, 0.3f), Range(0.5f, 1.2f), Range(-0.3f, 0.3f)), 0.5f,
            1.0f * scale, 1.7f * scale, new Color(1f, 0.85f, 0.6f, 1f), new Color(1f, 0.5f, 0.2f, 0f), 0f, 1.5f);
    }

    // ---- cluster airburst ----------------------------------------------------------------------------------------------

    /// <summary>The bomblet splits in the air: <paramref name="count"/> sparklets streak from the burst point to the points
    /// <paramref name="offsets"/> (metres east/north of <paramref name="impact"/>, pairs, the same on every peer: derived from the
    /// call's seed) and pop there as they arrive, <paramref name="flight"/> seconds later. Per sparklet 1 streak + 1 at its pop = 2;
    /// plus 1 burst flash and 1 puff: 8 for 3 sparklets, and no light.</summary>
    internal static void ClusterBurst(Vector3 burst, Vector3 impact, float[] offsets, int count, float flight, float scale)
    {
        var f = Math.Max(0.06f, flight);
        Flipbook(_sheets.Flash, burst, Vector3.zero, 0.3f, 0.8f * scale, 2.6f * scale, White, new Color(1f, 0.8f, 0.5f, 0f));
        Smoke(burst, new Vector3(0f, 0.3f, 0f), 1f, 0.7f * scale, 1.9f * scale, Smoke0, Smoke1, 0f, 1.2f);
        for (var i = 0; i < count; i++)
        {
            var target = Settle(impact + new Vector3(offsets[2 * i], 0f, offsets[2 * i + 1]), out _);
            var velocity = (target - burst) * (1f / f);
            var sparklet = Quad(_sheets.Streak, SpriteFace.Streak, burst, velocity, f, 0.13f * scale, 0.1f * scale, Hot0, new Color(1f, 0.5f, 0.12f, 0.7f));
            sparklet.Stretch = 0.045f;
            sparklet.OnEnd = PopAt;
            sparklet.EndScale = scale;
            sparklet.FadeIn = 0f;
            SpritePool.Emit(in sparklet);
        }
    }

    // ---- incendiary landing --------------------------------------------------------------------------------------------

    /// <summary>The fire shell bursts into a fireball that splashes burning tongues over the floor out to <paramref name="reach"/>
    /// (the burn area's edge) and hands over to the area's flames. Peak 3 fireball + 1 flash + 10 flung fire + 8 tongues +
    /// 7 smoke = 29 sprites for 2 s, plus 1 scorch.</summary>
    internal static void FireSplash(Vector3 at, float reach)
    {
        var ground = Settle(at, out var normal);
        var r = Mathf.Clamp(reach, 2f, 10f);
        Flash(at + Vector3.up * 0.8f, new Color(1f, 0.45f, 0.1f), r * 1.5f, 6f, 0.6f);
        Flipbook(_sheets.Flash, ground + Vector3.up * 0.6f, Vector3.zero, 0.4f, 1f, r * 0.9f, new Color(1f, 0.8f, 0.5f, 1f), new Color(1f, 0.5f, 0.2f, 0f));
        for (var i = 0; i < 3; i++)
            Flipbook(_sheets.Fireball, ground + new Vector3(Range(-0.4f, 0.4f), Range(0.5f, 0.9f), Range(-0.4f, 0.4f)), Vector3.up * Range(1.5f, 3f), Range(0.9f, 1.2f),
                1.2f, Range(3.2f, 4.2f), new Color(1f, 0.8f, 0.55f, 1f), new Color(1f, 0.5f, 0.2f, 0f), 0f, 1.4f, Range(-40f, 40f) * Deg);
        var drag = 2f;
        for (var i = 0; i < 10; i++)
        {
            var a = i * 0.6283f + Range(-0.2f, 0.2f);
            var speed = drag * r * Range(0.55f, 1f) * 1.15f;
            Flipbook(_sheets.Fireball, ground + Radial(a) * 0.4f + Vector3.up * 0.3f, Radial(a) * speed + Vector3.up * Range(0.5f, 1.5f), Range(1.5f, 2f),
                0.9f, Range(1.8f, 2.6f), new Color(1f, 0.7f, 0.4f, 0.9f), new Color(0.9f, 0.35f, 0.1f, 0f), 0f, drag, Range(-30f, 30f) * Deg);
        }
        for (var i = 0; i < 8; i++)
        {
            var a = i * 0.7854f + Range(-0.2f, 0.2f);
            var speed = drag * r * Range(0.7f, 1f) * 1.15f;
            var tongue = Quad(_sheets.Streak, SpriteFace.Streak, ground + Radial(a) * 0.5f + Vector3.up * 0.15f, Radial(a) * speed, 1.3f, 0.5f, 0.3f,
                new Color(1f, 0.7f, 0.25f, 0.95f), new Color(1f, 0.35f, 0.06f, 0f), 0f, drag);
            tongue.Stretch = 0.3f;
            tongue.FadeIn = 0.02f;
            SpritePool.Emit(in tongue);
        }
        for (var i = 0; i < 7; i++)
            Smoke(ground + Radial(Rnd() * 6.2832f) * Range(0f, 0.5f) + Vector3.up * 0.6f, new Vector3(Range(-0.5f, 0.5f), Range(1.2f, 2.4f), Range(-0.5f, 0.5f)),
                Range(1.6f, 2f), 1.1f, Range(2.6f, 3.4f), new Color(0.45f, 0.43f, 0.42f, 0.75f), new Color(0.6f, 0.58f, 0.56f, 0f), 0.05f * i, 1f);
        Decal(false, ground, normal, 14f, r * 1.2f, Scorch0, Scorch1, 0.6f);
    }

    // ---- gas release ---------------------------------------------------------------------------------------------------

    /// <summary>The canister lands and vents: a ring of fast green fog that settles into the cloud. 15 puffs + 1 ring = 16
    /// sprites for 1 s.</summary>
    internal static void GasBurst(Vector3 at)
    {
        var ground = Settle(at, out var normal);
        Flash(at + Vector3.up * 0.5f, new Color(0.5f, 1f, 0.3f), 5f, 3f, 0.3f);
        Ring(ground, normal, 0.4f, 0.6f, 4.2f, new Color(0.55f, 1f, 0.35f, 0.75f), new Color(0.5f, 0.95f, 0.3f, 0f));
        for (var i = 0; i < 15; i++)
        {
            var a = i * 0.41888f + Range(-0.12f, 0.12f);
            Smoke(ground + Radial(a) * 0.3f + Vector3.up * Range(0.2f, 0.5f), Radial(a) * Range(6f, 9f), Range(0.9f, 1.1f),
                0.8f, Range(2.2f, 2.8f), new Color(0.52f, 0.95f, 0.28f, 0.75f), new Color(0.38f, 0.72f, 0.2f, 0f), 0f, 3.3f, Range(-30f, 30f) * Deg);
        }
    }

    // ---- stasis field deployment ---------------------------------------------------------------------------------------

    /// <summary>The emitter lands and a blue shockwave sweeps the whole field to its edge, leaving mist that settles into the
    /// steady fog. 1 ring + 12 puffs = 13 sprites for 1.5 s.</summary>
    internal static void StasisPulse(Vector3 centre, float radius)
    {
        var ground = Settle(centre, out var normal);
        var r = Mathf.Clamp(radius, 2f, 14f);
        Flash(centre + Vector3.up * 0.6f, new Color(0.35f, 0.7f, 1f), r * 1.4f, 4f, 0.7f);
        Ring(ground, normal, 1.05f, 0.8f, r * 2.1f, new Color(0.7f, 0.92f, 1f, 0.9f), new Color(0.45f, 0.75f, 1f, 0f));
        for (var i = 0; i < 12; i++)
        {
            var a = i * 0.5236f + Range(-0.15f, 0.15f);
            Smoke(ground + Radial(a) * 0.5f + Vector3.up * Range(0.3f, 0.9f), Radial(a) * (r * Range(1.05f, 1.2f)), Range(1.3f, 1.6f),
                1.2f, 0.28f * r, new Color(0.55f, 0.82f, 1f, 0.45f), new Color(0.5f, 0.8f, 1f, 0f), 0f, 1f, Range(-15f, 15f) * Deg);
        }
    }

    // ---- cutting beam: charge and burn marks ---------------------------------------------------------------------------

    /// <summary>The beam gathers light where it will come from: a glow at the ceiling swelling for half a second. 2 sprites.</summary>
    internal static void BeamCharge(Vector3 at, Color colour)
    {
        var bright = new Color(Mathf.Lerp(colour.r, 1f, 0.6f), Mathf.Lerp(colour.g, 1f, 0.6f), Mathf.Lerp(colour.b, 1f, 0.6f), 0f);
        // The glow grows over the whole charge (StrikeTiming.BeamChargeSeconds), up to the moment the beam drops.
        var charge = StrikeTiming.BeamChargeSeconds;
        var glow = Quad(_sheets.Glow, SpriteFace.Camera, at, Vector3.zero, charge, 0.2f, 2.6f, new Color(bright.r, bright.g, bright.b, 0.95f), new Color(colour.r, colour.g, colour.b, 0.4f));
        SpritePool.Emit(in glow);
        var flare = Quad(_sheets.Flare, SpriteFace.Camera, at, Vector3.zero, charge, 0.1f, 1.4f, new Color(1f, 1f, 1f, 0.9f), new Color(bright.r, bright.g, bright.b, 0.3f),
            0f, 0f, 0f, 3f * 0.5f / charge);
        SpritePool.Emit(in flare);
        Flash(at + Vector3.down * 0.4f, colour, 9f, 3f, charge);
    }

    /// <summary>One burn mark on the floor where the beam stood; the line it draws over its route fades from glowing to dark in
    /// 4.5 s. The marks come from a ring of 48, the oldest replaced, so at most 24 per beam stay while it walks.</summary>
    internal static void BeamMark(Vector3 at, Color colour)
    {
        var ground = Settle(at, out var normal);
        var hot = new Color(Mathf.Lerp(colour.r, 1f, 0.35f), Mathf.Lerp(colour.g, 0.6f, 0.5f), Mathf.Lerp(colour.b, 0.25f, 0.5f), 0.8f);
        Decal(true, ground, normal, 4.5f, Range(1.1f, 1.5f), hot, new Color(0.03f, 0.028f, 0.026f, 0f), 0.2f);
    }

    // ---- heavy weapon return -------------------------------------------------------------------------------------------

    /// <summary>A small blue-white shimmer at the gun's place in the hand as it is taken back. 1 sprite for 0.4 s, kept small
    /// and thin so it never covers the view.</summary>
    internal static void ReturnGlow(Vector3 at)
    {
        var s = Quad(_sheets.Glow, SpriteFace.Camera, at, Vector3.zero, 0.4f, 0.12f, 0.5f, new Color(0.75f, 0.9f, 1f, 0.55f), new Color(0.55f, 0.8f, 1f, 0f));
        SpritePool.Emit(in s);
    }
}
