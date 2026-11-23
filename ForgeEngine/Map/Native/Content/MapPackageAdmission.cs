using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeMap.Fields;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>Map-owned package transaction. Runtime's previously published Item/Enemy content is outside it.
/// Every document and content reference is prepared before Rundown publication; native owners receive only the
/// same frozen survivors. Unknown native rollback state remains fatal in the shared publisher.</summary>
internal sealed class MapPackageAdmission
{
    private readonly IReadOnlyList<MapPackageStage> _stages;
    private readonly Action<string> _report;
    private readonly Action<MapPackageRejection>? _refusePlans;
    private readonly OnceReporter _once;
    private bool _prepared;
    internal IReadOnlyList<RundownPack> CapturedPacks { get; }
    /// <summary>Every package's captured sidecars, in root order (the content generation's Map digest and generation plans).</summary>
    internal IReadOnlyList<MapPackageStage> Stages => _stages;
    internal int FieldEntries { get; private set; }
    // Reading every package's sidecars happens in Capture, before the Map log exists, so the summary line waits for Prepare.
    private long _captureMilliseconds;
    // The BepInEx root the packages were captured from; imported model files are found under it by hash (I-RESOLVE).
    private string _installRoot = "";
    private const string AdmissionSummaryCode = "map.package-admission";
    private MapPackageAdmission(IReadOnlyList<MapPackageStage> stages, IReadOnlyList<RundownPack> captured,
        Action<string> report, Action<MapPackageRejection>? refusePlans = null)
    { _stages = stages; CapturedPacks = captured; _report = report; _once = new OnceReporter(report); _refusePlans = refusePlans; }

    internal static MapPackageAdmission Capture(string root, Action<string> report,
        Action<MapPackageRejection>? refusePlans = null)
    {
        var started = Stopwatch.GetTimestamp();
        // ForgeEngine's one discovery hands Map the Rundown packs and its documents; the packs pass unchanged to menu/host
        // admission. The intake itself is the code the offline package-admission runner executes.
        var intake = MapPackageIntake.Run(root,
            (directory, pattern, codes, rejected) => ContentPackages.Read(root, ContentPackages.EngineReader, directory, pattern, codes, rejected),
            refused => ContentPackages.ReadRundowns(root, ContentPackages.EngineReader, refused));
        return new(intake.Stages, intake.Packs, report, refusePlans)
        { _captureMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency, _installRoot = root };
    }
    internal static MapPackageAdmission Empty(Action<string> report)
        => new(Array.Empty<MapPackageStage>(), Array.Empty<RundownPack>(), report);

    private readonly List<RundownDiagnostic> _rundownRefusals = new();
    /// <summary>The Rundown refusals the commit of these packages reported (validation, menu slot, native publication), in order: the
    /// reload result lists each with its package.</summary>
    internal IReadOnlyList<RundownDiagnostic> RundownRefusals => _rundownRefusals;

    /// <summary>The Rundown report of a commit of these packages: kept for the reload result, then reported as every Rundown refusal is.</summary>
    internal void Report(RundownDiagnostic value)
    {
        _rundownRefusals.Add(value);
        RundownMenu.Report(value);
    }

    internal void ReportRefusals()
    {
        foreach (var rejection in _stages.SelectMany(stage => stage.Rejections))
        {
            var key = string.Join("/", rejection.Package, rejection.Document, rejection.Path, rejection.Reason, rejection.Detail);
            if (_once.Count(key) == 0)
            {
                _refusePlans?.Invoke(rejection);
                int? slot = CapturedPacks.FirstOrDefault(pack => PublishedResources.PackageRoot(pack.Path)
                    == PublishedResources.PackageRoot(rejection.Path))?.MenuSlot;
                foreach (var scan in rejection.ScanCoverage)
                {
                    var values = new List<RuntimeLogValue> { RuntimeLogValue.Of("reason", scan.Code) };
                    if (scan.Level is { } level) values.Add(RuntimeLogValue.Of("level", level.ToString()));
                    if (scan.ChainedPuzzleId is { } puzzle) values.Add(RuntimeLogValue.Of("chainedPuzzleId", (long)puzzle));
                    if (scan.Component is { } component) values.Add(RuntimeLogValue.Of("component", (long)component));
                    MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ScanCoverageRefused, scan.Reason, values.ToArray());
                }
                MapLog.PackageRejected(rejection.Package, rejection.Document, rejection.Path, rejection.Reason, rejection.Detail, slot);
                _once.Report(key, MapLog.SafeDetail("map.package-rejected package=" + rejection.Package + " slot=" + (slot?.ToString() ?? "unknown") + " document=" + rejection.Document
                    + " file=" + rejection.Path + " reason=" + rejection.Reason + ": " + rejection.Detail));
            }
        }
    }

    internal MapPreparedPackages Prepare(AdmittedContentSnapshot content, IReadOnlyList<RundownPack> installed,
        IReadOnlyList<ContentVanillaId> occupied, Action<RundownDiagnostic> rundownReport)
    {
        if (_prepared) throw new InvalidOperationException("Map package preparation is startup-only.");
        _prepared = true;
        var started = Stopwatch.GetTimestamp();
        if (NativeContentRefusal.Stopped != null)
            throw new InvalidOperationException("Native tables failed their ownership audit; restart before Map package preparation.");
        // Resolve A4 occupancy against the complete canonical snapshot, before Map validity. A bad winner
        // leaves vanilla in its slot; a same-slot loser never gets a second chance or reserves Map claims.
        var slotWinners = RundownPacks.SelectSlots(CapturedPacks, _ => { })
            .Select(pack => PublishedResources.PackageRoot(pack.Path)).ToHashSet(StringComparer.Ordinal);
        var slotLosers = CapturedPacks.Where(pack => !slotWinners.Contains(PublishedResources.PackageRoot(pack.Path)))
            .Select(pack => PublishedResources.PackageRoot(pack.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var stage in _stages.Where(stage => slotLosers.Contains(stage.Root))) stage.RefuseSlot();
        // Identity/referral validation sees the original complete discovered wrappers. Filtering Map sidecars
        // never fabricates or suppresses Runtime's captured cross-domain package identity preflight.
        var alreadyRejected = _stages.Where(stage => stage.IsRejected).Select(stage => stage.Root).ToHashSet(StringComparer.Ordinal);
        // Validate each wrapper in isolation first. A later sidecar/content-reference refusal must not reserve
        // native identities and turn an otherwise usable package into a collision victim.
        var candidates = installed.Where(pack => !alreadyRejected.Contains(PublishedResources.PackageRoot(pack.Path)))
            .SelectMany(pack => content.ValidateRundowns(new[] { pack }, occupied, rundownReport)).ToArray();
        var owners = candidates.ToDictionary(pack => PublishedResources.PackageRoot(pack.Path), StringComparer.Ordinal);
        var models = new Dictionary<string, IReadOnlyList<MapModelAssembly>>(StringComparer.Ordinal);
        var carried = new Dictionary<string, IReadOnlyList<CarryItemAssembly>>(StringComparer.Ordinal);
        foreach (var stage in _stages.Where(stage => !stage.IsRejected))
        {
            try
            {
                owners.TryGetValue(stage.Root, out var pack);
                bool declaredRundown = CapturedPacks.Any(candidate => PublishedResources.PackageRoot(candidate.Path) == stage.Root);
                if (pack == null)
                {
                    if (declaredRundown || HasLevelDocuments(stage) || stage.AreaTables.Any(table => table.Levels.Any()))
                        throw Refusal(stage.Root + "/forge/rundown.forge", "package-owner", "No usable owning Rundown was admitted.");
                    continue; // Standalone audio and reusable area dependencies do not need a Rundown.
                }
                var levels = Levels(pack);
                void Level(MapLevelReference level, string path)
                {
                    if (!levels.Contains(level))
                        throw Refusal(path, "level-owner", "Level " + level + " is not an authored tier/index of this package's Rundown.");
                }
                foreach (var field in stage.Fields) Level(field.Target.Level, field.Source);
                void Check(IEnumerable<MapLevelReference> values, string name)
                { foreach (var level in values) Level(level, stage.Root + "/forge/" + name); }
                Check(stage.InteriorDoors.Select(row => row.Level), InteriorDoorDocument.FileName);
                if (stage.RoomBuild != null)
                    try { stage.Recipes.AddRange(RoomBuildRecipe.ReadMany(stage.RoomBuild, sha => ResourceFile(stage.Root, sha, RoomBuildRecipe.FileExtension, "room-build.forge"))); }
                    catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                    { throw Refusal(stage.Root + "/forge/room-build.forge", "document-invalid", error.Message); }
                Check(stage.Recipes.SelectMany(recipe => recipe.Hosts).Select(host => host.Level), "room-build.forge");
                if (stage.Furnishings != null)
                {
                    IReadOnlyList<PlacedModel> furnished;
                    try { furnished = RoomFurnishing.ReadMany(stage.Furnishings, sha => ResourceFile(stage.Root, sha, RoomFurnishing.FileExtension, RoomFurnishing.FileNameOnly)); }
                    catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
                    { throw Refusal(stage.Root + "/forge/" + RoomFurnishing.FileNameOnly, "document-invalid", error.Message); }
                    Check(furnished.Select(model => model.Level), RoomFurnishing.FileNameOnly);
                    stage.PlacedModels.AddRange(furnished);
                }
                Check(stage.Items.Select(row => row.Level), "fixed-items.forge");
                Check(stage.Enemies.Select(row => row.Level), "fixed-enemies.forge");
                Check(stage.Sacks.Select(row => row.Level), "fixed-sacks.forge");
                Check(stage.Landings.Select(row => row.Level), "dimension-landings.forge");
                Check(stage.Controllers.Select(row => row.Level), "bulkhead-controllers.forge");
                Check(stage.Keys.Select(row => row.Level), "bulkhead-keys.forge");
                Check(stage.TriggerZones.Select(row => row.Level), "trigger-zones.forge");
                Check(stage.InteractionPoints.Select(row => row.Level), "interaction-points.forge");
                Check(stage.CarryItems.Select(row => row.Level), CarryItemManifest.FileNameOnly);
                foreach (var carry in stage.CarryItems)
                    if (!pack.Blocks.Any(block => block.Type == "Item" && block.PersistentId == carry.ItemId))
                        throw Refusal(stage.Root + "/forge/" + CarryItemManifest.FileNameOnly, "carry-item-block",
                            "Carry item `" + carry.Id + "` names Item " + carry.ItemId + ", which is not an Item block of this package.");
                foreach (var model in stage.PlacedModels)
                    Level(model.Level, stage.Root + "/forge/" + (model.Content.HasValue
                        ? PlacedModelManifest.ComposedFileNameOnly : PlacedModelManifest.FileNameOnly));
                foreach (var point in stage.InteractionPoints.Where(row => row.Appearance != null))
                    if (!stage.PlacedModels.Any(model => model.Level == point.Level
                        && model.Id == point.Appearance!.PlacedModelId))
                        throw Refusal(stage.Root + "/forge/interaction-points.forge", "interaction-point-model-reference",
                            "Interaction point `" + point.Id + "` names placed model `"
                            + point.Appearance!.PlacedModelId + "`, which is not a placed model of level "
                            + point.Level + ".");
                Check(stage.WaveAnchors.Select(row => row.Level), "wave-anchors.forge");
                Check(stage.Waves.SelectMany(row => row.Waves).Select(row => MapLevelReference.TryParse(row.Level)
                    ?? throw Refusal(stage.Root + "/forge/wave-resources.forge", "level-owner", "Invalid wave level.")), "wave-resources.forge");
                Check(stage.ScanCoverage.Select(row => row.Level), "scan-coverage.forge");
                Check(stage.ScanPlacements.Select(row => row.Level), "scan-placements.forge");
                Check(stage.ExtractionPoints.Select(row => row.Level), ExtractionPointManifest.FileNameOnly);
                Check(stage.EntryLandings.Select(row => row.Level), EntryLandingManifest.FileNameOnly);
                Check(stage.DoorLocks.Select(row => row.Level), DoorLocksDocument.FileName);
                Check(stage.ObjectiveRounds.Select(row => row.Level), ObjectiveRoundsDocument.FileName);
                Check(stage.GlobalLight.Select(row => row.Level), GlobalLightDocument.FileName);
                Check(stage.GeneratorSources.Select(row => row.Level), GeneratorSourcesDocument.FileName);
                foreach (var area in stage.AreaTables.SelectMany(table => table.Levels)) Level(area, stage.Root + "/forge/resources");
                foreach (var item in stage.Items)
                    if (item.ItemId is { } id && !pack.Blocks.Any(block => block.Type == "Item" && block.PersistentId == id)
                        && !content.DependencyItems(pack).Contains(id))
                    {
                        bool original = content.IsOriginalItem(id) && occupied.Any(row => row.Type == "Item" && row.Id == id);
                        if (original)
                            try { NativeDataBlockTables.RequireBlock("Item", id); }
                            catch (Exception error) when (error is InvalidOperationException or System.Reflection.TargetInvocationException)
                            { original = false; }
                        if (!original) throw Refusal(stage.Root + "/forge/fixed-items.forge", "fixed-item-provenance",
                            "Item " + id + " is neither a loaded original, an own Item nor an Item of a declared dependency.");
                    }
                var carryAssemblies = CarryItemAssembly.Prepare(stage.Root, stage.CarryItems, LevelIdentity.Read);
                models.Add(stage.Root, MapModelAssembly.Prepare(stage.Root, stage.PlacedModels, LevelIdentity.Read,
                    MapModelAssembly.ImportedPackages(_installRoot, stage, _report)));
                // Image boards' PNGs, found by hash in the package or its dependencies (I-RESOLVE): a missing file is an Error naming the
                // package, the hash and the board document, and that board draws nothing; a file that fails its hash or PNG check refuses the package.
                foreach (var png in stage.PlacedModels.Where(model => model.Image != null).Select(model => model.Image!.PngSha256).Distinct(StringComparer.Ordinal))
                {
                    var file = ContentPackages.FindResource(_installRoot, stage.Root, png, ".png", stage.Root + "/forge/" + PlacedModelManifest.FileNameOnly,
                        failure => _report(failure.Code + ": " + failure.Relative + " " + failure.Reason));
                    if (file is not { } found) continue;
                    if (!PlacedImageResources.TryRead(found, out var image, out var refused))
                        throw new MapPackageDocumentException(refused.Code, refused.Reason, refused.Relative);
                    stage.Images.Add(image!);
                }
                carried.Add(stage.Root, carryAssemblies);
            }
            catch (Exception error) when (error is MapPackageDocumentException or System.Text.Json.JsonException
                or RuntimeContractException or InvalidOperationException or KeyNotFoundException or FormatException)
            { Reject(stage, error); }
        }
        ValidateResources();
        ValidateClaims();
        var rejectedRoots = _stages.Where(stage => stage.IsRejected).Select(stage => stage.Root).ToHashSet(StringComparer.Ordinal);
        var survivors = candidates.Where(pack => !rejectedRoots.Contains(PublishedResources.PackageRoot(pack.Path))).ToArray();
        var packs = content.ValidateRundowns(survivors, occupied, rundownReport);
        var survivingRoots = packs.Select(pack => PublishedResources.PackageRoot(pack.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var stage in _stages.Where(stage => !stage.IsRejected && owners.ContainsKey(stage.Root) && !survivingRoots.Contains(stage.Root)))
            Reject(stage, Refusal(stage.Root + "/forge/rundown.forge", "rundown-rejected", "Rundown publication validation rejected this package."));
        var accepted = _stages.Where(stage => !stage.IsRejected).ToArray();
        var prepared = new MapPreparedPackages(accepted, packs, models, carried);
        ReportRefusals();
        if (MapLog.Logs(RuntimeLogLevel.Info))
        {
            long prepareMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;
            MapLog.Record(RuntimeLogLevel.Info, AdmissionSummaryCode, "Map package admission finished.",
                RuntimeLogValue.Of("packages", (long)_stages.Count), RuntimeLogValue.Of("accepted", (long)accepted.Length),
                RuntimeLogValue.Of("captureMs", _captureMilliseconds), RuntimeLogValue.Of("prepareMs", prepareMilliseconds),
                RuntimeLogValue.Of("elapsedMs", _captureMilliseconds + prepareMilliseconds));
        }
        return prepared;
    }

    // Run after existing Map hook preparation, still before any native Rundown publication.
    // A target without a proven Runtime installation withdraws its package, never the vanilla menu or other packages.
    internal MapPreparedPackages PrepareGenerations(MapPreparedPackages prepared, Action<Generation.GenerationProgram> prepare)
    {
        foreach (var stage in _stages.Where(stage => !stage.IsRejected && stage.Generation != null && stage.Generation.Plan.Levels.GetArrayLength() != 0))
        {
            try { prepare(stage.Generation!); }
            catch (Generation.GenerationRefusal error)
            { Reject(stage, Refusal(stage.Root + "/forge/map-generation.forge", error.Code, error.Path)); }
            catch (Exception error)
            { Reject(stage, Refusal(stage.Root + "/forge/map-generation.forge", "gen-hook-unavailable", error.ToString())); }
        }
        var accepted = _stages.Where(stage => !stage.IsRejected).ToArray();
        ReportRefusals();
        return prepared.Retain(accepted);
    }

    private static bool HasLevelDocuments(MapPackageStage stage) => stage.Documents.Any(document =>
        !document.Resource.Relative.StartsWith(stage.Root + "/forge/resources/", StringComparison.Ordinal));
    /// <summary>The authored tier/index of every level of a Rundown pack.</summary>
    internal static HashSet<MapLevelReference> Levels(RundownPack pack)
        => pack.AuthoredLevels().Select(level => new MapLevelReference(pack.RundownId, level.Tier, level.Index)).ToHashSet();
    private static MapPackageDocumentException Refusal(string path, string code, string reason) => new(code, reason, path);
    /// <summary>A room or furnishing file by hash in the package or its dependencies (I-RESOLVE), asked for by the package's
    /// <paramref name="document"/>; a missing or unreadable one refuses the package.</summary>
    private byte[] ResourceFile(string package, string sha, string extension, string document)
    {
        string requester = package + "/forge/" + document;
        var file = ContentPackages.FindResource(_installRoot, package, sha, extension, requester,
            failure => _report(failure.Code + ": " + failure.Relative + " " + failure.Reason))
            ?? throw Refusal(requester, "resource-missing", "File " + sha + extension + " is in neither this package nor its dependencies.");
        string? why = null;
        return PublishedResources.TryReadBytes(file, PublishedResources.MaximumFileBytes, reason => why = reason, out var bytes)
            ? bytes! : throw Refusal(file.Relative, "document-invalid", why ?? "File unreadable.");
    }
    private static void Reject(MapPackageStage stage, Exception error)
    {
        string path = (error as MapPackageDocumentException)?.Path ?? stage.Root + "/forge/rundown.forge";
        stage.Reject(new(Path.GetFileName(stage.Root), Path.GetFileName(path), path,
            (error as MapPackageDocumentException)?.Code ?? "document-invalid", error.Message)
        { AuthorId = (error as MapPackageDocumentException)?.AuthorId ?? "" });
    }

    private void ValidateResources()
    {
        var resources = new Dictionary<string, List<(MapPackageStage Stage, MapPackageDocument Document)>>(StringComparer.Ordinal);
        foreach (var stage in _stages.Where(stage => !stage.IsRejected))
            foreach (var document in stage.Documents.Where(document => document.Resource.Relative.StartsWith(stage.Root + "/forge/resources/", StringComparison.Ordinal)))
            {
                using var parsed = JsonDocument.Parse(document.Text);
                var root = parsed.RootElement;
                if (!root.TryGetProperty("reference", out var reference) || reference.ValueKind != JsonValueKind.Object
                    || !reference.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
                    || !reference.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.String) continue;
                string id = idValue.GetString()!;
                if (!resources.TryGetValue(id, out var claims)) resources.Add(id, claims = new());
                claims.Add((stage, document));
            }
        // Only an id claimed by more than one document has content to compare, and identical text is identical content.
        static string Canonical(MapPackageDocument document)
        {
            using var parsed = JsonDocument.Parse(document.Text);
            return RuntimeJson.Canonical(parsed.RootElement);
        }
        foreach (var claims in resources.Values.Where(claims => claims.Count > 1))
        {
            if (claims.All(claim => string.Equals(claim.Document.Text, claims[0].Document.Text, StringComparison.Ordinal))) continue;
            var first = Canonical(claims[0].Document);
            if (claims.Skip(1).Any(claim => Canonical(claim.Document) != first))
                foreach (var claim in claims)
                    Reject(claim.Stage, Refusal(claim.Document.Resource.Relative, "resource-duplicate-id", "The resource identity has differing revisions or content."));
        }
    }

    private void ValidateClaims()
    {
        // Claims are made only by packages that passed every private read and content-reference validation.
        // Conflicting author identities withdraw every claiming package, preserving existing all-claimant
        // trigger/resource refusal semantics without allowing an already-bad package to reserve an identity.
        // Rows that belong to a level claim as level + id: one level runs at a time, so an original and its remake in
        // another Rundown may reuse an id, and only two rows of the same level conflict.
        var claims = new Dictionary<string, List<(MapPackageStage Stage, string Path)>>(StringComparer.Ordinal);
        void Claim(MapPackageStage stage, string key, string path)
        {
            if (!claims.TryGetValue(key, out var owners)) claims.Add(key, owners = new());
            owners.Add((stage, path));
        }
        foreach (var stage in _stages.Where(stage => !stage.IsRejected))
        {
            void Add(string family, IEnumerable<string> keys, string document)
            { foreach (var key in keys) Claim(stage, family + "/" + key, stage.Root + "/forge/" + document); }
            Add("trigger", stage.TriggerZones.Select(row => row.Level + "/" + row.Id), "trigger-zones.forge");
            Add("interaction", stage.InteractionPoints.Select(row => row.Level + "/" + row.Id), "interaction-points.forge");
            foreach (var model in stage.PlacedModels)
                Claim(stage, "model/" + model.Level + "/" + model.Id, stage.Root + "/forge/" + (model.Content.HasValue
                    ? PlacedModelManifest.ComposedFileNameOnly : PlacedModelManifest.FileNameOnly));
            Add("item", stage.Items.Select(row => row.Level + "/" + row.Id), "fixed-items.forge");
            Add("enemy", stage.Enemies.Select(row => row.Level + "/" + row.Id), "fixed-enemies.forge");
            Add("sack", stage.Sacks.Select(row => row.Level + "/" + row.Id), "fixed-sacks.forge");
            Add("landing", stage.Landings.Select(row => row.Level + "/" + row.Id), "dimension-landings.forge");
            Add("controller", stage.Controllers.Select(row => row.Level + "/" + row.Id), "bulkhead-controllers.forge");
            Add("key", stage.Keys.Select(row => row.Level + "/" + row.Id), "bulkhead-keys.forge");
            Add("interior-door", stage.InteriorDoors.Select(row => row.Level + "/" + row.Id), InteriorDoorDocument.FileName);
            Add("recipe", stage.Recipes.Select(row => row.ResourceKey), "room-build.forge");
            Add("functional-room", stage.Recipes.SelectMany(recipe => recipe.Hosts.Select(host => RoomBuildRecipe.FunctionalKey(host, recipe.Design!.Function!))), "room-build.forge");
            Add("wave", stage.Waves.SelectMany(document => document.Waves).Select(row => row.Address), "wave-resources.forge");
            Add("scan-coverage", stage.ScanCoverage.Select(row => row.Level + "/" + row.ChainedPuzzleId + "/" + row.Component), "scan-coverage.forge");
            Add("scan-placement-id", stage.ScanPlacements.Select(row => row.Level + "/" + row.Id), "scan-placements.forge");
            Add("scan-placement-filter", stage.ScanPlacements.Select(row => row.Level + "/" + row.Filter), "scan-placements.forge");
            Add("scan-placement-puzzle", stage.ScanPlacements.Select(row => row.Level + "/" + row.ChainedPuzzleId), "scan-placements.forge");
            Add("extraction-point", stage.ExtractionPoints.Select(row => row.Level.ToString()), ExtractionPointManifest.FileNameOnly);
            Add("entry-landing", stage.EntryLandings.Select(row => row.Level + (row.Slot is { } slot ? "/player-" + slot : "")), EntryLandingManifest.FileNameOnly);
            Add("door-lock", stage.DoorLocks.Select(row => row.Level + "/" + row.Door), DoorLocksDocument.FileName);
            Add("objective-rounds", stage.ObjectiveRounds.Select(row => row.Level + "/" + row.Objective), ObjectiveRoundsDocument.FileName);
            Add("global-light", stage.GlobalLight.Select(row => row.Level.ToString()), GlobalLightDocument.FileName);
            Add("generator-source", stage.GeneratorSources.Select(row => row.Level + "/" + row.Address), GeneratorSourcesDocument.FileName);
        }
        // One level runs at a time, so the zone cap counts each level's rows alone and withdraws only the packages that fill it.
        foreach (var level in _stages.Where(stage => !stage.IsRejected).SelectMany(stage => stage.TriggerZones.Select(row => row.Level)).Distinct().ToArray())
        {
            var contributing = _stages.Where(stage => !stage.IsRejected && stage.TriggerZones.Any(row => row.Level == level)).ToArray();
            if (contributing.Sum(stage => stage.TriggerZones.Count(row => row.Level == level)) <= TriggerZoneManifest.MaximumZones) continue;
            foreach (var stage in contributing)
                Reject(stage, Refusal(stage.Root + "/forge/trigger-zones.forge", "document-limit",
                    "The installed rows for level " + level + " exceed the existing " + TriggerZoneManifest.MaximumZones + " row limit."));
        }
        foreach (var (identity, owners) in claims.Where(pair => pair.Value.Count > 1))
            foreach (var owner in owners)
                Reject(owner.Stage, Refusal(owner.Path, "document-duplicate", "More than one row claims " + identity + "."));
    }

    internal void PrepareFields(MapPreparedContent content) => MapFieldHost.Prepare(content, _report);
    internal void ActivateFields(MapPreparedContent content)
    { MapFieldHost.Activate(content, _report); FieldEntries = content.Fields.Count; }
}

internal sealed class MapPreparedPackages
{
    private readonly IReadOnlyList<(string Root, MapPreparedContent Content)> _parts;
    private readonly IReadOnlyList<MapPackageStage> _stages;
    internal IReadOnlyList<RundownPack> Packs { get; }
    internal MapPreparedContent Content { get; }
    internal MapPreparedPackages(IReadOnlyList<MapPackageStage> stages, IReadOnlyList<RundownPack> packs,
        IReadOnlyDictionary<string, IReadOnlyList<MapModelAssembly>>? models = null,
        IReadOnlyDictionary<string, IReadOnlyList<CarryItemAssembly>>? carried = null)
    {
        _stages = stages;
        _parts = Array.AsReadOnly(stages.Select(stage => (stage.Root, new MapPreparedContent(new[] { stage },
            models != null && models.TryGetValue(stage.Root, out var rows) ? rows : Array.Empty<MapModelAssembly>(),
            carried != null && carried.TryGetValue(stage.Root, out var carriedRows) ? carriedRows : Array.Empty<CarryItemAssembly>()))).ToArray());
        Packs = packs;
        Content = new(_parts.Select(part => part.Content).ToArray());
    }
    internal MapPreparedPackages Retain(IReadOnlyList<MapPackageStage> stages)
    {
        var roots = stages.Select(stage => stage.Root).ToHashSet(StringComparer.Ordinal);
        // Generation hook admission filters the already prepared transaction. Keep the model
        // registrations it owns; reconstructing without them loses Commit/Rollback at publication.
        return new(stages, Packs.Where(pack => roots.Contains(PublishedResources.PackageRoot(pack.Path))).ToArray(),
            _parts.Where(part => roots.Contains(part.Root)).ToDictionary(part => part.Root,
                part => part.Content.ModelAssemblies, StringComparer.Ordinal),
            _parts.Where(part => roots.Contains(part.Root)).ToDictionary(part => part.Root,
                part => part.Content.CarryAssemblies, StringComparer.Ordinal));
    }
    internal void PublishModels(RundownPack pack, Action publish)
    {
        var content = _parts.Single(part => part.Root == PublishedResources.PackageRoot(pack.Path)).Content;
        var models = content.ModelAssemblies;
        var carried = content.CarryAssemblies;
        try
        {
            foreach (var model in models) model.Commit();
            foreach (var item in carried) item.Commit();
            publish();
        }
        catch
        {
            for (int index = carried.Count - 1; index >= 0; index--) carried[index].Rollback();
            for (int index = models.Count - 1; index >= 0; index--) models[index].Rollback();
            throw;
        }
    }
    internal MapPreparedContent Published(IReadOnlyList<RundownPack> packs)
    {
        var published = packs.Select(pack => PublishedResources.PackageRoot(pack.Path)).ToHashSet(StringComparer.Ordinal);
        var candidates = Packs.Select(pack => PublishedResources.PackageRoot(pack.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var pack in Packs.Where(pack => !published.Contains(PublishedResources.PackageRoot(pack.Path))))
        {
            const string code = "rundown-publication-rejected";
            const string detail = "The native Rundown transaction did not publish this package; its prepared Map data was discarded.";
            // The stage carries the refusal like every other one, so the reload result lists it; the prepared parts are already built.
            _stages.FirstOrDefault(stage => stage.Root == PublishedResources.PackageRoot(pack.Path))
                ?.Reject(new(pack.Package, "rundown.forge", pack.Path, code, detail));
            MapLog.PackageRejected(pack.Package, "rundown.forge", pack.Path, code, detail, pack.MenuSlot);
        }
        return new(_parts.Where(part => !candidates.Contains(part.Root) || published.Contains(part.Root))
            .Select(part => part.Content).ToArray());
    }
}

/// <summary>One immutable row selection. Constructed only from fully prepared package stages, never by rereading
/// disk. Selecting a smaller successfully published set cannot introduce a new reference or claim conflict.</summary>
internal sealed class MapPreparedContent
{
    internal IReadOnlyList<Generation.GenerationProgram> Generations { get; }
    internal IReadOnlyList<MapFieldEntry> Fields { get; }
    internal IReadOnlyList<InteriorDoorRow> InteriorDoors { get; }
    internal IReadOnlyDictionary<string, RoomBuildRecipe> Recipes { get; }
    internal IReadOnlyList<FixedItem> Items { get; }
    internal IReadOnlyList<FixedEnemy> Enemies { get; }
    internal IReadOnlyList<FixedSack> Sacks { get; }
    internal IReadOnlyList<FixedLanding> Landings { get; }
    internal IReadOnlyList<FixedBulkheadController> Controllers { get; }
    internal IReadOnlyList<FixedBulkheadKey> Keys { get; }
    internal IReadOnlyList<TriggerZone> TriggerZones { get; }
    internal IReadOnlyList<InteractionPoint> InteractionPoints { get; }
    internal IReadOnlyList<PlacedModel> PlacedModels { get; }
    internal IReadOnlyList<MapModelAssembly> ModelAssemblies { get; }
    internal IReadOnlyList<CarryItemAssembly> CarryAssemblies { get; }
    internal IReadOnlyList<WaveAnchor> WaveAnchors { get; }
    internal IReadOnlyList<ScanCoverageRow> ScanCoverage { get; }
    internal IReadOnlyList<ScanPlacement> ScanPlacements { get; }
    internal IReadOnlyList<ExtractionPoint> ExtractionPoints { get; }
    internal IReadOnlyList<EntryLanding> EntryLandings { get; }
    internal IReadOnlyList<DoorLockRow> DoorLocks { get; }
    internal IReadOnlyList<ObjectiveRoundsRow> ObjectiveRounds { get; }
    internal IReadOnlyList<GlobalLightLevel> GlobalLight { get; }
    internal IReadOnlyList<GeneratorSourceRow> GeneratorSources { get; }
    internal WaveResourceRegistry Waves { get; }
    internal TeleportAreaTable Areas { get; }
    internal PlacedImageTable Images { get; }
    private IReadOnlyList<WaveResourceDocument> WaveDocuments { get; }
    private IReadOnlyList<TeleportAreaTable> AreaTables { get; }
    internal DoorBuildPlan DoorFields { get; }
    internal GlobalWavePlan GlobalFields { get; }
    internal SystemObjectPlan SystemFields { get; }
    internal MapPreparedContent(IReadOnlyList<MapPackageStage> stages, IReadOnlyList<MapModelAssembly>? models = null,
        IReadOnlyList<CarryItemAssembly>? carried = null)
    {
        ModelAssemblies = models ?? Array.Empty<MapModelAssembly>();
        CarryAssemblies = carried ?? Array.Empty<CarryItemAssembly>();
        IReadOnlyList<T> Rows<T>(Func<MapPackageStage, IEnumerable<T>> select) => Array.AsReadOnly(stages.SelectMany(select).ToArray());
        Generations = Rows(stage => stage.Generation == null || stage.Generation.Plan.Levels.GetArrayLength() == 0 ? Array.Empty<Generation.GenerationProgram>() : new[] { stage.Generation });
        var generatedLevels = Generations.SelectMany(program => program.Plan.Levels.EnumerateArray())
            .Select(row => Generation.GenerationProgram.Text(row, "reference")).ToHashSet(StringComparer.Ordinal);
        Fields = Rows(stage => stage.Fields);
        InteriorDoors = Rows(stage => stage.InteriorDoors);
        Recipes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, RoomBuildRecipe>(stages.SelectMany(stage => stage.Recipes)
            .ToDictionary(row => row.ResourceKey, StringComparer.Ordinal));
        Items = Rows(stage => stage.Items); Enemies = Rows(stage => stage.Enemies); Sacks = Rows(stage => stage.Sacks);
        Landings = Rows(stage => stage.Landings); Controllers = Rows(stage => stage.Controllers); Keys = Rows(stage => stage.Keys);
        TriggerZones = Rows(stage => stage.TriggerZones); InteractionPoints = Rows(stage => stage.InteractionPoints);
        PlacedModels = Rows(stage => stage.PlacedModels);
        WaveAnchors = Rows(stage => stage.WaveAnchors); ScanCoverage = Rows(stage => stage.ScanCoverage); ScanPlacements = Rows(stage => stage.ScanPlacements);
        ExtractionPoints = Rows(stage => stage.ExtractionPoints); EntryLandings = Rows(stage => stage.EntryLandings);
        DoorLocks = Rows(stage => stage.DoorLocks); ObjectiveRounds = Rows(stage => stage.ObjectiveRounds); GeneratorSources = Rows(stage => stage.GeneratorSources);
        GlobalLight = Rows(stage => stage.GlobalLight);
        void Impossible(string detail) => throw new InvalidOperationException("Prepared Map content invariant failed: " + detail);
        DoorFields = DoorBuildPlan.From(Fields);
        GlobalFields = GlobalWavePlan.From(Fields, diagnostic => Impossible(diagnostic.ToString()));
        SystemFields = SystemObjectPlan.From(Fields, diagnostic => Impossible(diagnostic.ToString()));
        WaveDocuments = Rows(stage => stage.Waves);
        AreaTables = Rows(stage => stage.AreaTables);
        Waves = new WaveResourceRegistry();
        foreach (var document in WaveDocuments) Waves.AddPrepared(document);
        var seenAreas = new HashSet<string>(StringComparer.Ordinal);
        Areas = TeleportAreaTable.CombinePrepared(AreaTables
            .Where(table => table.Ids.Any() && seenAreas.Add(table.Ids.Single())));
        Images = PlacedImageTable.FromPrepared(stages.SelectMany(stage => stage.Images));
    }
    /// <summary>Selection of already prepared parts. No schema parsing, field conversion, content resolution or
    /// rejection callback is reachable after native publication.</summary>
    internal MapPreparedContent(IReadOnlyList<MapPreparedContent> parts)
    {
        Generations = Array.AsReadOnly(parts.SelectMany(part => part.Generations).ToArray());
        ModelAssemblies = Array.AsReadOnly(parts.SelectMany(part => part.ModelAssemblies).ToArray());
        CarryAssemblies = Array.AsReadOnly(parts.SelectMany(part => part.CarryAssemblies).ToArray());
        IReadOnlyList<T> Rows<T>(Func<MapPreparedContent, IEnumerable<T>> select) => Array.AsReadOnly(parts.SelectMany(select).ToArray());
        Fields = Rows(part => part.Fields);
        InteriorDoors = Rows(part => part.InteriorDoors);
        Recipes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, RoomBuildRecipe>(parts.SelectMany(part => part.Recipes)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        Items = Rows(part => part.Items); Enemies = Rows(part => part.Enemies); Sacks = Rows(part => part.Sacks);
        Landings = Rows(part => part.Landings); Controllers = Rows(part => part.Controllers); Keys = Rows(part => part.Keys);
        TriggerZones = Rows(part => part.TriggerZones); InteractionPoints = Rows(part => part.InteractionPoints);
        PlacedModels = Rows(part => part.PlacedModels);
        WaveAnchors = Rows(part => part.WaveAnchors); ScanCoverage = Rows(part => part.ScanCoverage); ScanPlacements = Rows(part => part.ScanPlacements);
        ExtractionPoints = Rows(part => part.ExtractionPoints); EntryLandings = Rows(part => part.EntryLandings);
        DoorLocks = Rows(part => part.DoorLocks); ObjectiveRounds = Rows(part => part.ObjectiveRounds); GeneratorSources = Rows(part => part.GeneratorSources);
        GlobalLight = Rows(part => part.GlobalLight);
        DoorFields = DoorBuildPlan.CombinePrepared(parts.Select(part => part.DoorFields));
        GlobalFields = GlobalWavePlan.CombinePrepared(parts.Select(part => part.GlobalFields));
        SystemFields = SystemObjectPlan.CombinePrepared(parts.Select(part => part.SystemFields));
        WaveDocuments = Rows(part => part.WaveDocuments); AreaTables = Rows(part => part.AreaTables);
        Waves = new WaveResourceRegistry();
        foreach (var document in WaveDocuments) Waves.AddPrepared(document);
        var seenAreas = new HashSet<string>(StringComparer.Ordinal);
        Areas = TeleportAreaTable.CombinePrepared(AreaTables.Where(table => table.Ids.Any() && seenAreas.Add(table.Ids.Single())));
        Images = PlacedImageTable.FromPrepared(parts.SelectMany(part => part.Images.All()));
    }

}
