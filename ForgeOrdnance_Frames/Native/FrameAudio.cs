using ForgeAudio;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>This package's sounds: the <c>audio</c> folder next to the DLL, loaded once through ForgeAudio. This class only
/// chooses when and where they play; cue settings (including each sound's minimum gap, its group and ducking) live in <c>audio/cues.forge</c>, and
/// AudioKit logs every cue that cannot load or play by name. The suit gun keeps the selected vanilla weapon's Wwise events.</summary>
internal static class FrameAudio
{
    private static AudioLibrary? _sounds;
    private static readonly PlayOptions Self = new() { IsSelf = true }, Alone = new();
    private static readonly PlayOptions[] Sections = BuildSections();

    internal static void Load() => _sounds ??= AudioKit.Load(Plugin.Id);

    /// <summary>Stops every exosuit sound (level cleanup).</summary>
    internal static void Clear()
    {
        _sounds?.StopAll();
    }

    internal static void Dispose()
    {
        _sounds?.Dispose();
        _sounds = null;
    }

    /// <summary>A sound only the local player hears (2D): the pilot's own HUD, controls and weapon.</summary>
    internal static AudioHandle? Local(FrameSound sound, PlayOptions? options = null)
        => Handle(_sounds?.Play(Cue(sound), options ?? Self));

    internal static AudioHandle? PlayAt(FrameSound sound, string identity, Vector3 position, int dimension, bool self = false)
        => Handle(_sounds?.Play(Cue(sound), position, World(identity, dimension, self)));

    /// <summary>A sound on an emitter of its own at <paramref name="position"/> (a blast: nothing else of that place plays with it).</summary>
    internal static AudioHandle? PlayAlone(FrameSound sound, Vector3 position) => Handle(_sounds?.Play(Cue(sound), position, Alone));

    internal static AudioHandle? PlayAttached(FrameSound sound, string identity, Transform target, int dimension, bool self = false)
        => Handle(_sounds?.Play(Cue(sound), target, World(identity, dimension, self)));

    /// <summary>The power-on tone <paramref name="index"/> (one per block, <see cref="FrameBootScreen.Groups"/>), one step higher than the one before.</summary>
    internal static PlayOptions Section(int index) => Sections[index];

    private static AudioHandle? Handle(PlayResult? result) => result is { Accepted: true } ? result.Value.Handle : null;

    private static string Cue(FrameSound sound) => FrameSounds.Table[(int)sound].Cue;

    // One emitter per suit for the cues' maxPerEmitter.
    private static PlayOptions World(string identity, int dimension, bool self)
        => new() { Emitter = identity, IsSelf = self, Dimension = dimension };

    private static PlayOptions[] BuildSections()
    {
        var options = new PlayOptions[FrameBootScreen.Groups];
        for (var i = 0; i < options.Length; i++) options[i] = new PlayOptions { IsSelf = true, Pitch = 0.88f + 0.08f * i };
        return options;
    }

    internal static string Identity(FrameUnit unit) => "exo-" + unit.Record.Thrower + "-" + unit.Record.ThrowId;
}
