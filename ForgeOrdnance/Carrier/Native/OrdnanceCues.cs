using ForgeAudio;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>The call-ins' sounds: the <c>Carrier/audio</c> folder next to the DLL, loaded once through ForgeAudio. This class only
/// decides when and where a sound plays; the cue settings (gain, range, limits, each sound's minimum gap, its group and ducking) live in
/// <c>audio/cues.forge</c>, and AudioKit logs every cue that cannot load or play by name. Dimension of a sound at a point or on an
/// object is AudioKit's to resolve; looping sounds on objects are AudioKit sustains.</summary>
internal static class OrdnanceCues
{
    private static readonly PlayOptions LocalOptions = new() { IsSelf = true };

    /// <summary>Cue name per <see cref="OrdnanceSound"/>, in enum order.</summary>
    private static readonly string[] SoundCues =
    {
        "ui.key_up", "ui.key_down", "ui.key_left", "ui.key_right", "ui.key_wrong", "ui.code_complete", "ui.call_open", "ui.call_cancel",
        "ui.denied", "ui.click", "ui.slot_select", "ui.pick_confirm", "ui.pick_clear", "beacon.throw", "beacon.land", "beacon.tick",
        "drop.unlatch", "drop.leave", "drop.debris", "turret.lid_blast",
    };

    internal static void Attach(string cue, GameObject owner, int? dimension = null, float start = 0f)
    {
        PartSounds.Carrier?.Play(cue, owner, dimension == null && start <= 0f ? null : new PlayOptions { Dimension = dimension, StartSeconds = start });
    }

    internal static void At(string cue, Vector3 position) => PartSounds.Carrier?.Play(cue, position);

    /// <summary>A sound only the local player hears (2D): key presses, call-in feedback, menu clicks, refusals.</summary>
    internal static void Local(OrdnanceSound sound)
    {
        PartSounds.Carrier?.Play(SoundCues[(int)sound], LocalOptions);
    }

    /// <summary>A world event at <paramref name="position"/> (3D), triggered by each client from its own synchronised state.</summary>
    internal static void World(OrdnanceSound sound, Vector3 position) => At(SoundCues[(int)sound], position);

    /// <summary>A world event that follows <paramref name="owner"/> (3D).</summary>
    internal static void World(OrdnanceSound sound, GameObject owner, int? dimension = null) => Attach(SoundCues[(int)sound], owner, dimension);

    /// <summary>Stops every sound now. Native sentries can survive host takeover; their humming sustains start again by themselves.</summary>
    internal static void Clear() => PartSounds.Carrier?.StopAll();
}

/// <summary>The mod's sound sets, one ForgeAudio library per part: the folder <c>&lt;part&gt;/audio</c> next to the DLL. Each has its
/// own name in ForgeAudio (log lines, replays and the plays sent to other players name it) and its own <c>cues.forge</c>. A set is
/// null before the plugin loads it, when its part failed to load, and after shutdown.</summary>
internal static class PartSounds
{
    internal static AudioLibrary? Carrier, Arsenal, Frames;

    /// <summary>The sound set in the folder of <paramref name="part"/>, named <paramref name="id"/> in ForgeAudio: the carrier's has the
    /// plugin's id, a content part's its own (<see cref="Messages.ArsenalId"/>, <see cref="Messages.FramesId"/>).</summary>
    internal static AudioLibrary Load(string id, string part) => AudioKit.Load(id, System.IO.Path.Combine(Plugin.Folder, part, "audio"));

    internal static void Unload(ref AudioLibrary? sounds)
    {
        sounds?.Dispose();
        sounds = null;
    }

    internal static void Shutdown()
    {
        Unload(ref Carrier);
        Unload(ref Arsenal);
        Unload(ref Frames);
    }
}
