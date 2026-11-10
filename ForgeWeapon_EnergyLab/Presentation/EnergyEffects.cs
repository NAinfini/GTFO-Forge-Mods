using System;
using ForgeAudio;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>Audio for resolved weapon effects; native animations own handling sounds.</summary>
internal sealed class EnergyEffects : IDisposable
{
    private const int Capacity = EnergyLimits.ArcTargets;
    /// <summary>The waiting loop after full charge sits under the rise's last moments, so the full ping stands out
    /// and a long hold does not tire.</summary>
    private const float ChargeHoldGain = 0.6f;
    // The beam's start, hold and stop overlap: the hold fades in under the start's tail and fades out under the stop.
    private const float BeamLoopEntry = 0.23f, BeamLoopFadeIn = 0.2f, BeamLoopFadeOut = 0.18f;
    private const string BeamLoop = "beam-loop", BeamStart = "beam-start", BeamStop = "beam-stop", BeamHit = "beam-hit",
        Arc = "arc-fire", ArcLoop = "arc-loop", ArcHit = "arc-hit", ChargeRise = "plasma-charge-rise",
        ChargeHold = "plasma-charge-loop", ChargeFull = "plasma-charge-full", PlasmaLaunch = "orb-fire", ParcLaunch = "shock-fire",
        ChargeCancel = "plasma-charge-cancel";
    private readonly GameObject _root;
    private readonly Vector3[] _ends = new Vector3[Capacity];
    private readonly bool[] _hits = new bool[Capacity];
    private readonly AudioVoice _loop, _beamEvent, _beamStop, _beamHit, _arcEvent, _arcLoop, _chargeRise, _chargeHold, _chargeFull;
    private readonly AudioVoice[] _arcHits = new AudioVoice[Capacity];
    private readonly AudioVoice[] _launchVoices = new AudioVoice[4];
    private int _launchCursor;
    private float _arcStarted, _arcLifetime, _arcHopDelay;
    private float _beamStarted;
    private float _beamLoopGain, _beamReleaseGain;
    // When the released beam's hold began fading: -1 none, +infinity released and starting on the next Tick.
    private float _beamReleased = -1f;
    private int _segments;
    private bool _beaming;
    private bool _beamWasHit;
    private bool _charging;
    private bool _chargedFull;
    private float _nextBeamHit;
    private int _arcHitMask;
    private bool _disposed;
    private bool _arcHeld;

    /// <summary>The sounds of the gun in the hand; the owner sets it when the gun changes.</summary>
    internal EnergyLook Look { get; set; } = EnergyLook.Default;

    public EnergyEffects()
    {
        _root = new GameObject("Energy weapon effects");
        Object.DontDestroyOnLoad(_root);
        var anchor = _root.transform;
        _loop = EnergyAudio.Library.Voice(anchor);
        _beamEvent = EnergyAudio.Library.Voice(anchor);
        _beamStop = EnergyAudio.Library.Voice(anchor);
        _beamHit = EnergyAudio.Library.Voice(Child("beam-hit-voice"));
        _arcEvent = EnergyAudio.Library.Voice(anchor);
        _arcLoop = EnergyAudio.Library.Voice(anchor);
        for (var index = 0; index < _launchVoices.Length; index++) _launchVoices[index] = EnergyAudio.Library.Voice(anchor);
        _chargeRise = EnergyAudio.Library.Voice(anchor);
        _chargeHold = EnergyAudio.Library.Voice(anchor);
        _chargeFull = EnergyAudio.Library.Voice(anchor);
        for (var index = 0; index < Capacity; index++) _arcHits[index] = EnergyAudio.Library.Voice(Child("arc-hit-voice-" + index));
    }

    private Transform Child(string name)
    {
        var child = new GameObject(name).transform;
        child.SetParent(_root.transform, false);
        return child;
    }

    public void Beam(Vector3 start, Vector3 end, bool hit, float now)
    {
        _segments = 0;
        _root.transform.position = start;
        if (!_beaming)
        {
            _beamEvent.Play(Look.Cue(BeamStart), 0.7f);
            _beamStarted = now;
            _beaming = true;
            _beamReleased = -1f;
            if (_loop.Playing) _loop.Stop();
        }
        var introDone = now - _beamStarted >= BeamLoopEntry;
        if (introDone && hit && (!_beamWasHit || now >= _nextBeamHit) && !_beamHit.Playing)
        {
            _beamHit.Anchor.position = end;
            _beamHit.Play(Look.Cue(BeamHit), 0.33f);
            _nextBeamHit = now + 0.16f;
        }
        _beamWasHit = hit;
        if (introDone)
        {
            _beamLoopGain = (hit ? 0.63f : 0.55f) * Mathf.Clamp01((now - _beamStarted - BeamLoopEntry) / BeamLoopFadeIn);
            _loop.Hold(Look.Cue(BeamLoop), _beamLoopGain);
        }
    }

    /// <summary>Charging: the rise plays once from the first held frame and is timed to reach its top as the charge
    /// becomes full. At full the full ping sounds once and a waiting loop that continues the rise's hum takes over
    /// until launch or cancel.</summary>
    public void Charge(float power)
    {
        if (!_charging)
        {
            _charging = true;
            _chargedFull = false;
            _chargeFull.Stop();
            _chargeRise.Play(Look.Cue(ChargeRise), 0.95f);
        }
        if (power >= 1f && !_chargedFull)
        {
            _chargedFull = true;
            _chargeRise.Stop();
            _chargeFull.Play(Look.Cue(ChargeFull), 0.9f);
            _chargeHold.Hold(Look.Cue(ChargeHold), ChargeHoldGain);
        }
    }

    public void Charge(float power, Vector3 position)
    {
        _root.transform.position = position;
        Charge(power);
    }

    public void Discharge(bool arcOrb, Vector3 position)
    {
        StopCharge();
        _root.transform.position = position;
        _launchVoices[_launchCursor].Play(Look.Cue(arcOrb ? ParcLaunch : PlasmaLaunch), 0.8f);
        _launchCursor = (_launchCursor + 1) % _launchVoices.Length;
    }

    public void StopCharge()
    {
        if (!_charging) return;
        _charging = _chargedFull = false;
        _chargeRise.Stop();
        _chargeHold.Stop();
        _chargeFull.Stop();
    }

    public void CancelCharge()
    {
        if (!_charging) return;
        StopCharge();
        _chargeFull.Play(Look.Cue(ChargeCancel), 0.45f);
    }

    public void Chain(ReadOnlySpan<Vector3> starts, ReadOnlySpan<Vector3> ends, ReadOnlySpan<bool> hits, float now, float lifetime, float hopDelay)
    {
        if (starts.Length < 1 || starts.Length > Capacity || ends.Length != starts.Length || hits.Length != starts.Length)
            throw new ArgumentException("Invalid chain presentation segments.");
        ends.CopyTo(_ends);
        hits.CopyTo(_hits);
        _segments = starts.Length;
        _arcStarted = now; _arcLifetime = lifetime; _arcHopDelay = hopDelay;
        _arcHitMask = 0;
        _root.transform.position = starts[0];
        _arcEvent.Play(Look.Cue(Arc), 0.82f);
        Tick(now);
    }

    public void ArcHold(bool held)
    {
        if (held == _arcHeld) return;
        _arcHeld = held;
        if (held) _arcLoop.Hold(Look.Cue(ArcLoop), 0.5f);
        else _arcLoop.Stop();
    }

    /// <summary>Plays each chain hop's hit cue when its arc reaches the target, one hop delay after the last.</summary>
    public void Tick(float now)
    {
        if (_beamReleased >= 0f)
        {
            if (float.IsPositiveInfinity(_beamReleased)) _beamReleased = now;
            var fade = (now - _beamReleased) / BeamLoopFadeOut;
            if (fade >= 1f || !_loop.Playing) { _loop.Stop(); _beamReleased = -1f; }
            else _loop.SetGain(_beamReleaseGain * (1f - fade));
        }
        if (_segments == 0) return;
        var age = now - _arcStarted;
        if (age >= _arcLifetime + (_segments - 1) * _arcHopDelay) { _segments = 0; return; }
        for (var index = 0; index < _segments; index++)
        {
            if (age < index * _arcHopDelay || !_hits[index] || (_arcHitMask & (1 << index)) != 0) continue;
            _arcHitMask |= 1 << index;
            _arcHits[index].Anchor.position = _ends[index];
            _arcHits[index].Play(Look.Cue(ArcHit), index == 0 ? 0.34f : 0.22f);
        }
    }

    public void Hide()
    {
        if (_segments == 0 && !_arcHeld && !_charging && !_beaming) return;
        _segments = 0;
        if (_arcHeld) _arcLoop.Stop();
        _arcHeld = false;
        if (_charging) StopCharge();
        if (_beaming && !_disposed)
        {
            // The start keeps its tail; the hold fades out in Tick while the stop plays over it.
            _beamStop.Play(Look.Cue(BeamStop), 0.55f);
            if (_loop.Playing) { _beamReleased = float.PositiveInfinity; _beamReleaseGain = _beamLoopGain; }
        }
        _beaming = false;
        _beamWasHit = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The voices ride on the root's children; end their sounds before the root goes.
        _loop.Stop(); _beamEvent.Stop(); _beamStop.Stop(); _beamHit.Stop(); _arcEvent.Stop(); _arcLoop.Stop();
        _chargeRise.Stop(); _chargeHold.Stop(); _chargeFull.Stop();
        foreach (var voice in _launchVoices) voice.Stop();
        foreach (var voice in _arcHits) voice.Stop();
        if (_root != null) Object.Destroy(_root);
    }
}
