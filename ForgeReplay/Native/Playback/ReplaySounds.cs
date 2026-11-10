using System.Collections.Generic;
using ForgeAudio;
using UnityEngine;

namespace ForgeReplay;

/// <summary>Every game sound the replay posts goes through here, and each one's ForgeAudio handle is kept, so that all of them can be stopped at once: a jump in time,
/// forward or back, and the end of the replay leave nothing sounding, looping sounds included, and whatever should sound at the new moment is started again from the state
/// (<see cref="EventPresenter.Jumped"/>). Only the replay's own plays are stopped: the game's own sounds (room ambience, music, the same events it posts itself) are not touched. Main thread only.</summary>
internal static class ReplaySounds
{
    /// <summary>The replay's plays that may still sound; finished ones are dropped once this many have gathered.</summary>
    private const int PruneAt = 64;
    private static readonly List<GameSoundHandle> Playing = new();

    /// <summary>While not null, every post (the replay's and the game's own, from <see cref="GameVoices"/>) is added with its time and whether the replay posted it: the probe's trace.</summary>
    internal static List<(float Time, uint Id, bool Own)>? Trace;

    internal static bool Post(string name, Vector3 at)
    {
        // The game's weapon data names can carry a trailing space (PistolMechB3D ); the sound event is named without it.
        if (!AudioKit.TryGameEventId(name.Trim(), out var id)) return false;
        Trace?.Add((Time.realtimeSinceStartup, id, true));
        var handle = AudioKit.PlayGameEvent(id, at);
        if (handle == null) return false;
        if (Playing.Count >= PruneAt) Playing.RemoveAll(static played => !played.IsPlaying);
        Playing.Add(handle);
        return true;
    }

    internal static void StopAll()
    {
        foreach (var handle in Playing) handle.Stop();
        Playing.Clear();
    }
}
