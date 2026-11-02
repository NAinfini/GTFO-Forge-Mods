using System;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>
/// The looks of strikes, every peer: the cutting beam (a thin bright core inside a soft glow that pulses, a reticle, a flare and a
/// spark burst where it touches) and the areas (toxic gas as rolling smoke, fire with smoke, stasis mist), each with its ground
/// mark. The beam's lines are ForgeVisual's <see cref="ForgeVisual.Beam"/> and the ground marks its <see cref="GroundMarker"/>
/// (gas hatched band, fire flames, stasis dial, the beam a reticle); the clouds, flare and sparks are sprites of ForgeVisual's shared
/// pool (<see cref="SpritePool"/>) cut from the sheets of forge-ordnance-arsenal.bundle (<see cref="ArsenalArt"/>): a flare, a spark, a
/// 2×2 mist atlas, a 2×2 fire atlas and a 5×3 smoke flipbook. Each cloud sprite is attached (it does not age) and moved, sized and
/// tinted by <see cref="Cloud.Tick"/> every frame. Without the bundle or ForgeVisual's shaders the part that needs them is not drawn
/// and the log says why once. Purely visual: the host's zone and beam records decide where and how long, damage and slowing are
/// decided elsewhere.
/// </summary>
internal static class StrikeEffects
{
    private const float Deg = 0.01745329f;
    /// <summary>The beam reticle's radius (m).</summary>
    private const float ReticleRadius = 0.75f;
    /// <summary>Frames per second of the smoke flipbook.</summary>
    private const float SmokeFps = 9f;

    /// <summary>The cutting beam's colour (one per beam strike; the pillar colour of the call-in is ForgeOrdnance's).</summary>
    internal static readonly Color BeamColour = new(1f, 0.22f, 0.12f, 1f);

    private static readonly BeamStyle BeamLook = new()
    {
        CoreWidth = 0.07f, CorePerMetre = 0f, GlowWidth = 0.45f, CoreOpacity = 1f, GlowOpacity = 0.4f, Column = false,
    };

    /// <summary>Builds the beam reticle's ground mark layer at level load, so a first beam creates none in its own frame; false when
    /// the effects cannot be drawn.</summary>
    internal static bool Prepare()
    {
        if (ArsenalArt.Sheets == null) return false;
        GroundMarker.Place(Vector3.zero, ReticleRadius, BeamColour, GroundMarkerStyle.Reticle)?.Remove();
        return true;
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
            var grow = Mathf.Clamp01(t / 0.35f);
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
            var at = to + Vector3.up * 0.15f;
            SpritePool.Move(_flare, at, Vector3.zero);
            SpritePool.Resize(_flare, 1.1f * grow * pulse, 1f);
            SpritePool.Turn(_flare, t * 140f * Deg);
            // The sparks crackle: a new random turn and size about 12 times a second.
            var step = (int)(t * 12f);
            var jitter = (step * 0.61803f) % 1f;
            SpritePool.Move(_spark, at, Vector3.zero);
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
        if (sheets == null) { Plugin.Error("Strike beams and clouds are not drawn: " + ArsenalArt.Failure); return null; }
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
        var s = SpriteSpawn.Of(sheet, SpriteFace.Camera, Vector3.zero, 1f, 1f, 1f, colour, colour);
        s.Blend = FxBlend.Alpha;
        s.FadeIn = 0f;
        s.Cell = 0;
        s.Rotation = 0f;
        return SpritePool.Attach(in s);
    }

    // ---- clouds ----------------------------------------------------------------------------------------------------

    internal sealed class Puff
    {
        internal ForgeVisual.Sprite Sprite;
        internal Vector3 Home, Drift;
        internal Color Colour;
        internal float Size, Spin, Phase, Rise, Flicker;
    }

    internal sealed class Cloud
    {
        private readonly System.Collections.Generic.List<Puff> _puffs = new();
        internal float Start, Ends;
        internal Vector3 Centre;
        /// <summary>The area's ground mark; null without ForgeVisual's shaders.</summary>
        internal GroundMarker? Mark;

        internal void Add(Puff p) => _puffs.Add(p);

        /// <summary>Gives the sprites back to the pool and takes the ground mark away; the cloud is over.</summary>
        internal void Release()
        {
            foreach (var p in _puffs) SpritePool.Kill(p.Sprite);
            _puffs.Clear();
            Mark?.Remove();
            Mark = null;
        }

        /// <summary>Smoke drifts and breathes over seconds, so its sprites are moved this many times a second, not every frame.</summary>
        private const float StepSeconds = 1f / 30f;
        private float _nextStep;

        /// <summary>Drift, breathe, turn (the smoke frames are played by ForgeVisual), fade in over a second and out over the last 1.5 s;
        /// done at 30 Hz. Smoke given up to ForgeVisual's overdraw budget costs nothing.</summary>
        internal void Tick(float now)
        {
            if (now < _nextStep) return;
            _nextStep = now + StepSeconds;
            var t = now - Start;
            var fade = Mathf.Clamp01(t / 1.2f) * Mathf.Clamp01((Ends - now) / 1.5f);
            var spread = 0.45f + 0.55f * Mathf.Clamp01(t / 1.6f);
            foreach (var p in _puffs)
            {
                var wobble = new Vector3(Mathf.Sin(t * 0.35f + p.Phase), 0f, Mathf.Cos(t * 0.29f + p.Phase * 1.7f)) * 0.35f;
                var rise = p.Rise > 0f ? Mathf.Repeat(t * p.Rise + p.Phase, 2.4f) : 0f;
                var at = Centre + (p.Home - Centre) * spread + p.Drift * t + wobble + Vector3.up * rise;
                var flicker = p.Flicker > 0f ? 0.8f + 0.2f * Mathf.Sin(t * p.Flicker + p.Phase * 3f) : 1f;
                var breathe = 1f + 0.08f * Mathf.Sin(t * 0.9f + p.Phase);
                var riseFade = p.Rise > 0f ? 1f - rise / 2.4f : 1f;
                var alpha = p.Colour.a * fade * riseFade * (0.9f + 0.1f * flicker);
                SpritePool.Move(p.Sprite, at, Vector3.zero);
                SpritePool.Resize(p.Sprite, p.Size * spread * breathe * flicker, 1f);
                SpritePool.Recolour(p.Sprite, new Color(p.Colour.r, p.Colour.g, p.Colour.b, alpha));
                SpritePool.Turn(p.Sprite, (p.Phase * 57f + t * p.Spin) * Deg);
            }
            if (Mark != null && Mark.Alive) Mark.Opacity = Mathf.Clamp01(t / 0.4f) * Mathf.Clamp01((Ends - now) / 1.5f);
        }
    }

    /// <summary>A cloud and its ground mark in <paramref name="colour"/> for an area of <paramref name="radius"/> metres around
    /// <paramref name="centre"/> (on the floor) that lasts from <paramref name="start"/> to <paramref name="ends"/>. Puffs do not go
    /// through the walls around the centre.</summary>
    internal static Cloud? CreateCloud(StrikeKind kind, Vector3 centre, float radius, Color colour, float start, float ends, uint seed)
    {
        var sheets = ArsenalArt.Sheets;
        if (sheets == null) { Plugin.Error("Strike beams and clouds are not drawn: " + ArsenalArt.Failure); return null; }
        var cloud = new Cloud { Start = start, Ends = ends, Centre = centre };
        var random = new System.Random(unchecked((int)seed * 7919 + 13));
        float R() => (float)random.NextDouble();
        switch (kind)
        {
            case StrikeKind.Gas:
            {
                // A heavy, low, sickly green-yellow cloud of rolling smoke filling the whole radius.
                var count = Math.Clamp((int)Math.Round(radius * radius * 0.45f), 12, 28);
                for (var i = 0; i < count; i++)
                    cloud.Add(Spawn(sheets.Smoke, true, centre, radius * 0.9f, random, 0.25f, 1.9f,
                        new Color(0.42f + 0.25f * R(), 0.85f + 0.12f * R(), 0.16f + 0.1f * R(), 0.55f + 0.2f * R()),
                        radius * (0.55f + 0.3f * R()), 8f, 0f, 0f, 0.05f));
                break;
            }
            case StrikeKind.Incendiary:
            {
                var flames = Math.Clamp((int)Math.Round(radius * radius * 0.45f), 10, 22);
                for (var i = 0; i < flames; i++)
                    cloud.Add(Spawn(sheets.Fire, false, centre, radius * 0.85f, random, 0.15f, 0.9f,
                        new Color(1f, 0.38f + 0.3f * R(), 0.06f + 0.06f * R(), 0.7f + 0.2f * R()),
                        0.9f + 0.9f * R(), 40f, 0f, 9f + 6f * R(), 0f));
                var smoke = Math.Clamp(flames / 2, 5, 11);
                for (var i = 0; i < smoke; i++)
                    cloud.Add(Spawn(sheets.Smoke, true, centre, radius * 0.7f, random, 1.2f, 2.2f,
                        new Color(0.12f, 0.11f, 0.1f, 0.55f + 0.2f * R()), 2.2f + 1.6f * R(), 12f, 0.45f + 0.35f * R(), 0f, 0f));
                break;
            }
            case StrikeKind.Stasis:
            {
                var count = Math.Clamp((int)Math.Round(radius * radius * 0.28f), 8, 20);
                for (var i = 0; i < count; i++)
                    cloud.Add(Spawn(sheets.Mist, false, centre, radius * 0.9f, random, 0.15f, 1.2f,
                        new Color(0.45f + 0.2f * R(), 0.75f + 0.15f * R(), 1f, 0.2f + 0.1f * R()),
                        radius * (0.3f + 0.2f * R()), 5f, 0f, 0f, 0f));
                break;
            }
            default:
                return null;
        }
        var style = kind switch { StrikeKind.Gas => GroundMarkerStyle.Hatched, StrikeKind.Incendiary => GroundMarkerStyle.Flames, _ => GroundMarkerStyle.Dial };
        cloud.Mark = GroundMarker.Place(centre, radius, colour, style);
        if (cloud.Mark != null) cloud.Mark.Opacity = 0f;
        cloud.Tick(start);
        return cloud;
    }

    /// <summary>One puff: a random cell of <paramref name="sheet"/>, or, when <paramref name="animated"/>, the whole flipbook from a random frame.</summary>
    private static Puff Spawn(SpriteSheet sheet, bool animated, Vector3 centre, float radius, System.Random random,
        float low, float high, Color colour, float size, float spin, float rise, float flicker, float drift)
    {
        float R() => (float)random.NextDouble();
        var angle = R() * Mathf.PI * 2f;
        var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        var distance = Mathf.Sqrt(R()) * radius;
        // Keep puffs on this side of the walls around the area.
        if (Physics.Raycast(centre + Vector3.up, direction, out var wall, distance + size * 0.4f, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore))
            distance = Math.Max(0f, Math.Min(distance, Vector3.Distance(centre + Vector3.up, wall.point) - size * 0.4f));
        var floor = Strikes.Ground(centre + direction * distance);
        var cell = random.Next(sheet.Frames);
        var puff = new Puff
        {
            Colour = colour, Size = size, Rise = rise, Flicker = flicker,
            Home = floor + Vector3.up * (low + (high - low) * R()),
            Drift = new Vector3(R() - 0.5f, 0f, R() - 0.5f) * drift,
            Spin = (R() - 0.5f) * 2f * spin,
            Phase = R() * 6.283f,
        };
        var s = SpriteSpawn.Of(sheet, SpriteFace.Camera, puff.Home, 1f, size, size, new Color(colour.r, colour.g, colour.b, 0f), new Color(colour.r, colour.g, colour.b, 0f));
        s.FadeIn = 0f;
        s.Cell = cell;
        if (animated)
        {
            s.Flip = true;
            s.FlipFps = SmokeFps;
            s.FlipPhase = puff.Phase * 2.4f / SmokeFps;
        }
        puff.Sprite = SpritePool.Attach(in s);
        return puff;
    }
}
