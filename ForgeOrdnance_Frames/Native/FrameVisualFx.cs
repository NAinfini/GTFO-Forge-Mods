using System;
using ForgeVisual;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>The suit's particle effects and its one effect light, from the record's own state (so every peer draws the same and
/// a peer that joins late sees only what is still going on: the burning wreck, the planted lamp, never a past landing or blast).
/// What to emit and when comes from <see cref="FrameEffectsPlan"/>; <see cref="FrameFx"/> draws. Nothing here is networked.</summary>
internal sealed partial class FrameVisual
{
    private const float EjectSeconds = 1.6f;

    private const float PulseWatchRange = 60f;
    private const float FirstPersonReach = 1.2f;
    private float _nextChargeFx, _nextWreckFlame, _nextWreckSmoke, _nextHeat, _nextPulseWatch, _nextPulse;
    private float _ejectStart = -1f, _closeStart = -1f;
    private int _chargeTicks;
    private bool _kicked;
    private Vector3 _chargeFrom;
    private float _chargeFromAt = -1f;
    private int _blastLight, _wreckLight, _lampLight;

    /// <summary>Every frame of the fall: thrusters on the back, jetting down, burning harder towards the ground.</summary>
    private void ArrivalFx(float fall, Vector3 ground)
    {
        var strength = FrameEffectsPlan.ArrivalThrust(fall);
        if (strength <= 0f) return;
        var t = _root.transform;
        var carry = Vector3.down * (FallHeight * 2f * fall / FallSeconds);
        var origin = t.position + Vector3.up * 1.5f - t.forward * 0.5f;
        FrameFx.Jet(origin + t.right * 0.3f, carry, Vector3.down, strength);
        FrameFx.Jet(origin - t.right * 0.3f, carry, Vector3.down, strength);
        if (_kicked || fall < FrameEffectsPlan.KickFrom) return;
        _kicked = true;
        FrameFx.Kick(ground);
    }

    private void LandFx(Vector3 ground) => FrameFx.Landing(ground);

    private void SiegeFx() => FrameFx.Siege(_root.transform.position);

    /// <summary>One charge tick (every <see cref="FrameEffectsPlan.ChargeInterval"/>): flame from the back, dust behind the feet.</summary>
    private void ChargeFx(float now)
    {
        if (now < _nextChargeFx) return;
        _nextChargeFx = now + FrameEffectsPlan.ChargeInterval;
        var t = _root.transform;
        var back = (_chest != null ? _chest.position : t.position + Vector3.up * 1.5f) - t.forward * 0.45f;
        // The suit's own velocity, from where it was at the last tick; the flame leaves on top of it.
        var carry = _chargeFromAt >= 0f && now - _chargeFromAt < 0.5f ? Vector3.ClampMagnitude((t.position - _chargeFrom) / (now - _chargeFromAt), 20f) : t.forward * 8f;
        _chargeFrom = t.position;
        _chargeFromAt = now;
        FrameFx.Charge(back, t.forward, carry, t.position, _chargeTicks++ % FrameEffectsPlan.ChargeDustEvery == 0, InOwnEyes(back));
    }

    /// <summary>The camera is the pilot's own and sits at the suit's eye, so the flame born at <paramref name="back"/> would reach it: the
    /// local pilot in first person. A camera further than <see cref="FirstPersonReach"/> from the flame (a third-person view) sees the
    /// flame like anyone else does.</summary>
    private bool InOwnEyes(Vector3 back)
    {
        if (!_pilotLocal) return false;
        var agent = Player.PlayerManager.GetLocalPlayerAgent();
        if (agent == null || agent.WasCollected || agent.FPSCamera == null) return false;
        return (agent.FPSCamera.transform.position - back).sqrMagnitude < FirstPersonReach * FirstPersonReach;
    }

    /// <summary>A raised barrier's push seen on a client: the host shoves (and rings the pulse itself); the others look for the same
    /// enemies inside the dome four times a second and ring it when there are some. Not networked, so it is the same rule, not the same shove.</summary>
    private void WatchPush(FrameRecord r, float now)
    {
        if (SNet.IsMaster || !FrameRules.BarrierUp(r) || now < _nextPulseWatch) return;
        _nextPulseWatch = now + FrameRepel.Interval;
        var local = Player.PlayerManager.GetLocalPlayerAgent();
        if (local == null || local.WasCollected || (local.Position - _root.transform.position).sqrMagnitude > PulseWatchRange * PulseWatchRange) return;
        if (now >= _nextPulse && FrameRepel.AnyInside(r, _root.transform.position)) Pulse(now);
    }

    /// <summary>The barrier rings one push pulse (the host calls this when it really shoved someone).</summary>
    internal void Pulse(float now)
    {
        if (now < _nextPulse) return;
        _nextPulse = now + FrameEffectsPlan.PulseGap;
        FrameFx.Pulse(_root.transform.position + Vector3.up * 1.2f, _unit.Record.Spec.Shield!.Radius);
    }

    private void ExplosionFx() => FrameFx.Explosion(_root.transform.position);

    /// <summary>A pilot forced out of a standing suit: its plates blow open (the leaving curve, in <see cref="EjectSeconds"/>) with a steam jet from each back plate.</summary>
    private void EjectFx(float now)
    {
        _ejectStart = now;
        var t = _root.transform;
        foreach (var plate in _backParts)
        {
            if (plate == null || plate.WasCollected) continue;
            var sideways = plate.position - t.position;
            sideways.y = 0f;
            FrameFx.Eject(plate.position, -t.forward * 0.8f + Vector3.up * 0.3f + sideways.normalized * 0.5f);
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
        if (flame || smoke) FrameFx.Wreck(_root.transform.position, flame, smoke);
    }

    /// <summary>The local pilot's sprint heat (the only peer that knows it): haze from both shoulders once it climbs.</summary>
    internal void Overheat(float heat, bool overheated, float now)
    {
        _overheated = overheated;
        var level = FrameEffectsPlan.HeatLevel(heat, overheated);
        if (level <= 0f || now < _nextHeat) return;
        _nextHeat = now + FrameEffectsPlan.HeatInterval(level);
        if (_pose == null) return;
        FrameFx.Heat(_pose.Bone(FrameBodyRig.RightUpperArmLink).position + Vector3.up * 0.15f);
        FrameFx.Heat(_pose.Bone(FrameBodyRig.LeftUpperArmLink).position + Vector3.up * 0.15f);
    }

    private static readonly PointLightSpec BlastLight = new(new Color(1f, 0.52f, 0.2f), 14f, 5f, flicker: 0f);
    private static readonly PointLightSpec WreckLight = new(new Color(1f, 0.52f, 0.2f), 5.5f, 1.1f, flicker: 0.25f);
    private static readonly PointLightSpec LampLight = new(new Color(1f, 0.12f, 0.08f), 2.5f, 1.1f, flicker: 0f);

    /// <summary>The effect lights, ForgeVisual's game point lights: a flash at the moment of destruction, then the wreck's flicker; or
    /// the planted siege lamp. The lamp tells the others the suit is planted; its own pilot's peer never lights it, because it sits
    /// half a metre from the pilot's eye and the game's fog turns it into a large red glow over the right of the view (the cockpit
    /// already shows the siege). All off whenever the player turns the extra lights off.</summary>
    private void TickFxLight(FrameRecord r, float now)
    {
        float blast = 0f, fire = 0f, lamp = 0f;
        var at = default(Vector3);
        if (Plugin.ExtraLights.Value)
        {
            if (r.State == FrameState.Destroyed)
            {
                var age = now - _stateStart;
                blast = FrameEffectsPlan.ExplosionFlash(age);
                fire = FrameEffectsPlan.WreckFire(age, FrameRules.WreckSeconds);
                at = _root.transform.position + Vector3.up * 1.4f;
            }
            else if (r.Sieged && r.State == FrameState.Piloted && !_pilotLocal)
            {
                var t = _root.transform;
                lamp = 1f;
                at = (_chest != null ? _chest.position : t.position + Vector3.up * 1.6f) + t.up * 0.5f + t.right * 0.4f;
            }
        }
        Drive(ref _blastLight, blast, at, in BlastLight, now);
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
        PointLights.Release(ref _blastLight);
        PointLights.Release(ref _wreckLight);
        PointLights.Release(ref _lampLight);
    }
}
