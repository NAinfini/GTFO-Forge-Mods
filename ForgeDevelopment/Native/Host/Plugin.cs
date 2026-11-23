using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace ForgeDevelopment.Native;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.4")] // package-version: NAinfini-ForgeCore
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "NAinfini.ForgeDevelopment";
    public const string PluginName = "Forge Development";
    public const string PluginVersion = "1.0.0"; // package-version: NAinfini-ForgeDevelopment
    private bool _loadAttempted;
    private bool _sharedCleanupArmed;
    private bool _hookRequestsEnabled;
    private AuthoringMonitor? _monitor;
    private ExperimentRunner? _experimentRunner;
    private ExperimentPanel? _experimentPanel;
    private IDisposable? _collectionHooks;
    private IDisposable? _gameHooks;
    private bool _collectionAttempted;
    internal static ManualLogSource PluginLog { get; private set; } = null!;
    /// <summary>This plugin's settings file, held by ForgeCore's one configuration path (<see cref="ForgeCore.Settings"/>).</summary>
    internal static BepInEx.Configuration.ConfigFile Configuration { get; private set; } = null!;
    internal Exception? StartupFailure { get; private set; }

    public override void Load()
    {
        using var timing = new ForgeCore.StartupTiming(PluginGuid, message => Log.LogInfo(message));
        LoadCore(timing);
    }

    private void LoadCore(ForgeCore.StartupTiming timing)
    {
        if (_loadAttempted)
        {
            ReportFailure("repeat-load", new InvalidOperationException("Forge Development Load is single-attempt; restart the process after failure."));
            return;
        }
        _loadAttempted = true;
        PerformanceMonitor? performanceMonitor = null;
        bool recorderAttempted = false;
        string stage = "host";
        try
        {
            PluginLog = Log;
            // The package's own switch is the single opt-in: only developers install it, so it is on by default. Off binds no other
            // diagnostic setting and starts no hooks or collectors.
            ForgeCore.Settings.Adopt(PluginGuid, Config);
            Configuration = ForgeCore.Settings.For(PluginGuid);
            if (!ForgeCore.Settings.Bind(PluginGuid, "General", "Enabled", true,
                    "Run the developer tools (console, recorder, capture, diagnostics). Restart required.").Value)
            {
                Log.LogInfo("Forge Development inactive: [General] Enabled = false; no diagnostic hooks or collectors.");
                timing.Complete("inactive");
                return;
            }
            stage = "settings"; timing.Mark(stage);
            GTFO.API.LevelAPI.OnBuildStart += RecTime.AdvanceWorld;
            GTFO.API.LevelAPI.OnLevelCleanup += RecTime.AdvanceWorld;
            ForgeCore.DiagnosticCpuBudget.Shared.SetFrameSource(static () => UnityEngine.Time.frameCount);
            ForgeCore.DiagnosticCpuBudget.Shared.BeginFrame(UnityEngine.Time.frameCount);
            // BepInEx's registration window is synchronous. Account even an indivisible injection overrun.
            using var cpu = ForgeCore.DiagnosticCpuBudget.Shared.MeasureRequired();
            StartupSettings.Bind();
            NetSim.StartConfigured();
            stage = "drop-timing"; timing.Mark(stage);
            DropTimingHost.Start();
            if (!DropMeasurement.CaptureWorkEnabled)
            {
                Log.LogInfo("development.capture-work enabled=false; drop measurement only; no diagnostic hooks or collectors installed");
                timing.Complete("measurement-only");
                return;
            }
            _sharedCleanupArmed = true;
            stage = "hook-providers"; timing.Mark(stage);
            _hookRequestsEnabled = true;

            NativeFunctionTimings.Configure();
            NativeGameTimings.ConfigureMarkers((target, callback) =>
            {
                RequireHookRequests();
                return DevelopmentHookInstall.Observe("ForgeDevelopment.Performance.Marker", target, null, callback, null);
            });
            RecRuntime.ConfigureCollection(PrepareCollection, StartCapture, StopCollection);
            stage = "recorder"; timing.Mark(stage);
            recorderAttempted = true;
            RecRuntime.Start(BepInEx.Paths.BepInExRootPath, requested: LevelGuideCaptureMaterials.Requested);
            stage = "performance-component"; timing.Mark(stage);
            PerformanceLogWriter.Traffic = static () =>
            {
                var sent = ForgeCore.Net.Traffic;
                return (sent.Messages, sent.Bytes, sent.LossyMessages, sent.LossyBytes);
            };
            if (Settings.PerformanceLogging.Value) performanceMonitor = AddComponent<PerformanceMonitor>();
            stage = "loaded-log"; timing.Mark(stage);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Diagnostics only observe; native hooks are not game-verified.");
            timing.Complete();
        }
        catch (Exception original)
        {
            _hookRequestsEnabled = false;
            StartupFailure = original;
            var failures = new List<Exception>();
            // Teardown is the reverse of startup: the components that write into the session, then the recorder,
            // whose trace patches sit on game methods and whose session owns a writer thread, and only then the hooks
            // that report the failure.
            if (recorderAttempted) Rollback("recorder", RecRuntime.Stop, failures);
            Rollback("collection", StopCollection, failures);
            if (_sharedCleanupArmed) Rollback("hooks", () => DevelopmentHookSafety.StopAll(), failures);
            if (performanceMonitor != null) Rollback("performance_component", () => UnityEngine.Object.Destroy(performanceMonitor), failures);
            Rollback("drop_timing", DropTimingHost.Stop, failures);
            if (failures.Count != 0)
            {
                try { original.Data["ForgeDevelopment.StartupCleanupFailures"] = new AggregateException("Forge Development startup cleanup failures", failures); }
                catch (Exception) { /* An unwritable exception Data dictionary must not replace the startup exception. */ }
            }
            ReportFailure(stage, original);
        }
    }

    private void PrepareCollection()
    {
        ForgeCore.DiagnosticCpuBudget.Shared.BeginFrame(RecTime.Frame);
        using var cpu = ForgeCore.DiagnosticCpuBudget.Shared.MeasureRequired();
        RequireHookRequests();
        _collectionAttempted = true;
        try
        {
            DevelopmentTypedHooks.Register(DevelopmentNativeHooks.Shared);
            var hooks = DevelopmentHookSafety.Validate(typeof(Plugin).Assembly);
            RuntimeDiagnostics.Initialize();
            CaptureTriggers.Subscribe();
            ExitScanSettle.LifetimeSink = fields => RuntimeDiagnostics.Report?.Event("exit-scan", "lifetime", fields["instance"], new Dictionary<string, string>(fields));
            _collectionHooks = DevelopmentHookSafety.Install(hooks);
            _gameHooks = DevelopmentGameHooks.Enter();
            Log.LogInfo("startup.hooks-ready plugin=" + PluginGuid);
            _monitor = AddComponent<AuthoringMonitor>();
            _experimentRunner = AddComponent<ExperimentRunner>();
            _experimentPanel = AddComponent<ExperimentPanel>();
            ExperimentCatalog.DeferLoad(ExperimentPanel.Load);
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            Rollback("collection", StopCollection, failures);
            if (failures.Count != 0)
                try { original.Data["ForgeDevelopment.CollectionCleanupFailures"] = new AggregateException(failures); }
                catch (Exception) { /* Preserve the original startup error. */ }
            throw;
        }
    }

    private void StartCapture()
    {
        using var cpu = ForgeCore.DiagnosticCpuBudget.Shared.MeasureRequired();
        if (!Settings.WorldCaptureEnabled.Value)
        {
            Log.LogInfo("development.world-capture enabled=false; world snapshots and change records are not collected this session");
            return;
        }
        CaptureRegistry.EnsureStarted(() => AddComponent<CaptureMonitor>(), CaptureRegistry.DefaultPeriodicSeconds);
    }

    private void StopCollection()
    {
        if (!_collectionAttempted) return;
        using var cpu = ForgeCore.DiagnosticCpuBudget.Shared.MeasureRequired();
        var failures = new List<Exception>();
        Rollback("capture", CaptureRegistry.Stop, failures);
        Rollback("capture_triggers", CaptureTriggers.Unsubscribe, failures);
        if (_experimentPanel != null) Rollback("experiment_panel", () => { _experimentPanel.enabled = false; UnityEngine.Object.Destroy(_experimentPanel); _experimentPanel = null; }, failures);
        if (_experimentRunner != null) Rollback("experiment_runner", () => { _experimentRunner.enabled = false; UnityEngine.Object.Destroy(_experimentRunner); _experimentRunner = null; }, failures);
        // Destroy schedules OnDestroy; revoke callbacks now, before the session closes.
        Rollback("diagnostics", RuntimeDiagnostics.Pause, failures);
        if (_monitor != null) Rollback("authoring_component", () => { _monitor.enabled = false; UnityEngine.Object.Destroy(_monitor); _monitor = null; }, failures);
        if (_gameHooks != null) Rollback("game_hooks", () => { _gameHooks.Dispose(); _gameHooks = null; }, failures);
        if (_collectionHooks != null) Rollback("collection_hooks", () => { _collectionHooks.Dispose(); _collectionHooks = null; }, failures);
        if (failures.Count != 0) throw new AggregateException("Development collection cleanup incomplete.", failures);
        _collectionAttempted = false;
    }

    private void RequireHookRequests()
    {
        if (!_hookRequestsEnabled) throw new InvalidOperationException("Development hook providers are stopped; restart required.");
    }


    private void ReportFailure(string stage, Exception error)
    {
        StartupFailure ??= error;
        try { Log.LogError($"development.startup-failed stage={stage}; startup stopped, restart required: {error}"); }
        catch (Exception reporter)
        {
            Console.Error.WriteLine($"development.startup-failed stage={stage}: {error}; BepInEx logger failed: {reporter}");
        }
    }

    private void Rollback(string stage, Action cleanup, List<Exception> failures)
    {
        try { cleanup(); }
        catch (Exception error)
        {
            failures.Add(new InvalidOperationException("Forge Development startup cleanup: " + stage, error));
            try { Log.LogError($"Forge Development startup cleanup {stage} failed: {error}"); }
            catch (Exception reporter) { failures.Add(new InvalidOperationException("Forge Development startup cleanup reporter: " + stage, reporter)); }
        }
    }

    // Native log callbacks and IL2CPP components are process-lifetime; no hot unload contract exists.
    public override bool Unload()
    {
        _hookRequestsEnabled = false;
        if (!_sharedCleanupArmed) return false;
        using var cpu = ForgeCore.DiagnosticCpuBudget.Shared.MeasureRequired();
        var failures = new List<Exception>();
        Rollback("capture", CaptureRegistry.Stop, failures);
        Rollback("recorder", RecRuntime.Stop, failures);
        CombatSampling.Stop();
        Rollback("hooks", () => DevelopmentHookSafety.StopAll(), failures);
        Rollback("diagnostics", RuntimeDiagnostics.Stop, failures);
        if (failures.Count != 0) ReportFailure("unload-cleanup", new AggregateException("Development cleanup incomplete.", failures));
        return false;
    }
}
