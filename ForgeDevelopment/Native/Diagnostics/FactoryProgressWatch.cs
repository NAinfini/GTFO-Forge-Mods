using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Says in the BepInEx log (and so in the recorder's log channel) and in the run's report what the native
/// level factory is doing while a level generates: a heartbeat with the current batch and job, and a "no progress"
/// line when the same job stays current for several seconds. A generation that the factory never finishes ends with
/// no exception, so without this a stuck or merely slow build looks identical and leaves no trace.
/// Sampling is a time check per frame; the native factory is read twice a second.</summary>
internal static class FactoryProgressWatch
{
    private const float SampleSeconds = 0.5f;
    private static readonly FactoryProgressTracker Tracker = new();
    private static readonly FactoryBatchWindow BatchWindow = new();
    private static float _nextSample;
    private static int _run = -1;

    internal static void Begin() => BatchWindow.Begin(Time.realtimeSinceStartup);

    /// <param name="active">True from the start of a generation until the factory is done or the level is left.</param>
    internal static void Tick(bool active, int run, DiagnosticsReport? report)
    {
        var real = Time.realtimeSinceStartup;
        if (real < _nextSample) return;
        _nextSample = real + SampleSeconds;
        var factory = active ? LG_Factory.Current : null;
        if (factory == null || LG_Factory.BuildDone) { Tracker.Reset(); _run = -1; return; }
        if (run != _run) { Tracker.Reset(); _run = run; }
        var job = factory.m_currentJob;
        var sample = new FactorySample(LG_Factory.m_jobInstanceCounter, (int)factory.m_currentBatchName, job == null ? 0 : job.Pointer.ToInt64());
        if (BatchWindow.Observe(real, sample.Batch) is { } duration)
        {
            var message = "factory.batch-window batch=" + ((LG_Factory.BatchName)duration.Batch)
                + " minimumMs=" + duration.MinimumMs.ToString("F3", CultureInfo.InvariantCulture)
                + " maximumMs=" + duration.MaximumMs.ToString("F3", CultureInfo.InvariantCulture)
                + " boundary=sampled-native-batch-change";
            Plugin.PluginLog.LogInfo(message);
            report?.Event("factory", "batch_window", ((LG_Factory.BatchName)duration.Batch).ToString(),
                new() { ["message"] = message });
        }
        var verdict = Tracker.Observe(real, sample);
        if (verdict == FactoryProgress.None) return;
        if ((verdict & FactoryProgress.Stalled) != 0) Say(report, factory, job, real, "no-progress", true);
        else if ((verdict & FactoryProgress.Heartbeat) != 0) Say(report, factory, job, real, "progress", false);
        if ((verdict & FactoryProgress.BatchChanged) != 0)
            report?.Event("factory", "batch", ((LG_Factory.BatchName)sample.Batch).ToString(),
                new()
                {
                    ["jobsCreated"] = Tracker.JobsCreated.ToString(CultureInfo.InvariantCulture),
                    ["jobInstanceCounter"] = sample.JobInstances.ToString(CultureInfo.InvariantCulture),
                    ["elapsedSeconds"] = Seconds(Tracker.Elapsed(real))
                });
    }

    private static void Say(DiagnosticsReport? report, LG_Factory factory, LG_FactoryJob? job, float now, string code, bool stalled)
    {
        var batch = factory.m_currentBatchName.ToString();
        var message = $"factory.{code} batch={batch}({factory.m_batchStep}) job={JobName(job)} jobsCreated={Tracker.JobsCreated} jobInstanceCounter={LG_Factory.m_jobInstanceCounter}"
            + $" sinceProgress={Seconds(Tracker.SinceProgress(now))}s inBatch={Seconds(Tracker.InBatch(now))}s elapsed={Seconds(Tracker.Elapsed(now))}s"
            + (stalled ? $" report={Tracker.StallReports}" : "");
        if (stalled) Plugin.PluginLog.LogWarning(message); else Plugin.PluginLog.LogInfo(message);
        if (stalled) report?.Issue("factory_no_progress", "LG_Factory/" + batch, message, severity: "warning");
        report?.Event("factory", code, batch, new() { ["message"] = message });
    }

    private static string Seconds(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    // The class name is read from the native object so a job with no managed wrapper type still names itself.
    private static string JobName(LG_FactoryJob? job)
    {
        if (job == null) return "none";
        var native = IL2CPP.il2cpp_class_get_name(IL2CPP.il2cpp_object_get_class(job.Pointer));
        var name = native == IntPtr.Zero ? "unnamed" : Marshal.PtrToStringAnsi(native) ?? "unnamed";
        try { return name + ":" + job.GetName(); }
        catch (Exception) { return name; } // Il2Cpp read guard: the job label falls back to its native class name, which is all a progress line needs.
    }
}
