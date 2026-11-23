using System;
using System.Collections.Generic;
using BepInEx;
using ForgeEngine.Framework;

namespace ForgeWeapon.Native;

/// <summary>The Weapon domain's start: the engine's Load calls it once, in its fixed domain order, while registration is open.</summary>
internal static class WeaponHost
{
    /// <summary>The domain's ForgeCore owner id (net channel, content reader, screen and input owner).</summary>
    internal const string ModId = "NAinfini.ForgeEngine.Weapon";
    private const string StartupCode = "weapon.startup";

    internal static void Start(IForgeEngine kernel, RuntimeLogLevel logLevel, BepInEx.Logging.ManualLogSource log)
    {
        WeaponNativeSession? created = null;
        // Messages raised while the session is still starting wait here and are written once its log is attached.
        var early = new List<(RuntimeLogLevel Level, string Message)>();
        void Write(RuntimeLogLevel level, string message)
        {
            if (created != null) created.Log.Text(level, StartupCode, message);
            else early.Add((level, message));
        }
        using var timing = new ForgeCore.StartupTiming(ModId, message => { if (ForgeCore.Log.Developer) log.LogInfo(message); Write(RuntimeLogLevel.Info, message); }); // BepInEx keeps the whole line; the runtime log caps detail text.
        try
        {
            timing.Mark("session");
            // One declaration pass: every callback class is handed to Runtime, which is the only installer. A refused
            // target withdraws only the weapon function that owns it, with Runtime
            created = WeaponNativeSession.Start(kernel, logLevel, () => ForgeEngine.Plugin.CanExecuteGameplay,
                () => ForgeEngine.Plugin.CanExecuteOwnerGameplay,
                message => Write(RuntimeLogLevel.Error, message),
                message => Write(RuntimeLogLevel.Info, message),
                () =>
                {
                    timing.Mark("native-declarations");
                    var withdrawn = WeaponNativeHooks.Declare();
                    timing.Mark("session-finalization");
                    return withdrawn;
                },
                Paths.BepInExRootPath,
                // The one body whose signature a game-free build cannot carry: the launch row's spawn call reaches
                // a native prefab. It is built from this side, so the session declares the row only because a body
                // was really supplied for it, and the body's own readiness gate is the session's.
                gate => new WeaponProjectileActions(kernel, gate, message => Write(RuntimeLogLevel.Error, message)).Launch);
            foreach (var (level, message) in early) created.Log.Text(level, StartupCode, message);
            early.Clear();
            // A refusal is applied before the log exists, so it is named here: a withdrawn function is never silent.
            foreach (var (feature, reason) in created.Withdrawn)
                created.Log.Text(RuntimeLogLevel.Error, StartupCode, "withdrawn function " + WeaponFeatures.Label(feature) + ": " + reason);
            // In Authoring every per-frame unit goes on with the first content generation, so a reload never installs a hook.
            ForgeEngine.GameBindings.GameRuntimeBridge.RegisterOptionalHooks("weapon", WeaponLazyHooks.EnsureAll);
            timing.Mark("loaded-log");
            // Two statements, two records.
            created.Log.Text(RuntimeLogLevel.Info, StartupCode,
                "Forge Weapon authored equipment, shot, hit, reload, melee-hit and deployed-device facts"
                + " with gtfo.player owners.");
            created.Log.Text(RuntimeLogLevel.Info, StartupCode,
                "Forge Weapon executes the game's own enemy tag, the ammunition add/consume pair, the three weapon"
                + " instance overrides and the inventory give/consume pair; native bindings remain implementation-only.");
            timing.Complete();
        }
        catch (Exception original)
        {
            if (created != null)
            {
                try { created.Dispose(); }
                catch (Exception cleanup)
                {
                    DomainSession.PreserveCleanupFailure(original, "ForgeWeapon.LoadCleanupFailure", cleanup,
                        message => Write(RuntimeLogLevel.Error, message));
                }
            }
            throw;
        }
    }

}
