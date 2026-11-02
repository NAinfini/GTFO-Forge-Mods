using ChainedPuzzles;
using LevelGeneration;

namespace ForgeCore;

/// <summary>Puts level objects in a given state through the game's own state handlers. With <c>instant</c> the handler is told the state
/// arrives as a drop-in or a recall, which is how the game applies a state to a player who joins late: the object ends in the state with no
/// opening animation, sound or effect (a jump or a checkpoint restore). Without it the handler runs as for a live change, with the game's
/// animation, sound and effect. Nothing is replicated and no game rule runs (no unlocking, no waves, no objectives); the state is only shown.
/// Main thread only.</summary>
public static class WorldStates
{
    public static void Door(LG_SecurityDoor door, eDoorStatus status, bool instant)
        => door.OnSyncDoorStatusChange(DoorState(status), instant);

    public static void Door(LG_WeakDoor door, eDoorStatus status, bool instant)
        => door.OnSyncDoorStateChange(DoorState(status), instant);

    public static void Generator(LG_PowerGenerator_Core generator, ePowerGeneratorStatus status, bool instant)
        => generator.OnStateChange(new pPowerGeneratorState { status = status }, new pPowerGeneratorState { status = status }, instant);

    public static void Item(LG_PickupItem_Sync item, ePickupItemStatus status, bool instant)
    {
        var state = item.GetCurrentState();
        state.status = status;
        item.OnStateChange(item.GetCurrentState(), state, instant);
    }

    public static void Puzzle(ChainedPuzzleInstance puzzle, eChainedPuzzleStatus status, bool instant)
    {
        var state = new pChainedPuzzleState { status = status, isActive = status != eChainedPuzzleStatus.Solved, isSolved = status == eChainedPuzzleStatus.Solved };
        puzzle.OnStateChange(state, state, instant);
    }

    /// <summary>A bioscan at <paramref name="status"/> with <paramref name="progress"/> (0 to 1), nobody inside it: the scan's own sync handler
    /// shows its ring, sound and finish.</summary>
    public static void Scan(CP_Bioscan_Core scan, eBioscanStatus status, float progress, bool instant)
        => scan.OnSyncStateChange(status, progress, NoPlayers ??= new(), 0, NoItems ??= new(0), instant);

    /// <summary>A supply box or locker at <paramref name="status"/>: its lid, light and sound.</summary>
    public static void Container(LG_ResourceContainer_Sync container, eResourceContainerStatus status, bool instant)
    {
        var state = new pResourceContainerItemState { status = status };
        container.OnStateChange(state, state, instant);
    }

    /// <summary>A reactor at <paramref name="status"/> with <paramref name="progress"/> (0 to 1) of its current stage.</summary>
    public static void Reactor(LG_WardenObjective_Reactor reactor, eReactorStatus status, float progress, bool instant)
    {
        var state = new pReactorState { status = status, stateProgress = progress };
        reactor.OnStateChange(state, state, instant);
    }

    private static Il2CppSystem.Collections.Generic.List<Player.PlayerAgent>? NoPlayers;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<bool>? NoItems;

    private static pDoorState DoorState(eDoorStatus status)
    {
        var open = status is eDoorStatus.Open or eDoorStatus.Opening or eDoorStatus.Destroyed;
        return new pDoorState { status = status, animProgress = open ? 1f : 0f, hasBeenOpenedDuringGame = open };
    }
}
