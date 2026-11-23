using System.Collections.Generic;

namespace ForgeMap;

/// <summary>
/// How one authored fixed item is placed. The route is a property of the item's own inventory slot, not of the
/// document: the game's placement entry points differ by slot, and the one the item's own native block belongs to
/// is the only one that builds it correctly. The site never writes this — the block decides it at spawn time.
/// </summary>
internal enum FixedItemRoute
{
    /// <summary>A native pickup prefab, including slot None commodities and the microdrive. The native factory
    /// recipe initializes its pickup sync, terminal identity and any carried global state.</summary>
    Pickup,
    /// <summary>The native spawn manager entry point: a resource pack's own native path builds it from a position
    /// and a rotation, and the align entry point refuses its slot outright.</summary>
    ResourcePack
}

/// <summary>
/// One authored item placed at a fixed point of a room: the level it belongs to, the room it stands in, its own
/// prefab-root-local position and yaw, and what it is. An item row names exactly one of two things — a native item
/// block id, or the zone door whose own key it is — because the two are answers to "what is this item" and a row
/// that carried both, or neither, would name nothing.
///
/// A door's key is a door reference rather than an item id because the level's generator assigns zone keys at
/// random per level: the card that opens a door is the card whose block id the door's own lock asks for, so the
/// runtime reads that id off the door instead of trusting a number the author could not know.
/// </summary>
internal sealed class FixedItem : FixedRow
{
    internal FixedItem(string id, MapLevelReference level, RoomLocator locator, double[] position, double yaw,
        uint? itemId, double amount, MapObjectReference? targetDoor, FixedTaskObjectRole? taskObject = null,
        string? containerType = null, string? containerId = null, bool taskItemsOnly = false,
        FixedObjectiveCargo? objectiveCargo = null, FixedContainerLock? containerLock = null, bool startsOff = false)
        : base(id, level, locator, position, yaw)
    {
        ItemId = itemId;
        Amount = amount;
        TargetDoor = targetDoor;
        TaskObject = taskObject;
        ContainerType = containerType;
        ContainerId = containerId;
        TaskItemsOnly = taskItemsOnly;
        ObjectiveCargo = objectiveCargo;
        Lock = containerLock;
        StartsOff = startsOff;
    }

    /// <summary>The native `ItemDataBlock.persistentID` to place, or null when this row names a door's key.</summary>
    public uint? ItemId { get; }
    /// <summary>The consumable uses or resource-pack share this row carries, in the meaning the placed block's own
    /// slot gives it; zero on an item that is not consumed.</summary>
    public double Amount { get; }
    /// <summary>The zone door whose own key this row places, or null when the row names an item id. It is the
    /// door's own address, which is how the runtime finds the lock that knows the key's block id.</summary>
    public MapObjectReference? TargetDoor { get; }
    /// <summary>A native objective object to move, instead of a pickup to instantiate.</summary>
    public FixedTaskObjectRole? TaskObject { get; }
    public string? ContainerType { get; }
    public string? ContainerId { get; }
    /// <summary>Native regional tasks select only marked containers in this row's zone. Elevator cargo is unchanged.</summary>
    public bool TaskItemsOnly { get; }
    public FixedObjectiveCargo? ObjectiveCargo { get; }
    public FixedContainerLock? Lock { get; }
    /// <summary>The placed Fog Repeller (item 133) is left in the state its pickup is created with instead of being
    /// activated at spawn; a player picking it up and putting it down turns it on as the game does. Only that item
    /// has an on/off state, so the document accepts the flag on no other row.</summary>
    public bool StartsOff { get; }
    /// <summary>A placed object whose receptacle takes only these items, by native Item id; null keeps the game's own rule.
    /// Only an object that takes items has a receptacle (<see cref="HasReceptacle"/>).</summary>
    public IReadOnlyList<uint>? AcceptItems { get; internal set; }
    /// <summary>A placed object whose own look is hidden; a placed model beside it is what players see. Its interaction,
    /// collision and prompt stay the game's.</summary>
    public bool HideBody { get; internal set; }
    /// <summary>The author's group of a generator: generators of one group count their connected and total cells
    /// together instead of the native generator group they were built in. Null on every other row.</summary>
    public string? Group { get; internal set; }
    /// <summary>The placed objects with a receptacle the game inserts carried items into: the generator's cell slot.</summary>
    public static bool HasReceptacle(FixedItem item) => item.TaskObject?.Kind == FixedTaskObjectKind.Generator;
}

/// <summary>A resolved claim on a native Type 7 battery; it never requests a spawn.</summary>
internal sealed record FixedObjectiveCargo(int Layer, int ChainIndex, int Index, int Count);
internal enum FixedWeakLockType { None = 0, Melee = 1, Hackable = 2 }
internal sealed record FixedContainerLock(bool LockedFromStart, FixedWeakLockType WeakLockType);
