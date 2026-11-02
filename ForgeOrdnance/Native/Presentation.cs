using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnance;

/// <summary>The local player's thrown summoning ball, followed until it comes to rest. The ball itself is the game's own thrown
/// item; this only reads its rigid body (or, without one, its position) to find the landing point.</summary>
internal sealed class ThrownBeacon
{
    private const float RestSpeedSquared = 0.04f;
    private const float RestSeconds = 0.6f;
    private const float FallLimit = 150f;
    private readonly Item _item;
    private readonly Rigidbody? _body;
    private readonly float _bornAt;
    private float _stillSince = -1f;
    private Vector3 _last;

    internal ThrownBeacon(uint throwId, uint key, Vec3 origin, Item item, ushort replicator)
    {
        ThrowId = throwId; Key = key; Origin = origin; _item = item; Replicator = replicator;
        _body = item.GetComponentInChildren<Rigidbody>();
        _bornAt = Time.time;
        _last = item.transform.position;
    }

    internal uint ThrowId { get; }
    internal uint Key { get; }
    internal ushort Replicator { get; }
    internal Vec3 Origin { get; }
    internal bool Reported { get; set; }
    /// <summary>The game removed the item before it settled.</summary>
    internal bool Gone => _item == null || _item.WasCollected;
    internal Vector3 Position => Gone ? _last : _item.transform.position;
    internal float Age => Time.time - _bornAt;
    internal bool Fell => Position.y < Origin.Y - FallLimit;

    /// <summary>True once the ball has been asleep, or has moved slower than a walking pace, for a continuous stretch.</summary>
    internal bool Settled()
    {
        if (Gone) return false;
        var now = _item.transform.position;
        var still = _body != null && !_body.WasCollected
            ? _body.IsSleeping() || _body.velocity.sqrMagnitude <= RestSpeedSquared
            : (now - _last).sqrMagnitude <= 0.0004f;
        _last = now;
        if (!still) { _stillSince = -1f; return false; }
        if (_stillSince < 0f) _stillSince = Time.time;
        return Time.time - _stillSince >= RestSeconds;
    }
}

/// <summary>
/// The landing marker, drawn in the world (not interface), in the definition's <see cref="OrdnanceDefinition.BeamRgb"/>, through
/// ForgeVisual:
/// <list type="bullet">
/// <item>the beam (<see cref="ForgeVisual.Beams"/>): a hot core that stays at least about a pixel and a half wide at any distance, inside
/// a soft glow; both are brightest at the floor, settle above head height, breathe slowly and carry faint bands of energy climbing up;</item>
/// <item>a small camera-facing flare where the beam meets the floor (the beam's own flare);</item>
/// <item>the ground mark (<see cref="ForgeVisual.GroundMarker"/>, countdown style), laid once on the floor under it: a crisp rim whose lit
/// arc is the time left and empties clockwise from north, 24 scale ticks inside it, four brackets turning slowly outside it, a dashed inner
/// ring, a hot centre point, a faint fill and a ping that runs out past the rim;</item>
/// <item>the game's own world marker (ForgeUI's <see cref="ForgeUI.NavMarkerHandle"/>) beside the beam: the name small,
/// spaced and pale, the seconds large in monospaced digits in the beam colour, its unit small.</item>
/// </list>
/// Over the last three seconds everything tightens: the pings speed up from about one to three a second, the brackets and the
/// inner ring close in, the rim and fill blink on each second's tick, the beam narrows and brightens, and the seconds turn red
/// and grow. At arrival the whole beam flashes. Nothing is allocated per frame except the label text when its tenth of a second changes.
/// </summary>
internal sealed class OrdnanceBeacon
{
    private const float Height = 80f;
    private const float MarkRadius = 1.1f, MarkReach = 1.32f;
    /// <summary>The countdown ticks once for each of the last this many seconds.</summary>
    private const int TickSeconds = 3;

    /// <summary>The call-in beam: the core is never narrower than 0.0014 m per metre of distance (about 1.5 pixels at 1080p), so it never
    /// breaks up far away; each part's brightness is set every frame.</summary>
    private static readonly ForgeVisual.BeamStyle BeamLook = new()
    {
        CoreWidth = 0.03f, CorePerMetre = 0.0014f, GlowWidth = 0.34f, CoreSharpness = 1.1f, GlowSharpness = 3.2f,
        CoreOpacity = 1f, GlowOpacity = 1f, Column = true, FlareSize = 0.55f, FlareAtEnd = false, FlareOpacity = 1f, FlareLift = 0.14f,
    };

    private int _tickedSecond = int.MaxValue;
    private readonly GameObject _object;
    private ForgeAudio.AudioSustain? _waitingSound;
    private ForgeVisual.Beam? _beam;
    private ForgeVisual.GroundMarker? _mark;
    /// <summary>A beacon built at the moment its ball lands (not replayed from a late join) plays the touchdown: the beam shoots
    /// up, a shock ring runs out and sparks fly. Everything else is the same on every peer.</summary>
    private readonly bool _fresh;
    private bool _riseDone;
    private float _length;
    private readonly Color _colour, _hot;
    private readonly float _born, _total;
    private ForgeUI.NavMarkerHandle? _marker;

    internal OrdnanceBeacon(ulong thrower, uint throwId, OrdnanceDefinition definition, Vector3 position, float delay)
    {
        Thrower = thrower; ThrowId = throwId; _definition = definition; Position = position; ArriveAt = Time.time + delay;
        _born = Time.time;
        _total = Math.Max(0.1f, (float)definition.CallInDelay);
        _colour = Rgb(definition.BeamRgb);
        _hot = Color.Lerp(_colour, Color.white, 0.65f);
        _object = new GameObject("ForgeOrdnance.Beacon");
        _object.transform.position = position;
        // Pending arrivals are replayed with their remaining delay, rather than the original definition delay.
        _fresh = delay == (float)definition.CallInDelay;
        try { Build(definition, position); }
        catch
        {
            // A beacon that is never added to the controller's list would leave its beam, ground mark, label and sound in the map.
            Destroy();
            throw;
        }
    }

    private void Build(OrdnanceDefinition definition, Vector3 position)
    {
        if (_fresh)
        {
            OrdnanceCues.Attach("beacon.activate", _object);
            OrdnanceCues.World(OrdnanceSound.BeaconLand, _object);
            OrdnanceFx.BeaconImpact(position, _hot);
        }
        _waitingSound = OrdnanceCues.Sounds?.Sustain("beacon.loop", _object);
        _beam = ForgeVisual.Beams.Create(_colour, BeamLook);
        _length = Height;
        _mark = ForgeVisual.GroundMarker.Place(position, MarkRadius, _colour, ForgeVisual.GroundMarkerStyle.Countdown, MarkReach);
        Show(Time.time);

        _marker = ForgeUI.UIKit.Mod(Plugin.Id).AddCountdownMarker(position, ForgeUI.UIText.Plain(Name), ArriveAt, _colour);
    }

    internal static Color Rgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    /// <summary>Animates the beam, flare and ground mark: the touchdown rise at the start, breathing and pings while waiting, the
    /// tightening over the last three seconds and the flash at arrival.</summary>
    private void Show(float now)
    {
        var f = BeaconShow.At(now - _born, Remaining, _total);
        var hot = Color.Lerp(_hot, Color.white, f.HotToWhite);
        if (_beam != null)
        {
            if (!_riseDone)
            {
                var rise = BeaconShow.Rise(now - _born, _fresh);
                _length = Height * rise;
                _beam.Show(Position, Position + Vector3.up * _length);
                _riseDone = rise >= 1f;
            }
            _beam.Hot = hot;
            _beam.Width = f.Width;
            _beam.GlowWidthScale = f.GlowWidthScale;
            _beam.CoreLevel = f.CoreLevel;
            _beam.GlowLevel = f.GlowLevel;
            _beam.FlareLevel = f.FlareLevel;
            _beam.FlareScale = f.FlareScale;
        }
        if (_mark != null)
        {
            _mark.Hot = hot;
            _mark.Opacity = f.MarkOpacity;
            _mark.Urgency = f.Urgency;
            _mark.Remaining = f.MarkRemaining;
            _mark.PingRate = f.PingRate;
        }
    }
    /// <summary>Every frame: animates the marker and updates the countdown label (ForgeUI keeps it beside the beam and changes its text only
    /// when the shown tenth of a second changes).</summary>
    internal void Tick()
    {
        if (_object == null || _object.WasCollected) return;
        var seconds = (int)Math.Ceiling(Remaining);
        if (seconds >= 1 && seconds <= TickSeconds && seconds < _tickedSecond)
        {
            _tickedSecond = seconds;
            _mark?.Blink();
            OrdnanceCues.World(OrdnanceSound.BeaconTick, _object);
        }
        if (Remaining <= 0f) _waitingSound?.Stop();
        Show(Time.time);
        _marker?.Update();
    }

    internal ulong Thrower { get; }
    internal uint ThrowId { get; }
    private readonly OrdnanceDefinition _definition;

    /// <summary>The call-in's name in the current language.</summary>
    internal string Name => OrdnanceContent.Name(_definition);
    internal Vector3 Position { get; }
    internal float ArriveAt { get; }
    internal float Remaining => Math.Max(0f, ArriveAt - Time.time);

    internal void Destroy()
    {
        _waitingSound?.Stop();
        _marker?.Remove();
        _marker = null;
        _beam?.Release();
        _mark?.Remove();
        if (_object != null) Object.Destroy(_object);
    }
}