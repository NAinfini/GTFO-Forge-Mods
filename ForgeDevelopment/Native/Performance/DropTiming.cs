using System;
using System.Diagnostics;
using System.Globalization;
using GTFO.API;
using LevelGeneration;

namespace ForgeDiagnostics;

// Development only, never in a player's install: ForgeDevelopment (DropTimingHost) installs the drop-request hook. The one attempt clock of a drop; this observer changes no native result,
// budget, seed or wait threshold and emits no per-frame line: between two boundaries it writes only a `drop.wait` line after each
// QuietSeconds without one (what the game state and frame rate are while nothing is marked) and a `drop.stall` line for each frame
// that took StallSeconds or more (a blocked main thread shows as one long frame, not as a silence), both read from the frame
// callback with no native call beyond the game state name.
internal static class DropTiming
{
    private const double QuietSeconds = 5, StallSeconds = 1;
    private static long _lastQuietAt, _lastTickAt;
    private static int _quietFrame;

    private static readonly ForgeCore.DropTimingMetrics Metrics = new(Stopwatch.Frequency);
    private static IDisposable? _hooks;
    private static Func<string>? _worldEpoch;
    private static Action<string>? _write;
    private static long _requestedAt, _previousAt;
    private static long _dropRequestedAt;
    private static int _attempt;
    private static int _startFrame;
    private static string _previousPhase = "attempt-start";
    private static bool _active, _selectionPending, _selectionConfirmed;
    private static bool _requested, _requestReturned, _generating, _started, _done, _factoryDone, _landed, _entered;

    internal static (int Generation, long StartedAt, long Frequency, bool Active, bool FactoryDone, bool Ready, bool Entered) Current
        => (_attempt, _requestedAt, Stopwatch.Frequency, _write != null && _active, _factoryDone, _landed, _entered);

    /// <summary><paramref name="hooks"/> installs the request hooks (<see cref="RequestPrefix"/> and its ends) and answers what takes them off;
    /// <paramref name="worldEpoch"/> names the world a record belongs to.</summary>
    internal static void Start(Action<string> write, Func<string> worldEpoch, Func<IDisposable> hooks)
    {
        if (_write != null) return;
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _worldEpoch = worldEpoch;
        try
        {
            _hooks = hooks();
            EventAPI.OnGameStateChanged += State;
            LevelAPI.OnBuildStart += BuildStart;
            LevelAPI.OnFactoryStart += FactoryStart;
            LevelAPI.OnBuildDone += BuildDone;
            LevelAPI.OnFactoryDone += FactoryDone;
            LevelAPI.OnEnterLevel += InLevel;
            LevelAPI.OnLevelCleanup += Cleanup;
        }
        catch { Stop(); throw; }
    }

    internal static void Stop()
    {
        EventAPI.OnGameStateChanged -= State;
        LevelAPI.OnBuildStart -= BuildStart;
        LevelAPI.OnFactoryStart -= FactoryStart;
        LevelAPI.OnBuildDone -= BuildDone;
        LevelAPI.OnFactoryDone -= FactoryDone;
        LevelAPI.OnEnterLevel -= InLevel;
        LevelAPI.OnLevelCleanup -= Cleanup;
        _write = null;
        Metrics.Reset();
        _active = _selectionPending = _selectionConfirmed = false;
        _requested = _requestReturned = _generating = _started = _done = _factoryDone = _landed = _entered = false;
        _requestedAt = _previousAt = _dropRequestedAt = _lastQuietAt = _lastTickAt = 0; _previousPhase = "attempt-start";
        var hooks = _hooks; _hooks = null;
        hooks?.Dispose();
    }

    internal static void RequestPrefix()
    {
        try { RequestDrop(); }
        catch (Exception error)
        {
            // A failed observer cannot replace the native drop action. Missing
            // stages remain incomplete in the reader; never synthesize success.
            try { _write?.Invoke("drop.timing-failed phase=drop-requested reason=" + error); }
            catch (Exception reporter) { Console.Error.WriteLine("drop.timing-failed phase=drop-requested reason=" + error + " reporter=" + reporter.GetType().Name); }
        }
    }
    internal static void RequestPostfix() => RequestFinished(null);
    internal static Exception? RequestFinalizer(Exception? __exception)
    {
        if (__exception != null) RequestFinished(__exception);
        return __exception;
    }
    private static void RequestFinished(Exception? error)
    {
        if (!_requested || _requestReturned) return;
        _requestReturned = true;
        MarkBoundary(_attempt, error == null ? "drop-request-returned" : "drop-request-failed",
            "CM_PageLoadout.EXT_StartExpedition");
    }
    internal static void JobDequeued(bool successful) { if (successful) Metrics.JobStarted(); }

    internal static int SelectionStarted()
    {
        if (_write == null) return 0;
        try
        {
            if (_active && !_entered)
                Mark("selection-cancelled", _requested ? "selection-during-drop" : "selection-replaced", calls: 1);
            BeginAttempt(Stopwatch.GetTimestamp());
            _selectionPending = true;
            return _attempt;
        }
        catch (Exception error)
        {
            _active = _selectionPending = _selectionConfirmed = false;
            TimingFailed("selection-started", error);
            return 0;
        }
    }

    internal static void SelectionFinished(int generation, bool successful)
    {
        if (!_active || !_selectionPending || generation != _attempt) return;
        _selectionPending = false;
        _selectionConfirmed = successful;
        MarkBoundary(generation, successful ? "selection-confirmed" : "selection-failed",
            "RundownManager.MasterSelectActiveExpedition");
        if (!successful) _active = false;
    }

    internal static void RequestDrop()
    {
        if (_write == null || _requested && !_entered) return;
        var now = Stopwatch.GetTimestamp();
        if (!_active || !_selectionConfirmed || _entered)
        {
            if (_active && !_entered)
                MarkBoundary(_attempt, "selection-cancelled", "drop-request-before-selection-confirmed");
            BeginAttempt(now);
        }
        _requested = true; _selectionConfirmed = false;
        _requestReturned = _generating = _started = _done = _factoryDone = _landed = _entered = false;
        _dropRequestedAt = now;
        MarkBoundary(_attempt, "drop-requested", "CM_PageLoadout.EXT_StartExpedition");
    }

    private static void BeginAttempt(long startedAt)
    {
        _attempt++; _active = true;
        _selectionPending = _selectionConfirmed = _requested = _requestReturned = _generating = false;
        _started = _done = _factoryDone = _landed = _entered = false;
        Metrics.Reset();
        _requestedAt = _previousAt = startedAt; _dropRequestedAt = 0;
        _previousPhase = "attempt-start";
        _lastQuietAt = 0; _quietFrame = UnityEngine.Time.frameCount;
    }

    private static void State(eGameStateName previous, eGameStateName next)
    {
        // Remote clients have no local drop-button call. Do not pretend this is
        // a locally measured request: its origin remains explicit in the record.
        if (next == eGameStateName.Generating && !_requested)
        {
            var now = Stopwatch.GetTimestamp();
            if (!_active) BeginAttempt(now);
            _requested = true; _selectionPending = _selectionConfirmed = false;
            _dropRequestedAt = now;
            MarkBoundary(_attempt, "drop-requested", "state-Generating");
        }
        if (next == eGameStateName.Generating && _requested && !_generating)
        { _generating = true; MarkBoundary(_attempt, "state-generating", "EventAPI.OnGameStateChanged"); }
        if (next == eGameStateName.ReadyToStartLevel && _requested && !_landed)
        { _landed = true; MarkBoundary(_attempt, "landing", "state-ReadyToStartLevel"); }
        if (_requested && !_entered && (next is eGameStateName.NoLobby or eGameStateName.Lobby) && previous != next)
            Cleanup();
        else if (_active && !_requested && next == eGameStateName.NoLobby && previous != next)
            Cleanup();
    }

    private static void BuildStart() => GenerationStart("Builder.Build");
    private static void FactoryStart() => GenerationStart("LG_Factory.Setup");
    private static void GenerationStart(string origin)
    {
        if (_write == null || _started) return;
        // An externally driven build can occur without a local drop request.
        if (!_requested)
        {
            var now = Stopwatch.GetTimestamp();
            if (!_active) BeginAttempt(now);
            _requested = true; _selectionPending = _selectionConfirmed = false;
            _dropRequestedAt = now;
        }
        _started = true;
        _startFrame = UnityEngine.Time.frameCount;
        Metrics.Start(Stopwatch.GetTimestamp());
        MarkBoundary(_attempt, "generation-start", origin);
    }

    internal static void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        if (Metrics.Active) Metrics.Frame(now);
        var frameGap = _lastTickAt == 0 ? 0 : (now - _lastTickAt) * 1000.0 / Stopwatch.Frequency;
        _lastTickAt = now;
        if (_write == null || !_active || !_requested || _entered) return;  // an idle lobby between a selection and the drop button is not a wait
        try
        {
            if (frameGap >= StallSeconds * 1000)
                _write("drop.stall phase=" + _previousPhase + " generation=" + _attempt.ToString(CultureInfo.InvariantCulture) + " frameGapMs=" + Number(frameGap)
                    + " sincePhaseMs=" + Number((now - _previousAt) * 1000.0 / Stopwatch.Frequency) + " gameState=" + GameStateManager.CurrentStateName);
            var since = Math.Max(_previousAt, _lastQuietAt);
            if (now - since < QuietSeconds * Stopwatch.Frequency) return;
            var frames = UnityEngine.Time.frameCount - _quietFrame;
            _lastQuietAt = now; _quietFrame = UnityEngine.Time.frameCount;
            _write("drop.wait phase=" + _previousPhase + " generation=" + _attempt.ToString(CultureInfo.InvariantCulture)
                + " sincePhaseMs=" + Number((now - _previousAt) * 1000.0 / Stopwatch.Frequency) + " frames=" + frames.ToString(CultureInfo.InvariantCulture)
                + " gameState=" + GameStateManager.CurrentStateName + " elapsedMs=" + Number((now - _requestedAt) * 1000.0 / Stopwatch.Frequency));
        }
        catch (Exception error) { TimingFailed("wait", error); }
    }

    private static void BuildDone()
    {
        if (!_started || _done) return;
        _done = true; Metrics.Finish(Stopwatch.GetTimestamp());
        MarkBoundary(_attempt, "generation-done", "Builder.BuildDone", summary: true);
    }

    private static void FactoryDone()
    {
        if (!_started || _factoryDone) return;
        _factoryDone = true; MarkBoundary(_attempt, "factory-done", "LG_Factory.FactoryDone");
    }

    private static void InLevel()
    {
        if (!_requested || _entered) return;
        _entered = true; MarkBoundary(_attempt, "in-level", "LevelAPI.OnEnterLevel");
    }

    private static void Cleanup()
    {
        Metrics.Finish(Stopwatch.GetTimestamp());
        if (_requested && !_entered) MarkBoundary(_attempt, "aborted", "LevelAPI.OnLevelCleanup", summary: true);
        else if (_active && !_entered) MarkBoundary(_attempt, "selection-cancelled", "LevelAPI.OnLevelCleanup");
        _active = _selectionPending = _selectionConfirmed = _requested = false;
        _requestReturned = _generating = _started = false;
    }

    internal static void MarkBoundary(int generation, string phase, string origin, int calls = 1, bool summary = false)
    {
        if (_write == null || !_active || generation != _attempt || calls < 1) return;
        try { Mark(phase, origin, summary, calls); }
        catch (Exception error) { TimingFailed(phase, error); }
    }

    private static void TimingFailed(string phase, Exception error)
    {
        try { _write?.Invoke("drop.timing-failed phase=" + phase + " reason=" + error); }
        catch (Exception reporter) { Console.Error.WriteLine("drop.timing-failed phase=" + phase + " reason=" + error + " reporter=" + reporter.GetType().Name); }
    }

    private static string Number(double value) => double.IsFinite(value) ? value.ToString("F3", CultureInfo.InvariantCulture) : "unavailable";
    private static void Mark(string phase, string origin, bool summary = false, int calls = 1)
    {
        var now = Stopwatch.GetTimestamp();
        var expedition = RundownManager.GetActiveExpeditionData();
        var identity = expedition == null ? "unavailable" : Globals.Global.RundownIdToLoad.ToString(CultureInfo.InvariantCulture)
            + "/" + ((int)expedition.tier).ToString(CultureInfo.InvariantCulture)
            + "/" + expedition.expeditionIndex.ToString(CultureInfo.InvariantCulture);
        var worldEpoch = _worldEpoch?.Invoke() ?? "unavailable";
        var line = "drop.phase phase=" + phase + " utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            + " generation=" + _attempt.ToString(CultureInfo.InvariantCulture) + " pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + " expedition=" + identity + " worldEpoch=" + worldEpoch
            + " seed=" + (_started ? Builder.BuildSeed.ToString(CultureInfo.InvariantCulture) : "unavailable")
            + " hostIdSeed=" + (_started ? Builder.HostIDSeedRandom?.Seed.ToString(CultureInfo.InvariantCulture) ?? "unavailable" : "unavailable")
            + " sessionSeed=" + (_started ? Builder.SessionSeedRandom?.Seed.ToString(CultureInfo.InvariantCulture) ?? "unavailable" : "unavailable")
            + " elapsedMs=" + Number((now - _requestedAt) * 1000.0 / Stopwatch.Frequency)
            + " requestElapsedMs=" + (_dropRequestedAt == 0 ? "unavailable" : Number((now - _dropRequestedAt) * 1000.0 / Stopwatch.Frequency))
            + " stageMs=" + Number((now - _previousAt) * 1000.0 / Stopwatch.Frequency)
            + " selfMs=" + Number((now - _previousAt) * 1000.0 / Stopwatch.Frequency)
            + " calls=" + calls.ToString(CultureInfo.InvariantCulture)
            + " fromPhase=" + _previousPhase + " origin=" + origin;
        if (summary)
        {
            var frames = _started ? Math.Max(1, UnityEngine.Time.frameCount - _startFrame + 1) : 0;
            line += " jobs=" + Metrics.Jobs.ToString(CultureInfo.InvariantCulture)
            + " jobsMeaning=successful-factory-dequeues frames=" + frames.ToString(CultureInfo.InvariantCulture)
            + " frameSamples=" + Metrics.Frames.ToString(CultureInfo.InvariantCulture)
            + " frameMeanMs=" + Number(Metrics.MeanFrameMs) + " frameMaxMs=" + Number(Metrics.MaxFrameMs)
            + " jobsPerFrame=" + Number(frames == 0 ? double.NaN : (double)Metrics.Jobs / frames)
            + " frameClock=stopwatch-update-intervals-with-final-partial";
        }
        _previousAt = now; _previousPhase = phase;
        _write?.Invoke(line);
    }
}
