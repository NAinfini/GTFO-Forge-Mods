using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>A package-level refusal, collected before native installation and logged once the Map provider exists.</summary>
internal sealed record MapPackageRejection(string Package, string Document, string Path, string Reason, string Detail)
{
    internal IReadOnlyList<ScanCoverageRefusal> ScanCoverage { get; init; } = Array.Empty<ScanCoverageRefusal>();
    /// <summary>The author's own id of the refused row (contract I-RELOAD), empty when the refusal is about a document.</summary>
    internal string AuthorId { get; init; } = "";
}

/// <summary>Hands Map the documents of one kind every installed package ships (<c>forge/&lt;directory&gt;/&lt;pattern&gt;</c>):
/// in the game ForgeEngine's one discovery (<c>ContentPackages.Read</c>), offline a walk of the exported package.</summary>
internal delegate IReadOnlyList<PublishedResource> MapDocumentReader(string forgeDirectory, string pattern,
    PublishedDiscoveryCodes codes, Action<PublishedResourceFailure> rejected);

/// <summary>The exact text read through the Runtime's bounded, path-safe reader. Consumers never reopen this file.</summary>
internal sealed record MapPackageDocument(PublishedResource Resource, string Text)
{
    internal string Name => System.IO.Path.GetFileName(Resource.Relative);
}

/// <summary>Frozen input for one installation directory. A bad document withdraws the entire directory's inputs,
/// including documents parsed earlier; it does not prevent the other directories from being examined.</summary>
internal sealed class MapPackageDocuments
{
    internal string Root { get; }
    internal IReadOnlyList<MapPackageDocument> Documents { get; }
    internal IReadOnlyList<MapPackageRejection> Rejections { get; }
    internal bool IsRejected => Rejections.Count != 0;

    private MapPackageDocuments(string root, IReadOnlyList<MapPackageDocument> documents, IReadOnlyList<MapPackageRejection> rejections)
    { Root = root; Documents = documents; Rejections = rejections; }

    internal static readonly PublishedTextCodes Codes = new("map-package-path", "map-package-unreadable", "map-package-file-size",
        "map-package-json", "map-package-utf8");

    // Only Map's own input documents belong here. Runtime's content/plan documents retain their owning reader.
    private static readonly IReadOnlyDictionary<string, int> Sidecars = new ReadOnlyDictionary<string, int>(
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["map-generation.forge"] = PublishedResources.MaximumFileBytes,
            [InteriorDoorDocument.FileName] = PublishedResources.MaximumFileBytes,
            ["room-build.forge"] = PublishedResources.MaximumFileBytes,
            [RoomFurnishing.FileNameOnly] = PublishedResources.MaximumFileBytes,
            [FixedPlacementManifest.ItemFileNameOnly] = PublishedResources.MaximumFileBytes,
            [FixedPlacementManifest.EnemyFileNameOnly] = PublishedResources.MaximumFileBytes,
            [FixedPlacementManifest.SackFileNameOnly] = PublishedResources.MaximumFileBytes,
            [FixedPlacementManifest.LandingFileNameOnly] = PublishedResources.MaximumFileBytes,
            [FixedPlacementManifest.BulkheadControllerFileNameOnly] = PublishedResources.MaximumFileBytes,
            [FixedPlacementManifest.BulkheadKeyFileNameOnly] = PublishedResources.MaximumFileBytes,
            [DoorLocksDocument.FileName] = PublishedResources.MaximumFileBytes,
            [ObjectiveRoundsDocument.FileName] = PublishedResources.MaximumFileBytes,
            [GlobalLightDocument.FileName] = PublishedResources.MaximumFileBytes,
            [GeneratorSourcesDocument.FileName] = PublishedResources.MaximumFileBytes,
            [TriggerZoneManifest.FileNameOnly] = PublishedResources.MaximumFileBytes,
            [InteractionPointManifest.FileNameOnly] = PublishedResources.MaximumFileBytes,
            [PlacedModelManifest.FileNameOnly] = PublishedResources.MaximumFileBytes,
            [PlacedModelManifest.ComposedFileNameOnly] = PublishedResources.MaximumFileBytes,
            [CarryItemManifest.FileNameOnly] = PublishedResources.MaximumFileBytes,
            [WaveAnchorManifest.FileNameOnly] = PublishedResources.MaximumFileBytes,
            ["wave-resources.forge"] = PublishedResources.MaximumFileBytes,
            [ScanCoverageManifest.FileNameOnly] = PublishedResources.MaximumFileBytes,
            [ScanPlacementManifest.FileNameOnly] = PublishedResources.MaximumFileBytes,
            [ExtractionPointManifest.FileNameOnly] = PublishedResources.MaximumFileBytes,
            [EntryLandingManifest.FileNameOnly] = PublishedResources.MaximumFileBytes
        });

    internal static MapPackageDocuments Empty(string root)
        => new(root, Array.Empty<MapPackageDocument>(), Array.Empty<MapPackageRejection>());

    internal static IReadOnlyList<MapPackageDocuments> Capture(MapDocumentReader read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var documents = new Dictionary<string, List<MapPackageDocument>>(StringComparer.Ordinal);
        var refusals = new Dictionary<string, List<MapPackageRejection>>(StringComparer.Ordinal);
        void Ensure(string owner)
        {
            if (!documents.ContainsKey(owner)) documents.Add(owner, new());
            if (!refusals.ContainsKey(owner)) refusals.Add(owner, new());
        }
        void Reject(PublishedResourceFailure failure)
        {
            string owner = PublishedResources.PackageRoot(failure.Relative);
            if (owner == "plugins")
                throw new InvalidDataException("Map package discovery failed before an owning package could be identified: "
                    + failure.Code + ": " + failure.Reason);
            Ensure(owner);
            var rejection = new MapPackageRejection(Path.GetFileName(owner), Path.GetFileName(failure.Relative), failure.Relative,
                failure.Code, failure.Reason);
            if (!refusals[owner].Contains(rejection)) refusals[owner].Add(rejection);
        }
        // The walks below only plan: every refusal and every admitted file is a step, applied after the reads in the order a
        // one-file-at-a-time pass would have applied it. The reads themselves (bounded read, UTF-8 and JSON check, PNG and
        // mesh hash) run side by side, one worker per read budget: a budget's files keep their order, so each budget sees
        // the same sequence of reads and refuses the same file.
        var steps = new List<Action>();
        var reads = new List<(string Budget, Func<Action> Read)>();
        var outcomes = Array.Empty<Action>();
        void Later(string budget, Func<Action> read)
        {
            int index = reads.Count;
            reads.Add((budget, read));
            steps.Add(() => outcomes[index]());
        }
        void Refuse(PublishedResourceFailure failure) => steps.Add(() => Reject(failure));
        void Read(string budget, PublishedResource document, int cap) => Later(budget, () =>
        {
            var failures = new List<PublishedResourceFailure>();
            bool read = PublishedResources.TryRead(document, cap, Codes, failures.Add, out var text, out _);
            return () =>
            {
                string owner = PublishedResources.PackageRoot(document.Relative);
                Ensure(owner);
                failures.ForEach(Reject);
                if (read) documents[owner].Add(new(document, text!));
            };
        });
        // One read finds every sidecar of every package. Each name still has its own read budget, and its
        // refusals reach Reject at the same point a walk of its own would have reported them: the refusals of the
        // directories (named by no sidecar) with the first name, a document's own refusal with its name.
        var directoryOrDocument = new List<(string? Name, PublishedResourceFailure Failure)>();
        var found = read("forge", string.Join("|", Sidecars.Keys), Codes,
            (PublishedResourceFailure failure) =>
            {
                var file = Path.GetFileName(failure.Relative);
                directoryOrDocument.Add((Sidecars.ContainsKey(file) ? file : null, failure));
            }).ToLookup(document => Path.GetFileName(document.Relative), StringComparer.Ordinal);
        foreach (var (name, cap) in Sidecars)
        {
            foreach (var (failedName, failure) in directoryOrDocument)
                if (failedName == null || failedName == name) Refuse(failure);
            foreach (var document in found[name]) Read(name, document, cap);
        }
        foreach (var group in read("forge/map-fields", "*.forge", Codes, Refuse)
                     .GroupBy(document => PublishedResources.PackageRoot(document.Relative), StringComparer.Ordinal))
        {
            if (group.Count() > Fields.MapFieldDocuments.MaximumFilesPerPackage)
            {
                Refuse(new(group.Key + "/forge/map-fields", "map-package-document-limit",
                    "The package exceeds the map field document limit."));
                continue;
            }
            foreach (var document in group) Read("map-fields", document, Fields.MapFieldDocuments.MaximumFileBytes);
        }
        // Image-board PNGs and imported model files are found by hash when admission needs them (I-RESOLVE), not read here.
        var resources = read("forge/resources", "*.resource.forge", Codes, Refuse).ToArray();
        if (resources.Length > PublishedResources.MaximumResources)
            foreach (var document in resources)
                Refuse(new(document.Relative, "map-package-resource-limit", "The install exceeds the existing runtime resource document limit."));
        else foreach (var document in resources) Read(".forge", document, PublishedResources.MaximumResourceBytes);
        outcomes = new Action[reads.Count];
        var budgets = Enumerable.Range(0, reads.Count).GroupBy(index => reads[index].Budget, StringComparer.Ordinal)
            .Select(group => group.ToArray()).ToArray();
        try
        {
            Parallel.For(0, budgets.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
                budget => { foreach (int index in budgets[budget]) outcomes[index] = reads[index].Read(); });
        }
        catch (AggregateException error)
        {
            // One failed budget group is rethrown as itself; several are thrown together, so no group's cause is lost.
            var flat = error.Flatten();
            if (flat.InnerExceptions.Count == 1) ExceptionDispatchInfo.Capture(flat.InnerExceptions[0]).Throw();
            throw flat;
        }
        foreach (var step in steps) step();
        return Array.AsReadOnly(documents.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new MapPackageDocuments(pair.Key,
                Array.AsReadOnly(pair.Value.OrderBy(value => value.Resource.Relative, StringComparer.Ordinal).ToArray()),
                Array.AsReadOnly(refusals[pair.Key].ToArray()))).ToArray());
    }
}
