using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

/// <summary>
/// The fixed items, fixed enemies, fixed sacks, dimension landings, bulkhead controllers and bulkhead keys this
/// install authored,
/// read once after content admission from the package documents that declare them: `plugins/&lt;package&gt;/forge/fixed-items.forge`,
/// `fixed-enemies.forge`, `fixed-sacks.forge`, `dimension-landings.forge`, `bulkhead-controllers.forge` and
/// `bulkhead-keys.forge`, found by the
/// same one-level package scan every authored content file of this module uses.
///
/// Every document is the author's own placement, so they are read strictly and a row that does not read is
/// refused with its own diagnostic and left out — the rows around it are kept, because the placements of one level
/// are independent of each other. A row id claimed twice is refused the second time rather than applied twice:
/// the id is what a diagnostic and a placement seed name, so two rows sharing one would place an object nobody
/// wrote.
///
/// The snapshot is immutable once loaded, like the trigger-zone table and for the same reason: two machines
/// disagreeing about what stands where would be a different world on each of them.
/// </summary>
internal static class FixedPlacementData
{
    private const string ItemCodePrefix = "fixed-item";
    private const string EnemyCodePrefix = "fixed-enemy";
    private const string SackCodePrefix = "fixed-sack";
    private const string LandingCodePrefix = "dimension-landing";
    private const string BulkheadControllerCodePrefix = "fixed-controller";
    private const string BulkheadKeyCodePrefix = "fixed-bulkhead-key";
    private static readonly PublishedTextCodes ItemReadCodes = new("fixed-item-path", "fixed-item-unreadable", "fixed-item-file-size", "fixed-item-json", "fixed-item-utf8");
    private static readonly PublishedTextCodes EnemyReadCodes = new("fixed-enemy-path", "fixed-enemy-unreadable", "fixed-enemy-file-size", "fixed-enemy-json", "fixed-enemy-utf8");
    private static readonly PublishedTextCodes SackReadCodes = new("fixed-sack-path", "fixed-sack-unreadable", "fixed-sack-file-size", "fixed-sack-json", "fixed-sack-utf8");
    private static readonly PublishedTextCodes LandingReadCodes = new("dimension-landing-path", "dimension-landing-unreadable", "dimension-landing-file-size", "dimension-landing-json", "dimension-landing-utf8");
    private static readonly PublishedTextCodes BulkheadControllerReadCodes = new("fixed-controller-path", "fixed-controller-unreadable", "fixed-controller-file-size", "fixed-controller-json", "fixed-controller-utf8");
    private static readonly PublishedTextCodes BulkheadKeyReadCodes = new("fixed-bulkhead-key-path", "fixed-bulkhead-key-unreadable", "fixed-bulkhead-key-file-size", "fixed-bulkhead-key-json", "fixed-bulkhead-key-utf8");

    /// <summary>The parser of one document kind (<see cref="FixedPlacementManifest"/>).</summary>
    private delegate bool Parser<T>(string text, out FixedDocument<T>? document, out string? code, out string? reason) where T : FixedRow;

    /// <summary>A rule a row must also pass, with the document it came from; false leaves the row out (the rule has
    /// already reported why).</summary>
    private delegate bool RowRule<T>(PublishedResource document, T row);

    /// <summary>Every fixed item every package of this install declares, in ordinal document order and, inside one
    /// document, in the order the author wrote them.</summary>
    internal static IReadOnlyList<FixedItem> LoadItems(string bepInExRoot, RundownPlacementAdmission admission, Action<string> rejected)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        return Load<FixedItem>(bepInExRoot, admission, rejected, FixedPlacementManifest.ItemFileNameOnly, ItemCodePrefix, ItemReadCodes,
            FixedPlacementManifest.TryParseItems, "fixed-item-duplicate-id", "Item",
            beforeClaim: (document, item) => item.ItemId is not { } itemId || admission.AllowsItem(document, itemId, rejected),
            afterClaim: (document, item) =>
            {
                if (item.TaskObject is not { } role || roles.Add(item.Level + "/" + role.Key(item.Locator))) return true;
                rejected(PublishedResources.Diagnostic(document.Relative, "fixed-object-role-duplicate", "This native task object already has a point in another document."));
                return false;
            });
    }

    /// <summary>Every fixed enemy every package of this install declares, with the same document order and the
    /// same duplicate rule as the items.</summary>
    internal static IReadOnlyList<FixedEnemy> LoadEnemies(string bepInExRoot, RundownPlacementAdmission admission, Action<string> rejected)
        => Load<FixedEnemy>(bepInExRoot, admission, rejected, FixedPlacementManifest.EnemyFileNameOnly, EnemyCodePrefix, EnemyReadCodes,
            FixedPlacementManifest.TryParseEnemies, "fixed-enemy-duplicate-id", "Enemy");

    /// <summary>Every fixed sack every package of this install declares, with the same document order and the
    /// same duplicate rule as the items.</summary>
    internal static IReadOnlyList<FixedSack> LoadSacks(string bepInExRoot, RundownPlacementAdmission admission, Action<string> rejected)
        => Load<FixedSack>(bepInExRoot, admission, rejected, FixedPlacementManifest.SackFileNameOnly, SackCodePrefix, SackReadCodes,
            FixedPlacementManifest.TryParseSacks, "fixed-sack-duplicate-id", "Sack");

    /// <summary>Every dimension landing every package of this install declares, with the same document order and
    /// the same duplicate rule as the items. The order across documents is the discovery order, so the rows of
    /// one dimension keep the author's slot order within each document.</summary>
    internal static IReadOnlyList<FixedLanding> LoadLandings(string bepInExRoot, RundownPlacementAdmission admission, Action<string> rejected)
        => Load<FixedLanding>(bepInExRoot, admission, rejected, FixedPlacementManifest.LandingFileNameOnly, LandingCodePrefix, LandingReadCodes,
            FixedPlacementManifest.TryParseLandings, "dimension-landing-duplicate-id", "Landing");

    /// <summary>
    /// Every bulkhead controller every package of this install declares, with the same document order and the same
    /// duplicate rule as the items.
    ///
    /// The zone is the row's identity inside its level, so the same rule the document reader applies inside one
    /// file is applied across files here: a zone a level already has a row for is refused rather than loaded,
    /// because where that zone's one controller stands has exactly one answer.
    /// </summary>
    internal static IReadOnlyList<FixedBulkheadController> LoadBulkheadControllers(string bepInExRoot, RundownPlacementAdmission admission,
        Action<string> rejected)
    {
        var zones = new HashSet<string>(StringComparer.Ordinal);
        return Load<FixedBulkheadController>(bepInExRoot, admission, rejected, FixedPlacementManifest.BulkheadControllerFileNameOnly,
            BulkheadControllerCodePrefix, BulkheadControllerReadCodes, FixedPlacementManifest.TryParseBulkheadControllers,
            "fixed-controller-duplicate-id", "Controller",
            afterClaim: (document, controller) =>
            {
                if (zones.Add(controller.Level + "/" + controller.ZoneText)) return true;
                rejected(PublishedResources.Diagnostic(document.Relative, "fixed-controller-duplicate-zone",
                    "Zone `" + controller.ZoneText + "` of level " + controller.Level
                        + " is claimed by more than one document; the later row is left out."));
                return false;
            });
    }

    /// <summary>
    /// Every bulkhead key every package of this install declares, with the same document order and the same
    /// duplicate rule as the items. The order is also the order the rows claim their keys in.
    ///
    /// No second rule joins the duplicate id here, because several keys of one layer of one level are ordinary: the
    /// rows of a layer take that layer's keys one after another, so two rows naming one layer are two real rows.
    /// </summary>
    internal static IReadOnlyList<FixedBulkheadKey> LoadBulkheadKeys(string bepInExRoot, RundownPlacementAdmission admission,
        Action<string> rejected)
        => Load<FixedBulkheadKey>(bepInExRoot, admission, rejected, FixedPlacementManifest.BulkheadKeyFileNameOnly, BulkheadKeyCodePrefix,
            BulkheadKeyReadCodes, FixedPlacementManifest.TryParseBulkheadKeys, "fixed-bulkhead-key-duplicate-id", "Bulkhead key");

    /// <summary>One kind's rows across every package document: a document the admission refuses, that does not read or
    /// that does not parse is reported and skipped; a row the document refused is reported; a row of a level the
    /// admission refuses, that <paramref name="beforeClaim"/> refuses, whose id an earlier document already claimed or
    /// that <paramref name="afterClaim"/> refuses is left out.</summary>
    private static IReadOnlyList<T> Load<T>(string bepInExRoot, RundownPlacementAdmission admission, Action<string> rejected,
        string fileName, string codePrefix, PublishedTextCodes readCodes, Parser<T> parse, string duplicateCode, string label,
        RowRule<T>? beforeClaim = null, RowRule<T>? afterClaim = null) where T : FixedRow
    {
        ArgumentNullException.ThrowIfNull(bepInExRoot);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(rejected);
        var loaded = new List<T>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in ContentPackages.Read(bepInExRoot, ContentPackages.EngineReader, "forge", fileName, readCodes, rejected))
        {
            if (!admission.AllowsDocument(document, codePrefix, rejected)) continue;
            if (!PublishedResources.TryRead(document, PublishedResources.MaximumFileBytes, readCodes, rejected, out var text))
                continue;
            if (!parse(text!, out var parsed, out var code, out var reason))
            {
                rejected(PublishedResources.Diagnostic(document.Relative, code!, reason!));
                continue;
            }
            foreach (var refusal in parsed!.Refusals)
                rejected(PublishedResources.Diagnostic(document.Relative, refusal.Code, refusal.Reason));
            foreach (var row in parsed.Rows)
            {
                if (!admission.AllowsLevel(document, row.Level, codePrefix, rejected)) continue;
                if (beforeClaim != null && !beforeClaim(document, row)) continue;
                if (!claimed.Add(row.Id))
                {
                    rejected(PublishedResources.Diagnostic(document.Relative, duplicateCode,
                        label + " id `" + row.Id + "` is claimed by more than one document; the later row is left out."));
                    continue;
                }
                if (afterClaim != null && !afterClaim(document, row)) continue;
                loaded.Add(row);
            }
        }
        return Array.AsReadOnly(loaded.ToArray());
    }
}
