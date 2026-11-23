using System;
using System.Collections.Generic;

namespace ForgeMap;

/// <summary>
/// The one step between an authored zone and a judged one: a zone's document pose is the room's local coordinates,
/// and only the room's own world pose turns it into a volume. Placement is a fold over a level, not a mutation: the
/// authored table stays what the document said, and every pass answers the table this level can judge.
///
/// A zone whose room cannot be resolved is refused by name and left out of the answer, because a zone judged in
/// coordinates nobody converted is a volume at the wrong place, and a wrong volume is worse than a missing one.
/// Zones of another level are carried through untouched: their own level places them when it is generated.
/// </summary>
internal static class TriggerZonePlacement
{
    /// <summary>The code every room refusal carries, so one report line names the same thing whatever the reason
    /// was: the zone, its room, and that it is not judged.</summary>
    public const string RefusedCode = "trigger-zone-room";

    /// <summary>The zones of one level in world coordinates, in the document's own order. A zone that already
    /// carries a world pose and a zone of another level answer themselves; every other zone is placed through
    /// <paramref name="rooms"/>, or refused by name through <paramref name="refused"/> and left out.</summary>
    public static IReadOnlyList<TriggerZone> Of(IReadOnlyList<TriggerZone> zones, MapLevelReference level,
        RoomLookup? rooms, Action<string> refused)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(refused);
        var placed = new List<TriggerZone>(zones.Count);
        foreach (var zone in zones)
        {
            if (zone.Placed || zone.Level != level) { placed.Add(zone); continue; }
            if (rooms == null)
            {
                refused(Refusal(zone, "no room resolver is installed in this build"));
                continue;
            }
            RoomPose? pose;
            try { pose = rooms(zone.Room); }
            catch (Exception error) { refused(Refusal(zone, "the room resolver failed") + Environment.NewLine + error); continue; }
            if (pose is not { } room)
            {
                refused(Refusal(zone, "this level holds no single room matching `" + zone.Room.SourcePrefab + "`"));
                continue;
            }
            try { placed.Add(zone.PlacedIn(room)); }
            catch (Exception error)
            {
                refused(Refusal(zone, "the resolved room pose cannot be used") + Environment.NewLine + error);
            }
        }
        return Array.AsReadOnly(placed.ToArray());
    }

    private static string Refusal(TriggerZone zone, string reason)
        => RefusedCode + ": zone `" + zone.Id + "` in room " + zone.Room + " is not judged because " + reason + ".";
}
