
namespace ForgeMap;

/// <summary>
/// The layer one authored bulkhead key generated in, as the key document's wire spellings name it. Unlike a
/// controller's target layer this is every layer the game has: a bulkhead key is placed on the layer whose own
/// `BulkheadKeyPlacements` its key group was written on, and a source layer's own keys are `main` keys.
/// </summary>
internal enum FixedBulkheadKeyLayer { Main, Secondary, Third }

/// <summary>
/// One bulkhead key this level carries, as matching reads it: the index the level's own registry lists it at, and
/// the layer of the zone the generator placed it in. The layer is read off the key's own spawn zone, so it is where
/// the key really stands and not a number the placement computed. The zone is the key's own spawn zone, so a row
/// whose room stands in it prefers this key over another key of the same layer.
/// </summary>
internal readonly record struct BulkheadKeyCandidate(int Index, FixedBulkheadKeyLayer Layer, int Dimension, int LocalIndex);

/// <summary>
/// One authored bulkhead key at a fixed point of a room: the level, the room, the room-local point and yaw, and the
/// layer the key generated on. That layer is where the level's own `BulkheadKeyPlacements` put the key's group, and
/// it is also the layer the authored point stands on, because the site writes the group's candidate zones into the
/// point's own zone — so the key this row claims already generated in the room the point names.
///
/// This row moves a key the level's own generator already placed; it never builds one. Several keys of one layer are
/// ordinary, which is why the layer is not the row's identity here the way it is for a controller.
/// </summary>
internal sealed class FixedBulkheadKey : FixedRow
{
    internal FixedBulkheadKey(string id, MapLevelReference level, RoomLocator locator, double[] position,
        double yaw, FixedBulkheadKeyLayer layer)
        : base(id, level, locator, position, yaw)
    {
        Layer = layer;
    }

    /// <summary>The layer the key this row claims generated on.</summary>
    public FixedBulkheadKeyLayer Layer { get; }
}
