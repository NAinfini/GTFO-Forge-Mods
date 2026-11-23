using System;
using System.Collections.Generic;
using ForgeAudio;
using UnityEngine;

namespace ForgeEngine.Presentation.Native;

/// <summary>ForgeAudio's game sound events for the game-sound rows: the emitters, their reuse, the playing ids and
/// the stop are the library's (<c>AudioKit.PlayGameEvent</c>, <c>StopGameEventAtPoints</c>, <c>ClearGameSoundPoints</c>); what stays here is which world
/// objects a sound filter names, and the refusals the rows turn into codes.</summary>
internal static class LibraryPointSounds
{
    internal static void Play(uint sound, Vector3 position)
    {
        if (AudioKit.PlayGameEvent(sound, position) == null) throw new InvalidOperationException("The game refused the sound event " + sound + " at the point.");
    }

    internal static Action PlayInstance(uint sound, Vector3? position, string? filter)
    {
        var handle = !string.IsNullOrEmpty(filter) ? PlayAtObjects(sound, filter)
            : position is { } at ? AudioKit.PlayGameEvent(sound, at)
            : AudioKit.PlayGameEvent(sound);
        return (handle ?? throw new InvalidOperationException("The game refused the sound event " + sound + ".")).Stop;
    }

    internal static int Stop(uint sound)
        => AudioKit.StopGameEventAtPoints(sound) is var reached and >= 0 ? reached : throw new InvalidOperationException("The stop event " + sound + " could not be posted at the points.");

    internal static void Clear() => AudioKit.ClearGameSoundPoints();

    private static GameSoundHandle? PlayAtObjects(uint sound, string filter)
    {
        var objects = WorldEventManager.WorldEventObjectResults;
        var count = WorldEventManager.GetWorldEventObjectsNonAlloc(filter, objects);
        if (count >= objects.Length) throw new InvalidOperationException("Sound object filter exceeds query capacity.");
        if (count == 0) throw new InvalidOperationException("No matching sound object.");
        var points = new List<Vector3>(count);
        for (var index = 0; index < count; index++) points.Add(objects[index].transform.position);
        return AudioKit.PlayGameEvent(sound, points);
    }
}

internal static class LibraryFollowSounds
{
    internal static Action? Play(uint sound, GameObject target) => AudioKit.PlayGameEvent(sound, target) is { } handle ? handle.Stop : null;
}
