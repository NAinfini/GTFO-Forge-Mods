using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace ForgeAudio;

[BepInPlugin(Id, "Forge Audio", Version)]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.0")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = AudioKit.PluginId, Version = "1.0.0"; // package-version: NAinfini-ForgeAudio
    private static readonly ForgeCore.ModLog Logger = ForgeCore.Log.For(Id);
    private static ConfigEntry<float>? _master;
    private static readonly ConfigEntry<float>?[] Volumes = new ConfigEntry<float>?[AudioCategories.Count];

    private static ConfigFile? _config;
    private static readonly System.Collections.Generic.HashSet<string> Reported = new(StringComparer.Ordinal);

    /// <summary>Copies the kit's volumes from its config (master and one per global category, every mod's sounds together); runs at load and whenever one of them changes.</summary>
    internal static void ReadVolumes()
    {
        GameContext.KitMaster = _master?.Value ?? 1f;
        for (var c = 0; c < AudioCategories.Count; c++) GameContext.KitVolume[c] = Volumes[c]?.Value ?? 1f;
    }

    private static readonly ForgeCore.LazyGroups<HookGroup> Groups = ForgeCore.Patches.Groups<HookGroup>(Id, typeof(Plugin).Assembly,
        type => type.GetCustomAttributes(typeof(LazyHookAttribute), false) is { Length: > 0 } found ? ((LazyHookAttribute)found[0]).Group : null,
        (group, type, e) =>
        {
            if (group == HookGroup.Body) BodySounds.Disable("the game's feedback player could not be hooked: " + e);
            else if (group == HookGroup.Reserved) ReservedEvents.Disable("the hook " + type.Name + " could not be put on the game: " + e);
            else Logger.Error("The " + group + " hook " + type.Name + " could not be put on the game: " + e);
        });

    /// <summary>Puts on the hooks of one group the first time a mod claims what they serve (a body sound, a weapon action). Nothing
    /// takes them off again: with no claim they answer at once, and removing a hook would only leave a forwarder behind.</summary>
    internal static void InstallHooks(HookGroup group)
    {
        if (Groups.Has(group)) return;
        if (group == HookGroup.Weapon) WeaponActions.Subscribe();
        Groups.Ensure(group);
    }

    internal static ConfigEntry<float>? MasterVolume => _master;
    internal static ConfigEntry<float>? CategoryVolume(int category) => Volumes[category];

    /// <summary>Sets a volume without writing the file on every step of a drag (see <see cref="SaveVolumes"/>).</summary>
    internal static void SetVolume(ConfigEntry<float>? entry, float value)
    {
        if (entry == null || _config == null) return;
        var save = _config.SaveOnConfigSet;
        _config.SaveOnConfigSet = false;
        try { entry.Value = Math.Clamp(value, 0f, 1f); }
        finally { _config.SaveOnConfigSet = save; }
    }

    internal static void SaveVolumes()
    {
        try { _config?.Save(); }
        catch (Exception error) { Logger.Error("The kit's volumes could not be saved to " + Id + ".cfg: " + error); }
    }

    internal static void Error(string message) => Logger.Error(message);

    private static bool FirstReport(string key) { lock (Reported) return Reported.Add(key); }

    public override void Load()
    {
        // One startup.timing line per library too, so a slow start can be told apart from the packages that depend on it.
        using var timing = new ForgeCore.StartupTiming(Id, message => base.Log.LogInfo(message));
        ForgeCore.Settings.Adopt(Id, Config);
        _config = Config;
        ForgeCore.Settings.Batch(Id, file =>
        {
            _master = file.Bind("Volume", "Master", 1f, new ConfigDescription(
                "Every sound played through ForgeAudio, by every mod, on top of the game's own volume sliders.", new AcceptableValueRange<float>(0f, 1f)));
            for (var c = 0; c < AudioCategories.Count; c++)
                Volumes[c] = file.Bind("Volume", AudioCategories.Names[c], 1f, new ConfigDescription(
                    "Every mod's " + AudioCategories.Meanings[c] + ".", new AcceptableValueRange<float>(0f, 1f)));
        });
        ReadVolumes();
        _master!.SettingChanged += (_, _) => ReadVolumes();
        foreach (var entry in Volumes) entry!.SettingChanged += (_, _) => ReadVolumes();
        GameContext.Subscribe();
        AudioKit.SubscribeGameSounds();
        // Only failures are written. A voice restarted after the device changed is routine, not a failure.
        // Keyed by the report's key, so a failure whose text changes (a count, a path) is still written once.
        AudioLog.Sink = (key, failure) => { if (failure.Code != AudioError.DeviceChanged && FirstReport(key)) Logger.Error(failure.ToString()); };
        AudioKit.MainThread = Environment.CurrentManagedThreadId;
        ClipUpload.Initialize();
        // Every patch class carries [LazyHook] and goes on through InstallHooks when a claim needs it; nothing is patched eagerly.
        AudioDriver.Start();
        SyncPlays.Open();
        AudioKit.Ready = true;
        timing.Complete();
    }
    public override bool Unload()
    {
        AudioDriver.Shutdown();
        GameContext.Unsubscribe();
        AudioKit.UnsubscribeGameSounds();
        AudioKit.Ready = false;
        return true;
    }
}

/// <summary>The kit's per-frame work, run from ForgeCore's one driver (<see cref="ForgeCore.Frame"/>) instead of a component of its own.</summary>
internal static class AudioDriver
{
    private static AudioSettings.AudioConfigurationChangeHandler? _deviceCallback;

    internal static void Start()
    {
        _deviceCallback = (Action<bool>)(_ => Engine.DeviceChanged = true);
        AudioSettings.OnAudioConfigurationChanged += _deviceCallback;
        ForgeCore.Frame.OnUpdate(Plugin.Id, Update);
        ForgeCore.Frame.OnQuit(Plugin.Id, Shutdown);
    }

    private static void Update()
    {
        if (!AudioKit.Ready) return;
        // Each step runs alone and reports once under its own key: one failing must not stop the steps after it every frame. Only the
        // engine's own failure ends the sounds it was playing. The steps are direct calls, not delegates, so the hot-path getter walk sees into them.
        try { GameContext.Refresh(); } catch (Exception e) { StepFailed("context", e); }
        try { Assets.Tick(); } catch (Exception e) { StepFailed("assets", e); }
        try { SyncPlays.Pump(); } catch (Exception e) { StepFailed("synced-plays", e); }
        try { Engine.Tick(); } catch (Exception e) { StepFailed("engine", e); Engine.Clear(EndReason.Failed); }
        try { Sustains.Tick(); } catch (Exception e) { StepFailed("sustains", e); }
        try { AudioVolumes.Tick(); } catch (Exception e) { StepFailed("volumes", e); }
    }

    private static void StepFailed(string name, Exception e) =>
        AudioLog.Report("driver-update-" + name, AudioError.NotReady, "Audio driver update (" + name + ") failed: " + e);

    internal static void Shutdown()
    {
        if (_deviceCallback != null) AudioSettings.OnAudioConfigurationChanged -= _deviceCallback;
        _deviceCallback = null;
        Engine.Clear(EndReason.PluginUnloaded);
        Sustains.Clear();
        BodySounds.Clear();
        WeaponActions.Clear();
        Assets.Clear();
        ListenerOutput.Shutdown();
        GameContext.HasListener = false;
        AudioKit.Ready = false;
    }
}

/// <summary>Which claims a lazily installed hook serves.</summary>
internal enum HookGroup { Body, Weapon, Reserved }

/// <summary>Marks a Harmony patch class that goes on the game only when <see cref="Plugin.InstallHooks"/> is called for its group.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class LazyHookAttribute : Attribute
{
    public LazyHookAttribute(HookGroup group) { Group = group; }
    internal HookGroup Group { get; }
}
