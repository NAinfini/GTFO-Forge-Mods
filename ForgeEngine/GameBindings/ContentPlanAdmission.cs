using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

internal sealed record ContentPlanReference(string File, IReadOnlyList<string> References);

/// <summary>Host-only frozen content prerequisites. It does not interpret graphs, match native targets or grant
/// permissions. An explicit empty reference list is an author assertion, not authentication of untrusted code.</summary>
internal sealed class ContentPlanAdmission
{
    private readonly string root;
    private readonly RuntimeIdentity runtime;
    private readonly Dictionary<string, ContentReadSnapshot> sourceReads;
    private readonly ContentDependencyLocks.ExtraPreflight? extra;
    private readonly HashSet<string> menuSlotRefusedRoots;
    private readonly Dictionary<string, string[]> prerequisiteFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> contentRoots;
    private readonly HashSet<string> packageRoots;
    private readonly Dictionary<string, Dictionary<string, string?>> mappings = new(StringComparer.Ordinal);
    private static readonly Regex FileName = new(@"^[A-Za-z0-9_][A-Za-z0-9._-]*\.plan\.forge\z", RegexOptions.CultureInvariant);
    internal ContentPlanAdmission(string root, RuntimeIdentity runtime, ContentCatalog catalog, IReadOnlySet<string> accepted)
    {
        this.root = root; this.runtime = runtime; extra = catalog.ExtraDependencies;
        menuSlotRefusedRoots = new(catalog.MenuSlotRefusedPackageRoots, StringComparer.Ordinal);
        sourceReads = new(catalog.SourceReads, StringComparer.Ordinal);
        contentRoots = new(catalog.ContentRoots, StringComparer.Ordinal);
        packageRoots = new(catalog.PackageRoots.Except(catalog.RefusedPackageRoots, StringComparer.Ordinal), StringComparer.Ordinal);
        var installed = catalog.Packs.GroupBy(pack => pack.Package, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1 && !catalog.RefusedPackageRoots.Contains(group.Single().Path, StringComparer.Ordinal))
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var byId = catalog.Accepted.ToDictionary(row => row.Entry.Id, row => row.Entry, StringComparer.Ordinal);
        foreach (var pack in catalog.Packs)
        {
            // A duplicate package identity has no reliable version/entry ownership, even for an empty plan map.
            if (!installed.ContainsKey(pack.Package)) continue;
            var plans = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var plan in pack.Plans)
            {
                string? failure = null;
                foreach (var reference in plan.References)
                {
                    ContentIdentity.TryParse(reference, out var owner, out _);
                    if (owner != pack.Package && (!installed.TryGetValue(owner, out var dependency)
                        || !pack.Dependencies.Contains(owner + "-" + dependency.Version, StringComparer.Ordinal)))
                    { failure ??= ContentPackDiscovery.CodeDependencyMissing; break; }
                    if (!accepted.Contains(reference)) { failure ??= ContentPackDiscovery.CodeEntryMissing; break; }
                }
                plans.Add(plan.File, failure);
                var paths = new HashSet<string>(StringComparer.Ordinal);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Stack<string>(plan.References);
                while (pending.Count != 0)
                {
                    var id = pending.Pop();
                    if (!visited.Add(id) || !byId.TryGetValue(id, out var entry)) continue;
                    paths.Add(entry.Path);
                    ContentIdentity.TryParse(id, out var owner, out _);
                    if (installed.TryGetValue(owner, out var sourcePack)) paths.Add(sourcePack.Path + "/forge/content/pack.forge");
                    foreach (var reference in entry.References) pending.Push(reference);
                }
                var planPath = pack.Path + "/forge/plans/" + plan.File;
                prerequisiteFiles.Add(planPath, paths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
            }
            mappings.Add(pack.Path, plans);
        }
    }

    internal static bool TryReadMappings(JsonElement value, out IReadOnlyList<ContentPlanReference> result, out string problem)
    {
        result = Array.Empty<ContentPlanReference>(); problem = "Invalid content plan map; re-export the content package.";
        if (value.ValueKind != JsonValueKind.Array) return false;
        var rows = new List<ContentPlanReference>(); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in row.EnumerateObject())
                if (property.Name is not ("file" or "references") || !keys.Add(property.Name)) return false;
            if (keys.Count != 2 || row.GetProperty("file").ValueKind != JsonValueKind.String) return false;
            var file = row.GetProperty("file").GetString()!;
            if (file.Length > 255 || !FileName.IsMatch(file) || !ForgeCore.PosixPath.IsRelativeFile(file) || !files.Add(file)) return false;
            var references = row.GetProperty("references");
            if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() > ContentPackDiscovery.MaximumEntries) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal); var ids = new List<string>();
            foreach (var reference in references.EnumerateArray())
            {
                if (reference.ValueKind != JsonValueKind.String) return false;
                var id = reference.GetString()!;
                if (id.Any(character => character < 33 || character > 126) || !ContentIdentity.TryParse(id, out _, out _) || !seen.Add(id)) return false;
                ids.Add(id);
            }
            rows.Add(new ContentPlanReference(file, ids.ToArray()));
        }
        result = rows.ToArray(); problem = ""; return true;
    }

    /// <summary>Run when a level's plans load and at explicit authoring reload. Structural ambiguity invalidates only
    /// that content package. A well-formed map's missing/rejected prerequisite refuses only the dependent plan.
    /// <paramref name="discovered"/> is every plan path discovery found, when the candidates are one level's part of
    /// them: a package's file set is judged on all of its files, never on the part one level reads.</summary>
    internal IReadOnlyList<PlanCandidate> Check(IReadOnlyList<PlanCandidate> candidates, IEnumerable<string>? discovered = null)
    {
        var output = new List<PlanCandidate>(candidates.Count);
        var active = candidates.Where(candidate => !menuSlotRefusedRoots.Contains(Package(candidate.Path) ?? "")).ToArray();
        var checkedSources = new Dictionary<string, bool>(StringComparer.Ordinal);
        var readBudget = new ContentReadBudget();
        var recheckExtra = extra?.Recheck();
        bool Unchanged(string path)
        {
            if (checkedSources.TryGetValue(path, out var same)) return same;
            same = MatchesSource(path, readBudget); checkedSources.Add(path, same); return same;
        }
        // Slot losers are already refused as entire packages. Preserve the kernel's existing cross-package
        // conflict rule for every other content-refused copy.
        // Otherwise a refused conflicting copy could disappear and silently make its counterpart admissible.
        var identities = new Dictionary<string, List<PlanCandidate>>(StringComparer.Ordinal);
        foreach (var candidate in active)
        {
            if (candidate.IsHostRejected) continue;
            try
            {
                var identity = RuntimePlan.PeekIdentity(candidate.Document!, runtime);
                if (!identities.TryGetValue(identity.Id, out var rows)) identities.Add(identity.Id, rows = new());
                rows.Add(candidate);
            }
            catch (RuntimeContractException) { } // The ordinary kernel still emits the original identity refusal.
        }
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rows in identities.Values)
            if (rows.Skip(1).Any(row => row.ContentDigest != rows[0].ContentDigest))
                foreach (var row in rows) conflicts.Add(row.Path);
        var structural = new HashSet<string>(StringComparer.Ordinal);
        var listed = (discovered ?? active.Select(candidate => candidate.Path))
            .Where(path => !menuSlotRefusedRoots.Contains(Package(path) ?? "")).Distinct(StringComparer.Ordinal);
        var groups = listed.Where(path => Package(path) != null)
            .GroupBy(path => Package(path)!, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var expectedKind = contentRoots.Contains(group.Key) ? ContentPackDiscovery.ContentPathState.Directory : ContentPackDiscovery.ContentPathState.Absent;
            if (ContentPackDiscovery.ContentDirectoryState(root, group.Key) != expectedKind)
            { structural.Add(group.Key); continue; }
            if (!contentRoots.Contains(group.Key)) continue;
            if (!mappings.TryGetValue(group.Key, out var planMap) || !Unchanged(group.Key + "/forge/content/pack.forge")
                || !DirectoryMatches(group.Key, planMap.Keys)) { structural.Add(group.Key); continue; }
            var names = group.Select(path => path[(path.LastIndexOf('/') + 1)..]).ToArray();
            if (new HashSet<string>(names, StringComparer.OrdinalIgnoreCase).Count != names.Length
                || names.Length != planMap.Count || names.Any(name => !planMap.ContainsKey(name))) structural.Add(group.Key);
        }
        foreach (var candidate in candidates)
        {
            var package = Package(candidate.Path);
            // Preserve the discovery layer's own per-file IO/size/path refusal and its existing reason.
            if (candidate.IsHostRejected || package == null) { output.Add(candidate); continue; }
            if (conflicts.Contains(candidate.Path))
            {
                output.Add(PlanCandidate.Rejected(candidate.Path, "plan-conflict", "Installed copies of this plan identity have different content."));
                continue;
            }
            string? code = null;
            ContentRejection? dependencyFailure = null;
            if (!packageRoots.Contains(package) || structural.Contains(package))
            {
                dependencyFailure = menuSlotRefusedRoots.Contains(package) ? null : extra?.Failure(package);
                code = dependencyFailure?.Code ?? ContentPackDiscovery.CodeInvalid;
            }
            else if (recheckExtra?.Invoke(package) is { } extraFailure)
            { dependencyFailure = extraFailure; code = extraFailure.Code; }
            else if (contentRoots.Contains(package))
            {
                var file = candidate.Path[(candidate.Path.LastIndexOf('/') + 1)..];
                if (structural.Contains(package) || !mappings.TryGetValue(package, out var planMap)
                    || !planMap.TryGetValue(file, out code)) code = ContentPackDiscovery.CodeInvalid;
                else if (!MatchesPlanId(candidate.Document!.Syntax, file)) code = ContentPackDiscovery.CodeInvalid;
                else if (code == null && prerequisiteFiles[candidate.Path].Any(path => !Unchanged(path)))
                    code = ContentPackDiscovery.CodeEntryMissing;
            }
            output.Add(code == null ? candidate : PlanCandidate.Rejected(candidate.Path, code,
                dependencyFailure?.Detail ?? "The plan's declared content prerequisites are unavailable or invalid; re-export or repair the content package."));
        }
        return output;
    }

    /// <summary>The exact-suffix ordinary discovery intentionally ignores unrelated files. Content packages also
    /// reject case-insensitive plan aliases so Linux cannot accept a layout that collides on Windows.</summary>
    private bool DirectoryMatches(string package, IEnumerable<string> declared)
    {
        try
        {
            if (ContentPackDiscovery.CanonicalDirectory(root, package + "/forge", "plans") != ContentPackDiscovery.ContentPathState.Directory) return false;
            var directory = FrameworkFiles.Resolve(root, package + "/forge/plans");
            var actual = new HashSet<string>(StringComparer.Ordinal);
            var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The listing itself reports each entry's type without following it, so no entry is looked up again.
            var entries = new FileSystemEnumerable<(string Name, bool IsDirectory)>(directory,
                (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.IsDirectory),
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false });
            foreach (var (name, isDirectory) in entries)
            {
                if (!name.TrimEnd(' ', '.').EndsWith(".plan.forge", StringComparison.OrdinalIgnoreCase)) continue;
                if (!FileName.IsMatch(name) || !ForgeCore.PosixPath.IsRelativeFile(name) || name.Length > 255 || !folded.Add(name)) return false;
                // The ordinary discovery owns a linked file's per-file refusal; it must not turn an unrelated
                // sibling into a package-wide failure.
                if (isDirectory) return false;
                actual.Add(name);
            }
            return actual.SetEquals(declared);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private bool MatchesSource(string relative, ContentReadBudget budget)
    {
        if (!sourceReads.TryGetValue(relative, out var expected) || expected.Sha256 == null) return false;
        return ContentDependencyLocks.TryRead(root, relative, budget, _ => { }, out _, out var actual)
            && actual.Bytes == expected.Bytes && actual.Sha256 == expected.Sha256;
    }

    private static string? Package(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 5 && parts[0] == "plugins" && parts[2] == "forge" && parts[3] == "plans"
            ? parts[0] + "/" + parts[1] : null;
    }
    /// <summary>Whether the plan's own `planId` names this file. Read from the document's syntax-level value, which is
    /// empty (not an object) for text that does not parse.</summary>
    private static bool MatchesPlanId(JsonElement root, string file)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("planId", out var id)
            && id.ValueKind == JsonValueKind.String && file == id.GetString() + ".plan.forge";
}
