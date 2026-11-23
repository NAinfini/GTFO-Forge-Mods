using Gear;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The game-bound half of `forge.query.map.item_type`. The instance is what the level-object kind published
/// for the reference: a placed pickup's `LG_PickupItem_Sync` or a spawned item's `ItemReplicator`. The class is the
/// item component's own native class, the number its `ItemDataBlock.persistentID`.</summary>
internal static class ItemTypeQuery
{
    // Indices in the `item_kind` enum set (wire.json), in its order.
    private const int Carry = 0, Key = 1, SmallPickup = 2, ResourcePack = 3, Consumable = 4, Artifact = 5, Commodity = 6, Other = 7;

    internal static ItemTypeContract.ItemType? Read(object instance)
    {
        Item? item = instance switch
        {
            LG_PickupItem_Sync sync when !sync.WasCollected => sync.item,
            ItemReplicator replicator when !replicator.WasCollected => replicator.Item,
            Item direct => direct,
            _ => null
        };
        if (item == null || item.WasCollected || item.ItemDataBlock is not { } block) return null;
        int kind = item.TryCast<CarryItemPickup_Core>() != null ? Carry
            : item.TryCast<KeyItemPickup_Core>() != null ? Key
            : item.TryCast<GenericSmallPickupItem_Core>() != null ? SmallPickup
            : item.TryCast<ResourcePackPickup>() != null ? ResourcePack
            : item.TryCast<ConsumablePickup_Core>() != null ? Consumable
            : item.TryCast<ArtifactPickup_Core>() != null ? Artifact
            : item.TryCast<CommodityPickup_Core>() != null ? Commodity
            : Other;
        return new ItemTypeContract.ItemType(kind, block.persistentID);
    }
}
