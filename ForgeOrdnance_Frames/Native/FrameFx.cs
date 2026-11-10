using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>The exosuits' particle effects. Each moment plays its slot of the effect library (<see cref="FramesFxSlots"/>; a slot the table does not list logs its error and draws
/// nothing). Nothing here is networked: each peer calls these from the state it already has.</summary>
internal static class FrameFx
{
    // ---- the moments ----

    /// <summary>The last moment of a drop: the thrusters blow the dust off the ground below the suit, their air ring in the suit's <paramref name="glow"/>.</summary>
    internal static void Kick(Vector3 ground, Color glow) { if (FramesFxSlots.Kick is { } library) Fx.Play(library, ground + Vector3.up * 0.1f, Vector3.up, float.NaN, glow); }

    /// <summary>A suit touching down, its ring in the suit's <paramref name="glow"/>.</summary>
    internal static void Landing(Vector3 ground, Color glow) { if (FramesFxSlots.Landing is { } library) Fx.Play(library, ground + Vector3.up * 0.1f, Vector3.up, float.NaN, glow); }

    /// <summary>The siege supports planting: a ring in the suit's <paramref name="glow"/>.</summary>
    internal static void Siege(Vector3 ground, Color glow)
    {
        if (FramesFxSlots.Siege is { } library) Fx.Play(library, ground + Vector3.up * 0.1f, Vector3.up, float.NaN, glow);
    }

    /// <summary>The look of a charge's trail in the suit's <paramref name="glow"/>: a broad ribbon of light that thins to nothing along the path the suit's back has run, with
    /// no head sprite: only where the suit has been is drawn, not something riding behind it.</summary>
    internal static TrailStyle ChargeStyle(Color glow) => new()
    {
        RibbonPoints = 20,
        RibbonStep = 0.02f,
        RibbonWidth = 0.45f,
        RibbonTaper = 0.04f,
        RibbonHead = new Color(glow.r, glow.g, glow.b, 0.9f),
        RibbonTail = new Color(glow.r, glow.g, glow.b, 0f),
        RibbonBlend = FxBlend.Additive,
    };

    /// <summary>Every frame of a charge: the trail follows the suit's <paramref name="back"/> (<paramref name="trail"/> is the suit's own, null until the first frame). It is left
    /// out for the pilot's own first-person view (<paramref name="firstPerson"/>): the pilot cannot see their own back, and everyone else still sees the trail.</summary>
    internal static void ChargeTrail(ref Trail? trail, TrailStyle style, Vector3 back, bool firstPerson)
    {
        if (firstPerson) return;
        trail ??= Trails.Rent(back, style);
        trail?.Move(back, 0f);
    }

    /// <summary>The trail ended (the charge, a rocket's landing, or the suit going): the ribbon gathers into the last point and goes.</summary>
    internal static void TrailEnd(ref Trail? trail)
    {
        trail?.End();
        trail = null;
    }

    private static readonly TrailStyle RocketStyle = new()
    {
        RibbonPoints = 28,
        RibbonStep = 0.02f,
        RibbonWidth = 0.18f,
        RibbonTaper = 0.06f,
        RibbonHead = new Color(1f, 0.82f, 0.5f, 0.95f),
        RibbonTail = new Color(1f, 0.4f, 0.12f, 0f),
        RibbonBlend = FxBlend.Additive,
    };

    /// <summary>Every frame of a rocket's flight: its ribbon follows it (<paramref name="trail"/> is the rocket's own, null until the first frame).</summary>
    internal static void RocketTrail(ref Trail? trail, Vector3 at)
    {
        trail ??= Trails.Rent(at, RocketStyle);
        trail?.Move(at, 0f);
    }

    /// <summary>One tick of a charge (every second tick): dust kicked up behind the feet.</summary>
    internal static void ChargeDust(Vector3 feet)
    {
        if (FramesFxSlots.ChargeDust is { } kicked) Fx.Play(kicked, feet + Vector3.up * 0.1f, Vector3.up);
    }

    /// <summary>The charge's shoulder hitting a door or an enemy, thrown back along <paramref name="forward"/>, in the suit's <paramref name="glow"/>.</summary>
    internal static void Bash(Vector3 at, Vector3 forward, Color glow)
    {
        if (FramesFxSlots.Ram is { } library) Fx.Play(library, at, -forward, float.NaN, glow);
    }

    /// <summary>The barrier shoving enemies out: a ring in the suit's <paramref name="glow"/> along the floor from its feet out to the dome's wall,
    /// so its own pilot sees it below the view, not across it.</summary>
    internal static void Pulse(Vector3 feet, float radius, Color glow)
    {
        if (FramesFxSlots.Pulse is { } library) Fx.Play(library, feet + Vector3.up * 0.1f, Vector3.up, radius * 2f, glow);
    }

    /// <summary>The barrier taking a hit (<paramref name="hit"/>) or breaking: one flash around the body in the suit's <paramref name="glow"/>.</summary>
    internal static void Barrier(bool hit, Vector3 chest, Color glow)
    {
        if ((hit ? FramesFxSlots.ShieldHit : FramesFxSlots.ShieldBreak) is { } library) Fx.Play(library, chest, Vector3.up, float.NaN, glow);
    }

    /// <summary>A rocket leaving its shoulder: out of the back, the launcher's flash and a cone of dust blown backward (the same back-blast as Arsenal's rockets, not in the suit's colour).</summary>
    internal static void Backblast(Vector3 at, Vector3 backward)
    {
        if (backward.sqrMagnitude >= 1e-6f && FramesFxSlots.Backblast is { } library) Fx.Play(library, at, backward);
    }

    /// <summary>The moment of destruction.</summary>
    internal static void Explosion(Vector3 feet)
    {
        if (FramesFxSlots.Explosion is { } library) Fx.Play(library, feet + Vector3.up * 1.1f, Vector3.up, 8f, Color.white);
    }

    /// <summary>One emission of a burning wreck: a flame and/or a curl of black smoke somewhere over its body.</summary>
    internal static void Wreck(int key, Vector3 feet, bool flame, bool smoke)
    {
        var body = feet + Vector3.up * 1.0f;
        if (flame && FramesFxSlots.Wreck is { } fire) Fx.Hold(fire, key, body, Vector3.up, float.NaN, Color.white);
        if (smoke && FramesFxSlots.WreckSmoke is { } curl) Fx.Play(curl, body + Vector3.up * 0.4f, Vector3.up);
    }

    /// <summary>One back plate blowing open on ejection, along <paramref name="outward"/>.</summary>
    internal static void Eject(Vector3 plate, Vector3 outward)
    {
        if (outward.sqrMagnitude >= 1e-6f && FramesFxSlots.Eject is { } library) Fx.Play(library, plate, outward);
    }

    /// <summary>A faint small mist from a seam opening (boarding), along <paramref name="direction"/>.</summary>
    internal static void Vent(Vector3 at, Vector3 direction) { if (FramesFxSlots.Vent is { } library) Fx.Play(library, at, direction.normalized); }

    /// <summary>One wisp of hot haze from a shoulder as the suit overheats.</summary>
    internal static void Heat(Vector3 shoulder) { if (FramesFxSlots.Heat is { } library) Fx.Play(library, shoulder, Vector3.up); }

    // ---- the game's own effects (ForgeVisual's Game: catalogue) ----

    private static FxHandle? _explosion;

    /// <summary>A shot's tracer from the gun to where it lands (the game's own tracer).</summary>
    internal static void Tracer(Vector3 from, Vector3 to) => Fx.Tracer(from, to);

    /// <summary>The game's trip-mine explosion where a rocket lands, facing up.</summary>
    internal static void RocketExplosion(Vector3 point)
    {
        _explosion ??= Fx.Find("Game:FX_Tripmine");
        if (_explosion != null) Fx.Play(_explosion, point, Vector3.up);
    }

}
