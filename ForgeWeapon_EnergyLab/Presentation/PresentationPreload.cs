using System;
using System.Collections;
using ForgeWeaponEnergyLab.Native;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>One bounded main-thread step per frame, started at plugin startup rather than first draw.
/// Unity async requests are polled; no Unity objects are created on a worker thread.</summary>
internal static class PresentationPreload
{
    private static IEnumerator? _work;
    internal static bool Ready { get; private set; }
    internal static bool Failed { get; private set; }
    internal const float AudioLoadSeconds = 120f;
    private static string _phase = "icons";

    internal static void Tick()
    {
        if (Failed) return;
        // Once the guns are ready, a definition's own sounds keep decoding on their own; each gun picks its sounds up
        // when its pack finishes, so no slow decode can hold or fail this pass.
        if (Ready) { EnergyLook.ResolvePending(); return; }
        try
        {
            _work ??= Prepare();
            if (!_work.MoveNext())
            {
                (_work as IDisposable)?.Dispose(); _work = null; Ready = true;
            }
        }
        catch (Exception error)
        {
            Failed = true; (_work as IDisposable)?.Dispose(); _work = null;
            Plugin.Logger.ErrorOnce("EnergyPreload failed at " + _phase + "; energy weapons stay disabled: " + error);
        }
    }

    private static IEnumerator Prepare()
    {
        var phases = new[] { WeaponModels.Preload(), EnergyVfx.Preload(), PrepareAudio() };
        var names = new[] { "models", "vfx", "audio" };
        for (var i = 0; i < phases.Length; i++)
        {
            _phase = names[i];
            using var phase = phases[i] as IDisposable;
            while (phases[i].MoveNext()) yield return null;
            yield return null;
        }
    }

    /// <summary>ForgeAudio decodes the sounds in the background; the weapons wait until every cue this package plays is ready.
    /// A definition's own sounds are third-party: each gun resolves them when its pack finishes, so they never hold or fail this pass.</summary>
    private static IEnumerator PrepareAudio()
    {
        var give = Time.realtimeSinceStartup + AudioLoadSeconds;
        // Only Energy Lab's own sounds can hold the guns back; a definition's slow or broken sound set is that gun's own
        // failure, reported per sound and never a reason to switch every gun off.
        while (!EnergyAudio.Library.IsLoaded && Time.realtimeSinceStartup <= give) yield return null;
        // A sound set that does not decode in time silences this mod's sounds only; every gun still fires and shows its effects.
        if (!EnergyAudio.Library.IsLoaded) Plugin.Logger.ErrorOnce("ForgeAudio did not finish decoding the energy sounds in " + AudioLoadSeconds + " s; the guns start without their sounds.");
        else EnergyAudio.Library.Require(EnergyAudio.Cues);
        // What each gun's Look names is found now that its sounds have decoded and the effect library is known; a pack still
        // decoding is picked up later by EnergyLook.ResolvePending, one gun and one sound at a time.
        EnergyLook.ResolveAll();
    }
}
