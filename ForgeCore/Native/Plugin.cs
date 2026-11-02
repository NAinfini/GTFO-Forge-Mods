using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using GTFO.API;
using UnityEngine;

namespace ForgeCore;

[BepInPlugin(Id, "Forge Core", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeCore", Version = "0.1.0";

    internal static ModLog Logger { get; private set; } = null!;

    public override void Load()
    {
        var own = base.Log;
        Settings.Adopt(Id, Config);
        ForgeCore.Log.Sinks = id =>
        {
            var source = id == Id ? own : BepInEx.Logging.Logger.CreateLogSource(id);
            return message => source.LogError(message);
        };
        Logger = ForgeCore.Log.For(Id);
        Store.Root = Path.Combine(Paths.BepInExRootPath, "ForgeCore", "Store");
        Net.Transport = new GtfoTransport();
        Patches.CheckTargets();
        HoldPoint.NeedShutHook = static () => Patches.Ensure(Modules.HoldPointShut, typeof(HoldPointShutHook));
        // Before any dependent mod subscribes, so their own build-done handlers already find the level's dimensions.
        LevelAPI.OnBuildDone += Level.ReadDimensions;
        LevelAPI.OnLevelCleanup += () =>
        {
            Level.SetDimensions(0);
            StackedVolumes.Clear();
            HoldPoint.ForgetAll();
            InputLease.ReleaseAll();
            CameraLease.ReleaseAll();
            Enemies.Clear();
            SpecialSlot.Local.Clear();
        };
        // Before any dependent mod subscribes, so their own checkpoint handlers read the recalled slot.
        EventAPI.OnCheckpointReached += SpecialSlotHooks.CheckpointReached;
        EventAPI.OnCheckpointReloaded += SpecialSlotHooks.CheckpointReloaded;
        try
        {
            ItemSpawnHook.Start();
        }
        catch (Exception error) { Logger.Error("The Items.Spawned hook could not be put on; handlers of Items.Spawned will not run: " + error); }
        // After ForgeCore's own cleanup and recall, so a mod's GameEvents handler sees them done.
        GameEventSources.Start();
        AddComponent<CoreDriver>();
    }

    public override bool Unload() => false;
}

/// <summary>The only injected type: it answers network handshakes, runs the enemy slow zones' passes and every mod's <see cref="Frame"/> callbacks. Nothing here
/// allocates on an ordinary frame.</summary>
internal sealed class CoreDriver : MonoBehaviour
{
    public CoreDriver(IntPtr pointer) : base(pointer) { }

    private void Update()
    {
        try
        {
            Net.Tick(Time.realtimeSinceStartup);
            SoloDrop.Tick();
            Enemies.TickZones(Time.time);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("ForgeCore frame update failed: " + error); }
        Frame.Updates.Run();
    }

    private void LateUpdate() => Frame.LateUpdates.Run();

    private void OnDestroy() => Frame.Quits.Run();
}
