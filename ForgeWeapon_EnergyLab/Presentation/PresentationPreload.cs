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
    private const float AudioLoadSeconds = 120f;
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

    /// <summary>ForgeAudio decodes the sounds in the background; the weapons wait until every cue they play is ready.</summary>
    private static IEnumerator PrepareAudio()
    {
        var give = Time.realtimeSinceStartup + AudioLoadSeconds;
        while (!EnergyAudio.Loaded)
        {
            if (Time.realtimeSinceStartup > give) throw new System.TimeoutException("ForgeAudio did not finish decoding the energy sounds in " + AudioLoadSeconds + " s.");
            yield return null;
        }
        EnergyAudio.Library.Require(EnergyAudio.Cues);
    }
}
