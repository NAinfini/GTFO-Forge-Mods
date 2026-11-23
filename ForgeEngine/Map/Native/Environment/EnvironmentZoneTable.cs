using System.Collections.Generic;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The standing level's own zone table: the one place the environment slice turns a `gtfo.zone` address into the
/// native `LG_Zone` it names, and the one place it asks for every zone the level holds. The table is the level
/// builder's own `m_currentFloor.allZones` — the same list every map-object address and every zone read is built
/// from — so a place this level does not have is refused by name instead of being answered as an empty one, and no
/// second zone ledger is kept beside it.
///
/// Two rows need it: the `v-env` read, which counts a zone's light objects, and the light-colour action,
/// which writes them. Both run against the same reading of the same list, which is why it lives here rather than
/// in either of them.
/// </summary>
internal static class EnvironmentZoneTable
{
    /// <summary>The one zone of the standing level that answers to a coordinate triple, or null when no single zone
    /// does. A zone whose layer or dimension does not read has no coordinates and is skipped rather than matched by
    /// a guessed address, and two zones answering to one address make that address ambiguous, so neither of them is
    /// reachable through it.</summary>
    internal static LG_Zone? At(EnvironmentZone coordinates)
    {
        LG_Zone? found = null;
        foreach (var zone in Standing())
        {
            var layer = zone.m_layer;
            if (layer == null) continue;
            if ((int)zone.m_dimensionIndex != coordinates.Dimension || (int)layer.m_type != coordinates.Layer
                || (int)zone.LocalIndex != coordinates.Zone) continue;
            if (found != null) return null;
            found = zone;
        }
        return found;
    }

    /// <summary>Every zone the standing level holds, in the order the level built them, with the entries that are
    /// not zones dropped. An empty list is a level that is not there — no builder, no floor or no zone table — and
    /// every caller refuses that rather than answering as a level with no zones. The list is this package's own
    /// copy: the caller walks it while it also writes into the level, and the level's own list is not a thing to
    /// hold across that.</summary>
    internal static List<LG_Zone> Standing()
    {
        var zones = new List<LG_Zone>();
        var builder = LG_LevelBuilder.Current;
        var floor = builder == null ? null : builder.m_currentFloor;
        var all = floor == null ? null : floor.allZones;
        if (all == null) return zones;
        for (var index = 0; index != all.Count; index++)
        {
            var zone = all[index];
            if (zone != null) zones.Add(zone);
        }
        return zones;
    }
}
