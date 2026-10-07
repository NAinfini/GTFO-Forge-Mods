using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>Audio for resolved weapon effects; native animations own handling sounds.</summary>
internal sealed class EnergyEffects : IDisposable
{
    private const int Capacity = EnergyLimits.ArcTargets;
    /// <summary>The waiting loop after full charge sits under the rise's last moments, so the full ping stands out
    /// and a long hold does not tire.</summary>
    private const float ChargeHoldGain = 0.6f;
    // The beam's start, hold and stop overlap: the hold fades in under the start's tail and fades out under the stop.
    private const float BeamLoopEntry = 0.23f, BeamLoopFadeIn = 0.2f, BeamLoopFadeOut = 0.18f;
    private const int BeamLoop = 0, BeamStart = 1, BeamStop = 2, BeamHit = 3, Arc = 4, ArcLoop = 5, ArcHit = 6,
        ChargeRise = 7, ChargeHold = 8, ChargeFull = 9, PlasmaLaunch = 10, ParcLaunch = 11,
        ChargeCancel = 12;
    private static readonly string[] ClipNames = { "beam-loop", "beam-start", "beam-stop", "beam-hit",
        "arc-fire", "arc-loop", "arc-hit", "plasma-charge-rise", "plasma-charge-loop", "plasma-charge-full", "orb-fire", "shock-fire",
        "plasma-charge-cancel" };
    private readonly GameObject _root;
    private readonly Vector3[] _ends = new Vector3[Capacity];
    private readonly bool[] _hits = new bool[Capacity];
    private AudioSource _loop = null!;
    private AudioSource _beamEvent = null!;
    private AudioSource _beamStop = null!;
    private AudioSource _beamHit = null!;
    private AudioSource _arcEvent = null!;
    private AudioSource _arcLoop = null!;
    private readonly AudioSource[] _arcHits = new AudioSource[Capacity];
    private readonly AudioSource[] _launchVoices = new AudioSource[4];
    private int _launchCursor;
    private AudioSource _chargeRise = null!;
    private AudioSource _chargeHold = null!;
    private AudioSource _chargeFull = null!;
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

    public EnergyEffects()
    {
        _root = new GameObject("Energy weapon effects");
        Object.DontDestroyOnLoad(_root);
        try
        {
            _loop = EnergySound.Voice(_root.transform);
            _beamEvent = EnergySound.Voice(_root.transform);
            _beamStop = EnergySound.Voice(_root.transform);
            _beamHit = EnergySound.Voice(_root.transform, "beam-hit-voice");
            _arcEvent = EnergySound.Voice(_root.transform);
            _arcLoop = EnergySound.Voice(_root.transform);
            for (var index = 0; index < _launchVoices.Length; index++) _launchVoices[index] = EnergySound.Voice(_root.transform);
            _chargeRise = EnergySound.Voice(_root.transform);
            _chargeHold = EnergySound.Voice(_root.transform);
            _chargeFull = EnergySound.Voice(_root.transform);
            for (var index = 0; index < Capacity; index++) _arcHits[index] = EnergySound.Voice(_root.transform, "arc-hit-voice-" + index);
        }
        catch { Dispose(); throw; }
    }

    public void Beam(Vector3 start, Vector3 end, bool hit, float now)
    {
        _segments = 0;
        _root.transform.position = start;
        if (!_beaming)
        {
            Play(_beamEvent, BeamStart, 0.7f);
            _beamStarted = now;
            _beaming = true;
            _beamReleased = -1f;
            if (_loop.isPlaying) EnergySound.Stop(_loop);
        }
        var introDone = now - _beamStarted >= BeamLoopEntry;
        if (introDone && hit && (!_beamWasHit || now >= _nextBeamHit) && !_beamHit.isPlaying)
        {
            _beamHit.transform.position = end;
            Play(_beamHit, BeamHit, 0.33f);
            _nextBeamHit = now + 0.16f;
        }
        _beamWasHit = hit;
        if (introDone)
        {
            _beamLoopGain = (hit ? 0.63f : 0.55f) * Mathf.Clamp01((now - _beamStarted - BeamLoopEntry) / BeamLoopFadeIn);
            Hold(_loop, BeamLoop, _beamLoopGain);
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
            EnergySound.Stop(_chargeFull);
            Play(_chargeRise, ChargeRise, 0.95f);
        }
        if (power >= 1f && !_chargedFull)
        {
            _chargedFull = true;
            EnergySound.Stop(_chargeRise);
            Play(_chargeFull, ChargeFull, 0.9f);
            Hold(_chargeHold, ChargeHold, ChargeHoldGain);
        }
    }

    public void Charge(float power, Vector3 position)
    {
        _root.transform.position = position;
        Charge(power);
    }

    private void Hold(AudioSource voice, int clip, float volume)
    {
        if (!EnergySound.PlayingCue(voice, ClipNames[clip])) EnergySound.Stop(voice);
        voice.pitch = 1f;
        if (EnergySound.PlayingCue(voice, ClipNames[clip])) EnergySound.Gain(voice, volume);
        else EnergySound.PlayCue(voice, ClipNames[clip], volume);
    }

    private void Play(AudioSource source, int index, float volume)
    {
        EnergySound.Stop(source);
        source.pitch = 1f;
        EnergySound.PlayCue(source, ClipNames[index], volume);
    }

    public void Discharge(bool arcOrb, Vector3 position)
    {
        StopCharge();
        _root.transform.position = position;
        Play(_launchVoices[_launchCursor], arcOrb ? ParcLaunch : PlasmaLaunch, 0.8f);
        _launchCursor = (_launchCursor + 1) % _launchVoices.Length;
    }

    public void StopCharge()
    {
        if (!_charging) return;
        _charging = _chargedFull = false;
        EnergySound.Stop(_chargeRise);
        EnergySound.Stop(_chargeHold);
        EnergySound.Stop(_chargeFull);
    }

    public void CancelCharge()
    {
        if (!_charging) return;
        StopCharge();
        Play(_chargeFull, ChargeCancel, 0.45f);
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
        Play(_arcEvent, Arc, 0.82f);
        Tick(now);
    }

    public void ArcHold(bool held)
    {
        if (held == _arcHeld) return;
        _arcHeld = held;
        if (held) Hold(_arcLoop, ArcLoop, 0.5f);
        else if (_arcLoop.isPlaying) EnergySound.Stop(_arcLoop);
    }

    /// <summary>Plays each chain hop's hit cue when its arc reaches the target, one hop delay after the last.</summary>
    public void Tick(float now)
    {
        if (_beamReleased >= 0f)
        {
            if (float.IsPositiveInfinity(_beamReleased)) _beamReleased = now;
            var fade = (now - _beamReleased) / BeamLoopFadeOut;
            if (fade >= 1f || !_loop.isPlaying) { EnergySound.Stop(_loop); _beamReleased = -1f; }
            else EnergySound.Gain(_loop, _beamReleaseGain * (1f - fade));
        }
        if (_segments == 0) return;
        var age = now - _arcStarted;
        if (age >= _arcLifetime + (_segments - 1) * _arcHopDelay) { _segments = 0; return; }
        for (var index = 0; index < _segments; index++)
        {
            if (age < index * _arcHopDelay || !_hits[index] || (_arcHitMask & (1 << index)) != 0) continue;
            _arcHitMask |= 1 << index;
            _arcHits[index].transform.position = _ends[index];
            Play(_arcHits[index], ArcHit, index == 0 ? 0.34f : 0.22f);
        }
    }

    public void Hide()
    {
        if (_segments == 0 && !_arcHeld && !_charging && !_beaming) return;
        _segments = 0;
        if (_arcHeld && _arcLoop != null) EnergySound.Stop(_arcLoop);
        _arcHeld = false;
        if (_charging) StopCharge();
        if (_beaming && !_disposed)
        {
            // The start keeps its tail; the hold fades out in Tick while the stop plays over it.
            Play(_beamStop, BeamStop, 0.55f);
            if (_loop.isPlaying) { _beamReleased = float.PositiveInfinity; _beamReleaseGain = _beamLoopGain; }
        }
        _beaming = false;
        _beamWasHit = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Destroying the root owns every audio voice. Avoid touching child native components during scene
        // teardown, when Unity may have invalidated them.
        if (_root != null) Object.Destroy(_root);
    }
}
