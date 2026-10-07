using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ForgeWeaponEnergyLabExperimental.Native;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

/// <summary>
/// The energy weapons' sound cues, one recording each, prepared by tools/import_audio.py. Draw, holster
/// and reload are not cues: the donor gun's own sounds play with its own animations.
/// <para>
/// GTFO hears through Wwise; Unity's own listener is not on the player's camera, so Unity 3D attenuation
/// silenced every positioned clip. Clips therefore always play 2D, and this class places
/// them itself: every frame <see cref="Listen"/> takes the local camera, and each playing positioned voice
/// gets its gain from its distance to that camera (linear between the cue's near and far) and its stereo
/// pan from the camera's right axis.
/// </para>
/// </summary>
internal static class EnergySound
{
    /// <summary>Unity voices need extra headroom to sit alongside GTFO's Wwise weapon mix.</summary>
    private const float Mix = 2f;
    /// <summary>How far a positioned voice pans at most; full hard-panning sounds detached from the room.</summary>
    private const float PanWidth = 0.75f;
    private const int VoiceCapacity = 96;

    internal readonly struct Cue
    {
        internal readonly string Name;
        internal readonly bool Loop;
        internal readonly bool Spatial;
        internal readonly float Near, Far;

        internal Cue(string name, bool loop, bool spatial, float near, float far)
        {
            Name = name; Loop = loop; Spatial = spatial; Near = near; Far = far;
        }
    }

    private static Cue Clip(string name) => new(name, false, false, 0f, 0f);
    private static Cue Clip(string name, float near, float far) => new(name, false, true, near, far);
    private static Cue Loop(string name, float near, float far) => new(name, true, true, near, far);

    internal static readonly Cue[] Cues =
    {
        Clip("beam-start", 2f, 30f),
        Loop("beam-loop", 2f, 30f),
        Clip("beam-stop", 2f, 30f),
        Clip("beam-hit", 1.5f, 25f),
        Clip("arc-fire", 1f, 24f),
        Loop("arc-loop", 2f, 24f),
        Clip("arc-hit", 1f, 24f),
        Clip("plasma-charge-rise", 2f, 24f),
        Loop("plasma-charge-loop", 2f, 24f),
        Clip("plasma-charge-full", 2f, 24f),
        Clip("plasma-charge-cancel", 2f, 24f),
        Clip("orb-fire", 2f, 30f),
        Loop("orb-fly-loop", 2f, 30f),
        Loop("shock-fly-loop", 2f, 30f),
        Clip("orb-explode", 1.5f, 36f),
        Clip("shock-fire", 2f, 30f),
        Clip("shock-fade", 1.5f, 30f),
        Clip("shock-hit", 1f, 22f),
        Clip("shock-pulse", 1f, 24f),
        Clip("blast-fire", 2f, 30f),
        Clip("blast-cycle", 2f, 30f),
        Clip("blast-explode", 6f, 48f),
        Clip("disc-fire", 2f, 30f),
        Loop("disc-fly-loop", 4f, 36f),
        Clip("disc-bounce", 4f, 36f),
        Clip("disc-drop", 3f, 30f),
        Clip("flame-start", 2f, 24f),
        Loop("flame-loop", 2f, 24f),
        Clip("flame-stop", 2f, 24f),
        Clip("flame-hit", 3f, 18f),
        Clip("flame-crackle", 3f, 18f),
        Clip("gravity-fire", 2f, 30f),
        Loop("gravity-fly-loop", 3f, 30f),
        Clip("gravity-open", 5f, 40f),
        Clip("gravity-close", 5f, 40f),
        Loop("gravity-field-loop", 5f, 40f),
        Clip("energy-dryfire"), Clip("mech-dryfire"),
    };
    private static readonly string[] SelectedFiles = ReadSelectedFiles();
    private static readonly Dictionary<string, AudioClip> Loaded = new(StringComparer.Ordinal);
    private static int _volumeFrame = -1;
    private static float _volume;
    // Every voice that has played a cue: its cue and the caller's gain before master volume and placement.
    private static readonly AudioSource?[] Voices = new AudioSource?[VoiceCapacity];
    private static readonly int[] VoiceCues = new int[VoiceCapacity];
    private static readonly float[] VoiceGains = new float[VoiceCapacity];
    private static int _voiceCount;
    private static bool _hasEar;
    private static Vector3 _ear, _earRight;

    /// <summary>
    /// Gain for energy-weapon voices: the energy mix times the game's master and SFX volume, and zero while the
    /// game mutes itself in the background. Read once per frame; unreadable settings play nothing rather than full.
    /// </summary>
    internal static float MasterVolume
    {
        get
        {
            var frame = Time.frameCount;
            if (frame == _volumeFrame) return _volume;
            _volumeFrame = frame;
            _volume = Mix * GameVolume();
            return _volume;
        }
    }

    private static float GameVolume()
    {
        try
        {
            var audio = CellSettingsManager.SettingsData?.Audio;
            if (audio?.MasterVolume == null || audio.SFXVolume == null)
                return Unreadable(new InvalidOperationException("Audio settings are not loaded."));
            if ((audio.MuteInBackground?.Value ?? false) && !Application.isFocused) return 0f;
            return Clamp01(audio.MasterVolume.Value) * Clamp01(audio.SFXVolume.Value);
        }
        catch (Exception error) { return Unreadable(error); }
    }

    private static float Unreadable(Exception error)
    {
        Plugin.Error("Game volume unreadable; energy sounds muted: " + error);
        return 0f;
    }

    private static float Clamp01(float value) => float.IsNaN(value) || value < 0f ? 0f : value > 1f ? 1f : value;

    internal static int IndexOf(string cue)
    {
        for (var index = 0; index < Cues.Length; index++)
            if (Cues[index].Name == cue) return index;
        throw new ArgumentException("Unknown energy sound cue: " + cue, nameof(cue));
    }

    internal static AudioSource Voice(Transform parent, string? name = null)
    {
        if (name != null)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            parent = child;
        }
        var voice = parent.gameObject.AddComponent<AudioSource>();
        voice.playOnAwake = false;
        return voice;
    }

    internal static void PlaySpatial(AudioSource[] voices, ref int cursor, string cue, Vector3 position, float gain)
    {
        AudioSource? voice = null;
        for (var offset = 0; offset < voices.Length; offset++)
        {
            var index = (cursor + offset) % voices.Length;
            var candidate = voices[index];
            if (candidate == null || candidate.isPlaying) continue;
            voice = candidate;
            cursor = (index + 1) % voices.Length;
            break;
        }
        // A saturated pool replaces its next voice so each event is audible.
        voice ??= voices[cursor++ % voices.Length];
        if (voice == null) return;
        voice.transform.position = position;
        Stop(voice);
        PlayCue(voice, cue, gain);
    }

    /// <summary>
    /// Plays the cue's selected recording. <paramref name="gain"/> is the mix before game volume and placement.
    /// </summary>
    internal static void PlayCue(AudioSource source, string cue, float gain)
    {
        var index = IndexOf(cue);
        var entry = Cues[index];
        var slot = Slot(source);
        if (slot < 0) { Stop(source); return; }
        VoiceCues[slot] = index;
        VoiceGains[slot] = gain;
        source.loop = entry.Loop;
        source.dopplerLevel = 0f;
        source.spatialBlend = 0f;
        var clip = LoadSelected(index);
        if (source.clip != clip) source.clip = clip;
        Apply(slot);
        source.Play();
    }

    internal static bool PlayingCue(AudioSource source, string cue)
    {
        if (!source.isPlaying) return false;
        var index = IndexOf(cue);
        for (var slot = 0; slot < _voiceCount; slot++)
            if (ReferenceEquals(Voices[slot], source)) return VoiceCues[slot] == index;
        return false;
    }

    /// <summary>Changes a playing voice's mix, for loops whose level follows the weapon every frame.</summary>
    internal static void Gain(AudioSource source, float gain)
    {
        var slot = Find(source);
        if (slot < 0 || !source.isPlaying) return;
        VoiceGains[slot] = gain;
        Apply(slot);
    }

    /// <summary>
    /// Takes this frame's ear (the local player's camera, or null outside a playable view) and re-places every
    /// playing voice, so moving sources, a moving player and game volume changes are heard on the same frame.
    /// </summary>
    internal static void Listen(Transform? ear)
    {
        _hasEar = ear is not null && ear != null;
        if (_hasEar)
        {
            _ear = ear!.position;
            _earRight = ear.right;
        }
        var kept = 0;
        for (var slot = 0; slot < _voiceCount; slot++)
        {
            var voice = Voices[slot];
            if (voice is null || voice == null || !voice.isPlaying) continue;
            Voices[kept] = voice; VoiceCues[kept] = VoiceCues[slot]; VoiceGains[kept] = VoiceGains[slot];
            Apply(kept);
            kept++;
        }
        for (var slot = kept; slot < _voiceCount; slot++) Voices[slot] = null;
        _voiceCount = kept;
    }

    private static int Slot(AudioSource source)
    {
        var found = Find(source);
        if (found >= 0) return found;
        // Reclaim finished/pooled voices before admission as well as in Listen. A burst
        // earlier in this frame must not retain dead slots until the next listener tick.
        for (var slot = _voiceCount - 1; slot >= 0; slot--)
            if (Voices[slot] == null || !Voices[slot]!.isPlaying) Release(slot);
        // In a full burst the newest voice is skipped.
        if (_voiceCount == VoiceCapacity) return -1;
        Voices[_voiceCount] = source;
        return _voiceCount++;
    }

    private static int Find(AudioSource source)
    {
        for (var slot = 0; slot < _voiceCount; slot++)
            if (ReferenceEquals(Voices[slot], source)) return slot;
        return -1;
    }

    private static void Release(int slot)
    {
        var last = --_voiceCount;
        Voices[slot] = Voices[last]; VoiceCues[slot] = VoiceCues[last];
        VoiceGains[slot] = VoiceGains[last];
        Voices[last] = null;
    }

    internal static void Stop(AudioSource source)
    {
        if (source != null) source.Stop();
        var slot = Find(source!);
        if (slot >= 0) Release(slot);
    }

    private static void Apply(int slot)
    {
        var source = Voices[slot]!;
        var entry = Cues[VoiceCues[slot]];
        var gain = VoiceGains[slot] * MasterVolume;
        if (!entry.Spatial)
        {
            source.volume = Math.Min(1f, gain);
            source.panStereo = 0f;
            return;
        }
        Place(entry, source.transform.position, out var heard, out var pan);
        source.volume = Math.Min(1f, gain * heard);
        source.panStereo = pan;
    }

    /// <summary>How loud a positioned cue at <paramref name="position"/> is at the current ear, and where it pans.</summary>
    internal static void Place(in Cue entry, Vector3 position, out float heard, out float pan)
    {
        heard = 0f; pan = 0f;
        if (!_hasEar) return;
        var offset = position - _ear;
        var distance = offset.magnitude;
        heard = distance <= entry.Near ? 1f : distance >= entry.Far ? 0f : 1f - (distance - entry.Near) / (entry.Far - entry.Near);
        if (distance < 0.01f) return;
        var side = (offset.x * _earRight.x + offset.y * _earRight.y + offset.z * _earRight.z) / distance;
        pan = Math.Clamp(side, -1f, 1f) * PanWidth * Math.Min(1f, distance / entry.Near);
    }

    internal static AudioClip Load(string name) => LoadSelected(IndexOf(name));

    private static string[] ReadSelectedFiles()
    {
        using var stream = typeof(EnergySound).Assembly.GetManifestResourceStream("ForgeWeaponEnergyLabExperimental.Audio.variants.tsv")
            ?? throw new InvalidOperationException("Generated audio variant map is missing from this build.");
        using var reader = new StreamReader(stream);
        var result = new string[Cues.Length];
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length != 2 || !parts[1].EndsWith(".bin", StringComparison.Ordinal))
                throw new InvalidDataException("Malformed generated audio variant map.");
            var index = IndexOf(parts[0]);
            if (result[index] != null) throw new InvalidDataException("Duplicate recording for cue " + parts[0]);
            result[index] = parts[1];
        }
        for (var index = 0; index < result.Length; index++)
            if (result[index] == null) throw new InvalidDataException("Missing recording for cue " + Cues[index].Name);
        return result;
    }

    private static AudioClip LoadSelected(int index)
    {
        var name = SelectedFiles[index];
        if (Loaded.TryGetValue(name, out var cached) && cached != null) return cached;
        if (PresentationPreload.Ready) throw new InvalidOperationException("Energy sound was not prepared: " + name);
        using var stream = typeof(EnergySound).Assembly.GetManifestResourceStream("ForgeWeaponEnergyLabExperimental.Audio.Variants." + name + ".gz")
            ?? throw new InvalidOperationException("Missing selected energy sound: " + name);
        if (stream.Length > PcmSamples.MaxBytes) throw new InvalidDataException("Energy sound exceeds the resource budget.");
        using var data = new MemoryStream();
        using (var decoded = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true))
        {
            var buffer = new byte[8192];
            int count;
            while ((count = decoded.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (data.Length + count > PcmSamples.MaxBytes) throw new InvalidDataException("Decoded energy sound exceeds the resource budget.");
                data.Write(buffer, 0, count);
            }
        }
        var samples = PcmSamples.Decode(data.GetBuffer().AsSpan(0, checked((int)data.Length)));
        var clip = AssetRetention.Keep(AudioClip.Create("energy-" + Cues[index].Name + "-0", samples.Length, 1, PcmSamples.Rate, false));
        try
        {
            if (clip.channels != 1 || clip.samples != samples.Length || clip.frequency != PcmSamples.Rate)
                throw new InvalidOperationException("Unity created an unexpected audio buffer for " + name);
            var native = new Il2CppStructArray<float>(samples);
            // The restored instance SetData wrapper contains CLR ldlen on an IL2CPP array wrapper.
            // It reads a bogus size. Use the native binding with an explicit, checked frame count.
            try
            {
                if (!AudioClip.SetData(clip, native, samples.Length, 0))
                    throw new InvalidOperationException("Unity rejected the energy audio buffer: " + name);
                var count = Math.Min(32, samples.Length);
                var offset = (samples.Length - count) / 2;
                var readBack = new Il2CppStructArray<float>(count);
                if (!AudioClip.GetData(clip, readBack, count, offset))
                    throw new InvalidOperationException("Unity could not read back the energy audio buffer: " + name);
                for (var sampleIndex = 0; sampleIndex < count; sampleIndex++)
                    if (!float.IsFinite(readBack[sampleIndex]) || MathF.Abs(readBack[sampleIndex] - samples[offset + sampleIndex]) > 0.0001f)
                        throw new InvalidOperationException("Unity energy audio readback differs: " + name);
                GC.KeepAlive(readBack);
            }
            finally { GC.KeepAlive(native); }
            Loaded[name] = clip;
            return clip;
        }
        catch { Object.Destroy(clip); throw; }
    }
}
