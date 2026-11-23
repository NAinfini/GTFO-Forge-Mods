using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeEngine.Presentation.Native;
using ForgeEngine.Trigger.Native;

namespace ForgeEngine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
// A versioned dependency is always a hard dependency, and the version argument is a SemVer range rather than a
// floor: a bare version would refuse to load on any newer GTFO-API at all. The host therefore requires the
// release it was built against or a later one, instead of silently binding level events that may have moved.
[BepInDependency(GtfoApiGuid, GtfoApiMinimumVersion)]
// MTFO is a hard dependency so that every Forge stack runs as a modded game and is never played in the
// official Rundown.
[BepInDependency(MtfoGuid, MtfoMinimumVersion)]
// The public library ForgeCore holds the game calls, screen, sound and world visuals every base package reaches
// through this host (contract §1.4); release.json pins it at the host's own version.
[BepInDependency(ForgeCoreGuid, LibraryMinimumVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeEngine";
    /// <summary>GUID declared by the shipped GTFO-API assembly; the host subscribes to its level events.</summary>
    public const string GtfoApiGuid = "dev.gtfomodding.gtfo-api";
    /// <summary>The lowest GTFO-API release this host was built and validated against: the `provides` version
    /// release.json pins for the base dependency that ships the assembly. It is the floor because
    /// <c>EventAPI.OnCheckpointReached</c> and <c>EventAPI.OnCheckpointReloaded</c> are the host's checkpoint
    /// boundaries and exist only from that release or later.</summary>
    public const string GtfoApiMinimumVersion = ">=0.6.1";
    /// <summary>GUID declared by MTFO's plugin entry.</summary>
    public const string MtfoGuid = "com.dak.MTFO";
    /// <summary>The MTFO release release.json pins as a base dependency of the host.</summary>
    public const string MtfoMinimumVersion = ">=4.6.3";
    public const string ForgeCoreGuid = "NAinfini.ForgeCore";
    /// <summary>The library release release.json pins for the host.</summary>
    public const string LibraryMinimumVersion = ">=1.0.4"; // package-version: NAinfini-ForgeCore
    public const string PluginName = "Forge Engine";
    public const string PluginVersion = "1.0.0"; // package-version: NAinfini-ForgeEngine
    // The latch is per process, not per instance, so it is static like the readiness flag it guards.
    private static bool _loadAttempted;
    private static bool _loadComplete;
    /// <summary>The process's shared Forge kernel. A BepInEx plugin that depends on Forge Engine reads this in
    /// its own <c>Load</c>, registers its own provider before the first fixed tick closes registration, and keeps
    /// using this instance for lifecycle, query and event APIs. Null means the host is Off or did not finish loading.</summary>
    public static IForgeEngine? Runtime => _loadComplete ? GameRuntimeBridge.Kernel : null;
    /// <summary>Frozen startup configuration, not readiness or a permission grant.</summary>
    public static RuntimeMode ConfiguredMode { get; private set; } = RuntimeMode.Off;
    /// <summary>True when the host latched the runtime as suspended, so packages must stop registering and report it.</summary>
    public static bool IsSuspended => GameRuntimeBridge.Suspension != null;
    /// <summary>The reason code for <see cref="IsSuspended"/>, null while the runtime is not suspended.</summary>
    public static string? SuspensionCode => GameRuntimeBridge.Suspension;
    /// <summary>Simulation-thread phase/authority gate; each action must still validate its own receiver and permissions.</summary>
    public static bool CanExecuteGameplay => _loadComplete && GameRuntimeBridge.CanExecute;
    /// <summary>Native action gate for host execution or the kernel's validated, scoped owner callback.
    /// Event publishers must keep using <see cref="CanExecuteGameplay"/>; receivers still validate their own lives.</summary>
    public static bool CanExecuteOwnerGameplay => _loadComplete && GameRuntimeBridge.CanExecuteOwner;
    /// <summary>Explicit author command; call on the simulation thread. Play never re-reads plan files.
    /// Returns readiness refusal or reload counts; each file's admission result uses the existing plan logs.</summary>
    public static CommandResult ReloadAuthoringPlans() => GameRuntimeBridge.ReloadAuthoringPlans(ConfiguredMode);
    /// <summary>Explicit author command (contract I-RELOAD); call on the simulation thread. Reads the installed content again and
    /// commits it as the next content generation, in Authoring only and only in a clean lobby (this machine hosts, nobody else is
    /// in the lobby, no level is built); Play never changes its content. A refusal names its code and keeps the current content.</summary>
    public static CommandResult ReloadContent() => GameRuntimeBridge.ReloadContent(ConfiguredMode);
    internal static ManualLogSource PluginLog { get; private set; } = null!;

    public override void Load()
    {
        if (_loadAttempted) throw new InvalidOperationException("Forge Engine Load is single-attempt; restart the process after failure.");
        using var timing = new ForgeCore.StartupTiming(PluginGuid, message => { if (ForgeCore.Log.Developer) Log.LogInfo(message); });
        _loadAttempted = true;
        _loadComplete = false;
        ConfiguredMode = RuntimeMode.Off;
        PluginLog = Log;
        timing.Mark("settings");
        RuntimeSettings.Bind(Config);
        ConfiguredMode = RuntimeSettings.Mode;
        var logLevel = RuntimeSettings.LogLevel;
        if (ConfiguredMode == RuntimeMode.Off)
        {
            timing.Complete("off");
            return;
        }
        Harmony? harmony = null;
        bool hostAttempted = false;
        try
        {
            NativeFault.SetWarningSink((siteId, error) => Log.LogWarning($"{siteId}: {error}"));
            harmony = new Harmony(PluginGuid);
            hostAttempted = true;
            timing.Mark("kernel-and-game-image");
            // The first fixed tick compares these installed versions with the release this host embeds.
            GameRuntimeBridge.InstalledPluginVersion = guid => IL2CPPChainloader.Instance.Plugins.TryGetValue(guid, out var installed) ? installed.Metadata.Version.ToString() : null;
            GameRuntimeBridge.Initialize(logLevel);
            // Authoring is the one mode that promises complete behaviour-editor observability. Elevate while
            // registration is still open so built-in, already-registered and later domain providers all emit
            // Trace lifecycle records for the whole session; Play keeps the configured player-tier level.
            if (ConfiguredMode == RuntimeMode.Authoring && GameRuntimeBridge.Kernel is { } authoringKernel
                && GameRuntimeBridge.Suspension == null)
                authoringKernel.ElevateLogging();
            // The one discovery of the installed packages (I-RESOLVE), after the level is final so its record is written.
            GameRuntimeBridge.DiscoverContent();
            // The trigger provider is part of this package: it registers on the kernel's own registration while the
            // window is open, before any domain package loads, and a suspended host registers nothing.
            if (GameRuntimeBridge.Kernel is { } triggerKernel && GameRuntimeBridge.Suspension == null)
            {
                timing.Mark("trigger");
                TriggerHost.Start(triggerKernel);
                // The shared presentation rows are part of this package too and register on the same window.
                timing.Mark("presentation");
                PresentationHost.Start(triggerKernel);
            }
            timing.Mark("patching");
            HostMigrationPatches.Install(harmony);
            StartPlayingCatchUp.Install(harmony);
            timing.Mark("lifecycle");
            LevelLifecycle.Subscribe();
            // Author diagnostics of Forge maps: the generated layout record goes into this host's own runtime log.
            if (ConfiguredMode == RuntimeMode.Authoring) Authoring.GeneratedLayoutRecord.Start();
            // Choosing an expedition is when that level's plans start loading, off the game thread.
            ForgeCore.GameEvents.ExpeditionSelected += ExpeditionSelected;
            timing.Mark("frame");
            HostFrame.Start();
            // A suspended host starts no domain; SuspendStartup already wrote its Error.
            _loadComplete = true;
            // The four domains start last, in this fixed order, once the host itself is fully up: nothing after them can
            // fail and leave a started domain behind a rolled-back host. Each start rolls its own session back and
            // rethrows; a domain that fails stays down with its error on record and the others still start.
            if (GameRuntimeBridge.Kernel is { } domainKernel && GameRuntimeBridge.Suspension == null)
            {
                timing.Mark("domains");
                StartDomain("player", () => ForgePlayer.Native.PlayerHost.Start(domainKernel, logLevel, Log));
                StartDomain("map", () => ForgeMap.Native.MapHost.Start(domainKernel, logLevel, Log));
                StartDomain("enemy", () => ForgeEnemy.Native.EnemyHost.Start(domainKernel, logLevel, Log));
                StartDomain("weapon", () => ForgeWeapon.Native.WeaponHost.Start(domainKernel, logLevel, Log));
            }
            timing.Complete(IsSuspended ? "suspended" : "loaded");
        }
        catch (Exception original)
        {
            _loadComplete = false;
            NativeFault.SetWarningSink(null);
            var failures = new List<Exception>();
            // The provider's registration belongs to the kernel, so it is released before the kernel stops.
            Rollback("presentation", PresentationHost.Stop, failures);
            Rollback("trigger", TriggerHost.Stop, failures);
            if (hostAttempted) Rollback("runtime", GameRuntimeBridge.Stop, failures);
            // The network binding's SNet subscription and session host are process-lifetime too, so they are cleaned
            // unconditionally: a failure before the host start leaves nothing to release, and releasing twice is a
            // no-op rather than a second teardown path.
            Rollback("network", NetworkBinding.Stop, failures);
            // Subscriptions are process-lifetime, so cleanup is unconditional and idempotent rather than stage-flagged.
            Rollback("level_events", LevelLifecycle.Unsubscribe, failures);
            Rollback("layout_record", Authoring.GeneratedLayoutRecord.Stop, failures);
            Rollback("expedition_selected", () => ForgeCore.GameEvents.ExpeditionSelected -= ExpeditionSelected, failures);
            Rollback("migration_subscription", HostMigrationPatches.Uninstall, failures);
            Rollback("start_playing_subscription", StartPlayingCatchUp.Uninstall, failures);
            if (harmony != null) Rollback("hooks", harmony.UnpatchSelf, failures);
            Rollback("frame", HostFrame.Stop, failures);
            if (failures.Count != 0)
            {
                try { original.Data["ForgeEngine.StartupCleanupFailures"] = new AggregateException("Forge startup cleanup failures", failures); }
                catch (Exception) { /* An unwritable exception Data dictionary must not replace the startup exception. */ }
            }
            throw;
        }
    }

    private void StartDomain(string domain, Action start)
    {
        try { start(); }
        catch (Exception error) { Log.LogError($"Forge {domain} domain failed to start: {error}"); }
    }

    // Rollback runs while the original startup exception is being handled, so it must never throw: an escaping
    // exception would replace that original. Every stage failure, the failing stage's own BepInEx report included,
    // is retained on the original exception's Data instead.
    private void Rollback(string stage, Action cleanup, List<Exception> failures)
    {
        try { cleanup(); }
        catch (Exception error)
        {
            failures.Add(new InvalidOperationException("Forge startup cleanup: " + stage, error));
            try { Log.LogError($"Forge startup cleanup {stage} failed: {error}"); }
            catch (Exception reporter) { failures.Add(new InvalidOperationException("Forge startup cleanup reporter: " + stage, reporter)); }
        }
    }

    /// <summary>The chosen expedition as a `level` mount spells it (`rundown:tier:index`): the Local rundown's block id,
    /// the tier (A=1) and the index the game stored. Another rundown key, or a tier outside A–E, names no Forge level.</summary>
    private static void ExpeditionSelected(uint? rundown, int tier, int index)
        => GameRuntimeBridge.Guard(() => GameRuntimeBridge.SelectLevel(rundown is { } block && tier is >= 1 and <= 5 && index >= 0
            ? block.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (char)('A' + tier - 1)
                + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null));

    // This process-lifetime IL2CPP component does not support hot reloading.
    public override bool Unload() => false;
}
