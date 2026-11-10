using System;
using System.Collections.Generic;
using ForgeAudio;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The sound events the game's own scripts post while a replay plays (ForgeAudio's <see cref="AudioKit.ObserveGameEvents"/> tells
/// of them; the presenter's own <see cref="AudioKit.PostGameEvent(uint, Vector3)"/> posts are not among them). A puppet's hit reaction voices
/// the enemy's hurt by itself for some enemies, so the presenter asks here whether the game just played a voice before it plays its own.
/// Watching runs only between <see cref="Begin"/> and <see cref="End"/>. Main thread only.</summary>
internal static class GameVoices
{
    private static readonly Dictionary<uint, int> LastFrame = new();
    private static IDisposable? _watch;

    internal static void Begin()
    {
        LastFrame.Clear();
        End();
        _watch = AudioKit.ObserveGameEvents(Count);
        if (_watch == null) Plugin.Logger.Error("The game's own sound posts cannot be watched, so a voice may play twice.");
    }

    internal static void End() { _watch?.Dispose(); _watch = null; LastFrame.Clear(); }

    /// <summary>Whether the game itself posted event <paramref name="id"/> in this frame or one of the <paramref name="frames"/> before.</summary>
    internal static bool PostedWithin(uint id, int frames) => LastFrame.TryGetValue(id, out var frame) && Time.frameCount - frame <= frames;

    private static List<uint>? _caught;

    /// <summary>From now until it is called with null, every game post is also added to <paramref name="into"/>.</summary>
    internal static void Catch(List<uint>? into) => _caught = into;

    private static void Count(uint id)
    {
        LastFrame[id] = Time.frameCount;
        ReplaySounds.Trace?.Add((Time.realtimeSinceStartup, id, false));
        _caught?.Add(id);
    }
}
