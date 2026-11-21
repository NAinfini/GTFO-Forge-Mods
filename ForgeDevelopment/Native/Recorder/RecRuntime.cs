using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ForgeDevelopment.Native;

/// <summary>
/// The recorder's startup and lifetime. The order is fixed and each stage is torn down in reverse when a later one
/// fails: the session must exist before anything can record, followed by log capture and process inventory. The tracer
/// starts on the existing authoring Update only after GameData initialization succeeds.
///
/// Nothing here is a second diagnostics pipeline: <see cref="RuntimeDiagnostics"/> keeps owning the report, and this
/// adds the session's own channels beside it.
/// </summary>
internal static class RecRuntime
{
    internal static bool TracePending => TraceStartup.Pending || RecTracerRuntime.InstallationPending;
    private const int TracePatchesPerFrame = 16;
    private static int _traceInstallSequence, _traceLoggedTenth;
    private static RecTraceInstallProgress _traceInstallProgress;
    private static ForgeCore.StartupTiming? _traceTiming;
    private static bool _traceScheduled;
    private const string TraceWork = "work:forge.development.trace-startup";
    private static readonly Func<bool> TraceCanContinue = () => ForgeCore.DiagnosticCpuBudget.Shared.HasCooperativeTime;
    // Each trace patch is one indivisible Harmony install (15-35 ms measured, far over the 2 ms frame allowance), so
    // installing while the factory builds a level stretched a two-minute queue across the whole load: the native shard
    // bundle wait went from 12 s at 9 ms frames to 122 s at 39 ms frames. The queue holds while the game is Generating
    // and resumes on the next state; the held frames are reported on the progress lines.
    private static bool _traceHeldForGeneration;
    private static int _traceGenerationHeldFrames;
    private static void TraceGameState(eGameStateName previous, eGameStateName next)
        => _traceHeldForGeneration = next == eGameStateName.Generating;
    private static readonly object Gate = new();
    private static readonly List<string> Started = new();
    private static Action? _prepareCollection, _startCapture, _stopCollection;
    internal static void ConfigureCollection(Action prepare, Action capture, Action stop)
    { _prepareCollection = prepare; _startCapture = capture; _stopCollection = stop; }
    private const int LevelReadMilliseconds = 250;
    private const int RenewRetryMilliseconds = 5000;
    private static long _nextLevelRead, _nextRenew;
    private static string _lastLevel = "";
    private static long _lastEpoch = long.MinValue;
    private static bool _ticking, _starting, _stopRequested;
    private static long _nextMultiplayerCheck;
    private static bool _multiplayerNoted;
    private static volatile bool _stopping, _processExit;
    private static bool _stopInProgress;
    private static int _operationThread, _stopThread, _stopWaiters;

    private static readonly RecTraceStartup TraceStartup = new();
    private static readonly PopulationHookSwitch PopulationSwitch = new(
        () => RecPopulationBudget.HasHooks, StartPopulation, StopPopulation, PopulationFailure);

    private static System.Threading.Tasks.Task<RecDrainResult>? _persistenceDrain;
    internal static System.Threading.Tasks.Task<RecDrainResult>? PersistenceDrain => _persistenceDrain;
    internal static bool PersistencePending => _persistenceDrain is { IsCompleted: false };
    private static System.Threading.Tasks.Task<RecDrainResult?>? _renewal;
    /// <summary>A budget renewal is posted and not yet collected: no session admits records until it opens the next one.</summary>
    internal static bool Renewing => _renewal != null;

    internal static bool Active
    {
        get
        {
            bool started;
            using (RecGate.Enter(Gate, "recorder.lock-timeout")) started = !_stopping && Started.Count != 0;
            return started && RecSession.Active;
        }
    }
    internal static IReadOnlyList<string> Stages { get { using (RecGate.Enter(Gate, "recorder.lock-timeout")) return Started.ToArray(); } }
    internal static string TraceReport => RecTracerRuntime.Report;

    /// <summary>Starts the recorder for this process. It is called from the authoring branch of Plugin.Load after
    /// RuntimeDiagnostics is listening, so a stage that fails is reported through the diagnostics that already work.
    /// A stage that fails records itself in <see cref="Failure"/> and the ones before it keep running: the recorder
    /// degrades to the channels it could open instead of taking the authoring session down with it.</summary>
    internal static void Start(string bepInExRootPath, bool requested = false)
    {
        RecGate.UseGameThread();
        // A multiplayer session is not an authoring session: recording and tracing it would charge every player's
        // game for evidence about somebody else's run.
        if (MultiplayerSession()) return;
        // After a process-exit stop the trace hooks are still installed and still owned by that session.
        if (_processExit || (!requested && !Settings.RecorderEnabled.Value) || RecSession.WriterAlive) return;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (Started.Count != 0 || _stopInProgress || _starting || _ticking) return;
            _starting = true;
            _operationThread = Environment.CurrentManagedThreadId;
            _stopping = false;
            _persistenceDrain = null; _renewal = null;
            Failure = null;
            _nextLevelRead = 0;
            _lastLevel = "";
            _lastEpoch = long.MinValue;
        }
        try
        {
            if (_prepareCollection != null) { Stage("collection"); _prepareCollection(); }
            RecUnity.Install();
            Root = bepInExRootPath;
            if (!RecSession.Start(Root, Plugin.PluginVersion, EngineLookup.Version ?? "absent"))
            {
                Failure = "session: could not be opened under " + Root;
                _stopRequested = true;
                return;
            }
            Stage("session");
            Try("log", RecLog.Start, RecLog.Stop);
            // Native tracing belongs to a recorder the author opted into. A session that a pack test or a capture asked
            // for never traces: installing thousands of hooks one per frame took the shared per-frame diagnostic
            // allowance away from the test pump for minutes.
            TraceStartup.Schedule(Settings.RecorderTrace.Value && Settings.RecorderEnabled.Value);
            if (TraceStartup.Pending)
            {
                Plugin.PluginLog.LogInfo("development.trace-pending reason=waiting-for-game-data");
                RecSession.Note("trace_pending", json => json.WriteString("reason", "waiting-for-game-data"));
            }
            Try("process_inventory", RecProcessInventory.Start, RecProcessInventory.Stop);
            WriteStartup();
            RecSession.WritePatches(RecTracerRuntime.Report);
            _startCapture?.Invoke();
        }
        catch { _stopRequested = true; throw; }
        finally { FinishOperation(starting: true); }
    }

    /// <summary>Whether another human is in the session, which makes this a multiplayer run rather than an authoring
    /// one. The check is the lobby gate's existing membership rule, not a second roster implementation, and the one
    /// info line is written when the state is first seen.</summary>
    private static bool MultiplayerSession()
    {
        bool other;
        try { other = LobbyGate.HasOtherHuman(); }
        catch (Exception) { return false; } // Before SNet has a session there is no roster to read.
        if (!other) return false;
        if (_multiplayerNoted) return true;
        _multiplayerNoted = true;
        Plugin.PluginLog.LogInfo("development.recorder inactive: more than one player is in the session; recording and runtime trace stay off.");
        return true;
    }

    /// <summary>Renews a session that reached its byte budget (<see cref="RecSession.RenewAtBudget"/>) before a command
    /// starts on it. The producers, hooks and trace stay installed; when this pipeline owns the session, the new directory
    /// gets the per-session documents the old one held: the startup record and the trace patches and coverage.</summary>
    internal static void RenewAtBudget()
    {
        // The game thread only posts the renewal and collects it on a later call; the close, drain and reopen never block it.
        _renewal ??= RecSession.RenewAtBudgetInBackground(TimeSpan.FromSeconds(10));
        if (!_renewal.IsCompleted) return;
        var renewal = _renewal;
        _renewal = null;
        if (renewal.GetAwaiter().GetResult() == null) return;
        bool resident;
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) resident = !_stopping && Started.Contains("session");
        if (!resident) return;
        WriteStartup();
        RecSession.WritePatches(RecTracerRuntime.Report);
        RecTraceCoverage.Rewrite();
    }

    /// <summary>Why a stage is missing from the session, or null. A recorder that cannot start one channel says so
    /// once, in the session itself and in the plugin log, rather than throwing the whole authoring session away.</summary>
    internal static string? Failure { get; private set; }

    private static void Try(string stage, Action start, Action stop)
    {
        try
        {
            start();
            Stage(stage);
        }
        catch (Exception error)
        {
            Failure = stage + ": " + error.GetType().Name + ": " + error.Message;
            try { Plugin.PluginLog.LogError("Forge recorder stage " + stage + " failed: " + error); }
            catch (Exception logError)
            {
                RecDiag.Fallback("Forge recorder stage " + stage + " failed: " + error + "; logging failed: " + logError); // The stage failure is already stored in Failure.
            }
            try { stop(); }
            catch (Exception cleanup)
            {
                Failure += " (cleanup failed: " + cleanup + ")";
                Stage(stage); // The failed cleanup still owns resources and must be retried by Stop.
                StopFailure(stage, cleanup);
            }
            RecSession.Note("stage_failed", json =>
            {
                json.WriteString("stage", stage);
                json.WriteString("error", error.GetType().Name + ": " + error.Message);
            });
        }
    }

    /// <summary>Closes producer admission, detaches every producer, then lets the one writer drain and close.
    /// Failed detach stages retain their resources for a later Stop; they cannot restart over pending cleanup.</summary>
    internal static void Stop() => Stop(processExit: false);

    /// <summary>Stop for a process that is ending (Unity's OnApplicationQuit). Everything the recorder writes is still
    /// drained, flushed and indexed; the one thing skipped is physically removing the trace hooks, which only delays
    /// the exit (~4 ms each). A mid-run or level-change stop keeps using <see cref="Stop()"/> and removes them.</summary>
    internal static void StopForProcessExit()
    {
        _processExit = true;
        Stop(processExit: true);
    }

    private static void Stop(bool processExit)
    {
        string[] stages;
        var caller = Environment.CurrentManagedThreadId;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            _stopping = true;
            TraceStartup.Cancel();
            if (_starting || _ticking || _stopInProgress)
            {
                // A callback may own the session lock or be the writer an external Stop is joining.
                // Such callers must unwind; an unrelated shutdown caller waits without holding Gate.
                if (caller == RecGate.GameThreadId || caller == _operationThread || caller == _stopThread || RecSession.IsComposing || RecSession.IsWriterThread)
                {
                    if (_starting || _ticking) _stopRequested = true;
                    return;
                }
                var deadline = Environment.TickCount64 + 10000;
                _stopWaiters++;
                try
                {
                    while (_starting || _ticking || _stopInProgress)
                    {
                        var remaining = deadline - Environment.TickCount64;
                        if (remaining <= 0)
                        {
                            _stopRequested = _starting || _ticking;
                            Failure = "Recorder shutdown is still waiting for a producer or cleanup owner after 10 s.";
                            throw new TimeoutException(Failure);
                        }
                        Monitor.Wait(Gate, (int)remaining);
                    }
                }
                finally { _stopWaiters--; }
            }
            _stopRequested = false;
            _stopInProgress = true;
            _stopThread = caller;
            stages = Started.ToArray();
        }
        try
        {
            for (var i = stages.Length - 1; i >= 0; i--)
            {
                var stage = stages[i];
                if (stage == "session") continue;
                try
                {
                    StopStage(stage, processExit);
                    using (RecGate.Enter(Gate, "recorder.lock-timeout")) Started.Remove(stage);
                }
                catch (Exception error) { StopFailure(stage, error); }
            }
            string[] pending;
            using (RecGate.Enter(Gate, "recorder.lock-timeout")) pending = Started.Where(stage => stage != "session").ToArray();
            RecSession.Note("producers_stopped", json =>
            {
                json.WriteStartArray("cleanupPending");
                foreach (var stage in pending) json.WriteStringValue(stage);
                json.WriteEndArray();
            });
            // Stop closes admission under the same queue lock as producers. Accepted in-flight work and
            // completion callbacks precede the writer's final flush, close and terminal index.
            // Refresh through the same bounded document queue, after producer cleanup has frozen the counters.
            RecSession.WritePatches(RecTracerRuntime.Report);
            if (caller == RecGate.GameThreadId && !processExit)
            {
                _persistenceDrain ??= System.Threading.Tasks.Task.Run(DrainSession);
                // The pump never joins the disk owner. PackTest retains this task and polls across frames.
                if (!_persistenceDrain.IsCompleted) return;
                ReportDrain(_persistenceDrain.GetAwaiter().GetResult());
            }
            else ReportDrain(DrainSession());
        }
        finally
        {
            using (RecGate.Enter(Gate, "recorder.lock-timeout"))
            {
                _stopInProgress = false;
                _stopThread = 0;
                Monitor.PulseAll(Gate);
            }
        }
    }

    private static RecDrainResult DrainSession()
    {
        // A renewal still opening the next session finishes first, so the session it opens is the one closed here. Its own
        // failure needs no handling here: the recorder is stopping either way, and Stop answers for what is open.
        _renewal?.ContinueWith(static _ => { }).Wait(TimeSpan.FromSeconds(10));
        var result = RecSession.Stop(TimeSpan.FromSeconds(10));
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (!RecSession.WriterAlive) Started.Remove("session");
            if (result != RecDrainResult.Completed)
            {
                Failure = "session: recorder.persistence-" + result.ToString().ToLowerInvariant();
            }
        }
        return result;
    }

    private static void ReportDrain(RecDrainResult result)
    {
        if (result == RecDrainResult.Completed) return;
        // Incomplete means the writer finished cleanly but the byte budget or the queue shed records: the recording is
        // truncated and the run still counts as not clean, but nothing failed to run. Every other result is a fault.
        StopFailure("session", new IOException("Recorder persistence outcome: " + result
            + " (dropped " + RecSession.Dropped + ", byte budget reached " + RecSession.BudgetReached + ")"),
            fault: result != RecDrainResult.Incomplete);
    }

    private static void StopFailure(string stage, Exception error, bool fault = true)
    {
        Failure = stage + ": " + error;
        RecSession.Note("stop_stage_failed", json =>
        {
            json.WriteString("stage", stage);
            json.WriteString("error", error.ToString());
        });
        try
        {
            var message = "Forge recorder stop stage " + stage + (fault ? " failed: " : " incomplete: ") + error;
            if (fault) Plugin.PluginLog.LogError(message); else Plugin.PluginLog.LogWarning(message);
        }
        catch (Exception logError)
        {
            RecDiag.Fallback("Forge recorder stop stage " + stage + " failed: " + error + "; logging failed: " + logError); // The stop-stage error is already recorded above.
        }
    }

    /// <summary>The per-frame part: the recorder's hotkeys, the trace install queue, and the boundaries a snapshot is
    /// asked for at. It rides the authoring monitor's Update, which is the only per-frame callback this package owns.</summary>
    internal static void Tick()
    {
        TickRecorder();
        TraceStep();
    }

    private static void TickRecorder()
    {
#if FORGE_DEVELOPMENT_TIMING
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentRecorderPump);
#endif
        // Joining another player turns this process into a multiplayer session; a recorder that was already open is
        // closed rather than left tracing the session. The session check is one second, not one frame: it reads the
        // game's lobby and slot lists.
        var clock = Environment.TickCount64;
        if (clock >= _nextMultiplayerCheck)
        {
            _nextMultiplayerCheck = clock + 1000;
            if (MultiplayerSession())
            {
                bool active;
                using (RecGate.Enter(Gate, "recorder.lock-timeout")) active = Started.Count != 0 || TracePending;
                if (active) Stop();
                return;
            }
        }
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (_stopping || _starting || _ticking) return;
            _ticking = true;
            _operationThread = Environment.CurrentManagedThreadId;
        }
        try
        {
            if (RecTraceBudget.Active) RecTraceBudget.BeginFrame(RecTime.Frame);
            if (!RecSession.Active)
            {
                // Between a full session and the next one the recorder is not stopping: only the renewal is collected.
                if (_renewal != null) { RenewNearBudget(Environment.TickCount64); return; }
                PopulationSwitch.Tick(false, false, Environment.TickCount64);
                _stopRequested = true;
                return;
            }
            if (TraceStartup.Pending && TraceStartup.Take(GameData.GameDataInit.IsLoaded))
            {
                _traceInstallSequence = _traceLoggedTenth = 0;
                _traceInstallProgress = default;
                Plugin.PluginLog.LogInfo("development.trace-starting sessionId=" + RecSession.SessionId
                    + " realSeconds=" + (RecTime.Milliseconds / 1000d).ToString("F3", CultureInfo.InvariantCulture)
                    + " reason=game-data-loaded");
                RecSession.Note("trace_starting", json => json.WriteString("reason", "game-data-loaded"));
                Try("tracer", LoadTrace, () => StopTrace("trace-startup-failed"));
            }
            if (RecTracerRuntime.InstallationPending) _traceScheduled = true;
            PopulationSwitch.Tick(Settings.RecorderPopulationBudget.Value,
                GameData.GameDataInit.IsLoaded, Environment.TickCount64);
            RecHotkeys.Poll();
            var now = Environment.TickCount64;
            if (_renewal != null || RecSession.BudgetNear && now >= _nextRenew) RenewNearBudget(now);
            // The world epoch is read every frame. The level key is a native string that becomes a new managed string on
            // every read, so it is read when the epoch moved or LevelReadMilliseconds have passed, not every frame.
            var epoch = RecTime.WorldEpoch;
            if (epoch != _lastEpoch || now >= _nextLevelRead)
            {
                _nextLevelRead = now + LevelReadMilliseconds;
                var level = RecTime.Level;
                if (level != _lastLevel || epoch != _lastEpoch) NoteBoundary(level, epoch);
            }
        }
        finally { FinishOperation(starting: false); }
    }

    /// <summary>A recording that fills its byte budget continues in a new session instead of dropping everything after it.
    /// A failed renewal is reported; while a session is still open it is retried after <see cref="RenewRetryMilliseconds"/>,
    /// not on every frame. A renewal that fails after the previous session stopped has no session to continue, so the
    /// recorder ends there and the failure stays in the log.</summary>
    private static void RenewNearBudget(long now)
    {
        _nextRenew = now + RenewRetryMilliseconds;
        try { RenewAtBudget(); }
        catch (Exception error)
        {
            Failure = "session: " + error.Message;
            Plugin.PluginLog.LogError("Forge recorder could not continue in a new session: " + error);
        }
    }

    private static void NoteBoundary(string level, long epoch)
    {
        var previousLevel = _lastLevel;
        var previousEpoch = _lastEpoch;
        _lastLevel = level;
        _lastEpoch = epoch;
        if (previousLevel.Length == 0 && previousEpoch == long.MinValue) return;
        RecSession.Note("boundary", json =>
        {
            json.WriteString("from", previousLevel.Length == 0 ? "unavailable" : previousLevel);
            json.WriteString("to", level);
            json.WriteNumber("fromEpoch", previousEpoch == long.MinValue ? -1 : previousEpoch);
            json.WriteNumber("toEpoch", epoch);
        });
        CaptureRegistry.SnapshotAll("level_boundary");
    }


    private static void FinishOperation(bool starting)
    {
        bool stop;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (starting) _starting = false; else _ticking = false;
            _operationThread = 0;
            stop = _stopRequested && _stopWaiters == 0;
            if (stop) _stopRequested = false;
            Monitor.PulseAll(Gate);
        }
        if (stop) Stop();
    }

    /// <summary>The level-cleanup path: the last snapshot of the level and the summary that says what the session
    /// holds, before the world it describes is gone.</summary>
    internal static void LevelEnded(string phase)
    {
        if (_stopping || !RecSession.Active) return;
        CaptureRegistry.SnapshotAll("level_cleanup_" + phase);
        RecSession.Note("summary", json =>
        {
            json.WriteString("phase", phase);
            json.WriteNumber("bytes", RecSession.BytesWritten);
            json.WriteNumber("dropped", RecSession.Dropped);
            json.WriteBoolean("budgetReached", RecSession.BudgetReached);
            json.WriteNumber("tracedMethods", RecTracerRuntime.InstalledMethods.Count);
            json.WriteNumber("traceSkips", RecTracerRuntime.Skipped.Count);
            json.WriteString("level", RecTime.Level);
        });
        // Live lifecycle hooks enqueue evidence; the process stop path owns the final drain.
    }

    /// <summary>The BepInEx root this session writes under. It is read once at startup: a later read would be a
    /// second source of truth for where the session already wrote its files.</summary>
    private static string Root { get; set; } = "";

    private static void LoadTrace()
    {
        RecTraceBudget.Start();
        RecTraceBudget.BeginFrame(RecTime.Frame);
        _traceTiming = new(Plugin.PluginGuid, message => Plugin.PluginLog.LogInfo(message), "trace");
        _traceTiming.Mark("discovery");
        _traceGenerationHeldFrames = 0;
        _traceHeldForGeneration = GameStateManager.CurrentStateName == eGameStateName.Generating;
        GTFO.API.EventAPI.OnGameStateChanged += TraceGameState;

        RecTracerRuntime.BeginLoadProfiles(TraceFiles(), null,
            profile => RecTraceProfiles.Enabled(profile, Settings.RecorderTraceAllProfiles.Value,
                (name, enabled) => Plugin.Configuration.Bind("Trace Profiles", name, enabled,
                    "Enable the packaged trace profile '" + name + "'. Restart required.").Value),
            bind => ForgeCore.Settings.Batch(Plugin.PluginGuid, _ => bind()));
        _traceTiming.Mark("traceInstall");
    }

    private static IEnumerable<string> TraceFiles()
    {
        var sourceDirectory = Path.Combine(Path.GetDirectoryName(typeof(RecRuntime).Assembly.Location) ?? ".", "probes", RecTracer.ConfigFolder);
        var files = RecTraceProfiles.Discover(sourceDirectory, failure =>
        {
            Plugin.PluginLog.LogError("development.trace-profile-refused file=" + failure.Relative + " code="
                + failure.Code + " reason=" + failure.Reason);
            RecSession.Note("trace_profile_refused", json =>
            {
                json.WriteString("file", failure.Relative);
                json.WriteString("code", failure.Code);
                json.WriteString("reason", failure.Reason);
            });
        });
        RecSession.Note("trace_config", json =>
        {
            json.WriteString("directory", sourceDirectory);
            json.WriteNumber("files", files.Count);
        });
        foreach (var file in files) yield return file;
    }

    /// <summary>One frame's share of the trace install queue, inside the shared diagnostic allowance: a frame that has no
    /// allowance left, or a level that is generating, waits for the next frame.</summary>
    private static void TraceStep()
    {
        if (!_traceScheduled) return;
        if (_traceHeldForGeneration) { _traceGenerationHeldFrames++; return; }
        if (!ForgeCore.DiagnosticCpuBudget.Shared.TryReserveFor(TraceWork, out var reservation)) return;
        using (reservation)
        {
            using (RecGate.Enter(Gate, "recorder.lock-timeout"))
            {
                if (_stopping || !RecSession.Active) { _traceScheduled = false; return; }
                if (_ticking || _starting) return;
                _ticking = true; _operationThread = Environment.CurrentManagedThreadId;
            }
            try { Try("tracer", InstallTraceBatch, () => StopTrace("trace-startup-failed")); }
            finally { FinishOperation(starting: false); }
            if (!RecTracerRuntime.InstallationPending) _traceScheduled = false;
        }
    }

    private static void InstallTraceBatch()
    {
        var progress = RecTracerRuntime.InstallNextBatch(TracePatchesPerFrame, TraceCanContinue);
        var advanced = progress.HasNewProgressSince(_traceInstallProgress);
        _traceInstallProgress = progress;
        if (advanced || progress.Complete) _traceInstallSequence++;
        if (!progress.Complete)
        {
            if (advanced)
            {
                // Every step goes to the session report; LogOutput gets one line per tenth of the queue, which is
                // all the pack-test watchdog needs to see the install advancing.
                RecSession.Note("trace_install_progress", json =>
                {
                    json.WriteNumber("sequence", _traceInstallSequence);
                    json.WriteNumber("attempted", progress.Attempted);
                    json.WriteNumber("total", progress.Total);
                    json.WriteNumber("installed", progress.Installed);
                    json.WriteNumber("installMs", progress.InstallMs);
                    json.WriteNumber("elapsedMs", progress.ElapsedMs);
                });
                var tenth = progress.Total <= 0 ? 0 : (int)((long)progress.Attempted * 10 / progress.Total);
                if (tenth > _traceLoggedTenth)
                {
                    _traceLoggedTenth = tenth;
                    WriteTraceInstallProgress("development.trace-install-progress", progress);
                }
            }
            return;
        }
        RecSession.WritePatches(RecTracerRuntime.Report);
        WriteTraceInstallProgress("development.trace-ready", progress);
        FinishTraceTiming("loaded");
    }

    private static void WriteTraceInstallProgress(string code, RecTraceInstallProgress progress)
    {
        Plugin.PluginLog.LogInfo(code + " sessionId=" + RecSession.SessionId
            + " sequence=" + _traceInstallSequence.ToString(CultureInfo.InvariantCulture)
            + " realSeconds=" + (RecTime.Milliseconds / 1000d).ToString("F3", CultureInfo.InvariantCulture)
            + " attempted=" + progress.Attempted.ToString(CultureInfo.InvariantCulture)
            + " total=" + progress.Total.ToString(CultureInfo.InvariantCulture)
            + " installed=" + progress.Installed.ToString(CultureInfo.InvariantCulture)
            + " discoveryMs=" + progress.DiscoveryMs.ToString("F3", CultureInfo.InvariantCulture)
            + " installMs=" + progress.InstallMs.ToString("F3", CultureInfo.InvariantCulture)
            + " elapsedMs=" + progress.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture)
            + " discoveryPending=" + progress.DiscoveryPending
            + " maxDiscoveryStepMs=" + progress.MaxDiscoveryStepMs.ToString("F3", CultureInfo.InvariantCulture)
            + " maxInstallStepMs=" + progress.MaxInstallStepMs.ToString("F3", CultureInfo.InvariantCulture)
            + " batches=" + progress.Batches.ToString(CultureInfo.InvariantCulture)
            + " generationHeldFrames=" + _traceGenerationHeldFrames.ToString(CultureInfo.InvariantCulture));
    }

    private static void StopTrace(string reason)
    {
        _traceScheduled = false;
        RecTraceBudget.Stop();
        FinishTraceTiming(reason);
        RecTracerRuntime.UninstallAll(reason);
        if (RecTracerRuntime.CleanupPending) throw new InvalidOperationException("Trace hook cleanup is still pending.");
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) Started.Remove("tracer");
    }

    private static void FinishTraceTiming(string result)
    {
        GTFO.API.EventAPI.OnGameStateChanged -= TraceGameState;
        _traceHeldForGeneration = false;
        _traceTiming?.Complete(result);
        _traceTiming?.Dispose(); _traceTiming = null;
    }

    private static void WriteStartup()
    {
        RecSession.Write("session", "startup", json =>
        {
            json.WriteString("plugin", Plugin.PluginName + " " + Plugin.PluginVersion);
            json.WriteString("forgeEngine", EngineLookup.Version ?? "absent");
            json.WriteString("mode", EngineLookup.Mode);
            json.WriteString("directory", RecSession.Directory);
            json.WriteString("session", RecSession.SessionId);
            json.WriteString("role", RecSession.Role);
            json.WriteString("slot", RecTime.Slot);
            json.WriteString("gameVersion", RecUnity.GameVersion);
            json.WriteString("unityVersion", RecUnity.UnityVersion);
            json.WriteNumber("budgetBytes", Settings.RecorderBudgetGiB.Value * 1024L * 1024 * 1024);
            json.WriteNumber("segmentBytes", Settings.RecorderSegmentMiB.Value * 1024L * 1024);
            // The tracer's report lists every patch and refusal and outgrows the record budget once installed (a renewed session
            // starts with it whole); it is kept as its own document, which the session's "patches" record points at.
            json.WriteString("traceDocument", RecSession.PatchesDocument);

            json.WriteNumber("process", Environment.ProcessId);
            json.WriteString("startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        });
    }

    private static void StartPopulation()
    {
        RecPopulationBudget.Start();
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) if (!Started.Contains("population_budget")) Started.Add("population_budget");
    }
    private static void StopPopulation()
    {
        RecPopulationBudget.Stop();
        if (RecPopulationBudget.CleanupPending) throw new InvalidOperationException("Population hook cleanup is still pending.");
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) Started.Remove("population_budget");
    }
    private static void PopulationFailure(Exception error)
    {
        Failure = "population_budget: " + error.GetType().Name + ": " + error.Message;
        Plugin.PluginLog.LogError("Forge recorder population budget transition failed: " + error);
        RecSession.Note("stage_failed", json =>
        {
            json.WriteString("stage", "population_budget");
            json.WriteString("error", error.ToString());
            json.WriteBoolean("cleanupPending", RecPopulationBudget.CleanupPending);
            json.WriteNumber("requested", RecPopulationBudget.Requested);
        });
    }

    private static void Stage(string name)
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout")) if (!Started.Contains(name)) Started.Add(name);
    }

    /// <summary>Releases one started stage. The names are the ones <see cref="Try"/> records, and the order they are
    /// released in is the reverse of the order they started in.</summary>
    private static void StopStage(string name, bool processExit)
    {
        switch (name)
        {
            case "collection":
                _stopCollection?.Invoke();
                break;
            case "process_inventory":
                RecProcessInventory.Stop();
                break;
            case "population_budget":
                StopPopulation();
                PopulationSwitch.Tick(false, false, Environment.TickCount64);
                break;
            case "tracer" when processExit:
                _traceScheduled = false;
                RecTraceBudget.Stop();
                FinishTraceTiming("process-exit");
                RecTracerRuntime.ReleaseForProcessExit();
                break;
            case "tracer":
                StopTrace("recorder-stop");
                break;
            case "log":
                RecLog.Stop();
                break;
        }
    }
}
