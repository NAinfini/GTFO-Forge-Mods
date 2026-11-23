
namespace ForgeMap;

/// <summary>
/// One authored bulkhead controller at a fixed point of a room: the level, the room, the room-local point and yaw.
/// The row's identity inside its level is the zone its room stands in. The game's own controller logic
/// (`LG_BulkheadDoorController_Core.BuildBulkheadLogic`, build 20403457) reads the zone of the controller's terminal
/// node and opens the bulkhead doors that zone leads to directly, at most one per layer; a zone carries at most one
/// controller in vanilla data. So which controller a row moves is the one the level generated in the row's zone, and
/// a second row naming the same zone of the same level is refused.
///
/// This row moves a controller the level's own generator already placed; it never builds one, and it never names a
/// door: the doors the controller opens follow from its zone.
/// </summary>
internal sealed class FixedBulkheadController : FixedRow
{
    internal FixedBulkheadController(string id, MapLevelReference level, RoomLocator locator, double[] position,
        double yaw)
        : base(id, level, locator, position, yaw) { }


    /// <summary>Whether this row names the controller of one zone: the dimension, layer and local index of the
    /// zone the controller's own terminal node stands in.</summary>
    public bool InZone(int dimension, int layer, int localIndex)
        => Locator.Dimension == dimension && Locator.Layer == layer && Locator.LocalIndex == localIndex;

    /// <summary>The row's zone as one diagnostic spelling, `dimension/layer/zone`.</summary>
    public string ZoneText => Locator.Dimension + "/" + Locator.Layer + "/" + Locator.LocalIndex;
}
