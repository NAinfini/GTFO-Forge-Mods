using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ForgeDevelopment.Native;

// Owns every histogram, format buffer and filesystem operation after startup.
// Frame processing is independent of Unity and can be exercised offline.
internal sealed partial class PerformanceLogWriter : IDisposable
{
    internal const string Schema = "forgeperformance-2";
    internal const int DetailCapacity = 2048;
    internal const int MarkerWindowSeconds = 3;
    internal static readonly string[] Names =
    {
        "physics", "physics_late", "animation_begin", "animation_end", "behaviour_update",
        "behaviour_fixed", "behaviour_late", "renderers", "render_submission", "navigation",
        "enemy_update", "enemy_fixed", "enemy_master", "enemy_master_fixed", "gc_explicit",
        "initialization", "early_update", "pre_update_other", "post_late_update_other"
    };
    private static readonly string[] FrameFields = MakeFrameFields();
    private static readonly string[] CallFields = MakeFrameFields("_calls");
    private static string[] MakeFrameFields(string suffix = "_ms")
    {
        var fields = new string[Names.Length];
        for (var i = 0; i < fields.Length; i++) fields[i] = Names[i] + suffix;
        return fields;
    }
    private readonly StreamWriter _stream;
    private readonly CollectorMetrics? _collectorMetrics;
    private readonly StringBuilder _line = new(8192);
    private readonly PerformanceOptions _options;
    private readonly PerformanceHitchDetector _hitch = new();
    private readonly PerformanceHistogram _thresholds = new(histogram: false);
    private readonly PerformanceTimings _timings;
    private readonly PerformanceHistogram _frames = new(), _cpu = new(), _wait = new(), _other = new();
    private readonly PerformanceHistogram _collector = new(100), _observer = new(100), _allocated = new(histogram: false);
    private readonly PerformanceHistogram _used = new(histogram: false), _heap = new(histogram: false);
    private readonly PerformanceHistogram[] _phases = MakeHistograms(Names.Length);
    private readonly long[] _calls = new long[Names.Length], _marks = new long[6];
    private static readonly string[] SceneFields = { "area_uid", "area_size", "node_registered_enemies", "close_enemies", "aggressive_enemies",
        "fog_density", "fog_altitude", "player_y", "fog_relative_height" };
    private readonly PerformanceHistogram[] _scene = MakeHistograms(SceneFields.Length, scene: true);
    private readonly DetailFrame[] _recent = new DetailFrame[DetailCapacity];
    private int _head, _recentCount;
    private long _detailUntil = long.MinValue, _historyEvicted, _received, _complete, _hitches, _over33, _over50, _over100,
        _markerFrames, _spawnGroups, _waves, _presentCalls, _observerCalls, _dropped;
    private long _windowFirst;
    private double _completeWall, _completeCpu, _completeWait;
    private int _gc0First, _gc1First, _gc2First, _gc0Last, _gc1Last, _gc2Last;
    private struct DetailFrame { internal PerformanceFrame Frame; internal double HitchThreshold; internal int Attempt; internal bool Written; }
    private static PerformanceHistogram[] MakeHistograms(int count, bool scene = false)
    {
        var result = new PerformanceHistogram[count];
        for (var i = 0; i < count; i++) result[i] = new PerformanceHistogram(i == 5 && scene ? 100 : 1, !scene || i == 5 || i == 8);
        return result;
    }
    private readonly DateTime _epoch = DateTime.UtcNow;
    private readonly long _origin = Stopwatch.GetTimestamp();
    /// <summary>What every Forge package has sent since the process started (ForgeCore Net.Traffic), set by the plugin. Each
    /// frame_summary divides the change since the previous reading by the time between them.</summary>
    internal static Func<(long Messages, long Bytes, long LossyMessages, long LossyBytes)>? Traffic { get; set; }
    private (long Messages, long Bytes, long LossyMessages, long LossyBytes) _net;
    private long _netAt;
    private long _windowStart, _lastSequence, _lastTimestamp;
    private int _state = -2, _attempt;
    private bool _ended, _truncated, _closing;
    private long _bytes;
    private readonly long _maximumBytes;
    internal const long DefaultMaximumLogBytes = 64L << 20;
    internal void Presentation(string line)
    {
        _line.Clear(); _line.Append(Timestamp(Stopwatch.GetTimestamp())).Append('\t').Append(line); EndLine();
    }
    internal PerformanceLogWriter(string path, PerformanceOptions options, PerformanceTimings timings, CollectorMetrics? collectorMetrics = null,
        long maximumBytes = DefaultMaximumLogBytes)
    {
        _options = options; _timings = timings; _collectorMetrics = collectorMetrics; _maximumBytes = maximumBytes;
        _stream = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536), new UTF8Encoding(false), 65536);
    }
    internal void Start(string host, string version, string sceneReason, string gameReason, string waitReason, string memoryReason, string keyReason,
        int? vsync = null, int? targetFps = null, string limitsReason = "not_read_by_offline_writer_test")
    {
        Begin("session", _origin);
        Text("plugin", host); Text("plugin_version", version); Text("measurement_schema", Schema);
        Text("histogram_schema", "signed-upper-delta-1");
        Text("histogram_bounds", "0_exact;.01_through_1;.1_through_128;1_through_2048;then_powers_of_two;scale_divides_bounds");
        Text("hitch_rule", "max_floor_and_previous_frame_median;state_change_resets_history;current_frame_excluded");
        Number("hitch_floor_ms", PerformanceHitchDetector.FloorMilliseconds);
        Number("hitch_median_multiplier", PerformanceHitchDetector.MedianMultiplier);
        Number("hitch_history_frames", PerformanceHitchDetector.HistoryFrames);
        Number("marker_window_seconds", MarkerWindowSeconds);
        Number("marker_history_capacity", DetailCapacity);
        Text("frame_detail_policy", "hitch_or_mark_or_marker_window;overlapping_windows_union;no_duplicate_frames");
        Text("player_sha256_required", NativePresentWait.PlayerSha256); Text("game_sha256_required", NativeModuleFingerprint.GameSha256);
        Text("scene_fields", sceneReason); Text("game_entries", gameReason); Text("present_wait", waitReason);
        Text("native_memory", memoryReason); Text("manual_key", keyReason);
        if (vsync is { } sync) Number("vsync", sync); else Text("vsync", "unavailable");
        if (targetFps is { } limit) Number("target_fps", limit); else Text("target_fps", "unavailable");
        Text("frame_limits_reason", limitsReason);
        Text("cpu_source", ThreadCpuClock.Source); Text("cpu_clock_status", ThreadCpuClock.Reason);
        Number("cpu_cycles_per_second", ThreadCpuClock.CyclesPerSecond); Number("cpu_resolution_ns", ThreadCpuClock.ResolutionNanoseconds);
        Number("cpu_calibration_relative_spread", ThreadCpuClock.CalibrationSpread);
        Text("present_wait_source", NativePresentWait.Source);
        Text("marker_entries", NativeGameTimings.MarkerEntries);
        Text("marker_unavailable", NativeGameTimings.MarkerUnavailable);
        Text("observer_cost_coverage", "raw_timing_callbacks_only;Harmony_marker_dispatch_boxing_and_native_managed_boundaries_not_included");
        Text("collector_cost_coverage", "Tick_wall_including_clock_scene_counters_queue_and_wake;excludes_metrics_publication_and_native_managed_boundaries;previous_frame_histogram_is_shifted");
        Text("collector_allocation_coverage", "CLR_current_thread_bytes_inside_Tick;excludes_native_allocations_other_callbacks_writer_and_startup;main_managed_allocated_bytes_is_entire_thread_not_collector");
        Text("collector_summary_scope", "session_cumulative_includes_queue_drops;use_last_snapshot_not_sum;writer_bytes_are_UTF8_serialized_prefix_not_durable_file_size");
        Text("phase_source", "verified_native_PlayerLoop_function_slots_and_dump_verified_IL2CPP_methods;main_thread_wall_spans");
        Text("phase_overlap", "inclusive;Behaviour_contains_enemy_and_spawn;render_submission_can_contain_present_wait;do_not_sum_with_frame_partition");
        Text("phase_alignment", "completed_spans_since_previous_Update;outer_Behaviour_span_crosses_Update_boundary;adjacent_frame_phase_shift_possible");
        Text("aggregate_phase_source", "sum_of_observed_leaf_original_calls;no_parent_or_dispatch_timing;pre_update_excludes_AIUpdate;post_late_update_excludes_UpdateAllRenderers_and_FinishFrameRendering");
        Text("shutdown_entry", "UnityEngine.Application.add_quitting;Internal_ApplicationQuit_RVA_0x1DD0500;queue_drain_and_durable_flush_before_return");
        Text("wave_marker_coverage", NativeGameTimings.MarkerCoverage);
        Text("gpu_execution_ms", "unavailable"); Text("gpu_execution_reason", "FrameTiming_native_type_stripped");
        Text("render_thread_ms", "unavailable"); Text("render_thread_reason", "no_verified_render_thread_frame_boundary");
        Text("gc_total_ms", "unavailable"); Text("gc_total_reason", "automatic_incremental_GC_entry_not_verified;gc_explicit_is_only_explicit_collection");
        Text("total_enemies", "unavailable"); Text("total_enemies_reason", "no_low_cost_verified_total;node_and_game_summary_counts_have_limited_coverage");
        Text("active_rigidbodies", "unavailable"); Text("ragdolls", "unavailable"); Text("visible_renderers", "unavailable");
        Text("scene_count_reason", "no_verified_constant_cost_read;no_global_component_enumeration");
        Text("forge_hooks", "optional_P1_counters;forge_hook_and_forge_package_rows;main_thread_only;nested_self_cost;managed_bytes_only");
        EndLine();
        for (var i = 0; i < _timings.Points.Length; i++)
        {
            var point = _timings.Points[i]; Begin("timing_point", _origin);
            Text("point", Names[i]); Text("status", point.Available ? "available" : "unavailable");
            Text("entry", point.Entry); Text("reason", point.Reason);
            Text("coverage", point.Coverage); Text("missing_entries", point.MissingEntries); EndLine();
        }
        _stream.Flush();
    }
    internal void Frame(in PerformanceFrame frame)
    {
        if (_state != frame.Scene.State)
        {
            Summary(); _hitch.Reset(); _state = frame.Scene.State; _windowStart = frame.Timestamp;
            if (_state == 10) _attempt++;
            Begin("game_state", frame.Timestamp); Text("state", State(_state)); Number("attempt", _attempt); Number("frame", frame.Sequence); EndLine();
            Begin(_state == 10 ? "level_enter" : "level_state", frame.Timestamp); Text("state", State(_state)); Number("attempt", _attempt); EndLine();
        }
        _lastSequence = frame.Sequence; _lastTimestamp = frame.Timestamp;
        var threshold = _hitch.Observe(frame.Clock.FrameMilliseconds);
        var hitch = double.IsFinite(frame.Clock.FrameMilliseconds) && frame.Clock.FrameMilliseconds >= threshold;
        Collect(in frame, hitch, threshold);
        Detail(in frame, hitch, threshold);
        if (frame.Marks != PerformanceMark.None)
        {
            Begin("marker", frame.Timestamp); Text("state", State(_state)); Number("frame", frame.Sequence); Number("attempt", _attempt);
            Number("marks", (int)frame.Marks); Number("spawn_groups", frame.SpawnGroups); Number("wave_starts", frame.Waves);
            Number("history_evicted_frames", _historyEvicted); EndLine();
        }
        if (hitch)
        {
            Begin("hitch", frame.Timestamp); Text("state", State(_state)); Number("attempt", _attempt); Number("frame", frame.Sequence);
            Number("frame_ms", frame.Clock.FrameMilliseconds); Number("hitch_threshold_ms", threshold);
            Text("detail", "see_matching_frame_sample"); EndLine();
        }
        if ((frame.Timestamp - _windowStart) >= _options.SummarySeconds * Stopwatch.Frequency || (frame.Marks & PerformanceMark.Manual) != 0)
        { Summary(); _windowStart = frame.Timestamp; _stream.Flush(); }
    }
    private void Detail(in PerformanceFrame frame, bool hitch, double threshold)
    {
        var earliest = frame.Timestamp - MarkerWindowSeconds * Stopwatch.Frequency;
        while (_recentCount > 0 && _recent[_head].Frame.Timestamp < earliest)
        { _head = (_head + 1) % DetailCapacity; _recentCount--; }
        if (_recentCount == DetailCapacity)
        { _historyEvicted++; _head = (_head + 1) % DetailCapacity; _recentCount--; }
        var at = (_head + _recentCount++) % DetailCapacity;
        _recent[at] = new DetailFrame { Frame = frame, HitchThreshold = threshold, Attempt = _attempt };
        if (frame.Marks != PerformanceMark.None)
        {
            for (var i = 0; i < _recentCount; i++)
            {
                ref var saved = ref _recent[(_head + i) % DetailCapacity];
                if (saved.Written) continue;
                WriteSample(in saved.Frame, saved.Attempt, "marker_before", saved.HitchThreshold); saved.Written = true;
            }
            _detailUntil = frame.Timestamp + MarkerWindowSeconds * Stopwatch.Frequency;
        }
        if (!_recent[at].Written && (hitch || frame.Timestamp < _detailUntil))
        { WriteSample(in frame, _attempt, hitch ? "hitch" : "marker_after", threshold); _recent[at].Written = true; }
    }
    private void WriteSample(in PerformanceFrame frame, int attempt, string reason, double threshold)
    {
        Begin("frame_sample", frame.Timestamp); Text("measurement_schema", Schema); Text("state", State(frame.Scene.State));
        Text("detail_reason", reason); Number("attempt", attempt); Number("frame", frame.Sequence);
        Number("hitch_threshold_ms", threshold);
        if (frame.Clock.FrameMilliseconds > 0 && double.IsFinite(frame.Clock.FrameMilliseconds)) Number("frame_ms", frame.Clock.FrameMilliseconds);
        else { Text("frame_ms", "unavailable"); Text("frame_ms_reason", "no_completed_frame_interval"); }
        Timing("cpu_main_ms", frame.Clock.Cpu); Timing("present_wait_ms", frame.Clock.PresentWait);
        var covered = (frame.Clock.Cpu.Nanoseconds + frame.Clock.PresentWait.Nanoseconds) / 1_000_000.0;
        if (frame.Clock.Cpu.Available && frame.Clock.PresentWait.Available && covered <= frame.Clock.FrameMilliseconds)
        { Text("partition_status", "samples"); Number("other_ms", frame.Clock.FrameMilliseconds - covered); }
        else { Text("partition_status", "unavailable"); Text("other_ms", "unavailable"); Text("other_reason", "missing_clock_or_CPU_resolution_exceeds_wall"); }
        Number("present_calls", frame.Clock.WaitCalls);
        for (var i = 0; i < Names.Length; i++)
        {
            if (_timings.Points[i].Available)
            { Number(FrameFields[i], frame.Timings.Milliseconds((TimingPoint)i)); Number(CallFields[i], frame.TimingCalls.Get((TimingPoint)i)); }
            else { Text(FrameFields[i], "unavailable"); Text(CallFields[i], "unavailable"); Text(FrameFields[i] + "_reason", _timings.Points[i].Reason); }
        }
        Count("area_uid", frame.Scene.Area); Count("area_size", frame.Scene.AreaSize);
        Count("node_registered_enemies", frame.Scene.NodeEnemies); Count("close_enemies", frame.Scene.CloseEnemies); Count("aggressive_enemies", frame.Scene.AggressiveEnemies);
        Number("fog_density", frame.Scene.FogDensity); Number("fog_altitude", frame.Scene.FogAltitude);
        Number("player_y", frame.Scene.PlayerY); Number("fog_relative_height", frame.Scene.PlayerY - frame.Scene.FogAltitude);
        if (frame.Scene.Area < 0 || frame.Scene.AreaSize < 0 || frame.Scene.NodeEnemies < 0 || frame.Scene.CloseEnemies < 0 || frame.Scene.AggressiveEnemies < 0
            || !float.IsFinite(frame.Scene.FogDensity) || !float.IsFinite(frame.Scene.PlayerY) || !float.IsFinite(frame.Scene.FogAltitude))
            Text("scene_unavailable_reason", "field_layout_not_verified_or_local_object_not_present;see_session_scene_fields");
        Number("marks", (int)frame.Marks); Number("spawn_groups", frame.SpawnGroups); Number("wave_starts", frame.Waves);
        Count("il2cpp_used_bytes", frame.Il2CppUsed); Count("il2cpp_heap_bytes", frame.Il2CppHeap);
        if (frame.Il2CppUsed < 0 || frame.Il2CppHeap < 0) Text("native_memory_reason", "native_heap_not_verified;see_session_native_memory");
        Number("net_gc0", frame.NetGc0); Number("net_gc1", frame.NetGc1); Number("net_gc2", frame.NetGc2);
        Count("main_managed_allocated_bytes", frame.MainAllocatedBytes); Number("dropped_frames_before", frame.DroppedBefore);
        if (frame.MainAllocatedBytes < 0) Text("main_managed_allocated_bytes_reason", "managed_allocation_counter_reset");
        Number("collector_previous_frame_ms", frame.CollectorMilliseconds); Number("observer_ms", frame.ObserverTicks * (1000.0 / Stopwatch.Frequency));
        if (!double.IsFinite(frame.CollectorMilliseconds)) Text("collector_previous_frame_ms_reason", "previous_collector_interval_not_available");
        Number("observer_calls", frame.ObserverCalls); EndLine();
    }
    private void Collect(in PerformanceFrame frame, bool hitch, double threshold)
    {
        if (_received++ == 0)
        { _windowFirst = frame.Timestamp; _gc0First = frame.NetGc0; _gc1First = frame.NetGc1; _gc2First = frame.NetGc2; }
        _gc0Last = frame.NetGc0; _gc1Last = frame.NetGc1; _gc2Last = frame.NetGc2;
        var wall = frame.Clock.FrameMilliseconds;
        _frames.Add(wall > 0 ? wall : double.NaN, "no_completed_frame_interval");
        var cpu = frame.Clock.Cpu; var wait = frame.Clock.PresentWait;
        var main = cpu.Available && cpu.Nanoseconds >= 0 && cpu.Blocks > 0 ? cpu.Nanoseconds / 1_000_000.0 : double.NaN;
        var present = wait.Available && wait.Nanoseconds >= 0 && (wait.Blocks > 0 || wait.Nanoseconds == 0) ? wait.Nanoseconds / 1_000_000.0 : double.NaN;
        _cpu.Add(main, cpu.Available ? "invalid_cpu_counter" : cpu.Reason);
        _wait.Add(present, wait.Available ? "invalid_wait_counter" : wait.Reason);
        if (double.IsFinite(main) && double.IsFinite(present) && wall > 0 && main + present <= wall)
        { _complete++; _completeWall += wall; _completeCpu += main; _completeWait += present; _other.Add(wall - main - present); }
        else _other.Add(double.NaN, "missing_clock_or_CPU_resolution_exceeds_wall");
        for (var i = 0; i < Names.Length; i++)
        {
            _phases[i].Add(_timings.Points[i].Available ? frame.Timings.Milliseconds((TimingPoint)i) : double.NaN, _timings.Points[i].Reason);
            if (_timings.Points[i].Available) _calls[i] += frame.TimingCalls.Get((TimingPoint)i);
        }
        Scene(0, frame.Scene.Area); Scene(1, frame.Scene.AreaSize); Scene(2, frame.Scene.NodeEnemies);
        Scene(3, frame.Scene.CloseEnemies); Scene(4, frame.Scene.AggressiveEnemies);
        // Match TSV's six-decimal scene precision before binning float fields;
        // otherwise .1f can land above the serialized .1 context boundary.
        _scene[5].Add(Math.Round(frame.Scene.FogDensity, 6), "scene_fields_not_verified_or_local_object_missing");
        _scene[6].Add(frame.Scene.FogAltitude, "scene_fields_not_verified_or_local_object_missing");
        _scene[7].Add(frame.Scene.PlayerY, "scene_fields_not_verified_or_local_object_missing");
        _scene[8].Add(frame.Scene.PlayerY - frame.Scene.FogAltitude, "scene_fields_not_verified_or_local_object_missing");
        _collector.Add(frame.CollectorMilliseconds, "previous_collector_interval_not_available");
        _observer.Add(frame.ObserverTicks * (1000.0 / Stopwatch.Frequency)); _observerCalls += frame.ObserverCalls;
        _allocated.Add(frame.MainAllocatedBytes >= 0 ? frame.MainAllocatedBytes : double.NaN, "managed_allocation_counter_reset");
        _used.Add(frame.Il2CppUsed >= 0 ? frame.Il2CppUsed : double.NaN, "native_heap_not_verified;see_session_native_memory");
        _heap.Add(frame.Il2CppHeap >= 0 ? frame.Il2CppHeap : double.NaN, "native_heap_not_verified;see_session_native_memory");
        _dropped = Math.Max(_dropped, frame.DroppedBefore); _presentCalls += frame.Clock.WaitCalls;
        if (double.IsFinite(wall) && wall > 0) _thresholds.Add(threshold);
        if (hitch) { _hitches++; _hitchesTotal++; }
        if (wall >= 33) _over33++; if (wall >= 50) _over50++; if (wall >= 100) _over100++;
        if (frame.Marks != PerformanceMark.None) _markerFrames++;
        for (var i = 0; i < _marks.Length; i++) if (((int)frame.Marks & (1 << i)) != 0) _marks[i]++;
        _spawnGroups += frame.SpawnGroups; _waves += frame.Waves;
    }
    private void Scene(int index, int value) => _scene[index].Add(value >= 0 ? value : double.NaN, "scene_fields_not_verified_or_local_object_missing");
    private void Summary()
    {
        if (_received == 0) return;
        Begin("frame_summary", _lastTimestamp); Text("measurement_schema", Schema); Text("state", State(_state));
        Number("attempt", _attempt);
        Text("window_start", Timestamp(_windowFirst)); Number("frame", _lastSequence); Number("samples", _frames.Count); Number("received_frames", _received);
        Number("average_ms", _frames.Count > 0 ? _frames.Sum / _frames.Count : double.NaN); Number("maximum_ms", _frames.Maximum);
        Number("p50_ms", _frames.Percentile(.5)); Number("p95_ms", _frames.Percentile(.95)); Number("p99_ms", _frames.Percentile(.99)); Number("p999_ms", _frames.Percentile(.999));
        Number("hitch_frames", _hitches); Number("ge33_frames", _over33); Number("ge50_frames", _over50); Number("ge100_frames", _over100);
        Stats("hitch_threshold_ms", _thresholds, histogram: false); Stats("frame_ms", _frames);
        for (var i = 0; i < Names.Length; i++)
        { Stats(FrameFields[i], _phases[i]); if (_phases[i].Count > 0) Number(CallFields[i], _calls[i]); else Text(CallFields[i], "unavailable"); }
        for (var i = 0; i < SceneFields.Length; i++) Stats(SceneFields[i], _scene[i], histogram: i == 5 || i == 8, endpoints: true);
        Stats("collector_previous_frame_ms", _collector); Stats("observer_ms", _observer); Stats("main_managed_allocated_bytes", _allocated, histogram: false);
        Number("observer_calls", _observerCalls); Number("marker_frames", _markerFrames); Number("spawn_groups", _spawnGroups); Number("wave_starts", _waves);
        for (var i = 0; i < _marks.Length; i++) Number("mark_" + (1 << i) + "_frames", _marks[i]);
        Number("dropped_frames", _dropped); Number("history_evicted_frames", _historyEvicted);
        Number("net_gc0_first", _gc0First); Number("net_gc1_first", _gc1First); Number("net_gc2_first", _gc2First);
        Number("net_gc0_last", _gc0Last); Number("net_gc1_last", _gc1Last); Number("net_gc2_last", _gc2Last);
        ForgeTraffic(); EndLine();
        Begin("frame_partition", _lastTimestamp); Text("measurement_schema", Schema); Text("state", State(_state)); Number("attempt", _attempt); Number("frame", _lastSequence);
        Number("samples", _frames.Count); Number("complete_frames", _complete); Number("unavailable_frames", _received - _complete);
        Text("status", _complete > 0 ? "samples" : "unavailable"); Text("reason", _other.Missing > 0 || _complete == 0 ? _other.Reason : "none");
        Stats("cpu_main_ms", _cpu); Stats("present_wait_ms", _wait); Stats("other_ms", _other);
        Number("complete_wall_ms", _complete > 0 ? _completeWall : double.NaN);
        Number("complete_cpu_ms", _complete > 0 ? _completeCpu : double.NaN);
        Number("complete_wait_ms", _complete > 0 ? _completeWait : double.NaN);
        Number("present_calls", _presentCalls); EndLine();
        var memory = ProcessMemory(); Begin("memory_summary", _lastTimestamp);
        Text("measurement_schema", Schema); Text("state", State(_state)); Number("attempt", _attempt); Number("frame", _lastSequence);
        Count("working_set_bytes", memory.WorkingSet); Count("private_bytes", memory.Private);
        if (memory.WorkingSet < 0 || memory.Private < 0) Text("process_memory_reason", "GetProcessMemoryInfo_failed");
        Stats("il2cpp_used_bytes", _used, histogram: false, endpoints: true); Stats("il2cpp_heap_bytes", _heap, histogram: false, endpoints: true); EndLine();
        CollectorSummary();
        _thresholds.Reset(); _frames.Reset(); _cpu.Reset(); _wait.Reset(); _other.Reset(); _collector.Reset(); _observer.Reset(); _allocated.Reset(); _used.Reset(); _heap.Reset();
        foreach (var phase in _phases) phase.Reset(); foreach (var scene in _scene) scene.Reset();
        Array.Clear(_calls); Array.Clear(_marks);
        _received = _complete = _hitches = _over33 = _over50 = _over100 = _markerFrames = _spawnGroups = _waves = _presentCalls = _observerCalls = 0;
        _completeWall = _completeCpu = _completeWait = 0;
    }
    // Forge's own network sends per second over this window (contract P8 network): totals since process start plus the rates.
    private void ForgeTraffic()
    {
        var read = Traffic;
        if (read == null) { Text("forge_net", "unavailable"); return; }
        var now = Stopwatch.GetTimestamp();
        var sent = read();
        Number("forge_net_messages", sent.Messages); Number("forge_net_bytes", sent.Bytes);
        Number("forge_net_lossy_messages", sent.LossyMessages); Number("forge_net_lossy_bytes", sent.LossyBytes);
        var seconds = _netAt == 0 ? 0 : (now - _netAt) / (double)Stopwatch.Frequency;
        if (seconds > 0)
        {
            Number("forge_net_messages_per_second", (sent.Messages - _net.Messages) / seconds);
            Number("forge_net_bytes_per_second", (sent.Bytes - _net.Bytes) / seconds);
            Number("forge_net_lossy_messages_per_second", (sent.LossyMessages - _net.LossyMessages) / seconds);
            Number("forge_net_lossy_bytes_per_second", (sent.LossyBytes - _net.LossyBytes) / seconds);
        }
        else Text("forge_net_rate", "first_reading");
        _net = sent; _netAt = now;
    }
    private void CollectorSummary()
    {
        // Flush text buffering before reading actual UTF-8 prefix bytes. This is
        // writer-only work; the row itself belongs to the next growth interval.
        _stream.Flush();
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _origin) / (double)Stopwatch.Frequency;
        var bytes = _stream.BaseStream.Position;
        Begin("collector_summary", now); Text("measurement_schema", Schema); Text("scope", "session");
        if (_collectorMetrics != null && _collectorMetrics.TryRead(out var sample))
        {
            Number("sampled_frames", sample.Frames); Number("dropped_frames", sample.Dropped);
            var scale = 1000.0 / Stopwatch.Frequency;
            CollectorStats("collector_ms", sample.Frames, sample.Ticks * scale, sample.MinimumTicks * scale, sample.MaximumTicks * scale);
            CollectorStats("collector_managed_allocated_bytes", sample.Frames - sample.MissingAllocated,
                sample.Allocated, sample.MinimumAllocated, sample.MaximumAllocated);
            if (sample.MissingAllocated > 0)
            { Number("collector_managed_allocated_bytes_missing", sample.MissingAllocated); Text("collector_managed_allocated_bytes_reason", "managed_allocation_counter_reset"); }
            if (sample.Frames == 0) Text("collector_reason", "no_completed_collector_call");
        }
        else
        {
            Text("sampled_frames", "unavailable"); Text("dropped_frames", "unavailable");
            CollectorStats("collector_ms", 0, 0, 0, 0); CollectorStats("collector_managed_allocated_bytes", 0, 0, 0, 0);
            Text("collector_reason", _collectorMetrics == null ? "collector_source_not_attached" : "collector_snapshot_busy");
        }
        Text("collector_native_allocated_bytes", "unavailable"); Text("collector_native_allocated_bytes_reason", "no_verified_per_collector_native_allocation_counter");
        Number("log_bytes", bytes); Number("log_elapsed_seconds", elapsed);
        Number("log_bytes_per_second", elapsed > 0 ? bytes / elapsed : double.NaN); EndLine();
    }
    private void CollectorStats(string key, long count, double sum, double minimum, double maximum)
    {
        _line.Append('\t').Append(key).Append("_stats=").Append(count).Append(',');
        Scalar(count > 0 ? sum : double.NaN); _line.Append(','); Scalar(count > 0 ? minimum : double.NaN);
        _line.Append(','); Scalar(count > 0 ? maximum : double.NaN);
    }
    private void Stats(string key, PerformanceHistogram values, bool histogram = true, bool endpoints = false)
    {
        // One tuple avoids repeating long point names for every aggregate.
        // count,sum,min,max[,first,last]; all missing measurements stay explicit.
        _line.Append('\t').Append(key).Append("_stats=").Append(values.Count).Append(',');
        Scalar(values.Count > 0 ? values.Sum : double.NaN); _line.Append(','); Scalar(values.Minimum); _line.Append(','); Scalar(values.Maximum);
        if (endpoints) { _line.Append(','); Scalar(values.First); _line.Append(','); Scalar(values.Last); }
        if (values.Missing > 0) Number(key + "_missing", values.Missing);
        if (values.Missing > 0 || values.Count == 0) Text(key + "_reason", values.Reason);
        if (!histogram) return;
        Number(key + "_hist_scale", values.Scale);
        _line.Append('\t').Append(key).Append("_hist=");
        if (values.Count == 0) { _line.Append("unavailable"); return; }
        var first = true; var previous = 0;
        foreach (var bucket in values.Buckets())
        { if (!first) _line.Append(','); _line.Append(bucket.Id - previous).Append(':').Append(bucket.Count); previous = bucket.Id; first = false; }
    }
    internal void End(long dropped)
    {
        if (_ended) return;
        Summary(); CollectorSummary(); _closing = true;
        Begin("session_end", Stopwatch.GetTimestamp()); Number("dropped_frames", dropped);
        Number("history_evicted_frames", _historyEvicted);
        Number("forge_hook_dropped_windows", _forgeHookSource?.DroppedWindows ?? 0);
        Text("dropped_frame_effect", "dropped_samples_and_their_marks_are_missing;no_interpolation"); EndLine();
        _stream.Flush(); ((FileStream)_stream.BaseStream).Flush(flushToDisk: true); _ended = true;
    }
    private void Begin(string type, long timestamp)
    {
        _line.Clear();
        var time = Date(timestamp);
        Span<char> date = stackalloc char[32]; time.TryFormat(date, out var length, "O", CultureInfo.InvariantCulture);
        _line.Append(date[..length]); Text("type", type);
    }
    private DateTime Date(long timestamp) => _epoch.AddTicks((long)((timestamp - _origin) * (10_000_000.0 / Stopwatch.Frequency)));
    private string Timestamp(long timestamp) => Date(timestamp).ToString("O", CultureInfo.InvariantCulture);
    private void Text(string key, string value) => _line.Append('\t').Append(key).Append('=').Append(value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
    private void Number(string key, long value) => _line.Append('\t').Append(key).Append('=').Append(value);
    private void Number(string key, double value)
    {
        _line.Append('\t').Append(key).Append('=');
        Scalar(value);
    }
    private void Scalar(double value)
    {
        if (!double.IsFinite(value)) { _line.Append("unavailable"); return; }
        Span<char> chars = stackalloc char[32]; value.TryFormat(chars, out var length, "0.######", CultureInfo.InvariantCulture); _line.Append(chars[..length]);
    }
    private void Count(string key, long value) { if (value < 0) Text(key, "unavailable"); else Number(key, value); }
    private void Timing(string key, NativeTimingSample sample)
    { if (sample.Available) Number(key, sample.Nanoseconds / 1_000_000.0); else { Text(key, "unavailable"); Text(key + "_reason", sample.Reason); } }
    private void EndLine()
    {
        if (_truncated && !_closing) return;
        WriteBuffered();
        if (_truncated || _bytes < _maximumBytes) return;
        // One record explains the cut; afterwards only session_end is written, so the file stays within the limit plus a few lines.
        _truncated = true;
        Begin("log_truncated", Stopwatch.GetTimestamp()); Text("reason", "log_size_limit"); Number("limit_bytes", _maximumBytes);
        Text("effect", "no_further_lines_except_session_end");
        WriteBuffered();
    }
    private void WriteBuffered()
    {
        foreach (var chunk in _line.GetChunks()) { _stream.Write(chunk.Span); _bytes += Encoding.UTF8.GetByteCount(chunk.Span); }
        _stream.WriteLine(); _bytes += Environment.NewLine.Length;
    }
    internal static string State(int state) => state switch
    {
        0 => "Inactive",
        1 => "Startup",
        2 => "Offline",
        3 => "FakeLobby",
        4 => "NoLobby",
        5 => "Lobby",
        6 => "Generating",
        7 => "ReadyToStopElevatorRide",
        8 => "StopElevatorRide",
        9 => "ReadyToStartLevel",
        10 => "InLevel",
        11 => "AfterLevel",
        12 => "Slim",
        13 => "CaptureRecall",
        14 => "ExpeditionSuccess",
        15 => "ExpeditionFail",
        16 => "ExpeditionAbort",
        _ => "unavailable"
    };
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        internal uint Size, PageFaults;
        internal UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile, Private;
    }
    private static (long WorkingSet, long Private) ProcessMemory()
    {
        var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        return GetProcessMemoryInfo(new IntPtr(-1), ref counters, counters.Size) ? ((long)counters.WorkingSet.ToUInt64(), (long)counters.Private.ToUInt64()) : (-1, -1);
    }
    [DllImport("psapi.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
    public void Dispose() => _stream.Dispose();
}
