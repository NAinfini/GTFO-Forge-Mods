using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>
/// I-RESOLVE (Docs/forge-contract FORGE-FRAMEWORK §3.2): the one discovery of the installed packages, the hand-out of
/// each kind of document to the package that reads it, and the one lookup of a hash-named file.
///
/// <see cref="Discover"/> lists <c>plugins/</c> once per install root (ForgeEngine does it at its own Load, before any
/// base package loads) and reads every package's <c>manifest.json</c> dependency order. A base package never walks the
/// install itself: it names the kind of document it reads — a file pattern under <c>forge/</c> (<see cref="Read(string, string, string, string, PublishedDiscoveryCodes, Action{PublishedResourceFailure})"/>),
/// a runtime-resource kind (<see cref="ReadResources"/>) or the Rundown packs (<see cref="ReadRundowns"/>) — and gets
/// that kind's documents from the packages discovery found. Runtime resources are sorted by kind once per install,
/// and the Rundown packs are read once per install, whoever asks first; every hand-out is one <c>content.read</c>
/// record naming the reader, the kind and the counts.
///
/// A file is found by its name alone (<see cref="FindResource"/>): the asking package's
/// <c>forge/resources/&lt;hash&gt;&lt;ext&gt;</c> first, then each declared dependency's in manifest order. Nothing is
/// scanned into an index and no file is cached; a file none of them ships is an Error <see cref="MissingCode"/> that
/// names the package, the hash and who asked. Host-side (not part of the public SDK fingerprint).
/// </summary>
internal static class ContentPackages
{
    public const string MissingCode = "resource-missing";
    /// <summary>The reader name of ForgeEngine's own reads (plans, content entries, area fields).</summary>
    internal const string EngineReader = "NAinfini.ForgeEngine";
    /// <summary>Info. Once per install, at ForgeEngine's Load: the one discovery of the installed packages (values:
    /// packages, manifests, elapsedMs).</summary>
    public const string DiscoveredCode = "content.discovered";
    /// <summary>Info. One base package was handed one kind of document from that discovery (values: reader, kind,
    /// documents, packages).</summary>
    public const string ReadCode = "content.read";
    /// <summary>The host's Info writer for <see cref="DiscoveredCode"/> and <see cref="ReadCode"/>, and its <c>content.rejected</c>
    /// writer (code, path, reason) for a discovery refusal no reader owns; both null offline, where nothing is written.</summary>
    internal static Action<string, RuntimeLogValue[]>? Record { get; set; }
    internal static Action<string, string, string>? Refused { get; set; }
    private static readonly Regex DependencyPattern = new(@"\A([A-Za-z0-9_]{1,128}-[A-Za-z0-9_]{1,128})-(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant);
    private static readonly PublishedDiscoveryCodes ResourceCodes = new("resource-path", "resource-unreadable");
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Install> Installs = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Install
    {
        internal Install(IReadOnlyList<string> packages, IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
        { Packages = packages; Dependencies = dependencies; }
        internal IReadOnlyList<string> Packages { get; }
        internal IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies { get; }
        internal Dictionary<string, List<PublishedResource>>? Resources;
        internal List<(string? Kind, string Relative, string Reason)>? ResourceConflicts;
        internal int ResourceCount;
        internal IReadOnlyList<RundownPack>? Rundowns;
        internal IReadOnlyList<RundownDiagnostic>? RundownDiagnostics;
    }

    /// <summary>The one discovery of an install: every package directory under <c>plugins/</c> (<c>plugins/&lt;Team-Name&gt;</c>,
    /// ordinal) with its manifest dependency order. The first call for a root does the work and writes
    /// <c>content.discovered</c>; every later call and every reader below gets the same answer.</summary>
    public static IReadOnlyList<string> Discover(string bepInExRoot) => Of(bepInExRoot).Packages;

    /// <summary>The author's explicit reload (Authoring only): forget this install and discover it again, so a package
    /// added since startup is seen. Play never calls it.</summary>
    internal static IReadOnlyList<string> Rediscover(string bepInExRoot)
    {
        lock (Gate) Installs.Remove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bepInExRoot)));
        return Discover(bepInExRoot);
    }

    /// <summary>The packages whose files <paramref name="package"/> (<c>plugins/&lt;Team-Name&gt;</c>) may use: itself, then its
    /// manifest dependencies in declared order.</summary>
    public static IReadOnlyList<string> SearchOrder(string bepInExRoot, string package)
    {
        var order = new List<string> { package };
        if (Of(bepInExRoot).Dependencies.TryGetValue(package, out var dependencies))
            foreach (var dependency in dependencies) if (!order.Contains(dependency)) order.Add(dependency);
        return order;
    }

    /// <summary>The file <c>&lt;sha256&gt;&lt;extension&gt;</c> as <paramref name="package"/> sees it, or null after reporting
    /// <see cref="MissingCode"/> through <paramref name="missing"/>. The caller reads it with the bounded reader, which also
    /// proves the bytes match the name.</summary>
    public static PublishedResource? FindResource(string bepInExRoot, string package, string sha256, string extension, string requester,
        Action<PublishedResourceFailure> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bepInExRoot)), file = "/forge/resources/" + sha256 + extension;
        var order = SearchOrder(root, package);
        foreach (var candidate in order)
        {
            string relative = candidate + file, path;
            try { path = PublishedResources.ResolveWithinRoot(root, relative); }
            catch (InvalidDataException) { continue; }
            var info = new FileInfo(path);
            if (info.Exists) return new PublishedResource(relative, path, info.Length, root);
        }
        // The log line is length-bounded, so the root cause (who needs which file) comes first and the long search order last.
        missing(new(package + file, MissingCode, requester + " needs " + sha256 + extension + "; package " + package + " and its dependencies ("
            + string.Join(", ", order) + ") ship no such file."));
        return null;
    }

    /// <summary>The documents of one kind every discovered package ships: files matching <paramref name="pattern"/> (an
    /// exact name, a suffix pattern such as <c>*.forge</c>, or several joined by <c>|</c>) directly under
    /// <c>&lt;package&gt;/&lt;forgeDirectory&gt;</c>, ordinal. <paramref name="reader"/> names the base package for the
    /// <c>content.read</c> record; a path that cannot be inspected is refused with <paramref name="codes"/>.</summary>
    public static IReadOnlyList<PublishedResource> Read(string bepInExRoot, string reader, string forgeDirectory, string pattern,
        PublishedDiscoveryCodes codes, Action<PublishedResourceFailure> rejected)
    {
        var install = Of(bepInExRoot);
        var documents = PublishedResources.Discover(bepInExRoot, install.Packages, forgeDirectory, pattern, codes, rejected);
        Handed(reader, forgeDirectory + "/" + pattern, documents);
        return documents;
    }

    /// <summary>The same hand-out for a reader that reports a refusal as one formatted line.</summary>
    public static IReadOnlyList<PublishedResource> Read(string bepInExRoot, string reader, string forgeDirectory, string pattern,
        PublishedDiscoveryCodes codes, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        return Read(bepInExRoot, reader, forgeDirectory, pattern, codes,
            (PublishedResourceFailure failure) => rejected(PublishedResources.Diagnostic(failure.Relative, failure.Code, failure.Reason)));
    }

    /// <summary>The same hand-out for a reader that has no use for a refusal.</summary>
    public static IReadOnlyList<PublishedResource> Read(string bepInExRoot, string reader, string forgeDirectory, string pattern)
        => Read(bepInExRoot, reader, forgeDirectory, pattern, ResourceCodes, (PublishedResourceFailure _) => { });

    /// <summary>The runtime resources (<c>forge/resources/*.resource.forge</c>) of one <paramref name="kind"/>, in path
    /// order. Every resource of the install is read once, by whoever asks first, and sorted by the kind its envelope
    /// declares; identical copies of one id/revision are handed out once, and an id claimed with different revisions or
    /// content refuses every copy with <paramref name="codes"/>' duplicate code. An install over
    /// <see cref="PublishedResources.MaximumResources"/> hands out nothing. A document whose envelope does not read is
    /// handed to no kind and refused once, with ForgeEngine's own code, in <c>content.rejected</c>.</summary>
    public static IReadOnlyList<PublishedResource> ReadResources(string bepInExRoot, string reader, string kind,
        PublishedResourceSetCodes codes, Action<string> rejected)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(rejected);
        var install = Of(bepInExRoot);
        Dictionary<string, List<PublishedResource>> byKind;
        List<(string? Kind, string Relative, string Reason)> conflicts;
        lock (Gate)
        {
            if (install.Resources == null) SortResources(bepInExRoot, install);
            byKind = install.Resources!;
            conflicts = install.ResourceConflicts!;
        }
        if (install.ResourceCount > PublishedResources.MaximumResources)
        {
            rejected(codes.LimitCode + ": this install ships " + install.ResourceCount + " runtime resources; the cap is "
                + PublishedResources.MaximumResources + ". No runtime resource was loaded.");
            Handed(reader, "resource:" + kind, Array.Empty<PublishedResource>());
            return Array.Empty<PublishedResource>();
        }
        foreach (var conflict in conflicts)
            if (conflict.Kind == kind) rejected(PublishedResources.Diagnostic(conflict.Relative, codes.DuplicateIdCode, conflict.Reason));
        IReadOnlyList<PublishedResource> documents = byKind.TryGetValue(kind, out var found) ? found.ToArray() : Array.Empty<PublishedResource>();
        Handed(reader, "resource:" + kind, documents);
        return documents;
    }

    /// <summary>The installed Rundown packs (<see cref="RundownPacks.Discover"/>), read once per install by whoever asks
    /// first. Every reader gets the same packs and every diagnostic that one read reported.</summary>
    public static IReadOnlyList<RundownPack> ReadRundowns(string bepInExRoot, string reader, Action<RundownDiagnostic> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var install = Of(bepInExRoot);
        IReadOnlyList<RundownPack> packs;
        IReadOnlyList<RundownDiagnostic> diagnostics;
        lock (Gate)
        {
            if (install.Rundowns == null)
            {
                var found = new List<RundownDiagnostic>();
                install.Rundowns = RundownPacks.Discover(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bepInExRoot)), found.Add);
                install.RundownDiagnostics = found;
            }
            packs = install.Rundowns;
            diagnostics = install.RundownDiagnostics!;
        }
        foreach (var diagnostic in diagnostics) report(diagnostic);
        Handed(reader, "forge/rundown.forge", packs.Select(pack => pack.Path).ToArray());
        return packs;
    }

    private static void SortResources(string bepInExRoot, Install install)
    {
        var documents = PublishedResources.Discover(bepInExRoot, install.Packages, "forge/resources", "*.resource.forge", ResourceCodes,
            failure => Rejected(failure.Code, failure.Relative, failure.Reason));
        var byKind = new Dictionary<string, List<PublishedResource>>(StringComparer.Ordinal);
        var conflicts = new List<(string? Kind, string Relative, string Reason)>();
        install.ResourceCount = documents.Count;
        if (documents.Count <= PublishedResources.MaximumResources)
            PublishedResources.SortResources(documents, (kind, document) =>
            {
                if (kind == null)
                {
                    Rejected(ResourceCodes.UnreadableCode, document.Relative, "The runtime resource envelope does not read; no reader is given it.");
                    return;
                }
                if (!byKind.TryGetValue(kind, out var rows)) byKind[kind] = rows = new();
                rows.Add(document);
            }, (kind, relative, reason) => conflicts.Add((kind, relative, reason)));
        install.Resources = byKind;
        install.ResourceConflicts = conflicts;
    }

    private static Install Of(string bepInExRoot)
    {
        bepInExRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bepInExRoot));
        lock (Gate)
        {
            if (Installs.TryGetValue(bepInExRoot, out var known)) return known;
            var started = Stopwatch.GetTimestamp();
            var packages = PublishedResources.PackageDirectories(bepInExRoot, failure => Rejected(failure.Code, failure.Relative, failure.Reason));
            var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var manifest in PublishedResources.Discover(bepInExRoot, packages, "", "manifest.json", ResourceCodes, _ => { }))
            {
                if (!PublishedResources.TryRead(manifest, PublishedResources.MaximumFileBytes, _ => { }, out var text)) continue;
                var rows = new List<string>();
                using (var parsed = JsonDocument.Parse(text!, PublishedResources.JsonOptions))
                    if (parsed.RootElement.ValueKind == JsonValueKind.Object
                        && parsed.RootElement.TryGetProperty("dependencies", out var pins) && pins.ValueKind == JsonValueKind.Array)
                        foreach (var pin in pins.EnumerateArray())
                            if (pin.ValueKind == JsonValueKind.String && DependencyPattern.Match(pin.GetString()!) is { Success: true } match)
                                rows.Add("plugins/" + match.Groups[1].Value);
                dependencies[PublishedResources.PackageRoot(manifest.Relative)] = rows;
            }
            var install = Installs[bepInExRoot] = new Install(packages, dependencies);
            Record?.Invoke(DiscoveredCode, new[]
            {
                RuntimeLogValue.Of("packages", (long)packages.Count),
                RuntimeLogValue.Of("manifests", (long)dependencies.Count),
                RuntimeLogValue.Of("elapsedMs", (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency)
            });
            return install;
        }
    }

    /// <summary>`content.read`: one base package was handed one kind of document (how many, from how many packages).</summary>
    private static void Handed(string reader, string kind, IReadOnlyList<PublishedResource> documents)
        => Handed(reader, kind, documents.Select(document => document.Relative).ToArray());

    private static void Handed(string reader, string kind, IReadOnlyList<string> paths)
    {
        Record?.Invoke(ReadCode, new[]
        {
            RuntimeLogValue.Of("reader", reader), RuntimeLogValue.Quote("kind", kind),
            RuntimeLogValue.Of("documents", (long)paths.Count),
            RuntimeLogValue.Of("packages", (long)paths.Select(PublishedResources.PackageRoot).Distinct(StringComparer.Ordinal).Count())
        });
    }

    private static void Rejected(string code, string path, string reason) => Refused?.Invoke(code, path, reason);
}
