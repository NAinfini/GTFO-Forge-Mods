using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using ForgeCore;
using GTFO.API;

namespace ForgeReplay;

[BepInPlugin(Id, "Forge Replay", Version)]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.4")] // package-version: NAinfini-ForgeCore
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = "NAinfini.ForgeReplay", Version = "1.0.3"; // package-version: NAinfini-ForgeReplay

    internal static ModLog Logger { get; private set; } = null!;
    internal static ConfigEntry<bool> RecordingEnabled = null!;
    internal static ConfigEntry<int> DiskLimitMb = null!;
    /// <summary>Where recordings are kept, the one place recording, the list, the disk limit and the statistics export take it from:
    /// the folder the player set in <c>Recording.Folder</c>, else <c>replay/</c> beside the game (one place for every mod manager profile).</summary>
    internal static string ReplayFolder = "";
    /// <summary>The folder the player set that could not be used (the replay list says so once), or null.</summary>
    internal static string? UnusableFolder;

    public override void Load()
    {
        Logger = ForgeCore.Log.For(Id);
        var chosen = Config.Bind("Recording", "Folder", "", "Where recordings are kept, as a full path. Empty: the replay folder beside the game, which every mod manager profile shares. A folder that does not exist is created; one that cannot be created or written is not used, the replay folder beside the game is, and the replays list says so. Changing it does not move the recordings already in the old place: move them yourself. Takes effect when the game starts.").Value;
        ReplayFolder = ChooseFolder(chosen, Path.Combine(Paths.GameRootPath, "replay"));
        RecordingEnabled = Config.Bind("Recording", "Enabled", true, "Record every level into the replay folder (Recording.Folder).");
        // The content fingerprint is for the header of a recording; with recording off it is read only when a list or a replay asks for it.
        if (RecordingEnabled.Value)
        {
            Session.PrepareContent();
            IL2CPPChainloader.Instance.Finished += Session.PreparePlugins;
        }
        DiskLimitMb = Config.Bind("Recording", "DiskLimitMB", 200, "The most the replay folder may take, in MB (0: no limit). Past it the oldest recordings that are not starred are deleted, as are those beyond the newest 20; starred ones are never deleted.");
        LevelAPI.OnBuildDone += Session.BuildDone;
        LevelAPI.OnLevelCleanup += Session.LevelCleanup;
        LevelAPI.OnLevelCleanup += Replayer.Stop;
        LevelAPI.OnEnterLevel += Replayer.Entered;
        EventAPI.OnGameStateChanged += Session.StateChanged;
        Hooks.Install();
        FirstPerson.Install();
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
        Frame.OnUpdate(Id, Session.RaiseFinished);
        Frame.OnUpdate(Id, Replayer.Tick);
        Frame.OnUpdate(Id, ReplayList.Tick);
        Frame.OnUpdate(Id, ReplayScreen.Tick);
    }

    /// <summary>The player's folder when it is a full path that exists or can be made and takes a file; otherwise <paramref name="standard"/>.</summary>
    private static string ChooseFolder(string chosen, string standard)
    {
        if (string.IsNullOrWhiteSpace(chosen)) return standard;
        try
        {
            var folder = chosen.Trim();
            if (!Path.IsPathRooted(folder)) throw new ArgumentException("not a full path");
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".forge-replay-write-test");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return Path.GetFullPath(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            UnusableFolder = chosen;
            Logger.Error("Recording.Folder " + chosen + " cannot be used, recordings stay in " + standard + ": " + error.Message);
            return standard;
        }
    }

    public override bool Unload() => false;
}
