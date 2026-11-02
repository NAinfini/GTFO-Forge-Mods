using System;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeAudio;
using ForgeOrdnance;
using GTFO.API;
using HarmonyLib;
using UnityEngine;

namespace ForgeOrdnanceFrames;

[BepInPlugin(Id, "Forge Ordnance Frames", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore")]
[BepInDependency("NAinfini.ForgeOrdnance")]
[BepInDependency(AudioKit.PluginId)]
[BepInDependency(ForgeUI.UIKit.PluginId)]
[BepInDependency("NAinfini.ForgeVisual")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeOrdnanceFrames";
    internal const string Version = "1.0.0";

    /// <summary>This mod's ForgeCore log.</summary>
    internal static ForgeCore.ModLog Logger = null!;

    internal static ConfigEntry<bool> ExtraLights = null!;

    /// <summary>Every distinct error text is logged once (ForgeCore's report-once).</summary>
    internal static void Error(string message) => Logger.ErrorOnce(message);

    internal static string Num(float v) => ForgeCore.Log.Num(v);

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);

        ForgeCore.Settings.Adopt(Id, Config);
        FrameKeys.Register();
        ExtraLights = ForgeCore.Settings.Bind(Id, "Presentation", "Extra lights", true, "Point lights on a suit's destruction flash, its burning wreck and the planted siege lamp.");
        // The exosuit tab: between Weapon and Offensive, in the colour of the suit beacons.
        var exosuitTab = ForgeUI.UIKit.Mod(Id);
        if (!OrdnanceApi.RegisterCategory("Exosuit", () => exosuitTab.Text("category.Exosuit"), 3, 0xFFC23D))
        {
            Error("Forge Ordnance refused the Exosuit category (see its log line above); exosuits are unavailable.");
            return;
        }
        if (!OrdnanceApi.RegisterPayload(FrameSpecParser.TypeName, new FramesPayload()))
        {
            Error("Forge Ordnance refused the exosuit call-in type (see its log line above); exosuits are unavailable.");
            return;
        }
        FrameNetwork.Register();
        var harmony = new Harmony(Id);
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            PilotBullet.Subscribe();
            FramesMigrationCompleted.Subscribe();
            ForgeCore.PlayerHits.Redirect(Id, PilotDamage.Redirect);
            ForgeCore.PlayerHits.GateInfection(Id, PilotInfection.Allow);
            ForgeCore.Frame.OnUpdate(Id, FrameWorld.Tick);
            ForgeCore.Frame.OnLateUpdate(Id, FrameWorld.LateTick);
            ForgeCore.Frame.OnQuit(Id, FrameAudio.Dispose);
            FrameAudio.Load();
            LocalPilot.RegisterHud();
            FrameAssets.Register();
            LevelAPI.OnBuildDone += FrameAssets.LevelBuilt;
            LevelAPI.OnBuildDone += PilotBody.Load;
            LevelAPI.OnEnterLevel += FrameWorld.LevelEntered;
            ForgeCore.GameEvents.MasterChanged += () => Guard("host change", FrameWorld.AuthorityChanged);
            LevelAPI.OnLevelCleanup += () => Guard("level cleanup", FrameWorld.LevelCleanup);
            EventAPI.OnCheckpointReached += () => Guard("checkpoint", FrameWorld.CheckpointReached);
            EventAPI.OnCheckpointReloaded += () => Guard("checkpoint reload", FrameWorld.CheckpointReloaded);
        }
        catch (Exception error)
        {
            Error("Forge Ordnance Frames initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }
    }

    private static void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception error) { Error("Exosuit " + what + " failed: " + error); }
    }

    public override bool Unload() => false;
}


/// <summary>Also handles a migration that recalls the same master identity.</summary>
internal static class FramesMigrationCompleted
{
    internal static void Subscribe() => ForgeCore.GameHooks.MasterMigrationFound.After((_, _) => FrameWorld.MigrationCompleted());
}
