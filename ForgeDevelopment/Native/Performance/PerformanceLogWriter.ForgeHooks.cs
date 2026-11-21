using System.Diagnostics;

namespace ForgeDevelopment.Native;

internal sealed partial class PerformanceLogWriter
{
    private ForgeHookSampler? _forgeHookSource;
    private long _hitchesTotal, _hitchesAtWindow;
    internal void TrackForgeHooks(ForgeHookSampler? sampler) => _forgeHookSource = sampler;
    private static readonly string[] ForgePackages = { "ForgeEngine", "ForgePlayer", "ForgeWeapon", "ForgeEnemy", "ForgeMap", "ForgeDevelopment" };
    internal void ForgeHooks(ForgeHookWindow window, ForgeHookSampler sampler)
    {
        for (var i = 0; i < sampler.Names.Length; i++)
        {
            var inclusive = window.Inclusive[i]; var self = window.Exclusive[i];
            Begin("forge_hook", window.Timestamp);
            Text("state", State(window.State)); Number("frame", window.Frame); Number("window_seconds", window.Seconds);
            Text("package", sampler.Packages[i]); Text("hook", sampler.Names[i]); Number("calls", inclusive.Calls);
            Number("total_ms", inclusive.Ticks * (1000d / Stopwatch.Frequency));
            Number("self_ms", self.Ticks * (1000d / Stopwatch.Frequency));
            Number("max_ms", inclusive.MaximumTicks * (1000d / Stopwatch.Frequency));
            Number("max_self_ms", self.MaximumTicks * (1000d / Stopwatch.Frequency));
            Number("allocated_bytes", inclusive.Bytes); Number("self_allocated_bytes", self.Bytes);
            Number("max_allocated_bytes", inclusive.MaximumBytes); Number("dropped_windows", window.Dropped);
            Text("coverage", "instrumented_managed_bodies;excludes_Harmony_dispatch_boxing_native_boundary_and_uninstrumented_callbacks");
            EndLine();
        }
        Begin("forge_gc", window.Timestamp);
        Text("state", State(window.State)); Number("frame", window.Frame); Number("window_seconds", window.Seconds);
        Number("gc0", window.Gc0); Number("gc1", window.Gc1); Number("gc2", window.Gc2);
        Number("gc_pause_ms", window.GcPauseMilliseconds); Number("gc_missed_pauses", window.GcMissedPauses);
        Number("hitch_frames", _hitchesTotal - _hitchesAtWindow); _hitchesAtWindow = _hitchesTotal; Number("dropped_windows", window.Dropped);
        Text("coverage", "managed_collections_of_the_process;pause_of_the_last_collection_per_window_lower_bound_when_missed_pauses_nonzero;hitches_counted_by_the_frame_detector"); EndLine();
        foreach (var package in ForgePackages)
        {
            long ticks = 0, calls = 0, bytes = 0;
            for (var i = 0; i < sampler.Names.Length; i++) if (sampler.Packages[i] == package)
            { ticks += window.Exclusive[i].Ticks; calls += window.Exclusive[i].Calls; bytes += window.Exclusive[i].Bytes; }
            Begin("forge_package", window.Timestamp); Text("state", State(window.State)); Text("package", package);
            Number("window_seconds", window.Seconds); Number("calls", calls);
            Number("self_ms", ticks * (1000d / Stopwatch.Frequency)); Number("self_allocated_bytes", bytes);
            Text("coverage", "instrumented_bodies_only;not_entire_plugin_cost"); EndLine();
        }
    }
}
