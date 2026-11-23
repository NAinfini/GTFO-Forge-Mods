using System;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEnemy.Native;

/// <summary>The Enemy domain's start: the engine's Load calls it once, in its fixed domain order, while registration is open.</summary>
internal static class EnemyHost
{
    /// <summary>The domain's ForgeCore owner id (net channel, content reader, screen and input owner).</summary>
    internal const string ModId = "NAinfini.ForgeEngine.Enemy";
    internal static EnemyPluginSession? Session { get; private set; }

    /// <summary>The live session when the native function is available on this build, otherwise null: the hook body of
    /// a withdrawn function returns before it touches the game.</summary>
    internal static EnemyPluginSession? Offer(EnemyFeature feature)
        => Session is { } session && session.Hooks.Offers(feature) ? session : null;

    /// <summary>Whether the function stands, for a hook whose body reads static tables and needs no session.</summary>
    internal static bool Stands(EnemyFeature feature) => Offer(feature) != null;

    internal static void Start(IForgeEngine kernel, RuntimeLogLevel logLevel, BepInEx.Logging.ManualLogSource log)
    {
        EnemyPluginSession? created = null;
        DomainWorkPhases? work = null;
        DomainWorldObservation? damageKinds = null;
        using var timing = new ForgeCore.StartupTiming(ModId,
            message => { if (ForgeCore.Log.Developer) log.LogInfo(message); created?.WriteLog(RuntimeLogLevel.Info, EnemyLogCodes.Startup, message); }); // BepInEx keeps the whole line; the runtime log caps detail text.
        try
        {
            timing.Mark("session");
            // The Runtime is the only installer: the declaration is one call, and a refusal of a callback class withdraws
            // only the function that owns it (its bindings are not registered and its hook bodies are inert).
            created = EnemyPluginSession.Start(kernel, logLevel, () => ForgeEngine.Plugin.CanExecuteGameplay, null,
                () => { timing.Mark("native-declarations"); return EnemyNativeHooks.Declare(() => EnemyDamageKindEntries.Probe(kernel)); },
                () => { try { work?.Dispose(); } finally { damageKinds?.Dispose(); } });
            Session = created;
            foreach (var withdrawn in created.Hooks.Withdrawn.Keys)
                created.WriteLog(RuntimeLogLevel.Error, EnemyLogCodes.CapabilityUnavailable, EnemyHookFeatures.Describe(created.Hooks, withdrawn));
            if (created.Module.Log.Logs(RuntimeLogLevel.Info))
                created.Module.Log.Write(RuntimeLogLevel.Info, EnemyLogCodes.HooksInstalled, new RuntimeLogFacts
                {
                    Values = new[]
                    {
                        RuntimeLogValue.Of("classes", (long)EnemyNativeHooks.Types.Count),
                        RuntimeLogValue.Of("pending", (long)EnemyNativeHooks.Pending.Count),
                        RuntimeLogValue.Of("withdrawnFeatures", (long)created.Hooks.Withdrawn.Count)
                    }
                });
            timing.Mark("content-and-components");
            // Enemy profiles are static data, so they load once with the package and never during a level: a
            // document that is refused is reported here instead of surprising an author mid-run.
            timing.Mark("profiles");
            created.Module.LoadProfiles(EnemyProfileStore.Read(Paths.BepInExRootPath));
            foreach (var unused in created.Module.AuthoredButUnavailable(created.Hooks))
                created.WriteLog(RuntimeLogLevel.Error, EnemyLogCodes.CapabilityUnavailable,
                    "feature=" + EnemyHookFeatures.Of(unused).Name + " content=authored in a loaded enemy profile and not applied reason=" + created.Hooks.Withdrawn[unused]);
            timing.Mark("content-registration");
            created.Module.RegisterContent();
            timing.Mark("components");
            // The in-level frame driver: the native phase sweep publishes a fact, which a read-only lifecycle
            // observer may not do.
            work = new DomainWorkPhases("forge.enemy.frame", 2, created.Module.Registration, kernel, created.InFrame, perTick: true);
            // The bullet and melee receivers are shared with the weapon package: the Runtime owns their install and
            // this package observes the damage-kind window read-only.
            if (created.Hooks.Offers(EnemyFeature.DamageKind))
                damageKinds = EnemyDamageKindEntries.Observe(created.Module.Registration, kernel);
            created.WriteLog(RuntimeLogLevel.Info, EnemyLogCodes.Startup,
                "Enemy registration and native declarations are ready; classes=" + EnemyNativeHooks.Types.Count
                + " pending=" + EnemyNativeHooks.Pending.Count + " withdrawnFeatures=" + created.Hooks.Withdrawn.Count + ".");
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
                        created.WriteLog(RuntimeLogLevel.Error, EnemyLogCodes.NativeRefused,
                        "Enemy startup refused: " + original.GetType().Name + ": " + original.Message);
                    }
                    catch (Exception reporting) { original.Data["ForgeEnemy.StartupReportingFailure"] = reporting; }
                    try { created.Dispose(); }
                    catch (Exception cleanup)
                    {
                        DomainSession.PreserveCleanupFailure(original, "ForgeEnemy.LoadCleanupFailure", cleanup,
                        message => created.WriteLog(RuntimeLogLevel.Error, EnemyLogCodes.CleanupFailed, message));
                    }
                }
            }
            finally { Session = null; }
            throw;
        }
    }

}
