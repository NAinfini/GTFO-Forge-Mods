using System;
using ForgeVisual;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>Resolved segments only; chain hops retain their native timing. This renderer never applies damage.</summary>
internal sealed class EnergyVisuals : IDisposable
{
    private const int Capacity = EnergyLimits.ArcTargets;
    private readonly Vector3[] _starts = new Vector3[Capacity];
    private readonly Vector3[] _ends = new Vector3[Capacity];
    private readonly bool[] _hits = new bool[Capacity];
    /// <summary>This weapon's own hold keys for its beam, the glow at its muzzle and the burn where it lands.</summary>
    private readonly int _beamKey = Fx.NewKey(), _muzzleKey = Fx.NewKey(), _hitKey = Fx.NewKey();
    private float _arcStarted, _arcHopDelay;
    private int _segments, _started;
    private bool _beaming, _beamHit, _disposed;

    /// <summary>The effects of the gun in the hand; the owner sets it when the gun changes.</summary>
    internal EnergyLook Look { get; set; } = EnergyLook.Default;

    /// <summary>The held beam this frame with the glow at its muzzle; the burn where it lands, with its light, shows only while it hits something.</summary>
    public void Beam(Vector3 start, Vector3 end, bool hit, float now)
    {
        if (_disposed) return;
        _segments = 0;
        _beaming = true;
        if (Look.Effect(EnergyVfx.BeamLine) is { } line) Fx.Line(line, _beamKey, start, end, hit, Color.white);
        if (Look.Effect(EnergyVfx.BeamMuzzle) is { } muzzle) Fx.Hold(muzzle, _muzzleKey, start, end - start, float.NaN, Color.white);
        if (hit && Look.Effect(EnergyVfx.BeamHit) is { } burn) Fx.Hold(burn, _hitKey, end, start - end, float.NaN, Color.white);
        else if (_beamHit && Look.Effect(EnergyVfx.BeamHit) is { } gone) Fx.Release(gone, _hitKey);
        _beamHit = hit;
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
    public void Blast(Vector3 start, Vector3 direction, float now)
    {
        if (_disposed) return;
        Hide();
        EnergyVfx.Play(Look.Effect(EnergyVfx.BlastMuzzle), start, direction);
    }

    /// <summary>Starts each chain hop's arc when its delay is due; the arc then plays out on its own.</summary>
    public void Tick(float now)
    {
        if (_disposed) return;
        while (_started < _segments && now - _arcStarted >= _started * _arcHopDelay)
        {
            // The arc runs from this hop's start to its end: the line effect's +Z points at the target.
            if (Look.Effect(EnergyVfx.ArcLine) is { } line) Fx.Line(line, _starts[_started], _ends[_started], _hits[_started]);
            if (_hits[_started])
            {
                EnergyVfx.Play(Look.Effect(EnergyVfx.ArcHit), _ends[_started], _starts[_started] - _ends[_started]);
            }
            _started++;
        }
    }

    /// <summary>Lets the beam go, its line, muzzle glow and burn gone at once, and drops chain hops not yet started; arcs already playing finish.</summary>
    public void Hide()
    {
        if (_beaming)
        {
            _beaming = _beamHit = false;
            if (Look.Effect(EnergyVfx.BeamLine) is { } line) Fx.Release(line, _beamKey);
            if (Look.Effect(EnergyVfx.BeamMuzzle) is { } muzzle) Fx.Release(muzzle, _muzzleKey);
            if (Look.Effect(EnergyVfx.BeamHit) is { } burn) Fx.Release(burn, _hitKey);
        }
        _segments = _started = 0;
    }

    public void Dispose() => _disposed = true;
}
