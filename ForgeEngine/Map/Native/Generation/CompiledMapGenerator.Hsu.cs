using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ForgeMap.Generation;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    internal const string HsuLifecycleLogCode = "map.generation-hsu";

    private sealed record HsuObservation(string Phase, double ElapsedMs, long Core);

    private sealed class HsuRoomLifecycle(string roomId, LG_Geomorph root, LG_WardenObjective_HSUActivator_Room logic)
    {
        internal readonly string RoomId = roomId;
        internal readonly LG_Geomorph Root = root;
        internal readonly LG_WardenObjective_HSUActivator_Room Logic = logic;
        internal LG_CustomGeomorphBuildJob? Job;
        internal long JobPointer;
        internal bool Dequeued, Returned;
        internal int BuildCalls;
        internal double CoreFirstObservedMs = -1;
        internal readonly List<HsuObservation> Observations = new();
    }

    private static void HsuPrefabCreated(Run run, string roomId, GameObject room)
    {
        foreach (var logic in room.GetComponentsInChildren<iLG_CustomGeomorphLogic>())
            if (logic.TryCast<LG_WardenObjective_HSUActivator_Room>() is { } hsu)
            {
                var entry = new HsuRoomLifecycle(roomId, room.GetComponent<LG_Geomorph>()!, hsu);
                run.HsuRooms.Add(entry); RecordHsuLifecycle(run, entry, "prefab-created");
            }
    }

    private static void HsuRoomSetup(Run run, string roomId, string phase)
    {
        foreach (var entry in run.HsuRooms)
            if (entry.RoomId == roomId) RecordHsuLifecycle(run, entry, phase);
    }

    private sealed partial class Run
    {
        internal readonly List<HsuRoomLifecycle> HsuRooms = new();
        internal HsuRoomLifecycle[] HsuJobIndex = System.Array.Empty<HsuRoomLifecycle>();
        internal HsuRoomLifecycle? ActiveHsu;
    }

    private static void TrackHsuJob(Run run, string roomId, GameObject room, iLG_CustomGeomorphLogic logic, LG_CustomGeomorphBuildJob job)
    {
        if (logic.TryCast<LG_WardenObjective_HSUActivator_Room>() is not { } hsu) return;
        var root = room.GetComponent<LG_Geomorph>() ?? throw new GenerationRefusal("gen-hsu-lifecycle", roomId + "/hsu-room-missing");
        var entry = run.HsuRooms.SingleOrDefault(prior => prior.Logic.Pointer == hsu.Pointer);
        if (entry == null) { entry = new(roomId, root, hsu); run.HsuRooms.Add(entry); }
        if (entry.Job != null) throw new GenerationRefusal("gen-hsu-lifecycle", roomId + "/hsu-job-duplicate");
        entry.Job = job;
        entry.JobPointer = job.Pointer.ToInt64();
        int ordinal = HsuJobOrdinal(run, entry.JobPointer);
        if (ordinal >= 0) throw new GenerationRefusal("gen-hsu-lifecycle", roomId + "/hsu-job-duplicate");
        ordinal = ~ordinal;
        System.Array.Resize(ref run.HsuJobIndex, run.HsuJobIndex.Length + 1);
        System.Array.Copy(run.HsuJobIndex, ordinal, run.HsuJobIndex, ordinal + 1, run.HsuJobIndex.Length - ordinal - 1);
        run.HsuJobIndex[ordinal] = entry;
        RecordHsuLifecycle(run, entry, "queued");
    }

    private static void HsuJobDequeued(Run run, LG_Factory.BatchName batch, LG_FactoryJob job)
    {
        run.ActiveHsu = null;
        if (batch != LG_Factory.BatchName.CustomGeomorphBuildJob) return;
        int ordinal = HsuJobOrdinal(run, job.Pointer.ToInt64());
        if (ordinal < 0) return;
        var entry = run.ActiveHsu = run.HsuJobIndex[ordinal];
        entry.Dequeued = true; RecordHsuLifecycle(run, entry, "build-dequeued");
    }

    private static int HsuJobOrdinal(Run run, long pointer)
    {
        int low = 0, high = run.HsuJobIndex.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2; long candidate = run.HsuJobIndex[middle].JobPointer;
            if (candidate == pointer) return middle;
            if (candidate < pointer) low = middle + 1; else high = middle - 1;
        }
        return ~low;
    }

    private static void HsuBuildStarting(LG_CustomGeomorphBuildJob __instance)
    {
        if (!Live(out var run) || run.Failed) return;
        var entry = run.ActiveHsu;
        if (entry != null && entry.JobPointer == __instance.Pointer.ToInt64() && ++entry.BuildCalls == 1) RecordHsuLifecycle(run, entry, "build-started");
    }

    private static void HsuLateBuildStarting(LG_WardenObjective_HSUActivator_Room __instance)
        => ObserveHsuLateBuild(__instance, "late-build-started");

    private static void HsuLateBuildReturned(LG_WardenObjective_HSUActivator_Room __instance)
        => ObserveHsuLateBuild(__instance, "late-build-returned");

    private static void ObserveHsuLateBuild(LG_WardenObjective_HSUActivator_Room logic, string phase)
    {
        if (!Live(out var run) || run.Failed) return;
        foreach (var entry in run.HsuRooms)
            if (entry.Logic.Pointer == logic.Pointer) RecordHsuLifecycle(run, entry, phase);
    }

    // Native Update (0x1457580) calls GetNewJob only after the current Build returned true,
    // retaining m_currentJob. This observes the existing job; it never executes a second build.
    private static void HsuJobReturned(Run run, LG_FactoryJob? job)
    {
        var entry = run.ActiveHsu; run.ActiveHsu = null;
        if (job == null || entry == null) return;
        if (entry.Dequeued && !entry.Returned && entry.JobPointer == job.Pointer.ToInt64())
        { entry.Returned = true; RecordHsuLifecycle(run, entry, "build-returned"); }
    }

    private static void RequireHsuRegistration(Run run)
    {
        foreach (var entry in run.HsuRooms)
        {
            var root = entry.Root; var logic = entry.Logic;
            // Only WO_ActivateSmallHSU (type 10) reads the activator machine after this batch. In any other objective the
            // room is plain geometry: the native post-culling job already skips a null core, so it is not refused.
            var zone = root.WasCollected ? null : root.m_zone;
            if (zone != null && !zone.WasCollected && zone.Layer != null && !LayerActivatesHsu(zone.Layer.m_type))
            {
                RecordHsuLifecycle(run, entry, "not-objective", -1, "objective-not-activate-small-hsu");
                continue;
            }
            var area = logic.WasCollected ? null : logic.m_roomArea;
            var core = logic.WasCollected ? null : logic.m_core;
            var expected = root.WasCollected ? null : root.m_zone;
            var node = core == null || core.WasCollected ? null : core.SpawnNode;
            int references = 0;
            if (expected != null && !expected.WasCollected && expected.Layer != null && core != null && !core.WasCollected && WardenObjectiveManager.Current != null)
            {
                var objects = WardenObjectiveManager.GetObjectiveGameObjects(expected.Layer.m_type);
                if (objects != null) foreach (var item in objects)
                    if (item != null && !item.WasCollected && item.Pointer == core.gameObject.Pointer) references++;
            }
            string? reason = !entry.Dequeued ? "hsu-build-not-dequeued" : !entry.Returned ? "hsu-build-not-returned" :
                core == null || core.WasCollected ? "hsu-core-missing" :
                expected == null || expected.WasCollected || expected.Layer == null || area == null || area.WasCollected || area.m_geomorph?.Pointer != root.Pointer || area.m_zone?.Pointer != expected.Pointer ? "hsu-room-area-mismatch" :
                node == null || node.WasCollected || node.Pointer != area.m_courseNode?.Pointer || node.m_zone?.Pointer != expected.Pointer ? "hsu-spawn-node-mismatch" :
                core.OriginLayer != expected.Layer.m_type ? "hsu-origin-layer-mismatch" :
                references != 1 ? "hsu-object-not-registered" : null;
            RecordHsuLifecycle(run, entry, reason == null ? "registered" : "refused", references, reason);
            if (reason != null) throw new GenerationRefusal("gen-hsu-lifecycle", entry.RoomId + "/" + reason);
        }
    }

    /// <summary>Whether any objective of the layer's chain is ActivateSmallHSU, read through the game's own lookup.</summary>
    private static bool LayerActivatesHsu(LG_LayerType layer)
    {
        for (int chain = 0; WardenObjectiveManager.TryGetWardenObjectiveDataForLayer(layer, chain, out var data); chain++)
            if (data != null && data.Type == eWardenObjectiveType.ActivateSmallHSU) return true;
        return false;
    }

    private static void ReadHsuObjectiveSelection(Run run)
    {
        foreach (var entry in run.HsuRooms) RecordHsuLifecycle(run, entry, "objective-readback");
    }

    private static void RecordHsuLifecycle(Run run, HsuRoomLifecycle entry, string phase, int references = -1, string? reason = null)
    {
        var core = entry.Logic.WasCollected ? null : entry.Logic.m_core;
        if (core != null && core.WasCollected) core = null;
        var area = entry.Logic.WasCollected ? null : entry.Logic.m_roomArea;
        if (area != null && area.WasCollected) area = null;
        var node = core?.SpawnNode; if (node != null && node.WasCollected) node = null;
        var zone = node?.m_zone; if (zone != null && zone.WasCollected) zone = null;
        var expected = entry.Root.WasCollected ? null : entry.Root.m_zone;
        if (expected != null && expected.WasCollected) expected = null;
        long corePointer = core?.Pointer.ToInt64() ?? 0, areaPointer = area?.Pointer.ToInt64() ?? 0, nodePointer = node?.Pointer.ToInt64() ?? 0;
        int dimension = zone == null ? -1 : (int)zone.DimensionIndex, layer = zone?.Layer == null ? -1 : (int)zone.Layer.m_type, local = zone == null ? -1 : (int)zone.LocalIndex;
        int originLayer = zone?.Layer == null ? -1 : (int)core!.OriginLayer;
        bool registered = core?.IsRegistered ?? false;
        double elapsedMs = (Stopwatch.GetTimestamp() - run.Started) * 1000d / Stopwatch.Frequency;
        // This bounds the first observed assignment; it is not a timestamp of the native field store.
        if (corePointer != 0 && entry.CoreFirstObservedMs < 0) entry.CoreFirstObservedMs = elapsedMs;
        entry.Observations.Add(new(phase, elapsedMs, corePointer));
        run.Record("hsu-" + phase, entry.RoomId, new
        {
            core = corePointer,
            area = areaPointer,
            node = nodePointer,
            dimension,
            layer,
            zone = local,
            originLayer,
            tableReferences = references,
            objectiveRegistered = registered,
            dequeued = entry.Dequeued,
            buildReturned = entry.Returned,
            reason,
            observations = entry.Observations.ToArray(),
            coreFirstObservedMs = entry.CoreFirstObservedMs,
            buildCalls = entry.BuildCalls
        });
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        // Keep the complete identity row and put ordering in a
        // preceding timing row under the same room/code/sequence, rather than losing either record.
        MapLog.Record(RuntimeLogLevel.Info, HsuLifecycleLogCode, "HSU native lifecycle timing observation",
            RuntimeLogValue.Of("room", entry.RoomId), RuntimeLogValue.Of("phase", "timing"), RuntimeLogValue.Of("stage", phase),
            RuntimeLogValue.Of("order", entry.Observations.Count), RuntimeLogValue.Of("elapsedMs", elapsedMs),
            RuntimeLogValue.Of("coreFirstObservedMs", entry.CoreFirstObservedMs), RuntimeLogValue.Of("core", corePointer),
            RuntimeLogValue.Of("buildCalls", entry.BuildCalls), RuntimeLogValue.Of("generation", run.Attempt.Generation),
            RuntimeLogValue.Of("generationElapsedMs", (Stopwatch.GetTimestamp() - (run.Attempt.Active ? run.Attempt.StartedAt : run.Started)) * 1000d / Stopwatch.Frequency));
        MapLog.Record(RuntimeLogLevel.Info, HsuLifecycleLogCode, "HSU native lifecycle observation",
            RuntimeLogValue.Of("room", entry.RoomId), RuntimeLogValue.Of("phase", phase), RuntimeLogValue.Of("core", corePointer),
            RuntimeLogValue.Of("elapsedMs", elapsedMs),
            RuntimeLogValue.Of("area", areaPointer), RuntimeLogValue.Of("node", nodePointer), RuntimeLogValue.Of("dimension", dimension),
            RuntimeLogValue.Of("layer", layer), RuntimeLogValue.Of("zone", local), RuntimeLogValue.Of("originLayer", originLayer),
            RuntimeLogValue.Of("tableReferences", references), RuntimeLogValue.Of("objectiveRegistered", registered), RuntimeLogValue.Of("reason", reason ?? "none"),
            RuntimeLogValue.Of("expectedDimension", expected == null ? -1 : (int)expected.DimensionIndex), RuntimeLogValue.Of("expectedLayer", expected?.Layer == null ? -1 : (int)expected.Layer.m_type),
            RuntimeLogValue.Of("expectedZone", expected == null ? -1 : (int)expected.LocalIndex));
    }
}
