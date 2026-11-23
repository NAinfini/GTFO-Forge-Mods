using System;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEngine.Presentation.Native;

/// <summary>The outcome of one custom-sound play on this client: the refusal code, or the action that stops exactly
/// this playback and the check that is true while it is still heard.</summary>
internal readonly record struct CustomSoundStart(string? Code, Action? Stop, Func<bool>? Sounding = null);

/// <summary>The world's custom-sound player, which hands the sounds to the ForgeAudio library and plays them through it; a request for a sound that did not load is answered with its refusal code, never played some other way. The sounds it plays are <see cref="CustomSoundLibrary.Current"/>.</summary>
internal static class CustomSounds
{
    internal static ForgeAudioSounds? Live { get; set; }
}

/// <summary>Custom sounds for the packages above this one: a sound the author chose for something that is not a behavior
/// row (an enemy's native sound event, say). The same player, the same refusal codes.</summary>
internal static class CustomSoundPlayback
{
    /// <summary>The refusal code a play of <paramref name="resourceId"/> (an id, or the revision <see cref="CustomSoundLibrary.Require"/> found)
    /// would get, or null when it can play.</summary>
    public static string? Refusal(string resourceId) => CustomSounds.Live is { } player ? player.Refusal(resourceId) : ResultCode.AudioEngineUnavailable;

    /// <summary>Plays the sound attached to <paramref name="follow"/>. Returns the refusal code, or null when it started;
    /// <paramref name="sounding"/> is then true while this play is still heard.</summary>
    public static string? Play(string resourceId, GameObject follow, out Func<bool>? sounding) => Start(resourceId, null, follow, out sounding);

    /// <summary>Plays the sound at a world point. Returns the refusal code, or null when it started;
    /// <paramref name="sounding"/> is then true while this play is still heard.</summary>
    public static string? Play(string resourceId, Vector3 point, out Func<bool>? sounding) => Start(resourceId, point, null, out sounding);

    private static string? Start(string resourceId, Vector3? point, GameObject? follow, out Func<bool>? sounding)
    {
        sounding = null;
        if (CustomSounds.Live is not { } player) return ResultCode.AudioEngineUnavailable;
        var start = player.Play(resourceId, point, follow);
        sounding = start.Sounding;
        return start.Code;
    }
}
