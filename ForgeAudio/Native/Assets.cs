using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeAudio;

/// <summary>A decoded clip shared by every cue (of any library) whose file has the same content hash.</summary>
internal sealed class ClipLease
{
    internal readonly string Key;
    internal readonly AudioClip Clip;
    internal readonly long CompressedBytes, DecodedBytes;
    internal int References;
    internal ClipLease(string key, AudioClip clip, long compressed, long decoded)
    {
        Key = key; Clip = clip; CompressedBytes = compressed; DecodedBytes = decoded;
        ForgeCore.Assets.Keep(clip);
    }
}

/// <summary>A loaded cue: its definition and bus, the clips of every variant for each distance stretch (a stretch where
/// nothing plays has none), and its minimum-interval gate.</summary>
internal sealed class CueHandle
{
    internal readonly AudioLibrary Library;
    internal readonly CueDefinition Definition;
    internal readonly BusDefinition Bus;
    internal readonly MixProfile[] Profiles;
    internal readonly ClipLease[]?[] Clips;
    internal readonly int Variants;
    internal double NextAllowed;
    internal int LastVariant = -1;
    internal CueHandle(AudioLibrary library, CueDefinition definition, BusDefinition bus, MixProfile[] profiles, ClipLease[]?[] clips)
    {
        Library = library; Definition = definition; Bus = bus; Profiles = profiles; Clips = clips;
        Variants = clips.First(stretch => stretch != null)!.Length;
    }
}

/// <summary>Loading runs one file at a time: a worker reads, checks and hashes it; a second worker decodes it unless an
/// identical file is already cached; the main thread creates the Unity clip. Workers touch managed data only and hand
/// results over through their Task, which the main thread polls.</summary>
internal static class Assets
{
    private sealed class Prepared
    {
        internal byte[] Data = Array.Empty<byte>();
        internal OggInfo Info;
        internal string Hash = "";
        internal AudioFailure? Failure;
    }
    private sealed class Loading
    {
        internal readonly AudioLibrary Library;
        internal readonly string[] Files;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly Dictionary<string, ClipLease> Clips = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, AudioFailure> Failed = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, DecodedAudio> Pcm = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, (MixProfile[] Profiles, ClipLease[]?[] Clips)> Mixed = new(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> DirectFiles, MixedFiles;
        internal Queue<CueDefinition>? Todo;
        internal int Index;
        internal Task<Prepared>? Preparing;
        internal Prepared? Ready;
        internal Task<DecodedAudio>? Decoding;
        internal Loading(AudioLibrary library, string[] files)
        { Library = library; Files = files; DirectFiles = library.Catalog.DirectFiles; MixedFiles = library.Catalog.MixedFiles; }
        internal string File => Files[Index];
    }
    private static readonly Queue<Loading> Pending = new();
    private static readonly Dictionary<string, ClipLease> Cache = new(StringComparer.Ordinal);
    private static readonly List<AudioLibrary> Libraries = new();
    private static Loading? _active;
    internal static long CompressedBytes, DecodedBytes;
    /// <summary>The libraries loaded (or loading), in load order.</summary>
    internal static IReadOnlyList<AudioLibrary> Loaded => Libraries;

    internal static AudioLibrary Load(string identity, string folder)
    {
        var library = new AudioLibrary(identity ?? "", folder);
        AudioFailure? error = null;
        if (!AudioKit.Ready) error = new AudioFailure(AudioError.NotReady, "ForgeAudio is not loaded; add [BepInDependency(AudioKit.PluginId)] to your plugin.");
        else if (!AudioKit.OnMainThread) error = new AudioFailure(AudioError.WrongThread, "AudioKit.Load must run on the Unity main thread (your plugin's Load).");
        else if (ClipUpload.Failure is { } upload) error = upload;
        else if (!AudioKit.ValidIdentity(identity)) error = new AudioFailure(AudioError.InvalidIdentity, "identity '" + identity + "' must be letters, digits, '.', '_' or '-' (at most 100).");
        else if (Libraries.Any(other => string.Equals(other.Identity, identity, StringComparison.OrdinalIgnoreCase)))
            error = new AudioFailure(AudioError.DuplicateLibrary, "a library named '" + identity + "' is already loaded.");
        else if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder))
            error = new AudioFailure(AudioError.PathOutsideRoot, "audio folder '" + folder + "' is not an absolute path; pass the folder to AudioKit.Load.");
        else if (!Directory.Exists(folder)) error = new AudioFailure(AudioError.InvalidSource, "audio folder not found: " + folder);
        if (error == null)
        {
            try
            {
                var root = Path.GetFullPath(folder);
                var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(root, file)).ToArray();
                var settings = Path.Combine(root, CueCatalog.SettingsFile);
                library.Catalog = CueCatalog.Build(files, File.Exists(settings) ? File.ReadAllText(settings) : null);
                error = library.Catalog.Error;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { error = new AudioFailure(AudioError.InvalidSource, "audio folder unreadable: " + e); }
        }
        if (error is { } failure)
        {
            library.Error = new AudioFailure(failure.Code, identity + ": " + failure.Detail + " No sounds will play.");
            library.IsLoaded = true;
            AudioLog.Report(library.Error.Value);
            return library;
        }
        foreach (var unavailable in library.Catalog.Failures) AudioLog.Report(unavailable.Value.Code, identity + ": " + unavailable.Value.Detail);
        Libraries.Add(library);
        SyncPlays.Register(library);
        var loading = new Loading(library, library.Catalog.Files);
        Pending.Enqueue(loading);
        return library;
    }

    internal static void Tick()
    {
        if (_active == null && Pending.Count != 0) _active = Pending.Dequeue();
        var r = _active;
        if (r == null) return;
        if (r.Library.IsDisposed) { Abort(r); return; }
        if (r.Index == r.Files.Length) { if (BuildNext(r)) Complete(r); return; }
        var file = r.File;
        try
        {
            var needPcm = r.MixedFiles.Contains(file); var needClip = r.DirectFiles.Contains(file);
            if (r.Ready == null)
            {
                r.Preparing ??= Task.Run(() => Prepare(Path.Combine(r.Library.Folder, file), r.Cancel.Token));
                if (!r.Preparing.IsCompleted) return;
                r.Ready = r.Preparing.GetAwaiter().GetResult();
                r.Preparing = null;
                if (r.Ready.Failure is { } failed) { Fail(r, failed); return; }
                // Identical content (in this or any library) decodes once, unless a mixed cue needs its PCM.
                if (needClip && !needPcm && Cache.TryGetValue(r.Ready.Hash, out var cached)) { cached.References++; Accept(r, cached); return; }
                var info = r.Ready.Info;
                if (DecodedBytes + info.DecodedBytes > AssetValidation.MaxDecodedBytes || CompressedBytes + r.Ready.Data.Length > AssetValidation.MaxCompressedCache)
                { Fail(r, new AudioFailure(AudioError.AssetLimit, "the audio cache is full (512 MiB decoded / 256 MiB compressed in total).")); return; }
            }
            if (r.Decoding == null)
            {
                var prepared = r.Ready;
                var token = r.Cancel.Token;
                r.Decoding = Task.Run(() => VorbisDecode.Decode(prepared.Data, prepared.Info, token));
                return;
            }
            if (!r.Decoding.IsCompleted) return;
            var audio = r.Decoding.GetAwaiter().GetResult();
            r.Decoding = null;
            if (needPcm) r.Pcm[file] = audio;
            if (!needClip) { Next(r); return; }
            if (Cache.TryGetValue(r.Ready.Hash, out var shared)) { shared.References++; Accept(r, shared); return; }
            var clip = MakeClip(r.Library.Identity + "/" + CueCatalog.CueName(file), audio);
            var lease = new ClipLease(r.Ready.Hash, clip, r.Ready.Data.Length, (long)audio.Frames * audio.Channels * sizeof(float)) { References = 1 };
            Cache.Add(lease.Key, lease); CompressedBytes += lease.CompressedBytes; DecodedBytes += lease.DecodedBytes;
            Accept(r, lease);
        }
        catch (OperationCanceledException) { Abort(r); }
        catch (Exception e) { Fail(r, new AudioFailure(AudioError.DecodeFailed, e.ToString())); }
    }

    /// <summary>A Unity clip holding <paramref name="audio"/>, refused (and destroyed) when Unity's frequency or length
    /// disagree with it: such a clip would play at the wrong speed.</summary>
    private static AudioClip MakeClip(string name, DecodedAudio audio)
    {
        var clip = AudioClip.Create(name, audio.Frames, audio.Channels, audio.SampleRate, false)
            ?? throw new InvalidDataException("Unity returned no clip.");
        try
        {
            ClipUpload.SetData(clip, audio);
            if (clip.samples != audio.Frames || clip.channels != audio.Channels) throw new InvalidDataException("Unity clip size disagrees with the decoded audio.");
            var expected = (double)audio.Frames / audio.SampleRate;
            if (clip.frequency != audio.SampleRate || Math.Abs(clip.length - expected) > expected * .01 + .001)
                throw new InvalidDataException("Unity clip plays at " + clip.frequency + " Hz for " + clip.length.ToString("F3", CultureInfo.InvariantCulture)
                    + " s, but the audio is " + audio.SampleRate + " Hz for " + expected.ToString("F3", CultureInfo.InvariantCulture) + " s, so it would play at the wrong speed.");
        }
        catch { Object.Destroy(clip); throw; }
        return clip;
    }

    /// <summary>Mixes one layered or capped cue (all its variants and distance stretches); true when none is left.</summary>
    private static bool BuildNext(Loading r)
    {
        var library = r.Library;
        r.Todo ??= new Queue<CueDefinition>(library.Catalog.Cues.Values.Where(cue => !cue.IsDirect && !cue.Clips.Any(r.Failed.ContainsKey)).OrderBy(cue => cue.Name, StringComparer.OrdinalIgnoreCase));
        if (r.Todo.Count == 0) return true;
        var cue = r.Todo.Dequeue();
        try
        {
            var profiles = CueMix.Profiles(cue);
            var variants = CueMix.Variants(cue);
            var clips = new ClipLease[]?[profiles.Length];
            for (var p = 0; p < profiles.Length; p++)
            {
                if (profiles[p].Silent) continue;
                clips[p] = new ClipLease[variants];
                for (var v = 0; v < variants; v++)
                {
                    var mixed = CueMix.Render(cue, profiles[p], v, file => r.Pcm[file])!;
                    var audio = new DecodedAudio(mixed.Samples, mixed.Channels, mixed.SampleRate);
                    var clip = MakeClip(library.Identity + "/" + cue.Name + "#" + p + "." + v, audio);
                    var lease = new ClipLease("mix:" + library.Identity + "/" + cue.Name + "#" + p + "." + v, clip, 0, (long)audio.Samples.Length * sizeof(float)) { References = 1 };
                    DecodedBytes += lease.DecodedBytes; library.Assets.Add(lease); clips[p]![v] = lease;
                }
            }
            r.Mixed[cue.Name] = (profiles, clips);
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            var failure = new AudioFailure(AudioError.DecodeFailed, library.Identity + ": cue '" + cue.Name + "' cannot be mixed: " + e);
            library.Catalog.Failures[cue.Name] = failure;
            AudioLog.Report(failure);
        }
        return false;
    }
    private static Prepared Prepare(string path, CancellationToken cancellation)
    {
        var result = new Prepared();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > AssetValidation.MaxFileBytes) { result.Failure = new AudioFailure(AudioError.AssetLimit, "file exceeds 64 MiB."); return result; }
            var bytes = File.ReadAllBytes(path);
            var info = AssetValidation.Ogg(bytes);
            if (!info.Success) { result.Failure = info.Error; return result; }
            result.Data = bytes; result.Info = info.Value;
            result.Hash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { result.Failure = new AudioFailure(AudioError.InvalidSource, "unreadable: " + e); }
        return result;
    }

    private static void Accept(Loading r, ClipLease lease)
    {
        r.Clips[r.File] = lease;
        r.Library.Assets.Add(lease);
        Next(r);
    }
    private static void Fail(Loading r, AudioFailure failure)
    {
        r.Failed[r.File] = new AudioFailure(failure.Code, r.File + ": " + failure.Detail);
        Next(r);
    }
    private static void Next(Loading r) { r.Ready = null; r.Preparing = null; r.Decoding = null; r.Index++; }

    private static void Complete(Loading r)
    {
        var library = r.Library;
        foreach (var definition in library.Catalog.Cues.Values.ToArray())
        {
            if (library.Catalog.Failures.ContainsKey(definition.Name)) continue;
            var broken = definition.Clips.FirstOrDefault(r.Failed.ContainsKey);
            if (broken != null)
            {
                var failure = r.Failed[broken];
                library.Catalog.Failures[definition.Name] = failure;
                AudioLog.Report(failure.Code, library.Identity + ": cue '" + definition.Name + "' unavailable, " + failure.Detail);
                continue;
            }
            var bus = library.Catalog.Buses[definition.Bus];
            if (definition.IsDirect)
            {
                var clips = definition.Layers[0].Clips.Select(file => r.Clips[file]).ToArray();
                library.Cues[definition.Name] = new CueHandle(library, definition, bus, new[] { new MixProfile(0, float.PositiveInfinity, 1) }, new[] { clips });
            }
            else if (r.Mixed.TryGetValue(definition.Name, out var mixed)) library.Cues[definition.Name] = new CueHandle(library, definition, bus, mixed.Profiles, mixed.Clips);
        }
        foreach (var cue in library.Cues.Values)
        {
            var key = SyncKey.Of(cue.Definition.Name);
            if (library.CueKeys.TryGetValue(key, out var clash))
                AudioLog.Report(AudioError.InvalidSettings, library.Identity + ": cues '" + clash.Definition.Name + "' and '" + cue.Definition.Name
                    + "' have the same network name; '" + cue.Definition.Name + "' is not played for other players.");
            else library.CueKeys[key] = cue;
        }
        r.Pcm.Clear();
        library.IsLoaded = true;
        _active = null;
        r.Cancel.Dispose();
    }
    // A worker still running finishes on managed data only; its result is dropped.
    private static void Abort(Loading r)
    {
        r.Cancel.Cancel();
        if (_active == r) _active = null;
        r.Library.IsLoaded = true;
    }

    internal static void ReleaseLibrary(AudioLibrary library)
    {
        if (_active?.Library == library) Abort(_active);
        foreach (var lease in library.Assets) Release(lease);
        library.Assets.Clear(); library.Cues.Clear(); library.CueKeys.Clear();
        Libraries.Remove(library);
        SyncPlays.Forget(library);
    }
    private static void Release(ClipLease lease)
    {
        if (--lease.References != 0) return;
        Cache.Remove(lease.Key); CompressedBytes -= lease.CompressedBytes; DecodedBytes -= lease.DecodedBytes;
        if (lease.Clip != null && !lease.Clip.WasCollected) Object.Destroy(lease.Clip);
    }
    internal static void Clear()
    {
        while (Libraries.Count != 0) Libraries[Libraries.Count - 1].Dispose();
        while (Pending.Count != 0) Abort(Pending.Dequeue());
        if (_active != null) Abort(_active);
    }
}
