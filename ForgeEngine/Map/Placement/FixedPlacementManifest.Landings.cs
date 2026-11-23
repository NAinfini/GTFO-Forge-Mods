using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

// Placement document parsing for Landings. Shared envelope rules remain in FixedPlacementManifest.cs.
internal static partial class FixedPlacementManifest
{
    private const string LandingFileName = "dimension-landings.forge";
    /// <summary>The dimension-landing document's one file name.</summary>
    public static string LandingFileNameOnly => LandingFileName;

    /// <summary>
    /// Reads one dimension-landing document. Its rows are refused one by one like every other document of this
    /// family, and the one rule that is not a single row's own is the slot cap: a dimension carries four player
    /// slots, so the fifth row written for one dimension is refused by name here instead of being read as a fifth
    /// slot that does not exist. A fifth row another document contributes is refused at registration, where the
    /// whole dimension's rows are known.
    /// </summary>
    public static bool TryParseLandings(string text, out FixedDocument<FixedLanding>? document, out string? code, out string? reason)
    {
        document = null;
        using var parsed = Read(text, LandingRootCodes, "landings", out code, out reason);
        if (parsed == null) return false;
        var landings = new List<FixedLanding>();
        var refusals = new List<FixedPlacementRefusal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in parsed.RootElement.GetProperty("landings").EnumerateArray())
        {
            index++;
            if (!TryLanding(row, out var landing, out var rowCode, out var rowReason))
            {
                refusals.Add(new FixedPlacementRefusal(rowCode!, Row(index, rowReason!), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (!seen.Add(landing!.Id))
            {
                refusals.Add(new FixedPlacementRefusal("dimension-landing-duplicate",
                    Row(index, "Landing id `" + landing.Id + "` is claimed more than once; the later row is left out."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            var slot = landing.Level.ToString() + "/" + landing.Dimension;
            slots.TryGetValue(slot, out var taken);
            if (taken >= FixedPlacement.MaximumLandingSlots)
            {
                refusals.Add(new FixedPlacementRefusal("dimension-landing-limit",
                    Row(index, "Dimension " + landing.Dimension + " of level " + landing.Level + " already carries "
                        + taken + " rows and has " + FixedPlacement.MaximumLandingSlots
                        + " player slots; the later row is left out."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            slots[slot] = taken + 1;
            landings.Add(landing);
        }
        document = new FixedDocument<FixedLanding>(Array.AsReadOnly(landings.ToArray()), Array.AsReadOnly(refusals.ToArray()));
        return true;
    }

    private static bool TryLanding(JsonElement row, out FixedLanding? landing, out string? code, out string? reason)
    {
        landing = null;
        code = "dimension-landing-row";
        reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "A landing row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "dimension", "locator", "p_u", "yaw_u"))
        { code = "dimension-landing-field"; reason = "Unknown landing field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "id", FixedPlacement.MaximumIdChars, out var id) || !Prefixed(id!, "landing:"))
        { code = "dimension-landing-id"; reason = "A landing needs an id of at most " + FixedPlacement.MaximumIdChars + " characters, written `landing:<id>`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "dimension-landing-level"; reason = "A landing needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!RuntimeJson.TryInteger(row, "dimension", 1, FixedPlacement.MaximumDimension, out var dimensionValue))
        { code = "dimension-landing-dimension"; reason = "A landing's dimension is a whole number in [1, " + FixedPlacement.MaximumDimension + "]."; return false; }
        if (!Locator(row, out var locator, out reason)) { code = "dimension-landing-room"; return false; }
        // A spawn point of one dimension is a world pose in that dimension's own space, so a room of another
        // dimension's geometry names no place in it. The mismatch is refused here rather than registered as a
        // point at a world pose nothing stands at.
        if (locator!.Dimension != (int)dimensionValue)
        { code = "dimension-landing-dimension"; reason = "A landing's room stands in dimension " + locator.Dimension + " but the row registers into dimension " + dimensionValue + "; the two are the same dimension."; return false; }
        if (!DocumentReading.Numbers(row, "p_u", 3, -FixedPlacement.MaximumCoordinate, FixedPlacement.MaximumCoordinate, out var position))
        { code = "dimension-landing-position"; reason = "A position carries three finite metre coordinates in the room's prefab-root frame."; return false; }
        if (!DocumentReading.Number(row, "yaw_u", -FixedPlacement.MaximumYawDegrees, FixedPlacement.MaximumYawDegrees, out var yaw))
        { code = "dimension-landing-yaw"; reason = "A yaw is a finite angle in degrees about +Y."; return false; }
        landing = new FixedLanding(id!, level, (int)dimensionValue, locator, position!, yaw);
        return true;
    }
}
