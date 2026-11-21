using System;
using System.Globalization;
using System.Reflection;
using ForgeDiagnostics;
using GTFO.API;
using LevelGeneration;

namespace ForgeDevelopment.Native;

/// <summary>The drop measurement of an authoring session: the drop-request hook and the read-only selection, factory-setup and
/// job-dequeue observations, and the attempt clock a level generator's boundaries report to (<see cref="ForgeCore.GenerationAttempt"/>).
/// A player's install has none of this.</summary>
internal static class DropTimingHost
{
    private const BindingFlags Callback = BindingFlags.Static | BindingFlags.NonPublic;
    private static IDisposable? _jobs, _setup, _selection;
    private static bool _framed, _jobsActive;

    internal static void Start()
    {
        DropTiming.Start(message => Plugin.PluginLog.LogInfo(message),
            static () => RecTime.WorldEpoch.ToString(CultureInfo.InvariantCulture), RequestHook);
        ForgeCore.GenerationAttempt.Bind(static () =>
        {
            var now = DropTiming.Current;
            return new(now.Generation, now.StartedAt, now.Frequency, now.Active, now.FactoryDone, now.Ready, now.Entered);
        }, static (generation, phase, origin, calls) => DropTiming.MarkBoundary(generation, phase, origin, calls));
        Observe(ref _setup, "Setup", typeof(LG_Factory).GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null),
            nameof(SetupBefore), nameof(SetupFinished));
        Observe(ref _selection, "Selection", typeof(RundownManager).GetMethod("MasterSelectActiveExpedition", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(eRundownKey), typeof(eRundownTier), typeof(int), typeof(string), typeof(uint) }, null),
            nameof(SelectionBefore), nameof(SelectionFinished));
        // Successful dequeues of the factory's job queue (a level generator that supplies the job still returns through it).
        try
        {
            _jobs = DevelopmentHookInstall.Observe("ForgeDevelopment.DropTiming.Jobs", typeof(LG_Factory).GetMethod(nameof(LG_Factory.GetNewJob),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                    ?? throw new MissingMethodException("LG_Factory.GetNewJob()"), null, typeof(DropTimingHost).GetMethod(nameof(JobReturned), Callback), null);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException or MissingMethodException)
        { Plugin.PluginLog.LogWarning("drop.timing-unavailable boundary=jobs reason=" + error.Message); }
        LevelAPI.OnBuildStart += BuildStarted;
        LevelAPI.OnLevelCleanup += ReleaseJobs;
        if (!_framed) { _framed = true; ForgeCore.Frame.OnUpdate(Plugin.PluginGuid, DropTiming.Tick); }
    }

    internal static void Stop()
    {
        LevelAPI.OnBuildStart -= BuildStarted;
        LevelAPI.OnLevelCleanup -= ReleaseJobs;
        ReleaseJobs();
        _jobs?.Dispose(); _jobs = null;
        _setup?.Dispose(); _setup = null;
        _selection?.Dispose(); _selection = null;
        ForgeCore.GenerationAttempt.Bind(null, null);
        DropTiming.Stop();
    }

    private static IDisposable RequestHook() => DevelopmentHookInstall.Managed("ForgeDevelopment.DropTiming",
        typeof(CellMenu.CM_PageLoadout).GetMethod("EXT_StartExpedition")!,
        typeof(DropTiming).GetMethod(nameof(DropTiming.RequestPrefix), Callback),
        typeof(DropTiming).GetMethod(nameof(DropTiming.RequestPostfix), Callback),
        typeof(DropTiming).GetMethod(nameof(DropTiming.RequestFinalizer), Callback),
        ilWrapper: true);

    // A refused observation leaves that boundary out of the records; the measurement says so and the session goes on.
    private static void Observe(ref IDisposable? lease, string name, MethodInfo? target, string before, string finished)
    {
        try
        {
            lease = DevelopmentHookInstall.Observe("ForgeDevelopment.DropTiming." + name,
                target ?? throw new MissingMethodException(name), typeof(DropTimingHost).GetMethod(before, Callback), null,
                typeof(DropTimingHost).GetMethod(finished, Callback));
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException or MissingMethodException)
        { Plugin.PluginLog.LogWarning("drop.timing-unavailable boundary=" + name + " reason=" + error.Message); }
    }

    // Dequeues count from a level's build start until its cleanup, the window the world-bound subscription covered.
    private static void BuildStarted() => _jobsActive = true;
    private static void ReleaseJobs() => _jobsActive = false;
    private static void JobReturned(bool __result) { if (_jobsActive) DropTiming.JobDequeued(__result); }

    private static void SelectionBefore(out int __state) => __state = DropTiming.SelectionStarted();
    private static void SelectionFinished(Exception? __exception, int __state) => DropTiming.SelectionFinished(__state, __exception == null);
    private static void SetupBefore(out int __state)
    {
        __state = DropTiming.Current.Generation;
        DropTiming.MarkBoundary(__state, "factory-setup-started", "LG_Factory.Setup");
    }
    private static void SetupFinished(Exception? __exception, int __state)
        => DropTiming.MarkBoundary(__state, __exception == null ? "factory-setup-returned" : "factory-setup-failed", "LG_Factory.Setup");
}
