using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgePerformance;

/// <summary>Speed-ups only. The plugin installs a piece when its setting is on and nothing otherwise: with every setting off it
/// registers no hook, no component and no quit callback, so it costs nothing. The developer probe (Probe option, off by default)
/// measures frame time and the game's enemy work for this mod's own tuning.</summary>
[BepInPlugin("NAinfini.ForgePerformance", "Forge Performance", "1.0.3")] // package-version: NAinfini-ForgePerformance
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
public sealed class Plugin : BasePlugin
{
    internal static bool Quitting;
    private Il2CppSystem.Action? _quitting;
    private LoadShedding.LoadSheddingController? _shedding;
    private LoadShedding.LoadSheddingWatch? _sheddingWatch;
    private LoadShedding.WindCapacityProtection? _wind;
    public override void Load()
    {
        Quitting = false;
        LoadShedding.FileHashCache.Store = System.IO.Path.Combine(Paths.CachePath, "ForgePerformance", "file-sha256.txt");
        var settings = LoadShedding.SheddingConfiguration.Read(Config, line => Log.LogInfo(line));
        if (settings.WindCapacityProtection || settings.WindNearest)
        {
            _wind = new LoadShedding.WindCapacityProtection(Log, settings);
            try { _wind.Install(); }
            catch (Exception error) { Log.LogError("performance-install-failed: wind protection is off: " + error); _wind = null; }
        }
        Crowd.CrowdSwitches.Configure(settings, settings.CrowdHooks);
        if (settings.Probe) Crowd.Probe.Start(Paths.BepInExRootPath, settings.ProbeSeconds, Crowd.CrowdSwitches.Describe, line => Log.LogError(line));
        if (settings.Probe || settings.ClientHiddenSyncThrottle)
            Crowd.CrowdPatches.Install(settings, line => { Log.LogError(line); Crowd.Probe.Line(line); });
        var plan = LoadShedding.SheddingHookPlan.Create(settings);
        if (plan.HasShedding)
        {
            _shedding = new LoadShedding.LoadSheddingController(Log, plan);
            try
            {
                _shedding.Install();
                _sheddingWatch = AddComponent<LoadShedding.LoadSheddingWatch>(); // drains the level-start queue (LevelWork)
            }
            catch (Exception error) { Log.LogError("performance-install-failed: load shedding is off: " + error); _shedding = null; }
        }
        if (_wind == null && _shedding == null) return;
        // dump.cs: UnityEngine.Application.add_quitting (RVA 0x1DD0930),
        // invoked by RequiredByNativeCode Internal_ApplicationQuit (0x1DD0500).
        // The QA interop exposes add/remove methods, not a C# event. Retain the
        // converted Il2Cpp delegate for the entire subscription lifetime.
        _quitting = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OnQuitting);
        Application.add_quitting(_quitting);
    }
    private void OnQuitting()
    {
        Quitting = true;
        try { _wind?.Finish(); }
        catch (Exception error) { Log.LogError("Wind capacity quit cleanup failed: " + error); }
        try { _shedding?.EndLevel(); }
        catch (Exception error) { Log.LogError("Load shedding quit restore failed: " + error); }
        // Keep native callbacks/trampolines rooted until process teardown. Quit
        // can run inside one of them; synchronous Undo would wait for itself.
    }
    public override bool Unload()
    {
        if (Quitting) return true;
        Exception? failure = null;
        bool Cleanup(Action action)
        {
            try { action(); return true; }
            catch (Exception error)
            {
                failure = failure == null ? error : new AggregateException(failure, error);
                return false;
            }
        }

        if (_wind is { } wind && Cleanup(wind.Dispose)) _wind = null;
        if (_shedding is { } shedding && Cleanup(shedding.Dispose)) _shedding = null;
        if (_sheddingWatch is { } sheddingWatch && Cleanup(() => sheddingWatch.enabled = false)) _sheddingWatch = null;
        Cleanup(ForgePerformance.Crowd.CrowdPatches.Uninstall);
        if (_quitting is { } quitting && Cleanup(() => Application.remove_quitting(quitting))) _quitting = null;
        if (failure != null) throw failure;
        return true;
    }
}
