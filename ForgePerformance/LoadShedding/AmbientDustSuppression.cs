using System;
using System.Collections.Generic;
using AirParticles = AirParticleSystem.AirParticleSystem;

namespace ForgePerformance.LoadShedding;

// Exactly the two native types already classified by AmbientParticles thinning.
// These use GPU draws, not generic ParticleSystem/Renderer components. Never
// walk their child hierarchy: attached weapon/mission/unknown effects are not dust.
internal sealed class AmbientDustSuppression
{
    private readonly Dictionary<IntPtr, AmbientState> _ambient = new();
    private readonly Dictionary<IntPtr, AirState> _air = new();
    internal long Changed, Restored, SkippedSimulation, SkippedAirCommands;
    internal int Seen => _ambient.Count + _air.Count;
    internal void BeginLevel() => Changed = Restored = SkippedSimulation = SkippedAirCommands = 0;

    internal void Ambient(AmbientParticles particles)
    {
        if (particles == null) return;
        if (_ambient.ContainsKey(particles.Pointer)) return;
        _ambient.Add(particles.Pointer, new(particles, particles.instanceCount, particles.enabled));
        if (particles.instanceCount != 0 || particles.enabled) Changed++;
        // canRender tests instanceCount > 0. Zero prevents the native GPU draw,
        // including callers which do not consult Behaviour.enabled.
        particles.instanceCount = 0;
        particles.enabled = false;
    }
    internal void Air(AirParticles particles)
    {
        if (particles == null) return;
        if (_air.ContainsKey(particles.Pointer)) return;
        _air.Add(particles.Pointer, new(particles, particles.m_throttle, particles.enabled));
        if (particles.m_throttle != 0 || particles.enabled) Changed++;
        particles.m_throttle = 0;
        particles.enabled = false;
        // Do not set m_maxParticles=0: the native buffer sizing expects a
        // positive count. CollectCommands is the simulation AND renderer gate.
    }
    internal bool SimulateAmbient()
    { SkippedSimulation++; return false; }
    internal bool CollectAir()
    { SkippedAirCommands++; return false; }
    internal bool PrewarmAir()
    { SkippedSimulation++; return false; }

    internal void EndLevel()
    {
        Exception? first = null;
        foreach (var state in _ambient.Values)
            try
            {
                if (state.Particles == null) continue;
                var restored = false;
                try
                {
                    if (state.Count != 0 && state.Particles.instanceCount == 0)
                    {
                        state.Particles.instanceCount = state.Count;
                        if (state.Count > 0) state.Particles.ResetSystem();
                        restored = true;
                    }
                }
                finally
                {
                    if (state.Enabled && !state.Particles.enabled)
                    { state.Particles.enabled = true; restored = true; }
                }
                if (restored) Restored++;
            }
            catch (Exception error) { first ??= error; }
        foreach (var state in _air.Values)
            try
            {
                if (state.Particles == null) continue;
                var restored = false;
                try
                {
                    if (state.Throttle != 0 && state.Particles.m_throttle == 0)
                    {
                        state.Particles.m_throttle = state.Throttle;
                        state.Particles.NotifyConfigChange();
                        restored = true;
                    }
                }
                finally
                {
                    if (state.Enabled && !state.Particles.enabled)
                    { state.Particles.enabled = true; restored = true; }
                }
                if (restored) Restored++;
            }
            catch (Exception error) { first ??= error; }
        _ambient.Clear(); _air.Clear();
        if (first != null) throw first;
    }
    private sealed record AmbientState(AmbientParticles Particles, int Count, bool Enabled);
    private sealed record AirState(AirParticles Particles, float Throttle, bool Enabled);
}
