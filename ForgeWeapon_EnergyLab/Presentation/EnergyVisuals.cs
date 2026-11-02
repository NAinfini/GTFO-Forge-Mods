using System;
using System.Collections;
using ForgeVisual;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>Resolved segments only; chain hops retain their native timing. This renderer never applies damage.</summary>
internal sealed class EnergyVisuals : IDisposable
{
    private const string IgniteState = "ignite";
    private static readonly PointLightSpec BeamMuzzleLight = new(new Color(0.4f, 0.7f, 1f), 3f, 1.5f, flicker: 0.2f);
    private static readonly PointLightSpec BeamImpactLight = new(new Color(0.45f, 0.75f, 1f), 4.5f, 2.5f, flicker: 0.3f);
    private static readonly PointLightSpec ArcLight = new(new Color(0.5f, 0.75f, 1f), 5f, 3f, 0.2f, 0.4f);
    private static readonly PointLightSpec BlastMuzzleLight =new(new Color(1f, 0.6f, 0.25f), 4f, 3f, 0.12f);
    private const int Capacity = EnergyLimits.ArcTargets;
    private readonly FxInstancePool _pool = new("Energy weapon visuals");
    private readonly Vector3[] _starts = new Vector3[Capacity];
    private readonly Vector3[] _ends = new Vector3[Capacity];
    private readonly bool[] _hits = new bool[Capacity];
    private FxInstance? _beam;
    private int _beamMuzzleLight, _beamImpactLight;
    private float _arcStarted, _arcHopDelay;
    private int _segments, _started;
    private bool _disposed, _prepared;

    internal IEnumerator Prepare()
    {
        if (_prepared) yield break;
        foreach (var step in _pool.Prepare(EnergyVfx.Beam, 2)) yield return step;
        foreach (var step in _pool.Prepare(EnergyVfx.Arc, Capacity * 2)) yield return step;
        _prepared = true;
    }

    /// <summary>The held beam this frame; its end glow, sparks and light show only while it hits something.</summary>
    public void Beam(Vector3 start, Vector3 end, bool hit, float now)
    {
        if (_disposed) return;
        _segments = 0;
        var ignite = _beam == null;
        _beam ??= _pool.Take(EnergyVfx.Beam).Hold();
        _beam.Stretch(start, end);
        _beam.Emit("impact", hit);
        _beam.Keep();
        if (ignite) _beam.PlayState(IgniteState);
        PointLights.Hold(ref _beamMuzzleLight, start, BeamMuzzleLight, now);
        if (hit) PointLights.Hold(ref _beamImpactLight, end, BeamImpactLight, now);
        else PointLights.Release(ref _beamImpactLight);
    }

    public void Chain(ReadOnlySpan<Vector3> starts, ReadOnlySpan<Vector3> ends, ReadOnlySpan<bool> hits, float now, float hopDelay)
    {
        if (starts.Length < 1 || starts.Length > Capacity || ends.Length != starts.Length || hits.Length != starts.Length)
            throw new ArgumentException("Invalid chain presentation segments.");
        if (_disposed) return;
        Hide();
        starts.CopyTo(_starts);
        ends.CopyTo(_ends);
        hits.CopyTo(_hits);
        _segments = starts.Length;
        _started = 0;
        _arcStarted = now; _arcHopDelay = hopDelay;
        Tick(now);
    }

    /// <summary>A Blast Gun shot: only the muzzle flashes; the explosion appears at the impact with no line between.</summary>
    public void Blast(Vector3 start, float now)
    {
        if (_disposed) return;
        Hide();
        PointLights.Flash(start, BlastMuzzleLight, now);
    }

    /// <summary>Starts each chain hop's arc when its delay is due; the arc then plays out on its own.</summary>
    public void Tick(float now)
    {
        if (_disposed) return;
        while (_started < _segments && now - _arcStarted >= _started * _arcHopDelay)
        {
            var arc = _pool.Take(EnergyVfx.Arc);
            arc.Stretch(_starts[_started], _ends[_started]);
            arc.Emit("end", _hits[_started]);
            arc.Start();
            if (_hits[_started]) PointLights.Flash(_ends[_started], ArcLight, now);
            _started++;
        }
    }

    /// <summary>Lets the beam go and drops chain hops not yet started; arcs already playing finish.</summary>
    public void Hide()
    {
        if (_segments == 0 && _beam == null && _beamMuzzleLight == 0 && _beamImpactLight == 0) return;
        _segments = _started = 0;
        _beam?.Release();
        _beam = null;
        PointLights.Release(ref _beamMuzzleLight);
        PointLights.Release(ref _beamImpactLight);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.Dispose();
    }
}
