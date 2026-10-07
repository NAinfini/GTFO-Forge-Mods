using System;
using System.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>Held orbs with independent one-shot discharges and detonations. Native code supplies contacts.</summary>
internal sealed class PlasmaEffects : IDisposable
{
    private const string OrbMember = "forge_plasma_orb", DischargeMember = "forge_plasma_discharge", ExplosionMember = "forge_plasma_explosion";
    /// <summary>The orb prefab's state children (inactive by default) for each phase.</summary>
    private const string ChargingState = "charging", ChargedState = "charged", LaunchState = "launch", StormState = "storm",
        FizzleState = "fizzle";
    /// <summary>A detonation is drawn this far back along the orb's approach, so the blast does not sink into the
    /// wall it hit.</summary>
    private const float ExplosionStandOff = 0.3f;
    private static readonly LightSpec OrbLight = new(new Color(0.25f, 0.55f, 1f), 4f, 1.4f, flicker: 0.25f);
    private static readonly LightSpec ElectricOrbLight = new(new Color(0.5f, 0.35f, 1f), 4f, 1.4f, flicker: 0.45f);
    private static readonly LightSpec ChargedLight = new(new Color(0.3f, 0.6f, 1f), 3f, 2.5f, 0.2f);
    private static readonly LightSpec LaunchLight = new(new Color(0.3f, 0.6f, 1f), 5f, 3f, 0.15f);
    private static readonly LightSpec DischargeLight = new(new Color(0.55f, 0.4f, 1f), 3f, 2f, 0.12f, 0.5f);
    private static readonly LightSpec ExplosionLight = new(new Color(0.35f, 0.6f, 1f), 7f, 3f, 0.55f, 0.2f);
    /// <summary>The members' default sizes (the prefabs are authored at them): orb radius and blast radius.</summary>
    private const float OrbSize = 0.3f, ExplosionSize = 3f;
    /// <summary>The PlasmaArc variant's orb: the blue orb shifted toward violet.</summary>
    private static readonly Color ElectricTint = new(1f, 0.7f, 1f);

    private readonly VfxPool _pool = new("Plasma ionization effects");
    private readonly VfxInstance?[] _orbs;
    private readonly bool[] _exploded;
    private readonly Vector3[] _positions;
    private readonly bool[] _electric;
    private readonly bool[] _flying;
    private readonly int[] _lights;
    private readonly GameObject _voices;
    /// <summary>Each orb slot's flight hum, following its orb while it flies.</summary>
    private readonly AudioSource[] _flightVoices;
    private readonly AudioSource[] _impactVoices = new AudioSource[4];
    private readonly AudioSource[] _arcVoices = new AudioSource[6];
    private int _impactCursor, _arcCursor;
    private bool _disposed, _prepared;

    public PlasmaEffects(int capacity)
    {
        _orbs = new VfxInstance?[capacity];
        _exploded = new bool[capacity];
        _positions = new Vector3[capacity];
        _electric = new bool[capacity];
        _flying = new bool[capacity];
        _lights = new int[capacity];
        _flightVoices = new AudioSource[capacity];
        _voices = new GameObject("Plasma ionization voices");
        Object.DontDestroyOnLoad(_voices);
        try
        {
            // Load the prefabs up front, so a missing bundle fails the weapon mode when it is chosen, not mid-flight.
            VfxLibrary.Prefab(OrbMember);
            VfxLibrary.Prefab(DischargeMember);
            VfxLibrary.Prefab(ExplosionMember);
            for (var index = 0; index < _flightVoices.Length; index++)
                _flightVoices[index] = EnergySound.Voice(_voices.transform, "plasma-flight-voice-" + index);
            for (var index = 0; index < _impactVoices.Length; index++)
                _impactVoices[index] = EnergySound.Voice(_voices.transform, "plasma-impact-voice-" + index);
            for (var index = 0; index < _arcVoices.Length; index++)
                _arcVoices[index] = EnergySound.Voice(_voices.transform, "plasma-arc-voice-" + index);
        }
        catch { Dispose(); throw; }
    }

    private static bool Alive(Object? item) => item is not null && item != null;
    private bool Ready => !_disposed;

    internal IEnumerator Prepare()
    {
        if (_prepared) yield break;
        // Each tint has flying/charging slots plus fading copies from the preceding release.
        foreach (var step in _pool.Prepare(OrbMember, _orbs.Length * 2)) yield return step;
        foreach (var step in _pool.Prepare(OrbMember, _orbs.Length * 2, ElectricTint)) yield return step;
        foreach (var step in _pool.Prepare(DischargeMember, _orbs.Length * EnergyLimits.PulseTargets * 2)) yield return step;
        foreach (var step in _pool.Prepare(ExplosionMember, _orbs.Length * 2)) yield return step;
        _prepared = true;
    }

    /// <summary>A smaller muzzle preview leaves the first-person sight line clear. Flight uses full payload size.</summary>
    public void ChargingOrb(int index, Vector3 position, float radius, float now, bool electric)
        => Orb(index, position, radius * 0.55f, now, electric, flying: false);

    /// <summary>The orb of slot <paramref name="index"/>. A flying orb hums as it goes;
    /// an orb still charging at the muzzle has no flight sound.</summary>
    public void Orb(int index, Vector3 position, float radius, float now, bool electric, bool flying)
    {
        if (!Ready) return;
        Draw(index, position, radius, electric, flying, now);
        if (!flying) return;
        var voice = _flightVoices[index];
        if (!Alive(voice)) return;
        voice.transform.position = position;
        if (voice.isPlaying) return;
        voice.pitch = 1f;
        EnergySound.PlayCue(voice, electric ? "shock-fly-loop" : "orb-fly-loop", .85f);
    }

    /// <summary>The arc orb running out or stopped by a wall: its hum ends and it fades with a crackle.</summary>
    public void Fade(int index, Vector3 position)
    {
        if (!Ready) return;
        StopFlight(index);
        _orbs[index]?.PlayState(FizzleState);
        EnergySound.PlaySpatial(_arcVoices, ref _arcCursor, "shock-fade", position, .5f);
    }

    private void StopFlight(int index)
    {
        var voice = _flightVoices[index];
        if (Alive(voice) && voice.isPlaying) EnergySound.Stop(voice);
    }

    public void Contact(Vector3 position)
    {
        if (Ready) EnergySound.PlaySpatial(_arcVoices, ref _arcCursor, "shock-hit", position, .22f);
    }

    /// <summary>An orb running out: it shrinks to a tenth of its radius as <paramref name="progress"/> reaches 1.</summary>
    public void Dissipate(int index, Vector3 position, float radius, float progress, float now)
    {
        if (!Ready) return;
        Draw(index, position, radius * (1f - Mathf.Clamp01(progress) * 0.9f), electric: true, flying: true, now);
    }

    // The slot latch prevents duplicate detonations until the next orb starts.
    public void Explosion(int index, Vector3 position, float blastRadius, float now)
    {
        if (!Ready || _exploded[index]) return;
        _exploded[index] = true;
        StopFlight(index);
        var approach = _orbs[index] != null ? _positions[index] - position : Vector3.zero;
        ReleaseOrb(index);
        if (approach.sqrMagnitude > 0.000001f) position += approach.normalized * ExplosionStandOff;
        var blast = _pool.Take(ExplosionMember);
        blast.Place(position, Quaternion.identity, Mathf.Max(0.1f, blastRadius / ExplosionSize));
        blast.Start();
        EnergyLights.Flash(position, ExplosionLight, now, Mathf.Clamp(blastRadius / ExplosionSize, 0.6f, 1.4f));
    }

    private void Draw(int index, Vector3 position, float radius, bool electric, bool flying, float now)
    {
        _exploded[index] = false;
        if (_orbs[index] is { } held && _electric[index] != electric) { held.Release(); _orbs[index] = null; }
        var appeared = _orbs[index] == null;
        var orb = _orbs[index] ??= _pool.Take(OrbMember, electric ? ElectricTint : null).Hold();
        _electric[index] = electric;
        _positions[index] = position;
        var scale = Mathf.Max(0.02f, radius / OrbSize);
        orb.Place(position, Quaternion.identity, scale);
        orb.Keep();
        if (appeared) _flying[index] = false;
        if (appeared && !flying) orb.PlayState(ChargingState);
        if (flying && !_flying[index])
        {
            _flying[index] = true;
            orb.PlayState(LaunchState);
            if (electric) orb.PlayState(StormState);
            EnergyLights.Flash(position, LaunchLight, now, Mathf.Clamp(scale, 0.5f, 1.2f));
        }
        EnergyLights.Hold(ref _lights[index], position, electric ? ElectricOrbLight : OrbLight, now, Mathf.Clamp(scale, 0.3f, 1.2f));
    }

    public void Pulse(int index, ReadOnlySpan<Vector3> targets, float now)
    {
        if (!Ready) return;
        if (targets.Length < 1 || targets.Length > EnergyLimits.PulseTargets) throw new ArgumentException("Invalid orb pulse targets.");
        var start = _positions[index];
        EnergySound.PlaySpatial(_arcVoices, ref _arcCursor, "shock-pulse", start, .22f);
        EnergyLights.Flash(start, DischargeLight, now);
        foreach (var target in targets)
        {
            var bolt = _pool.Take(DischargeMember);
            bolt.Stretch(start, target);
            bolt.Start();
        }
    }

    /// <summary>The orb in slot <paramref name="index"/> reached full charge: its `charged` state (a flash, an expanding
    /// ring and a slow pulse) plays beside `charging` until the orb is launched or cancelled.</summary>
    public void Charged(int index)
    {
        if (!Ready || _orbs[index] is not { } orb) return;
        orb.PlayState(ChargedState);
        EnergyLights.Flash(_positions[index], ChargedLight, Time.time);
    }

    private void ReleaseOrb(int index)
    {
        _orbs[index]?.Release();
        _orbs[index] = null;
        _flying[index] = false;
        EnergyLights.Release(ref _lights[index]);
    }

    /// <summary>The slot is free: its orb stops emitting and fades out; a running detonation finishes.</summary>
    public void Hide(int index)
    {
        if (!Ready) return;
        StopFlight(index);
        ReleaseOrb(index);
        _exploded[index] = false;
    }

    public void Boom(Vector3 position)
    {
        if (!Ready) return;
        EnergySound.PlaySpatial(_impactVoices, ref _impactCursor, "orb-explode", position, .48f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var voice in _impactVoices) if (Alive(voice)) EnergySound.Stop(voice);
        foreach (var voice in _arcVoices) if (Alive(voice)) EnergySound.Stop(voice);
        foreach (var voice in _flightVoices) if (Alive(voice)) EnergySound.Stop(voice);
        _pool.Dispose();
        if (Alive(_voices)) Object.Destroy(_voices);
    }
}
