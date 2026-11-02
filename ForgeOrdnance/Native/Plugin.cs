using System.Linq;
using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using GTFO.API;
using HarmonyLib;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

[BepInPlugin(Id, "Forge Ordnance", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore")]
[BepInDependency(ForgeUI.UIKit.PluginId)]
[BepInDependency(ForgeAudio.AudioKit.PluginId)]
[BepInDependency("NAinfini.ForgeVisual")]
// MTFO marks the game as modded (achievements and public matchmaking off) and provides the CustomPath the definitions live in.
[BepInDependency("com.dak.MTFO")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeOrdnance";
    internal const string Version = "1.0.0";
    private static OrdnanceController? _controller;
    private static OrdnanceHud? _hud;

    /// <summary>This mod's ForgeCore log.</summary>
    internal static ForgeCore.ModLog Logger = null!;
    internal static OrdnanceSession Session { get; } = new();

    /// <summary>A fault that can repeat: ForgeCore's report-once writes each exact text once, so a failing frame cannot flood the log.</summary>
    internal static void Error(string message) => Logger.ErrorOnce(message);

    /// <summary>A load-time message that happens once per file or entry (content, settings, saved loadout): written as it is.</summary>
    internal static void Report(string message) => Logger.Error(message);

    /// <summary>ForgeCore's position formatting, for error text.</summary>
    internal static string Pos(Vector3 v) => ForgeCore.Log.Pos(v);

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        ForgeCore.Settings.Adopt(Id, Config);
        // The kit's volumes in the game's audio settings (ForgeAudio holds the values, ForgeUI draws the rows; added once however many packages ask).
        ForgeUI.SettingsPages.AddAudioSliders("ForgeAudio.volumes",
            ForgeAudio.AudioVolumes.HeadingEnglish, ForgeAudio.AudioVolumes.HeadingChinese,
            ForgeAudio.AudioVolumes.Controls.Select(c => new ForgeUI.SettingsSlider(c.Id, c.English, c.Chinese, c.Get, c.Set)).ToList(), ForgeAudio.AudioVolumes.ResetAll);
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
            SummonedTurretDeployWrite.Subscribe();
            SummonedTurretSpawnGuard.Subscribe();
            SummonedTurretLifecycleGuard.Subscribe();
            SentryMarkerHook.Subscribe();
            MigrationCompleted.Subscribe();
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
            LevelAPI.OnLevelCleanup += LeaveLevel;
            EventAPI.OnCheckpointReached += Session.CheckpointReached;
            EventAPI.OnCheckpointReloaded += Session.CheckpointReloaded;
        }
        catch (Exception error)
        {
            Error("Forge Ordnance initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }
    }

    private static void EnterLevel()
    {
        try { Session.EnterLevel(); }
        catch (Exception error) { Error("Entering the level failed for Forge Ordnance: " + error); }
    }

    private static void LeaveLevel()
    {
        try
        {
            Session.LeaveLevel();
            GearPayload.Clear();
            BeaconLook.Clear();
            TurretLooks.Clear();
            _controller?.LevelLeft();
        }
        catch (Exception error) { Error("Leaving the level failed for Forge Ordnance: " + error); }
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


/// <summary>Vanilla has chosen and recalled a replacement host. ForgeCore reports a changed master as an event; this hook covers a
/// migration that ends with the same master lookup.</summary>
internal static class MigrationCompleted
{
    internal static void Subscribe() => ForgeCore.GameHooks.MasterMigrationFound.After((_, _) => Completed());

    private static void Completed()
    {
        try { Plugin.Session.MigrationCompleted(); }
        catch (Exception error) { Plugin.Error("Host migration hook failed: " + error); }
    }
}
