using System;
using BepInEx;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>The Map domain's start: the engine's Load calls it once, in its fixed domain order, while registration is open.</summary>
internal static class MapHost
{
    /// <summary>The domain's ForgeCore owner id (net channel, content reader, screen and input owner).</summary>
    internal const string ModId = "NAinfini.ForgeEngine.Map";
    internal static MapPluginSession? Session { get; private set; }

    internal static void Start(IForgeEngine kernel, RuntimeLogLevel logLevel, BepInEx.Logging.ManualLogSource log)
    {
        MapPluginSession? created = null;
        using var timing = new ForgeCore.StartupTiming(ModId,
            message => { if (ForgeCore.Log.Developer) log.LogInfo(message); if (created != null) MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.Startup, message); }); // BepInEx keeps the whole line; the runtime log caps detail text.
        try
        {
            // Every line of this package is a record in the Map provider's own log (`MapLog`), which the session
            // attaches the moment its registration exists; what a native part reports before that waits in
            // `MapReport` and is written then.
            timing.Mark("package-capture");
            // A refused package's plans are refused with it, at startup and in every later generation's capture.
            void RefusePlans(MapPackageRejection rejection) => RundownPlanAdmission.Refuse(kernel, "plugins/" + rejection.Package,
                rejection.Reason, rejection.Detail + "; path=" + rejection.Path);
            var packages = MapPackageAdmission.Capture(Paths.BepInExRootPath, MapReport.Write, RefusePlans);
            timing.Mark("session");
            MapNet.Open();
            created = MapPluginSession.Start(kernel, logLevel, MapReport.Write, packages,
                () => { timing.Mark("native-declarations"); return MapNativeHooks.Declare(); });
            timing.Mark("session-finalization");
            Session = created;
            ForgeCore.Items.Spawned += NetworkItemSpawnReadback.OnSpawned;
            MapGameHooks.Subscribe();
            // Read-only gate and course-graph readback on levels this generator did not build, for comparison with vanilla.
            CompiledMapGenerator.ObserveOtherLevelNavigation();
            var hooks = created.Hooks;
            timing.Mark("content-and-components");
            packages.ReportRefusals();
            timing.Mark("rundown-menu");
            RundownMenu.Start(MapReport.Write, packages.CapturedPacks, rejection =>
            {
                if (rejection.Package.Length != 0)
                    RundownPlanAdmission.Refuse(kernel, "plugins/" + rejection.Package, rejection.Code,
                        rejection.Detail + "; entry=" + rejection.Entry + "; other=" + rejection.Other);
            }, hooks.Offers(MapHookFeatures.RundownMenuFeature), hooks.Offers(MapHookFeatures.LevelNamesPerLanguageFeature));
            timing.Mark("field-host-and-components");
            var registration = created.MapObjects!.Registration;
            MapFieldHost.Start(registration, hooks);
            var session = created;
            session.ContentStartupRegistration = session.RegisterContentStartup(content =>
            {
                session.CommitContent(packages, content, redo: false);
                LogLoaded(session, packages.FieldEntries);
            }, RundownMenu.CapturedPacks, original =>
            {
                // Plugin.Load has already returned when this callback runs. Roll back its owned session
                // here, while the SDK is still in write-capable startup, before Failed is observed.
                try { session.Dispose(); }
                finally { if (ReferenceEquals(Session, session)) Session = null; }
            });
            // Map's content and its generation plans are participants of every content generation (contract I-RELOAD); in
            // Authoring its optional hook groups go on with the first generation, so a reload never installs a hook.
            MapContentParticipants.Register(session, RefusePlans);
            GameRuntimeBridge.RegisterOptionalHooks("map", MapLazyHooks.EnsureAll);
            GameRuntimeBridge.RegisterOptionalHooks("map-generation", CompiledMapGenerator.EnsureHooks);
            // The frame phase: Map's per-tick native work cannot run in a read-only lifecycle observer.
            timing.Mark("world-leases");
            session.StartPlayerArrivals();
            timing.Mark("frame-phase");
            session.StartFrame();
            timing.Complete();
        }
        catch (Exception original)
        {
            try
            {
                if (created != null)
                {
                    try { created.Dispose(); }
                    catch (Exception cleanup)
                    { DomainSession.PreserveCleanupFailure(original, "ForgeMap.LoadCleanupFailure", cleanup, MapReport.Write); }
                }
            }
            finally { Session = null; }
            throw;
        }
    }

    /// <summary>The one load record: how much authored map content this session accepted. A table whose half was not
    /// built counts nothing.</summary>
    private static void LogLoaded(MapPluginSession session, int fieldEntries)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        var fixedRows = session.Placements?.Loaded ?? default;
        MapLog.Write(RuntimeLogLevel.Info, MapLogCodes.ContentLoaded, new RuntimeLogFacts
        {
            Values = new[]
            {
                RuntimeLogValue.Of("fieldEntries", fieldEntries),
                RuntimeLogValue.Of("fieldsDeclared", ModuleDefinition.DefinitionFields.Count),
                RuntimeLogValue.Of("fixedItems", fixedRows.Items),
                RuntimeLogValue.Of("fixedEnemies", fixedRows.Enemies),
                RuntimeLogValue.Of("fixedSacks", fixedRows.Sacks),
                RuntimeLogValue.Of("fixedLandings", fixedRows.Landings),
                RuntimeLogValue.Of("fixedControllers", fixedRows.Controllers),
                RuntimeLogValue.Of("fixedKeys", fixedRows.Keys),
                RuntimeLogValue.Of("waveAnchors", session.WaveAnchors?.Anchors ?? 0),
                RuntimeLogValue.Of("scanCoverageRows", session.ScanCoverage?.Rows ?? 0),
                RuntimeLogValue.Of("triggerZones", session.TriggerZones?.Authored ?? 0)
            }
        });
    }
}
