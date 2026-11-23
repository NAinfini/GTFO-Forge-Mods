using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ForgeEngine.Framework;

/// <summary>One published package document: its diagnostic path, full path, discovered size and install root.</summary>
public readonly record struct PublishedResource(string Relative, string Path, long Length, string Root = "")
{
    // Shared only during a cold discovery/read. Never a resource cache or a runtime lookup.
    internal PublishedReadBudget? ReadBudget { get; init; }
    // Reader accounting is not part of the published document's value identity.
    public bool Equals(PublishedResource other)
        => (Relative, Path, Length, Root) == (other.Relative, other.Path, other.Length, other.Root);
    public override int GetHashCode() => HashCode.Combine(Relative, Path, Length, Root);
}

/// <summary>
/// The directories one discovery or read pass already inspected, so a directory shared by many documents is inspected
/// once instead of once per document. A directory is remembered only after every component from it up to the install
/// root was seen to be neither a link nor a junction. This is a discovery hint, never proof of an opened handle's
/// identity. The memory lives for this pass only; every bounded read verifies its actual root and document handles.
/// </summary>
internal sealed class PublishedPathVerdicts
{
    private readonly HashSet<string> clean = new(StringComparer.Ordinal);
    // Plan discovery reads side by side; a verdict another read has not yet recorded only costs that read one more look.
    internal bool IsClean(string directory) { lock (clean) return clean.Contains(directory); }
    internal void Remember(List<string> directories) { lock (clean) foreach (var directory in directories) clean.Add(directory); }
}

internal sealed class PublishedReadBudget
{
    // Borrowed by synchronous reads only. The scan owns and disposes the pinned root.
    internal SafeFileHandle? RootIdentity { get; init; }
    internal string? RootPath { get; init; }
    private readonly Dictionary<string, long> observed = new(StringComparer.Ordinal);
    private long total;
    /// <summary>Set by a caller that reads a whole discovered batch in one pass. Without it every read inspects its own
    /// complete path chain, exactly as a lone read always did. Living here keeps the public resource's layout fixed.</summary>
    internal PublishedPathVerdicts? Verdicts { get; init; }
    /// <summary>A pass that has no shared read limit (plan discovery) still carries its path verdicts.</summary>
    internal bool Unlimited { get; init; }
    internal int Available(string path)
        => Unlimited ? int.MaxValue : (int)(Wire.PackageLimits.MaximumPublishedBytes - total + observed.GetValueOrDefault(path));
    internal void Observe(string path, long bytes)
    {
        if (Unlimited) return;
        long previous = observed.GetValueOrDefault(path);
        if (bytes <= previous) return;
        observed[path] = bytes;
        total += bytes - previous;
    }
}

/// <summary>A refused package path or document, before any caller-specific diagnostics are formatted.</summary>
public readonly record struct PublishedResourceFailure(string Relative, string Code, string Reason);

/// <summary>
/// The one discovery and read of authored package JSON, including resources, plans and settings. A package is
/// exactly one directory beneath `plugins/`; each requested `forge/` subdirectory is scanned only at its top
/// level, and hits are ordinally sorted.
///
/// The folder is shared by every resource kind a package ships, so the kind inside a document is what tells kinds
/// apart — this reader knows only the file rule and the budget, and each kind's own contract reads the payload.
/// The bytes come back as text with a strict decoder, and a document over its cap is refused from the opened stream,
/// so a huge or growing file is refused rather than loaded. Nothing here reads BepInEx or Unity: the root is the caller's, so
/// a test points it at a temporary folder and gets the same rule.
/// </summary>
public static partial class PublishedResources
{
    /// <summary>One JSON syntax policy for package documents, including plans.</summary>
    public const int MaximumJsonDepth = 64;
    public static JsonDocumentOptions JsonOptions => new()
    {
        MaxDepth = MaximumJsonDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };
    /// <summary>A document may consume the whole bounded input budget; no smaller settings-file limit.</summary>
    public const int MaximumFileBytes = Wire.PackageLimits.MaximumPublishedBytes;

    /// <summary>Runtime resources share the same document budget as every other published JSON.</summary>
    public const int MaximumResourceBytes = Wire.PackageLimits.MaximumPublishedBytes;

    /// <summary>The greatest number of runtime resources one install's packages may declare, so a directory that
    /// grew without bound is refused as a set rather than loaded one file at a time.</summary>
    public const int MaximumResources = 1024;

    /// <summary>Reads the shared runtime-resource envelope. Payload validation remains with the owning kind.</summary>
    public static bool TryResourceKind(string text, string format, string formatCode, string kindCode,
        out string? kind, out string? code, out string? reason)
    {
        kind = null; code = null; reason = null;
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(text, JsonOptions); }
        catch (JsonException error) { code = formatCode; reason = error.Message; return false; }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var declaredFormat) || declaredFormat.ValueKind != JsonValueKind.String
                || declaredFormat.GetString() != format)
            {
                code = formatCode; reason = "The file is not a " + format + " document.";
                return false;
            }
            if (!root.TryGetProperty("kind", out var declaredKind) || declaredKind.ValueKind != JsonValueKind.String)
            {
                code = kindCode; reason = "The resource document declares no kind.";
                return false;
            }
            kind = declaredKind.GetString();
            return true;
        }
    }

    /// <summary>The published revision spelling: exactly 64 lower-case hexadecimal characters.</summary>
    public static bool IsSha256(string? value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (var character in value)
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    /// <summary>Runtime resources in path order. Identical copies of one id/revision are loaded once;
    /// a different revision or content withdraws every copy, with all claiming packages named.</summary>
    public static IReadOnlyList<PublishedResource> Discover(string bepInExRoot, PublishedResourceSetCodes codes, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var documents = Discover(bepInExRoot, "forge/resources", "*.resource.forge", codes, rejected);
        if (documents.Count > MaximumResources)
        {
            rejected(codes.LimitCode + ": this install ships " + documents.Count + " runtime resources; the cap is "
                + MaximumResources + ". No runtime resource was loaded.");
            return Array.Empty<PublishedResource>();
        }
        var accepted = new List<PublishedResource>();
        SortResources(documents, (_, document) => accepted.Add(document),
            (_, relative, reason) => rejected(Diagnostic(relative, codes.DuplicateIdCode, reason)));
        return accepted;
    }

    /// <summary>The identity half of runtime-resource discovery, shared by the per-kind hand-out: each document's kind
    /// (null for an envelope that does not read, left to the owning reader's own diagnostics) and, for one id claimed with
    /// different revisions or content, the refusal of every copy. Accepted documents keep their order.</summary>
    public static void SortResources(IReadOnlyList<PublishedResource> documents, Action<string?, PublishedResource> accepted,
        Action<string?, string, string> duplicate)
    {
        var claims = new Dictionary<string, List<(PublishedResource Document, ResourceEnvelope Envelope)>>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string?>(StringComparer.Ordinal);
        // One pinned root for the whole pass, as plan discovery reads; a root that does not open leaves each read to
        // open and report its own.
        SafeFileHandle? openedRoot = null;
        try { if (documents.Count != 0) openedRoot = PublishedFileIdentity.OpenRoot(documents[0].Root); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { }
        using var pinnedRoot = openedRoot;
        var pass = pinnedRoot == null ? null : new PublishedReadBudget
        { Verdicts = new PublishedPathVerdicts(), RootIdentity = pinnedRoot, RootPath = documents[0].Root };
        foreach (var document in documents)
        {
            // Discovery only owns identity. Leave unreadable or malformed envelopes to the owning reader,
            // preserving its existing payload/syntax diagnostics rather than admitting an invalid resource.
            var envelope = Envelope(document, pass);
            kinds[document.Relative] = envelope?.Kind;
            if (envelope?.Id == null) continue;
            if (!claims.TryGetValue(envelope.Id, out var copies)) claims[envelope.Id] = copies = new();
            copies.Add((document, envelope));
        }
        var refused = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, copies) in claims)
        {
            var first = copies[0];
            // The content digest is only needed where two files with different bytes claim one id with one revision.
            if (copies.All(copy => copy.Envelope.Revision == first.Envelope.Revision)
                && (copies.All(copy => copy.Envelope.Bytes.AsSpan().SequenceEqual(first.Envelope.Bytes))
                    || copies.All(copy => Content(copy.Document, copy.Envelope) is { } content && content == Content(first.Document, first.Envelope))))
            {
                // Identical copies of one id/revision are one resource: only the first is handed on.
                foreach (var copy in copies.Skip(1)) refused.Add(copy.Document.Relative);
                continue;
            }
            foreach (var copy in copies) refused.Add(copy.Document.Relative);
            duplicate(first.Envelope.Kind, first.Document.Relative, "Resource id `" + id
                + "` has different revisions or content in " + string.Join(", ", copies.Select(copy =>
                    copy.Document.Relative + " (revision=" + copy.Envelope.Revision + ")")) + "; all copies are refused.");
        }
        foreach (var document in documents)
            if (!refused.Contains(document.Relative)) accepted(kinds[document.Relative], document);
    }

    /// <summary>What the identity pass reads from one runtime resource envelope: the reference it claims (null when it
    /// claims none) and the kind it declares (null when it declares none).</summary>
    private sealed class ResourceEnvelope
    {
        internal string? Id, Revision, Kind;
        internal string? Content;
        // The digest of the file's exact bytes: equal bytes are equal content, so identical copies need no canonical form.
        internal byte[] Bytes = Array.Empty<byte>();
    }

    // Every resource reader (area fields, sounds, profiles, animations) runs this identity pass over the whole folder, at
    // startup once per reader and again at every new world, so it reads only the envelope; the canonical content digest
    // of the payload is computed for an id that two documents claim, not for every document.
    // The bytes are checked exactly as a text read checks them (strict UTF-8, one JSON value under the shared syntax
    // policy), but with one forward pass and no decoded text or document tree: the payload is only skipped over.
    private static ResourceEnvelope? Envelope(PublishedResource document, PublishedReadBudget? pass)
    {
        ResourceEnvelope? envelope = null;
        ReadCore(pass == null ? document : document with { ReadBudget = pass }, MaximumResourceBytes, (_, _) => { }, bytes =>
        {
            envelope = ReadEnvelope(bytes.Span);
            if (envelope.Id != null) envelope.Bytes = SHA256.HashData(bytes.Span);
        }, out var observed);
        // The caller's own budget is charged with what this pass read, as when the pass read through it.
        if (pass != null) document.ReadBudget?.Observe(document.Relative, observed);
        return envelope;
    }

    private static ResourceEnvelope ReadEnvelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        StrictUtf8.GetCharCount(bytes);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            MaxDepth = MaximumJsonDepth,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        string? format = null, kind = null, id = null, revision = null;
        reader.Read();
        if (reader.TokenType == JsonTokenType.StartObject)
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                static string? Text(ref Utf8JsonReader value) => value.TokenType == JsonTokenType.String ? value.GetString() : null;
                // A repeated member takes its last value, as a parsed document's lookup does.
                if (name == "format") format = Text(ref reader);
                else if (name == "kind") kind = Text(ref reader);
                else if (name == "reference")
                {
                    id = revision = null;
                    if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var member = reader.GetString();
                        reader.Read();
                        if (member == "id") id = Text(ref reader);
                        else if (member == "revision") revision = Text(ref reader);
                        else reader.Skip();
                    }
                }
                else reader.Skip();
            }
        else reader.Skip();
        // Anything after the one value is a syntax error, as it is for a parsed document.
        while (reader.Read()) { }
        var envelope = new ResourceEnvelope();
        if (format != "gtfo-forge-engine-resource") return envelope;
        envelope.Kind = kind;
        if (id != null && revision != null) { envelope.Id = id; envelope.Revision = revision; }
        return envelope;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // The canonical content digest of one claimed document, read when first needed; null when it no longer reads.
    private static string? Content(PublishedResource document, ResourceEnvelope envelope)
    {
        if (envelope.Content != null) return envelope.Content;
        if (!TryRead(document, MaximumResourceBytes, _ => { }, out var text)) return null;
        using var parsed = JsonDocument.Parse(text!, JsonOptions);
        return envelope.Content = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RuntimeJson.Canonical(parsed.RootElement))));
    }


    /// <summary>Finds matching files directly under each package's forge directory. No package or document
    /// directory is followed through a link, and a failed inspection is reported by its relative path.</summary>
    public static IReadOnlyList<PublishedResource> Discover(string bepInExRoot, string forgeDirectory, string pattern,
        PublishedDiscoveryCodes codes, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        return Discover(bepInExRoot, forgeDirectory, pattern, codes,
            (PublishedResourceFailure failure) => rejected(Diagnostic(failure.Relative, failure.Code, failure.Reason)));
    }

    /// <summary>The same walk for a caller that has no use for a refusal: whatever cannot be inspected is left out.</summary>
    public static IReadOnlyList<PublishedResource> Discover(string bepInExRoot, string forgeDirectory, string pattern)
        => Discover(bepInExRoot, forgeDirectory, pattern, (_, _, _) => { });

    /// <summary>Structured refusals let the plan loader preserve its public plan rejection codes. The pattern is one
    /// exact name, one suffix pattern such as `*.plan.forge`, or several of either joined by `|`. Several patterns
    /// share one walk of the install instead of one walk each: every pattern keeps its own read budget, a document
    /// belongs to the first pattern it satisfies, and the hits of all of them come back in one ordinal order.</summary>
    public static IReadOnlyList<PublishedResource> Discover(string bepInExRoot, string forgeDirectory, string pattern,
        PublishedDiscoveryCodes codes, Action<PublishedResourceFailure> rejected)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(rejected);
        return Discover(bepInExRoot, forgeDirectory, pattern,
            (relative, refusal, reason) => rejected(new(relative, CodeOf(codes, refusal), reason)));
    }

    /// <summary>The same walk over package directories the caller already found (<c>plugins/&lt;package&gt;</c>, ordinal), so the
    /// one install discovery (<c>ContentPackages</c>) never lists <c>plugins/</c> again for each kind it hands out.</summary>
    public static IReadOnlyList<PublishedResource> Discover(string bepInExRoot, IReadOnlyList<string> packages, string forgeDirectory,
        string pattern, PublishedDiscoveryCodes codes, Action<PublishedResourceFailure> rejected)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(rejected);
        return Discover(bepInExRoot, forgeDirectory, pattern,
            (relative, refusal, reason) => rejected(new(relative, CodeOf(codes, refusal), reason)), packages);
    }

    /// <summary>Every package directory directly under <c>plugins/</c> (<c>plugins/&lt;package&gt;</c>, ordinal), links and junctions
    /// left out. Nothing beneath a package is read.</summary>
    public static IReadOnlyList<string> PackageDirectories(string bepInExRoot, Action<PublishedResourceFailure> rejected)
    {
        var found = new List<string>();
        try
        {
            bepInExRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bepInExRoot));
            var pluginsRoot = ResolveWithinRoot(bepInExRoot, "plugins");
            if (!Directory.Exists(pluginsRoot)) return found;
            foreach (var directory in new DirectoryInfo(pluginsRoot).GetDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                { rejected(new("plugins/" + directory.Name, "resource-path", "Framework paths cannot traverse links or junctions.")); continue; }
                found.Add("plugins/" + directory.Name);
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { rejected(new("plugins", "resource-unreadable", error.Message)); }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static IReadOnlyList<PublishedResource> Discover(string bepInExRoot, string forgeDirectory, string pattern,
        Action<string, PublishedRefusal, string> rejected, IReadOnlyList<string>? packages = null)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(forgeDirectory);
        ArgumentNullException.ThrowIfNull(pattern);
        var patterns = pattern.Split('|');
        foreach (var each in patterns)
            if (each.Length == 0 || each.Contains('?') || each.IndexOf('*', 1) >= 0
                || (each[0] != '*' && each.Contains('*')))
                throw new ArgumentException("Only exact names and suffix patterns are supported.", nameof(pattern));
        var hits = new List<PublishedResource>();
        var budgets = new PublishedReadBudget[patterns.Length];
        var verdicts = new PublishedPathVerdicts();
        for (var index = 0; index < budgets.Length; index++) budgets[index] = new PublishedReadBudget { Verdicts = verdicts };
        Walk(bepInExRoot, forgeDirectory, patterns, verdicts, budgets, (_, relative, refusal, reason) => rejected(relative, refusal, reason),
            (_, hit) => hits.Add(hit), packages);
        hits.Sort((left, right) => string.CompareOrdinal(left.Relative, right.Relative));
        return hits;
    }

    /// <summary>The one walk of an install: every package directory beneath `plugins/`, the requested forge
    /// subdirectory of each, and the documents directly in it that satisfy one of the patterns. A directory is
    /// inspected for links once per walk, and a document's own kind and size come from the listing of its directory,
    /// so a package of many documents costs one listing, not one inspection per document. The refusals are exactly the
    /// ones an inspection of every component would report; <paramref name="rejected"/> and
    /// <paramref name="found"/> receive the index of the pattern a document matched, and -1 for a refusal that belongs
    /// to a directory.</summary>
    private static void Walk(string bepInExRoot, string forgeDirectory, string[] patterns,
        PublishedPathVerdicts verdicts, PublishedReadBudget[] budgets, Action<int, string, PublishedRefusal, string> rejected,
        Action<int, PublishedResource> found, IReadOnlyList<string>? packages = null)
    {
        try { bepInExRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bepInExRoot)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            rejected(-1, "plugins", PublishedRefusal.Path, error.Message);
            return;
        }
        void Report(int pattern, string relative, Exception error) => rejected(pattern, relative,
            error is InvalidDataException ? PublishedRefusal.Path : PublishedRefusal.Unreadable, error.Message);
        string pluginsRoot;
        string[] pluginDirectories;
        try
        {
            pluginsRoot = ResolveWithinRoot(bepInExRoot, "plugins", verdicts);
            if (!Directory.Exists(pluginsRoot)) return;
            pluginDirectories = packages == null ? Directory.GetDirectories(pluginsRoot)
                : packages.Select(package => Path.Combine(bepInExRoot, package.Replace('/', Path.DirectorySeparatorChar))).ToArray();
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Report(-1, "plugins", error);
            return;
        }
        var suffixes = new string?[patterns.Length];
        for (var index = 0; index < patterns.Length; index++) suffixes[index] = patterns[index][0] == '*' ? patterns[index][1..] : null;
        var forgeRelative = forgeDirectory.Replace('/', Path.DirectorySeparatorChar);
        foreach (var discoveredPluginDirectory in pluginDirectories)
        {
            var pluginRelative = Path.GetRelativePath(bepInExRoot, discoveredPluginDirectory);
            var documentsRelative = Path.Combine(pluginRelative, forgeRelative);
            FileSystemInfo[] documents;
            try
            {
                ResolveWithinRoot(bepInExRoot, pluginRelative, verdicts);
                var directory = ResolveWithinRoot(bepInExRoot, documentsRelative, verdicts);
                if (!Directory.Exists(directory)) continue;
                documents = new DirectoryInfo(directory).GetFileSystemInfos();
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Report(-1, documentsRelative.Replace(Path.DirectorySeparatorChar, '/'), error);
                continue;
            }
            foreach (var entry in documents)
            {
                var name = entry.Name;
                var match = -1;
                for (var index = 0; index < patterns.Length && match < 0; index++)
                    if (suffixes[index] is { } suffix ? name.EndsWith(suffix, StringComparison.Ordinal)
                        : string.Equals(name, patterns[index], StringComparison.Ordinal)) match = index;
                if (match < 0) continue;
                var relativePath = Path.GetRelativePath(bepInExRoot, entry.FullName);
                try
                {
                    RequireRelative(relativePath);
                    var document = Locate(bepInExRoot, relativePath);
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Framework paths cannot traverse links or junctions.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        throw new InvalidDataException("Package document path is a directory.");
                    var relative = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                    found(match, new PublishedResource(relative, document, ((FileInfo)entry).Length, bepInExRoot)
                    { ReadBudget = budgets[match] });
                }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Report(match, relativePath.Replace(Path.DirectorySeparatorChar, '/'), error);
                    continue;
                }
            }
        }
    }

    /// <summary>Reads one discovered document up to the cap, answering false and reporting the one refusal when the
    /// file is too large or cannot be decoded. A document this answers true for is the file's own bytes as text,
    /// and its grammar is the caller's.</summary>
    public static bool TryRead(PublishedResource document, int maximumBytes, PublishedTextCodes codes, Action<string> rejected,
        out string? text)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        return TryRead(document, maximumBytes, codes,
            (PublishedResourceFailure failure) => rejected(Diagnostic(failure.Relative, failure.Code, failure.Reason)),
            out text, out _);
    }

    /// <summary>Reads from the opened stream, checks growth, decodes strict UTF-8, and validates JSON syntax.
    /// Raw bytes are returned for contracts that require an exact canonical byte comparison.</summary>
    public static bool TryRead(PublishedResource document, int maximumBytes, PublishedTextCodes codes,
        Action<PublishedResourceFailure> rejected, out string? text, out byte[]? rawBytes)
        => TryRead(document, maximumBytes, codes, rejected, out text, out rawBytes, out _);

    // The content-source adapter settles both successful and failed reads from this same stream's counter.
    internal static bool TryRead(PublishedResource document, int maximumBytes, PublishedTextCodes codes,
        Action<PublishedResourceFailure> rejected, out string? text, out byte[]? rawBytes, out long observedBytes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(rejected);
        return ReadText(document, maximumBytes, Refused(document, codes, rejected), out text, out rawBytes, out observedBytes);
    }

    /// <summary>The same read for a caller that reports only why a document was refused.</summary>
    public static bool TryRead(PublishedResource document, int maximumBytes, Action<string> reason, out string? text)
        => TryRead(document, maximumBytes, reason, out text, out _, out _);

    /// <summary>The same read for a caller that reports only why a document was refused and needs the raw bytes.</summary>
    public static bool TryRead(PublishedResource document, int maximumBytes, Action<string> reason,
        out string? text, out byte[]? rawBytes)
        => TryRead(document, maximumBytes, reason, out text, out rawBytes, out _);

    // The same adapter, reporting only why a read was refused.
    internal static bool TryRead(PublishedResource document, int maximumBytes, Action<string> reason,
        out string? text, out byte[]? rawBytes, out long observedBytes)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return ReadText(document, maximumBytes, (_, why) => reason(why), out text, out rawBytes, out observedBytes);
    }

    /// <summary>Reads exact bytes through the same bounded, handle-verified reader as published JSON. Binary
    /// payloads need no text decoding or JSON syntax; callers validate their own format from these bytes.</summary>
    public static bool TryReadBytes(PublishedResource document, int maximumBytes, PublishedBytesCodes codes,
        Action<PublishedResourceFailure> rejected, out byte[]? rawBytes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(rejected);
        return ReadBytes(document, maximumBytes, Refused(document, codes, rejected), out rawBytes);
    }

    /// <summary>The same read for a caller that reports only why a document was refused.</summary>
    public static bool TryReadBytes(PublishedResource document, int maximumBytes, Action<string> reason, out byte[]? rawBytes)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return ReadBytes(document, maximumBytes, (_, why) => reason(why), out rawBytes);
    }

    /// <summary>A file named <c>&lt;lower-case SHA-256&gt;&lt;extension&gt;</c> read through the one bounded, handle-verified reader (with the
    /// package's aggregate read budget; no file has a private read path) and proved to hold exactly the bytes its name hashes.</summary>
    public static bool TryReadHashed(PublishedResource document, string extension, PublishedBytesCodes codes, string nameCode, string nameReason,
        string sizeCode, string sizeReason, string hashCode, string hashReason, out string sha256, out byte[]? bytes,
        out PublishedResourceFailure failure)
    {
        bytes = null;
        string file = System.IO.Path.GetFileName(document.Relative);
        sha256 = file.EndsWith(extension, StringComparison.Ordinal) ? file[..^extension.Length] : "";
        if (!IsSha256(sha256)) { failure = new(document.Relative, nameCode, nameReason); return false; }
        if (document.Length <= 0) { failure = new(document.Relative, sizeCode, sizeReason); return false; }
        PublishedResourceFailure refused = default;
        if (!TryReadBytes(document, MaximumResourceBytes, codes, reason => refused = reason, out bytes))
        {
            failure = refused;
            return false;
        }
        string actual = ForgeCore.Hash.Sha256Hex(bytes!);
        if (!string.Equals(actual, sha256, StringComparison.Ordinal)) { failure = new(document.Relative, hashCode, hashReason); return false; }
        failure = default;
        return true;
    }

    /// <summary>The package a published file belongs to: <c>plugins/&lt;package&gt;</c>, or <c>plugins</c> for a path outside every package.</summary>
    public static string PackageRoot(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        return parts.Length >= 2 && parts[0] == "plugins" && parts[1].Length != 0 ? "plugins/" + parts[1] : "plugins";
    }

    private static bool ReadBytes(PublishedResource document, int maximumBytes, Action<PublishedRefusal, string> refused,
        out byte[]? rawBytes)
    {
        byte[]? data = null;
        var read = ReadCore(document, maximumBytes, refused, bytes => data = bytes.ToArray(), out _);
        rawBytes = read ? data : null;
        return read;
    }

    private static bool ReadText(PublishedResource document, int maximumBytes, Action<PublishedRefusal, string> refused,
        out string? text, out byte[]? rawBytes, out long observedBytes)
    {
        string? decoded = null;
        byte[]? data = null;
        var read = ReadCore(document, maximumBytes, refused, bytes =>
        {
            var copy = bytes.ToArray();
            var value = new UTF8Encoding(false, true).GetString(copy);
            if (value.Length != 0 && value[0] == '\uFEFF') value = value[1..];
            using (JsonDocument.Parse(value, JsonOptions)) { }
            data = copy;
            decoded = value;
        }, out observedBytes);
        text = read ? decoded : null;
        rawBytes = read ? data : null;
        return read;
    }

    /// <summary>The same read for a plan file, which is decoded, checked and parsed exactly once: the document that
    /// comes back carries the parsed value, the runtime's validation verdict and the digest of the file's bytes, so
    /// no later step reads or parses the file again. The refusals and their codes are those of
    /// <c>TryRead</c>.</summary>
    internal static bool TryReadPlan(PublishedResource document, int maximumBytes, PublishedTextCodes codes,
        Action<PublishedResourceFailure> rejected, out PlanDocument? plan)
    {
        PlanDocument? read = null;
        var accepted = ReadCore(document, maximumBytes, Refused(document, codes, rejected), bytes =>
        {
            var parsed = PlanDocument.FromFile(bytes);
            if (parsed.SyntaxError != null) throw new JsonException(parsed.SyntaxError);
            read = parsed;
        }, out _);
        plan = accepted ? read : null;
        return accepted;
    }

    /// <summary>The same guarded read for startup's plan index: the bytes are hashed and only the plan's head is read
    /// (<see cref="PlanIndexEntry"/>); nothing is decoded or kept. The file is read again, in full, when a level needs it.</summary>
    internal static bool TryReadPlanIndex(PublishedResource document, int maximumBytes, PublishedTextCodes codes,
        Action<PublishedResourceFailure> rejected, out PlanIndexEntry? entry)
    {
        PlanIndexEntry? read = null;
        var accepted = ReadCore(document, maximumBytes, Refused(document, codes, rejected),
            bytes => read = PlanIndexEntry.Read(document.Relative, bytes.Span), out _);
        entry = accepted ? read : null;
        return accepted;
    }

    private static Action<PublishedRefusal, string> Refused(PublishedResource document, PublishedDiscoveryCodes codes,
        Action<PublishedResourceFailure> rejected)
        => (refusal, reason) => rejected(new(document.Relative, CodeOf(codes, refusal), reason));

    /// <summary>The caller's fixed code for one refusal. A read never reports a refusal its codes do not name:
    /// a byte read decodes nothing, so it has no JSON or UTF-8 refusal.</summary>
    private static string CodeOf(PublishedDiscoveryCodes codes, PublishedRefusal refusal) => refusal switch
    {
        PublishedRefusal.Path => codes.PathCode,
        PublishedRefusal.Unreadable => codes.UnreadableCode,
        PublishedRefusal.FileSize when codes is PublishedBytesCodes bytes => bytes.FileSizeCode,
        PublishedRefusal.Json when codes is PublishedTextCodes text => text.JsonCode,
        PublishedRefusal.Utf8 when codes is PublishedTextCodes text => text.Utf8Code,
        _ => throw new InvalidOperationException("These codes name no " + refusal + " refusal.")
    };
    /// <summary>Pins the root, verifies the opened document handle, reads it into a pooled buffer under
    /// the cap and hands the bytes to <paramref name="accept"/> for the caller's own decoding. The buffer is only
    /// valid during that call. Everything <paramref name="accept"/> throws is mapped to the one refusal its kind has.</summary>
    private static bool ReadCore(PublishedResource document, int maximumBytes, Action<PublishedRefusal, string> refused,
        Action<ReadOnlyMemory<byte>> accept, out long observedBytes)
    {
        ArgumentNullException.ThrowIfNull(refused);
        observedBytes = 0;
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (document.ReadBudget != null)
            maximumBytes = Math.Min(maximumBytes, document.ReadBudget.Available(document.Relative));
        byte[]? buffer = null;
        try
        {
            if (!string.IsNullOrEmpty(document.Root))
            {
                var resolved = ResolveWithinRoot(document.Root, document.Relative, document.ReadBudget?.Verdicts);
                if (!string.Equals(resolved, document.Path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package document path changed after discovery.");
            }
            var borrowedRoot = document.ReadBudget?.RootIdentity;
            if (borrowedRoot is { IsClosed: true } or { IsInvalid: true })
                throw new InvalidDataException("The pinned read root has expired.");
            if (borrowedRoot != null && !string.Equals(document.ReadBudget!.RootPath, document.Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pinned read root does not own this document.");
            using var ownedRoot = string.IsNullOrEmpty(document.Root) || borrowedRoot != null
                ? null : PublishedFileIdentity.OpenRoot(document.Root);
            var rootIdentity = borrowedRoot ?? ownedRoot;
            FileStream? prepared = null;
            PrepareDocument(ref prepared, document.Path);
            using var stream = prepared ?? new FileStream(document.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            if (rootIdentity != null)
            {
                PublishedFileIdentity.VerifyRoot(rootIdentity, document.Root);
                PublishedFileIdentity.VerifyDocument(stream.SafeFileHandle, document.Path);
            }
            if (stream.Length > maximumBytes)
            {
                refused(PublishedRefusal.FileSize, "The document is " + stream.Length + " bytes; the cap is " + maximumBytes + ".");
                return false;
            }
            document.ReadBudget?.Observe(document.Relative, stream.Length);
            // One byte more than the file, so the read that finds the end of it needs no second buffer.
            buffer = ArrayPool<byte>.Shared.Rent((int)stream.Length + 1);
            long total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, (long)maximumBytes + 1));
                    Buffer.BlockCopy(buffer, 0, larger, 0, (int)total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
                var read = stream.Read(buffer, (int)total, (int)Math.Min(buffer.Length - total, (long)maximumBytes + 1 - total));
                if (read <= 0) break;
                total += read;
                observedBytes = total;
                if (total > maximumBytes)
                {
                    refused(PublishedRefusal.FileSize, "The document grew beyond the " + maximumBytes + " byte cap while it was read.");
                    return false;
                }
                document.ReadBudget?.Observe(document.Relative, total);
            }
            // A cached directory verdict is only a hint. Prove the same opened handle again before admission.
            if (rootIdentity != null) PublishedFileIdentity.VerifyDocument(stream.SafeFileHandle, document.Path);
            accept(buffer.AsMemory(0, (int)total));
        }
        catch (InvalidDataException error)
        {
            refused(PublishedRefusal.Path, error.Message);
            return false;
        }
        catch (JsonException error)
        {
            refused(PublishedRefusal.Json, error.Message);
            return false;
        }
        catch (DecoderFallbackException error)
        {
            refused(PublishedRefusal.Utf8, error.Message);
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            refused(PublishedRefusal.Unreadable, error.Message);
            return false;
        }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
        }
        return true;
    }

    // Source-linked race injection only; absent from the shipped assembly.
    static partial void PrepareDocument(ref FileStream? stream, string path);

    /// <summary>Resolves one BepInEx-relative path and rejects traversal through a link or junction.</summary>
    public static string ResolveWithinRoot(string root, string relative) => ResolveWithinRoot(root, relative, null);

    /// <summary>The same resolution, remembering in <paramref name="verdicts"/> each directory found clean so that the
    /// next path through it stops inspecting at the first directory already known to be clean. Every component that is
    /// not yet known is inspected exactly as before, so the answer for a given file system is the same.</summary>
    internal static string ResolveWithinRoot(string root, string relative, PublishedPathVerdicts? verdicts)
    {
        RequireRelative(relative);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Locate(root, relative);
        List<string>? clean = verdicts == null ? null : new List<string>(8);
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (verdicts != null && verdicts.IsClean(current)) break;
            // A path component that does not exist cannot be a link; reading the file reports its absence.
            if (TryGetAttributes(current, out var attributes))
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Framework paths cannot traverse links or junctions.");
                if (clean != null && (attributes & FileAttributes.Directory) != 0) clean.Add(current);
            }
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        if (verdicts != null) verdicts.Remember(clean!);
        return path;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { attributes = default; return false; }
    }

    /// <summary>Names here come from packages and works: an absolute path, a drive or a `..` step is refused outright,
    /// even one that would land back inside the root after normalization. Either separator is accepted, since some
    /// callers build the relative path with the system's own.</summary>
    private static void RequireRelative(string relative)
    {
        if (!ForgeCore.PosixPath.IsRelativeFile(ForgeCore.PosixPath.ToPosix(relative)))
            throw new InvalidDataException("Framework path must be relative to BepInEx, without `..`.");
    }

    /// <summary>The full path of a relative one under an already full, separator-trimmed root, refused when it leaves
    /// the root. Purely lexical: nothing on disk is touched.</summary>
    private static string Locate(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Framework path escapes BepInEx.");
        return path;
    }

    /// <summary>One refusal in the shape every package-content diagnostic uses: the code, the document it came
    /// from and the first thing that could not be read.</summary>
    public static string Diagnostic(string relative, string code, string reason)
        => code + " file=" + relative + ": " + reason;
}
