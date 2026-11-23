using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEngine.Network;

/// <summary>
/// One plan's identity, as the handshake compares it. A shipped planId includes its final consumer mounts;
/// the resource revision, frozen binding pins and exact validated plan-byte digest also participate.
/// Plan files are never sent here.
/// </summary>
internal readonly record struct PlanIdentity(string PlanId, string ResourceId, string ResourceRevision, string BindingPins, string Mounts, string ContentDigest);

/// <summary>
/// The whole plan set's one comparison value: SHA-256 over JSON-framed identities, including the actual mounts and
/// the SHA-256 of each loaded plan's original bytes,
/// in ordinal planId order. The rows are hashed, never sent: a hello carries the digest and the
/// count, so the size of a plan set never bounds a message. A set is digested when it is adopted — once — and never
/// inside a tick.
/// </summary>
internal static class PlanSetDigest
{
    /// <summary>The digest of a canonical plan set: the caller passes the rows in the ordinal order both sides sort
    /// into, which is what makes the digest independent of the order a plan set was declared in.</summary>
    internal static byte[] Of(IReadOnlyList<PlanIdentity> canonical)
    {
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical));
    }

    /// <summary>Two digests are the same set summary; the comparison is over all 32 bytes, never a prefix.</summary>
    internal static bool Equal(byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right);
}

/// <summary>A snapshot of the pack files the current level needs, taken at discovery/session boundaries, never in a tick.
/// Only the packs the level's Rundown names (itself and its exact dependencies) are read: other installed packs and
/// mods never reach the file comparison, and the files of those packs are hashed whole, whatever their count or size.
/// File names and actual bytes have separate digests so missing/extra files can be distinguished from edits.
/// Resource documents contain the packed audio bytes; their claimed resource revisions are not trusted.
/// <para>The one exception to "only this level" is the loaded enemy table, compared whole: the game's enemy spawn packet
/// carries no enemy ID, only the position of the enemy's prefab in a list built from that table, so every installed pack
/// that adds an enemy shifts the meaning of that position on its machine alone. Two machines whose tables differ would
/// spawn a different enemy type for the same packet.</para></summary>
internal sealed class NetworkContentSnapshot
{
    internal const int CategoryCount = 5;
    internal const int WordsPerCategory = 9; // count/status, file-set (or ID order) SHA-256, content SHA-256
    private const int Unreadable = 1;
    private const int EnemyCategory = 4;
    private static readonly string[] Codes = { "map", "room", "native", "audio", "enemies" };
    private static readonly string[] Names = { "Rundown and map files", "Room layout files", "Native game data and content packs", "Packed audio, image and model resources", "Enemy types" };
    private readonly ulong[] _words;

    private NetworkContentSnapshot(ulong[] words, IReadOnlyList<string> packages)
    {
        _words = words;
        Packages = packages;
    }

    /// <summary>The packs this computer reads for the level: the set its plans are chosen from and the sentence a
    /// mismatch is explained with. The wire carries digests only, so a snapshot read from a peer's detail has none.</summary>
    internal IReadOnlyList<string> Packages { get; }

    /// <summary>The packs a sender names for its own level in its handshake detail: package names (<c>Author-Pack</c>) joined by commas,
    /// as many as one text slot holds. Anything a peer wrote that is not a package name is dropped.</summary>
    internal static string JoinPackages(IReadOnlyList<string> packages)
    {
        var text = new StringBuilder();
        foreach (var package in packages)
        {
            if (!IsPackageName(package) || text.Length + package.Length + 1 > NetworkProtocol.TextSlotBytes) continue;
            if (text.Length > 0) text.Append(',');
            text.Append(package);
        }
        return text.ToString();
    }

    internal static IReadOnlyList<string> SplitPackages(string joined)
        => joined.Length == 0 ? Array.Empty<string>() : joined.Split(',').Where(IsPackageName).ToArray();

    private static bool IsPackageName(string name)
        => name.Length is > 0 and <= 96 && name.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' or '-');

    internal static NetworkContentSnapshot FromWords(ReadOnlySpan<ulong> words)
    {
        if (words.Length != CategoryCount * WordsPerCategory)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Invalid content digest width.");
        return new NetworkContentSnapshot(words.ToArray(), Array.Empty<string>());
    }
    internal ReadOnlySpan<ulong> Words => _words;
    internal bool Same(NetworkContentSnapshot other) => _words.AsSpan().SequenceEqual(other._words);

    /// <summary>The packs a level reads: its Rundown's own pack and the packs that pack pins exactly (a pin is
    /// <c>Author-Pack-1.2.3</c>). A level of no installed Rundown pack — the original game's — reads none.</summary>
    internal static string[] NeededPackages(IReadOnlyList<RundownPack> rundowns, uint rundownId)
    {
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pack in rundowns)
        {
            if (pack.RundownId != rundownId || !pack.DiscoveryAdmitted) continue;
            packages.Add(pack.Package);
            foreach (var pin in pack.Dependencies) packages.Add(pin.Substring(0, pin.LastIndexOf('-')));
        }
        return new List<string>(packages).ToArray();
    }

    /// <summary>The pack directory a plan file was found in (<c>plugins/Pack/forge/plans/...</c>), or null for a path
    /// outside a pack.</summary>
    internal static string? PackOf(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 2 && parts[0] == "plugins" ? parts[1] : null;
    }

    /// <summary>The plans of this level: those installed in a pack the snapshot reads. A plan of any other pack is not
    /// compared, the same as that pack's files. <paramref name="packsOfDigest"/> maps a plan's content digest to the
    /// packs holding a copy of it.</summary>
    internal List<PlanIdentity> OfLevel(IEnumerable<PlanIdentity> plans, ILookup<string, string> packsOfDigest)
        => plans.Where(plan => packsOfDigest[plan.ContentDigest].Any(pack => Packages.Contains(pack))).ToList();

    /// <summary>The file names of this computer's packed resources (images, meshes, sounds, resource documents) as captured; a snapshot read
    /// from a peer's hello has none. Only the log of a resource difference reads it.</summary>
    internal IReadOnlyList<string> ResourceFiles { get; private set; } = Array.Empty<string>();

    /// <summary>The SHA-256 of each pack file's bytes, by full path, length and last write time, so a level entry hashes
    /// only the files that changed since the last capture. The digest itself is the one a full read gives.</summary>
    private static readonly Dictionary<string, (long Length, long WriteTicks, byte[] Digest)> FileDigests = new(StringComparer.Ordinal);

    /// <param name="enemies">The loaded enemy table, every row in table order (persistentID and name), read on the game thread;
    /// null when it could not be read, which is compared as unreadable.</param>
    internal static NetworkContentSnapshot Capture(string root, IReadOnlyList<string> packages, IReadOnlyList<(uint Id, string Name)>? enemies)
    {
        var files = new List<(string Relative, int Category)>();
        var status = new int[CategoryCount];
        var counts = new int[CategoryCount];
        void Scan(string relative, int category, Func<string, bool> accepts)
        {
            try
            {
                var directory = PublishedResources.ResolveWithinRoot(root, relative);
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) throw new IOException("Expected directory.");
                }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var name = Path.GetFileName(path);
                    if (!accepts(name)) continue;
                    // The two room documents of the forge folder, and a room or furnishing file in any category, count as rooms.
                    bool roomDocument = category == 0 && name is "room-build.forge" or "room-furnishings.forge";
                    bool roomFile = name.EndsWith(".room.forge", StringComparison.Ordinal) || name.EndsWith(".furnishing.forge", StringComparison.Ordinal);
                    var part = roomDocument || roomFile ? 1 : category;
                    counts[part]++;
                    files.Add((ForgeCore.PosixPath.ToPosix(Path.GetRelativePath(root, path)), part));
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                status[category] |= Unreadable;
                if (category == 0) status[1] |= Unreadable;
            }
        }
        try
        {
            root = Path.GetFullPath(root);
            foreach (var package in packages)
            {
                var relative = "plugins/" + package;
                Scan(relative + "/forge", 0, name => name.EndsWith(".forge", StringComparison.Ordinal));
                Scan(relative + "/forge/map-fields", 0, name => name.EndsWith(".forge", StringComparison.Ordinal));
                Scan(relative + "/forge/enemies", 0, name => name.EndsWith(".forge", StringComparison.Ordinal));
                Scan("GameData/" + package, 2, name => name.StartsWith("GameData_", StringComparison.Ordinal) && name.EndsWith("DataBlock_bin.json", StringComparison.Ordinal));
                // Native content is installed from these authored bytes, not from the plan identity or derived ID.
                // Keep it under the existing native category and the same link-refusing reader.
                Scan(relative + "/forge/content", 2, name => name.EndsWith(".forge", StringComparison.Ordinal));
                foreach (var domain in ContentPackDiscovery.Domains)
                    Scan(relative + "/forge/content/" + domain, 2, name => name.EndsWith(".forge", StringComparison.Ordinal));
                // Weapon definition documents are authored content the native weapon session installs from.
                Scan(relative + "/forge/weapons", 2, name => name.EndsWith(".forge", StringComparison.Ordinal));
                // Resource documents and the images, meshes and sounds they or the plans reference are one resource category, so a model whose mesh
                // (or a plan whose sound) one player lacks fails the join by name instead of drawing or playing nothing on that machine.
                // Room and furnishing files (ForgeMap RoomBuildRecipe / RoomFurnishing FileExtension) belong with room-build.forge
                // and room-furnishings.forge in the room category.
                Scan(relative + "/forge/resources", 3, name => name.EndsWith(".resource.forge", StringComparison.Ordinal)
                    || name.EndsWith(".room.forge", StringComparison.Ordinal) || name.EndsWith(".furnishing.forge", StringComparison.Ordinal)
                    || name.EndsWith(".png", StringComparison.Ordinal)
                    || name.EndsWith(".fmesh", StringComparison.Ordinal) // ImportedModelFiles.MeshExtension
                    || name.EndsWith(".ogg", StringComparison.Ordinal) // ForgeEngine.Presentation.CustomSoundContract.Extension
                    || name.EndsWith(".glb", StringComparison.Ordinal)); // ForgeEnemy.Native.ActionResourceCodes.Extension
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { for (var i = 0; i < status.Length; i++) status[i] |= Unreadable; }

        files.Sort((left, right) => string.CompareOrdinal(left.Relative, right.Relative));
        var words = new ulong[CategoryCount * WordsPerCategory];
        var buffer = new byte[81920];
        for (var category = 0; category < CategoryCount; category++)
        {
            using var names = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in files)
            {
                if (file.Category != category) continue;
                var pathBytes = Encoding.UTF8.GetBytes(file.Relative);
                Append(names, pathBytes);
                Append(content, pathBytes);
                try
                {
                    var path = PublishedResources.ResolveWithinRoot(root, file.Relative);
                    // The file is still opened every time, so a file this side cannot read stays Unreadable; only its
                    // bytes are not hashed again while its length and write time are those of the digest kept.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var length = stream.Length;
                    var written = File.GetLastWriteTimeUtc(path).Ticks;
                    byte[]? fileDigest = null;
                    lock (FileDigests)
                        if (FileDigests.TryGetValue(path, out var kept) && kept.Length == length && kept.WriteTicks == written) fileDigest = kept.Digest;
                    if (fileDigest == null)
                    {
                        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long read = 0;
                        int count;
                        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            read += count;
                            digest.AppendData(buffer, 0, count);
                        }
                        fileDigest = digest.GetHashAndReset();
                        if (read != length || stream.Length != length) status[category] |= Unreadable;
                        else lock (FileDigests) FileDigests[path] = (length, written, fileDigest);
                    }
                    Append(content, fileDigest);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { status[category] |= Unreadable; }
            }
            if (category == EnemyCategory)
            {
                // The ID order is what a spawn packet's prefab position means; the names catch a same-ID row with other content.
                if (enemies == null) status[category] |= Unreadable;
                else
                {
                    counts[category] = enemies.Count;
                    var id = new byte[4];
                    foreach (var row in enemies)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(id, row.Id);
                        names.AppendData(id);
                        content.AppendData(id);
                        Append(content, Encoding.UTF8.GetBytes(row.Name));
                    }
                }
            }
            var offset = category * WordsPerCategory;
            words[offset] = (uint)counts[category] | ((ulong)(uint)status[category] << 32);
            var nameDigest = names.GetHashAndReset();
            var contentDigest = content.GetHashAndReset();
            for (var i = 0; i < 4; i++)
            {
                words[offset + 1 + i] = BinaryPrimitives.ReadUInt64LittleEndian(nameDigest.AsSpan(i * 8));
                words[offset + 5 + i] = BinaryPrimitives.ReadUInt64LittleEndian(contentDigest.AsSpan(i * 8));
            }
        }
        return new NetworkContentSnapshot(words, packages)
        {
            ResourceFiles = files.Where(file => file.Category == 3).Select(file => file.Relative.Substring(file.Relative.LastIndexOf('/') + 1)).ToArray()
        };
    }

    private static void Append(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    internal HandshakeEvaluation Compare(NetworkContentSnapshot peer, SessionRole role, IReadOnlyList<string>? peerPackages = null)
    {
        for (var category = 0; category < CategoryCount; category++)
        {
            var offset = category * WordsPerCategory;
            var localCount = (uint)_words[offset];
            var peerCount = (uint)peer._words[offset];
            var unreadable = ((uint)(_words[offset] >> 32) | (uint)(peer._words[offset] >> 32)) != 0;
            string? difference = null;
            if (unreadable) difference = "unreadable";
            else if (localCount != peerCount)
                difference = (role == SessionRole.Host ? peerCount < localCount : localCount < peerCount) ? "missing" : "extra";
            else if (!_words.AsSpan(offset + 1, 4).SequenceEqual(peer._words.AsSpan(offset + 1, 4))) difference = "files";
            else if (!_words.AsSpan(offset + 5, 4).SequenceEqual(peer._words.AsSpan(offset + 5, 4))) difference = "mismatch";
            if (difference == null) continue;
            var code = ForgeEngine.Logging.DynamicDiagnosticCodes.Of("content-{value}-{difference}", Codes[category], difference);
            return new HandshakeEvaluation(false, code, Difference(code, peerPackages));
        }
        return new HandshakeEvaluation(true, NetworkCodes.Accepted, "");
    }

    /// <summary>The log sentence for a content code, naming the packs this level reads on this computer (the player sees
    /// the `suspended.host-mismatch` chat line; this goes to the suspension record). Empty for any other code.</summary>
    internal string Difference(string code, IReadOnlyList<string>? peerPackages = null)
    {
        for (var category = 0; category < CategoryCount; category++)
        {
            var prefix = "content-" + Codes[category] + "-";
            if (!code.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var reason = code.Substring(prefix.Length) switch
            {
                "missing" => "are missing on the joining player's computer",
                "extra" => "include extra files on the joining player's computer",
                "files" => "contain different files",
                "unreadable" => "could not be read on one of the computers",
                _ => "have different contents"
            };
            if (category == EnemyCategory)
                return "The enemy types loaded by the game differ between the two computers (" + code.Substring(prefix.Length)
                    + "). Every installed Rundown and content pack adds to them, not only this level's, and a different set makes the game "
                    + "spawn another enemy than the one asked for. Install the same packs in the same versions on both computers, check "
                    + "that neither log reports a rejected package, and rejoin to play together.";
            // A computer with no pack for the level reads nothing; the other side names what it needs.
            if (Packages.Count == 0 && peerPackages is { Count: > 0 })
                return "The Rundown package for this level is not installed on this computer: " + string.Join(", ", peerPackages) + ". Install it and rejoin to play together.";
            var level = Packages.Count == 0 ? "the host's level" : "this level (packs: " + string.Join(", ", Packages) + ")";
            return Names[category] + " of " + level + " " + reason + ". Install the same packs and rejoin to play together.";
        }
        return "";
    }
}

/// <summary>The local runtime facts a peer checks: which runtime, on which API version, against which game build.</summary>
internal readonly record struct RuntimeIdentitySummary(string Id, string Version, string ApiVersion, string GameBuild);

/// <summary>Which side of the authority split a participant runs as.</summary>
internal enum SessionRole : byte
{
    Unknown = 0,
    Host = 1,
    Client = 2
}

/// <summary>What a side tells a player it was refused by, so the difference is named on both sides: the same facts the channel's
/// content fingerprint is made of, plus the packs its level reads. It travels as the channel's content detail.</summary>
internal sealed record PeerIdentity(RuntimeIdentitySummary Runtime, int PlanCount, byte[] PlanDigest, NetworkContentSnapshot Content, IReadOnlyList<string> Packages)
{
    internal byte[] Write()
    {
        var packages = NetworkContentSnapshot.JoinPackages(Packages);
        var words = Content.Words;
        var size = 1 + Runtime.Id.Length + 1 + Runtime.Version.Length + 1 + Runtime.ApiVersion.Length + 1 + Runtime.GameBuild.Length
            + 2 + PlanDigest.Length + words.Length * sizeof(ulong) + 1 + packages.Length;
        var body = new byte[size];
        var writer = new NetWriter(body);
        writer.Text(Runtime.Id); writer.Text(Runtime.Version); writer.Text(Runtime.ApiVersion); writer.Text(Runtime.GameBuild);
        writer.UInt16((ushort)PlanCount);
        foreach (var value in PlanDigest) writer.Byte(value);
        foreach (var word in words) writer.UInt64(word);
        writer.Text(packages);
        return body;
    }

    /// <summary>A peer's detail; null when it is missing or not readable as one.</summary>
    internal static PeerIdentity? Read(byte[]? body)
    {
        if (body == null || body.Length == 0) return null;
        try
        {
            var reader = new NetReader(body);
            var runtime = new RuntimeIdentitySummary(reader.Text(), reader.Text(), reader.Text(), reader.Text());
            int planCount = reader.UInt16();
            var digest = new byte[NetworkProtocol.PlanSetDigestBytes];
            for (var index = 0; index < digest.Length; index++) digest[index] = reader.Byte();
            var words = new ulong[NetworkContentSnapshot.CategoryCount * NetworkContentSnapshot.WordsPerCategory];
            for (var index = 0; index < words.Length; index++) words[index] = reader.UInt64();
            var packages = NetworkContentSnapshot.SplitPackages(reader.Text());
            reader.RequireComplete();
            return new PeerIdentity(runtime, planCount, digest, NetworkContentSnapshot.FromWords(words), packages);
        }
        catch (NetworkContractException) { return null; }
    }
}

/// <summary>What one comparison decided, with the exact difference to report.</summary>
internal readonly record struct HandshakeEvaluation(bool Accepted, string Code, string Detail);

/// <summary>
/// What the engine's content identity is made of, pure logic. Whether two copies are the same is the channel's handshake (ForgeCore
/// <c>NetChannel</c>): both sides fingerprint <see cref="ContentText"/>, so a client never has to trust a verdict it cannot
/// reproduce. This class owns what that comparison is made of and says what differs when the channel refuses a player: the
/// refused side reads the other's <see cref="PeerIdentity"/> from the channel's content detail and names the first real difference.
/// </summary>
internal sealed class NetworkHandshake
{
    /// <summary>The packs each refused peer said its level reads, kept to explain the refusal; never compared.</summary>
    private readonly Dictionary<ulong, IReadOnlyList<string>> _peerPackages = new();
    private byte[] _digest = Array.Empty<byte>();
    private NetworkContentSnapshot? _content;

    internal NetworkHandshake(RuntimeIdentitySummary identity)
    {
        Identity = identity;
        Plans = Array.Empty<PlanIdentity>();
    }

    /// <summary>The local runtime lock this side compares every peer against. It is frozen for the process: a plan
    /// set may follow later plan discovery, the runtime identity never does.</summary>
    internal RuntimeIdentitySummary Identity { get; }

    /// <summary>The local plan set, ordered ordinally: both sides sort the same way, so the rows the digest covers
    /// are the same rows on both machines.</summary>
    internal IReadOnlyList<PlanIdentity> Plans { get; private set; }

    /// <summary>True once a plan set has been adopted, empty or not. Until then the channel holds this side's content, so no peer
    /// is compared against nothing.</summary>
    internal bool PlansAdopted => _content != null;

    /// <summary>The installed-content snapshot this side compares.</summary>
    internal NetworkContentSnapshot Content => _content ?? throw new InvalidOperationException("No plan set is adopted yet.");

    /// <summary>
    /// Adopts the plan set this side compares and advertises from now on. The canonical order and the digest are
    /// computed here, once, at adoption — never per message and never per tick. The result says whether the set
    /// really changed, so a caller can tell a repeated adoption of the same set from a new one.
    /// </summary>
    internal bool AdoptPlans(IReadOnlyList<PlanIdentity> plans, NetworkContentSnapshot content)
    {
        var canonical = Canonical(plans);
        var digest = PlanSetDigest.Of(canonical);
        var changed = _content == null || !PlanSetDigest.Equal(_digest, digest) || !_content.Same(content);
        Plans = canonical;
        _digest = digest;
        _content = content;
        if (changed) _peerPackages.Clear();
        return changed;
    }

    /// <summary>The text the channel fingerprints: everything two sides must share to play together. Only a major version is in it,
    /// the compatibility promise: the minor a plan needs is checked by each side when it loads that plan, and both hold the same plans.
    /// The message layouts are not in it: the channel compares those itself.</summary>
    internal string ContentText()
    {
        var text = new StringBuilder(Identity.Id).Append('|').Append(ForgeVersion.Major(Identity.Version))
            .Append('|').Append(ForgeVersion.Major(Identity.ApiVersion)).Append('|').Append(Identity.GameBuild).Append('|').Append(Plans.Count).Append('|').Append(Convert.ToHexString(_digest));
        foreach (var word in Content.Words) text.Append('|').Append(word.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    /// <summary>This side's identity as a refused player reads it.</summary>
    internal PeerIdentity Local() => new(Identity, Plans.Count, _digest, Content, Content.Packages);

    /// <summary>The channel refused this player: its message layouts or its content differ. The verdict names the first real difference.</summary>
    internal HandshakeEvaluation Refuse(ulong session, SessionRole role, bool formatDiffers, string reason, byte[]? detail)
    {
        var peer = PeerIdentity.Read(detail);
        if (peer != null) _peerPackages[session] = peer.Packages;
        var evaluation = formatDiffers ? new HandshakeEvaluation(false, NetworkCodes.ProtocolMismatch, reason)
            : peer == null ? new HandshakeEvaluation(false, NetworkCodes.FormatInvalid, reason)
            : Compare(peer, role);
        // The fingerprints differ, so a comparison that finds nothing has only a hash to go on.
        return evaluation.Accepted ? new HandshakeEvaluation(false, NetworkCodes.HostUnmatched, reason) : evaluation;
    }

    /// <summary>The log sentence for a content code about one peer, naming the packs that peer said its level reads.
    /// Empty for any other code.</summary>
    internal string Explain(ulong session, string code)
        => Content.Difference(code, _peerPackages.TryGetValue(session, out var named) ? named : null);

    /// <summary>
    /// What differs from a refused peer, in a fixed order so the reported reason is the first real difference rather than
    /// whichever check happened to run: runtime identity, runtime API version, game build, content, then the plan set.
    /// The identity checks are separate so a peer that runs a different runtime is told that, rather than being told its plans do not match.
    /// </summary>
    internal HandshakeEvaluation Compare(PeerIdentity peer, SessionRole role)
    {
        var identity = peer.Runtime;
        // Compatibility promise: only the leading major is breaking.
        if (identity.Id != Identity.Id || !ForgeVersion.SameMajor(identity.Version, Identity.Version))
            return new HandshakeEvaluation(false, NetworkCodes.RuntimeMismatch, "Peer runtime " + identity.Id + " " + identity.Version + " is not the same runtime and major version as " + Identity.Id + " " + Identity.Version + ".");
        if (!ForgeVersion.SameMajor(identity.ApiVersion, Identity.ApiVersion))
            return new HandshakeEvaluation(false, NetworkCodes.RuntimeMismatch, "Peer runtime API " + identity.ApiVersion + " is not the same major version as " + Identity.ApiVersion + ".");
        if (identity.GameBuild != Identity.GameBuild)
            return new HandshakeEvaluation(false, NetworkCodes.RuntimeMismatch, "Peer game build " + identity.GameBuild + " is not " + Identity.GameBuild + ".");
        var content = Content.Compare(peer.Content, role, peer.Packages);
        if (!content.Accepted) return content;
        return ComparePlans(peer.PlanCount, peer.PlanDigest);
    }

    /// <summary>
    /// The plan-set comparison: equal counts and equal digests mean the same set, because both sides hash the same
    /// rows in the same order. A different set is reported with both counts.
    /// </summary>
    internal HandshakeEvaluation ComparePlans(int peerCount, byte[] peerDigest)
    {
        if (peerCount != Plans.Count || !PlanSetDigest.Equal(_digest, peerDigest))
            return new HandshakeEvaluation(false, NetworkCodes.PlanSetMismatch, PlanDifference(peerCount));
        return new HandshakeEvaluation(true, NetworkCodes.Accepted, "");
    }

    /// <summary>How a plan-set difference is explained. The digests say only that the sets differ, so the two counts
    /// are what a reader can act on.</summary>
    internal string PlanDifference(int peerPlanCount)
        => "Installed behaviors or the objects they affect differ (other player: " + peerPlanCount
            + ", this player: " + Plans.Count + "). The declared identities, object mounts and exact execution-plan bytes must all match. Install the same packs to play together.";

    private static IReadOnlyList<PlanIdentity> Canonical(IReadOnlyList<PlanIdentity> plans)
    {
        // The plan count travels as a ushort in PeerIdentity.
        if (plans.Count > ushort.MaxValue)
            throw new NetworkContractException(NetworkCodes.PlanSetMismatch, "Installed plan count cannot be encoded in the handshake uint16 field.");
        var sorted = new List<PlanIdentity>(plans);
        sorted.Sort((left, right) => string.CompareOrdinal(left.PlanId, right.PlanId));
        for (var index = 1; index < sorted.Count; index++)
            if (sorted[index].PlanId == sorted[index - 1].PlanId)
                throw new NetworkContractException(NetworkCodes.PlanSetMismatch, "Duplicate plan id " + sorted[index].PlanId + ".");
        foreach (var plan in sorted)
        {
            if (plan.ContentDigest == null || plan.ContentDigest.Length != 64)
                throw new NetworkContractException(NetworkCodes.FormatInvalid, "A loaded plan requires its complete SHA-256 byte digest.");
            foreach (var digit in plan.ContentDigest)
                if (!(digit is >= '0' and <= '9' or >= 'A' and <= 'F'))
                    throw new NetworkContractException(NetworkCodes.FormatInvalid, "A loaded plan byte digest must use canonical SHA-256 hex.");
        }
        return sorted.AsReadOnly();
    }
}
