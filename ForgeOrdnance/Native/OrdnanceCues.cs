using ForgeAudio;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>Ordnance's sounds: the <c>audio</c> folder next to the DLL, loaded once through ForgeAudio. This class only
/// decides when and where a sound plays; the cue settings (gain, range, limits, each sound's minimum gap, its group and ducking) live in
/// <c>audio/cues.forge</c>, and AudioKit logs every cue that cannot load or play by name. Dimension of a sound at a point or on an
/// object is AudioKit's to resolve; looping sounds on objects are AudioKit sustains.</summary>
internal static class OrdnanceCues
{
    private static AudioLibrary? _sounds;
    private static readonly PlayOptions LocalOptions = new() { IsSelf = true };

    /// <summary>Cue name per <see cref="OrdnanceSound"/>, in enum order.</summary>
    private static readonly string[] SoundCues =
    {
        "ui.key_up", "ui.key_down", "ui.key_left", "ui.key_right", "ui.key_wrong", "ui.code_complete", "ui.call_open", "ui.call_cancel",
        "ui.denied", "ui.click", "ui.slot_select", "ui.pick_confirm", "ui.pick_clear", "beacon.throw", "beacon.land", "beacon.tick",
        "drop.unlatch", "drop.leave", "drop.debris", "turret.lid_blast",
    };

    internal static void Load()
    {
        _sounds ??= AudioKit.Load(Plugin.Id);
    }

    internal static void Attach(string cue, GameObject owner, int? dimension = null, float start = 0f)
    {
        _sounds?.Play(cue, owner, dimension == null && start <= 0f ? null : new PlayOptions { Dimension = dimension, StartSeconds = start });
    }

    internal static void At(string cue, Vector3 position) => _sounds?.Play(cue, position);

    /// <summary>The sounds, for the calls that need the library itself (a sustained loop); null before <see cref="Load"/>.</summary>
    internal static AudioLibrary? Sounds => _sounds;

    /// <summary>A sound only the local player hears (2D): key presses, call-in feedback, menu clicks, refusals.</summary>
    internal static void Local(OrdnanceSound sound)
    {
        _sounds?.Play(SoundCues[(int)sound], LocalOptions);
    }

    /// <summary>A world event at <paramref name="position"/> (3D), triggered by each client from its own synchronised state.</summary>
    internal static void World(OrdnanceSound sound, Vector3 position) => At(SoundCues[(int)sound], position);

    /// <summary>A world event that follows <paramref name="owner"/> (3D).</summary>
    internal static void World(OrdnanceSound sound, GameObject owner, int? dimension = null) => Attach(SoundCues[(int)sound], owner, dimension);

    /// <summary>Stops every sound now. Native sentries can survive host takeover; their humming sustains start again by themselves.</summary>
    internal static void Clear() => _sounds?.StopAll();

    internal static void Shutdown()
    {
        _sounds?.Dispose();
        _sounds = null;
    }
}
