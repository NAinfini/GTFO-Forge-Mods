using System;
using ForgeAudio;
using ForgeWeaponEnergyLab.Native;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>
/// The energy weapons' sounds. Every cue is an Ogg Vorbis file of the <c>audio</c> folder beside the DLL, described in
/// <c>audio/cues.json</c> (buses on ForgeAudio's global categories, near and far distance, loops) and played by ForgeAudio, which
/// owns voices, placement, the game's volume sliders and the shared mix. Draw, holster and reload are not cues: the donor gun's own
/// sounds play with its own animations.
/// </summary>
internal static class EnergyAudio
{
    /// <summary>Every cue the presentation plays. The preload hands them to <see cref="AudioLibrary.Require"/>, which refuses to start without all of them.</summary>
    internal static readonly string[] Cues =
    {
        "beam-start", "beam-loop", "beam-stop", "beam-hit",
        "arc-fire", "arc-loop", "arc-hit",
        "plasma-charge-rise", "plasma-charge-loop", "plasma-charge-full", "plasma-charge-cancel",
        "orb-fire", "orb-fly-loop", "shock-fly-loop", "orb-explode",
        "shock-fire", "shock-fade", "shock-hit", "shock-pulse",
        "blast-fire", "blast-cycle", "blast-explode",
        "disc-fire", "disc-fly-loop", "disc-bounce", "disc-drop",
        "flame-start", "flame-loop", "flame-stop", "flame-hit", "flame-crackle",
        "gravity-fire", "gravity-fly-loop", "gravity-open", "gravity-close", "gravity-field-loop",
    };

    private static AudioLibrary? _library;

    internal static AudioLibrary Library => _library ?? throw new InvalidOperationException("Energy sounds were not loaded.");

    /// <summary>Starts decoding the sounds in the background; ForgeAudio logs a missing folder or a bad file.</summary>
    internal static void Load() => _library = AudioKit.Load(Plugin.Id);

    /// <summary>True once every file has decoded or failed.</summary>
    internal static bool Loaded => Library.IsLoaded;

    /// <summary>Plays <paramref name="cue"/> once at a world point. ForgeAudio logs a refused play.</summary>
    internal static void PlayAt(string cue, Vector3 position, float gain) => Library.Play(cue, position, PlayOptions.OfGain(gain));
}
