
namespace ForgeMap;

/// <summary>
/// Which of the two prefabs one authored fixed sack is, as the static-spawn block's own two routes name them. The
/// names are the document's wire spellings: the block's own `Path` is the large sack, and the smallest
/// `DistanceMin` entry of its `PrefabDistancePairs` is the small one the vanilla generator uses within its own
/// near range. Neither is a prefab path the document may write — the block stays the one source of a path.
/// </summary>
internal enum FixedSackVariant { Large, Small }

/// <summary>
/// One authored fixed sack placed at a fixed point of a room: the level, the room, the room-local point and yaw,
/// how big it is, and which of the static-spawn block's two prefabs it is. The scale is the author's own size and
/// is checked against the block's `MinScale..MaxScale` at placement time, because that window is the block's and
/// not this document's; the variant names one of the block's own two routes rather than a prefab path.
///
/// A sack is a visual and audio marker: the prefab carries no state and nothing replicates it, so every machine
/// places its own copy from the same document. It is placed like an item and not like an enemy.
/// </summary>
internal sealed class FixedSack : FixedRow
{
    internal FixedSack(string id, MapLevelReference level, RoomLocator locator, double[] position, double yaw,
        double scale, FixedSackVariant variant)
        : base(id, level, locator, position, yaw)
    {
        Scale = scale;
        Variant = variant;
    }

    /// <summary>The scale the sack is placed at, which the static-spawn block's own `MinScale..MaxScale` accepts
    /// or refuses at placement time.</summary>
    public double Scale { get; }
    /// <summary>Which of the block's two prefabs this row is.</summary>
    public FixedSackVariant Variant { get; }
}
