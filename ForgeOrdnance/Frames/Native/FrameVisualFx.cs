using System;
using ForgeVisual;
using UnityEngine;

namespace ForgeOrdnance.Frames;

/// <summary>The suit's particle effects and its one effect light, from the record's own state (so every peer draws the same and
/// a peer that joins late sees only what is still going on: the burning wreck, the planted lamp, never a past landing or blast).
/// What to emit and when comes from <see cref="FrameEffectsPlan"/>; <see cref="FrameFx"/> draws. Nothing here is networked.</summary>
internal sealed partial class FrameVisual
{
    private const float EjectSeconds = 1.6f;

    private const float PulseWatchRange = 60f;
    private float _nextChargeFx, _nextWreckFlame, _nextWreckSmoke, _nextPulseWatch, _nextPulse;
    private float _ejectStart = -1f, _closeStart = -1f;
    private int _chargeTicks;
    /// <summary>This suit's own hold key for its burning wreck, and its charge trail (own look in its glow colour, own ribbon), so two suits side by side keep their own.</summary>
    private readonly int _wreckKey = Fx.NewKey();
    private Trail? _chargeTrail;
    private TrailStyle? _chargeStyle;
    private bool _kicked;
    private int _wreckLight, _lampLight;

    /// <summary>The suit's light colour (its definition's <c>Light.Glow</c>), passed to the effects that take it: thrusters, the charge flame, the landing and kick rings, the barrier's pulse, hit and break, the siege ring, the ram.</summary>
    internal Color SuitGlow { get { var l = _unit.Record.Spec.Light; return new Color(l.GlowR, l.GlowG, l.GlowB); } }

    /// <summary>Every frame of the fall: at the end of it the touchdown blast, once.</summary>
    private void ArrivalFx(float fall, Vector3 ground)
    {
        if (_kicked || fall < FrameEffectsPlan.KickFrom) return;
        _kicked = true;
        FrameFx.Kick(ground, SuitGlow);
    }

    private void LandFx(Vector3 ground) => FrameFx.Landing(ground, SuitGlow);

    private void SiegeFx() => FrameFx.Siege(_transform.position, SuitGlow);

    /// <summary>Every frame of a charge: the trail follows the suit's back; every <see cref="FrameEffectsPlan.ChargeInterval"/> dust behind the feet.</summary>
    private void ChargeFx(float now)
    {
        var t = _transform;
        var chest = _chest != null ? _chest.position : t.position + Vec.Up * 1.5f;
        FrameFx.ChargeTrail(ref _chargeTrail, _chargeStyle ??= FrameFx.ChargeStyle(SuitGlow), chest - t.forward * 0.45f, _pilotLocal);
        if (now < _nextChargeFx) return;
        _nextChargeFx = now + FrameEffectsPlan.ChargeInterval;
        if (_chargeTicks++ % FrameEffectsPlan.ChargeDustEvery == 0) FrameFx.ChargeDust(t.position);
    }

    /// <summary>A raised barrier's push seen on a client: the host shoves (and rings the pulse itself); the others look for the same
    /// enemies inside the dome four times a second and ring it when there are some. Not networked, so it is the same rule, not the same shove.</summary>
    private void WatchPush(FrameRecord r, float now)
    {
        if (ForgeCore.Session.IsHost || !FrameRules.BarrierUp(r) || now < _nextPulseWatch) return;
        _nextPulseWatch = now + FrameRepel.Interval;
        var local = ForgeCore.Players.Local;
        if (local == null || local.WasCollected) return;
        var feet = _transform.position;
        if (Vec.SqrLength(local.Position - feet) > PulseWatchRange * PulseWatchRange) return;
        if (now >= _nextPulse && FrameRepel.AnyInside(r, feet)) Pulse(now);
    }

    /// <summary>The barrier rings one push pulse (the host calls this when it really shoved someone).</summary>
    internal void Pulse(float now)
    {
        if (now < _nextPulse) return;
        _nextPulse = now + FrameEffectsPlan.PulseGap;
        FrameFx.Pulse(_transform.position, _unit.Record.Spec.Shield!.Radius, SuitGlow);
    }

    /// <summary>The barrier stopping an attack (<paramref name="hit"/>) or breaking: one short flash around the suit's body, none standing. Not on the
    /// pilot's own peer: it would fill the cockpit view, which shows the hit on its edge and the barrier on its line.</summary>
    private void BarrierFx(bool hit)
    {
        if (_pilotLocal) return;
        var t = _transform;
        FrameFx.Barrier(hit, _chest != null ? _chest.position : t.position + Vec.Up * 1.4f, SuitGlow);
    }

    private void ExplosionFx() => FrameFx.Explosion(_transform.position);

    /// <summary>A pilot forced out of a standing suit: its plates blow open (the leaving curve, in <see cref="EjectSeconds"/>) with a steam jet from each back plate.</summary>
    private void EjectFx(float now)
    {
        _ejectStart = now;
        var centre = _transform.position;
        var jet = -_transform.forward * 0.8f + Vec.Up * 0.3f;
        foreach (var plate in _backParts)
        {
            if (plate == null || plate.WasCollected) continue;
            var at = plate.position;
            var sideways = at - centre;
            sideways.y = 0f;
            FrameFx.Eject(OnBack(at), jet + sideways.normalized * 0.5f);
        }
    }

    /// <summary>The plates of a suit nobody wears: an ejection playing (the leaving sequence squeezed into <see cref="EjectSeconds"/>), the
    /// suit closing on its own after its pilot landed (<see cref="FrameSequence.ClosePlates"/>, with its sounds and steam), or shut.</summary>
    private void IdlePlates(float now)
    {
        if (_ejectStart >= 0f)
        {
            var t = (now - _ejectStart) / EjectSeconds;
            if (t < 1f) { Open(FrameSequence.EjectPlates(t, EjectSeconds, OpenLength, CloseLength)); return; }
            _ejectStart = -1f;
        }
        if (_closeStart >= 0f)
        {
            var pose = FrameSequence.ClosePlates(now - _closeStart, OpenLength, CloseLength);
            if (pose is { } closing) { Open(closing); PlateSounds(closing); return; }
            _closeStart = -1f;
        }
        Open(default);
    }

    /// <summary>The burning wreck: flames and black smoke over its body, dying down before it is removed, and its sinking at the end.</summary>
    private void WreckFx(float now, float age)
    {
        var life = FrameRules.WreckSeconds;
        var fire = FrameEffectsPlan.WreckFire(age, life);
        var flame = fire > 0.02f && now >= _nextWreckFlame;
        if (flame) _nextWreckFlame = now + FrameEffectsPlan.WreckFlameInterval / Math.Max(0.25f, fire);
        var smoke = FrameEffectsPlan.WreckSmoking(age, life) && now >= _nextWreckSmoke;
        if (smoke) _nextWreckSmoke = now + FrameEffectsPlan.WreckSmokeInterval;
        if (flame || smoke) FrameFx.Wreck(_wreckKey, _transform.position, flame, smoke);
    }

    /// <summary>The local pilot's sprint heat (the only peer that knows it): one wisp from both shoulders at the moment it overheats; the suit's glow shows the rest.</summary>
    internal void Overheat(bool overheated)
    {
        var entered = overheated && !_overheated;
        _overheated = overheated;
        if (!entered || _pose == null) return;
        FrameFx.Heat(_pose.Bone(FrameBodyRig.RightUpperArmLink).position + Vec.Up * 0.15f);
        FrameFx.Heat(_pose.Bone(FrameBodyRig.LeftUpperArmLink).position + Vec.Up * 0.15f);
    }

    private static readonly PointLightSpec WreckLight = new(new Color(1f, 0.52f, 0.2f), 5.5f, 1.1f, flicker: 0.25f);
    private static readonly PointLightSpec LampLight = new(new Color(1f, 0.12f, 0.08f), 2.5f, 1.1f, flicker: 0f);

    /// <summary>The held effect lights, ForgeVisual's game point lights: the wreck's flicker, or the planted siege lamp (the destruction flash
    /// is the light of the <c>frame.explode</c> slot, so ForgeVisual's <c>Effects / FlashLights</c> turns it off with every other flash; held
    /// lights stay, as ForgeVisual keeps them). The lamp tells the others the suit is planted; its own pilot's peer never lights it, because it sits
    /// half a metre from the pilot's eye and the game's fog turns it into a large red glow over the right of the view (the cockpit
    /// already shows the siege).</summary>
    private void TickFxLight(FrameRecord r, float now)
    {
        float fire = 0f, lamp = 0f;
        var at = default(Vector3);
        if (r.State == FrameState.Destroyed)
        {
            fire = FrameEffectsPlan.WreckFire(now - _stateStart, FrameRules.WreckSeconds);
            at = _transform.position + Vec.Up * 1.4f;
        }
        else if (r.Sieged && r.State == FrameState.Piloted && !_pilotLocal)
        {
            var t = _transform;
            lamp = 1f;
            at = (_chest != null ? _chest.position : t.position + Vec.Up * 1.6f) + t.up * 0.5f + t.right * 0.4f;
        }
        Drive(ref _wreckLight, fire, at, in WreckLight, now);
        Drive(ref _lampLight, lamp, at, in LampLight, now);
    }

    /// <summary>Holds the light at <paramref name="strength"/> of its spec while it is visible at all; lets it go otherwise.</summary>
    private static void Drive(ref int handle, float strength, Vector3 at, in PointLightSpec spec, float now)
    {
        if (strength * spec.Intensity > 0.01f) PointLights.Hold(ref handle, at, in spec, now, strength);
        else PointLights.Release(ref handle);
    }

    private void DestroyFx()
    {
        PointLights.Release(ref _wreckLight);
        PointLights.Release(ref _lampLight);
    }
}
