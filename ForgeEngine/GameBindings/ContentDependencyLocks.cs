using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;


namespace ForgeEngine.GameBindings;

internal sealed record ContentExtraDependencyLock(string Package, string Version);

/// <summary>Evidence from the exact bounded read used by discovery, before BOM removal or JSON parsing.
/// Failed reads debit consumed payload bytes without exposing unverified partial raw content.</summary>
internal sealed record ContentReadSnapshot(string? Sha256, long Bytes);

internal sealed class ContentReadBudget
{
    // One possible growth-probe byte per attempted safe read is separate from the inclusive payload cap.
    // Failed attempts settle consumed payload bytes; attempts still bound the separate growth probes.
    internal const int MaximumAttempts = ContentPackDiscovery.MaximumPluginChildren + ContentPackDiscovery.MaximumEntries + ContentDependencyLocks.MaximumPackages;
    internal long Bytes { get; private set; }
    internal int Attempts { get; private set; }
    internal bool Charge(long bytes)
    {
        if (bytes < 0 || bytes > ContentPackDiscovery.MaximumCombinedBytes - Bytes) return false;
        Bytes += bytes; return true;
    }
    internal bool Reserve(out int maximum, out long allowance)
    {
        maximum = 0; allowance = 0;
        if (Attempts == MaximumAttempts) return false;
        Attempts++;
        maximum = (int)Math.Min(ContentPackDiscovery.MaximumEntryBytes, ContentPackDiscovery.MaximumCombinedBytes - Bytes);
        allowance = maximum;
        return maximum > 0 && Charge(allowance);
    }
    internal void Settle(long allowance, long actual) => Bytes -= allowance - actual;
}


/// <summary>Internal, consumer-owned preflight of the exact Thunderstore dependencies a package declares beside its own
/// content. No provider, graph compiler, archive reader, publisher authentication or loaded-audio provenance is introduced here.</summary>
internal sealed class ContentDependencyLocks
{
    internal const int MaximumPackages = 64, MaximumConsumers = 256;
    private static readonly ReleasePins CurrentRelease = ReleasePins.Load();
    /// <summary>Plugin GUID to (Thunderstore package, release version) of every base package and library in the release this host
    /// was built with; null when the embedded release identity is unreadable.</summary>
    internal static IReadOnlyDictionary<string, (string Package, string Version)>? ReleasePlugins => CurrentRelease.Valid ? CurrentRelease.Plugins : null;

    internal sealed class MetadataBudget
    {
        private int consumers;
        private long bytes;
        internal bool Retain(long rawBytes)
        {
            if (consumers == MaximumConsumers || rawBytes < 1 || rawBytes > ContentPackDiscovery.MaximumCombinedBytes - bytes) return false;
            consumers++; bytes += rawBytes; return true;
        }
    }

    internal const string ExtraMissing = "content-extra-dependency-missing", ExtraVersion = "content-extra-dependency-version",
        ExtraManifest = "content-extra-dependency-manifest", ExtraLayout = "content-extra-dependency-layout", ExtraLock = "content-extra-dependency-lock";

    internal static bool TryParseExtraLocks(JsonElement value, out IReadOnlyList<ContentExtraDependencyLock> result)
    {
        result = Array.Empty<ContentExtraDependencyLock>();
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumPackages) return false;
        var rows = new List<ContentExtraDependencyLock>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in value.EnumerateArray())
        {
            if (!Fields(row, "package", "version") || !Text(row, "package", out var package) || !ContentIdentity.IsPack(package)
                || !Text(row, "version", out var version) || !ContentIdentity.IsVersion(version) || !names.Add(package)) return false;
            rows.Add(new(package, version));
        }
        result = rows.OrderBy(row => row.Package, StringComparer.Ordinal).ToArray(); return true;
    }

    /// <summary>Same package preflight, using only declared Thunderstore identities and installed manifest
    /// metadata. It never reads DLLs/archives, authenticates a publisher, or disables third-party code.</summary>
    internal sealed class ExtraPreflight
    {
        private readonly string root;
        private readonly HashSet<string> structuralRoots;
        private readonly Dictionary<string, ContentRejection> failures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ManifestObservation> observations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ContentPack> owners;
        private readonly HashSet<string> contentNames;
        private readonly HashSet<string> foldedContentNames;
        private sealed record ManifestObservation(string? Code, string? Version);
        internal ExtraPreflight(string root, IReadOnlyList<ContentPack> packs, ContentReadBudget budget, IReadOnlySet<string> refusedRoots)
        {
            this.root = root; structuralRoots = new(refusedRoots, StringComparer.Ordinal);
            owners = packs.GroupBy(pack => pack.Path, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            contentNames = new(packs.Select(pack => pack.Package), StringComparer.Ordinal);
            foldedContentNames = new(contentNames, StringComparer.OrdinalIgnoreCase);
            foreach (var pack in packs.OrderBy(pack => pack.Path, StringComparer.Ordinal))
            {
                if (structuralRoots.Contains(pack.Path)) continue;
                bool inertSource = pack.Plans.Count == 0 && pack.Entries.Count > 0 && pack.Entries.All(entry => entry.Domain == "behavior");
                if (inertSource && pack.ExtraDependencyLocks.Count == 0) continue;
                var exactDependencies = new HashSet<string>(pack.ExtraDependencyLocks.Count == 0 ? Array.Empty<string>() : pack.Dependencies, StringComparer.Ordinal);
                var extraPins = new HashSet<string>(pack.ExtraDependencyLocks.Select(pair => pair.Package + "-" + pair.Version), StringComparer.Ordinal);
                var referenceOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in pack.Entries.SelectMany(entry => entry.References).Concat(pack.Plans.SelectMany(plan => plan.References)))
                    if (ContentIdentity.TryParse(id, out var referenceOwner, out _)) referenceOwners.Add(referenceOwner);
                foreach (var pair in pack.ExtraDependencyLocks)
                {
                    var pin = pair.Package + "-" + pair.Version;
                    if (!CurrentRelease.Valid || CurrentRelease.Reserved.Contains(pair.Package)
                        || string.Equals(pair.Package, pack.Package, StringComparison.OrdinalIgnoreCase)
                        || !exactDependencies.Contains(pin) || foldedContentNames.Contains(pair.Package)
                        || referenceOwners.Contains(pair.Package))
                    { Refuse(pack.Path, ExtraLock, pin, "Extra dependency lock conflicts with the package's exact dependency or content identity."); break; }
                    var failure = Check(pack.Path, pair, budget, observations);
                    if (failure != null) { failures.TryAdd(pack.Path, failure.Value); break; }
                }
                if (failures.ContainsKey(pack.Path) || inertSource) continue;
                // Non-release pins with no Forge source identity/reference/table must not silently become
                // unchecked native dependencies. Original inert behavior sources keep their source-closure rules.
                foreach (var pin in pack.Dependencies)
                {
                    if (CurrentRelease.Allows(pin) || !ContentIdentity.TryParseDependency(pin, out var name, out _)
                        || extraPins.Contains(pin)) continue;
                    if (contentNames.Contains(name) || referenceOwners.Contains(name)) continue;
                    Refuse(pack.Path, ExtraLock, pin, "A non-framework dependency needs an explicit extraDependencyLocks identity or a Forge content source."); break;
                }
            }
        }
        internal IReadOnlyDictionary<string, ContentRejection> Failures => failures;
        internal void Refuse(string owner, string code, string other, string detail)
        {
            if (!structuralRoots.Contains(owner)) failures.TryAdd(owner, Rejection(owner, code, other, "Dependency " + other + ": " + detail));
        }
        private static ContentRejection Rejection(string owner, string code, string other, string detail)
            => new(code, owner + "/forge/content/pack.forge", detail, other, owner + "/forge/content/pack.forge");
        internal ContentRejection? Failure(string owner) => failures.TryGetValue(owner, out var refusal) ? refusal : null;
        internal void PreserveStructuralRefusal(string owner) { structuralRoots.Add(owner); failures.Remove(owner); }

        // Captured Rundown-only owners can lack a content directory entirely. Their dependencies still need
        // the same explicit classification; a missing sibling identity must not bypass package preflight.
        internal void CheckRundown(string owner, IReadOnlyList<string> dependencies)
        {
            if (structuralRoots.Contains(owner) || owners.ContainsKey(owner)) return;
            foreach (var pin in dependencies)
            {
                if (CurrentRelease.Allows(pin) || !ContentIdentity.TryParseDependency(pin, out var package, out _)) continue;
                if (contentNames.Contains(package)) continue;
                Refuse(owner, ExtraLock, pin, "The Rundown's extra dependency has no verified sibling package identity and exact lock."); break;
            }
        }

        internal Func<string, ContentRejection?> Recheck()
        {
            var reads = new ContentReadBudget(); var cache = new Dictionary<string, ManifestObservation>(StringComparer.Ordinal);
            var checkedOwners = new Dictionary<string, ContentRejection?>(StringComparer.Ordinal);
            return owner =>
            {
                if (checkedOwners.TryGetValue(owner, out var previous)) return previous;
                var failure = Failure(owner);
                if (failure == null)
                    foreach (var pair in owners.TryGetValue(owner, out var pack) ? pack.ExtraDependencyLocks : Array.Empty<ContentExtraDependencyLock>())
                        if ((failure = Check(owner, pair, reads, cache)) != null) break;
                checkedOwners.Add(owner, failure); return failure;
            };
        }
        private ContentRejection? Check(string owner, ContentExtraDependencyLock pair, ContentReadBudget reads, Dictionary<string, ManifestObservation> cache)
        {
            if (!cache.TryGetValue(pair.Package, out var observed))
            {
                // Failed observations count as well; expectations never enter this shared cache.
                if (cache.Count >= MaximumPackages) observed = new(ExtraManifest, null);
                else { observed = Observe(pair.Package, reads); cache.Add(pair.Package, observed); }
            }
            var code = observed.Code ?? (observed.Version == pair.Version ? null : ExtraVersion);
            if (code == null) return null;
            var pin = pair.Package + "-" + pair.Version;
            var detail = code switch
            {
                ExtraVersion => "Dependency " + pin + " is required but " + pair.Package + "-" + observed.Version + " is installed.",
                ExtraMissing => "Dependency " + pin + " is not installed or is disabled.",
                ExtraLayout => "Dependency " + pin + " has a noncanonical or unsafe installed layout.",
                _ => "Dependency " + pin + " has no readable manifest with a valid matching identity and version."
            };
            return Rejection(owner, code, pin, detail);
        }
        private ManifestObservation Observe(string package, ContentReadBudget reads)
        {
            var owner = "plugins/" + package;
            try
            {
                var plugins = ContentPackDiscovery.CanonicalDirectory(root, "", "plugins");
                if (plugins == ContentPackDiscovery.ContentPathState.Absent) return new(ExtraMissing, null);
                if (plugins != ContentPackDiscovery.ContentPathState.Directory) return new(ExtraLayout, null);
                var state = ContentPackDiscovery.CanonicalDirectory(root, "plugins", package);
                if (state == ContentPackDiscovery.ContentPathState.Absent) return new(ExtraMissing, null);
                if (state != ContentPackDiscovery.ContentPathState.Directory) return new(ExtraLayout, null);
                var children = ContentPackDiscovery.BoundedChildren(FrameworkFiles.Resolve(root, owner), ContentPackDiscovery.MaximumPluginChildren);
                // Refuse known manager nesting, never recursively search another layout for a matching manifest.
                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if ((name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) || name.Equals("plugins", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(package, StringComparison.OrdinalIgnoreCase)) && (File.GetAttributes(child) & FileAttributes.Directory) != 0)
                        return new(ExtraLayout, null);
                }
                var content = ContentPackDiscovery.ContentDirectoryState(root, owner);
                if (content != ContentPackDiscovery.ContentPathState.Absent) return new(ExtraLayout, null);
                var forge = ContentPackDiscovery.CanonicalDirectory(root, owner, "forge");
                if (forge == ContentPackDiscovery.ContentPathState.Directory && ContentPackDiscovery.CanonicalDirectory(root, owner + "/forge", "plans") != ContentPackDiscovery.ContentPathState.Absent)
                    return new(ExtraLayout, null);
                var manifests = children.Where(path => string.Equals(Path.GetFileName(path).TrimEnd(' ', '.'), "manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (manifests.Length == 0)
                    return new(children.Any(path => Path.GetFileName(path) == "manifest.json.old") ? ExtraMissing : ExtraManifest, null);
                if (manifests.Length != 1 || Path.GetFileName(manifests[0]) != "manifest.json" || !ContentPackDiscovery.CanonicalFile(root, owner, "manifest.json")) return new(ExtraLayout, null);
                if (!TryRead(root, owner + "/manifest.json", reads, _ => { }, out var text, out _)) return new(ExtraManifest, null);
                using var parsed = JsonDocument.Parse(text!, PublishedResources.JsonOptions);
                var manifest = parsed.RootElement; RuntimeJson.ValidateJson(manifest);
                if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String
                    || nameValue.GetString() != package.Substring(package.IndexOf('-') + 1)
                    || !manifest.TryGetProperty("version_number", out var version) || version.ValueKind != JsonValueKind.String || !ContentIdentity.IsVersion(version.GetString()!)) return new(ExtraManifest, null);
                foreach (var name in new[] { "author", "AuthorName" })
                    if (manifest.TryGetProperty(name, out var author) && (author.ValueKind != JsonValueKind.String || author.GetString() != package.Substring(0, package.IndexOf('-')))) return new(ExtraManifest, null);
                return new(null, version.GetString());
            }
            catch (Exception error) when (error is JsonException or RuntimeContractException or InvalidOperationException or KeyNotFoundException)
            { return new(ExtraManifest, null); }
            catch (Exception error) when (ReadFailure(error)) { return new(ExtraLayout, null); }
        }
    }

    private static bool Text(JsonElement row, string field, out string text)
    {
        var value = row.GetProperty(field); text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        return value.ValueKind == JsonValueKind.String;
    }
    private static bool Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name)) && seen.Count == names.Length;
    }
    private static bool ReadFailure(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;


    /// <summary>Budget adapter around the existing safe reader, not a second file reader or public API.</summary>
    internal static bool TryRead(string root, string relative, ContentReadBudget budget, Action<string> rejected,
        out string? text, out ContentReadSnapshot snapshot)
    {
        text = null; snapshot = new(null, 0);
        if (!budget.Reserve(out var maximum, out var allowance)) { rejected("The bounded content read allowance is exhausted."); return false; }
        long observedBytes = 0;
        try
        {
            var path = FrameworkFiles.Resolve(root, relative);
            var document = new PublishedResource(relative, path, new FileInfo(path).Length, root);
            bool read = PublishedResources.TryRead(document, maximum, rejected, out text, out var bytes, out observedBytes);
            snapshot = new(null, Math.Min(allowance, observedBytes));
            if (!read) return false;
            snapshot = new(ForgeCore.Hash.Sha256Hex(bytes!), bytes!.LongLength);
            return true;
        }
        catch (Exception error) when (ReadFailure(error)) { rejected(error.Message); return false; }
        finally { budget.Settle(allowance, Math.Min(allowance, observedBytes)); }
    }

    private sealed class ReleasePins
    {
        internal bool Valid { get; private set; }
        /// <summary>The exact pins of the third-party base dependencies (BepInEx, MTFO ...): their versions are not ours to relax.</summary>
        private HashSet<string> Exact { get; } = new(StringComparer.Ordinal);
        /// <summary>Player-facing Forge package to the version of this release; a pack's pin of it is a minimum (contract 1.4).</summary>
        private Dictionary<string, string> Forge { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Reserved { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, (string Package, string Version)> Plugins { get; } = new(StringComparer.Ordinal);
        /// <summary>Whether a dependency pin names something this release provides: a third-party base pin exactly, a Forge
        /// package at the same major with a minor this release has.</summary>
        internal bool Allows(string pin) => Exact.Contains(pin)
            || ContentIdentity.TryParseDependency(pin, out var name, out var version) && Forge.TryGetValue(name, out var release) && ForgeVersion.Meets(release, version);
        internal static ReleasePins Load()
        {
            var result = new ReleasePins();
            try
            {
                using var stream = typeof(ContentDependencyLocks).Assembly.GetManifestResourceStream("ForgeEngine.ReleaseIdentity.json");
                if (stream == null || stream.Length is < 1 or > 1024 * 1024) return result;
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                RuntimeJson.ValidateJson(root);
                if (!Text(root, "kind", out var kind) || kind != "forge-base-release-identity"
                    || !ForgeCore.FormatVersion.Matches(root)) return result;
                var bases = root.GetProperty("baseDependencies"); var packages = root.GetProperty("packages");
                // The public library ships in the release set beside the base packages; the host pins it.
                var libraries = root.GetProperty("libraries");
                if (bases.ValueKind != JsonValueKind.Array || packages.ValueKind != JsonValueKind.Array || libraries.ValueKind != JsonValueKind.Array
                    || bases.GetArrayLength() is < 1 or > MaximumPackages || packages.GetArrayLength() is < 1 or > MaximumPackages
                    || libraries.GetArrayLength() is < 1 or > MaximumPackages) return result;
                var rows = new List<JsonElement>(packages.EnumerateArray());
                rows.AddRange(libraries.EnumerateArray());
                foreach (var row in bases.EnumerateArray())
                {
                    if (!Text(row, "dependency", out var pin) || !ContentIdentity.TryParseDependency(pin, out var name, out _)
                        || !result.Reserved.Add(name) || !result.Exact.Add(pin)) return result;
                }
                foreach (var row in rows)
                {
                    if (!Text(row, "packageName", out var name) || !ContentIdentity.IsPack(name) || !result.Reserved.Add(name)
                        || !Text(row, "version", out var version) || !ContentIdentity.IsVersion(version)
                        || !Text(row, "audience", out var audience) || audience is not ("player" or "author")) return result;
                    if (audience == "player") result.Forge[name] = version;
                    if (!Text(row, "pluginGuid", out var guid) || guid.Length == 0 || !result.Plugins.TryAdd(guid, (name, version))) return result;
                }
                foreach (var row in rows)
                {
                    var dependencies = row.GetProperty("dependencies");
                    if (dependencies.ValueKind != JsonValueKind.Array || dependencies.GetArrayLength() > MaximumPackages) return result;
                    var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dependency in dependencies.EnumerateArray())
                        if (dependency.ValueKind != JsonValueKind.String || !result.Allows(dependency.GetString()!)
                            || !ContentIdentity.TryParseDependency(dependency.GetString()!, out var name, out _) || !unique.Add(name)) return result;
                }
                result.Valid = true;
            }
            catch (Exception error) when (error is JsonException or RuntimeContractException or InvalidOperationException or KeyNotFoundException or IOException or ArgumentException or FormatException or OverflowException or NotSupportedException)
            { throw new InvalidDataException("The embedded release identity is unreadable: " + RuntimeDiagnostics.Describe(error), error); }
            return result;
        }
    }
}
