using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>
/// The strike effects themselves, one method per moment: the library effect of the moment's slot (<see cref="ArsenalFxSlots"/>; a slot the
/// table does not list logs its error and draws nothing, no moment has a second look) and what the library does not draw: the warnings, light
/// flashes, reach rings, scorch marks and a bomblet's small blast, as sprites for ForgeVisual's shared pool (<see cref="SpritePool"/>) and flat
/// marks for its decal rings, with Arsenal's own sheets. They only read the numbers they are given, so every peer shows the same event from
/// the same host data. Purely visual and local.
/// </summary>
internal static class StrikeFx
{
    private const float Deg = 0.01745329f;
    /// <summary>The soft smoke sheet is faint by itself (its alpha peaks low); every smoke tint's alpha is multiplied by this and
    /// the blend clamps it at 1.</summary>
    private const float SmokeGain = 2.4f;
    private const int MarkCount = 48;
    /// <summary>Where the ring sheet's bright band lies, as a share of the sprite's half width (tools/make_fx_textures.py): a ring of radius R is a sprite 2R / this across.</summary>
    private const float RingRim = 0.84f;

    private static readonly Color Dust0 = new(0.7f, 0.62f, 0.5f, 0.75f), Dust1 = new(0.72f, 0.65f, 0.54f, 0f);
    private static readonly Color Hot0 = new(1f, 0.85f, 0.55f, 1f), Hot1 = new(1f, 0.4f, 0.08f, 0f);
    private static readonly Color Scorch0 = new(0.035f, 0.03f, 0.028f, 0.82f), Scorch1 = new(0.05f, 0.045f, 0.04f, 0f);
    private static readonly Color White = new(1f, 1f, 1f, 1f);

    private static ArsenalSheets _sheets = null!;
    private static DecalRing? _marks;
    private const float ShakeRange = 35f;
    /// <summary>The camera shake of a landing, by weight.</summary>
    private static readonly ShakeSpec BigShake = new(0.6f, ShakeRange, 0.6f, 20f), ShellShake = new(0.35f, ShakeRange, 0.6f, 20f), ClusterShake = new(0.2f, ShakeRange, 0.6f, 20f);
    /// <summary>The landings ForgeVisual's <see cref="Impacts"/> plays whole (the library effect, the light, the scorch on the shared marks and the camera shake), one look per kind.</summary>
    private static ImpactLook? _big, _small, _fire, _gas;

    internal static bool Ready => _sheets != null;

    /// <summary>A straight line of light as one sprite that lives <paramref name="life"/> seconds: from <paramref name="from"/> to
    /// <paramref name="to"/>, <paramref name="width"/> metres wide, turned toward the camera, keeping <paramref name="colour0"/> for the first
    /// <paramref name="hold"/> of its life and then fading to <paramref name="colour1"/>.</summary>
    private static void Line(SpriteSheet sheet, Vector3 from, Vector3 to, float width, float life, Color colour0, Color colour1, float hold = 0f)
    {
        var along = to - from;
        var length = along.magnitude;
        if (length < 1e-3f || width <= 0f) return;
        var s = SpriteSpawn.Of(sheet, SpriteFace.Streak, to, life, width, width, colour0, colour1);
        // A speed far too small to see only gives the streak its direction.
        s.Velocity = along * (0.01f / length);
        s.Aspect = length / width;
        s.Hold = hold;
        s.FadeIn = 0.03f;
        SpritePool.Emit(in s);
    }

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
        PointLights.Flash(at, new PointLightSpec(colour, range, peak, life, 0f), Time.time);
    }

    /// <summary>The look of one kind of landing, made again only when its library effect changed: <paramref name="fx"/> at <paramref name="fxSize"/> metres times the play's scale, the light a flash <paramref name="lift"/> metres up,
    /// a scorch of <paramref name="decalSize"/> metres times the scale that stays <paramref name="decalLife"/> s (full for its first <paramref name="decalHold"/> share), and the shake.</summary>
    private static ImpactLook Look(ref ImpactLook? look, FxHandle? fx, float fxSize, in PointLightSpec light, float lift, ShakeSpec shake, float decalSize = 0f, float decalLife = 0f, float decalHold = 0f)
    {
        if (look != null && ReferenceEquals(look.Fx, fx)) return look;
        return look = new ImpactLook
        {
            Fx = fx,
            FxSize = fxSize,
            Light = light,
            LightLift = lift,
            Shake = shake,
            Decal = decalSize > 0f ? _sheets.Scorch : null,
            DecalSize = decalSize,
            DecalLife = decalLife,
            DecalFade = decalLife * (1f - decalHold),
            DecalColour = Scorch0,
        };
    }

    /// <summary>A long-lived flat mark from the ring of beam burns or, with <paramref name="beamMark"/> false, the shared marks (the oldest of its ring is replaced, so
    /// marks never starve the transient sprites and never grow).</summary>
    private static void Decal(bool beamMark, Vector3 pos, Vector3 normal, float life, float size, Color c0, Color c1, float hold)
    {
        var ring = beamMark ? Rings(ref _marks, MarkCount) : ForgeVisual.Decals.Shared;
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
        Rings(ref _marks, MarkCount);
    }

    /// <summary>The ring kept in <paramref name="ring"/>, made once and kept across levels (ForgeVisual's pool keeps it and clears its marks
    /// with the level); made again only when the pool was rebuilt and its quads are gone.</summary>
    private static DecalRing? Rings(ref DecalRing? ring, int capacity)
        => ring is { Alive: true } ? ring : ring = SpritePool.CreateDecals(capacity);

    /// <summary>Draws one barely visible, tiny sprite of every sheet at <paramref name="at"/> (in front of the camera) for a moment, so
    /// the first real strike does not pay for the GPU's first use of each material in its landing frame.</summary>
    internal static void Warm(Vector3 at) => SpritePool.Warm(at, _sheets.All);

    private static Vector3 Settle(Vector3 at, out Vector3 normal) => SpritePool.Settle(at, out normal);

    // ---- warning: where it lands ---------------------------------------------------------------------------------------

    /// <summary>The colour a kind's warning and dropped tracer are drawn in.</summary>
    internal static Color WarnColour(StrikeKind kind) => kind switch
    {
        StrikeKind.Gas => new Color(0.55f, 1f, 0.3f, 1f),
        StrikeKind.Stasis => new Color(0.4f, 0.8f, 1f, 1f),
        StrikeKind.Incendiary => new Color(1f, 0.45f, 0.1f, 1f),
        StrikeKind.Precision => new Color(1f, 0.16f, 0.1f, 1f),
        _ => new Color(1f, 0.6f, 0.15f, 1f),
    };

    /// <summary>A shell's warning for <paramref name="seconds"/>: a ring standing at exactly the shell's reach and a glow filling it from the middle, so the rim says how far and the fill says
    /// how soon; a single shell (not a barrage) also a line of light along the way it will come. An incendiary shell has no fill: an orange glow on the floor reads as the fire itself
    /// before the shell has landed. 2 sprites, 3 with the line.</summary>
    internal static void Warn(StrikeKind kind, Vector3 at, Vector3 from, float radius, float seconds)
    {
        var ground = Settle(at, out var normal);
        var colour = WarnColour(kind);
        var fade = new Color(colour.r, colour.g, colour.b, 0f);
        var across = Mathf.Max(radius, 0.5f) * 2f / RingRim;
        var rim = Quad(_sheets.Ring, SpriteFace.Floor, ground + normal * 0.05f, Vector3.zero, seconds, across, across, colour, fade);
        rim.Normal = normal;
        rim.Hold = 0.9f;
        rim.FadeIn = 0.04f;
        SpritePool.Emit(in rim);
        if (kind != StrikeKind.Incendiary)
        {
            var fill = Quad(_sheets.Glow, SpriteFace.Floor, ground + normal * 0.07f, Vector3.zero, seconds, across * 0.1f, across * 0.95f, new Color(colour.r, colour.g, colour.b, 0.55f), new Color(colour.r, colour.g, colour.b, 0.2f));
            fill.Normal = normal;
            SpritePool.Emit(in fill);
        }
        if (kind is StrikeKind.Area or StrikeKind.Creeping or StrikeKind.Cluster) return;
        Line(_sheets.Streak, ground, from, 0.14f, seconds, new Color(colour.r, colour.g, colour.b, 0.8f), fade, 0.85f);
    }

    // ---- shell falling --------------------------------------------------------------------------------------------------

    /// <summary>A dropped canister's or emitter's trail: only a tracer and a glow in <paramref name="colour"/>.</summary>
    internal static TrailStyle DropTrail(Color colour) => new()
    {
        Glow = _sheets.Glow,
        GlowSize = 0.9f,
        GlowColour = new Color(colour.r, colour.g, colour.b, 0.7f),
        HeadBack = 0.1f,
        Flicker = 0.1f,
        RibbonPoints = 9,
        RibbonWidth = 0.22f,
        RibbonHead = new Color(colour.r, colour.g, colour.b, 1f),
        RibbonTail = new Color(colour.r, colour.g, colour.b, 0f),
        RibbonBlend = FxBlend.Additive,
    };

    // ---- big blast -------------------------------------------------------------------------------------------------------

    /// <summary>The blow of a big shell: a column of white light up the way it came, gone in a quarter second. 2 sprites.</summary>
    internal static void ImpactColumn(Vector3 at, Vector3 from, float radius)
    {
        var ground = Settle(at, out _);
        var width = Mathf.Clamp(radius, 2f, 9f) * 0.35f;
        var up = from;
        Line(_sheets.Streak, ground, up, width, 0.28f, new Color(1f, 0.92f, 0.75f, 1f), new Color(1f, 0.5f, 0.2f, 0f));
        Line(_sheets.Streak, ground, up, width * 0.3f, 0.16f, White, new Color(1f, 1f, 1f, 0f));
    }

    /// <summary>The big shell's explosion: a light flash, the library's blast (its fireball about four fifths of the blast's width; a barrage shell's is two thirds: the heavy shell must not
    /// look the smaller), a ring running out to exactly its reach and a scorch mark that stays 24 s (1 of a ring of 6).</summary>
    internal static void BigBlast(Vector3 at, float radius)
    {
        var ground = Settle(at, out var normal);
        var s = Mathf.Clamp(radius, 2f, 9f);
        // The light reaches 13 m at a 5 m blast and shines the stronger the wider the blast.
        Impacts.Play(Look(ref _big, ArsenalFxSlots.BigBlast, 1.6f, new PointLightSpec(new Color(1f, 0.7f, 0.35f), 13f, 7f, 0.5f, 0f), 1.2f, BigShake, 1.7f, 24f, 0.65f), ground, normal, s, Mathf.Clamp(s / 5f, 0.5f, 1.8f));
        // Only the effect's size is held to what the library draws well; the ring is the real reach, so a larger authored radius shows where it hurts.
        ReachRing(ground, normal, radius);
    }

    /// <summary>The blast's edge: a thin ring running out to exactly <paramref name="radius"/> and fading there. 1 sprite for 0.45 s.</summary>
    private static void ReachRing(Vector3 ground, Vector3 normal, float radius)
        => Ring(ground + normal * 0.05f, normal, 0.45f, 1f, radius * 2f / RingRim, new Color(1f, 0.92f, 0.78f, 0.75f), new Color(1f, 0.85f, 0.65f, 0f));

    // ---- small blast -----------------------------------------------------------------------------------------------------

    /// <summary>A barrage shell's explosion: a light flash, the library's blast, a ring to its reach and a scorch that stays 12 s.</summary>
    internal static void SmallBlast(Vector3 at, float radius)
    {
        var ground = Settle(at, out var normal);
        var s = Mathf.Clamp(radius, 1.5f, 6f) / 4.5f * 1.35f;
        Impacts.Play(Look(ref _small, ArsenalFxSlots.SmallBlast, 4.5f, new PointLightSpec(new Color(1f, 0.72f, 0.4f), 6f, 5f, 0.35f, 0f), 0.8f, ShellShake, 1.9f, 12f, 0.6f), ground, normal, s);
        ReachRing(ground, normal, radius);
    }

    /// <summary>A cluster bomblet's blast (ten of them within a second or two; the library plays once for the strike, <see cref="ClusterCentre"/>): a small ring, a fireball, 2 dust and 3 sparks.
    /// 7 sprites, no flash light and no scorch.</summary>
    internal static void BombletBlast(Vector3 at, float radius)
    {
        var ground = Settle(at, out var normal);
        var s = Mathf.Clamp(radius, 1.5f, 6f) / 4.5f;
        CameraShake.Shake(ground, ClusterShake);
        Ring(ground, normal, 0.3f, 0.6f, 3.5f * s, new Color(1f, 0.95f, 0.85f, 0.9f), new Color(1f, 0.9f, 0.8f, 0f));
        Flipbook(_sheets.Fireball, ground + Vector3.up * 1.2f * s, Vector3.up * 0.5f, 0.8f, 2.6f * s, 4.2f * s, new Color(1f, 0.85f, 0.6f, 1f), new Color(1f, 0.5f, 0.2f, 0f), 0f, 0.5f, Range(-30f, 30f) * Deg);
        for (var i = 0; i < 2; i++)
        {
            var a = i * 3.1416f + Range(-0.25f, 0.25f);
            Smoke(ground + Radial(a) * (0.3f * s) + Vector3.up * 0.25f, Radial(a) * (3.4f * s * Range(0.8f, 1.2f)), Range(0.9f, 1.2f),
                0.5f * s, 1.5f * s, Dust0, Dust1, 0f, 2.3f, Range(-20f, 20f) * Deg);
        }
        for (var i = 0; i < 3; i++)
        {
            var a = Rnd() * 6.2832f;
            Spark(ground + Vector3.up * 0.15f, Radial(a) * Range(3f, 7f) * s + Vector3.up * Range(2.5f, 6f), Range(0.35f, 0.6f));
        }
    }

    /// <summary>The one light flash and one scorch of a whole cluster strike, at its centre: a short warm flash and a mark of the
    /// strike's size that stays 12 s.</summary>
    internal static void ClusterCentre(Vector3 at, float radius)
    {
        var ground = Settle(at, out var normal);
        ArsenalFxSlots.Play(ArsenalFxSlots.Cluster, ground, normal, Mathf.Clamp(radius, 1.5f, 6f) * 2f);
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

    // ---- cluster airburst ----------------------------------------------------------------------------------------------

    /// <summary>The bomblet splits in the air.</summary>
    internal static void ClusterBurst(Vector3 burst) => ArsenalFxSlots.Play(ArsenalFxSlots.Cluster, burst, Vector3.up, 2f);

    // ---- incendiary landing --------------------------------------------------------------------------------------------

    /// <summary>The fire shell bursts: a light flash and the library's splash out to <paramref name="reach"/> (the burn area's edge), handing over to the area's flames.</summary>
    internal static void FireSplash(Vector3 at, float reach)
    {
        var ground = Settle(at, out var normal);
        var r = Mathf.Clamp(reach, 2f, 10f);
        Impacts.Play(Look(ref _fire, ArsenalFxSlots.Incendiary, 2f, new PointLightSpec(new Color(1f, 0.45f, 0.1f), 7.5f, 6f, 0.6f, 0f), 0.8f, ShellShake), ground, normal, r);
    }

    // ---- gas release ---------------------------------------------------------------------------------------------------

    /// <summary>The canister slams down and vents: a light flash and the library's burst out to the cloud's edge.</summary>
    internal static void GasBurst(Vector3 at, float radius)
    {
        var ground = Settle(at, out var normal);
        var r = Mathf.Clamp(radius, 2f, 14f);
        Impacts.Play(Look(ref _gas, ArsenalFxSlots.GasBurst, 2f, new PointLightSpec(new Color(0.5f, 1f, 0.3f), 8.4f, 3.5f, 0.35f, 0f), 0.5f, ClusterShake), ground, normal, r);
    }

    // ---- stasis field deployment ---------------------------------------------------------------------------------------

    /// <summary>The field beats while it runs: one ring sweeping to its edge. 1 sprite for 1 s.</summary>
    internal static void StasisBeat(Vector3 centre, float radius)
    {
        var ground = Settle(centre, out var normal);
        Ring(ground, normal, 1f, 0.8f, radius * 2.1f, new Color(0.6f, 0.88f, 1f, 0.55f), new Color(0.45f, 0.75f, 1f, 0f));
    }

    /// <summary>The emitter slams down: a light flash and the library's pulse over the whole field.</summary>
    internal static void StasisPulse(Vector3 centre, float radius)
    {
        var ground = Settle(centre, out var normal);
        var r = Mathf.Clamp(radius, 2f, 14f);
        Flash(centre + Vector3.up * 0.6f, new Color(0.35f, 0.7f, 1f), r * 1.4f, 4f, 0.7f);
        if (ArsenalFxSlots.StasisPulse is { } library) Fx.Play(library, ground, normal, r * 2f, WarnColour(StrikeKind.Stasis));
        CameraShake.Shake(ground, ClusterShake);
    }

    // ---- cutting beam: charge and burn marks ---------------------------------------------------------------------------

    /// <summary>The beam gathers light where it will come from, for the whole charge (<see cref="StrikeTiming.BeamChargeSeconds"/>).</summary>
    internal static void BeamCharge(Vector3 at)
    {
        ArsenalFxSlots.Play(ArsenalFxSlots.BeamCharge, at, Vector3.up);
    }

    /// <summary>One burn mark on the floor where the beam stood; the line it draws over its route fades from glowing to dark in
    /// 4.5 s. The marks come from a ring of 48, the oldest replaced, so at most 24 per beam stay while it walks.</summary>
    internal static void BeamMark(Vector3 at, Color colour)
    {
        var ground = Settle(at, out var normal);
        ArsenalFxSlots.Play(ArsenalFxSlots.BeamBurn, ground, normal);
        var hot = new Color(Mathf.Lerp(colour.r, 1f, 0.35f), Mathf.Lerp(colour.g, 0.6f, 0.5f), Mathf.Lerp(colour.b, 0.25f, 0.5f), 0.8f);
        Decal(true, ground, normal, 4.5f, Range(1.1f, 1.5f), hot, new Color(0.03f, 0.028f, 0.026f, 0f), 0.2f);
    }

    // ---- strafing run ---------------------------------------------------------------------------------------------------

    /// <summary>One burst of the run hits: a tracer slanting in along the run and the library's hit.</summary>
    internal static void RunHit(Vector3 at, Vector3 direction, Color colour)
    {
        var ground = Settle(at, out _);
        var hot = new Color(Mathf.Lerp(colour.r, 1f, 0.5f), Mathf.Lerp(colour.g, 1f, 0.5f), Mathf.Lerp(colour.b, 1f, 0.5f), 1f);
        Line(_sheets.Streak, ground - direction * 7f + Vector3.up * 5f, ground, 0.1f, 0.09f, hot, new Color(hot.r, hot.g, hot.b, 0f));
        if (ArsenalFxSlots.StrafeHit is { } library) Fx.Play(library, ground, Vector3.up, 1f, colour);
    }

    // ---- heavy weapon return -------------------------------------------------------------------------------------------

    /// <summary>The library's small shimmer at the gun's place in the hand as it is taken back.</summary>
    internal static void ReturnGlow(Vector3 at) => ArsenalFxSlots.Play(ArsenalFxSlots.StrikeReturn, at, Vector3.up);
}
