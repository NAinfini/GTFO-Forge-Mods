using ForgeCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ForgeMap.Fields;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>All Map data of one package, parsed in private before any of its rows can reach a native owner.
/// A stage with any refusal is discarded whole.</summary>
internal sealed class MapPackageStage
{
    internal string Root { get; }
    internal IReadOnlyList<MapPackageDocument> Documents { get; }
    internal List<MapFieldEntry> Fields { get; } = new();
    internal List<InteriorDoorRow> InteriorDoors { get; } = new();
    internal List<RoomBuildRecipe> Recipes { get; } = new();
    /// <summary>The package's <c>room-build.forge</c> text; admission reads it with the room files it finds by hash (I-RESOLVE).</summary>
    internal string? RoomBuild { get; private set; }
    /// <summary>The package's <c>room-furnishings.forge</c> text; admission reads it with the furnishing files it finds by hash (I-RESOLVE).</summary>
    internal string? Furnishings { get; private set; }
    internal List<FixedItem> Items { get; } = new();
    internal List<FixedEnemy> Enemies { get; } = new();
    internal List<FixedSack> Sacks { get; } = new();
    internal List<FixedLanding> Landings { get; } = new();
    internal List<FixedBulkheadController> Controllers { get; } = new();
    internal List<FixedBulkheadKey> Keys { get; } = new();
    internal List<TriggerZone> TriggerZones { get; } = new();
    internal List<InteractionPoint> InteractionPoints { get; } = new();
    internal List<PlacedModel> PlacedModels { get; } = new();
    internal List<CarryItem> CarryItems { get; } = new();
    internal List<WaveAnchor> WaveAnchors { get; } = new();
    internal List<WaveResourceDocument> Waves { get; } = new();
    internal List<ScanCoverageRow> ScanCoverage { get; } = new();
    internal List<ScanPlacement> ScanPlacements { get; } = new();
    internal List<ExtractionPoint> ExtractionPoints { get; } = new();
    internal List<EntryLanding> EntryLandings { get; } = new();
    internal List<DoorLockRow> DoorLocks { get; } = new();
    internal List<ObjectiveRoundsRow> ObjectiveRounds { get; } = new();
    internal List<GlobalLightLevel> GlobalLight { get; } = new();
    internal List<GeneratorSourceRow> GeneratorSources { get; } = new();
    internal List<PlacedImageResource> Images { get; } = new();
    internal List<MapPackageDocument> Areas { get; } = new();
    internal List<TeleportAreaTable> AreaTables { get; } = new();
    internal IReadOnlyList<MapPackageRejection> Rejections { get; private set; } = Array.Empty<MapPackageRejection>();
    private bool _slotRefused;
    internal bool IsRejected => _slotRefused || Rejections.Count != 0;
    internal Generation.GenerationProgram? Generation { get; set; }
    internal Generation.MapGenerationPlan? GenerationPlan { get; private set; }

    // SelectSlots already emitted the owning A4 refusal. Drop every staged row without a second owner error.
    internal void RefuseSlot() { _slotRefused = true; Withdraw(); }

    private MapPackageStage(MapPackageDocuments source)
    {
        Root = source.Root; Documents = source.Documents;
    }

    internal static MapPackageStage Parse(MapPackageDocuments source)
    {
        var stage = new MapPackageStage(source);
        var failures = new List<MapPackageRejection>(source.Rejections);
        foreach (var document in source.Documents)
        {
            try { stage.Read(document); }
            catch (Exception error) when (error is MapPackageDocumentException or JsonException or FormatException
                or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException or InvalidDataException)
            {
                failures.Add(new(Path.GetFileName(source.Root), document.Name, document.Resource.Relative,
                    error is MapPackageDocumentException refusal ? refusal.Code : "document-invalid", error.Message)
                {
                    ScanCoverage = (error as MapPackageDocumentException)?.ScanCoverage ?? Array.Empty<ScanCoverageRefusal>(),
                    AuthorId = (error as MapPackageDocumentException)?.AuthorId ?? ""
                });
            }
        }
        if (failures.Count == 0)
        {
            try
            {
                // Semantic field validation and duplicate checks are preparation, never installation.
                var merged = MapFieldDocuments.Merge(stage.Fields, FieldRefused);
                GlobalWavePlan.From(merged, FieldRefused);
                SystemObjectPlan.From(merged, FieldRefused);
                if (stage.InteriorDoors.GroupBy(row => row.Level + "/" + row.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw new MapPackageDocumentException("interior-door-duplicate", "Repeated interior door identity.",
                        source.Root + "/forge/" + InteriorDoorDocument.FileName);
                stage.Fields.Clear(); stage.Fields.AddRange(merged);
            }
            catch (MapPackageDocumentException error)
            {
                string path = error.Path ?? source.Root + "/forge/map-fields";
                failures.Add(new(Path.GetFileName(source.Root), Path.GetFileName(path), path, error.Code, error.Message));
            }
        }
        if (failures.Count != 0) stage.Withdraw();
        stage.Rejections = Array.AsReadOnly(failures.ToArray());
        return stage;
    }

    internal void Reject(MapPackageRejection rejection)
    {
        Withdraw();
        Rejections = Array.AsReadOnly(Rejections.Append(rejection).ToArray());
    }

    private void Withdraw()
    {
        Fields.Clear(); Recipes.Clear(); RoomBuild = null; Furnishings = null;
        InteriorDoors.Clear();
        Items.Clear(); Enemies.Clear(); Sacks.Clear(); Landings.Clear(); Controllers.Clear(); Keys.Clear();
        TriggerZones.Clear(); InteractionPoints.Clear(); PlacedModels.Clear(); CarryItems.Clear(); WaveAnchors.Clear(); Waves.Clear();
        ScanCoverage.Clear(); ScanPlacements.Clear(); ExtractionPoints.Clear(); EntryLandings.Clear(); DoorLocks.Clear(); ObjectiveRounds.Clear(); GlobalLight.Clear(); GeneratorSources.Clear();
        Images.Clear(); Areas.Clear(); AreaTables.Clear();
        Generation = null; GenerationPlan = null;
    }

    private static void FieldRefused(MapDiagnostic diagnostic)
        => throw new MapPackageDocumentException(diagnostic.Code, diagnostic.Detail, diagnostic.File);
    private static void TextRefused(string reason) => throw new MapPackageDocumentException("document-invalid", reason);
    private static void Require(bool success, string? code, string? reason)
    { if (!success) throw new MapPackageDocumentException(code ?? "document-invalid", reason ?? "The document was refused."); }
    private static void Refusals(IEnumerable<FixedPlacementRefusal> refusals)
    { foreach (var refusal in refusals) throw new MapPackageDocumentException(refusal.Code, refusal.Reason, authorId: refusal.AuthorId); }

    private void Read(MapPackageDocument document)
    {
        string text = document.Text, path = document.Resource.Relative;
        if (path.StartsWith(Root + "/forge/map-fields/", StringComparison.Ordinal))
        { Fields.AddRange(MapFieldDocuments.Read(path, text, FieldRefused)); return; }
        if (path.StartsWith(Root + "/forge/resources/", StringComparison.Ordinal))
        {
            Require(PublishedResources.TryResourceKind(text, "gtfo-forge-engine-resource", "document-invalid", "document-invalid",
                out var kind, out var code, out var reason), code, reason);
            if (kind == "area_field")
            {
                AreaTables.Add(TeleportAreaTable.ReadDocuments(new[] { (path, text) }, TextRefused));
                Areas.Add(document);
            }
            return;
        }
        switch (document.Name)
        {
            case "map-generation.forge":
                try { GenerationPlan = ForgeMap.Generation.MapGenerationPlanReader.Read(System.Text.Encoding.UTF8.GetBytes(text)); }
                catch (ForgeMap.Generation.GenerationRefusal error) { throw new MapPackageDocumentException(error.Code, error.Path, path); }
                break;
            case PlacedModelManifest.ComposedFileNameOnly:
                Require(PlacedModelManifest.TryParseComposed(text, out var composed, out var composedCode, out var composedReason), composedCode, composedReason);
                PlacedModels.AddRange(composed!); break;
            case CarryItemManifest.FileNameOnly:
                Require(CarryItemManifest.TryParse(text, out var carryItems, out var carryCode, out var carryReason), carryCode, carryReason);
                CarryItems.AddRange(carryItems!); break;
            case InteriorDoorDocument.FileName: InteriorDoors.AddRange(InteriorDoorDocument.Parse(text)); break;
            case "room-build.forge": RoomBuild = text; break;
            case RoomFurnishing.FileNameOnly: Furnishings = text; break;
            case "fixed-items.forge":
                Require(FixedPlacementManifest.TryParseItems(text, out var items, out var ic, out var ir), ic, ir);
                Refusals(items!.Refusals); Items.AddRange(items.Rows); break;
            case "fixed-enemies.forge":
                Require(FixedPlacementManifest.TryParseEnemies(text, out var enemies, out var ec, out var er), ec, er);
                Refusals(enemies!.Refusals); Enemies.AddRange(enemies.Rows); break;
            case "fixed-sacks.forge":
                Require(FixedPlacementManifest.TryParseSacks(text, out var sacks, out var sc, out var sr), sc, sr);
                Refusals(sacks!.Refusals); Sacks.AddRange(sacks.Rows); break;
            case "dimension-landings.forge":
                Require(FixedPlacementManifest.TryParseLandings(text, out var landings, out var lc, out var lr), lc, lr);
                Refusals(landings!.Refusals); Landings.AddRange(landings.Rows); break;
            case "bulkhead-controllers.forge":
                Require(FixedPlacementManifest.TryParseBulkheadControllers(text, out var controllers, out var cc, out var cr), cc, cr);
                Refusals(controllers!.Refusals); Controllers.AddRange(controllers.Rows); break;
            case "bulkhead-keys.forge":
                Require(FixedPlacementManifest.TryParseBulkheadKeys(text, out var keys, out var kc, out var kr), kc, kr);
                Refusals(keys!.Refusals); Keys.AddRange(keys.Rows); break;
            case "trigger-zones.forge":
                Require(TriggerZoneManifest.TryParse(text, out var zones, out var zc, out var zr), zc, zr);
                TriggerZones.AddRange(zones!); break;
            case "interaction-points.forge":
                Require(InteractionPointManifest.TryParse(text, out var points, out var pc, out var pr), pc, pr);
                InteractionPoints.AddRange(points!); break;
            case PlacedModelManifest.FileNameOnly:
                Require(PlacedModelManifest.TryParse(text, out var models, out var mc, out var mr), mc, mr);
                PlacedModels.AddRange(models!); break;
            case "wave-anchors.forge":
                Require(WaveAnchorManifest.TryParse(text, out var anchors, out var ac, out var ar), ac, ar);
                Refusals(anchors!.Refusals); WaveAnchors.AddRange(anchors.Anchors); break;
            case "wave-resources.forge": Waves.Add(WaveResourceDocuments.Read(text)); break;
            case "scan-coverage.forge":
                Require(ScanCoverageManifest.TryParse(text, out var scans, out var xc, out var xr), xc, xr);
                if (scans!.Refusals.Count != 0) throw new MapPackageDocumentException(scans.Refusals[0].Code, scans.Refusals[0].Reason, scanCoverage: scans.Refusals);
                ScanCoverage.AddRange(scans.Rows); break;
            case "scan-placements.forge":
                Require(ScanPlacementManifest.TryParse(text, out var placements, out var reason), "scan-placement-document", reason);
                ScanPlacements.AddRange(placements!); break;
            case ExtractionPointManifest.FileNameOnly:
                Require(ExtractionPointManifest.TryParse(text, out var extraction, out var extractionReason), "extraction-point-document", extractionReason);
                ExtractionPoints.AddRange(extraction!); break;
            case EntryLandingManifest.FileNameOnly:
                Require(EntryLandingManifest.TryParse(text, out var entryLandings, out var entryLandingReason), "entry-landing-document", entryLandingReason);
                EntryLandings.AddRange(entryLandings!); break;

            case DoorLocksDocument.FileName: DoorLocks.AddRange(DoorLocksDocument.Parse(text)); break;
            case ObjectiveRoundsDocument.FileName: ObjectiveRounds.AddRange(ObjectiveRoundsDocument.Parse(text)); break;
            case GlobalLightDocument.FileName: GlobalLight.AddRange(GlobalLightDocument.Parse(text)); break;
            case GeneratorSourcesDocument.FileName: GeneratorSources.AddRange(GeneratorSourcesDocument.Parse(text)); break;
            default: throw new InvalidOperationException("Map document has no owning parser: " + document.Name);
        }
    }
}

internal sealed class MapPackageDocumentException : Exception
{
    internal string Code { get; }
    internal string? Path { get; }
    internal IReadOnlyList<ScanCoverageRefusal> ScanCoverage { get; }
    /// <summary>The author's id of the refused row, empty when the refusal is about a whole document.</summary>
    internal string AuthorId { get; }
    internal MapPackageDocumentException(string code, string detail, string? path = null, IReadOnlyList<ScanCoverageRefusal>? scanCoverage = null,
        string authorId = "") : base(detail)
    { Code = code; Path = path; ScanCoverage = scanCoverage ?? Array.Empty<ScanCoverageRefusal>(); AuthorId = authorId; }
}
