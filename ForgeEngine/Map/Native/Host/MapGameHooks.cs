using ForgeCore;

namespace ForgeMap.Native;

/// <summary>The game methods Map reads that ForgeCore patches once for every package (<see cref="GameHooks"/>). Map declares no
/// Harmony class for them: it subscribes here, at plugin load, and each readback keeps its own rules (host-only, session present).
/// A subscriber that throws is one ForgeCore error line and the other subscribers still run.</summary>
internal static class MapGameHooks
{
    internal static void Subscribe()
    {
        GameHooks.WeakDoorState.After((call, _) => WeakDoorStateReadback.Changed(call.Door, call.State));
        GameHooks.NoiseMade.After(WeakDoorNoiseReadback.Completed);
        GameHooks.TerminalCommand.Before(call => TerminalCommandReadbackFixed.Received(call.Interpreter, call.Command, call.Line));
        GameHooks.ReactorState.After((call, ran) => ReactorWaveReadback.Changed(call.Reactor, call.Old, call.New, call.Dropin, ran));
        GameHooks.ReactorMessage.Before(call => { if (MapHost.Session?.ObjectiveRounds?.ReactorMessage(call.Reactor) is { } text) call.Message = text; });
        GameHooks.PuzzleState.After((call, _) => ScanStateReadback.Changed(call.Puzzle, call.Old, call.New));
        GameHooks.ResourceContainerState.After((call, _) => ResourceContainerStateReadback.Changed(call.Container, call.New));
        GameHooks.PickupItemState.After((call, _) => ItemPickupReadback.Changed(call.Item, call.New, call.Recall));
        GameHooks.GeneratorState.After((call, _) => GeneratorCellReadback.Changed(call.Generator, call.New));
        GameHooks.ItemSpawned.After((call, _) => CarryItemSpawned.Spawned(call));
        GameHooks.PrefabBuilt.Before(call => RoomRecipeHost.CaptureSources(call.Spawner));
        GameHooks.PrefabBuilt.After((call, _) => RoomRecipeSpatialReadback.Built(call.Spawner, call.Built!));
        GameHooks.MarkerSpawned.After((call, _) => MarkerOccupancyObserved.Observed(call));
        GameHooks.SurvivalWaveStarted.Before(SurvivalWaveAnchorRewrite.Rewrite);
        GameHooks.ExpeditionEnded.After(ExpeditionEndedReadback.Ended);
        GameHooks.PlayerDespawned.After((_, _) => MapHost.Session?.PlayersChanged());
        GameHooks.LevelCleanup.After((_, _) => RoomRecipeHost.Reset());
        GameHooks.LevelCleanup.After((_, _) => DoorOrientationObservation.Clear());
        GameHooks.LevelCleanup.After((_, _) => { CompiledMapGenerator.Clear(); GeneratedRoomRegistry.Clear(); RoomGenerationFailure.Reset(); });
        GameHooks.LevelCleanup.After((_, _) => VanillaWorldObjects.Clear());
        GameHooks.LevelCleanup.After((_, _) => ShuttleboxRegistry.Reset());
        GameHooks.LevelCleanup.After((_, _) => MapHost.Session?.ObjectiveRounds?.LevelEnded());
        GameHooks.EnemySyncSpawned.After((call, _) => FixedEnemySpawnInitialized.Spawned(call.Sync, call.Data));
        GameHooks.ProgressionObjectiveSet.After((call, _) => MapHost.Session?.ObjectiveText?.PanelChanged(call.Panel));
        GameHooks.ProgressionObjectiveRemoved.After((call, _) => MapHost.Session?.ObjectiveText?.PanelChanged(call.Panel));
        GameHooks.ObjectivesCleaned.After((panel, _) => MapHost.Session?.ObjectiveText?.PanelCleaned(panel));
        GameHooks.ExpeditionWindowVisible.After((call, _) => BriefingSectorIcons.Shown(call.Window, call.Visible));
        // GTFO-API patches LG_Factory.FactoryDone; its postfix raises this after the engine's own subscriber (LevelLifecycle).
        GTFO.API.LevelAPI.OnFactoryDone += FactoryDoneHooks.Done;
    }
}
