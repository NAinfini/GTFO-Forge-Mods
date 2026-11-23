using GTFO.API;

namespace ForgeEngine.GameBindings;

/// <summary>Level lifecycle through GTFO-API instead of the host's own Harmony patches. Trigger points, verified
/// against the shipped GTFO-API's own patch set: <c>OnBuildStart</c> and <c>OnBuildDone</c> are postfixes of
/// <c>LevelGeneration.Builder.Build</c> and <c>BuildDone</c>; the start is the one the host acts on (it needs the new
/// world before the game builds into it), while build-done and <c>OnFactoryDone</c> are only published as upstream events
/// for packages that wait on a finished build; <c>OnEnterLevel</c> reaches the API through
/// <c>RundownManager.OnExpeditionGameplayStarted</c> → <c>EventAPI.OnExpeditionStarted</c> and fires once the player can
/// move; <c>OnLevelCleanup</c> is the postfix of <c>Global.OnLevelCleanup</c>. Checkpoint save and reload are the two
/// <c>EventAPI</c> events over <c>CheckpointManager.OnStateChange</c>: <c>OnCheckpointReached</c> fires for a state
/// change that is not a recall whose door lock position moved, <c>OnCheckpointReloaded</c> for a recall of a reloading
/// checkpoint. The host binds these points rather than <c>EventAPI.OnGameStateChanged</c>: a state name says where the
/// game is, not that a world was built, that the player can move or that a checkpoint was taken, and the previous raw
/// <c>GameStateManager.DoChangeState</c> patch was removed for that reason rather than for lack of an event. GTFO-API
/// exposes no host-migration lifecycle; the narrow SNet start/capture/recall/failure patches and the completion subscription therefore
/// live on <see cref="GameRuntimeBridge"/>, not in this level/checkpoint subscription set.</summary>
internal static class LevelLifecycle
{
    internal static void Subscribe()
    {
        // Claim the same verified installation before later packages/probes can inspect the target.
        FactoryJobs.Prepare();
        RuntimeUpstreamEvents.DataReady = GameData.GameDataInit.IsLoaded;
        GameDataAPI.OnGameDataInitialized += GameDataReady;
        LevelAPI.OnBuildDone += BuildDone;
        LevelAPI.OnFactoryDone += FactoryDone;
        LevelAPI.OnBuildStart += BuildStarted;
        LevelAPI.OnEnterLevel += LevelEntered;
        LevelAPI.OnLevelCleanup += LevelCleanedUp;
        EventAPI.OnCheckpointReached += CheckpointReached;
        EventAPI.OnCheckpointReloaded += CheckpointReloaded;
    }

    internal static void Unsubscribe()
    {
        RuntimeUpstreamEvents.Stop();
        GameDataAPI.OnGameDataInitialized -= GameDataReady;
        LevelAPI.OnBuildDone -= BuildDone;
        LevelAPI.OnFactoryDone -= FactoryDone;
        LevelAPI.OnBuildStart -= BuildStarted;
        LevelAPI.OnEnterLevel -= LevelEntered;
        LevelAPI.OnLevelCleanup -= LevelCleanedUp;
        EventAPI.OnCheckpointReached -= CheckpointReached;
        EventAPI.OnCheckpointReloaded -= CheckpointReloaded;
    }

    private static void BuildStarted() => Forward(GameRuntimeBridge.BeginGeneration, RuntimeUpstreamKind.BuildStarted);
    private static void BuildDone() => Publish(RuntimeUpstreamKind.BuildDone);
    private static void FactoryDone() => Publish(RuntimeUpstreamKind.FactoryDone);
    private static void LevelEntered() => Forward(GameRuntimeBridge.EnterLevel, RuntimeUpstreamKind.LevelEntered);
    private static void LevelCleanedUp() => Forward(GameRuntimeBridge.LeaveLevel, RuntimeUpstreamKind.LevelCleanup);
    private static void CheckpointReached() => Forward(GameRuntimeBridge.CaptureCheckpoint, RuntimeUpstreamKind.CheckpointReached);
    private static void CheckpointReloaded() => Forward(GameRuntimeBridge.RestoreCheckpoint, RuntimeUpstreamKind.CheckpointReloaded);
    // A later data-initialized event (somebody re-initialized the tables) checks Forge's rows against the ledger first.
    private static void GameDataReady() => Forward(GameRuntimeBridge.DataTablesInitialized, RuntimeUpstreamKind.GameDataReady);
    private static void Publish(RuntimeUpstreamKind kind)
        => GameRuntimeBridge.Guard(() => RuntimeUpstreamEvents.Publish(kind));
    private static void Forward(System.Action boundary, RuntimeUpstreamKind kind)
    {
        GameRuntimeBridge.Guard(() =>
        {
            boundary();
            RuntimeUpstreamEvents.Publish(kind);
        });
    }
}
