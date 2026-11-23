using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

/// <summary>I-PACK content-pack identity: the stable id grammar. The numbers come from the website, which issues one
/// per stable id at first publish and writes it into pack.forge (<c>ids</c>); the game only reads them. The website's
/// `Web/src/forge/content-pack.ts` is the other half of the same rule.</summary>
internal static class ContentIdentity
{
    static readonly Regex PackPattern = new(@"\A[A-Za-z0-9_]{1,128}-[A-Za-z0-9_]{1,128}\z", RegexOptions.CultureInvariant);
    static readonly Regex EntryPattern = new(@"\A[a-z0-9](?:[a-z0-9_-]{0,62}[a-z0-9])?\z", RegexOptions.CultureInvariant);
    static readonly Regex VersionPattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
    static readonly Regex DependencyPattern = new(@"\A([A-Za-z0-9_]{1,128}-[A-Za-z0-9_]{1,128})-((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))\z", RegexOptions.CultureInvariant);

    internal static bool IsPack(string value) => PackPattern.IsMatch(value);
    internal static bool IsEntry(string value) => EntryPattern.IsMatch(value);
    internal static bool IsVersion(string value) => VersionPattern.IsMatch(value);

    /// <summary>`&lt;team&gt;-&lt;package&gt;/&lt;entry&gt;` split into its package and entry, or false.</summary>
    internal static bool TryParse(string id, out string pack, out string entry)
    {
        pack = entry = "";
        int slash = id.IndexOf('/');
        if (slash < 0) return false;
        pack = id.Substring(0, slash); entry = id.Substring(slash + 1);
        return IsPack(pack) && IsEntry(entry);
    }

    /// <summary>`&lt;team&gt;-&lt;package&gt;-&lt;x.y.z&gt;` split into its package name and exact version, or false.</summary>
    internal static bool TryParseDependency(string dependency, out string pack, out string version)
    {
        var match = DependencyPattern.Match(dependency);
        pack = match.Success ? match.Groups[1].Value : "";
        version = match.Success ? match.Groups[2].Value : "";
        return match.Success;
    }
}

/// <summary>One content entry as its file declares it. <see cref="ContentJson"/> is the entry's `content` object as
/// raw JSON; the domain package that registers the entry owns its grammar.</summary>
internal sealed record ContentEntry(string Id, string Domain, string Name, IReadOnlyList<string> References, string ContentJson, string Path);

/// <summary>One installed content pack: its identity file and the entries found beside it.</summary>
internal sealed record ContentPack(string Package, string Version, IReadOnlyList<string> Dependencies, IReadOnlyList<ContentEntry> Entries, string Path)
{
    /// <summary>Block or entry id → the number the website issued, from pack.forge. An entry the table does not
    /// cover is rejected on its own.</summary>
    internal IReadOnlyDictionary<string, uint> Ids { get; init; } = new Dictionary<string, uint>();
    internal IReadOnlyList<ContentPlanReference> Plans { get; init; } = Array.Empty<ContentPlanReference>();
    internal IReadOnlyList<ContentExtraDependencyLock> ExtraDependencyLocks { get; init; } = Array.Empty<ContentExtraDependencyLock>();
}

/// <summary>One refusal. <see cref="Entry"/> is the stable id (or, for a file that never got that far, the file's
/// BepInEx-relative path); <see cref="Other"/> names the other side: the colliding id(s), the missing package or the
/// missing entry.</summary>
internal readonly record struct ContentRejection(string Code, string Entry, string Detail, string? Other, string Path = "");

/// <summary><paramref name="BlockIds"/> is the issued number of every native block the entry declares, keyed by stable block id.</summary>
internal sealed record AcceptedContentEntry(ContentEntry Entry, uint PersistentId, IReadOnlyDictionary<string, uint> BlockIds);

internal sealed record ContentCatalog(IReadOnlyList<AcceptedContentEntry> Accepted, IReadOnlyList<ContentRejection> Rejections)
{
    internal IReadOnlyList<ContentPack> Packs { get; init; } = Array.Empty<ContentPack>();
    internal IReadOnlyList<string> ContentRoots { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> PackageRoots { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> RefusedPackageRoots { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> MenuSlotRefusedPackageRoots { get; init; } = Array.Empty<string>();
    internal IReadOnlyDictionary<string, string> SourceHashes { get; init; } = new Dictionary<string, string>();
    internal IReadOnlyDictionary<string, ContentReadSnapshot> SourceReads { get; init; } = new Dictionary<string, ContentReadSnapshot>();
    internal ContentReadBudget ReadBudget { get; init; } = new();
    internal ContentDependencyLocks.ExtraPreflight? ExtraDependencies { get; init; }
}

/// <summary>
/// I-PACK content-pack discovery: `plugins/&lt;one package directory&gt;/forge/content/pack.forge` names the pack and
/// its exact dependencies (the same list as its manifest.json), and `forge/content/&lt;domain&gt;/&lt;entry&gt;.forge`
/// holds one entry each. It is the one-level package rule plans and resources follow: `plugins/` missing is not an
/// error, package directories are never recursed, only the known domain folders and the exact `.json` suffix are read,
/// and everything is ordinally sorted so one install always scans the same way.
///
/// Validation is <see cref="Validate"/>, the same rule the website runs before it writes a pack: a collision rejects
/// every side and names the others instead of letting load order pick a winner, a reference to another pack must go
/// through a declared exact dependency that is installed at that version, and a reference to an entry that does not
/// exist (or was itself rejected) rejects the referencing entry. Discovery only reads and decides; the domain packages
/// register the accepted entries.
/// </summary>
internal static class ContentPackDiscovery
{
    internal const string CodeCollision = "content-id-collision";
    /// <summary>A collision on a local-band number (an offline export, never site-issued): every entry of each colliding
    /// package is refused.</summary>
    internal const string CodeLocalCollision = "content-id-local-collision";
    internal const string CodeDependencyMissing = "content-dependency-missing";
    internal const string CodeEntryMissing = "content-entry-missing";
    internal const string CodeReferenceCycle = "content-reference-cycle";
    internal const string CodeInvalid = "content-invalid";

    internal static readonly string[] Domains = { "behavior", "enemy", "weapon" };

    // Native content headers are checked before dispatch, across the complete discovered batch. The domain
    // still validates payload semantics; a header never grants permission to install an arbitrary native type.
    // Headerless discovery records are not evidence that a native domain can accept or install the payload.
    internal static IReadOnlyList<ContentDataBlockId> NativeIdentities(ContentEntry entry)
    {
        using var document = JsonDocument.Parse(entry.ContentJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("Duplicate or invalid content header.");
        if (entry.Domain is not ("enemy" or "weapon") || !document.RootElement.TryGetProperty("blocks", out var blocks))
            return Array.Empty<ContentDataBlockId>();
        if (blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() is 0 or > 64)
            throw new InvalidDataException("Native block header must contain 1–64 blocks.");
        // Same rule as the website's `contentEntryDataBlockIdentities`: the primary block is the entry id, a child
        // block is `<entry>#<Type>` or `<entry>#<Type>:<key>`, one type may have several children, and ids are unique.
        var result = new List<ContentDataBlockId>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        int primary = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("id", out var idValue)
                || idValue.ValueKind != JsonValueKind.String || !block.TryGetProperty("type", out var typeValue)
                || typeValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Native block header requires id and type.");
            if (block.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
                throw new InvalidDataException("Duplicate native block header field.");
            string id = idValue.GetString()!, type = typeValue.GetString()!;
            string child = entry.Id + "#" + type;
            if (!Regex.IsMatch(type, "^[A-Za-z][A-Za-z0-9]{0,63}\\z") || !ids.Add(id)
                || id != entry.Id && id != child
                    && !(id.StartsWith(child + ":", StringComparison.Ordinal) && Regex.IsMatch(id.Substring(child.Length + 1), "^[a-z0-9][a-z0-9_-]{0,63}\\z")))
                throw new InvalidDataException("Invalid native block header: " + id);
            if (id == entry.Id) primary++;
            result.Add(new(id, type));
        }
        if (primary != 1) throw new InvalidDataException("Native block header requires exactly one primary block.");
        var primaryType = result.Single(block => block.Id == entry.Id).Type;
        if (entry.Domain == "enemy" ? primaryType != "Enemy" : primaryType is not ("PlayerOfflineGear" or "Item"))
            throw new InvalidDataException("Native block header has an invalid primary type for " + entry.Domain + ".");
        return result;
    }

    /// <summary>Cap on one entry file, one install's entry count and their combined size; the website's
    /// `contentPackBudget` uses the same numbers so an export is never looser than the load.</summary>
    internal const int MaximumEntryBytes = Wire.PackageLimits.MaximumPublishedBytes;
    internal const int MaximumEntries = 1024;
    internal const long MaximumCombinedBytes = Wire.PackageLimits.MaximumPublishedBytes;
    internal const int MaximumPluginChildren = 4096, MaximumDomainChildren = 2048;

    internal static ContentCatalog Scan(string bepInExRoot)
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        var fileRejections = new List<ContentRejection>();
        var contentRoots = new List<string>(); var packageRoots = new List<string>();
        var sourceHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceReads = new Dictionary<string, ContentReadSnapshot>(StringComparer.Ordinal);
        var readBudget = new ContentReadBudget();
        var refusedRoots = new HashSet<string>(StringComparer.Ordinal);
        var packs = Read(bepInExRoot, fileRejections, contentRoots, packageRoots, sourceHashes, sourceReads, readBudget, refusedRoots);
        // Keep every copy until the complete batch is known: an invalid version-suffixed or case-aliased
        // copy must not disappear and let its canonical counterpart win by directory order.
        foreach (var group in packs.GroupBy(pack => pack.Package, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            foreach (var pack in group) refusedRoots.Add(pack.Path);
        var extra = new ContentDependencyLocks.ExtraPreflight(bepInExRoot, packs, readBudget, refusedRoots);
        // Dependency refusals withhold the whole owner before collision/reference validation, but are
        // not evidence of a noncanonical root. Preserve their precise reason for every owning entry.
        var catalog = Validate(packs, refusedRoots, extra.Failures);
        foreach (var owner in extra.Failures.Keys) refusedRoots.Add(owner);
        var rejections = fileRejections.Select(row => row with { Path = row.Entry }).Concat(catalog.Rejections).Concat(extra.Failures.Values).ToList();
        rejections.Sort((a, b) => string.CompareOrdinal(a.Entry, b.Entry));
        return new ContentCatalog(catalog.Accepted, rejections) { Packs = packs, ContentRoots = contentRoots, PackageRoots = packageRoots, RefusedPackageRoots = Array.AsReadOnly(refusedRoots.OrderBy(path => path, StringComparer.Ordinal).ToArray()), SourceHashes = sourceHashes, SourceReads = sourceReads, ReadBudget = readBudget, ExtraDependencies = extra };
    }

    static List<ContentPack> Read(string bepInExRoot, List<ContentRejection> rejections, List<string> contentRoots, List<string> packageRoots, Dictionary<string, string> sourceHashes, Dictionary<string, ContentReadSnapshot> sourceReads, ContentReadBudget readBudget, HashSet<string> refusedRoots)
    {
        var packs = new List<ContentPack>();
        string[] pluginDirectories, pluginChildren;
        try
        {
            var pluginsState = CanonicalDirectory(bepInExRoot, "", "plugins");
            if (pluginsState == ContentPathState.Absent) return packs;
            if (pluginsState != ContentPathState.Directory) throw new InvalidDataException("The plugins directory has a Windows alias or is unsafe.");
            // The packages are ForgeEngine's one discovery (ContentPackages, I-RESOLVE); this reader only judges them.
            var packages = ContentPackages.Discover(bepInExRoot);
            if (packages.Count > MaximumPluginChildren) throw new InvalidDataException("The directory exceeds " + MaximumPluginChildren + " direct children.");
            pluginChildren = packages.Select(package => FrameworkFiles.Resolve(bepInExRoot, package)).ToArray();
            pluginDirectories = pluginChildren;
        }
        catch (Exception error) when (ReadFailure(error))
        { rejections.Add(new(CodeInvalid, "plugins", error.Message, null)); return packs; }
        Array.Sort(pluginDirectories, string.CompareOrdinal);
        foreach (var group in pluginChildren.GroupBy(path => Path.GetFileName(path).TrimEnd(' ', '.'), StringComparer.OrdinalIgnoreCase))
            if (group.Count() != 1 || Path.GetFileName(group.First()) != group.Key)
                foreach (var path in group) refusedRoots.Add(Relative(bepInExRoot, path));
        int entryCount = 0;
        // Stricter startup IO boundary: all identity + entry reads share one inclusive 64 MiB payload ledger.
        // Dependency-lock metadata retains its own cap.
        // Cached discovery reads are never physically charged/read twice; failed reservations stay debited.
        var metadataBudget = new ContentDependencyLocks.MetadataBudget();
        foreach (var pluginDirectory in pluginDirectories)
        {
            if (!refusedRoots.Contains(Relative(bepInExRoot, pluginDirectory)) && IsManagerDisabledPackage(pluginDirectory)) continue;
            var packageRoot = Relative(bepInExRoot, pluginDirectory);
            packageRoots.Add(packageRoot);
            var contentRelative = Relative(bepInExRoot, Path.Combine(pluginDirectory, "forge", "content"));
            string contentRoot;
            try
            {
                var state = ContentDirectoryState(bepInExRoot, packageRoot);
                if (state == ContentPathState.Absent)
                {
                    // A manager can wrap an already rooted archive in another package directory. Inspect
                    // only this already-bounded direct-child boundary for the known payload paths; never
                    // recurse, read, admit, or repair the nested package as an alternate layout.
                    foreach (var child in BoundedChildren(FrameworkFiles.Resolve(bepInExRoot, packageRoot), MaximumPluginChildren))
                    {
                        string name = Path.GetFileName(child);
                        if (!ContentIdentity.IsPack(name) || (File.GetAttributes(child) & FileAttributes.Directory) == 0) continue;
                        string nested = packageRoot + "/" + name + "/forge";
                        if (!Directory.Exists(FrameworkFiles.Resolve(bepInExRoot, nested + "/content"))
                            && !File.Exists(FrameworkFiles.Resolve(bepInExRoot, nested + "/rundown.forge"))) continue;
                        refusedRoots.Add(packageRoot);
                        rejections.Add(new(CodeInvalid, nested, "Nested package payload is unsupported; install its own canonical plugins/" + name + "/ root.", null));
                    }
                    continue;
                }
                contentRoots.Add(packageRoot);
                if (state != ContentPathState.Directory)
                { refusedRoots.Add(packageRoot); rejections.Add(new(CodeInvalid, contentRelative, "The content path is not a safely readable directory.", null)); continue; }
                contentRoot = FrameworkFiles.Resolve(bepInExRoot, contentRelative);
            }
            catch (Exception error) when (ReadFailure(error))
            { contentRoots.Add(packageRoot); refusedRoots.Add(packageRoot); rejections.Add(new(CodeInvalid, contentRelative, error.Message, null)); continue; }

            var entryFiles = new List<(string Domain, string Relative)>(); var enumerationFailed = false;
            foreach (var domain in Domains)
            {
                var relativeDomain = contentRelative + "/" + domain;
                try
                {
                    var domainRoot = FrameworkFiles.Resolve(bepInExRoot, relativeDomain);
                    var state = CanonicalDirectory(bepInExRoot, contentRelative, domain);
                    if (state == ContentPathState.Absent) continue;
                    if (state != ContentPathState.Directory) throw new InvalidDataException("The content domain has a Windows alias or is unsafe.");
                    var files = BoundedChildren(domainRoot, MaximumDomainChildren);
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        var name = Path.GetFileName(file);
                        if (!name.TrimEnd(' ', '.').EndsWith(".forge", StringComparison.OrdinalIgnoreCase)) continue;
                        if (name != name.TrimEnd(' ', '.') || !name.EndsWith(".forge", StringComparison.Ordinal)
                            || !names.Add(name) || (File.GetAttributes(file) & FileAttributes.Directory) != 0)
                            throw new InvalidDataException("The content entry has a Windows filename alias or is not a file.");
                        entryFiles.Add((domain, Relative(bepInExRoot, file)));
                    }
                }
                catch (Exception error) when (ReadFailure(error))
                { enumerationFailed = true; rejections.Add(new(CodeInvalid, relativeDomain, error.Message, null)); }
            }
            if (enumerationFailed)
            {
                refusedRoots.Add(packageRoot);
                RejectAll(entryFiles, rejections, "The complete content boundary cannot be enumerated safely within its cap.");
            }
            entryFiles.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));

            var identityRelative = contentRelative + "/pack.forge";
            if (!CanonicalFile(bepInExRoot, contentRelative, "pack.forge"))
            {
                refusedRoots.Add(packageRoot);
                rejections.Add(new(CodeInvalid, identityRelative, "The pack identity has a Windows filename alias or is unsafe.", null));
                continue;
            }
            if (!TryReadText(bepInExRoot, identityRelative, rejections, out var identityText, sourceHashes, sourceReads, readBudget)) { refusedRoots.Add(packageRoot); RejectAll(entryFiles, rejections, "The pack identity file cannot be read."); continue; }
            if (identityText == null) { refusedRoots.Add(packageRoot); RejectAll(entryFiles, rejections, "The package ships content without forge/content/pack.forge."); continue; }
            if (!TryReadIdentity(identityText, out var package, out var version, out var dependencies, out var plans, out var extraLocks, out var ids, out var identityProblem, sourceReads[identityRelative].Bytes, metadataBudget))
            {
                refusedRoots.Add(packageRoot);
                rejections.Add(new(identityProblem.StartsWith("Invalid extraDependencyLocks", StringComparison.Ordinal) ? ContentDependencyLocks.ExtraLock : CodeInvalid, identityRelative, identityProblem, null));
                RejectAll(entryFiles, rejections, "The pack identity file is invalid.");
                continue;
            }

            if (packageRoot != "plugins/" + package)
            {
                refusedRoots.Add(packageRoot);
                rejections.Add(new(CodeInvalid, identityRelative, "The package root must be plugins/" + package + ".", null));
            }
            var entries = new List<ContentEntry>();
            if (enumerationFailed)
            {
                // Retain the safely read identity even when payload enumeration failed, so a malformed
                // second version cannot disappear and grant the first copy ownership by default.
                packs.Add(new ContentPack(package, version, dependencies, entries, packageRoot) { Plans = plans, ExtraDependencyLocks = extraLocks, Ids = ids });
                continue;
            }
            foreach (var (domain, relative) in entryFiles)
            {
                if (entryCount >= MaximumEntries) { rejections.Add(new(CodeInvalid, relative, "More than " + MaximumEntries + " content entries are installed.", null)); continue; }
                entryCount++;
                if (!TryReadText(bepInExRoot, relative, rejections, out var text, sourceHashes, sourceReads, readBudget) || text == null) continue;
                var stem = Path.GetFileName(relative);
                stem = stem.Substring(0, stem.Length - ".forge".Length);
                if (!TryReadEntry(text, package, domain, stem, relative, out var entry, out var problem)) { rejections.Add(new(CodeInvalid, relative, problem, null)); continue; }
                entries.Add(entry!);
            }
            packs.Add(new ContentPack(package, version, dependencies, entries, packageRoot) { Plans = plans, ExtraDependencyLocks = extraLocks, Ids = ids });
        }
        return packs;
    }

    internal enum ContentPathState { Absent, Directory, Invalid }

    internal static ContentPathState ContentDirectoryState(string root, string packageRoot)
    {
        var forge = CanonicalDirectory(root, packageRoot, "forge");
        if (forge != ContentPathState.Directory) return forge;
        return CanonicalDirectory(root, packageRoot + "/forge", "content");
    }

    // r2modman InstallRulePluginInstaller.applyModeSubDirs appends .old to EVERY owned file.
    // A fully disabled manager package is absent content, not a malformed pack identity.
    // A missing identity in an active/mixed directory still goes through the ordinary refusal.
    private static bool IsManagerDisabledPackage(string directory)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            var marker = Path.Combine(directory, "manifest.json.old");
            if (!File.Exists(marker) || File.Exists(Path.Combine(directory, "manifest.json"))
                || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) return false;
            var pending = new Stack<string>(); pending.Push(directory);
            while (pending.Count != 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                    else if (!Path.GetFileName(child).EndsWith(".old", StringComparison.OrdinalIgnoreCase)) return false;
                }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Inspect actual direct-child names before resolving the canonical path. Windows-equivalent names
    /// must never turn a malformed content boundary into an apparently ordinary package on Linux.</summary>
    internal static ContentPathState CanonicalDirectory(string root, string parent, string name)
    {
        string directory;
        try
        {
            directory = parent.Length == 0 ? Path.GetDirectoryName(FrameworkFiles.Resolve(root, name))! : FrameworkFiles.Resolve(root, parent);
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) return ContentPathState.Invalid;
        }
        catch (FileNotFoundException) { return ContentPathState.Absent; }
        catch (DirectoryNotFoundException) { return ContentPathState.Absent; }
        catch (Exception error) when (ReadFailure(error)) { return ContentPathState.Invalid; }
        try
        {
            var found = false;
            foreach (var entry in BoundedChildren(directory, MaximumPluginChildren))
            {
                var actual = Path.GetFileName(entry);
                if (!string.Equals(actual.TrimEnd(' ', '.'), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (actual != name || found) return ContentPathState.Invalid;
                found = true;
            }
            if (!found) return ContentPathState.Absent;
            var path = FrameworkFiles.Resolve(root, parent.Length == 0 ? name : parent + "/" + name);
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? ContentPathState.Directory : ContentPathState.Invalid;
        }
        catch (Exception error) when (ReadFailure(error)) { return ContentPathState.Invalid; }
    }

    internal static bool CanonicalFile(string root, string parent, string name)
    {
        try
        {
            var directory = FrameworkFiles.Resolve(root, parent);
            var aliases = BoundedChildren(directory, MaximumPluginChildren)
                .Where(path => string.Equals(Path.GetFileName(path).TrimEnd(' ', '.'), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (aliases.Length != 1 || Path.GetFileName(aliases[0]) != name) return false;
            var path = FrameworkFiles.Resolve(root, parent + "/" + name);
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (Exception error) when (ReadFailure(error)) { return false; }
    }

    /// <summary>Bound collection before sorting; overflow refuses the whole inspected boundary, never a
    /// truncated prefix. Nonmatching files count too, so ignored suffixes cannot hide unbounded work.</summary>
    internal static string[] BoundedChildren(string directory, int maximum)
    {
        var rows = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (rows.Count == maximum) throw new InvalidDataException("The directory exceeds " + maximum + " direct children.");
            rows.Add(path);
        }
        rows.Sort(StringComparer.Ordinal); return rows.ToArray();
    }

    static string Relative(string root, string path) => ForgeCore.PosixPath.ToPosix(Path.GetRelativePath(root, path));

    static bool ReadFailure(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    static void RejectAll(List<(string Domain, string Relative)> files, List<ContentRejection> rejections, string reason)
    {
        foreach (var (_, relative) in files) rejections.Add(new(CodeInvalid, relative, reason, null));
    }

    /// <summary>Reads a file within the cap. False when it exists but cannot be read (the refusal is recorded);
    /// true with null text when it does not exist.</summary>
    static bool TryReadText(string root, string relative, List<ContentRejection> rejections, out string? text,
        Dictionary<string, string> sourceHashes, Dictionary<string, ContentReadSnapshot> sourceReads, ContentReadBudget budget)
    {
        var ok = ContentDependencyLocks.TryRead(root, relative, budget,
            reason => rejections.Add(new(CodeInvalid, relative, reason, null)), out text, out var snapshot);
        sourceReads[relative] = snapshot;
        if (ok) sourceHashes[relative] = snapshot.Sha256!;
        return ok;
    }

    static readonly JsonDocumentOptions Strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 256 };

    static bool ExactFields(JsonElement root, string[] names, out string problem)
    {
        problem = "";
        if (root.ValueKind != JsonValueKind.Object) { problem = "The document is not a JSON object."; return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) { problem = "Field " + property.Name + " appears twice."; return false; }
            if (Array.IndexOf(names, property.Name) < 0) { problem = "Unknown field " + property.Name + "."; return false; }
        }
        foreach (var name in names) if (!seen.Contains(name)) { problem = "Missing field " + name + "."; return false; }
        return true;
    }

    /// <summary>`ids` maps a stable entry or block id to the number the website issued; absent means empty. Each value
    /// must be an integer that fits a UInt32. Whether it lies in the right range for its table is decided per entry.</summary>
    static bool TryReadIds(JsonElement value, out IReadOnlyDictionary<string, uint> ids, out string problem)
    {
        var rows = new Dictionary<string, uint>(StringComparer.Ordinal); ids = rows; problem = "";
        if (value.ValueKind != JsonValueKind.Object) { problem = "ids must be an object."; return false; }
        foreach (var property in value.EnumerateObject())
        {
            if (!rows.TryAdd(property.Name, 0)) { problem = "ids names " + property.Name + " twice."; return false; }
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetUInt32(out uint number))
            { problem = "ids[" + property.Name + "] is not a UInt32."; return false; }
            rows[property.Name] = number;
        }
        return true;
    }

    static bool TryReadIdentity(string text, out string package, out string version, out IReadOnlyList<string> dependencies, out IReadOnlyList<ContentPlanReference> plans, out IReadOnlyList<ContentExtraDependencyLock> extraLocks, out IReadOnlyDictionary<string, uint> ids, out string problem, long rawBytes, ContentDependencyLocks.MetadataBudget budget)
    {
        package = version = ""; dependencies = Array.Empty<string>(); plans = Array.Empty<ContentPlanReference>(); extraLocks = Array.Empty<ContentExtraDependencyLock>();
        ids = new Dictionary<string, uint>();
        try
        {
            using var document = JsonDocument.Parse(text, Strict);
            var root = document.RootElement;
            var hasExtra = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("extraDependencyLocks", out _);
            if (hasExtra && !budget.Retain(rawBytes)) { problem = "The installed dependency-lock metadata budget is exhausted."; return false; }
            var fields = new List<string> { "format", "package", "version", "dependencies", "plans" };
            if (hasExtra) fields.Add("extraDependencyLocks");
            var hasIds = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ids", out _);
            if (hasIds) fields.Add("ids");
            // Optional version declarations; the package-needs-base-update decision is made by the Map package gate.
            foreach (var declaration in new[] { "minBasePackageVersion" })
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(declaration, out _)) fields.Add(declaration);
            if (!ExactFields(root, fields.ToArray(), out problem)) return false;
            if (hasIds && !TryReadIds(root.GetProperty("ids"), out ids, out problem)) return false;
            if (hasExtra && !ContentDependencyLocks.TryParseExtraLocks(root.GetProperty("extraDependencyLocks"), out extraLocks))
            { problem = "Invalid extraDependencyLocks; use exact package/version pairs."; return false; }
            if (root.GetProperty("format").ValueKind != JsonValueKind.String || root.GetProperty("format").GetString() != "forge-content-pack") { problem = "format must be forge-content-pack."; return false; }
            if (root.GetProperty("package").ValueKind != JsonValueKind.String || !ContentIdentity.IsPack(package = root.GetProperty("package").GetString()!)) { problem = "package must be <team>-<name>."; return false; }
            if (root.GetProperty("version").ValueKind != JsonValueKind.String || !ContentIdentity.IsVersion(version = root.GetProperty("version").GetString()!)) { problem = "version must be x.y.z."; return false; }
            var list = root.GetProperty("dependencies");
            if (list.ValueKind != JsonValueKind.Array) { problem = "dependencies must be an array."; return false; }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var values = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !ContentIdentity.TryParseDependency(item.GetString()!, out var name, out _)) { problem = "A dependency is not <team>-<package>-<x.y.z>."; return false; }
                if (!names.Add(name)) { problem = "Dependency " + name + " is declared twice."; return false; }
                values.Add(item.GetString()!);
            }
            dependencies = values;
            return ContentPlanAdmission.TryReadMappings(root.GetProperty("plans"), out plans, out problem);
        }
        catch (JsonException error) { problem = error.Message; return false; }
    }

    static bool TryReadEntry(string text, string package, string domain, string stem, string relative, out ContentEntry? entry, out string problem)
    {
        entry = null;
        try
        {
            using var document = JsonDocument.Parse(text, Strict);
            var root = document.RootElement;
            if (!ExactFields(root, new[] { "format", "id", "domain", "name", "references", "content" }, out problem)) return false;
            if (root.GetProperty("format").ValueKind != JsonValueKind.String || root.GetProperty("format").GetString() != "forge-content-entry") { problem = "format must be forge-content-entry."; return false; }
            var id = root.GetProperty("id").ValueKind == JsonValueKind.String ? root.GetProperty("id").GetString()! : "";
            if (id != package + "/" + stem) { problem = "id must be " + package + "/" + stem + ", the package and the file name."; return false; }
            if (root.GetProperty("domain").ValueKind != JsonValueKind.String || root.GetProperty("domain").GetString() != domain) { problem = "domain must be " + domain + ", the folder the entry is in."; return false; }
            var name = root.GetProperty("name");
            if (name.ValueKind != JsonValueKind.String || name.GetString()!.Length is 0 or > 120) { problem = "name must be 1-120 characters."; return false; }
            var list = root.GetProperty("references");
            if (list.ValueKind != JsonValueKind.Array) { problem = "references must be an array."; return false; }
            var references = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) { problem = "A reference is not a string."; return false; }
                references.Add(item.GetString()!);
            }
            var content = root.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Object) { problem = "content must be an object."; return false; }
            entry = new ContentEntry(id, domain, name.GetString()!, references, content.GetRawText(), relative);
            return true;
        }
        catch (JsonException error) { problem = error.Message; return false; }
    }

    /// <summary>The rule set shared with the website's `validateContentPacks`, in the same order: structure, then
    /// collisions, then references, then rejection spreading to every entry that references a rejected one. Each
    /// entry gets at most one rejection (the first that applies); the result is ordinally sorted by stable id.</summary>
    internal static ContentCatalog Validate(IReadOnlyList<ContentPack> packs, IReadOnlySet<string>? refusedRoots = null,
        IReadOnlyDictionary<string, ContentRejection>? dependencyFailures = null)
    {
        var rejected = new Dictionary<string, ContentRejection>(StringComparer.Ordinal);
        var paths = packs.SelectMany(pack => pack.Entries).GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Path).OrderBy(path => path, StringComparer.Ordinal).First(), StringComparer.Ordinal);
        void Reject(string code, string entry, string detail, string? other = null)
        {
            if (!rejected.ContainsKey(entry)) rejected[entry] = new ContentRejection(code, entry, detail, other,
                paths.TryGetValue(entry, out var path) ? path : "");
        }

        var packCount = packs.GroupBy(pack => pack.Package, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var idCount = packs.SelectMany(pack => pack.Entries).GroupBy(entry => entry.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var installed = new Dictionary<string, ContentPack>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            if (refusedRoots?.Contains(pack.Path) == true)
            { foreach (var entry in pack.Entries) Reject(CodeInvalid, entry.Id, "The package root is noncanonical or conflicts with another installed copy."); continue; }
            if (packCount[pack.Package] != 1) { foreach (var entry in pack.Entries) Reject(CodeInvalid, entry.Id, "Package " + pack.Package + " is installed more than once."); continue; }
            if (dependencyFailures != null && dependencyFailures.TryGetValue(pack.Path, out var failure))
            { foreach (var entry in pack.Entries) Reject(failure.Code, entry.Id, failure.Detail, failure.Other); continue; }
            installed[pack.Package] = pack;
            foreach (var entry in pack.Entries)
            {
                if (!ContentIdentity.TryParse(entry.Id, out var owner, out _) || owner != pack.Package) Reject(CodeInvalid, entry.Id, "Entry id does not belong to package " + pack.Package + ".");
                else if (idCount[entry.Id] != 1) Reject(CodeInvalid, entry.Id, "The same stable id is declared more than once.");
                else if (entry.References.Any(reference => !ContentIdentity.TryParse(reference, out _, out _))) Reject(CodeInvalid, entry.Id, "A reference is not a stable content id.");
            }
        }

        var nativeIdentities = new Dictionary<string, IReadOnlyList<ContentDataBlockId>>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            try
            {
                foreach (var entry in pack.Entries.Where(entry => !rejected.ContainsKey(entry.Id)))
                    nativeIdentities[entry.Id] = NativeIdentities(entry);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException)
            {
                // A malformed declaration cannot hide a collision behind partial discovery of its own package.
                foreach (var entry in pack.Entries) Reject(CodeInvalid, entry.Id, error.Message);
            }
        }
        // Each entry takes the numbers its own pack issued. A missing or out-of-range number rejects that entry alone.
        var byNumber = new Dictionary<(string Type, uint Number), List<(string Id, string Owner)>>();
        var issued = new Dictionary<string, IReadOnlyList<ContentIssuedBlock>>(StringComparer.Ordinal);
        foreach (var pack in packs)
            foreach (var entry in pack.Entries)
            {
                if (rejected.ContainsKey(entry.Id)) continue;
                var blocks = nativeIdentities[entry.Id];
                var wanted = blocks.Count == 0 ? new[] { new ContentDataBlockId(entry.Id, "") } : blocks;
                var rows = new List<ContentIssuedBlock>(); string? problem = null;
                foreach (var block in wanted)
                {
                    if (!pack.Ids.TryGetValue(block.Id, out uint number)) { problem = "Package " + pack.Package + " issues no number for " + block.Id + "."; break; }
                    if (!ContentDataBlockIds.InIssuedRange(block.Type, number)) { problem = "Issued number " + number + " for " + block.Id + " is outside the " + (block.Type.Length == 0 ? "content" : block.Type) + " range."; break; }
                    rows.Add(new(block.Id, block.Type, number));
                }
                if (problem != null) { Reject(CodeInvalid, entry.Id, problem); continue; }
                issued[entry.Id] = rows;
                foreach (var row in rows)
                {
                    var key = (row.Type, row.Number);
                    if (!byNumber.TryGetValue(key, out var group)) byNumber[key] = group = new();
                    group.Add((row.Id, entry.Id));
                }
            }
        static string PackOf(string id) => id.Substring(0, id.IndexOf('/'));
        foreach (var (key, group) in byNumber)
        {
            if (group.Count < 2) continue;
            var sorted = group.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var named = sorted.Select(PackOf).Distinct(StringComparer.Ordinal).ToList();
            // A local-band number was never registered by the site, so neither pack owns it: both packages are refused whole.
            if (ContentDataBlockIds.IsLocal(key.Type, key.Number))
            {
                string detail = (named.Count == 1 ? "Package " : "Packages ") + string.Join(" and ", named) + " carry the same local "
                    + (key.Type.Length == 0 ? "" : key.Type + " ") + "persistentID " + key.Number + " for " + string.Join(" and ", sorted) + "; none of these packages is loaded.";
                foreach (var pack in packs.Where(pack => named.Contains(pack.Package)))
                {
                    string others = string.Join(",", named.Where(name => name != pack.Package));
                    foreach (var entry in pack.Entries) Reject(CodeLocalCollision, entry.Id, detail, others.Length == 0 ? pack.Package : others);
                }
                continue;
            }
            foreach (var row in group.OrderBy(row => row.Id, StringComparer.Ordinal))
                Reject(CodeCollision, row.Owner, (named.Count == 1 ? "Package " : "Packages ") + string.Join(" and ", named) + " are issued the same "
                    + (key.Type.Length == 0 ? "" : key.Type + " ") + "persistentID " + key.Number + " for " + string.Join(" and ", sorted) + "; none of these entries is loaded.",
                    string.Join(",", sorted.Where(other => other != row.Id)));
        }

        var owners = new Dictionary<string, ContentPack>(StringComparer.Ordinal);
        foreach (var pack in installed.Values) foreach (var entry in pack.Entries) owners[entry.Id] = pack;
        static bool Has(ContentPack pack, string id) => pack.Entries.Any(entry => entry.Id == id);
        foreach (var pack in installed.Values)
            foreach (var entry in pack.Entries)
            {
                if (rejected.ContainsKey(entry.Id)) continue;
                foreach (var reference in entry.References)
                {
                    ContentIdentity.TryParse(reference, out var target, out _);
                    if (target == pack.Package)
                    {
                        if (!Has(pack, reference)) { Reject(CodeEntryMissing, entry.Id, "Package " + pack.Package + " has no entry " + reference + ".", reference); break; }
                        continue;
                    }
                    string? declared = null, wanted = null;
                    foreach (var dependency in pack.Dependencies)
                        if (ContentIdentity.TryParseDependency(dependency, out var name, out var version) && name == target) { declared = dependency; wanted = version; break; }
                    if (declared == null) { Reject(CodeDependencyMissing, entry.Id, reference + " belongs to " + target + ", which manifest.json does not declare as a dependency.", target); break; }
                    if (!installed.TryGetValue(target, out var present)) { Reject(CodeDependencyMissing, entry.Id, "Dependency " + declared + " is not installed.", target); break; }
                    if (present.Version != wanted) { Reject(CodeDependencyMissing, entry.Id, "Dependency " + declared + " is required but " + target + "-" + present.Version + " is installed.", target); break; }
                    if (!Has(present, reference)) { Reject(CodeEntryMissing, entry.Id, "Package " + target + " has no entry " + reference + ".", reference); break; }
                }
            }

        var active = installed.Values.SelectMany(pack => pack.Entries).Where(entry => !rejected.ContainsKey(entry.Id))
            .ToDictionary(entry => entry.Id, entry => entry, StringComparer.Ordinal);
        var references = active.ToDictionary(row => row.Key, row => row.Value.References, StringComparer.Ordinal);
        foreach (var component in ContentReferenceGraph.Cycles(references))
            foreach (var id in component.OrderBy(id => id, StringComparer.Ordinal))
            {
                var other = active[id].References.Where(reference => reference != id && component.Contains(reference))
                    .OrderBy(reference => reference, StringComparer.Ordinal).First();
                Reject(CodeReferenceCycle, id, "Content references form a cycle.", other);
            }

        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var pack in installed.Values)
                foreach (var entry in pack.Entries)
                {
                    if (rejected.ContainsKey(entry.Id)) continue;
                    var blocked = entry.References.FirstOrDefault(reference => owners.ContainsKey(reference) && rejected.ContainsKey(reference));
                    if (blocked != null) { Reject(CodeEntryMissing, entry.Id, "Referenced entry " + blocked + " was rejected.", blocked); changed = true; }
                }
        }

        var accepted = packs.SelectMany(pack => pack.Entries).Where(entry => !rejected.ContainsKey(entry.Id))
            .Select(entry => new AcceptedContentEntry(entry, issued[entry.Id].Single(row => row.Id == entry.Id).Number,
                issued[entry.Id].ToDictionary(row => row.Id, row => row.Number, StringComparer.Ordinal)))
            .OrderBy(row => row.Entry.Id, StringComparer.Ordinal).ToList();
        var rejections = rejected.Values.OrderBy(row => row.Entry, StringComparer.Ordinal).ToList();
        return new ContentCatalog(accepted, rejections);
    }
}
