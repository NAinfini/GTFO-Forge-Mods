using System;
using AirParticles = AirParticleSystem.AirParticleSystem;

namespace ForgePerformance.LoadShedding;

internal sealed partial class LoadSheddingController
{
    internal void Ambient(AmbientParticles particles)
    {
        if (!Active || (!Settings.AmbientDustOff && !Settings.AmbientParticles)
            || IsRefused(Settings.AmbientDustOff ? SheddingCut.Dust : SheddingCut.Thinning)) return;
        if (Settings.AmbientDustOff) { SetupCut(SheddingCut.Dust, () => Dust.Ambient(particles)); return; }
        SetupCut(SheddingCut.Thinning, () => ThinAmbient(particles));
    }
    private void ThinAmbient(AmbientParticles particles)
    {
        if (!Active || particles == null || _ambient.ContainsKey(particles.Pointer)) return;
        var original = particles.instanceCount;
        var reduced = original <= 0 ? original : Math.Max(1, (int)(original * Settings.ParticleFactor));
        _ambient.Add(particles.Pointer, new(particles, original, reduced));
        if (original == reduced) return;
        particles.instanceCount = reduced; particles.ResetSystem(); _ambientChanged++;
    }
    internal bool AmbientUpdate(AmbientParticles particles, ref float deltaTime)
    {
        if (Active && Settings.AmbientDustOff) return IsRefused(SheddingCut.Dust) || Dust.SimulateAmbient();
        if (!Active || IsRefused(SheddingCut.Thinning) || !_ambient.TryGetValue(particles.Pointer, out var state)) return true;
        _ambientUpdateCalls++;
        if (SheddingPolicy.AmbientStep(ref state.AccumulatedTime, deltaTime, out var granted, 1f / Settings.ParticleHz))
        { deltaTime = granted; return true; }
        _ambientSkippedUpdates++; return false;
    }
    internal bool AmbientReset(AmbientParticles particles)
    {
        if (Active && Settings.AmbientDustOff) return IsRefused(SheddingCut.Dust) || Dust.SimulateAmbient();
        if (!IsRefused(SheddingCut.Thinning) && _ambient.TryGetValue(particles.Pointer, out var state)) state.AccumulatedTime = 0;
        return true;
    }
    internal void Air(AirParticles particles)
    {
        if (!Active || (!Settings.AmbientDustOff && !Settings.AmbientParticles)
            || IsRefused(Settings.AmbientDustOff ? SheddingCut.Dust : SheddingCut.Thinning)) return;
        if (Settings.AmbientDustOff) { SetupCut(SheddingCut.Dust, () => Dust.Air(particles)); return; }
        SetupCut(SheddingCut.Thinning, () => ThinAir(particles));
    }
    private void ThinAir(AirParticles particles)
    {
        if (!Active || particles == null || _air.ContainsKey(particles.Pointer)) return;
        var original = particles.m_maxParticles;
        var reduced = original <= 0 ? original : Math.Max(1, (int)(original * Settings.ParticleFactor));
        _air.Add(particles.Pointer, new(particles, original, reduced));
        if (original == reduced) return;
        particles.m_maxParticles = reduced; particles.NotifyConfigChange(); _ambientChanged++;
    }

}
