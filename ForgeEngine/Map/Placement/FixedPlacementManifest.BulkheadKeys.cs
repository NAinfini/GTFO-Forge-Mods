using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

// Placement document parsing for BulkheadKeys. Shared envelope rules remain in FixedPlacementManifest.cs.
internal static partial class FixedPlacementManifest
{
    private const string BulkheadKeyFileName = "bulkhead-keys.forge";
    /// <summary>The bulkhead-key document's one file name.</summary>
    public static string BulkheadKeyFileNameOnly => BulkheadKeyFileName;

    /// <summary>
    /// Reads one bulkhead-key document. Its rows are refused one by one like every other document of this family,
    /// and the one rule that is not a single row's own is deliberately absent here: several keys of one layer are
    /// ordinary, so rows naming the same layer of the same level are kept and claim that layer's keys one after
    /// another in the document's own order. Whether the level really carries a key of a row's layer, and whether an
    /// earlier row already claimed it, is not a document question and is answered at placement, where a row with no
    /// open key of its layer is refused rather than matched to one the author did not name.
    /// </summary>
    public static bool TryParseBulkheadKeys(string text, out FixedDocument<FixedBulkheadKey>? document,
        out string? code, out string? reason)
    {
        document = null;
        using var parsed = Read(text, BulkheadKeyRootCodes, "keys", out code, out reason);
        if (parsed == null) return false;
        var keys = new List<FixedBulkheadKey>();
        var refusals = new List<FixedPlacementRefusal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in parsed.RootElement.GetProperty("keys").EnumerateArray())
        {
            index++;
            if (!TryBulkheadKey(row, out var key, out var rowCode, out var rowReason))
            {
                refusals.Add(new FixedPlacementRefusal(rowCode!, Row(index, rowReason!), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (!seen.Add(key!.Id))
            {
                refusals.Add(new FixedPlacementRefusal("fixed-bulkhead-key-duplicate",
                    Row(index, "Bulkhead key id `" + key.Id + "` is claimed more than once; the later row is left out."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            keys.Add(key);
        }
        document = new FixedDocument<FixedBulkheadKey>(Array.AsReadOnly(keys.ToArray()),
            Array.AsReadOnly(refusals.ToArray()));
        return true;
    }

    private static bool TryBulkheadKey(JsonElement row, out FixedBulkheadKey? key,
        out string? code, out string? reason)
    {
        key = null;
        code = "fixed-bulkhead-key-row";
        reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "A bulkhead key row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "locator", "p_u", "yaw_u", "layer"))
        { code = "fixed-bulkhead-key-field"; reason = "Unknown bulkhead key field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "id", FixedPlacement.MaximumIdChars, out var id) || !Prefixed(id!, "bulkhead-key:"))
        { code = "fixed-bulkhead-key-id"; reason = "A bulkhead key needs an id of at most " + FixedPlacement.MaximumIdChars + " characters, written `bulkhead-key:<id>`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "fixed-bulkhead-key-level"; reason = "A bulkhead key needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!Locator(row, out var locator, out reason)) { code = "fixed-bulkhead-key-room"; return false; }
        if (!DocumentReading.Numbers(row, "p_u", 3, -FixedPlacement.MaximumCoordinate, FixedPlacement.MaximumCoordinate, out var position))
        { code = "fixed-bulkhead-key-position"; reason = "A position carries three finite metre coordinates in the room's prefab-root frame."; return false; }
        if (!DocumentReading.Number(row, "yaw_u", -FixedPlacement.MaximumYawDegrees, FixedPlacement.MaximumYawDegrees, out var yaw))
        { code = "fixed-bulkhead-key-yaw"; reason = "A yaw is a finite angle in degrees about +Y."; return false; }
        if (!DocumentReading.Text(row, "layer", 16, out var layerText) || !FixedPlacement.TryKeyLayer(layerText!, out var layer))
        { code = "fixed-bulkhead-key-layer"; reason = "A layer is `main`, `secondary` or `third`."; return false; }
        key = new FixedBulkheadKey(id!, level, locator!, position!, yaw, layer);
        return true;
    }
}
