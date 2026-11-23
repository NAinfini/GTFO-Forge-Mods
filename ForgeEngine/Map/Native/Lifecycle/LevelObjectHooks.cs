using System;
using ChainedPuzzles;
using HarmonyLib;
using LevelGeneration;
using Player;
using ForgeCore;

namespace ForgeMap.Native;

// The level-object readbacks. They are installed only through `MapNativeHooks.Types`, the one list the plugin
// hands to the class processor. Each hook only chooses when to read, and every one of them reads the instance
// after the native body returned rather than turning the callback's own arguments into fact values. A hook whose
// bindings no plan subscribes to still reads its subject, because the value rows and the resource tables answer
// from the same tables the facts fill; the read is one property access and one dictionary probe per native change.

// `CP_Bioscan_Core.Master_OnPlayerScanChangedCheckProgress` is the master's own progress entry for one scan. The
// instance's `Master_OnPlayerScanChanged` sits above it, but a group never forwards its scans to the instance (the
// cluster subscribes only their OnPuzzleDone, CP_Cluster_Core.Setup 0x18698c0), so this is the one entry both plain and
// grouped scans pass. A last-priority prefix reports the members the coverage rewrite settled on, before the body
// writes the state the progress belongs to.
[HarmonyPatch(typeof(CP_Bioscan_Core), nameof(CP_Bioscan_Core.Master_OnPlayerScanChangedCheckProgress))]
internal static class ScanProgressReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Prefix(CP_Bioscan_Core __instance, float scanProgress,
        Il2CppSystem.Collections.Generic.List<PlayerAgent> playersInScan)
    {
        if (!Session.IsHost) return;
        // Without a plan on the progress row there is nothing to report to: read nothing from the scan.
        if (MapHost.Session is not { Levels: { ScanProgressObserved: true } } session) return;
        session.Run(session.Levels,
            module => LevelObjectObservation.ScanProgress(__instance, scanProgress, playersInScan, module),
            "level-object observation");
    }
}

// The instance's own state replication callback: the one point where the game records that a scan became
// active or solved. The started, completed and alarm-stopped rows are all read from the instance here; the two
// state structures the callback carries are what tells a solve that just happened from an instance that was
// already solved when this provider attached.
// Subscribed to `ForgeCore.GameHooks.PuzzleState` (see `MapGameHooks`); ForgeCore patches the game method.
internal static class ScanStateReadback
{
    internal static void Changed(ChainedPuzzleInstance instance, pChainedPuzzleState oldState, pChainedPuzzleState newState)
    {
        bool justSolved = !oldState.isSolved && newState.isSolved;
        if (justSolved) MapHost.Session?.ObjectiveRounds?.ScanSolved(instance);
        if (!Session.IsHost) return;
        bool justActivated = oldState.status != eChainedPuzzleStatus.Active && newState.status == eChainedPuzzleStatus.Active;
        bool leftActive = oldState.status == eChainedPuzzleStatus.Active && newState.status != eChainedPuzzleStatus.Active;
        if (MapHost.Session is { } session)
            session.Run(session.Levels, module => LevelObjectObservation.ScanStateChanged(instance, justSolved,
                justActivated, leftActive, module), "level-object observation");
        // The original callback has returned, including its own stop of the puzzle's native wave. Waves Forge
        // started for this puzzle follow the same block switch, after the observation above was published.
        if (justSolved) StopForgeWavesOnComplete(instance);
    }

    internal static void StopForgeWavesOnComplete(ChainedPuzzleInstance instance)
    {
        try
        {
            if (WaveResourceSession.Current is not { } waves || instance == null || instance.WasCollected
                || instance.Data is not { WasCollected: false } data || !data.DisableSurvivalWaveOnComplete) return;
            waves.StopCompletedHost(data.persistentID, "scan-completed");
        }
        catch (Exception error) { MapLog.Contained("alarm-wave.scan-completed-stop", error); }
    }
}

// A resource container's own state callback: the one point where the game records that a locker or a resource
// box changed state — locked, opened, closed, or a player standing close to it.
// Subscribed to `ForgeCore.GameHooks.ResourceContainerState` (see `MapGameHooks`); ForgeCore patches the game method.
internal static class ResourceContainerStateReadback
{
    internal static void Changed(LG_ResourceContainer_Sync container, pResourceContainerItemState newState)
    {
        if (MapHost.Session is { } session)
            session.Run(session.Levels,
                module => LevelObjectObservation.ContainerStateChanged(container, newState, module),
                "level-object observation");
        LevelInstanceSweep.Run();
    }
}

// A level item's own state callback: the one point where the game records that an item went into a player's
// hands or came back to the floor. The state carries the player the game itself replicated.
// Subscribed to `ForgeCore.GameHooks.PickupItemState` (see `MapGameHooks`); ForgeCore patches the game method.
internal static class ItemPickupReadback
{
    internal static void Changed(LG_PickupItem_Sync item, pPickupItemState newState, bool isRecall)
    {
        // A recall is the game replaying buffered state (checkpoint, restart, a late join): the item did not change
        // hands now, so it publishes no pickup or put-back fact (contract M16); its presentation is still released.
        if (MapHost.Session is { } session)
        {
            session.SpawnRules?.Flush();
            session.Run(session.Levels, module => LevelObjectObservation.ItemStateChanged(item, newState, isRecall, module),
                "level-object observation");
        }
        LevelInstanceSweep.Run();
    }
}

/// <summary>Records every item the game spawns in the level-instance table. It subscribes to ForgeCore's
/// <c>Items.Spawned</c> (the one patch of <c>ItemReplicationManager.OnItemSpawn</c>), so it declares no hook of its own.</summary>
internal static class NetworkItemSpawnReadback
{
    internal static void OnSpawned(pItemSpawnData _, ItemReplicator replicator)
    {
        if (MapHost.Session is { } session && session.Hooks.Offers(MapHookFeatures.LevelItemInstancesFeature))
            session.Run(session.Levels, module => LevelInstanceObservation.Item(replicator, module), "item instance observation");
    }
}

[HarmonyPatch(typeof(SNet_ReplicationManager_Item), "OnDeSpawn")]
internal static class NetworkItemDespawnReadback
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(ItemReplicator replicator)
    {
        if (MapHost.Session is { } session && session.Hooks.Offers(MapHookFeatures.LevelItemInstancesFeature))
            session.Run(session.Levels, module => LevelInstanceObservation.Despawn(replicator, module), "item instance cleanup");
    }
}

/// <summary>Releases the local presentations (item and container signs) whose observed receiver no longer exists.
/// It runs after the state callbacks that can replace an item's view or a container's registration; a despawn
/// releases its own presentation through the instance table, and the world's end clears the table.</summary>
internal static class LevelInstanceSweep
{
    private static readonly Action<LevelObjectModule> Sweep = static module => module.SweepInstancePresentations();

    internal static void Run()
    {
        if (MapHost.Session is { Levels: { HasInstancePresentations: true } } session
            && session.Hooks.Offers(MapHookFeatures.LevelItemInstancesFeature))
            session.Run(session.Levels, Sweep, "item marker cleanup");
    }
}
