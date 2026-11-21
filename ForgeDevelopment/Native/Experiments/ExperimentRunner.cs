using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace ForgeDevelopment.Native;

internal sealed class ExperimentRecord
{
    internal ExperimentRunStatus Status { get; set; } = ExperimentRunStatus.NotRun;
    internal int Done { get; set; }
    internal int Total { get; set; }
    internal string Message { get; set; } = "";
    internal string When { get; set; } = "";
    internal double Milliseconds { get; set; }
}

internal sealed record ExperimentEvent(string Type, string Message);

/// <summary>
/// Runs one experiment command at a time on the main thread. The runner owns what the engine deliberately does
/// not know: the host/client gate, the session channel, the per-frame budget and the on-disk record of what
/// has already been executed.
/// </summary>
internal sealed class ExperimentRunner : MonoBehaviour
{
    private const string StateFolder = "experiments";
    private const int MaximumEvents = 512;

    private static readonly Dictionary<string, ExperimentRecord> Records = new(StringComparer.Ordinal);
    private static readonly List<ExperimentEvent> Events = new();

    private ExperimentCommand? _command;
    private ExperimentEngine? _engine;
    private ExperimentTargetSpec _spec = null!;
    private string _runId = "";
    private string _result = "";
    private long _startedTicks;
    private double _frameStart;
    private bool _sessionStarted;
    private bool _previousHooked;
    private bool _cancelling;
    private Action<ExperimentOutcome>? _observer;
    private int _threadId;
    private string _readSession = "";
    private RecSession.RecMark _evidenceMark;
    private long _worldEpoch;
    private long _runWorldEpoch;
    private ExperimentWorldReadback? _worldReadback;
    // The drive evidence barrier posted at Finish; later frames read its answer, the game thread never waits for the writer.
    private System.Threading.Tasks.Task<RecDrainResult>? _drain;
    private float _drainDeadline;

    internal static ExperimentRunner? Instance { get; private set; }

    public ExperimentRunner(IntPtr pointer) : base(pointer) { }

    private void Awake()
    {
        _threadId = Environment.CurrentManagedThreadId;
        Instance = this;
        ExperimentRequests.Start();
    }

    private void OnDestroy()
    {
        if (!ReferenceEquals(Instance, this)) return;
        _cancelling = true;
        _worldReadback?.Cancel();
        try
        {
            _engine?.Cancel("experiment runner destroyed");
            ExperimentDamage.Stop();
            CleanupDrive();
        }
        catch (Exception error)
        {
            FailRun(_command, error);
            ExperimentRequests.Stop("Failed", error.ToString());
            throw;
        }
        finally
        {
            try { ExperimentRequests.Stop("Aborted", "experiment runner destroyed before completion"); }
            finally
            {
                _observer = null;
                if (ReferenceEquals(Instance, this)) Instance = null;
            }
        }
    }

    internal static ExperimentRecord Record(string id)
    {
        _ = ExperimentCatalog.All;
        return Records.TryGetValue(id, out var record) ? record : new ExperimentRecord();
    }

    internal static IReadOnlyDictionary<string, ExperimentRecord> AllRecords { get { _ = ExperimentCatalog.All; return Records; } }

    internal static IReadOnlyList<ExperimentEvent> Recent { get { _ = ExperimentCatalog.All; return Events; } }

    internal static bool Running => Instance != null && Instance._engine is { Finished: false };
    internal static bool AllowsDamage => Instance is { _cancelling: false, _engine: { Finished: false, Failed: false } };
    internal static int SessionExecutions { get; private set; }

    internal static string CurrentCommand => Instance?._command?.Id ?? "";

    /// <summary>Loads the catalog into the config directory and restores what has already been executed.</summary>
    internal static string Load(string shippedDirectory)
    {
        ExperimentCatalog.BeginLoadAttempt();
        var seed = ExperimentCatalog.Seed(shippedDirectory);
        ExperimentCatalog.Load();
        LoadState();
        return seed;
    }

    /// <summary>Runs a command by catalog index. Returns null when it started, otherwise the reason it did not.</summary>
    internal static string? Run(int index)
    {
        var runner = Instance;
        if (runner == null) return "the experiment runner component is not attached";
        if (ExperimentCatalog.All.Count == 0) return "no command file loaded; see " + ExperimentCatalog.DirectoryPath;
        if (index < 0 || index >= ExperimentCatalog.All.Count) return "no command at index " + index.ToString(CultureInfo.InvariantCulture);
        return runner.Begin(ExperimentCatalog.All[index]);
    }

    internal static string? Run(string id)
    {
        var runner = Instance;
        if (runner == null) return "the experiment runner component is not attached";
        var index = -1;
        for (var i = 0; i < ExperimentCatalog.All.Count; i++)
            if (ExperimentCatalog.All[i].Id == id) { index = i; break; }
        return index < 0 ? "no command with id '" + id + "'" : runner.Begin(ExperimentCatalog.All[index]);
    }

    internal static void Cancel() => Cancel("cancelled from the panel");

    internal static void Cancel(string reason)
    {
        var runner = Instance;
        if (reason == "level cleanup" && runner?._engine is { Finished: false, Failed: false }
            && runner._worldReadback?.LevelCleanup() == true)
        {
            // Returning to the lobby owns this cleanup. Release native leases now, but keep its
            // pending readback alive until the new world, Lobby and absence of the floor agree.
            try { ExperimentDamage.Stop(); runner.CleanupDrive(); }
            catch (Exception error) { runner.FailRun(runner._command, error); throw; }
            return;
        }
        runner?._worldReadback?.Cancel();
        if (runner != null) runner._cancelling = true;
        try { runner?._engine?.Cancel(reason); runner?.CleanupDrive(); }
        catch (Exception error)
        {
            // Engine cancellation reports before replacing its main stack with cleanup. A reporting
            // failure must retire that engine; its aborted flag alone cannot stop later main actions.
            runner?.FailRun(runner._command, error);
            throw;
        }
        ExperimentDamage.Stop();
    }

    internal static string? Run(ExperimentCommand command, Func<ExperimentAction, string?> guard, Action<ExperimentOutcome> observer)
        => Instance == null ? "the experiment runner component is not attached" : Instance.Begin(command, guard, observer);

    /// <summary>Destroys the markers and clones experiments created, without running a command.</summary>
    internal static string CleanUp() => ExperimentObjects.DestroyAll();

    /// <summary>The live resolution of a selector, for the panel's "what is the crosshair on" line.</summary>
    internal static string Describe(string target)
    {
        if (!ExperimentTargetSpec.TryParse(target, out var spec)) return "unknown selector";
        try
        {
            var resolved = ExperimentTargeting.Resolve(spec);
            return resolved.Ok ? resolved.Describe + " (" + resolved.Count + ")" : "unresolved: " + resolved.Error;
        }
        catch (Exception e)
        {
            return "resolution failed: " + e.Message;
        }
    }

    [HideFromIl2Cpp]
    private string? Begin(ExperimentCommand command, Func<ExperimentAction, string?>? guard = null, Action<ExperimentOutcome>? observer = null)
    {
        if (Running) return "an experiment is already running: " + _command?.Id;
        if (_drain != null) return "the previous command's records are still being written: " + _command?.Id;
        // A previous failed removal must be retried before any new command can own damage hooks.
        ExperimentDamage.Stop();
        try { return BeginCore(command, guard, observer); }
        catch (Exception error) { FailRun(command, error); throw; }
    }

    [HideFromIl2Cpp]
    private string? BeginCore(ExperimentCommand command, Func<ExperimentAction, string?>? guard, Action<ExperimentOutcome>? observer)
    {
        if (!ExperimentTargetSpec.TryParse(command.Target, out var spec)) return "target '" + command.Target + "' could not be parsed";
        var host = RecTime.Role;
        if (command.Host != ExperimentHost.Any)
        {
            var wantsHost = command.Host == ExperimentHost.Host;
            if ((host == "host") != wantsHost) return "the command requires " + command.Host + " but this instance is " + host;
        }

        // A full session persists nothing more: the command starts on a renewed one instead. The renewal and the close of an
        // earlier session run on the writer side; a command asked for meanwhile is refused rather than waited for.
        RecRuntime.RenewAtBudget();
        if (RecRuntime.Renewing) return "the recorder is moving to a new session; run the command again in a moment";
        if (!RecSession.Active && RecSession.WriterAlive) return "the recorder is still closing the previous session; run the command again in a moment";

        _cancelling = false;
        _spec = spec;
        _command = command;
        _result = "";
        _runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + command.Id;
        _startedTicks = Stopwatch.GetTimestamp();
        _frameStart = Time.realtimeSinceStartup;

        if (!RecSession.Active)
        {
            _sessionStarted = RecSession.Start(Paths.BepInExRootPath, Plugin.PluginVersion, EngineLookup.Version ?? "absent");
            if (!_sessionStarted) throw new InvalidOperationException("The recorder session could not be started.");
        }
        _readSession = RecSession.SessionId;
        _evidenceMark = RecSession.Mark();
        _worldEpoch = RecTime.WorldEpoch;
        _runWorldEpoch = _worldEpoch;
        _worldReadback = new ExperimentWorldReadback(_worldEpoch);
        _engine = new ExperimentEngine(command, spec, new ExperimentRuntime(), OnOutcome, guard);
        _observer = observer;
        SessionExecutions++;
        var annotation = Record(command.Id);
        // Record() is also a read-only UI lookup. Only a real run creates a persisted history entry.
        Records[command.Id] = annotation;
        annotation.Status = ExperimentRunStatus.Rejected;
        annotation.Total = command.Steps.Count;
        annotation.Message = "running";
        annotation.When = DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture);
        _previousHooked = ExperimentTrace.Capturing;
        if (UsesTraceWait(command)) ExperimentTrace.Start();
        Publish("command", command, null);
        Log($"started {command.Id} ({command.Steps.Count} steps) target={_spec.Describe()} host={host}");
        return null;
    }

    private void Update()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentExperiment);
        var cpu = ForgeCore.DiagnosticCpuBudget.Shared;
        cpu.BeginFrame(RecTime.Frame);
        // Aborts and terminal cleanup must unwind even after optional observations used the allowance.
        var required = _cancelling || _engine?.Finished == true;
        var reservation = default(ForgeCore.DiagnosticReservation);
        if (required) reservation = cpu.MeasureRequired();
        else if (!cpu.TryReserve(out reservation)) return;
        using var charge = reservation;
        var command = _command;
        try
        {
            ExperimentRequests.Tick();
            command = _command;
            UpdateCore();
        }
        catch (Exception error)
        {
            // Update is a Unity message: a rethrow only reaches the IL2CPP trampoline, which drops it. The failed run is
            // the result - FailRun records it and completes the request - and the log line names the command and cause.
            var failed = _command ?? command;
            FailRun(failed, error);
            Log("failed " + (failed?.Id ?? "<no command>") + ": " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void UpdateCore()
    {
        var engine = _engine;
        if (engine == null) return;
        if (engine.Finished) { Finish(); return; }
        // A lease exists only during this synchronous main-thread slice. It is never carried to a
        // later frame, and every native member read rechecks the run's session and declared world.
        using (RecObservation.EnterReadLease(ValidateReadContext))
        {
            ValidateReadContext();
            _frameStart = Time.realtimeSinceStartup;
            var budget = Settings.InspectionBudget.Value;
            while (!engine.Finished && (_cancelling || ForgeCore.DiagnosticCpuBudget.Shared.CanContinue))
            {
                var elapsed = (Time.realtimeSinceStartup - _frameStart) * 1000.0;
                var action = engine.Next(elapsed, budget);
                if (engine.Failed) { _cancelling = true; _worldReadback?.Cancel(); ExperimentDamage.Stop(); }
                if (action == null) break;
                engine.Advance(action);
                if (engine.Failed) { _cancelling = true; _worldReadback?.Cancel(); ExperimentDamage.Stop(); }
                if ((Time.realtimeSinceStartup - _frameStart) * 1000.0 >= budget) break;
            }
            ValidateReadContext();
        }
        if (engine.Finished) Finish();
    }

    private void ValidateReadContext()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("experiment.read-wrong-thread");
        if (!RecSession.Active || RecSession.SessionId != _readSession)
            throw new InvalidOperationException("experiment.read-session-changed");
        _worldReadback!.Validate(RecTime.WorldEpoch);
        _worldEpoch = _worldReadback.World;
    }

    // Only the typed native menu operations call this, immediately before issuing the native action.
    // Arbitrary experiment calls never gain permission to follow a changed world.
    internal static ExperimentCallResult ChangeWorld(bool returning, Action issue)
        => ChangeWorld(readback => readback.Begin(returning), issue, null);

    /// <summary>Leaves the session hub from a cleaned lobby; <paramref name="left"/> is the native readback that the hub is gone.</summary>
    internal static ExperimentCallResult LeaveLobby(Action issue, Func<bool> left)
        => ChangeWorld(readback => readback.BeginLeave(), issue, left);

    private static ExperimentCallResult ChangeWorld(Action<ExperimentWorldReadback> begin, Action issue, Func<bool>? settled)
    {
        var runner = Instance ?? throw new InvalidOperationException("experiment.runner-unavailable");
        runner.ValidateReadContext();
        var readback = runner._worldReadback!;
        begin(readback);
        try { issue(); }
        catch { readback.Cancel(); throw; }
        ExperimentCallResult Poll()
        {
            runner.ValidateReadContext();
            var state = GameStateManager.CurrentStateName.ToString();
            if (settled?.Invoke() == false || !readback.Readback(state, LevelGeneration.Builder.Current?.m_currentFloor != null))
                return ExperimentCallResult.Await(Poll);
            var detail = "native-world-readback;state=" + state + ";world=" + readback.World
                + ";floorPresent=" + (LevelGeneration.Builder.Current?.m_currentFloor != null);
            return ExperimentCallResult.Done(detail, detail);
        }
        return ExperimentCallResult.Await(Poll);
    }

    /// <summary>Aborted runs still finish their cleanup stage, so the runner keeps ticking until then.</summary>
    internal void Tick() => Update();

    private void Finish()
    {
        // Cleanup precedes reporting success, and is independent of command-authored cleanup steps.
        _cancelling = true;
        var engine = _engine;
        var command = _command;
        if (_drain == null)
        {
            ExperimentDamage.Stop();
            CleanupDrive();
            // Only what this command's run lost counts against it, not an earlier drop of the same session. The barrier is
            // posted now and answered by the writer; the run finishes on the frame its answer arrives, at most 2 s later.
            if (command != null && UsesDrive(command.Steps))
            {
                _drain = RecSession.RequestFlush(_evidenceMark);
                _drainDeadline = Time.realtimeSinceStartup + 2f;
            }
        }
        if (_drain != null)
        {
            if (!_drain.IsCompleted && Time.realtimeSinceStartup < _drainDeadline) return;
            var drain = _drain.IsCompleted ? _drain.Result : RecDrainResult.TimedOut;
            _drain = null;
            if (drain != RecDrainResult.Completed)
                throw new InvalidOperationException("experiment.drive-evidence-not-persisted:" + drain);
        }
        _engine = null;
        _command = null;
        _observer = null;
        if (engine == null || command == null) return;
        var annotation = Record(command.Id);
        Records[command.Id] = annotation;
        // The engine distinguishes a completed command from user cancellation/rejection. This is
        // execution bookkeeping, not acceptance of the game's behavior or a probe's conclusions.
        annotation.Status = engine.FinalStatus;
        annotation.Done = engine.Done;
        annotation.Total = command.Steps.Count;
        annotation.Message = annotation.Status == ExperimentRunStatus.Passed
            ? _result.Length > 0 ? _result : "command steps completed"
            : engine.Blocked;
        annotation.When = DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture);
        annotation.Milliseconds = (Stopwatch.GetTimestamp() - _startedTicks) * 1000.0 / Stopwatch.Frequency;
        Publish("result", command, annotation);
        SaveState();
        var cleaned = ExperimentObjects.DestroyAll();
        RecSession.Write("exp", "cleanup", json =>
        {
            json.WriteString("command", command.Id);
            json.WriteString("run", _runId);
            json.WriteString("objects", cleaned);
        });
        Add(new ExperimentEvent("cleanup", cleaned));
        if (!_previousHooked) ExperimentTrace.Stop();
        _previousHooked = false;
        if (_sessionStarted)
        {
            _sessionStarted = false;
            // The writer drains everything accepted before it closes; the game thread only closes admission.
            RecSession.RequestStop();
        }
        ExperimentRequests.Completed(command.Id, annotation, _runWorldEpoch, _readSession);
        Log($"{annotation.Status.ToString().ToLowerInvariant()} {command.Id}: {annotation.Done}/{annotation.Total} steps, {annotation.Message}");
    }

    [HideFromIl2Cpp]
    private void FailRun(ExperimentCommand? command, Exception original)
    {
        _cancelling = true;
        _worldReadback?.Cancel();
        _drain = null;
        _engine = null;
        _command = null;
        _observer = null;
        try { ExperimentDamage.Stop(); }
        catch (Exception cleanup) { NoteFailure(original, "ExperimentDamage.CleanupFailure", cleanup); }
        try { CleanupDrive(); }
        catch (Exception cleanup) { original = new AggregateException("experiment drive cleanup failed", original, cleanup); }
        try { ExperimentObjects.DestroyAll(); }
        catch (Exception cleanup) { NoteFailure(original, "ExperimentObjects.CleanupFailure", cleanup); }
        try
        {
            if (command != null)
            {
                var record = Record(command.Id);
                Records[command.Id] = record;
                record.Status = ExperimentRunStatus.Failed;
                record.Message = original.ToString();
                SaveState();
            }
        }
        catch (Exception reporting) { NoteFailure(original, "ExperimentRunner.ReportingFailure", reporting); }
        finally
        {
            try { if (!_previousHooked) ExperimentTrace.Stop(); }
            catch (Exception cleanup) { NoteFailure(original, "ExperimentTrace.CleanupFailure", cleanup); }
            _previousHooked = false;
            if (_sessionStarted)
            {
                _sessionStarted = false;
                try { RecSession.RequestStop(); }
                catch (Exception cleanup) { NoteFailure(original, "ExperimentSession.CleanupFailure", cleanup); }
            }
        }
        if (command != null) ExperimentRequests.Completed(command.Id, Record(command.Id), _runWorldEpoch, _readSession);
    }

    [HideFromIl2Cpp]
    private void CleanupDrive()
    {
        using (RecObservation.EnterReadLease(ValidateReadContext)) AuthoringGameDrive.Cleanup();
    }

    private static bool UsesDrive(IReadOnlyList<ExperimentStep> steps)
        => steps.Any(step => step.Kind == ExperimentStepKind.Call && step.Name.StartsWith("drive.", StringComparison.Ordinal)
            || step.Kind == ExperimentStepKind.Repeat && UsesDrive(step.Body));

    [HideFromIl2Cpp]
    private static void NoteFailure(Exception original, string key, Exception cleanup)
    {
        try { original.Data[key] = cleanup; }
        catch (Exception) { /* Preserve the original failure even for an unwritable Data dictionary. */ }
    }

    [HideFromIl2Cpp]
    private void OnOutcome(ExperimentOutcome outcome)
    {
        if (outcome.Kind == "read" && outcome.Ok)
        {
            var marker = outcome.Message.IndexOf(" :: ", StringComparison.Ordinal);
            if (marker >= 0) _result = outcome.Message[(marker + 4)..];
        }
        else if (outcome.Kind == "call" && outcome.Ok && _command != null && UsesDrive(_command.Steps))
        {
            _result = outcome.Message;
        }
        RecSession.Write("exp", "step", json =>
        {
            json.WriteString("command", _command?.Id ?? "");
            json.WriteString("run", _runId);
            json.WriteString("step", outcome.Kind);
            json.WriteBoolean("ok", outcome.Ok);
            json.WriteString("detail", RecReflect.Truncate(outcome.Message, 4096));
            json.WriteNumber("ms", Math.Round(outcome.Milliseconds, 4));
        });
        Add(new ExperimentEvent(outcome.Ok ? outcome.Kind : "error", outcome.Message));
        _observer?.Invoke(outcome);
    }

    [HideFromIl2Cpp]
    private void Publish(string kind, ExperimentCommand command, ExperimentRecord? record)
    {
        RecSession.Write("exp", kind, json =>
        {
            json.WriteString("command", command.Id);
            json.WriteString("title", command.Title);
            json.WriteString("run", _runId);
            json.WriteString("target", command.Target);
            json.WriteString("host", command.Host.ToString().ToLowerInvariant());
            json.WriteString("source", command.Source);
            json.WriteStartArray("points");
            foreach (var point in command.Points) json.WriteStringValue(point);
            json.WriteEndArray();
            if (record == null) return;
            json.WriteString("status", record.Status.ToString().ToLowerInvariant());
            json.WriteNumber("done", record.Done);
            json.WriteNumber("total", record.Total);
            json.WriteString("message", RecReflect.Truncate(record.Message, 2048));
            json.WriteNumber("ms", Math.Round(record.Milliseconds, 4));
        });
    }

    private static bool UsesTraceWait(ExperimentCommand command)
    {
        static bool Scan(IReadOnlyList<ExperimentStep> steps)
        {
            foreach (var step in steps)
            {
                if (step.Kind == ExperimentStepKind.Wait && step.Wait.TraceType.Length > 0) return true;
                if (step.Kind == ExperimentStepKind.Repeat && Scan(step.Body)) return true;
            }
            return false;
        }
        return Scan(command.Steps) || Scan(command.Cleanup);
    }

    private static void Add(ExperimentEvent entry)
    {
        Events.Add(entry);
        if (Events.Count > MaximumEvents) Events.RemoveRange(0, Events.Count - MaximumEvents);
    }

    internal static void Log(string message, string area = "experiment")
    {
        try { Plugin.PluginLog.LogInfo(area + ": " + message); }
        catch (Exception) { } // Last resort: the plugin log itself failed, and there is no other sink to report to.
    }

    private static string StatePath => Path.Combine(DevelopmentPaths.Root, StateFolder, "state.forge");

    private static void LoadState()
    {
        if (!File.Exists(StatePath)) return;
        var restored = new Dictionary<string, ExperimentRecord>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(StatePath));
            if (!document.RootElement.TryGetProperty("commands", out var commands)) return;
            foreach (var entry in commands.EnumerateObject())
            {
                var value = entry.Value;
                restored.Add(entry.Name, new ExperimentRecord
                {
                    Status = Enum.TryParse<ExperimentRunStatus>(Text(value, "status"), true, out var status) ? status : ExperimentRunStatus.NotRun,
                    Done = Integer(value, "done"),
                    Total = Integer(value, "total"),
                    Message = Text(value, "message"),
                    When = Text(value, "when"),
                    Milliseconds = Number(value, "ms")
                });
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            // The file is only the last run history: a damaged one starts the history over and never stops the commands loading.
            Log("the experiment state " + StatePath + " is damaged and was ignored: " + error.Message);
            return;
        }
        // A failed restore publishes no partial history; an earlier command's in-memory result wins over disk.
        foreach (var entry in restored) Records.TryAdd(entry.Key, entry.Value);
    }

    private static void SaveState()
    {
        try
        {
            var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("format", "forge-development-experiments");
                writer.WriteStartObject("commands");
                foreach (var (id, record) in Records.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject(id);
                    writer.WriteString("status", record.Status.ToString().ToLowerInvariant());
                    writer.WriteNumber("done", record.Done);
                    writer.WriteNumber("total", record.Total);
                    writer.WriteString("message", record.Message);
                    writer.WriteString("when", record.When);
                    writer.WriteNumber("ms", Math.Round(record.Milliseconds, 4));
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            ForgeCore.Store.WriteBytes(StatePath, stream.ToArray());
        }
        catch (Exception e)
        {
            Log("the experiment state could not be written: " + e.Message);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : "";

    private static int Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.TryGetInt32(out var value) ? value : 0;

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.TryGetDouble(out var value) ? value : 0;
}
