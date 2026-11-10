using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeCore;
using GTFO.API;

namespace ForgeReplay;

[BepInPlugin(Id, "Forge Replay", Version)]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.0")]
[BepInDependency("NAinfini.ForgeUI", ">=1.0.0")]
[BepInDependency("NAinfini.ForgeAudio", ">=1.0.0")]
[BepInDependency("NAinfini.ForgeVisual", ">=1.0.0")]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeReplay", Version = "1.0.0"; // package-version: NAinfini-ForgeReplay

    internal static ModLog Logger { get; private set; } = null!;
    /// <summary>The BepInEx log source, for the host-sync diagnostic lines ("[sync]") that are to be removed once the multiplayer test has confirmed them.</summary>
    internal static BepInEx.Logging.ManualLogSource Diagnostic { get; private set; } = null!;
    internal static ConfigEntry<bool> RecordingEnabled = null!;
    internal static ConfigEntry<int> DiskLimitMb = null!;
    internal static ConfigEntry<bool> LogRemoteAnimatorStates = null!;
    internal static string ReplayFolder = "";

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        Diagnostic = Log;
        ReplayFolder = Path.Combine(Paths.GameRootPath, "replay");
        RecordingEnabled = Config.Bind("Recording", "Enabled", true, "Record every level into the replay folder beside the game.");
        // The content fingerprint is for the header of a recording; with recording off it is read only when a list or a replay asks for it.
        if (RecordingEnabled.Value)
        {
            Session.PrepareContent();
            IL2CPPChainloader.Instance.Finished += Session.PreparePlugins;
        }
        DiskLimitMb = Config.Bind("Recording", "DiskLimitMB", 200, "The most the replay folder may take, in MB (0: no limit). Past it the oldest recordings that are not starred are deleted, as are those beyond the newest 20; starred ones are never deleted.");
        LogRemoteAnimatorStates = Config.Bind("Recording", "LogRemoteAnimatorStates", false, "Development only: while recording, write the animation states other players' bodies play to replay/animator-states.log (for finding the numbers a body copy needs). Off by default: it reads every animator layer ten times a second and builds a line for each change.");
        LevelAPI.OnBuildDone += Session.BuildDone;
        LevelAPI.OnLevelCleanup += Session.LevelCleanup;
        LevelAPI.OnLevelCleanup += Replayer.Stop;
        LevelAPI.OnEnterLevel += Replayer.Entered;
        EventAPI.OnGameStateChanged += Session.StateChanged;
        Hooks.Install();
        FirstPerson.Install();
        Listening.Install();
        HostSync.Install();
        var ui = ForgeUI.UIKit.Mod(Id);
        ReplayList.Load(ui);
        ReplayScreen.Load(ui);
        // The sounds of the HUD's buttons are the game's own menu sounds; ForgeUI has a hook and no audio, so this mod joins the two.
        ForgeUI.HudPointer.ButtonSound = kind =>
        {
            var name = kind switch { ForgeUI.HudPointer.ButtonSoundKind.Hover => "GAME_MENU_HOVER", ForgeUI.HudPointer.ButtonSoundKind.Click => "GAME_MENU_CONFIRM", _ => null };
            if (name != null && ForgeAudio.AudioKit.TryGameEventId(name, out var id)) ForgeAudio.AudioKit.PlayGameEvent(id);
        };
        // One callback per subsystem: ForgeCore reports one that throws and the others still run.
        Frame.OnUpdate(Id, PlayerRecorder.Tick);
        Frame.OnUpdate(Id, HostSync.Tick);
        Frame.OnUpdate(Id, Session.HeaderTick);
        Frame.OnUpdate(Id, Session.WatchReplayLoad);
        Frame.OnUpdate(Id, Replayer.Tick);
        Frame.OnUpdate(Id, ReplayList.Tick);
        Frame.OnUpdate(Id, ReplayScreen.Tick);
    }

    public override bool Unload() => false;
}
