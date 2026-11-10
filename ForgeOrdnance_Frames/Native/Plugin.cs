using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using ForgeAudio;
using ForgeOrdnance;
using GTFO.API;
using HarmonyLib;

namespace ForgeOrdnanceFrames;

[BepInPlugin(Id, "Forge Ordnance Frames", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.0")]
[BepInDependency("NAinfini.ForgeOrdnance", ">=1.0.1")]
[BepInDependency(AudioKit.PluginId, ">=1.0.0")]
[BepInDependency(ForgeUI.UIKit.PluginId, ">=1.0.0")]
[BepInDependency("NAinfini.ForgeVisual", ">=1.0.0")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeOrdnanceFrames";
    internal const string Version = "1.0.1"; // package-version: NAinfini-ForgeOrdnance_Frames

    /// <summary>This mod's ForgeCore log; `ErrorOnce` writes each distinct error text once.</summary>
    internal static ForgeCore.ModLog Logger = null!;

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);

        FrameKeys.Register();
        // The exosuit tab: between Weapon and Offensive, in the colour of the suit beacons.
        var exosuitTab = ForgeUI.UIKit.Mod(Id);
        if (!OrdnanceApi.RegisterCategory("Exosuit", () => exosuitTab.Text("category.Exosuit"), 3, 0xFFC23D))
        {
            Logger.ErrorOnce("Forge Ordnance refused the Exosuit category (see its log line above); exosuits are unavailable.");
            return;
        }
        if (!OrdnanceApi.RegisterPayload(FrameSpecParser.TypeName, new FramesPayload()))
        {
            Logger.ErrorOnce("Forge Ordnance refused the exosuit call-in type (see its log line above); exosuits are unavailable.");
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
            FramesFxSlots.Load();
            LevelAPI.OnBuildDone += FrameAssets.LevelBuilt;
            LevelAPI.OnBuildDone += PilotBody.Load;
            LevelAPI.OnEnterLevel += FrameWorld.LevelEntered;
            ForgeCore.GameEvents.MasterChanged += () => Guard("host change", FrameWorld.AuthorityChanged);
            ForgeCore.GameEvents.LevelEnded += () => Guard("level cleanup", FrameWorld.LevelCleanup);
            ForgeCore.GameEvents.CheckpointReached += () => Guard("checkpoint", FrameWorld.CheckpointReached);
            ForgeCore.GameEvents.CheckpointReloaded += () => Guard("checkpoint reload", FrameWorld.CheckpointReloaded);
        }
        catch (Exception error)
        {
            Logger.ErrorOnce("Forge Ordnance Frames initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }
    }

    private static void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception error) { Logger.ErrorOnce("Exosuit " + what + " failed: " + error); }
    }

    public override bool Unload() => false;
}


/// <summary>Also handles a migration that recalls the same master identity.</summary>
internal static class FramesMigrationCompleted
{
    internal static void Subscribe() => ForgeCore.GameHooks.MasterMigrationFound.After((_, _) => FrameWorld.MigrationCompleted());
}
