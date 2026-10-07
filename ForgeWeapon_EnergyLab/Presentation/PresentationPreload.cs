using System;
using System.Collections;
using ForgeWeaponEnergyLabExperimental.Native;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>One bounded main-thread step per frame, started at plugin startup rather than first draw.
/// Unity async requests are polled; no Unity objects are created on a worker thread.</summary>
internal static class PresentationPreload
{
    private static IEnumerator? _work;
    internal static bool Ready { get; private set; }
    internal static bool Failed { get; private set; }
    private static string _phase = "icons";

    internal static void Tick()
    {
        if (Ready || Failed) return;
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
            Plugin.Error("EnergyPreload failed at " + _phase + "; energy weapons stay disabled: " + error);
        }
    }

    private static IEnumerator Prepare()
    {
        var phases = new[] { EnergyIcons.Preload(), WeaponModels.Preload(), VfxLibrary.Preload(), PrepareAudio() };
        var names = new[] { "icons", "models", "vfx", "audio" };
        for (var i = 0; i < phases.Length; i++)
        {
            _phase = names[i];
            using var phase = phases[i] as IDisposable;
            while (phases[i].MoveNext()) yield return null;
            yield return null;
        }
    }

    private static IEnumerator PrepareAudio()
    {
        foreach (var cue in EnergySound.Cues) { EnergySound.Load(cue.Name); yield return null; }
    }
}
