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
    internal float Age(float now) => now - _bornAt;
    internal bool Fell => Position.y < Origin.Y - FallLimit;

    /// <summary>True once the ball has been asleep, or has moved slower than a walking pace, for a continuous stretch.</summary>
    internal bool Settled(float now)
    {
        if (Gone) return false;
        var at = _item.transform.position;
        var still = _body != null && !_body.WasCollected
            ? _body.IsSleeping() || Vec.SqrLength(_body.velocity) <= RestSpeedSquared
            : Vec.SqrLength(at - _last) <= 0.0004f;
        _last = at;
        if (!still) { _stillSince = -1f; return false; }
        if (_stillSince < 0f) _stillSince = now;
        return now - _stillSince >= RestSeconds;
    }
}

/// <summary>
/// The landing marker, drawn in the world (not interface), in the definition's <see cref="OrdnanceDefinition.BeamRgb"/>:
/// <list type="bullet">
/// <item>the pillar of light, the slot <c>beacon.pillar</c> of the effect library: a thin beam in the beam colour held from the landing point up to the ceiling
/// (at most 20 m; with no ceiling it fades out in the air);</item>
/// <item>the ground mark (<see cref="ForgeVisual.GroundMarker"/>, countdown style), laid once on the floor under it: a crisp rim whose lit
/// arc is the time left and empties clockwise from north, 24 scale ticks inside it, four brackets turning slowly outside it, a dashed inner
/// ring, a centre dot and a thin ping ring that runs out past the rim, all thin lines with no fill;</item>
/// <item>the game's own world marker (ForgeUI's <see cref="ForgeUI.NavMarkerHandle"/>) beside the pillar: the name small,
/// spaced and pale, the seconds large in monospaced digits in the beam colour, its unit small.</item>
/// </list>
/// Over the last three seconds everything tightens: the pings speed up from about one to three a second, the brackets and the
/// inner ring close in, the rim blinks on each second's tick, and the seconds turn red and grow. At arrival the mark flashes.
/// </summary>
internal sealed class OrdnanceBeacon
{
    private const float MarkRadius = 1.1f, MarkReach = 1.32f;
    /// <summary>The countdown ticks once for each of the last this many seconds.</summary>
    private const int TickSeconds = 3;
    /// <summary>The tallest the pillar stands (m) where no ceiling is above it: higher than GTFO's tallest halls (about 15 m), so a big room still shows it
    /// meeting its ceiling, and outdoors a thin line seen over the crates from across the area without reaching up forever.</summary>
    private const float PillarReach = 20f;
    private Vector3 _pillarTop;
    private bool _pillarCeiling;
    /// <summary>This beacon's own hold key for its pillar: two beacons side by side keep two pillars.</summary>
    private readonly int _pillarKey = ForgeVisual.Fx.NewKey();

    private int _tickedSecond = int.MaxValue;
    private readonly GameObject _object;
    private ForgeAudio.AudioSustain? _waitingSound;
    private ForgeVisual.GroundMarker? _mark;
    /// <summary>A beacon built at the moment its ball lands (not replayed from a late join) plays the touchdown. Everything else is the same on every peer.</summary>
    private readonly bool _fresh;
    private readonly Color _colour, _hot;
    private readonly float _born, _total;
    private ForgeUI.NavMarkerHandle? _marker;

    internal OrdnanceBeacon(ulong thrower, uint throwId, OrdnanceDefinition definition, Vector3 position, float delay)
    {
        Thrower = thrower; ThrowId = throwId; _definition = definition; Position = position; ArriveAt = Time.time + delay;
        _born = Time.time;
        _total = Math.Max(0.1f, (float)definition.CallInDelay);
        _colour = ForgeCore.Colours.FromRgb(definition.BeamRgb);
        _hot = Color.Lerp(_colour, Color.white, 0.65f);
        _object = new GameObject("ForgeOrdnance.Beacon");
        _object.transform.position = position;
        // Pending arrivals are replayed with their remaining delay, rather than the original definition delay.
        _fresh = delay == (float)definition.CallInDelay;
        try { Build(definition, position); }
        catch
        {
            // A beacon that is never added to the controller's list would leave its ground mark, label and sound in the map.
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
        // The pillar stands from the floor to the ceiling, which it meets in a small glow; with no ceiling within PillarReach it ends in the air, fading out.
        var height = ForgeCore.Level.LiftUnderRoof(position, PillarReach, 1f);
        _pillarCeiling = height < PillarReach;
        _pillarTop = position + Vec.Up * height;
        _waitingSound = PartSounds.Carrier?.Sustain("beacon.loop", _object);
        _mark = ForgeVisual.GroundMarker.Place(position, MarkRadius, _colour, ForgeVisual.GroundMarkerStyle.Countdown, MarkReach);
        Show(Time.time);

        _marker = ForgeUI.UIKit.Mod(Plugin.Id).AddCountdownMarker(position, ForgeUI.UIText.Plain(Name), ArriveAt, _colour);
    }

    /// <summary>Keeps the pillar alive and animates the ground mark: the pings while waiting, the tightening over the last three seconds and the flash at arrival.</summary>
    private void Show(float now)
    {
        var f = BeaconShow.At(now - _born, Remaining(now), _total);
        if (OrdnanceFxSlots.Pillar is { } pillar) ForgeVisual.Fx.Line(pillar, _pillarKey, Position, _pillarTop, _pillarCeiling, _colour);
        if (_mark != null)
        {
            // Halfway from the mark's colour to its hot colour: the mark's centre point and ping add up over its fill, and the hot colour itself burned to white.
            _mark.Hot = Color.Lerp(_colour, Color.Lerp(_hot, Color.white, f.HotToWhite), 0.5f);
            _mark.Opacity = f.MarkOpacity;
            _mark.Urgency = f.Urgency;
            _mark.Remaining = f.MarkRemaining;
            _mark.PingRate = f.PingRate;
        }
    }
    /// <summary>Every frame: animates the marker and updates the countdown label (ForgeUI keeps it beside the pillar and changes its text only
    /// when the shown tenth of a second changes).</summary>
    internal void Tick(float now)
    {
        if (_object == null || _object.WasCollected) return;
        var remaining = Remaining(now);
        var seconds = (int)Math.Ceiling(remaining);
        if (seconds >= 1 && seconds <= TickSeconds && seconds < _tickedSecond)
        {
            _tickedSecond = seconds;
            _mark?.Blink();
            OrdnanceCues.World(OrdnanceSound.BeaconTick, _object);
        }
        if (remaining <= 0f) _waitingSound?.Stop();
        Show(now);
        _marker?.Update();
    }

    internal ulong Thrower { get; }
    internal uint ThrowId { get; }
    private readonly OrdnanceDefinition _definition;

    /// <summary>The call-in's name in the current language.</summary>
    internal string Name => OrdnanceContent.Name(_definition);
    internal Vector3 Position { get; }
    internal float ArriveAt { get; }
    private float Remaining(float now) => Math.Max(0f, ArriveAt - now);

    internal void Destroy()
    {
        _waitingSound?.Stop();
        _marker?.Remove();
        _marker = null;
        _mark?.Remove();
        if (_object != null) Object.Destroy(_object);
    }
}
