using System;
using System.Collections;
using ForgeAudio;
using ForgeVisual;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>Held orbs with independent one-shot discharges and detonations. Native code supplies contacts.</summary>
internal sealed class PlasmaEffects : IDisposable
{
    /// <summary>The orb prefab's state children (inactive by default) for each phase.</summary>
    private const string ChargingState = "charging", ChargedState = "charged", LaunchState = "launch", StormState = "storm",
        FizzleState = "fizzle";
    /// <summary>A detonation is drawn this far back along the orb's approach, so the blast does not sink into the
    /// wall it hit.</summary>
    private const float ExplosionStandOff = 0.3f;
    private static readonly PointLightSpec OrbLight = new(new Color(0.25f, 0.55f, 1f), 4f, 1.4f, flicker: 0.25f);
    private static readonly PointLightSpec ElectricOrbLight = new(new Color(0.5f, 0.35f, 1f), 4f, 1.4f, flicker: 0.45f);
    private static readonly PointLightSpec ChargedLight = new(new Color(0.3f, 0.6f, 1f), 3f, 2.5f, 0.2f);
    private static readonly PointLightSpec LaunchLight = new(new Color(0.3f, 0.6f, 1f), 5f, 3f, 0.15f);
    private static readonly PointLightSpec DischargeLight = new(new Color(0.55f, 0.4f, 1f), 3f, 2f, 0.12f, 0.5f);
    private static readonly PointLightSpec ExplosionLight = new(new Color(0.35f, 0.6f, 1f), 7f, 3f, 0.55f, 0.2f);
    /// <summary>The members' default sizes (the prefabs are authored at them): orb radius and blast radius.</summary>
    private const float OrbSize = 0.3f, ExplosionSize = 3f;
    /// <summary>A sighted preview's reach in orb radii: the plasma core and its surface arcs, once the layers that reach
    /// past them are muted.</summary>
    private const float SightedReach = 1.1f;
    /// <summary>The gap a sighted preview's edge keeps below the sight line, in metres.</summary>
    private const float SightMargin = 0.01f;
    /// <summary>The orb's own layers that reach past its core: the ionised shell and the shed motes.</summary>
    private static readonly string[] BeyondCore = { "shell", "motes" };
    /// <summary>The PlasmaArc variant's orb: the blue orb shifted toward violet.</summary>
    private static readonly Color ElectricTint = new(1f, 0.7f, 1f);

    private readonly FxInstancePool _pool = new("Plasma ionization effects");
    private readonly FxInstance?[] _orbs;
    private readonly bool[] _exploded;
    private readonly Vector3[] _positions;
    private readonly bool[] _electric;
    private readonly bool[] _flying;
    /// <summary>Slots whose orb is the local player's first-person charge preview.</summary>
    private readonly bool[] _sighted;
    private readonly int[] _lights;
    private readonly GameObject _voices;
    /// <summary>Each orb slot's flight hum, following its orb while it flies.</summary>
    private readonly AudioVoice[] _flightVoices;
    private bool _disposed, _prepared;

    public PlasmaEffects(int capacity)
    {
        _orbs = new FxInstance?[capacity];
        _exploded = new bool[capacity];
        _positions = new Vector3[capacity];
        _electric = new bool[capacity];
        _flying = new bool[capacity];
        _sighted = new bool[capacity];
        _lights = new int[capacity];
        _flightVoices = new AudioVoice[capacity];
        _voices = new GameObject("Plasma ionization voices");
        Object.DontDestroyOnLoad(_voices);
        for (var index = 0; index < _flightVoices.Length; index++)
        {
            var anchor = new GameObject("plasma-flight-voice-" + index).transform;
            anchor.SetParent(_voices.transform, false);
            _flightVoices[index] = EnergyAudio.Library.Voice(anchor);
        }
    }

    private bool Ready => !_disposed;

    internal IEnumerator Prepare()
    {
        if (_prepared) yield break;
        // Each tint has flying/charging slots plus fading copies from the preceding release.
        foreach (var step in _pool.Prepare(EnergyVfx.Orb, _orbs.Length * 2)) yield return step;
        foreach (var step in _pool.Prepare(EnergyVfx.Orb, _orbs.Length * 2, ElectricTint)) yield return step;
        foreach (var step in _pool.Prepare(EnergyVfx.Discharge, _orbs.Length * EnergyLimits.PulseTargets * 2)) yield return step;
        foreach (var step in _pool.Prepare(EnergyVfx.PlasmaExplosion, _orbs.Length * 2)) yield return step;
        _prepared = true;
    }

    /// <summary>The orb charging at the muzzle, drawn smaller than its payload; flight uses the full size. For the local
    /// player's first-person view, <paramref name="sight"/> is the look camera's ray: the preview is kept below it however
    /// close the optic sits to the bore, and the layers and states that reach past the core are not drawn, so the target
    /// stays visible through the sight. Other players see the whole charge.</summary>
    public void ChargingOrb(int index, Vector3 position, float radius, float now, bool electric, Ray? sight)
    {
        if (!Ready) return;
        var preview = radius * 0.55f;
        if (sight is { } line)
        {
            var offset = position - line.origin;
            var clearance = (offset - line.direction * Vector3.Dot(offset, line.direction)).magnitude;
            preview = Mathf.Min(preview, Mathf.Max(0f, clearance - SightMargin) / SightedReach);
        }
        Draw(index, position, preview, electric, flying: false, sighted: sight.HasValue, now);
    }

    /// <summary>The orb of slot <paramref name="index"/>. A flying orb hums as it goes;
    /// an orb still charging at the muzzle has no flight sound.</summary>
    public void Orb(int index, Vector3 position, float radius, float now, bool electric, bool flying)
    {
        if (!Ready) return;
        Draw(index, position, radius, electric, flying, sighted: false, now);
        if (!flying) return;
        var voice = _flightVoices[index];
        voice.Anchor.position = position;
        if (voice.Playing) return;
        voice.Play(electric ? "shock-fly-loop" : "orb-fly-loop", .85f);
    }

    /// <summary>The arc orb running out or stopped by a wall: its hum ends and it fades with a crackle.</summary>
    public void Fade(int index, Vector3 position)
    {
        if (!Ready) return;
        StopFlight(index);
        _orbs[index]?.PlayState(FizzleState);
        EnergyAudio.PlayAt("shock-fade", position, .5f);
    }

    private void StopFlight(int index) => _flightVoices[index].Stop();

    public void Contact(Vector3 position)
    {
        if (Ready) EnergyAudio.PlayAt("shock-hit", position, .22f);
    }

    /// <summary>An orb running out: it shrinks to a tenth of its radius as <paramref name="progress"/> reaches 1.</summary>
    public void Dissipate(int index, Vector3 position, float radius, float progress, float now)
    {
        if (!Ready) return;
        Draw(index, position, radius * (1f - Mathf.Clamp01(progress) * 0.9f), electric: true, flying: true, sighted: false, now);
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
        var blast = _pool.Take(EnergyVfx.PlasmaExplosion);
        blast.Place(position, Quaternion.identity, Mathf.Max(0.1f, blastRadius / ExplosionSize));
        blast.Start();
        PointLights.Flash(position, ExplosionLight, now, Mathf.Clamp(blastRadius / ExplosionSize, 0.6f, 1.4f));
    }

    private void Draw(int index, Vector3 position, float radius, bool electric, bool flying, bool sighted, float now)
    {
        _exploded[index] = false;
        if (_orbs[index] is { } held && _electric[index] != electric) { held.Release(); _orbs[index] = null; }
        var appeared = _orbs[index] == null;
        var orb = _orbs[index] ??= _pool.Take(EnergyVfx.Orb, electric ? ElectricTint : null).Hold();
        _electric[index] = electric;
        if (appeared)
        {
            _sighted[index] = sighted;
            foreach (var layer in BeyondCore) orb.Emit(layer, !sighted);
        }
        _positions[index] = position;
        var scale = Mathf.Max(0.02f, radius / OrbSize);
        orb.Place(position, Quaternion.identity, scale);
        orb.Keep();
        if (appeared) _flying[index] = false;
        if (appeared && !flying && !sighted) orb.PlayState(ChargingState);
        if (flying && !_flying[index])
        {
            _flying[index] = true;
            orb.PlayState(LaunchState);
            if (electric) orb.PlayState(StormState);
            PointLights.Flash(position, LaunchLight, now, Mathf.Clamp(scale, 0.5f, 1.2f));
        }
        PointLights.Hold(ref _lights[index], position, electric ? ElectricOrbLight : OrbLight, now, Mathf.Clamp(scale, 0.3f, 1.2f));
    }

    public void Pulse(int index, ReadOnlySpan<Vector3> targets, float now)
    {
        if (!Ready) return;
        if (targets.Length < 1 || targets.Length > EnergyLimits.PulseTargets) throw new ArgumentException("Invalid orb pulse targets.");
        var start = _positions[index];
        EnergyAudio.PlayAt("shock-pulse", start, .22f);
        PointLights.Flash(start, DischargeLight, now);
        foreach (var target in targets)
        {
            var bolt = _pool.Take(EnergyVfx.Discharge);
            bolt.Stretch(start, target);
            bolt.Start();
        }
    }

    /// <summary>The orb in slot <paramref name="index"/> reached full charge: its `charged` state (a flash, an expanding
    /// ring and a slow pulse) plays beside `charging` until the orb is launched or cancelled. A sighted preview only
    /// flashes its light, since that state reaches past the core.</summary>
    public void Charged(int index)
    {
        if (!Ready || _orbs[index] is not { } orb) return;
        if (!_sighted[index]) orb.PlayState(ChargedState);
        PointLights.Flash(_positions[index], ChargedLight, Time.time);
    }

    private void ReleaseOrb(int index)
    {
        _orbs[index]?.Release();
        _orbs[index] = null;
        _flying[index] = false;
        PointLights.Release(ref _lights[index]);
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
        EnergyAudio.PlayAt("orb-explode", position, .48f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var voice in _flightVoices) voice.Stop();
        _pool.Dispose();
        if (_voices != null) Object.Destroy(_voices);
    }
}
