using System;
using System.Collections.Generic;
using ForgeMap.Generation;
using LevelGeneration;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    // Runtime has already changed the world epoch before publishing this upstream observation.
    // GTFO-API owns Builder.Build's physical hook; a second native patch is neither needed nor allowed.
    internal static void BeginGeneration() => Begin();

    // Native GetNewJob (RVA 0x1455220) crosses empty batches, then dequeues or
    // calls FactoryDone. Retain its three seed debugger snapshots and native
    // NextBatch (including objective before/after callbacks), but check the
    // rejection after Activate returns, before another job or a success callback.
    // FactoryDone has existing Map hooks; it is deliberately not a new shared target.
    private static bool GetCompiledJob(LG_Factory __instance, ref bool __result)
    {
        if (!Live(out var run)) return true;
        __result = false;
        try
        {
            HsuJobReturned(run, __instance.m_currentJob);
            while (!run.Failed)
            {
                var batch = __instance.m_currentBatch
                    ?? throw new ForgeMap.Generation.GenerationRefusal("gen-stage-invalid", run.Level + "/current-batch");
                if (batch.Jobs.Count != 0)
                {
                    __instance.m_currentJob = batch.Jobs.Dequeue(); run.JobsDequeued++;
                    HsuJobDequeued(run, __instance.m_currentBatchName, __instance.m_currentJob); __result = true; return false;
                }
                // NextBatch publishes the previous batch's objective callbacks before activating the next.
                // A queue receipt is insufficient: reject a missing HSU before those callbacks consume it.
                bool hsuBarrier = __instance.m_currentBatchName == LG_Factory.BatchName.CustomGeomorphBuildJob;
                if (__instance.m_currentBatchName == LG_Factory.BatchName.Distribution) CompleteBulkheadKeys(run);
                if (hsuBarrier) RequireHsuRegistration(run);
                string name = __instance.m_currentBatchName.ToString();
                Builder.BuildSeedRandom.SeedDebugger.Log(name);
                Builder.HostIDSeedRandom.SeedDebugger.Log(name);
                Builder.SessionSeedRandom.SeedDebugger.Log(name);
                bool next = __instance.NextBatch();
                run.BatchAdvances++;
                if (run.Failed) break;
                if (hsuBarrier) ReadHsuObjectiveSelection(run);
                if (next) continue;
                __instance.m_currentJob = null;
                run.Record("factory-queue", run.Level, new
                {
                    jobsDequeued = run.JobsDequeued,
                    batchAdvances = run.BatchAdvances,
                    nativeNextBatch = true,
                    nativeFactoryDoneRequested = true
                });
                __instance.FactoryDone();
                EnsureFirstAreaNavigable(run);
                RecordNavigationGraph(run);
                return false;
            }
        }
        catch (Exception error) { Refuse(run, error); }
        __instance.m_currentJob = null;
        if (!run.QueueStopped)
        {
            run.QueueStopped = true;
            run.Record("factory-queue-refused", run.Level, new
            {
                jobsDequeued = run.JobsDequeued,
                batchAdvances = run.BatchAdvances,
                nativeFactoryDoneRequested = false
            });
        }
        return false;
    }

    private static void BatchActivated(LG_FactoryBatch __instance)
    {
        CurrentBatch = __instance.m_batchName;
        if (!Live(out var run) || run.Failed) return;
        try
        {
            if (__instance.m_batchName == LG_Factory.BatchName.LoadResourcesShards_Level)
                LoadFrozenResources(run, __instance);
            if (__instance.m_batchName == LG_Factory.BatchName.PrepareZones) { RecordZoneSources(run, "PrepareZones"); ReleaseUnpairedRootSources(run); }
            if (__instance.m_batchName is LG_Factory.BatchName.Gates or LG_Factory.BatchName.AreaNames) RecordZoneSources(run, __instance.m_batchName.ToString());
            if (__instance.m_batchName == LG_Factory.BatchName.TerminalDistribution) ZonePlacementReport.Snapshot(run.Epoch);
            if (__instance.m_batchName == LG_Factory.BatchName.SetupFloor)
            {
                var retained = new List<LG_FactoryJob>(); LG_SetupFloor? setupFloor = null; int count = 0;
                foreach (var job in __instance.Jobs)
                    if (job.TryCast<LG_SetupFloor>() is { } setup) { setupFloor = setup; count++; }
                    else retained.Add(job);
                __instance.Jobs.Clear(); foreach (var job in retained) __instance.Jobs.Enqueue(job);
                if (count != 1) throw new GenerationRefusal("gen-stage-invalid", run.Level + "/setup-floor-job");
                BootstrapFloor(run, setupFloor!);
                if (run.Failed) return;
                run.Record("floor-bootstrap", run.Level, new
                {
                    ownedSetupJobs = count,
                    retainedJobs = retained.Count,
                    phase = "before-native-dequeue",
                    nativeSetupFloorExecuted = false
                });
            }
            // Activate is called for empty batches too, before any dequeue/Build.
            BatchStarted(__instance.m_batchName, true);
            if (__instance.m_batchName == LG_Factory.BatchName.Plugs) run.LinkEnds = LinkPlugs(run);
        }
        catch (Exception error) { Refuse(run, error); }
    }
}
