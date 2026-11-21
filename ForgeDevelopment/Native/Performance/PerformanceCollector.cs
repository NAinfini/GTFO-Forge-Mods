using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using Il2CppInterop.Runtime;

namespace ForgeDevelopment.Native;

internal readonly record struct PerformanceOptions(float SummarySeconds, int ManualKey);

// What differs between the hosts that compile this collector; everything else is shared.
// ConfigureNativeTiming installs the host's native timing registration (null when the host already did).
// EnterFrame/ExitFrame bracket the fixed-size sampling body.
internal sealed record PerformanceBindings(Action? ConfigureNativeTiming = null, Action? EnterFrame = null, Action? ExitFrame = null);

internal sealed class PerformanceCollector
{
    // Session logs of one plugin prefix kept on disk, the new session included.
    internal const int RetainedSessions = 10;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private delegate bool ReadKey(int key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadInt();
    private readonly NativeFrameClock _clock = new();
    private readonly PerformanceSceneReader _scene = new();
    private readonly PerformanceFrameQueue _queue = new();
    private readonly CollectorMetrics _metrics = new();
    private readonly List<IDisposable> _hooks = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualLogSource _log;
    private readonly PerformanceOptions _options;
    private readonly PerformanceBindings _bindings;
    private readonly PerformanceTimings _timings = new();
    // Frozen, low-frequency settings and per-level presentation counters only.
    private readonly ConcurrentQueue<string> _presentation = new();
    internal void RecordPresentation(string line)
    {
        if (_presentation.Count >= 512) throw new InvalidOperationException("presentation_log_queue_full");
        _presentation.Enqueue(line);
        _wake.Set();
    }
    private void DrainPresentation()
    {
        while (_presentation.TryDequeue(out var line)) _writer!.Presentation(line);
    }
    private PerformanceLogWriter? _writer;
    private ForgeHookSampler? _forgeHooks;
    private Action<ForgeHookWindow>? _writeHooks;
    private int _lastState = -1;
    private Thread? _thread;
    private Semaphore? _lease;
    private ReadKey? _readKey;
    private volatile bool _running, _closing;
    private bool _started, _stopped, _finished;
    private Exception? _writerFailure;
    private long _sequence, _dropped, _allocated, _memoryDue, _used = -1, _heap = -1;
    private double _previousCollectorMs = double.NaN;
    // The writer is woken at most every 1/20 s (50 ms), half its own idle wait, instead of once per frame.
    private const long WakeIntervalDivisor = 20;
    private long _wakeDue;
    private bool _nativeMemory;
    internal bool Running => _running;
    internal PerformanceCollector(ManualLogSource log, PerformanceOptions options, PerformanceBindings? bindings = null)
    { _log = log; _options = options; _bindings = bindings ?? new(); }
    internal bool Start(string root, string host, string version)
    {
        if (_started || _finished) throw new InvalidOperationException("collector_start_is_single_attempt");
        _started = true;
        // Assemblies sharing source have separate statics. A process semaphore
        // also prevents two installed plugins from detouring the same functions.
        _lease = new Semaphore(1, 1, "Local\\GTFO.ForgePerformance." + Environment.ProcessId);
        if (!_lease.WaitOne(0)) { _lease.Dispose(); _lease = null; _log.LogWarning("performance_unavailable: native_timing_entry_already_owned_by_another_collector"); return false; }
        try
        {
            var directory = Path.Combine(root, "PerformanceLogs"); Directory.CreateDirectory(directory);
            // The new session's file takes the last slot.
            PruneSessions(directory, host + "_", RetainedSessions - 1, _log);
            var path = Path.Combine(directory, host + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + ".log");
            _bindings.ConfigureNativeTiming?.Invoke();
            _scene.Initialize();
            ThreadCpuClock.Initialize();
            _nativeMemory = NativeModuleFingerprint.Verified("GameAssembly.dll", NativeModuleFingerprint.GameSha256, out var memoryReason) != IntPtr.Zero;
            var gameReason = NativeGameTimings.Install(_timings, _hooks, _scene.AlarmFieldsVerified);
            NativePlayerLoop.Install(_timings, _hooks);
            var waitReason = NativePresentWait.Start(_timings);
            string keyReason;
            try { _readKey = IL2CPP.ResolveICall<ReadKey>("UnityEngine.Input::GetKeyDownInt"); keyReason = "none"; }
            catch (Exception error) { keyReason = "key_unavailable:" + error.GetBaseException().Message; }
            _writer = new PerformanceLogWriter(path, _options, _timings, _metrics);
            int? vsync = null, targetFps = null; var limitsReason = "none";
            try
            {
                vsync = IL2CPP.ResolveICall<ReadInt>("UnityEngine.QualitySettings::get_vSyncCount")();
                targetFps = IL2CPP.ResolveICall<ReadInt>("UnityEngine.Application::get_targetFrameRate")();
            }
            catch (Exception error) { limitsReason = "frame_limits_unavailable:" + error.GetBaseException().Message; }
            _writer.Start(host, version, _scene.Reason, gameReason, waitReason, memoryReason, keyReason, vsync, targetFps, limitsReason);
            DrainPresentation();
            _forgeHooks = ForgeHookSampler.Start(_options.SummarySeconds);
            _writer.TrackForgeHooks(_forgeHooks);
            if (_forgeHooks != null) _writeHooks = window => _writer.ForgeHooks(window, _forgeHooks);
            _clock.Reset(); _allocated = GC.GetAllocatedBytesForCurrentThread();
            _running = true;
            _thread = new Thread(WriteLoop) { IsBackground = true, Name = "GTFO performance log" }; _thread.Start();
            _log.LogInfo("Performance collection started: " + path);
            return true;
        }
        catch { Stop(); throw; }
    }

    // Keeps the newest `keep` ordinary files of this plugin's prefix; links, directories and other prefixes are left alone.
    internal static void PruneSessions(string directory, string prefix, int keep, ManualLogSource log)
    {
        var stale = new DirectoryInfo(directory).EnumerateFiles(prefix + "*.log")
            .Where(file => (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (var file in stale)
            try { file.Delete(); }
            catch (Exception error) { log.LogWarning("performance_log_retention_failed: " + file.Name + ": " + error.GetBaseException().Message); }
    }

    // Entire production frame path: clocks, primitive field reads, fixed-index
    // counters, queue copy and a kernel wake. All formatting is in WriteLoop.
    internal void Tick()
    {
        if (!_running) return;
        var started = Stopwatch.GetTimestamp();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        bool queued;
        _bindings.EnterFrame?.Invoke();
        try
        {
            var frame = new PerformanceFrame
            {
                Sequence = ++_sequence,
                Timestamp = started,
                Clock = _clock.Read(),
                Scene = _scene.Read(),
                CollectorMilliseconds = _previousCollectorMs,
                DroppedBefore = _dropped
            };
            if (_readKey != null && _readKey(_options.ManualKey)) _timings.Mark(PerformanceMark.Manual);
            _timings.Take(ref frame.Timings, ref frame.TimingCalls);
            frame.Marks = _timings.TakeMarks(out frame.SpawnGroups, out frame.Waves);
            _timings.TakeObserverCost(out frame.ObserverTicks, out frame.ObserverCalls);
            // Low-frequency reads of native heap sizes allocate no managed wrappers.
            // Native GC collection timing is only the explicitly observed entry.
            if (_nativeMemory && started >= _memoryDue)
            {
                _used = IL2CPP.il2cpp_gc_get_used_size(); _heap = IL2CPP.il2cpp_gc_get_heap_size();
                _memoryDue = started + Stopwatch.Frequency;
            }
            frame.Il2CppUsed = _used; frame.Il2CppHeap = _heap;
            frame.NetGc0 = GC.CollectionCount(0); frame.NetGc1 = GC.CollectionCount(1); frame.NetGc2 = GC.CollectionCount(2);
            var allocated = GC.GetAllocatedBytesForCurrentThread(); frame.MainAllocatedBytes = allocated - _allocated; _allocated = allocated;
            _lastState = frame.Scene.State;
            queued = _queue.TryWrite(in frame);
            // The writer wakes itself every 100 ms; a kernel event per frame is only for a queue that is filling up.
            if (queued) { if (started >= _wakeDue) { _wake.Set(); _wakeDue = started + Stopwatch.Frequency / WakeIntervalDivisor; } }
            else { _dropped++; _wake.Set(); }
        }
        finally { _bindings.ExitFrame?.Invoke(); }
        // Close the frame scope before Capture resets its accounting window.
        _forgeHooks?.Capture(started, _lastState, _sequence);
        var collectorAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        var collectorTicks = Stopwatch.GetTimestamp() - started;
        _previousCollectorMs = collectorTicks * (1000.0 / Stopwatch.Frequency);
        _metrics.Record(collectorTicks, collectorAllocated, !queued);
    }
    private void WriteLoop()
    {
        try
        {
            while (!_closing)
            {
                while (_queue.TryRead(out var frame)) _writer!.Frame(in frame);
                if (_writeHooks != null) _forgeHooks!.Drain(_writeHooks);
                DrainPresentation();
                _wake.WaitOne(100);
            }
            while (_queue.TryRead(out var frame)) _writer!.Frame(in frame);
            if (_writeHooks != null) _forgeHooks!.Drain(_writeHooks);
            DrainPresentation();
            _writer!.End(_dropped);
        }
        catch (Exception error) { _writerFailure = error; _running = false; _forgeHooks?.Dispose(); _log.LogError("Performance log failed: " + error); }
        finally
        {
            try { _writer?.Dispose(); }
            catch (Exception error)
            {
                _writerFailure ??= error; _running = false;
                _log.LogError("Performance log close failed: " + error);
            }
        }
    }
    // Only the Unity/main-thread producer calls this. The writer drains a finite
    // queue before joining; no timeout can abandon a half-written final line.
    // This is safe inside a native timing callback because it does not undo it.
    internal void FinishSession()
    {
        if (_finished)
        {
            if (_writerFailure != null) throw new InvalidOperationException("performance_writer_shutdown_failed", _writerFailure);
            return;
        }
        try { _forgeHooks?.Capture(Stopwatch.GetTimestamp(), _lastState, _sequence, force: true); }
        catch (Exception error) { _writerFailure ??= error; }
        finally { _forgeHooks?.Dispose(); }
        _running = false; _closing = true; _wake.Set();
        if (_thread != null) { _thread.Join(); _thread = null; }
        else if (_writer != null)
        {
            try { _writer.End(_dropped); }
            catch (Exception error) { _writerFailure = error; }
            finally
            {
                try { _writer.Dispose(); }
                catch (Exception error) { _writerFailure ??= error; _log.LogError("Performance log close failed: " + error); }
            }
        }
        _finished = true;
        if (_writerFailure != null) throw new InvalidOperationException("performance_writer_shutdown_failed", _writerFailure);
    }
    internal void Stop()
    {
        if (_stopped) return;
        var failures = new List<Exception>();
        // A failed writer thread does not leave a hook running, so it must not keep the lease; it is thrown after the release.
        Exception? writerFailure = null;
        try { FinishSession(); } catch (Exception error) { writerFailure = error; }
        if (_lease == null && _writer == null && _hooks.Count == 0 && _thread == null)
        { _wake.Dispose(); _stopped = true; return; }
        // Keep callback objects rooted and the lease held if any hook cannot
        // drain; never destroy a trampoline still executing an original call.
        try { NativeGameTimings.StopMarkers(); } catch (Exception error) { failures.Add(error); }
        for (var i = _hooks.Count - 1; i >= 0; i--)
            try { _hooks[i].Dispose(); } catch (Exception error) { failures.Add(error); }
        try { NativePresentWait.Stop(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("performance_shutdown_incomplete", failures);
        if (_lease != null) { _lease.Release(); _lease.Dispose(); _lease = null; }
        _wake.Dispose(); _stopped = true;
        if (writerFailure != null) throw writerFailure;
    }
}
