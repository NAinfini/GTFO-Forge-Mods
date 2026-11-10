using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using GTFO.API;
using SNetwork;
using UnityEngine;

namespace ForgeCore;

[BepInPlugin(Id, "Forge Core", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeCore", Version = "1.0.0"; // package-version: NAinfini-ForgeCore

    internal static ModLog Logger { get; private set; } = null!;

    /// <summary>[diag1110] Info lines while the modifier path is confirmed in game; removed with them.</summary>
    internal static BepInEx.Logging.ManualLogSource Diag { get; private set; } = null!;

    public override void Load()
    {
        var own = base.Log;
        Diag = own;
        // One startup.timing line per library too, so a slow start can be told apart from the packages that depend on it.
        using var timing = new StartupTiming(Id, message => own.LogInfo(message));
        Settings.Adopt(Id, Config);
        ForgeCore.Log.Sinks = id =>
        {
            var source = id == Id ? own : BepInEx.Logging.Logger.CreateLogSource(id);
            return message => source.LogError(message);
        };
        Logger = ForgeCore.Log.For(Id);
        // Before any package that depends on ForgeCore loads: every Il2Cpp entry a Forge package patches gets the same prologue check.
        HarmonyLib.Public.Patching.PatchManager.ResolvePatcher += Hooks.Resolve;
        if (HookBridgeBackend.Mismatch() is { } bridge) Logger.ErrorOnce("core-hook-bridge-mismatch", bridge);
        Store.Root = Path.Combine(Paths.BepInExRootPath, "ForgeCore", "Store");
        Net.Transport = new GtfoTransport();
        Net.Clock = static () => Time.time;
        Net.WorldProbe = static () => GameStateManager.CurrentStateName == eGameStateName.InLevel
            && SNet.MasterManagement != null && !SNet.MasterManagement.IsMigrating && SNet.Master != null;
        Patches.CheckTargets();
        // Once every plugin has loaded (and declared its hooks at load), each owned run-time target must still have one package.
        IL2CPPChainloader.Instance.Finished += Hooks.CheckAll;
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
            Gear.ForgetToolSlotSentries();
        };
        // Before any dependent mod subscribes, so their own checkpoint handlers read the recalled slot.
        EventAPI.OnCheckpointReached += SpecialSlotHooks.CheckpointReached;
        EventAPI.OnCheckpointReloaded += SpecialSlotHooks.CheckpointReloaded;
        try
        {
            ItemSpawnHook.Start();
        }
        catch (Exception error) { Logger.Error("core-items-spawned-hook", "The Items.Spawned hook could not be put on; handlers of Items.Spawned will not run: " + error); }
        ReplicationMisses.Start(message => own.LogInfo(message));
        // After ForgeCore's own cleanup and recall, so a mod's GameEvents handler sees them done.
        GameEventSources.Start();
        LandingWork.Start();
        Frame.Stamp();
        AddComponent<CoreDriver>();
        timing.Complete();
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
        Frame.Stamp();
        try
        {
            Net.Tick(Time.realtimeSinceStartup);
            SoloDrop.Tick();
            Enemies.TickZones(Time.time);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-frame-update-failed", "ForgeCore frame update failed: " + error); }
        Frame.Updates.Run();
    }

    private void LateUpdate() => Frame.LateUpdates.Run();

    private void FixedUpdate() => Frame.FixedUpdates.Run();

    private void OnDestroy() => Frame.Quits.Run();
}
