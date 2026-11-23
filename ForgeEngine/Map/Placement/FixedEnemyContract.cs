
namespace ForgeMap;

/// <summary>
/// What one authored fixed enemy does when it is there. The names are the document's wire spellings; each one is
/// the game's own agent mode, so an intent is a native starting state and not a behaviour this layer interprets.
/// </summary>
internal enum FixedEnemyIntent { Hibernate, Aggressive, Patrolling, Scout }

/// <summary>
/// One authored enemy placed at a fixed point of a room: the level, the room, the room-local point and yaw, the
/// native enemy type, and the mode it starts in. The type is a native `EnemyDataBlock.persistentID` and the intent
/// is the mode that enemy's own setup is handed, so neither is a behaviour this layer reimplements.
/// </summary>
internal sealed class FixedEnemy : FixedRow
{
    internal FixedEnemy(string id, MapLevelReference level, RoomLocator locator, double[] position, double yaw,
        uint typeId, FixedEnemyIntent intent)
        : base(id, level, locator, position, yaw)
    {
        TypeId = typeId;
        Intent = intent;
    }

    /// <summary>The native `EnemyDataBlock.persistentID` of the enemy to place.</summary>
    public uint TypeId { get; }
    /// <summary>The mode the enemy starts in.</summary>
    public FixedEnemyIntent Intent { get; }
}
