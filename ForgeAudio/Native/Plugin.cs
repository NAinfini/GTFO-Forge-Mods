using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace ForgeAudio;

[BepInPlugin(Id, "Forge Audio", Version)]
[BepInDependency("NAinfini.ForgeCore", ">=0.1.0")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = AudioKit.PluginId, Version = "0.1.0";
    private static readonly ForgeCore.ModLog Logger = ForgeCore.Log.For(Id);
    private static ConfigEntry<float>? _master;
    private static readonly ConfigEntry<float>?[] Volumes = new ConfigEntry<float>?[AudioCategories.Count];

    private static ConfigFile? _config;

    /// <summary>Copies the kit's volumes from its config (master and one per global category, every mod's sounds together); runs at load and whenever one of them changes.</summary>
    internal static void ReadVolumes()
    {
        GameContext.KitMaster = _master?.Value ?? 1f;
        for (var c = 0; c < AudioCategories.Count; c++) GameContext.KitVolume[c] = Volumes[c]?.Value ?? 1f;
    }

    private static readonly bool[] HooksInstalled = new bool[Enum.GetValues(typeof(HookGroup)).Length];

    /// <summary>Puts on the hooks of one group the first time a mod claims what they serve (a body sound, a weapon action). Nothing
    /// takes them off again: with no claim they answer at once, and removing a hook would only leave a forwarder behind.</summary>
    internal static void InstallHooks(HookGroup group)
    {
        if (HooksInstalled[(int)group]) return;
        HooksInstalled[(int)group] = true;
        if (group == HookGroup.Weapon) WeaponActions.Subscribe();
        var harmony = new HarmonyLib.Harmony(Id);
        foreach (var type in HarmonyLib.AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(LazyHookAttribute), false) is not { Length: > 0 } found || ((LazyHookAttribute)found[0]).Group != group) continue;
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e)
            {
                if (group == HookGroup.Body) BodySounds.Disable("the game's feedback player could not be hooked: " + e);
                else Logger.Error("The " + group + " hook " + type.Name + " could not be put on the game: " + e);
            }
        }
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

    public override void Load()
    {
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
        // Only failures are written. A voice restarted after the device changed is routine, not a failure.
        AudioLog.Sink = (_, failure) => { if (failure.Code != AudioError.DeviceChanged) Logger.ErrorOnce(failure.ToString()); };
        AudioKit.MainThread = Environment.CurrentManagedThreadId;
        ClipUpload.Initialize();
        AddComponent<AudioDriver>();
        var harmony = new HarmonyLib.Harmony(Id);
        foreach (var type in HarmonyLib.AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.IsDefined(typeof(LazyHookAttribute), false)) continue;
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e)
            {
                BodySounds.Disable("the game's feedback player could not be hooked: " + e);
            }
        }
        SyncPlays.Open();
        AudioKit.Ready = true;
    }
    public override bool Unload()
    {
        AudioDriver.Shutdown();
        GameContext.Unsubscribe();
        AudioKit.Ready = false;
        return true;
    }
}

internal sealed class AudioDriver : MonoBehaviour
{
    private static AudioDriver? _current;
    private static AudioSettings.AudioConfigurationChangeHandler? _deviceCallback;
    public AudioDriver(IntPtr pointer) : base(pointer) { }
    private void Awake()
    {
        _current = this;
        _deviceCallback = (Action<bool>)(_ => Engine.DeviceChanged = true);
        AudioSettings.OnAudioConfigurationChanged += _deviceCallback;
    }
    private void Update()
    {
        if (!AudioKit.Ready) return;
        try { GameContext.Refresh(); Assets.Tick(); SyncPlays.Pump(); Engine.Tick(); Sustains.Tick(); AudioVolumes.Tick(); }
        catch (Exception e)
        {
            AudioLog.Report("driver-update", AudioError.NotReady, "Audio driver update failed: " + e);
            Engine.Clear(EndReason.Failed);
        }
    }
    private void OnDestroy() { if (_current == this) Shutdown(); }
    private void OnDisable() { if (_current == this) Shutdown(); }
    private void OnApplicationPause(bool paused) { GameContext.Suspended = paused; }
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
        GameContext.HasListener = GameContext.Suspended = false;
        AudioKit.Ready = false;
        if (_current != null) { var driver = _current; _current = null; UnityEngine.Object.Destroy(driver); }
    }
}

/// <summary>Which claims a lazily installed hook serves.</summary>
internal enum HookGroup { Body, Weapon }

/// <summary>Marks a Harmony patch class that goes on the game only when <see cref="Plugin.InstallHooks"/> is called for its group.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class LazyHookAttribute : Attribute
{
    public LazyHookAttribute(HookGroup group) { Group = group; }
    internal HookGroup Group { get; }
}
