using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

// Placement document parsing for Sacks. Shared envelope rules remain in FixedPlacementManifest.cs.
internal static partial class FixedPlacementManifest
{
    private const string SackFileName = "fixed-sacks.forge";
    /// <summary>The sack document's one file name.</summary>
    public static string SackFileNameOnly => SackFileName;

    /// <summary>Reads one sack document, with the same row-level contract as an item document.</summary>
    public static bool TryParseSacks(string text, out FixedDocument<FixedSack>? document, out string? code, out string? reason)
    {
        document = null;
        using var parsed = Read(text, SackRootCodes, "sacks", out code, out reason);
        if (parsed == null) return false;
        var sacks = new List<FixedSack>();
        var refusals = new List<FixedPlacementRefusal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in parsed.RootElement.GetProperty("sacks").EnumerateArray())
        {
            index++;
            if (!TrySack(row, out var sack, out var rowCode, out var rowReason))
            {
                refusals.Add(new FixedPlacementRefusal(rowCode!, Row(index, rowReason!), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (!seen.Add(sack!.Id))
            {
                refusals.Add(new FixedPlacementRefusal("fixed-sack-duplicate",
                    Row(index, "Sack id `" + sack.Id + "` is claimed more than once; the later row is left out."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            sacks.Add(sack);
        }
        document = new FixedDocument<FixedSack>(Array.AsReadOnly(sacks.ToArray()), Array.AsReadOnly(refusals.ToArray()));
        return true;
    }

    private static bool TrySack(JsonElement row, out FixedSack? sack, out string? code, out string? reason)
    {
        sack = null;
        code = "fixed-sack-row";
        reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "A sack row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "locator", "p_u", "yaw_u", "scale", "variant"))
        { code = "fixed-sack-field"; reason = "Unknown sack field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "id", FixedPlacement.MaximumIdChars, out var id) || !Prefixed(id!, "sack:"))
        { code = "fixed-sack-id"; reason = "A sack needs an id of at most " + FixedPlacement.MaximumIdChars + " characters, written `sack:<id>`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "fixed-sack-level"; reason = "A sack needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!Locator(row, out var locator, out reason)) { code = "fixed-sack-room"; return false; }
        if (!DocumentReading.Numbers(row, "p_u", 3, -FixedPlacement.MaximumCoordinate, FixedPlacement.MaximumCoordinate, out var position))
        { code = "fixed-sack-position"; reason = "A position carries three finite metre coordinates in the room's prefab-root frame."; return false; }
        if (!DocumentReading.Number(row, "yaw_u", -FixedPlacement.MaximumYawDegrees, FixedPlacement.MaximumYawDegrees, out var yaw))
        { code = "fixed-sack-yaw"; reason = "A yaw is a finite angle in degrees about +Y."; return false; }
        if (!DocumentReading.Number(row, "scale", 0, FixedPlacement.MaximumScale, out var scale) || scale <= 0)
        { code = "fixed-sack-scale"; reason = "A sack's scale is a finite positive number in (0, " + FixedPlacement.MaximumScale + "]."; return false; }
        if (!DocumentReading.Text(row, "variant", 16, out var variantText) || !TryVariant(variantText!, out var variant))
        { code = "fixed-sack-variant"; reason = "A variant is `large` or `small`."; return false; }
        sack = new FixedSack(id!, level, locator!, position!, yaw, scale, variant);
        return true;
    }

    private static bool TryVariant(string text, out FixedSackVariant variant)
    {
        switch (text)
        {
            case "large": variant = FixedSackVariant.Large; return true;
            case "small": variant = FixedSackVariant.Small; return true;
            default: variant = FixedSackVariant.Large; return false;
        }
    }
}
