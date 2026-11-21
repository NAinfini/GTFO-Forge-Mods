using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CullingSystem;
using LevelGeneration;
using UnityEngine;
using Il2CppInterop.Runtime;
using UnityEngine.SceneManagement;

namespace ForgeDevelopment.Native;

public sealed class AuthoringMonitor : MonoBehaviour
{
    private readonly long _collection = RuntimeDiagnostics.Collection;
    public AuthoringMonitor(IntPtr pointer) : base(pointer) { }
    public void Update()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentFrame);
        using (ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentMaterials))
            RuntimeDiagnostics.Budgeted(LevelGuideCaptureMaterials.Tick);
        RuntimeDiagnostics.Budgeted(RuntimeDiagnostics.Tick);
    }
    // The recorder's own notice is the only thing this component draws: a session that reached its budget says so
    // in the corner of the screen, not only in a log the player will never open.
    public void OnGUI() => RuntimeDiagnostics.Budgeted(RecNotice.OnGui);
    public void OnApplicationQuit()
    {
        RuntimeDiagnostics.ProcessExiting = true;
        RuntimeDiagnostics.Stop();
    }
    public void OnDestroy()
    {
        if (_collection != RuntimeDiagnostics.Collection || !RuntimeDiagnostics.Collecting) return;
        LevelGuideCaptureMaterials.StopPlanWatch();
        RecRuntime.Stop();
        RuntimeDiagnostics.Pause();
    }
}

internal static class RuntimeDiagnostics
{
    internal sealed record JobTrace(string Subject, string Stage, string Before, Dictionary<string, string> Context, long Started, LG_FactoryJob Job, DiagnosticsReport Report, long WorldEpoch);
    private sealed class LogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.Error | LogLevel.Fatal | LogLevel.Warning;
        public void LogEvent(object sender, LogEventArgs args)
        {
            if (args.Source is BepInEx.Unity.IL2CPP.Logging.IL2CPPUnityLogSource) return;
            // This plugin's own errors are reported too (its warnings stay out); Report.Issue never logs, so there is no loop.
            var ownSource = args.Source.SourceName == Plugin.PluginName;
            if ((args.Level & (ownSource ? LogLevel.Error | LogLevel.Fatal : LogLevel.Error | LogLevel.Fatal | LogLevel.Warning)) == 0) return;
            var text = args.Data?.ToString() ?? "";
            if (text.Length == 0) return;
            var (type, source) = DiagnosticLogClassifier.Classify(args.Level.ToString(), args.Source.SourceName, text);
            _report?.Issue(type, source, text, text, severity: (args.Level & (LogLevel.Error | LogLevel.Fatal)) != 0 ? "error" : "warning",
                origin: DiagnosticLogClassifier.Origin(args.Source.SourceName, text));
        }
        public void Dispose() { }
    }
    private static readonly LogListener Listener = new();
    private static Application.LogCallback? _unityCallback;
    private static uint _unityCallbackRoot;
    private static readonly string Session = DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
    private static readonly Dictionary<string, int> JobCalls = new();
    private static readonly Dictionary<string, (int Calls, double Total, double Maximum)> StageCosts = new();
    private static readonly HashSet<string> CullingObservations = new();
    private static volatile DiagnosticsReport? _report;
    private static IEnumerator<byte>? _inspection;
    private static readonly GenerationWorldBinding _world = new();
    private static ProjectInspectionSession? _inspectionSession => _world.Session;
    private static bool _generationActive;
    private static AsyncReportWriter? _writer;
    private static bool _listening, _stopped = true, _structureComplete, _jobIdentityCaptured;
    internal static long Collection { get; private set; }
    internal static bool Collecting => !_stopped;
    private static int _run, _cleanupCount;
    private static string _path = "", _state = "", _outcome = "observing";
    private static eGameStateName? _nativeState;
    private static double _nextExport;
    private static long _inspectionTicks, _maxInspectionTicks, _traceTicks;
    private static int _steps;

    internal static DiagnosticsReport? Report => _report;
    /// <summary>Whether this run's <see cref="LG_Factory.FactoryDone"/> has already been observed.</summary>
    internal static bool StructureComplete => _structureComplete;
    internal static string GenerationId => Session + "-" + _run.ToString("D3", CultureInfo.InvariantCulture);
    internal static string SessionId => Session;
    /// <summary>Records a one-line observation about the diagnostics themselves in the run's report, so a failure
    /// inside the layout record is visible where every other diagnostics failure is.</summary>
    internal static void Note(string code, string message) => _report?.Issue(code, "diagnostics", message);
    internal static long WorldEpoch => RecTime.WorldEpoch;
    /// <summary>No simulation clock runs in this package: the tick is unknown (null) and the reports say so.</summary>
    internal static long? SimulationTick => null;
    internal static string Number(double n) => n.ToString("R", CultureInfo.InvariantCulture);
    internal static string Vector(Vector3 p) => $"{Number(p.x)},{Number(p.y)},{Number(p.z)}";
    internal static string Rotation(Quaternion q) => $"{Number(q.x)},{Number(q.y)},{Number(q.z)},{Number(q.w)}";
    internal static string Zone(LG_Zone? zone) => zone == null ? "unavailable" : $"{zone.DimensionIndex}/{zone.Layer?.m_type.ToString() ?? "unavailable"}/{zone.LocalIndex}";
    internal static string PathOf(Transform? t)
    {
        if (t == null) return "unavailable";
        var names = new List<string>();
        for (var i = 0; t != null && i < 32; i++, t = t.parent) names.Add(t.name + "[" + t.GetSiblingIndex() + "]");
        names.Reverse(); return string.Join("/", names);
    }
    internal static void Safe(Action action)
    {
        using var cpu = ForgeCore.DiagnosticCpuBudget.Shared.MeasureRequired();
        using var scope = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentObserver);
        try { action(); }
        catch (Exception e)
        {
            // A failed observer is reported, never substituted for the original game exception.
            _report?.Issue("diagnostic_error", "ForgeDevelopment", e.Message, e.ToString());
            if (_report == null) Plugin.PluginLog.LogError(e);
        }
    }
    internal static void Budgeted(Action action)
    {
        var budget = ForgeCore.DiagnosticCpuBudget.Shared;
        budget.BeginFrame(RecTime.Frame);
        if (!budget.TryReserve(out var reservation)) return;
        using (reservation) Safe(action);
    }
    internal static void Initialize()
    {
        if (_listening) return;
        Collection++;
        _stopped = false;
        _writer = new AsyncReportWriter(error => Plugin.PluginLog.LogError("Forge report write failed: " + error));
        _unityCallback = (Application.LogCallback)(Action<string, string, LogType>)UnityLog;
        // Keep the native delegate alive, not just its managed wrapper.
        _unityCallbackRoot = IL2CPP.il2cpp_gchandle_new(_unityCallback.Pointer, false);
        Application.add_logMessageReceivedThreaded(_unityCallback);
        BepInEx.Logging.Logger.Listeners.Add(Listener);
        _listening = true;
        _report = new DiagnosticsReport(Session + "-startup");
        _path = System.IO.Path.Combine(Paths.BepInExRootPath, "ForgeReports", Session + "-startup.forge");
        _report.SetMetadata("sessionId", Session);
        _report.SetMetadata("forgeVersion", EngineLookup.Version ?? "absent");
        _report.SetMetadata("developmentVersion", Plugin.PluginVersion);
        _report.Check("structure", "factory", "not_checked", "Waiting for generation.");
        // Bounds ForgeReports whether or not the recorder is on; a recorder session opened later is protected by the live window.
        RecRetention.StartOnce(System.IO.Path.Combine(Paths.BepInExRootPath, "ForgeReports"), "", Session,
            Settings.RecorderKeepSessions.Value, Settings.RecorderReportsMaxGiB.Value * 1024L * 1024 * 1024,
            message => Plugin.PluginLog.LogInfo(message));
    }
    private static void UnityLog(string message, string stack, LogType level)
    {
        if (level is not (LogType.Exception or LogType.Error or LogType.Assert or LogType.Warning)) return;
        var report = _report;
        if (report == null || string.IsNullOrEmpty(message)) return;
        var (type, source) = DiagnosticLogClassifier.Classify(level.ToString(), "Unity", message + "\n" + stack);
        report.Issue(type, source, message, stack, severity: level == LogType.Warning ? "warning" : "error", origin: DiagnosticLogClassifier.Origin("Unity", message + "\n" + stack));
    }
    internal static void Begin()
    {
        CancelInspection("new_generation");
        ProjectChecks.CancelSourceSnapshot("New generation started.");
        Export(_run == 0 ? "startup" : _outcome);
        _run++;
        var id = Session + "-" + _run.ToString("D3", CultureInfo.InvariantCulture);
        _report = new DiagnosticsReport(id);
        _path = System.IO.Path.Combine(Paths.BepInExRootPath, "ForgeReports", id + ".forge");
        _structureComplete = false; _jobIdentityCaptured = false; _outcome = "generating"; _lastMapProgress = "";
        _generationActive = true;
        FactoryProgressWatch.Begin();
        _inspectionTicks = _maxInspectionTicks = _traceTicks = 0; _steps = 0;
        JobCalls.Clear(); StageCosts.Clear(); CullingObservations.Clear();
        _report.SetMetadata("sessionId", Session);
        _report.SetMetadata("attempt", _run.ToString(CultureInfo.InvariantCulture));
        _report.SetMetadata("cleanupCount", _cleanupCount.ToString(CultureInfo.InvariantCulture));
        _report.SetMetadata("forgeVersion", EngineLookup.Version ?? "absent");
        _report.SetMetadata("developmentVersion", Plugin.PluginVersion);
        _report.SetMetadata("unityVersion", Application.unityVersion);
        _report.SetMetadata("gameVersion", Application.version);
        _report.SetMetadata("bepInExAssemblyVersion", typeof(Paths).Assembly.GetName().Version?.ToString() ?? "unavailable");
        _report.SetMetadata("mode", EngineLookup.Mode);
        _report.SetMetadata("role", ForgeCore.Session.IsHost ? "host" : "client");
        CaptureGenerationIdentity("factory_setup_prefix");
        foreach (var info in IL2CPPChainloader.Instance.Plugins.Values.OrderBy(p => p.Metadata.GUID))
            _report.SetMetadata("plugin:" + info.Metadata.GUID, info.Metadata.Version.ToString());
        _report.Check("structure", "factory", "not_checked", "FactoryDone has not been observed.");
        _report.Check("objective_scripts", "expedition", "not_checked", "No general proof of objective or script completion is possible from object presence.");
        _report.Check("playability", "expedition", "not_checked", "Structure completion does not establish that the level is completable.");
        _report.Event("generation", "begin", id, new() { ["random"] = RandomState() });
        _nextExport = Time.realtimeSinceStartup + 30;
        // The Runtime starts this generation's world after this prefix, so the epoch is not read here: the
        // inspection session is bound at the first job or frame that sees the world the generation runs in.
        _world.Reset();

        Export("generating");
    }
    private static void CaptureGenerationIdentity(string phase)
    {
        if (_report == null) return;
        _report.SetMetadata("seedCaptureStage", phase);
        _report.SetMetadata("seed", Builder.BuildSeed.ToString(CultureInfo.InvariantCulture));
        var expedition = Builder.LevelGenExpedition;
        if (expedition != null)
        {
            _report.SetMetadata("mainLayout", expedition.LevelLayoutData.ToString());
            _report.SetMetadata("secondaryLayout", expedition.SecondaryLayout.ToString());
            _report.SetMetadata("thirdLayout", expedition.ThirdLayout.ToString());
            _report.SetMetadata("hostSeed", expedition.HostIDSeed.ToString());
            _report.SetMetadata("sessionSeed", expedition.SessionSeed.ToString());
        }
    }
    private static void BindWorld()
    {
        if (_world.IsBound || _report == null || !_generationActive) return;
        var epoch = WorldEpoch;
        var scan = ProjectChecks.Load(_report, epoch, SimulationTick, ProjectRoomSource.Resolve);
        _world.Bind(_report, scan, epoch);
    }
    private static string RandomState()
    {
        static string Read(SeedRandom? r) => r == null ? "unavailable" : $"{r.m_seed}:{r.m_step}:{r.m_randomRangeIntCount}:{r.m_randomRangeFloatCount}";
        return "build=" + Read(Builder.BuildSeedRandom) + ";host=" + Read(Builder.HostIDSeedRandom) + ";session=" + Read(Builder.SessionSeedRandom);
    }
    private static string LocalRandom(LG_FactoryJob job)
    {
        var geoJob = job.TryCast<LG_BuildGeomorphJob>();
        if (geoJob?.m_rnd?.m_random == null) return "not_exposed";
        var rnd = geoJob.m_rnd.m_random;
        // Reading the existing state must not draw or reseed the random generator.
        var state = new StringBuilder().Append(rnd.inext).Append(':').Append(rnd.inextp);
        if (rnd.SeedArray != null) foreach (var value in rnd.SeedArray) state.Append(':').Append(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
    }
    internal static JobTrace? StartJob(LG_FactoryJob job, MethodBase method)
    {
        if (_report == null || _stopped || !_generationActive || !DevelopmentFrameActivity.Needed) return null;
        BindWorld();
        if (!_world.Admits(WorldEpoch)) return null;
        var begin = Stopwatch.GetTimestamp();
        // Setup may update generation identity; capture again once jobs actually start.
        if (!_jobIdentityCaptured) { CaptureGenerationIdentity("first_job_prefix"); _jobIdentityCaptured = true; }
        var subject = method.DeclaringType!.FullName + ":" + job.Pointer.ToString("X");
        var stage = LG_Factory.Current?.GetCurrentBatchName.ToString() ?? "unavailable";
        var fields = new Dictionary<string, string> { ["jobType"] = method.DeclaringType!.FullName!, ["randomLocalBefore"] = LocalRandom(job) };
        var context = GenerationJobContext.For(method.DeclaringType).Read(job.Pointer);
        var geo = context.Geomorph as LG_Geomorph;
        var area = context.Area as LG_Area;
        var zone = context.Zone as LG_Zone ?? geo?.m_zone ?? area?.m_zone;
        if (zone != null) fields["zone"] = Zone(zone);
        if (area != null) fields["area"] = area.UID.ToString();
        var batch = LG_Factory.Current?.m_currentBatch;
        if (batch != null) { fields["batchSeed"] = batch.m_localSeed.ToString(); fields["batchSeedOffset"] = batch.m_seedOffset.ToString(); }
        if (geo != null)
        {
            fields["geomorph"] = geo.m_geoPrefab == null ? geo.name : geo.m_geoPrefab.name;
            fields["zone"] = Zone(geo.m_zone);
            fields["position"] = Vector(geo.transform.position);
        }
        var key = method.DeclaringType.FullName + ":" + job.Pointer;
        if (JobCalls.Count < 100000 || JobCalls.ContainsKey(key))
        { JobCalls.TryGetValue(key, out var count); JobCalls[key] = ++count; fields["invocation"] = count.ToString(); }
        fields["invocationMeaning"] = "Build calls; false may be continuation, not evidence of random retry";
        var before = RandomState();
        _traceTicks += Stopwatch.GetTimestamp() - begin;
        return new(subject, stage, before, fields, Stopwatch.GetTimestamp(), job, _report, WorldEpoch);
    }
    internal static void EndJob(JobTrace? trace, bool done, Exception? error)
    {
        if (trace == null || _report == null || !_generationActive ||
            !ReferenceEquals(trace.Report, _report) || trace.WorldEpoch != WorldEpoch) return;
        var end = Stopwatch.GetTimestamp();
        trace.Context["randomBefore"] = trace.Before;
        trace.Context["randomAfter"] = RandomState();
        trace.Context["randomLocalAfter"] = LocalRandom(trace.Job);
        trace.Context["returnedDone"] = done.ToString();
        var elapsed = (end - trace.Started) * 1000.0 / Stopwatch.Frequency;
        StageCosts.TryGetValue(trace.Stage, out var costs);
        StageCosts[trace.Stage] = (costs.Calls + 1, costs.Total + elapsed, Math.Max(costs.Maximum, elapsed));
        if (done || error != null || trace.Context.GetValueOrDefault("invocation") == "1")
            _report.Event("generation_job", trace.Stage, trace.Subject, trace.Context, elapsed);
        if (error != null)
            _report.Issue(error.GetType().FullName!, trace.Context["jobType"] + "/" + trace.Stage + "/" + trace.Context.GetValueOrDefault("zone", "unavailable") + "/" + trace.Context.GetValueOrDefault("geomorph", trace.Subject), error.Message, error.ToString());
        _traceTicks += Stopwatch.GetTimestamp() - end;
    }
    internal static void StructureFinished()
    {
        BindWorld();
        var session = _inspectionSession;
        if (!_generationActive || _structureComplete) return;
        _structureComplete = true;
        _report?.Check("structure", "factory", "observed_complete", "Native FactoryDone returned; objectives and scripts remain unverified.");
        _report?.Event("generation", "factory_done", "factory", new() { ["random"] = RandomState() });
        RecordMapGeneration("factory_done");
        // FactoryDone belongs to the native generation. A cancelled project inspection does not
        // cancel that generation or its terrain capture; its own world guard still applies below.
        if (session == null || !session.AcceptWorld(WorldEpoch, SimulationTick))
        { Export("structure_complete_inspection_cancelled"); return; }
        _inspection = WorldInspection.Capture(session).GetEnumerator();
        Export("structure_complete_inspection_pending");
    }
    internal static void Tick()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentDiagnostics);
        if (_stopped) return;
        if (Input.GetKeyDown(Settings.ExportKey.Value)) Export(_outcome);
        if (Input.GetKeyDown(Settings.EnemyAbilityExportKey.Value)) EnemyAbilityInventory.ExportCurrent();
        if (Input.GetKeyDown(Settings.CombatSamplingKey.Value)) CombatSampling.Toggle();
        // A failed writer is no longer a consumer. Let its recorder release the
        // collection before the no-activity gate would otherwise strand its hooks.
        RecRuntime.Tick();
        if (_stopped) return;
        if (!DevelopmentFrameActivity.Needed && _inspection == null) return;
        BindWorld();
        if (_inspectionSession is { IsClosed: false } current && !current.AcceptWorld(WorldEpoch, SimulationTick))
        {
            CancelInspection("world_epoch_changed");
            ProjectChecks.CancelSourceSnapshot("World changed before diagnostics completed.");
            Export("inspection_cancelled");
        }
        var nativeState = GameStateManager.CurrentStateName;
        if (nativeState != _nativeState)
        {
            var state = nativeState.ToString();
            _report?.Event("lifecycle", "game_state", state, new() { ["previous"] = _state, ["sceneCount"] = SceneManager.sceneCount.ToString() });
            _nativeState = nativeState;
            _state = state;
            Export(_outcome);
        }
        if (_inspection != null)
        {
            var started = Stopwatch.GetTimestamp();
            var budget = Settings.InspectionBudget.Value * Stopwatch.Frequency / 1000.0;
            while (_inspection != null)
            {
                try
                {
                    if (_inspectionSession == null || !_inspectionSession.AcceptWorld(WorldEpoch, SimulationTick))
                    {
                        CancelInspection("world_epoch_changed");
                        ProjectChecks.CancelSourceSnapshot("World changed during inspection.");
                        Export("inspection_cancelled");
                        break;
                    }
                    if (!_inspection.MoveNext())
                    {
                        _inspection.Dispose(); _inspection = null;
                        Export(_inspectionSession.Outcome);
                        break;
                    }
                }
                catch (Exception e)
                {
                    _report?.Issue("diagnostic_error", "world_iterator", e.Message, e.ToString());
                    CancelInspection("iterator_failed");
                    ProjectChecks.CancelSourceSnapshot("World inspection failed.");
                    Export("inspection_failed");
                    break;
                }
                if (Stopwatch.GetTimestamp() - started >= budget) break;
            }
            var ticks = Stopwatch.GetTimestamp() - started;
            _inspectionTicks += ticks; _maxInspectionTicks = Math.Max(_maxInspectionTicks, ticks); _steps++;
        }
        FactoryProgressWatch.Tick(_generationActive && !_structureComplete, _run, _report);
        if (Time.realtimeSinceStartup >= _nextExport) { Export(_outcome, onlyIfChanged: true); _nextExport = Time.realtimeSinceStartup + 30; }
    }
    internal static void Cleanup(string phase, Exception? error = null)
    {
        Safe(() => ExitScanSettle.Cleanup(phase));
        _report?.Event("lifecycle", "level_cleanup_" + phase, "Builder", new() { ["sceneCount"] = SceneManager.sceneCount.ToString(), ["courseNodes"] = AIGraph.AIG_CourseNode.s_allNodes?.Count.ToString() ?? "unavailable" });
        if (phase == "before")
        {
            // The last point at which the world is still the one that generated: the outcome of a generation that was
            // cut off, or its final state, is read now and kept in the report that the later export writes.
            Safe(() => RecordMapGeneration("level_cleanup"));
            _generationActive = false;
            CancelInspection("level_cleanup");
            ProjectChecks.CancelSourceSnapshot("Level cleanup started.");
            PerformanceDiagnostics.CancelWorldInspection();
            // Explicit experiments cannot keep a damage hook alive across a world boundary.
            Safe(() => ExperimentRunner.Cancel("level cleanup"));
            Safe(ExperimentDamage.Stop);
            RecRuntime.LevelEnded(phase);
        }
        if (error != null) _report?.Issue(error.GetType().FullName!, "Builder.OnLevelCleanup", error.Message, error.ToString());
        if (phase != "after") return;
        _cleanupCount++; JobCalls.Clear(); CullingObservations.Clear(); Safe(CombatSampling.Stop);
        _report?.SetMetadata("authoringIteratorActiveAfterCleanup", (_inspection != null).ToString());
        _report?.SetMetadata("performanceWorldScanActiveAfterCleanup", PerformanceDiagnostics.WorldInspectionActive.ToString());
        _report?.Check("retained_world_objects", "all_plugins", "not_checked", "Only this plugin's two scan ownership states are observed; no global retained-reference count is claimed.");
        Export(_structureComplete ? "level_left" : "generation_interrupted");
    }
    internal static void Marker(LG_MarkerSpawner marker, bool success)
    {
        var go = marker.m_spawnedGO;
        _report?.Event("placement", "marker_spawn", marker.m_markerDataBlockID.ToString(), new()
        {
            ["source"] = PathOf(marker.m_producerSource?.transform),
            ["zone"] = Zone(marker.m_zone),
            ["area"] = marker.m_area?.UID.ToString() ?? "unavailable",
            ["seed"] = marker.m_markerInstanceSeed.ToString(),
            ["localPosition"] = Vector(marker.m_localPosition),
            ["localRotation"] = Rotation(marker.m_localRotation),
            ["spawned"] = success.ToString(),
            ["object"] = go == null ? "none" : PathOf(go.transform),
            ["worldPosition"] = go == null ? "unavailable" : Vector(go.transform.position),
            ["worldRotation"] = go == null ? "unavailable" : Rotation(go.transform.rotation)
        });
    }
    internal static void Spawner(LG_PrefabSpawner spawner, GameObject? go)
    {
        _report?.Event("prefab_spawner", "build", PathOf(spawner.transform), new()
        {
            ["prefab"] = spawner.m_prefab == null ? "unavailable" : spawner.m_prefab.name,
            ["zone"] = Zone(spawner.m_zone),
            ["built"] = spawner.m_isBuilt.ToString(),
            ["object"] = go == null ? "none" : PathOf(go.transform),
            ["position"] = Vector(spawner.transform.position),
            ["rotation"] = Rotation(spawner.transform.rotation),
            ["collisionDisabled"] = spawner.m_disableCollision.ToString()
        });
    }
    internal static void Culling(C_Cullable culler, string method, string phase, Exception? error = null)
    {
        if (error != null) _report?.Issue(error.GetType().FullName!, "CullingSystem." + method, error.Message, error.ToString());
        var key = culler.Pointer.ToString("X") + ":" + method + ":" + phase;
        if (CullingObservations.Count >= 10000 || !CullingObservations.Add(key)) return;
        var fields = new Dictionary<string, string> { ["phase"] = phase, ["registered"] = culler.IsRegistered.ToString(), ["cleanupCount"] = _cleanupCount.ToString() };
        fields["identifierScope"] = "native pointer within this run only; not a stable cross-run identity";
        var cluster = culler.TryCast<C_CullingCluster>();
        if (cluster != null)
        {
            fields["renderers"] = cluster.Renderers?.Length.ToString() ?? "unavailable";
            if (cluster.Renderers != null) fields["destroyedRenderers"] = cluster.Renderers.Count(r => r == null).ToString();
        }
        _report?.Event("culling_lifecycle", method, culler.Pointer.ToString("X"), fields);
    }

    /// <summary>One record per C_BuildCullerPortal.Build entry (and one for an exception leaving it): which link of the
    /// chain Build dereferences is missing. Build reads portal.Gate (whose getter calls the expander's GetGate once and
    /// caches it behind m_hasGate), then the gate's m_linksFrom and m_linksTo, each area's m_courseNode and its m_cullNode.
    /// Only fields are read here; get_Gate and GetGate are never called, so the recorded state is the one Build is about
    /// to see. The portal's expander is also searched for the gate component the way GetGate does, to tell "no gate on
    /// the connector" from "gate present but its links unset".</summary>
    internal static void CullerPortal(C_BuildCullerPortal job, string phase, Exception? error = null)
    {
        if (error != null) _report?.Issue(error.GetType().FullName!, "CullingSystem.C_BuildCullerPortal.Build", error.Message, error.ToString());
        var key = "portal:" + job.Pointer.ToString("X") + ":" + phase;
        if (CullingObservations.Count >= 10000 || !CullingObservations.Add(key)) return;
        var portal = job.m_portal;
        var fields = new Dictionary<string, string>
        {
            ["phase"] = phase,
            ["cleanupCount"] = _cleanupCount.ToString(),
            ["identifierScope"] = "native pointer within this run only; not a stable cross-run identity",
            ["portal"] = portal == null ? "null" : "present",
            ["dimension"] = job.m_dimensionIndex.ToString(),
            ["needsNodeFlip"] = job.m_needsNodeFlip.ToString(),
            ["backwardsNode"] = Presence(job.m_backwardsNode),
            ["forwardNode"] = Presence(job.m_forwardNode),
        };
        if (error != null) fields["exception"] = error.GetType().Name;
        if (portal != null)
        {
            fields["hasGate"] = portal.m_hasGate.ToString();
            fields["cullPortal"] = Presence(portal.m_cullPortal);
            fields["nodeA"] = CourseNodeState(portal.m_nodeA);
            fields["nodeB"] = CourseNodeState(portal.m_nodeB);
            fields["cachedGate"] = GateState(portal.m_gate, portal);
            var expander = portal.m_zoneExpander;
            fields["expander"] = expander == null ? "null" : $"{expander.GetIl2CppType().Name}|{expander.name}|path={PathOf(expander.transform)}|coursePortalIsThis={expander.CoursePortal?.Pointer == portal.Pointer}|from={AreaState(expander.m_linksFrom)}|to={AreaState(expander.m_linksTo)}|originalParent={AreaState(expander.m_originalParentArea)}";
            var plug = expander?.TryCast<LG_Plug>();
            if (plug != null)
            {
                var paired = plug.m_pariedWith;
                fields["plugGate"] = GateState(plug.m_gate, portal);
                fields["plugGateInChildren"] = GateState(plug.GetComponentInChildren<LG_Gate>(true), portal);
                fields["paired"] = paired == null ? "null" : $"{paired.name}|path={PathOf(paired.transform)}|gate={GateState(paired.m_gate, portal)}|gateInChildren={GateState(paired.GetComponentInChildren<LG_Gate>(true), portal)}|from={AreaState(paired.m_linksFrom)}|to={AreaState(paired.m_linksTo)}|coursePortal={Presence(paired.CoursePortal)}";
            }
        }
        _report?.Event("culling_portal", "build", job.Pointer.ToString("X"), fields);
    }
    private static string Presence(object? value) => value == null ? "null" : "present";
    private static string AreaState(LG_Area? area)
        => area == null ? "null" : $"{area.name}|geomorph={area.m_geomorph?.name ?? "null"}|courseNode={Presence(area.m_courseNode)}|cullNode={Presence(area.m_courseNode?.m_cullNode)}";
    private static string CourseNodeState(AIGraph.AIG_CourseNode? node)
        => node == null ? "null" : $"area={AreaState(node.m_area)}|cullNode={Presence(node.m_cullNode)}";
    private static string GateState(LG_Gate? gate, AIGraph.AIG_CoursePortal portal)
    {
        if (gate == null) return "null";
        var passage = gate.TryCast<LG_FreePassageGate>();
        return $"{gate.GetIl2CppType().Name}|{gate.name}|type={gate.Type}|from={AreaState(gate.m_linksFrom)}|to={AreaState(gate.m_linksTo)}|coursePortalIsThis={gate.CoursePortal?.Pointer == portal.Pointer}|portalHelper={(passage == null ? "n/a" : Presence(passage.m_portalHelper))}";
    }
    private static void CancelInspection(string why)
    {
        _inspectionSession?.Cancel(_inspectionSession.WorldEpoch == WorldEpoch ? SimulationTick : null, why);
        if (_inspection == null) return;
        var iterator = _inspection; _inspection = null;
        try { iterator.Dispose(); }
        catch (Exception e) { _report?.Issue("diagnostic_error", "world_iterator_dispose", e.Message, e.ToString()); }

    }
    // The periodic export compares against the last queued one, so a quiet report costs neither the deep copy nor the
    // file; every other export is asked for by an event and always runs.
    private static readonly DiagnosticsReport.ExportMemo LastExport = new();

    private static string _lastMapProgress = "";

    /// <summary>Writes Forge Engine's map generator progress (see <see cref="GenerationProgressSummary"/>, read through the Engine's
    /// author read) into this run's report: always as the one `mapGeneration` metadata value, and as a `generation/map_progress`
    /// event when it changed. Without the Engine in Authoring mode, or in a world without a map generation plan, there is nothing to
    /// record; a readback that cannot be summarized is recorded as unreadable, so an in-flight, cut-off or oversized generation is
    /// never silently absent from the report.</summary>
    private static void RecordMapGeneration(string reason)
    {
        var report = _report;
        if (report == null || EngineLookup.Mode != "Authoring") return;
        string line, status;
        try { (status, line) = GenerationProgressSummary.Summarize(EngineLookup.Call<string>("GenerationProgress")); }
        catch (Exception error) { status = "unreadable"; line = "status=unreadable;reason=readback-failed;error=" + error.GetType().Name; }
        if (status == "vanilla") return;
        report.SetMetadata("mapGeneration", line);
        if (line == _lastMapProgress) return;
        _lastMapProgress = line;
        report.Event("generation", "map_progress", reason, new() { ["summary"] = line });
        if (status == "unreadable") report.Issue("map_generation_progress_unreadable", "ForgeDevelopment", line, severity: "warning");
    }

    internal static void Export(string outcome, bool onlyIfChanged = false)
    {
        if (_report == null) return;
        _outcome = outcome;
        var cpu = ForgeCore.DiagnosticCpuBudget.Shared.Snapshot;
        _report.SetMetadata("diagnosticCpuLimitMs", Number(cpu.LimitTicks * 1000d / Stopwatch.Frequency));
        _report.SetMetadata("diagnosticCpuMeasuredMs", Number(cpu.MeasuredTicks * 1000d / Stopwatch.Frequency));
        _report.SetMetadata("diagnosticCpuMaximumFrameMs", Number(cpu.MaximumFrameTicks * 1000d / Stopwatch.Frequency));
        _report.SetMetadata("diagnosticCpuMaximumStepMs", Number(cpu.MaximumStepTicks * 1000d / Stopwatch.Frequency));
        _report.SetMetadata("diagnosticCpuBudgetHits", cpu.BudgetHits.ToString(CultureInfo.InvariantCulture));
        _report.SetMetadata("diagnosticCpuDropped", cpu.Dropped.ToString(CultureInfo.InvariantCulture));
        _report.SetMetadata("diagnosticCpuIncomplete", cpu.Incomplete.ToString());
        _report.SetMetadata("inspectionTotalMs", Number(_inspectionTicks * 1000.0 / Stopwatch.Frequency));
        _report.SetMetadata("inspectionMaximumStepMs", Number(_maxInspectionTicks * 1000.0 / Stopwatch.Frequency));
        _report.SetMetadata("inspectionSteps", _steps.ToString());
        _report.SetMetadata("generationTraceOverheadMs", Number(_traceTicks * 1000.0 / Stopwatch.Frequency));
        foreach (var (stage, cost) in StageCosts)
            _report.SetMetadata("stage:" + stage, $"calls={cost.Calls};totalMs={Number(cost.Total)};maxMs={Number(cost.Maximum)}");
        // A generation that has not finished reports how far the map generator got with every snapshot it writes.
        if (_generationActive && !_structureComplete) RecordMapGeneration("export");
        // Read before the freeze: a change that lands in between is exported once more, never missed.
        var stamp = _report.Stamp();
        if (onlyIfChanged && LastExport.Exported(_report, stamp, outcome) && _writer?.Failure.Length == 0) return;
        if (_writer?.Enqueue(_report, _path, outcome) != true)
        {
            LastExport.Forget();
            _report.Issue("report_queue_full", "ForgeDevelopment", "Report was not queued; previous files do not include this requested snapshot.");
            Plugin.PluginLog.LogError("Forge report queue rejected a snapshot: " + _path);
            return;
        }
        LastExport.Remember(_report, stamp, outcome);
    }
    /// <summary>The shutdown stages with each one recorded into the session as it starts, which is what makes the real
    /// list readable after the process is gone. The recorder closes the session on its own stage, so the stages up to
    /// and including `recorder` are the ones the session can hold; the writer it releases is proved by the segment's
    /// own end.</summary>
    private static (string Stage, Action Action)[] RecordedStages((string Stage, Action Action)[] steps)
    {
        var recorded = new (string, Action)[steps.Length];
        for (var i = 0; i != steps.Length; i++)
        {
            var step = steps[i];
            recorded[i] = (step.Stage, () =>
            {
                RecSession.Write("session", "shutdown_stage", json => json.WriteString("stage", step.Stage));
                step.Action();
            }
            );
        }
        return recorded;
    }

    /// <summary>Set by Unity's OnApplicationQuit before <see cref="Stop"/> runs. A stop caused by startup rollback or a
    /// destroyed component is not an exit: the process keeps running and its hooks must really be removed.</summary>
    internal static volatile bool ProcessExiting;

    internal static void Pause() => Stop(releaseProvider: false);
    internal static void Stop() => Stop(releaseProvider: true);
    private static void Stop(bool releaseProvider)
    {
        if (_stopped) return;
        _stopped = true;
        if (releaseProvider) ExitScanSettle.Stop();
        ExitScanSettle.LifetimeSink = null;
        _generationActive = false;
        var failures = new List<Exception>();
        // A failed native unsubscribe must not prevent exports or writer disposal.
        ShutdownSequence.Run(RecordedStages(new (string, Action)[]
        {
            ("bepinex_listener", () => { if (_listening) BepInEx.Logging.Logger.Listeners.Remove(Listener); _listening = false; }),
            ("unity_listener", () =>
            {
                if (_unityCallback == null) return;
                // This interop event accessor casts Delegate.Remove's null result and throws when
                // removing the last subscriber. Apply the identical unsubscribe with a null check.
                var remaining = Il2CppSystem.Delegate.Remove(Application.s_LogCallbackHandlerThreaded, _unityCallback);
                Application.s_LogCallbackHandlerThreaded = remaining == null ? null : remaining.Cast<Application.LogCallback>();
                _unityCallback = null;
                if (_unityCallbackRoot != 0) IL2CPP.il2cpp_gchandle_free(_unityCallbackRoot);
                _unityCallbackRoot = 0;
            }),
            ("world_inspection", () => CancelInspection("shutdown")),
            ("performance_scan", PerformanceDiagnostics.CancelWorldInspection),
            ("source_snapshot", () => ProjectChecks.CancelSourceSnapshot("Process shutdown.")),
            ("job_state", () => { JobCalls.Clear(); CullingObservations.Clear(); }),
            ("experiment_cancel", () => ExperimentRunner.Cancel("process shutdown")),
            ("experiment_damage", ExperimentDamage.Stop),
            ("material_capture", () => { if (!LevelGuideCaptureMaterials.Stop()) throw new InvalidOperationException("Material capture hook cleanup remains pending."); }),
            ("combat_sampling", CombatSampling.Stop),
            ("final_export", () => Export("process_exit")),

            // Unity ended the application: every record is still written and indexed, but the hooks end with
            // the process and unhooking thousands of them one by one only delays the exit.
            ("recorder", () => { if (releaseProvider) { if (ProcessExiting) RecRuntime.StopForProcessExit(); else RecRuntime.Stop(); } }),
            ("performance_markers", () => { if (releaseProvider) NativeGameTimings.StopMarkers(); }),
            ("shared_native_hooks", () => { if (releaseProvider && !ProcessExiting) DevelopmentHookSafety.StopAll(); }),
            ("material_plan_watch", LevelGuideCaptureMaterials.StopPlanWatch),
            ("report_writer", () => _writer?.Dispose())
        }), (stage, error) =>
        {
            failures.Add(error);
            _report?.Issue("shutdown_error", stage, error.Message, error.ToString());
            Plugin.PluginLog.LogError("Forge shutdown " + stage + " failed: " + error);
        });
        if (failures.Count != 0)
        {
            _stopped = false;
            throw new AggregateException("Development diagnostics cleanup incomplete.", failures);
        }
        _writer = null;
        _world.Reset();
        _report = null;
    }
}
