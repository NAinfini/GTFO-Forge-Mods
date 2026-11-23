using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

// Placement document parsing for BulkheadControllers. Shared envelope rules remain in FixedPlacementManifest.cs.
internal static partial class FixedPlacementManifest
{
    private const string BulkheadControllerFileName = "bulkhead-controllers.forge";
    /// <summary>The bulkhead-controller document's one file name.</summary>
    public static string BulkheadControllerFileNameOnly => BulkheadControllerFileName;

    /// <summary>
    /// Reads one bulkhead-controller document. Its rows are refused one by one like every other document of this
    /// family, and the one rule that is not a single row's own is the zone: a zone carries one controller, so a
    /// second row naming the same zone of the same level is refused by name here rather than read as a second answer
    /// to where that controller stands. Whether the level really generated a controller in a row's zone is not a
    /// document question and is answered at placement.
    /// </summary>
    public static bool TryParseBulkheadControllers(string text, out FixedDocument<FixedBulkheadController>? document,
        out string? code, out string? reason)
    {
        document = null;
        using var parsed = Read(text, ControllerRootCodes, "controllers", out code, out reason);
        if (parsed == null) return false;
        var controllers = new List<FixedBulkheadController>();
        var refusals = new List<FixedPlacementRefusal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var zones = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in parsed.RootElement.GetProperty("controllers").EnumerateArray())
        {
            index++;
            if (!TryBulkheadController(row, out var controller, out var rowCode, out var rowReason))
            {
                refusals.Add(new FixedPlacementRefusal(rowCode!, Row(index, rowReason!), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (!seen.Add(controller!.Id))
            {
                refusals.Add(new FixedPlacementRefusal("fixed-controller-duplicate",
                    Row(index, "Controller id `" + controller.Id + "` is claimed more than once; the later row is left out."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            // The zone is the row's identity inside its level: a zone has one controller, so two rows naming the
            // same zone of the same level would be two answers to where that controller stands.
            if (!zones.Add(controller.Level + "/" + controller.ZoneText))
            {
                refusals.Add(new FixedPlacementRefusal("fixed-controller-duplicate-zone",
                    Row(index, "Zone `" + controller.ZoneText + "` of level " + controller.Level
                        + " is claimed by an earlier row; the later row is left out."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            controllers.Add(controller);
        }
        document = new FixedDocument<FixedBulkheadController>(Array.AsReadOnly(controllers.ToArray()),
            Array.AsReadOnly(refusals.ToArray()));
        return true;
    }

    private static bool TryBulkheadController(JsonElement row, out FixedBulkheadController? controller,
        out string? code, out string? reason)
    {
        controller = null;
        code = "fixed-controller-row";
        reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "A bulkhead controller row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "locator", "p_u", "yaw_u"))
        { code = "fixed-controller-field"; reason = "Unknown bulkhead controller field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "id", FixedPlacement.MaximumIdChars, out var id) || !Prefixed(id!, "controller:"))
        { code = "fixed-controller-id"; reason = "A bulkhead controller needs an id of at most " + FixedPlacement.MaximumIdChars + " characters, written `controller:<id>`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "fixed-controller-level"; reason = "A bulkhead controller needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!Locator(row, out var locator, out reason)) { code = "fixed-controller-room"; return false; }
        if (!DocumentReading.Numbers(row, "p_u", 3, -FixedPlacement.MaximumCoordinate, FixedPlacement.MaximumCoordinate, out var position))
        { code = "fixed-controller-position"; reason = "A position carries three finite metre coordinates in the room's prefab-root frame."; return false; }
        if (!DocumentReading.Number(row, "yaw_u", -FixedPlacement.MaximumYawDegrees, FixedPlacement.MaximumYawDegrees, out var yaw))
        { code = "fixed-controller-yaw"; reason = "A yaw is a finite angle in degrees about +Y."; return false; }
        controller = new FixedBulkheadController(id!, level, locator!, position!, yaw);
        return true;
    }
}
