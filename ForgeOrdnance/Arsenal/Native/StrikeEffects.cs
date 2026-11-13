using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>
/// The looks of strikes the effect library does not draw, every peer: a strafing run's streak (a thin bright core inside a soft glow that
/// pulses, a reticle, a flare and a spark burst where it touches) and each area's ground mark. The lines are ForgeVisual's
/// <see cref="ForgeVisual.Beam"/> and the ground marks its <see cref="GroundMarker"/> (gas hatched band, fire flames, stasis dial, the beam a
/// reticle); the flare and sparks are sprites of ForgeVisual's shared pool (<see cref="SpritePool"/>) cut from the sheets of
/// forge-ordnance-arsenal.bundle (<see cref="ArsenalArt"/>). Without the bundle or ForgeVisual's shaders the part that needs them is not drawn
/// and the log says why once. Purely visual: the host's zone and beam records decide where and how long, damage and slowing are
/// decided elsewhere.
/// </summary>
internal static class StrikeEffects
{
    private const float Deg = 0.01745329f;
    /// <summary>The beam reticle's radius (m).</summary>
    private const float ReticleRadius = 0.75f;

    /// <summary>The cutting beam's colour (one per beam strike; the pillar colour of the call-in is the carrier's).</summary>
    internal static readonly Color BeamColour = new(1f, 0.22f, 0.12f, 1f);

    private static readonly BeamStyle BeamLook = new()
    {
        CoreWidth = 0.07f,
        CorePerMetre = 0f,
        GlowWidth = 0.45f,
        CoreOpacity = 1f,
        GlowOpacity = 0.4f,
        Column = false,
    };

    /// <summary>The hunter's laser: its colour and look. The mechanism is <see cref="TargetLaser"/>.</summary>
    internal static readonly Color LaserColour = new(1f, 0.08f, 0.05f, 1f);

    internal static TargetLaser? CreateLaser()
    {
        var sheets = ArsenalArt.Sheets;
        if (sheets == null) { Plugin.Logger.ErrorOnce("Strike beams and clouds are not drawn: " + ArsenalArt.Failure); return null; }
        return TargetLaser.Create(LaserColour, new TargetLaserStyle
        {
            Dot = sheets.Flare,
            Ring = sheets.Ring,
            LightRange = 5f,
        });
    }

    /// <summary>Builds the beam reticle's ground mark layer at level load, so a first beam creates none in its own frame; false when
    /// the effects cannot be drawn.</summary>
    internal static bool Prepare()
    {
        if (ArsenalArt.Sheets == null) return false;
        GroundMarker.Place(Vec.Zero, ReticleRadius, BeamColour, GroundMarkerStyle.Reticle)?.Remove();
        return true;
    }

    // ---- the ground a call can hurt -----------------------------------------------------------------------------------

    /// <summary>The ground a call can hurt: a hazard band at exactly its edge on the floor (it stops at walls), round a disc or along a strip's rectangle in the same look, a strip
    /// that walks (creeping barrage, strafing run) with chevrons going its way; brightening and beating over the last second.</summary>
    internal sealed class Danger
    {
        private const float UrgentSeconds = 1.2f, FadeSeconds = 0.3f;
        private readonly GroundMarker? _mark;
        private readonly float _born, _ends;

        internal Danger(GroundMarker? mark, float now, float seconds)
        {
            _mark = mark;
            _born = now;
            _ends = now + seconds;
        }

        /// <summary>Every frame; false once it is over and gone.</summary>
        internal bool Tick(float now)
        {
            if (now >= _ends + FadeSeconds) { Release(); return false; }
            if (_mark == null || !_mark.Alive) return true;
            var left = _ends - now;
            var beat = left < UrgentSeconds ? 0.5f * Mathf.Abs(Mathf.Sin(now * 11f)) : 0f;
            _mark.Opacity = Mathf.Clamp01((now - _born) / 0.25f) * Mathf.Clamp01((left + FadeSeconds) / FadeSeconds) * (0.75f + beat);
            return true;
        }

        internal void Release() => _mark?.Remove();
    }

    internal static Danger? PlaceDanger(in DangerRecord r, float now)
    {
        var at = new Vector3(r.X, r.Y, r.Z);
        var f = r.Footprint;
        var forward = new Vector3(r.DirX, 0f, r.DirZ);
        var mark = f.Radius > 0f
            ? GroundMarker.Place(at, f.Radius, StrikeFx.WarnColour(r.Kind), r.Kind == StrikeKind.Stasis ? GroundMarkerStyle.Dial : GroundMarkerStyle.Hatched)
            : GroundMarker.PlaceStrip(at + forward * ((f.Back + f.Front) * 0.5f), forward, f.HalfWidth, (f.Front - f.Back) * 0.5f, StrikeFx.WarnColour(r.Kind),
                GroundMarkerStyle.Hatched, r.Kind is StrikeKind.Creeping or StrikeKind.Strafe);
        if (mark != null) mark.Opacity = 0f;
        return new Danger(mark, now, r.Footprint.Seconds);
    }

    // ---- beam ------------------------------------------------------------------------------------------------------

    internal sealed class Beam
    {
        private readonly ForgeVisual.Beam _line;
        private readonly ForgeVisual.Sprite _flare, _spark;
        private GroundMarker? _reticle;
        private readonly Color _colour;
        private readonly float _born;

        internal Beam(ForgeVisual.Beam line, ForgeVisual.Sprite flare, ForgeVisual.Sprite spark, Color colour, float born)
        {
            _line = line;
            _flare = flare;
            _spark = spark;
            _colour = colour;
            _born = born;
        }

        /// <summary>Every frame: from the top (above the ceiling) to the shown point on the ground or target.</summary>
        internal void Show(Vector3 from, Vector3 to, float now)
        {
            var t = now - _born;
            var grow = Mathf.Clamp01(t / 0.12f);
            var pulse = 1f + 0.18f * Mathf.Sin(t * 19f) + 0.08f * Mathf.Sin(t * 47f);
            _line.Width = grow;
            _line.CoreScale = 0.9f + 0.1f * pulse;
            _line.GlowWidthScale = pulse;
            _line.Show(from, to);
            // The reticle where the beam touches: laid on the floor under the beam, again as it moves.
            if (_reticle == null || !_reticle.Alive) _reticle = GroundMarker.Place(to, ReticleRadius, _colour, GroundMarkerStyle.Reticle);
            else _reticle.Move(to);
            if (_reticle != null) _reticle.Opacity = grow;
            var hot = new Color(Mathf.Lerp(_colour.r, 1f, 0.65f), Mathf.Lerp(_colour.g, 1f, 0.65f), Mathf.Lerp(_colour.b, 1f, 0.65f), 1f);
            var at = to + Vec.Up * 0.15f;
            SpritePool.Move(_flare, at, Vec.Zero);
            SpritePool.Resize(_flare, 1.1f * grow * pulse, 1f);
            SpritePool.Turn(_flare, t * 140f * Deg);
            // The sparks crackle: a new random turn and size about 12 times a second.
            var step = (int)(t * 12f);
            var jitter = (step * 0.61803f) % 1f;
            SpritePool.Move(_spark, at, Vec.Zero);
            SpritePool.Resize(_spark, grow * (0.9f + 0.6f * jitter), 1f);
            SpritePool.Turn(_spark, jitter * 360f * Deg);
            SpritePool.Recolour(_spark, new Color(hot.r, hot.g, hot.b, 0.65f + 0.35f * ((step * 0.3819f) % 1f)));
        }

        /// <summary>The beam is over: its lines, reticle, flare and sparks go.</summary>
        internal void Release()
        {
            _line.Release();
            _reticle?.Remove();
            _reticle = null;
            SpritePool.Kill(_flare);
            SpritePool.Kill(_spark);
        }
    }

    internal static Beam? CreateBeam(Color colour, float now)
    {
        var sheets = ArsenalArt.Sheets;
        if (sheets == null) { Plugin.Logger.ErrorOnce("Strike beams and clouds are not drawn: " + ArsenalArt.Failure); return null; }
        var line = Beams.Create(colour, BeamLook);
        if (line == null) return null;
        var hot = new Color(Mathf.Lerp(colour.r, 1f, 0.5f), Mathf.Lerp(colour.g, 1f, 0.5f), Mathf.Lerp(colour.b, 1f, 0.5f), 0.85f);
        var flare = Attached(sheets.Flare, hot);
        var spark = Attached(sheets.Spark, colour);
        return new Beam(line, flare, spark, colour, now);
    }

    /// <summary>A sprite that stays where it is put and is drawn by alpha, with <paramref name="colour"/>.</summary>
    private static ForgeVisual.Sprite Attached(SpriteSheet sheet, Color colour)
    {
        var s = SpriteSpawn.Of(sheet, SpriteFace.Camera, Vec.Zero, 1f, 1f, 1f, colour, colour);
        s.Blend = FxBlend.Alpha;
        s.FadeIn = 0f;
        s.Cell = 0;
        s.Rotation = 0f;
        return SpritePool.Attach(in s);
    }

    // ---- areas -----------------------------------------------------------------------------------------------------

    /// <summary>An area's ground mark at its true radius; what stands over it is the library's field (<see cref="ArsenalFxSlots.FieldOf"/>).</summary>
    internal sealed class Cloud
    {
        internal float Start, Ends;
        internal GroundMarker? Mark;

        internal void Release()
        {
            Mark?.Remove();
            Mark = null;
        }

        /// <summary>Fades in over 0.4 s and out over the last 1.5 s.</summary>
        internal void Tick(float now)
        {
            if (Mark != null && Mark.Alive) Mark.Opacity = Mathf.Clamp01((now - Start) / 0.4f) * Mathf.Clamp01((Ends - now) / 1.5f);
        }
    }

    /// <summary>The ground mark in <paramref name="colour"/> for an area of <paramref name="radius"/> metres around <paramref name="centre"/> (on the floor)
    /// that lasts from <paramref name="start"/> to <paramref name="ends"/>.</summary>
    internal static Cloud CreateCloud(StrikeKind kind, Vector3 centre, float radius, Color colour, float start, float ends)
    {
        var cloud = new Cloud { Start = start, Ends = ends };
        var style = kind switch { StrikeKind.Gas => GroundMarkerStyle.Hatched, StrikeKind.Incendiary => GroundMarkerStyle.Flames, _ => GroundMarkerStyle.Dial };
        cloud.Mark = GroundMarker.Place(centre, radius, colour, style);
        if (cloud.Mark != null)
        {
            cloud.Mark.Opacity = 0f;
            // The danger mark stood at this radius until now: the area's own takes over without sweeping on again.
            cloud.Mark.SweepSeconds = 0f;
        }
        return cloud;
    }
}
