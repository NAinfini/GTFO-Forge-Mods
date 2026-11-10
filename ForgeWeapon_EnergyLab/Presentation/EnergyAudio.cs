using System;
using System.Collections.Generic;
using System.Linq;
using ForgeAudio;
using ForgeWeaponEnergyLab.Native;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>
/// The energy weapons' sounds. Every cue is an Ogg Vorbis file of the <c>audio</c> folder beside the DLL, described in
/// <c>audio/cues.forge</c> (buses on ForgeAudio's global categories, near and far distance, loops) and played by ForgeAudio, which
/// owns voices, placement, the game's volume sliders and the shared mix. Draw, holster and reload are not cues: the donor gun's own
/// sounds play with its own animations.
/// </summary>
internal static class EnergyAudio
{
    /// <summary>Every cue the presentation plays. The preload hands them to <see cref="AudioLibrary.Require"/>, which refuses to start without all of them.</summary>
    internal static string[] Cues => EnergyLookNames.Cues;

    private static AudioLibrary? _library;

    private static readonly Dictionary<string, AudioCue> Shipped = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AudioLibrary?> Folders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AudioLibrary> OfDefinition = new(StringComparer.Ordinal);

    internal static AudioLibrary Library => _library ?? throw new InvalidOperationException("Energy sounds were not loaded.");

    /// <summary>Starts decoding the sounds in the background; ForgeAudio logs a missing folder or a bad file.</summary>
    internal static void Load()
    {
        _library = AudioKit.Load(Plugin.Id);
        foreach (var cue in Cues) Shipped[cue] = _library.Cue(cue);
    }

    /// <summary>The shipped cue <paramref name="name"/> (one of <see cref="Cues"/>), found once.</summary>
    internal static AudioCue Cue(string name) => Shipped[name];

    /// <summary>Remembers the definition's own sounds: the <c>audio</c> folder beside its definition file, when it has a <c>cues.forge</c>, loaded once per
    /// folder as the ForgeAudio library <c>EnergyLab.&lt;name of the folder that holds ForgeEnergyLab&gt;</c> (decoding in the background, like the shipped sounds).</summary>
    internal static void BindPack(string definitionId, string definitionFolder)
    {
        if (!Folders.TryGetValue(definitionFolder, out var pack))
        {
            var audio = System.IO.Path.Combine(definitionFolder, AudioKit.FolderName);
            pack = Folders[definitionFolder] = System.IO.File.Exists(System.IO.Path.Combine(audio, "cues.forge"))
                ? AudioKit.Load(PackIdentity(definitionFolder), audio) : null;
        }
        if (pack != null) OfDefinition[definitionId] = pack;
    }

    private static string PackIdentity(string definitionFolder)
    {
        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(definitionFolder)) ?? "") ?? "";
        var name = new string(parent.Select(c => (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-') ? c : '_').ToArray());
        var identity = "EnergyLab." + (name.Length == 0 ? "pack" : name);
        for (var n = 2; Folders.Values.Any(l => l != null && l.Identity == identity); n++) identity = "EnergyLab." + name + "-" + n;
        return identity;
    }

    /// <summary>The library of the definition's own <c>audio</c> folder, or null.</summary>
    internal static AudioLibrary? PackOf(string definitionId) => OfDefinition.TryGetValue(definitionId, out var pack) ? pack : null;

    /// <summary>Plays <paramref name="cue"/> once at a world point. ForgeAudio logs a refused play.</summary>
    internal static void PlayAt(AudioCue cue, Vector3 position, float gain) => cue.Play(position, PlayOptions.OfGain(gain));
}
