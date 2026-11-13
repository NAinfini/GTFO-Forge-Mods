using System;
using ForgeAudio;
using ForgeVisual;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>Held orbs with independent one-shot discharges and detonations. Native code supplies contacts.</summary>
internal sealed class PlasmaEffects : IDisposable
{
    /// <summary>A detonation is drawn this far back along the orb's approach, so the blast does not sink into the
    /// wall it hit.</summary>
    private const float ExplosionStandOff = 0.3f;
    /// <summary>The charge at the muzzle is this share of the payload's diameter; in the local player's own view it is at most
    /// <see cref="FirstPersonCharge"/> metres across (half a metre from the eye, about 5 % of the view).</summary>
    private const float ChargeShare = 0.55f, FirstPersonCharge = 0.12f;
    /// <summary>The gap a sighted charge's edge keeps below the sight line, in metres.</summary>
    private const float SightMargin = 0.01f;
    /// <summary>The hue the charge takes: the plasma orb's blue, the shock orb's violet.</summary>
    private static readonly Color PlasmaHue = new(0.35f, 0.65f, 1f), ShockHue = new(0.65f, 0.4f, 1f);


    /// <summary>The slots with an orb out.</summary>
    private readonly bool[] _shown;
    private readonly bool[] _exploded;
    /// <summary>Each orb's own hold keys: its body and its trail or charge layer.</summary>
    private readonly int[] _bodyKeys, _layerKeys;
    private readonly Vector3[] _positions;
    /// <summary>Each flying orb's last movement: its trail is held along it, so the tail streams out behind the orb.</summary>
    private readonly Vector3[] _headings;
    private readonly bool[] _flying;
    private readonly GameObject _voices;
    /// <summary>Each orb slot's flight hum, following its orb while it flies.</summary>
    private readonly AudioVoice[] _flightVoices;
    private bool _disposed;

    /// <summary>The sounds and effects of the gun in the hand; the owner sets it when the gun changes.</summary>
    internal EnergyLook Look { get; set; } = EnergyLook.Default;

    public PlasmaEffects(int capacity)
    {
        _shown = new bool[capacity];
        _exploded = new bool[capacity];
        _bodyKeys = new int[capacity];
        _layerKeys = new int[capacity];
        _positions = new Vector3[capacity];
        _headings = new Vector3[capacity];
        _flying = new bool[capacity];
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

    /// <summary>The charge gathering at the muzzle (slot energy.orb.charge, the orb's own hue, simulated with the gun so it stays on the
    /// muzzle); the orb body shows once it flies. It is <see cref="ChargeShare"/> of the payload's diameter, and in the local player's own
    /// view (<paramref name="firstPerson"/>) at most <see cref="FirstPersonCharge"/>; aimed through an optic, <paramref name="sight"/> is
    /// the look camera's ray and the charge also stays below it however close the optic sits to the bore.</summary>
    public void ChargingOrb(int index, Vector3 position, float radius, bool electric, bool firstPerson, Ray? sight)
    {
        if (!Ready) return;
        var size = radius * 2f * ChargeShare;
        if (firstPerson) size = Mathf.Min(size, FirstPersonCharge);
        if (sight is { } line)
        {
            var offset = position - line.origin;
            var clearance = (offset - line.direction * Vector3.Dot(offset, line.direction)).magnitude;
            size = Mathf.Min(size, 2f * Mathf.Max(0f, clearance - SightMargin));
        }
        Begin(index, position, flying: false);
        if (Look.Effect(EnergyVfx.OrbCharge) is { } charge) Fx.Hold(charge, _layerKeys[index], position, Vector3.up, size, electric ? ShockHue : PlasmaHue);
    }

    /// <summary>The orb of slot <paramref name="index"/>. A flying orb hums as it goes;
    /// an orb still charging at the muzzle has no flight sound.</summary>
    public void Orb(int index, Vector3 position, float radius, bool electric, bool flying)
    {
        if (!Ready) return;
        Draw(index, position, radius, electric, flying);
        if (!flying) return;
        var voice = _flightVoices[index];
        voice.Anchor.position = position;
        if (voice.Playing) return;
        voice.Play(Look.Cue(electric ? "shock-fly-loop" : "orb-fly-loop"), .85f);
    }

    /// <summary>The arc orb running out or stopped by a wall: it bursts (slot energy.shock.burst) and its hum ends with a crackle.</summary>
    public void Fade(int index, Vector3 position)
    {
        if (!Ready) return;
        StopFlight(index);
        EnergyVfx.Play(Look.Effect(EnergyVfx.ShockBurst), position, Vector3.up);
        EnergyAudio.PlayAt(Look.Cue("shock-fade"), position, .5f);
    }

    private void StopFlight(int index) => _flightVoices[index].Stop();

    public void Contact(Vector3 position)
    {
        if (Ready) EnergyAudio.PlayAt(Look.Cue("shock-hit"), position, .22f);
    }

    /// <summary>An orb running out: it shrinks to a tenth of its radius as <paramref name="progress"/> reaches 1.</summary>
    public void Dissipate(int index, Vector3 position, float radius, float progress)
    {
        if (!Ready) return;
        Draw(index, position, radius * (1f - Mathf.Clamp01(progress) * 0.9f), electric: true, flying: true);
    }

    // The slot latch prevents duplicate detonations until the next orb starts.
    public void Explosion(int index, Vector3 position, float blastRadius)
    {
        if (!Ready || _exploded[index]) return;
        _exploded[index] = true;
        StopFlight(index);
        var approach = _shown[index] ? _positions[index] - position : Vector3.zero;
        ReleaseOrb(index);
        if (approach.sqrMagnitude > 0.000001f) position += approach.normalized * ExplosionStandOff;
        if (Look.Effect(EnergyVfx.PlasmaBlast) is { } library) Fx.Play(library, position, Vector3.up, blastRadius * 2f, Color.white);
    }

    /// <summary>Marks slot <paramref name="index"/> shown at <paramref name="position"/>; a new orb in it takes new hold keys, so it never moves
    /// the last orb's copies.</summary>
    private void Begin(int index, Vector3 position, bool flying)
    {
        _exploded[index] = false;
        if (!_shown[index])
        {
            _flying[index] = false;
            _bodyKeys[index] = Fx.NewKey();
            _layerKeys[index] = Fx.NewKey();
            _headings[index] = Vector3.zero;
        }
        else if (flying && (position - _positions[index]).sqrMagnitude > 1e-6f) _headings[index] = position - _positions[index];
        _shown[index] = true;
        _positions[index] = position;
    }

    private void Draw(int index, Vector3 position, float radius, bool electric, bool flying)
    {
        Begin(index, position, flying);
        if (Look.Effect(electric ? EnergyVfx.ShockOrb : EnergyVfx.OrbBody) is { } body) Fx.Hold(body, _bodyKeys[index], position, Vector3.up, radius * 2f, Color.white);
        // The trail follows the flight (+Z along it, its tail behind), as wide as the orb.
        if (flying && Look.Effect(EnergyVfx.OrbTrail) is { } trail) Fx.Hold(trail, _layerKeys[index], position, _headings[index], radius * 2f, Color.white);
        if (flying && !_flying[index])
        {
            _flying[index] = true;
            EnergyVfx.Play(Look.Effect(EnergyVfx.OrbMuzzle), position, Vector3.up);
        }
    }

    public void Pulse(int index, ReadOnlySpan<Vector3> targets)
    {
        if (!Ready) return;
        if (targets.Length < 1 || targets.Length > EnergyLimits.PulseTargets) throw new ArgumentException("Invalid orb pulse targets.");
        var start = _positions[index];
        EnergyAudio.PlayAt(Look.Cue("shock-pulse"), start, .22f);
        foreach (var target in targets)
        {
            EnergyVfx.Play(Look.Effect(EnergyVfx.Discharge), target, start - target); // lightning lands on each target
        }
    }

    /// <summary>The orb in slot <paramref name="index"/> reached full charge: its light flashes (slot energy.orb.charged, a light only).</summary>
    public void Charged(int index)
    {
        if (!Ready || !_shown[index]) return;
        EnergyVfx.Play(Look.Effect(EnergyVfx.OrbCharged), _positions[index], Vector3.up);
    }

    private void ReleaseOrb(int index)
    {
        _shown[index] = false;
        _flying[index] = false;
    }

    /// <summary>The slot is free: its orb is no longer held; a running detonation finishes.</summary>
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
        EnergyAudio.PlayAt(Look.Cue("orb-explode"), position, .48f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var voice in _flightVoices) voice.Stop();
        if (_voices != null) Object.Destroy(_voices);
    }
}
