using System.Linq;
using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using GTFO.API;
using HarmonyLib;
using UnityEngine;

namespace ForgeOrdnance;

[BepInPlugin(Id, "Forge Ordnance", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.0")]
[BepInDependency(ForgeUI.UIKit.PluginId, ">=1.0.0")]
[BepInDependency(ForgeAudio.AudioKit.PluginId, ">=1.0.0")]
[BepInDependency("NAinfini.ForgeVisual", ">=1.0.0")]
// MTFO marks the game as modded (achievements and public matchmaking off) and provides the CustomPath the definitions live in.
[BepInDependency("com.dak.MTFO")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeOrdnance";
    internal const string Version = "1.0.1"; // package-version: NAinfini-ForgeOrdnance
    private static OrdnanceController? _controller;
    private static OrdnanceHud? _hud;

    /// <summary>This mod's ForgeCore log.</summary>
    internal static ForgeCore.ModLog Logger = null!;
    internal static OrdnanceSession Session { get; } = new();

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        ForgeCore.Settings.Adopt(Id, Config);
        // The kit's volumes in the game's audio settings (ForgeAudio holds the values, ForgeUI draws the rows; added once however many packages ask).
        ForgeUI.SettingsPages.AddAudioSliders("ForgeAudio.volumes", ForgeAudio.AudioVolumes.HeadingKey,
            ForgeAudio.AudioVolumes.Controls.Select(c => new ForgeUI.SettingsSlider(c.Id, c.TextKey, c.Get, c.Set)).ToList(), ForgeAudio.AudioVolumes.ResetAll);
        OrdnanceKeys.Register();
        LoadoutStore.Load();

        OrdnanceApi.RegisterBuiltIn(ItemPayload.TypeName, new ItemPayload());
        OrdnanceApi.RegisterBuiltIn(GearPayload.TypeName, new GearPayload());
        OrdnanceNetwork.Register(Session);
        LobbyRules.Register();

        var harmony = new Harmony(Id);
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            SupplyHoldPatch.Subscribe();
            EmptyPackGuard.Subscribe();
            ForgeCore.GameHooks.PickupItemState.After((call, _) => SupplyPickupSound.Changed(call.Old, call.New, call.Recall));
            BeaconLookHook.Subscribe();
            TurretLookHook.Subscribe();
            SummonedTurretStrength.Subscribe();
            SummonedTurretToolSlot.Subscribe();
            SentryMarkerHook.Subscribe();
            _controller = new OrdnanceController(Session);
            Session.CallInProgress = () => _controller.CallInProgress;
            _hud = new OrdnanceHud(_controller);
            ForgeCore.Frame.OnUpdate(Id, Tick);
            ForgeCore.Frame.OnLateUpdate(Id, TurretLooks.LateTick);
            ForgeCore.Frame.OnQuit(Id, OrdnanceCues.Shutdown);
            OrdnanceCues.Load();
            OrdnanceLoadout.Register();
            LobbySync.Register();
            ForgeCore.Items.Spawned += BeaconSpawnHook.OnSpawned;
            GameDataAPI.OnGameDataInitialized += BeaconItem.Register;
            GameDataAPI.OnGameDataInitialized += OrdnanceContent.Load;
            LevelAPI.OnBuildDone += OrdnanceAssets.Load;
            LevelAPI.OnEnterLevel += EnterLevel;
            ForgeCore.GameEvents.LevelEnded += LeaveLevel;
            ForgeCore.GameEvents.CheckpointReached += Session.CheckpointReached;
            // After the channel's own (OrdnanceNetwork.Register): state sent from these is stamped with the epoch the channel just minted.
            ForgeCore.GameEvents.MasterChanged += Session.HostChanged;
            ForgeCore.GameEvents.MasterMigrated += Session.MigrationCompleted;
            ForgeCore.GameEvents.CheckpointReloaded += Session.CheckpointReloaded;
        }
        catch (Exception error)
        {
            Logger.ErrorOnce("Forge Ordnance initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }
    }

    private static void EnterLevel()
    {
        try { Session.EnterLevel(); }
        catch (Exception error) { Logger.ErrorOnce("Entering the level failed for Forge Ordnance: " + error); }
    }

    /// <summary>Each step on its own: one that throws must not leave the others' level state for the next level.</summary>
    private static void LeaveLevel()
    {
        Leave("session", Session.LeaveLevel);
        Leave("sentries", GearPayload.Clear);
        Leave("beacon looks", BeaconLook.Clear);
        Leave("turret looks", TurretLooks.Clear);
        Leave("controller", () => _controller?.LevelLeft());
    }

    private static void Leave(string what, Action step)
    {
        try { step(); }
        catch (Exception error) { Logger.ErrorOnce("Leaving the level failed for Forge Ordnance (" + what + "): " + error); }
    }

    public override bool Unload() => false;

    /// <summary>Every frame, from ForgeCore's <c>Frame.OnUpdate</c>: the level controller, then its display.</summary>
    internal static void Tick()
    {
        LobbyRules.Tick();
        LobbySync.Tick();
        _controller?.Update();
        Deliveries.Tick();
        OrdnanceArrival.Tick();
        OrdnanceClaims.Tick(Time.time);
        BeaconLook.Tick();
        if (_controller != null) _hud?.Update(_controller, Session);
    }
}

