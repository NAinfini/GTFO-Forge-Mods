using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgePerformance.Crowd;

/// <summary>What the probe times or counts. Names are the log keys.</summary>
internal enum ProbePoint
{
    EnemySpawned, OwnSetupPrefix, RagdollOn, EnemyDestroyed,
    WakePropagation, ReachableQuery, NavAgentOn, NavAgentOff,
    LightVisible, ShadowRedraw, TransformSync, TransformSyncSkipped, DamageBaseUpdate,
    ProjectileUpdate, ProjectileSpawn,
    // Where the main thread goes: the game-state update (the enemy update manager runs inside it), its per-enemy groups, the
    // per-enemy components Unity updates itself, the local player and camera.
    GameStateUpdate, GameStateFixed, EnemyBehaviourGroup, EnemyDetectionGroup, EnemyLocomotionGroup, EnemyLocomotionFixedGroup,
    EnemyNetworkGroup, EnemyNodeGroup, PathMoveUpdate, MaterialFeeder, MovingCuller, TentacleUpdate, TentacleLate,
    EnemyTentacleLate, IkLate, PlayerEnemyCollision, CameraLate, CameraPreCull, LocalPlayerUpdate,
    LocalPlayerLate, CurvyLate, ShadowAtlasLate, FrameLimiterLate, IndirectBatcherLate, ControlRigLate, AudioLate,
    MapDetailsLate,
    // This mod's own per-frame hooks on instanced decoration and wind, and the game's instanced-render compute update
    // (timed only while IrfPhysicsHalfRate's hook is installed).
    OwnIrfLateUpdate, OwnIrfDraw, OwnWind, IrfRendererUpdate,
    // Dissonance's microphone restart: calls that ran (with their time) and calls VoiceRestartGuard held back or VoiceFrameSkipFilter ignored.
    VoiceRestart, VoiceRestartHeldBack, VoiceFrameSkipIgnored,
    // ShadowUtility.InjectDrawCommands (with the material), the call ShadowProxyHold hooks.
    ShadowInject, Count
}

/// <summary>Developer measurement (the <c>Probe</c> option): per-call time and count of the probed game methods, frame time, and
/// main- and render-thread CPU time, written as one line per window to <c>BepInEx/PerformanceLogs/ForgePerformance_*.log</c>.
/// Every probed method runs on the Unity main thread, so the counters are plain fields. A call adds two timestamps and three
/// array writes; nothing allocates or looks anything up by name on a probed call. Formatting happens once per window.</summary>
internal static class Probe
{
    internal const int RetainedSessions = 10;
    internal const string PruneFailedCode = "perf.probe-log-prune-failed";
    internal static bool On { get; private set; }
    private static readonly int Points = (int)ProbePoint.Count;
    private static readonly long[] Ticks = new long[Points], Calls = new long[Points], Max = new long[Points],
        FrameCalls = new long[Points], PeakPerFrame = new long[Points];
    // Sum of the enemies in every GetReachableEnemiesInNodes result: the enemies PropagateTargetLimited walks.
    internal static long ReachableEnemies;
    // Gauges the crowd tick sets every 0.25 s.
    internal static int Enemies, FarSkins, ThrottledSyncs;
    internal static int FrameIndex;
    // Whether this machine hosts, as the crowd tick last read it (in a level).
    internal static bool HostNow;
    private static readonly double[] FrameMs = new double[4096];
    private static int _frames;
    private static long _last, _due, _windowStart, _sessionStart, _windowTicks;
    private static double _windowMax;
    private static long _mainCpu, _renderCpu;
    private static IntPtr _mainThread, _renderThread;
    private static long _renderSearchDue;
    private static int _gc0, _il2cppGc, _hitches;
    private static long _clrAllocated, _heapLast, _heapGrowth;
    private static uint _renderThreadId;
    private const double HitchMs = 33;
    private const int MaxHitches = 20000;
    private static float _seconds;
    private static StreamWriter? _writer;
    private static string _label = "none";
    private static Func<string>? _switches;
    // Whether the game window has focus, sampled every frame: a background window can be throttled by the OS or the driver.
    private static IntPtr _window;
    private static long _windowSearchDue;
    private static int _foregroundFrames;

    internal static void Start(string bepInExRoot, float seconds, Func<string> switches, Action<string> error)
    {
        var directory = Path.Combine(bepInExRoot, "PerformanceLogs");
        Directory.CreateDirectory(directory);
        // An old log that cannot be deleted (open elsewhere, read-only) stays; how many stayed is logged once.
        var kept = 0; string? cause = null;
        foreach (var stale in new DirectoryInfo(directory).EnumerateFiles("ForgePerformance_*.log")
                     .OrderByDescending(file => file.LastWriteTimeUtc).Skip(RetainedSessions - 1))
            try { stale.Delete(); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { kept++; cause ??= stale.Name + ": " + failure.Message; }
        if (kept != 0) error("type=perf_probe_prune\tcode=" + PruneFailedCode + "\tkept=" + kept + "\tfirst=" + cause);
        var path = Path.Combine(directory, "ForgePerformance_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".log");
        _writer = new StreamWriter(path, false, new UTF8Encoding(false));
        _seconds = seconds; _switches = switches;
        _mainThread = OpenThread(ThreadQueryLimitedInformation, false, GetCurrentThreadId());
        _sessionStart = Stopwatch.GetTimestamp();
        _writer.WriteLine("type=perf_probe_start\tpath=" + path + "\twindow_s=" + seconds.ToString("0.###", CultureInfo.InvariantCulture)
            + "\tstopwatch_hz=" + Stopwatch.Frequency + "\tmain_thread=" + (_mainThread != IntPtr.Zero ? "found" : "unavailable"));
        _writer.Flush();
        ProbeNative.Start(Path.GetDirectoryName(typeof(Probe).Assembly.Location) ?? "", Path.ChangeExtension(path, null), Line);
        On = true;
    }

    internal static void Line(string line)
    {
        if (_writer == null) return;
        _writer.WriteLine(line); _writer.Flush();
    }

    /// <summary>Closes the open window now, so a window never spans a label or a switch change.</summary>
    internal static void Cut()
    {
        if (On && _frames > 0) Flush(Stopwatch.GetTimestamp());
    }

    internal static void Mark(string label)
    {
        if (!On) return;
        Cut();
        _label = string.IsNullOrWhiteSpace(label) ? "none" : label.Replace('\t', ' ');
        Line("type=perf_probe_mark\tt=" + Seconds(Stopwatch.GetTimestamp()) + "\tqpc=" + Stopwatch.GetTimestamp() + "\tutc=" + Utc() + "\tlabel=" + _label + "\tswitches=" + _switches!()
            + "\t" + FrameLimits());
    }

    /// <summary>The game's own frame limits and focus, read on demand (never per frame).</summary>
    internal static string FrameLimits()
        => "run_in_background=" + (Application.runInBackground ? 1 : 0) + "\tvsync=" + QualitySettings.vSyncCount
            + "\ttarget_fps=" + Application.targetFrameRate + "\twindow=" + (_window == IntPtr.Zero ? "missing" : "found")
            + "\tforeground=" + (_window != IntPtr.Zero && GetForegroundWindow() == _window ? 1 : 0);

    /// <summary>Asks Windows to bring the game window to the front; Windows may refuse when another process holds the input.</summary>
    internal static string Foreground()
    {
        if (_window == IntPtr.Zero) _window = GameWindow();
        if (_window == IntPtr.Zero) return "window=missing";
        var asked = SetForegroundWindow(_window);
        return "asked=" + (asked ? 1 : 0) + ";foreground=" + (GetForegroundWindow() == _window ? 1 : 0);
    }

    /// <summary>Hands the focus to the desktop shell so the game runs as a background window, without minimizing it.</summary>
    internal static string Background()
    {
        var asked = SetForegroundWindow(GetShellWindow());
        return "asked=" + (asked ? 1 : 0) + ";foreground=" + (_window != IntPtr.Zero && GetForegroundWindow() == _window ? 1 : 0);
    }

    // The visible top-level Unity window of this process.
    private static IntPtr GameWindow()
    {
        var self = (uint)Environment.ProcessId;
        var found = IntPtr.Zero;
        var name = new StringBuilder(64);
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != self || !IsWindowVisible(handle)) return true;
            name.Clear();
            GetClassName(handle, name, name.Capacity);
            if (name.ToString() != "UnityWndClass") return true;
            found = handle;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Begin() => On ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void End(ProbePoint point, long started)
    {
        if (started != 0) Add(point, Stopwatch.GetTimestamp() - started);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Add(ProbePoint point, long ticks)
    {
        var i = (int)point;
        Ticks[i] += ticks; Calls[i]++; FrameCalls[i]++;
        if (ticks > Max[i]) Max[i] = ticks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Count(ProbePoint point)
    {
        if (!On) return;
        var i = (int)point;
        Calls[i]++; FrameCalls[i]++;
    }

    /// <summary>Once per frame, from the plugin's Update: frame time between two Updates, per-frame peaks, and the window line.</summary>
    internal static void Frame()
    {
        if (!On) return;
        FrameIndex++;
        var now = Stopwatch.GetTimestamp();
        if (_last != 0)
        {
            var dt = now - _last;
            var ms = dt * 1000.0 / Stopwatch.Frequency;
            if (_frames < FrameMs.Length) FrameMs[_frames] = ms;
            _frames++; _windowTicks += dt;
            if (ms > _windowMax) _windowMax = ms;
            // A long frame, with the counter value the sampler stamps its stacks with: what the main thread did in it is read there.
            if (ms >= HitchMs && _hitches++ < MaxHitches)
                Line("type=perf_probe_hitch\tqpc=" + now + "\tutc=" + Utc() + "\tlabel=" + _label + "\tms=" + F(ms));
        }
        _last = now;
        ProbeNative.Frame(_renderThreadId, Line);
        var heap = IL2CPP.il2cpp_gc_get_used_size();
        if (heap > _heapLast) _heapGrowth += heap - _heapLast;
        _heapLast = heap;
        if (_window != IntPtr.Zero && GetForegroundWindow() == _window) _foregroundFrames++;
        for (var i = 0; i < Points; i++)
        {
            if (FrameCalls[i] > PeakPerFrame[i]) PeakPerFrame[i] = FrameCalls[i];
            FrameCalls[i] = 0;
        }
        if (_windowStart == 0) { _windowStart = now; _due = now + (long)(_seconds * Stopwatch.Frequency); ReadCpu(out _mainCpu, out _renderCpu); return; }
        if (now >= _due) Flush(now);
    }

    private static void Flush(long now)
    {
        ReadCpu(out var main, out var render);
        var frames = Math.Max(1, Math.Min(_frames, FrameMs.Length));
        Array.Sort(FrameMs, 0, frames);
        // Thread CPU time per frame comes in 100 ns FILETIME units: main near frame_ms means the main thread is the limit.
        var line = new StringBuilder(1024);
        line.Append("type=perf_probe\tt=").Append(Seconds(now)).Append("\tutc=").Append(Utc()).Append("\tlabel=").Append(_label)
            .Append("\thost=").Append(HostNow ? 1 : 0).Append("\tswitches=").Append(_switches!())
            .Append("\tframes=").Append(_frames)
            .Append("\tframe_ms_avg=").Append(F(_frames == 0 ? 0 : _windowTicks * 1000.0 / Stopwatch.Frequency / _frames))
            .Append("\tframe_ms_p50=").Append(F(FrameMs[frames / 2]))
            .Append("\tframe_ms_p95=").Append(F(FrameMs[Math.Min(frames - 1, (int)(frames * 0.95))]))
            .Append("\tframe_ms_max=").Append(F(_windowMax))
            .Append("\tmain_cpu_ms=").Append(main < 0 || _mainCpu < 0 ? "unavailable" : F((main - _mainCpu) / 10000.0 / Math.Max(1, _frames)))
            .Append("\trender_cpu_ms=").Append(render < 0 || _renderCpu < 0 ? "unavailable" : F((render - _renderCpu) / 10000.0 / Math.Max(1, _frames)))
            .Append("\tforeground_frames=").Append(_window == IntPtr.Zero ? "no-window" : _foregroundFrames.ToString(CultureInfo.InvariantCulture))
            .Append("\tqpc=").Append(now)
            .Append("\tgc0=").Append(GC.CollectionCount(0) - _gc0)
            .Append("\tclr_alloc_kb_frame=").Append(F((GC.GetTotalAllocatedBytes(false) - _clrAllocated) / 1024.0 / Math.Max(1, _frames)))
            .Append("\til2cpp_gc=").Append(Il2CppSystem.GC.GetCollectionCount(0) - _il2cppGc)
            .Append("\til2cpp_growth_kb_frame=").Append(F(_heapGrowth / 1024.0 / Math.Max(1, _frames)))
            .Append("\til2cpp_heap_mb=").Append(F(IL2CPP.il2cpp_gc_get_used_size() / 1048576.0))
            .Append("\tenemies=").Append(Enemies).Append("\tfar_skins=").Append(FarSkins)
            .Append("\tthrottled_syncs=").Append(ThrottledSyncs)
            .Append("\treachable_enemies=").Append(ReachableEnemies)
            .Append("\tjob_workers=").Append(ForgePerformance.LoadShedding.JobWorkers.Count());
        for (var i = 0; i < Points; i++)
        {
            if (Calls[i] == 0) continue;
            var name = ((ProbePoint)i).ToString();
            line.Append('\t').Append(name).Append("_n=").Append(Calls[i])
                .Append('\t').Append(name).Append("_peak=").Append(PeakPerFrame[i]);
            if (Ticks[i] != 0)
                line.Append('\t').Append(name).Append("_ms=").Append(F(Ticks[i] * 1000.0 / Stopwatch.Frequency))
                    .Append('\t').Append(name).Append("_max_ms=").Append(F(Max[i] * 1000.0 / Stopwatch.Frequency));
            Ticks[i] = Calls[i] = Max[i] = PeakPerFrame[i] = 0;
        }
        ProbeNative.Append(line, _frames);
        Line(line.ToString());
        _clrAllocated = GC.GetTotalAllocatedBytes(false); _il2cppGc = Il2CppSystem.GC.GetCollectionCount(0); _heapGrowth = 0;
        _frames = 0; _windowTicks = 0; _windowMax = 0; ReachableEnemies = 0; _foregroundFrames = 0;
        if (_window == IntPtr.Zero && now >= _windowSearchDue) { _windowSearchDue = now + 10 * Stopwatch.Frequency; _window = GameWindow(); }
        _mainCpu = main; _renderCpu = render; _gc0 = GC.CollectionCount(0);
        _windowStart = now; _due = now + (long)(_seconds * Stopwatch.Frequency);
    }

    private static string Seconds(long now) => F((now - _sessionStart) / (double)Stopwatch.Frequency);
    // Wall-clock time, to line the windows up with other logs of the same session (ForgeDevelopment's per-second lines).
    private static string Utc() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void ReadCpu(out long main, out long render)
    {
        main = Cpu(_mainThread);
        if (_renderThread == IntPtr.Zero && Stopwatch.GetTimestamp() >= _renderSearchDue)
        {
            _renderSearchDue = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
            _renderThread = FindThread("UnityGfxDeviceWorker");
            if (_renderThread != IntPtr.Zero)
            {
                _renderThreadId = GetThreadId(_renderThread);
                Line("type=perf_probe_thread\tname=UnityGfxDeviceWorker\tstate=found\tid=" + _renderThreadId);
            }
        }
        render = Cpu(_renderThread);
    }

    private static long Cpu(IntPtr thread)
        => thread != IntPtr.Zero && GetThreadTimes(thread, out _, out _, out var kernel, out var user) ? kernel + user : -1;

    // The render thread by its name (Windows 10 1607 and later); without it render_cpu_ms is reported unavailable.
    private static IntPtr FindThread(string name)
    {
        using var process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            var handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)thread.Id);
            if (handle == IntPtr.Zero) continue;
            try
            {
                if (GetThreadDescription(handle, out var text) >= 0 && text != IntPtr.Zero)
                {
                    var description = Marshal.PtrToStringUni(text);
                    LocalFree(text);
                    if (description == name) { var found = handle; handle = IntPtr.Zero; return found; }
                }
            }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
            finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
        }
        return IntPtr.Zero;
    }

    private const uint ThreadQueryLimitedInformation = 0x0800;
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint GetThreadId(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
}
