using System;
using System.Diagnostics;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>
/// The pre-navigation build window for solid placed models: their colliders must exist before the level bakes.
///
/// The window is the start of the navigation bake. `LG_Factory` runs its batches in `LG_Factory.BatchName` order
/// (Modules-ASM: `Geomorphs` = 8, `Areas` = 9, `Plugs` = 10, `Gates` = 11, `Markers` = 19, `PrefabSpawners` = 20,
/// `LateGeomorphScan` = 23, `AIGraph_UnityAIGraph` = 25, `PreCulling` = 50 … `PostCulling` = 60,
/// `LateRuntimeOptimizations` = 66, `FixColliders` = 68), and the first job of batch 25 is
/// `LG_BuildUnityGraphJob` (`Build`, `NavmeshDone`). By its first `Build` every room's geometry exists and
/// nothing has baked it yet: the navigation mesh, the culling cells and the late optimisations all read the models
/// this prefix placed. A factory job's `Build` is called again until it reports done, and once per dimension, so the
/// models are placed once per level build of a world and later calls pass.
/// </summary>
internal static class ModelBuildWindow
{
    private static bool _placed;
    private static long _world = long.MinValue;

    /// <summary>The first navigation build of a level build places the level's solid models.</summary>
    internal static bool Open(LG_BuildUnityGraphJob job)
    {
        if (RoomGenerationFailure.Pending) return false;
        long world = MapHost.Session?.WorldEpoch ?? long.MinValue;
        if (_world != world) { _world = world; _placed = false; }
        if (_placed) return true;
        _placed = true;
        var measure = MapLog.Logs(RuntimeLogLevel.Info);
        var startedAt = measure ? Stopwatch.GetTimestamp() : 0;
        var ready = PlaceSolidModels();
        if (measure) MapLog.Record(RuntimeLogLevel.Info, "level.load-work", "Solid-model navigation-window work.",
            RuntimeLogValue.Of("work", "solid-models"), RuntimeLogValue.Of("phase", "generation"),
            RuntimeLogValue.Of("elapsedMs", (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency),
            RuntimeLogValue.Of("completed", ready));
        return ready;
    }

    /// <summary>The level finished building: the next level build opens the window again.</summary>
    internal static void Close() => _placed = false;

    private static bool PlaceSolidModels()
    {
        try
        {
            using var stage = MapModelAssembly.OpenBuildStage(LevelIdentity.Read());
            if (!stage.Required) return true;
            var models = PlacedModelSession.Current ?? throw new InvalidOperationException("model-build-session-unavailable");
            models.Place();
            var published = MapHost.Session?.MapObjects?.PlacedModels;
            if (stage.Complete(assembly => ReferenceEquals(published?.ById(assembly.Model.Id)?.Model, assembly.Model)) is { } refusal)
                throw new InvalidOperationException(refusal);
            return true;
        }
        catch (Exception error)
        {
            MapLog.Contained("model-build-window.place", error);
            RoomGenerationFailure.RecordException(error);
            RoomGenerationFailure.Request();
            return false;
        }
    }
}
