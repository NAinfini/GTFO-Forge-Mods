using System;
using System.Collections.Generic;

namespace ForgeMap;

/// <summary>What every authored fixed placement row is: the author's id, the level it belongs to, the room it stands
/// in, and the room-local point and yaw it stands at. The six row kinds (items, enemies, sacks, landings, bulkhead
/// controllers and bulkhead keys) add only their own fields, and all of them reach the world through the one
/// conversion here, so a room's rotation turns both the position and the facing.</summary>
internal abstract class FixedRow
{
    private protected FixedRow(string id, MapLevelReference level, RoomLocator locator, double[] position, double yaw)
    {
        Id = id;
        Level = level;
        Locator = locator;
        Position = Array.AsReadOnly(position);
        Yaw = yaw;
    }

    /// <summary>The author's own id with the document's kind prefix (`item:`, `enemy:`, `sack:`, `landing:`,
    /// `controller:`, `bulkhead-key:`), used to name the row in a diagnostic and to derive its placement seed.</summary>
    public string Id { get; }
    /// <summary>The level this row belongs to. A row of another level is not this world's row.</summary>
    public MapLevelReference Level { get; }
    /// <summary>The room the row's point stands in, in the one locator spelling every authored reference uses.</summary>
    public RoomLocator Locator { get; }
    /// <summary>The point in the room's prefab-root local coordinates, in metres. The height is part of it: an item on
    /// a shelf is the author's own height and not a floor drop.</summary>
    public IReadOnlyList<double> Position { get; }
    /// <summary>The point's yaw in degrees about +Y, in the same local frame as <see cref="Position"/>.</summary>
    public double Yaw { get; }

    /// <summary>The row's own rotation in its room's local frame: the authored yaw about +Y.</summary>
    public double[] LocalRotation() => Rotations.Yaw(Yaw);

    /// <summary>This row's placement in the world: the room's own world pose composed with the row's local point and
    /// rotation.</summary>
    public RoomPose WorldPose(RoomPose room) => RoomPose.Of(room.Point(Position), room.Compose(LocalRotation()));

    public override string ToString() => Id;
}

/// <summary>The rows one fixed-placement document answered: the rows it could read, in the author's order, and the
/// rows it refused.</summary>
internal sealed class FixedDocument<T> where T : FixedRow
{
    internal FixedDocument(IReadOnlyList<T> rows, IReadOnlyList<FixedPlacementRefusal> refusals)
    {
        Rows = rows;
        Refusals = refusals;
    }

    public IReadOnlyList<T> Rows { get; }
    public IReadOnlyList<FixedPlacementRefusal> Refusals { get; }
}
