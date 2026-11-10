using System;
using Enemies;
using UnityEngine;
using AirParticles = AirParticleSystem.AirParticleSystem;

namespace ForgePerformance.LoadShedding;

[Flags]
internal enum SheddingCut
{
    CorpseShadows = 2, CorpseTimer = 4, Dust = 8, Thinning = 16,
    Visor = 32, Garden = 64, DecorationShadows = 512,
    DecorationCulling = 1024, DecorationAnimation = 2048, DecorationLights = 4096
}

internal sealed partial class LoadSheddingController
{
    private SheddingCut _refusedCuts, _levelFailedCuts;
    private int _levelSetupFailures;
    internal bool IsRefused(SheddingCut cut) => ((_refusedCuts | _levelFailedCuts) & cut) != 0;

    // Cold setup only. No unpatching inside native callbacks, no global rollback.
    internal void SetupCut(SheddingCut cut, Action setup)
    {
        if (IsRefused(cut)) return;
        try { setup(); }
        catch (Exception error)
        {
            if (cut == SheddingCut.Visor) Visor.Refuse(error);
            else RefuseCut(cut, error.GetType().Name + ": " + error.Message, error is MissingMemberException or TypeLoadException or EntryPointNotFoundException);
        }
    }
    private void RefuseCut(SheddingCut cut, string reason, bool session = true)
    {
        if (IsRefused(cut)) return;
        if (session) _refusedCuts |= cut; // Only an unprovable native binding lasts the session.
        else { _levelFailedCuts |= cut; _levelSetupFailures++; }
        if (cut == SheddingCut.Dust)
            try { Dust.EndLevel(); }
            catch (Exception error) { reason += "; restore failed: " + error.GetType().Name + ": " + error.Message; }
        var item = cut switch
        {
            SheddingCut.CorpseShadows => "corpse-shadows",
            SheddingCut.CorpseTimer => "corpse-timer",
            SheddingCut.Dust => "ambient-dust-off",
            SheddingCut.Thinning => "ambient-particles",
            SheddingCut.Visor => "visor-liquid-off",
            SheddingCut.Garden => "garden-props",
            SheddingCut.DecorationShadows => "decoration-shadows",
            SheddingCut.DecorationCulling => "decoration-culling",
            SheddingCut.DecorationAnimation => "decoration-animation",
            SheddingCut.DecorationLights => "decoration-lights",
            _ => throw new ArgumentOutOfRangeException(nameof(cut))
        };
        try { if (session) LogRefusal(item, reason); else LogSetupFailure(item, reason); }
        catch (Exception error) { Console.Error.WriteLine(item + " refusal report failed: " + reason + "; " + error.GetType().Name + ": " + error.Message); }
    }

    internal void BeginLevel()
    {
        LevelSequence++;
        _levelFailedCuts = 0; _levelSetupFailures = 0;
        _gardenSeen = _gardenChanged = 0;
        _corpseShadowSeen = _corpseShadowChanged = _corpseShadowRestored = 0;
        _stealthHidden = _stealthRestored = 0;
        _ragdollSeen = _ragdollEvicted = _ambientChanged = _shellSeen = _shellSuppressed = 0;
        _ragdollRefreshSeen = _ragdollRefreshed = _ragdollRefreshFailures = 0;
        _gardenRestored = _ambientRestored = 0;
        _restoreFailures = 0;
        _gardenInstances = _gardenShadowCalls = _gardenScopesReleased = 0;
        _ambientUpdateCalls = _ambientSkippedUpdates = 0;
        _shellFrame = -1; _shellBudget = 0;
        _gardens.Clear();
        Active = true;
        Work.Clear();
        if (Settings.FastCorpseHide) SetupCut(SheddingCut.CorpseTimer, Corpses.BeginLevel);
        if (Settings.CorpseShadows) Work.Add("enemy-find", () => SetupCut(SheddingCut.CorpseShadows, () =>
        {
            foreach (var enemy in UnityEngine.Object.FindObjectsOfType<EnemyAgent>())
                EnemyVisuals(enemy);
        }));
        if (Settings.AmbientDustOff) Dust.BeginLevel(); // Managed counters reset even after a session refusal.
        if (Settings.VisorLiquidOff) Visor.BeginLevel();
        if (Settings.AmbientParticles || Settings.AmbientDustOff)
        {
            var ambientCut = Settings.AmbientDustOff ? SheddingCut.Dust : SheddingCut.Thinning;
            Work.Add("ambient-find", () => SetupCut(ambientCut, () =>
            {
                // One piece per particle system: a level holds hundreds.
                foreach (var ambient in UnityEngine.Object.FindObjectsOfType<AmbientParticles>())
                { var one = ambient; Work.Add("ambient", () => SetupCut(ambientCut, () => Ambient(one))); }
                foreach (var air in UnityEngine.Object.FindObjectsOfType<AirParticles>())
                { var one = air; Work.Add("air", () => SetupCut(ambientCut, () => Air(one))); }
            }));
        }
        if (Settings.VisorLiquidOff) Work.Add("visor-find", () => SetupCut(SheddingCut.Visor, () =>
        {
            foreach (var liquid in UnityEngine.Object.FindObjectsOfType<GlassLiquidSystem>()) Visor.Suppress(liquid);
        }));
        if (Settings.GardenShadows) Work.Add("garden-find", () => SetupCut(SheddingCut.Garden, ApplyGarden));
        Extras.BeginLevel();
        Decorations.BeginLevel(); // Individual decoration setups use SetupCut too.
        SetupBuiltLevel(); // The level is complete when the elevator ride waits to stop.
    }
}
