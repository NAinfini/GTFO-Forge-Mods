using System;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgePlayer.Native;

/// <summary>The Player domain's start: the engine's Load calls it once, in its fixed domain order, while registration is open.</summary>
internal static class PlayerHost
{
    /// <summary>The domain's ForgeCore owner id (net channel, content reader, screen and input owner).</summary>
    internal const string ModId = "NAinfini.ForgeEngine.Player";
    /// <summary>The started Player session, or null when the Player domain did not start.</summary>
    internal static PlayerPluginSession? Session { get; private set; }
    /// <summary>The one player directory every domain names players through: the started session attaches its identity
    /// half, and while none is attached every read is refused with <see cref="PlayerDirectoryService.MissingCode"/>.</summary>
    internal static PlayerDirectory Directory { get; } = new();
    /// <summary>The directory while a Player session is running, otherwise null: for a caller that names its own refusal.</summary>
    internal static PlayerDirectory? Players => Directory.Running ? Directory : null;

    internal static void Start(IForgeEngine kernel, RuntimeLogLevel logLevel, BepInEx.Logging.ManualLogSource log)
    {
        PlayerPluginSession? created = null;
        IDisposable? membership = null, postEvents = null, work = null;
        using var timing = new ForgeCore.StartupTiming(ModId,
            message => { if (ForgeCore.Log.Developer) log.LogInfo(message); created?.WriteLog(RuntimeLogLevel.Info, "player.startup", message); }); // BepInEx keeps the whole line; the runtime log caps detail text.
        try
        {
            timing.Mark("session");
            created = PlayerPluginSession.Start(kernel, logLevel, null, null,
                () => { timing.Mark("native-declarations"); return PlayerNativeHooks.Declare(); },
                () => { try { work?.Dispose(); } finally { try { postEvents?.Dispose(); } finally { membership?.Dispose(); } } });
            Session = created;
            timing.Mark("world-leases");
            // Identity first: a refused membership lease refuses the whole package before any other lease is taken.
            membership = PlayerMembershipReadback.Subscribe(created.Registration!, kernel);
            postEvents = PlayerGameEventPosted.Subscribe(created.Registration!, kernel);
            work = new DomainWorkPhases("forge.player.frame", 0, created.Registration!, kernel, created.InFrame);
            timing.Mark("bot-roster");
            created.UseBotRoster(BotRosterContent.Read(Paths.BepInExRootPath,
                message => created.WriteLog(RuntimeLogLevel.Error, BotRosterContent.RefusedCode, message)));
            timing.Mark("carry-on-back");
            created.UseCarryOnBack(CarryOnBack.Start(CarryOnBack.Read(Paths.BepInExRootPath,
                message => created.WriteLog(RuntimeLogLevel.Error, CarryOnBack.RefusedCode, message)),
                message => created.WriteLog(RuntimeLogLevel.Error, CarryOnBack.UnavailableCode, message)));
            // The game keeps a player's movement-speed modifier in its table, but its player locomotion never reads it (only enemy path
            // movement calls ApplyModifier for that member), so each peer scales its own player's walk by the table's aggregate.
            ForgeCore.PlayerStats.Walk(static (owner, velocity)
                => velocity * AgentModifierManager.ApplyModifier(owner, AgentModifier.MovementSpeed, 1f));
            created.WriteLog(RuntimeLogLevel.Info, "player.startup", "Player registration and native declarations are ready; refused targets="
                + created.Hooks.Refused.Count + " unavailable features=" + created.Hooks.Unavailable.Count + ".");
            timing.Complete();
        }
        catch (Exception original)
        {
            try
            {
                if (created != null)
                {
                    try
                    {
                        created.WriteLog(RuntimeLogLevel.Error, "player.native-refused",
                        "Player startup refused: " + original.GetType().Name + ": " + original.Message);
                    }
                    catch (Exception reporting) { original.Data["ForgePlayer.StartupReportingFailure"] = reporting; }
                    try { created.Dispose(); }
                    catch (Exception cleanup)
                    {
                        DomainSession.PreserveCleanupFailure(original, "ForgePlayer.LoadCleanupFailure", cleanup,
                        message => created.WriteLog(RuntimeLogLevel.Error, "player.cleanup-failed", message));
                    }
                }
            }
            finally { Session = null; }
            throw;
        }
    }

}
