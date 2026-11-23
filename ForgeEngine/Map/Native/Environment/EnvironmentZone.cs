using System.Globalization;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// One zone address the environment rows act on: the level's own three coordinates, exactly the three the two
/// zone-addressed light entries and the fog entry carry (`DimensionIndex`, `Layer`, `LocalIndex`). The reference
/// text is the `gtfo.zone` grammar <see cref="RuntimeZones"/> writes, so a zone a selector answered and a zone
/// this layer reads are the same place.
/// </summary>
internal readonly struct EnvironmentZone
{
    private EnvironmentZone(int dimension, int layer, int zone)
    {
        Dimension = dimension; Layer = layer; Zone = zone;
    }

    internal int Dimension { get; }
    internal int Layer { get; }
    internal int Zone { get; }

    /// <summary>The address of a native zone this level already holds, or null when one of its three coordinates is
    /// outside the enum the game indexes that axis with. It is how a zone the level's own table answered is named
    /// again without going back through the reference grammar.</summary>
    internal static EnvironmentZone? Of(int dimension, int layer, int zone)
    {
        var address = new EnvironmentZone(dimension, layer, zone);
        return address.InRange ? address : null;
    }

    /// <summary>Whether a dimension index is inside the enum the game addresses dimensions by. It is the bound
    /// the level's own `eDimensionIndex` declares, shared by the zone addresses below and the dimension the
    /// outside-environment row writes, so the two cannot disagree about which dimensions exist.</summary>
    internal static bool DimensionInRange(int dimension)
        => dimension >= 0 && dimension < (int)eDimensionIndex.MAX_COUNT;

    /// <summary>Whether each of the three coordinates is inside the enum the game indexes that axis with. The
    /// check is made here rather than at the cast, because a cast of an out-of-range integer produces a member
    /// the game never assigned and the entry would then read a zone that does not exist.</summary>
    internal bool InRange => DimensionInRange(Dimension)
        && Layer >= 0 && Layer <= (int)LG_LayerType.ThirdLayer
        && Zone >= 0 && Zone <= (int)eLocalZoneIndex.Zone_20;

    internal eDimensionIndex DimensionIndex => (eDimensionIndex)Dimension;
    internal LG_LayerType LayerType => (LG_LayerType)Layer;
    internal eLocalZoneIndex LocalIndex => (eLocalZoneIndex)Zone;

    internal GlobalZoneIndex Global => new(Dimension, Layer, Zone);

    /// <summary>One `gtfo.zone:<dimension>:<layer>:<zone>` reference, or null when the text is not that
    /// grammar. Nothing is inferred from a shorter or longer id: an address this layer cannot read is refused
    /// rather than defaulted to the reality layer.</summary>
    internal static EnvironmentZone? Parse(EntityReference reference)
    {
        if (!EntityKinds.Is(reference, EntityKinds.Zone)) return null;
        var parts = reference.Id[(EntityKinds.Zone.Length + 1)..].Split(':');
        if (parts.Length != 3) return null;
        var numbers = new int[3];
        for (var index = 0; index < 3; index++)
            if (!int.TryParse(parts[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out numbers[index]))
                return null;
        var zone = new EnvironmentZone(numbers[0], numbers[1], numbers[2]);
        return zone.InRange ? zone : null;
    }
}

