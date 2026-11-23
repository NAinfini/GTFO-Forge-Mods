
namespace ForgeMap;

/// <summary>
/// One authored landing point of one dimension: the level, the dimension the point is registered into, the room
/// the point stands in, and the room-local point and yaw. The row's position inside its dimension is the player
/// slot it is registered for, so the document's own order is the whole slot assignment and there is no slot field.
///
/// The room's locator carries its own native dimension, and a landing's room has to stand in the dimension it
/// registers into: a world pose from another dimension's geometry names no place in this one. That is refused at
/// the document rather than placed, because a spawn point at a wrong world pose is worse than a missing one.
/// </summary>
internal sealed class FixedLanding : FixedRow
{
    internal FixedLanding(string id, MapLevelReference level, int dimension, RoomLocator locator, double[] position,
        double yaw)
        : base(id, level, locator, position, yaw)
    {
        Dimension = dimension;
    }

    /// <summary>The native dimension: 1–20 register dimension spawn points.</summary>
    public int Dimension { get; }
}
