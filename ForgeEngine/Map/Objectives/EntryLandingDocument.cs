using ForgeCore;
using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

/// <summary>
/// The authored default landing of a level's start elevator (contract §4.5 M13): the start room it is placed in, the
/// point in that room's prefab-root local metres and the facing in degrees about +Y in the same frame. The point may
/// be in the air or in a shaft; nothing here snaps, projects or moves it. A level without a row keeps the native
/// landing.
///
/// A row with a <c>slot</c> is one player's own landing (the native <c>PlayerAgent.PlayerSlotIndex</c>, 0–3, which
/// every machine reads alike): that player stands on it after leaving the seat. A level has at most one party row and
/// one row per slot; a player without a row of their own keeps the party landing if the level has one, otherwise the native landing. The map editor writes either the party row or the player rows of a level, never both.
/// </summary>
internal sealed record EntryLanding(MapLevelReference Level, RoomLocator Locator, IReadOnlyList<double> Position, double YawDegrees, int? Slot = null);

internal static class EntryLandingManifest
{
    public const string FileNameOnly = "entry-landing.forge";
    public const int MaximumRows = 512;

    public static bool TryParse(string text, out IReadOnlyList<EntryLanding>? rows, out string? reason)
    {
        rows = null; reason = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !DocumentReading.Known(root, out _, "schemaVersion", "landings")
                || !FormatVersion.Matches(root)
                || !root.TryGetProperty("landings", out var landings) || landings.ValueKind != JsonValueKind.Array
                || landings.GetArrayLength() > MaximumRows)
            { reason = "Expected schemaVersion 1.0.0 and at most 512 entry landings."; return false; }
            var parsed = new List<EntryLanding>();
            var levels = new HashSet<(MapLevelReference, int?)>();
            foreach (var landing in landings.EnumerateArray())
            {
                if (landing.ValueKind != JsonValueKind.Object || !DocumentReading.Known(landing, out _, "level", "room", "p_u", "yaw_u", "slot")
                    || !DocumentReading.Text(landing, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level
                    || !landing.TryGetProperty("room", out var roomElement) || !RoomLocator.TryParse(roomElement, out var locator, out _, out _)
                    || !DocumentReading.Numbers(landing, "p_u", 3, -float.MaxValue, float.MaxValue, out var position)
                    || !DocumentReading.Number(landing, "yaw_u", -float.MaxValue, float.MaxValue, out var yaw))
                { reason = "Invalid entry landing level, room locator, finite position or finite facing."; return false; }
                foreach (var axis in position!) if (!float.IsFinite((float)axis)) { reason = "An entry landing position is three finite Singles."; return false; }
                if (!float.IsFinite((float)yaw)) { reason = "An entry landing facing is a finite Single."; return false; }
                int? slot = null;
                if (landing.TryGetProperty("slot", out _))
                {
                    if (!RuntimeJson.TryInteger(landing, "slot", 0, FixedPlacement.MaximumLandingSlots - 1, out var value))
                    { reason = "An entry landing slot is a player slot in [0, " + (FixedPlacement.MaximumLandingSlots - 1) + "]."; return false; }
                    slot = (int)value;
                }
                // A level has one start elevator, so one party landing, and one landing per player slot.
                if (!levels.Add((level, slot))) { reason = "A level has at most one entry landing and one per player slot."; return false; }
                parsed.Add(new EntryLanding(level, locator!, Array.AsReadOnly(position!), yaw, slot));
            }
            rows = parsed.AsReadOnly(); return true;
        }
        catch (JsonException error) { reason = error.Message; return false; }
    }
}
