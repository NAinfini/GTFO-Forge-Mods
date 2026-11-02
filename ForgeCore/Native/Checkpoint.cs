using SNetwork;
using UnityEngine;

namespace ForgeCore;

/// <summary>When the game's checkpoint state can be written to. After a reload the game puts the backpack, the ammunition and the
/// players back over several frames (its capture recalls the replicated state); anything a mod writes before that is overwritten.
/// <see cref="Settled"/> is false from the reload event until the game has finished, so a mod that restores its own part of the
/// state (an item it took out of a slot, a weapon it lent) waits on it, then writes once. Main thread only.</summary>
public static class Checkpoint
{
    private static int _reloadedFrame = -1;

    /// <summary>The game is reloading a checkpoint or its capture is still recalling the replicated state.</summary>
    public static bool Recalling => CheckpointManager.IsReloadingCheckpoint || SNet.Capture?.IsRecalling == true;

    /// <summary>No reload is in progress: a frame has passed since the last reload event and the game finished recalling.</summary>
    public static bool Settled => Time.frameCount > _reloadedFrame && !Recalling;

    internal static void NoteReloaded()
    {
        _reloadedFrame = Time.frameCount;
        GameEvents.NoteCheckpointReloaded();
    }
}
