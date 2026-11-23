using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Presentation;

/// <summary>
/// The custom-sound half of `forge.action.presentation.audio_play`: the one resource kind the row's optional `clip`
/// port names and the payload grammar of that kind.
/// The row plays exactly one source: `sound` is a game sound event id, `clip` is a plain Ogg Vorbis sound the
/// author's browser transcoded and the ForgeAudio library plays. There is no engine bank and no event table.
/// A shipped sound is the file `forge/resources/&lt;revision&gt;.ogg` itself, the revision being the SHA-256 of its bytes.
/// </summary>
internal static class CustomSoundContract
{
    /// <summary>The file extension of a shipped sound.</summary>
    public const string Extension = ".ogg";

}

/// <summary>
/// One custom sound this install ships: the `{id, revision}` reference a plan compiles, the path of its Ogg Vorbis file
/// (named after the revision, the SHA-256 of its bytes) and the channel count. The bytes stay in the file; the library
/// reads them from there when the table is installed.
/// </summary>
internal sealed record CustomSound(string Id, string Revision, string Path, int Channels)
{
    /// <summary>The table's own key: the revision is part of a sound's identity.</summary>
    public string Key => Id + "@" + Revision;
}

/// <summary>
/// A set of custom sounds, each found by its hash in the package that names it (<see cref="Find"/>,
/// <see cref="CustomSoundLibrary"/>); nothing lists the install for sounds. Every file is proved whole before it
/// enters a table, and a file that fails is reported with its own reason and left out; one bad file never removes
/// the others. What is checked is identity and structure only: the bytes hash to the file name and carry the Ogg
/// Vorbis identification header. Whether the library decodes the stream is the library's answer at load time.
/// </summary>
internal sealed class CustomSoundTable
{
    private const string CodePrefix = "audio-sound";
    private static readonly PublishedBytesCodes FileCodes = new(CodePrefix + "-path", CodePrefix + "-unreadable", CodePrefix + "-file-size");
    /// <summary>The smallest thing that can be an Ogg Vorbis stream: one page header (27 bytes) and the 30-byte identification packet.</summary>
    private const int MinimumOggBytes = 58;

    private readonly Dictionary<string, CustomSound> _sounds;
    private readonly Dictionary<string, List<string>> _revisionsById;
    private readonly List<ResourceRef> _references;

    private CustomSoundTable(Dictionary<string, CustomSound> sounds)
    {
        _sounds = sounds;
        _revisionsById = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var sound in sounds.Values)
        {
            if (!_revisionsById.TryGetValue(sound.Id, out var revisions))
                _revisionsById[sound.Id] = revisions = new List<string>();
            revisions.Add(sound.Key);
        }
        _references = new List<ResourceRef>(_revisionsById.Count);
        foreach (var id in _revisionsById.Keys) _references.Add(new ResourceRef(PresentationContract.AudioResourceKind, id));
        _references.Sort((left, right) => string.CompareOrdinal(left.ResourceId, right.ResourceId));
    }

    /// <summary>An install that shipped no sound has none to answer, and a request naming one is refused by the
    /// handler rather than played from nothing.</summary>
    public static CustomSoundTable Empty { get; } = new(new Dictionary<string, CustomSound>(StringComparer.Ordinal));

    public int Count => _sounds.Count;

    /// <summary>Every sound this install ships, one reference per id, in ordinal id order.</summary>
    public IReadOnlyList<ResourceRef> Enumerate() => _references;

    public IReadOnlyList<CustomSound> All() => _sounds.Values.ToArray();

    /// <summary>
    /// One id resolved to the reference a plan's compiled literal becomes. It answers only for a sound this install
    /// ships: a plan naming a sound that is not installed is a dangling reference and is refused as stale at load,
    /// not at the moment it would play.
    /// </summary>
    public ResourceRef? Resolve(string? resourceId)
        => string.IsNullOrWhiteSpace(resourceId) || !_revisionsById.ContainsKey(resourceId)
            ? null : new ResourceRef(PresentationContract.AudioResourceKind, resourceId);

    /// <summary>The revision this install ships for one id, or null when the id is not shipped or is claimed by more
    /// than one revision (the runtime reference cannot tell which one a request means, see <see cref="Sound"/>).</summary>
    public string? Revision(string? resourceId)
        => !string.IsNullOrWhiteSpace(resourceId) && _revisionsById.TryGetValue(resourceId, out var revisions)
           && revisions.Count == 1 ? _sounds[revisions[0]].Revision : null;

    /// <summary>The sound one runtime reference's id names. The runtime's resource value has no revision slot, so an
    /// id published under two different revisions resolves to neither: the request names one `id@revision` and this
    /// reader cannot tell which.</summary>
    public CustomSound? Sound(string? resourceId)
    {
        // A base package above names its sound by the hash it found it under (an enemy's replaced sound event).
        if (PublishedResources.IsSha256(resourceId))
            return _sounds.Values.FirstOrDefault(sound => sound.Revision == resourceId);
        if (string.IsNullOrWhiteSpace(resourceId) || !_revisionsById.TryGetValue(resourceId, out var revisions)
            || revisions.Count != 1) return null;
        return _sounds[revisions[0]];
    }

    /// <summary>Whether an id this install ships is claimed by more than one revision.</summary>
    public bool IsAmbiguous(string? resourceId)
        => !string.IsNullOrWhiteSpace(resourceId) && _revisionsById.TryGetValue(resourceId, out var revisions)
           && revisions.Count > 1;

    public static CustomSoundTable FromPrepared(IEnumerable<CustomSound> sounds)
        => new(sounds.GroupBy(sound => sound.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    /// <summary>One sound by its hash, as <paramref name="package"/> sees it (I-RESOLVE): <c>&lt;revision&gt;.ogg</c> in
    /// the package's own <c>forge/resources/</c>, then in its declared dependencies'. <paramref name="id"/> is the id the
    /// requester names it by (a plan's `audio:` literal; a base package that asks by hash alone passes the hash). A file none of
    /// them ships is <c>resource-missing</c> naming the package, the hash and <paramref name="requester"/>; a file whose bytes do
    /// not hash to its name, or are no Ogg Vorbis stream of one or two channels, is refused with its own reason. The bytes are
    /// proved and dropped: the table keeps the path, the library reads the file itself.</summary>
    public static CustomSound? Find(string bepInExRoot, string package, string id, string revision, string requester,
        Action<PublishedResourceFailure> rejected)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        if (ForgeEngine.GameBindings.ContentPackages.FindResource(bepInExRoot, package, revision, CustomSoundContract.Extension, requester, rejected)
            is not { } file) return null;
        if (!PublishedResources.TryReadHashed(file, CustomSoundContract.Extension, FileCodes,
                CodePrefix + "-file-name", "A sound filename is its 64-character lower-case SHA-256 followed by " + CustomSoundContract.Extension + ".",
                CodePrefix + "-file-size", "The sound file is empty.", CodePrefix + "-sha256", "The sound bytes do not match the SHA-256 in the filename.",
                out _, out var bytes, out var failure))
        {
            rejected(failure);
            return null;
        }
        if (!TryReadOggVorbis(bytes, out var channels))
        {
            rejected(new(file.Relative, CodePrefix + "-invalid", "The file is not an Ogg Vorbis stream of one or two channels."));
            return null;
        }
        return new CustomSound(id, revision, file.Path, channels);
    }
    /// <summary>Whether the bytes are an Ogg Vorbis stream of one or two channels: the `OggS` page tag, then the
    /// Vorbis identification packet (type 1, `vorbis`, version 0) with its channel byte. It says nothing about the
    /// rest of the stream.</summary>
    public static bool TryReadOggVorbis(byte[]? bytes, out int channels)
    {
        channels = 0;
        if (bytes == null || bytes.Length < MinimumOggBytes) return false;
        if (bytes[0] != (byte)'O' || bytes[1] != (byte)'g' || bytes[2] != (byte)'g' || bytes[3] != (byte)'S') return false;
        int segments = bytes[26];
        int start = 27 + segments;
        if (bytes.Length < start + 30 || bytes[start] != 1) return false;
        var tag = "vorbis";
        for (var index = 0; index < tag.Length; index++)
            if (bytes[start + 1 + index] != (byte)tag[index]) return false;
        if (bytes[start + 7] != 0 || bytes[start + 7 + 1] != 0 || bytes[start + 7 + 2] != 0 || bytes[start + 7 + 3] != 0) return false;
        channels = bytes[start + 11];
        return channels is 1 or 2;
    }
}

/// <summary>
/// The custom sounds this client plays: the ones the adopted plans name and the ones a base package above asked for
/// (an enemy's replaced sound event), each found by its hash in the package that names it. A plan carries its
/// package from its own path when its level's plans are adopted, so a sound is read when the level that uses it is
/// chosen, not at startup. <see cref="Current"/> is replaced whole whenever the set changes; the player installs the
/// new table into the ForgeAudio library at its next frame or play.
/// </summary>
internal static class CustomSoundLibrary
{
    private const string PlanOwner = "plans";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, CustomSound>> Needs = new(StringComparer.Ordinal);

    /// <summary>The union of every owner's sounds; empty until a plan or a package names one.</summary>
    public static CustomSoundTable Current { get; private set; } = CustomSoundTable.Empty;

    /// <summary>The runtime's resource provider of the `audio` kind: what <see cref="Current"/> holds when a plan's reference is resolved.
    /// The game and the offline admission runner both declare exactly this.</summary>
    internal static RuntimeResourceProvider Provider()
        => RuntimeResourceProvider.Of(() => Current.Enumerate(), id => Current.Resolve(id), revision: id => Current.Revision(id));

    /// <summary>The sounds the plans just adopted name, replacing the previous plan set's: every `{id, revision}` audio
    /// literal of a step input, found in the plan's own package (<c>plugins/&lt;package&gt;</c> of its path). A sound that
    /// is not found or does not read leaves that plan's reference unresolved, so the kernel refuses the plan as
    /// `stale-resource`.</summary>
    internal static void UsePlans(string bepInExRoot, IEnumerable<(string Path, JsonElement Plan)> plans, Action<PublishedResourceFailure> rejected)
    {
        var found = new Dictionary<string, CustomSound>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            var package = PublishedResources.PackageRoot(plan.Path);
            foreach (var (id, revision) in AudioLiterals(plan.Plan))
            {
                if (found.ContainsKey(revision)) continue;
                if (CustomSoundTable.Find(bepInExRoot, package, id, revision, "plan " + plan.Path, rejected) is not { } sound) continue;
                found[revision] = sound;
            }
        }
        lock (Gate) { Needs[PlanOwner] = found; Rebuild(); }
    }

    /// <summary>One sound <paramref name="owner"/> (a base package) plays, by its hash as <paramref name="package"/> sees it.
    /// True when it was found and read; the refusal otherwise went to <paramref name="rejected"/>.</summary>
    public static bool Require(string bepInExRoot, string owner, string package, string revision, string requester,
        Action<PublishedResourceFailure> rejected)
    {
        lock (Gate)
            if (Needs.TryGetValue(owner, out var known) && known.ContainsKey(revision)) return true;
        if (CustomSoundTable.Find(bepInExRoot, package, revision, revision, requester, rejected) is not { } sound) return false;
        lock (Gate)
        {
            if (!Needs.TryGetValue(owner, out var sounds)) Needs[owner] = sounds = new Dictionary<string, CustomSound>(StringComparer.Ordinal);
            sounds[revision] = sound;
            Rebuild();
        }
        return true;
    }

    /// <summary>Forgets every owner's sounds (host stop).</summary>
    internal static void Clear()
    {
        lock (Gate) { Needs.Clear(); Current = CustomSoundTable.Empty; }
    }

    private static void Rebuild() => Current = CustomSoundTable.FromPrepared(Needs.Values.SelectMany(sounds => sounds.Values));

    /// <summary>Every audio literal of a compiled plan's step inputs, one member at a time for a list: an object whose id
    /// is an `audio:` sound id and whose revision is a SHA-256, the shape the website compiles.</summary>
    private static IEnumerable<(string Id, string Revision)> AudioLiterals(JsonElement plan)
    {
        if (plan.ValueKind != JsonValueKind.Object || !plan.TryGetProperty("entrypoints", out var entrypoints)
            || entrypoints.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in entrypoints.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) continue;
            foreach (var step in steps.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array) continue;
                foreach (var input in inputs.EnumerateArray())
                {
                    if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("value", out var value)) continue;
                    var members = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : new[] { value };
                    foreach (var member in members)
                        if (member.ValueKind == JsonValueKind.Object
                            && member.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString()!.StartsWith("audio:", StringComparison.Ordinal)
                            && member.TryGetProperty("revision", out var revision) && PublishedResources.IsSha256(revision.ValueKind == JsonValueKind.String ? revision.GetString() : null))
                            yield return (id.GetString()!, revision.GetString()!);
                }
            }
        }
    }
}
