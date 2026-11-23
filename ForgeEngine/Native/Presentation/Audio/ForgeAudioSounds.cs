using System;
using System.Collections.Generic;
using System.Text;
using ForgeAudio;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// Plays the author's custom sounds through the ForgeAudio library. Whenever <see cref="CustomSoundLibrary.Current"/>
/// changes (a level's plans were adopted, a package above asked for a sound), the next frame or play hands the library
/// that table: one `cues.forge` text that puts every cue on one `device` bus, and the path of each package file
/// (`forge/resources/&lt;revision&gt;.ogg`) the library reads and decodes itself. Nothing is copied, written to a cache
/// or kept as bytes. A play is a library cue play: globally when no place was named, at a world point, or attached to a
/// followed object. The library owns the voice limits, distance and category budget, and ends every playback itself
/// when the level is left.
///
/// Game-verified: nothing yet. The category the cues share (`device`, 10 voices shared by every mod) and the
/// library's default distance model (full volume to 2 m, silent beyond 30 m for a positioned or followed play) are
/// the choices the verification list carries.
/// </summary>
internal sealed class ForgeAudioSounds : IDisposable
{
    internal const string Identity = "NAinfini.ForgeEngine";
    private const string Bus = "custom";
    private const string Category = "device";

    private const int MaxAttempts = 3;

    private readonly Action<string> _report;
    private CustomSoundTable _table = CustomSoundTable.Empty;
    private AudioLibrary? _library;
    private string? _unavailable;
    // The table the library holds, and the table the failed attempts count for: a table that did not install is tried again, a few times.
    private CustomSoundTable _installed = CustomSoundTable.Empty;
    private CustomSoundTable? _attempted;
    private int _attempts;

    internal ForgeAudioSounds(Action<string> report) => _report = report ?? throw new ArgumentNullException(nameof(report));

    /// <summary>Hands the sounds to the library, replacing whatever was installed. A library that refuses the list leaves
    /// every sound refused by name. That does not count as installed, so <see cref="Sync"/> tries the table again, up to
    /// <see cref="MaxAttempts"/> times.</summary>
    internal void Install(CustomSoundTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!ReferenceEquals(_attempted, table)) { _attempted = table; _attempts = 0; }
        _attempts++;
        Release();
        _table = table;
        if (table.Count != 0)
        {
            try
            {
                var (cues, files) = Describe(table.All());
                var library = AudioKit.Load(Identity, cues, files);
                if (library.Error is { } error)
                {
                    _unavailable = ResultCode.AudioEngineUnavailable;
                    _report("presentation.audio-sound-refused: the ForgeAudio library refused the sound list: " + error + ".");
                    library.Dispose();
                }
                else _library = library;
            }
            catch (Exception error)
            {
                _unavailable = ResultCode.AudioEngineUnavailable;
                _report("presentation.audio-sound-refused: " + error.GetType().Name + ": " + error.Message + ".");
            }
        }
        if (table.Count == 0 || _library != null) _installed = table;
    }

    public void Dispose()
    {
        Release();
        _table = CustomSoundTable.Empty;
        _installed = CustomSoundTable.Empty;
    }

    private void Release()
    {
        _library?.StopAll();
        _library?.Dispose();
        _library = null;
        _unavailable = null;
    }

    /// <summary>The `cues.forge` text and the clip files of a set of sounds: one cue per revision, named like its file.</summary>
    private static (string Cues, Dictionary<string, string> Files) Describe(IReadOnlyList<CustomSound> sounds)
    {
        var cues = new StringBuilder();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sound in sounds)
        {
            if (!files.TryAdd(sound.Revision + CustomSoundContract.Extension, sound.Path)) continue;
            if (cues.Length != 0) cues.Append(',');
            cues.Append('"').Append(sound.Revision).Append("\":{\"bus\":\"").Append(Bus).Append("\"}");
        }
        return ("{\"buses\":{\"" + Bus + "\":{\"category\":\"" + Category + "\"}},\"cues\":{" + cues + "}}", files);
    }
    /// <summary>Installs <see cref="CustomSoundLibrary.Current"/> when it is not the table the library holds.</summary>
    internal void Sync()
    {
        var current = CustomSoundLibrary.Current;
        if (ReferenceEquals(_installed, current)) return;
        if (ReferenceEquals(_attempted, current) && _attempts >= MaxAttempts) return;
        Install(current);
    }

    /// <summary>The refusal code a request naming <paramref name="resourceId"/> gets, or null when it may go on.</summary>
    public string? Refusal(string resourceId)
    {
        Sync();
        if (_table.IsAmbiguous(resourceId)) return ResultCode.AudioSoundConflict;
        if (_table.Sound(resourceId) is not { } sound) return ResultCode.AudioSoundNotLoaded;
        if (_unavailable != null) return _unavailable;
        return _library is { IsDisposed: false } library && library.Has(sound.Revision) ? null : ResultCode.AudioSoundNotLoaded;
    }

    /// <summary>Plays the sound globally (both null), at a world point, or on a followed object.</summary>
    public CustomSoundStart Play(string resourceId, Vector3? position, GameObject? follow)
    {
        if (Refusal(resourceId) is { } refusal) return new(refusal, null);
        var cue = _table.Sound(resourceId)!.Revision;
        var options = new PlayOptions();
        PlayResult result;
        try
        {
            result = position is { } point ? _library!.Play(cue, point, options)
                : follow != null ? _library!.Play(cue, follow.transform, options)
                : _library!.Play(cue, options);
        }
        catch (Exception error)
        {
            _report("presentation.audio-sound-refused: play of `" + resourceId + "` threw " + error.GetType().Name + ": " + error.Message + ".");
            return new(ResultCode.AudioPostFailed, null);
        }
        if (!result.Accepted)
        {
            // Out of range and over the voice limit are what the library is for, not faults; enemies hit them all the time.
            if (result.Status == PlayStatus.Failed) _report("presentation.audio-sound-refused: play of `" + resourceId + "` failed.");
            return new(ResultCode.AudioPostFailed, null);
        }
        var handle = result.Handle;
        return handle == null ? new(null, null) : new(null, () => handle.Stop(0f), () => handle.State is PlaybackState.Playing or PlaybackState.Virtualized);
    }
}
