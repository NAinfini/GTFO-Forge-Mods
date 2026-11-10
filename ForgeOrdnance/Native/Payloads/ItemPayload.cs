using System;
using System.Text.Json;
using AIGraph;
using GameData;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

internal sealed record ItemParameters(ItemEntry[] Items);

/// <summary>Built-in payload <c>Item</c>: pickups of item data blocks, spawned through the game's own item replication.
/// <c>{ "Type": "Item", "Items": [ { "Item": 101 | "name", "Count": 1..4, "AmmoRatio": 0..1 }, ... ] }</c>, at most
/// <see cref="ItemList.MaxPickups"/> pickups in all. They arrive in the drop pod: every peer plays the pod (<see cref="DropPod"/>);
/// the host places the pickups on the pod's anchors just after it lands, shut away until its doors open, and reports the pod empty once all are taken.</summary>
internal sealed class ItemPayload : IOrdnancePayload
{
    internal const string TypeName = "Item";
    /// <summary>The host reads each placed pickup's replicated state back this long after placing it.</summary>
    private const float ReadBackSeconds = 0.5f;

    public object Parse(JsonElement payload) => new ItemParameters(ItemList.Parse(payload, Resolve));

    private static uint? Resolve(uint? id, string? name)
    {
        var block = id.HasValue ? GameDataBlockBase<ItemDataBlock>.GetBlock(id.Value) : GameDataBlockBase<ItemDataBlock>.GetBlock(name!);
        if (block == null || block.WasCollected) return null;
        return PayloadReader.ReplicatedPrefabId(block.persistentID, "Item");
    }

    public bool HasOwnArrival => true;

    public bool SupportsDirect => true;

    public void OnArrived(OrdnancePresentation presentation) => Deliveries.StartPod(presentation);

    public void Execute(OrdnanceExecution execution)
    {
        var p = (ItemParameters)execution.Parameters;
        var ids = ItemList.Expand(p.Items, out var ratios);
        foreach (var id in ids)
            if (!ItemSpawnManager.ItemHasMode(id, ItemMode.Pickup)) throw new InvalidOperationException("Item " + id + " has no pickup prefab.");
        var node = execution.CourseNode;
        var landing = execution.Position;
        var podRotation = DeliveryTiming.PodRotation(execution.Rotation.eulerAngles.y);
        var thrower = execution.SummonerLookup;
        var throwId = execution.ThrowId;
        var inPod = ArrivalPlan.HasVehicle(execution.Arrival);
        // Dropped: stocked just after touchdown, long before the doors open, and shut away (SupplyHold) until they do.
        // Direct: laid out on the ground at once.
        Deliveries.HostAfter(ArrivalPlan.ContentsAfter(execution.Arrival, PodTimeline.StockDelay), () =>
        {
            try { Place(ids, ratios, node, landing, podRotation, thrower, throwId, inPod); }
            catch (Exception error)
            {
                Plugin.Logger.ErrorOnce("Drop pod of throw " + throwId + " could not finish placing its pickups; any pickups already spawned remain available: " + error);
                Deliveries.ReportFailure(thrower, throwId);
            }
        });
    }

    private static void Place(uint[] ids, float[] ratios, AIG_CourseNode node, Vector3 landing, Quaternion podRotation, ulong thrower, uint throwId, bool inPod)
    {
        if (node == null || node.WasCollected) throw new InvalidOperationException("The landing course node is gone.");
        var host = SNet.LocalPlayer ?? throw new InvalidOperationException("The host has no local network player.");
        var placed = new ItemInLevel?[ids.Length];
        var problems = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            var block = GameDataBlockBase<ItemDataBlock>.GetBlock(ids[i]) ?? throw new InvalidOperationException("Item data block " + ids[i] + " is gone.");
            var (position, rotation) = PodPacking.Pose(ids[i], landing, podRotation, i, inPod);
            var custom = new pItemData_Custom { ammo = Ammo(block, ratios[i]) };
            // ForgeCore spawns the pickup through the game's own item replication; in play the game gives its state replicator its own key.
            // The game's red lines for a pickup made in play ("self amanged replicators during play", "CullBucket was null", and the
            // "replaced duplicate entry ... terminalKey" warning, which the level's own pickups also raise) are expected and left alone
            // on purpose; changing the replicator key or the culler is what broke amounts after a checkpoint reload (ForgeCore VALIDATION).
            var replicator = ForgeCore.Items.Spawn(ids[i], block.inventorySlot, custom, ItemMode.Pickup, position, rotation, node, out var refusal);
            if (refusal == ForgeCore.ItemSpawnRefusal.NotReady) throw new InvalidOperationException("The game has not finished building its item prefabs or item replication is not ready.");
            var item = replicator?.Item?.TryCast<ItemInLevel>();
            placed[i] = item;
            if (item == null) continue;
            // A pickup spawned at run time starts from an empty pickup state: no amount (an empty pack) and no course node.
            // Placing it through the pickup's own replicated interaction, the way a dropped item is placed, gives every peer the
            // amount and the placement (position, rotation, node).
            item.CourseNode = node;
            if (inPod) Deliveries.Hold(item, thrower, throwId);
            var sync = item.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
            if (sync == null || sync.WasCollected)
            {
                Plugin.Logger.ErrorOnce("Drop pod of throw " + throwId + ": pickup " + ids[i] + " has no pickup state to place.");
                problems++;
                continue;
            }
            sync.AttemptPickupInteraction(ePickupItemInteractionType.Place, host, custom, position, rotation, node, false, true);
            var bay = i + 1;
            var id = ids[i];
            Deliveries.HostAfter(ReadBackSeconds, () => ReadBack(sync, throwId, id, bay, custom.ammo, position));
        }
        var unreadable = Array.FindAll(placed, item => item == null).Length;
        if (!inPod)
        {
            if (unreadable != 0 || problems != 0)
                Plugin.Logger.ErrorOnce("Direct supply of call " + throwId + ": " + unreadable + " of its " + ids.Length + " requested pickups could not be read back and "
                    + problems + " were not placed as asked; any pickups that did spawn remain available.");
            return;
        }
        if (unreadable == 0 && problems == 0) Deliveries.Watch(thrower, throwId, placed);
        else
        {
            Plugin.Logger.ErrorOnce("Drop pod of throw " + throwId + ": " + unreadable + " of its " + ids.Length + " requested pickups could not be read back and "
                + problems + " were not placed as asked; any pickups that did spawn remain available and the untrackable pod will leave.");
            Deliveries.ReportFailure(thrower, throwId);
        }
    }

    /// <summary>Host: what the pickup's replicated state says once the placement has gone through: in the level, with the amount
    /// and at the place asked for. A difference is an error in the log; the pickup stays where the game put it.</summary>
    private static void ReadBack(LG_PickupItem_Sync sync, uint throwId, uint id, int bay, float ammo, Vector3 position)
    {
        if (sync == null || sync.WasCollected) return;
        var state = sync.GetCurrentState();
        var at = state.placement.position;
        if (state.status != ePickupItemStatus.PickedUp && (Math.Abs(state.custom.ammo - ammo) > 0.01f || (at - position).sqrMagnitude > 0.0025f))
            Plugin.Logger.ErrorOnce("Drop pod of throw " + throwId + ": pickup " + id + " in bay " + bay + " reads back amount " + state.custom.ammo + " (wanted " + ammo
                + ") at " + ForgeCore.Log.Pos(at) + " (wanted " + ForgeCore.Log.Pos(position) + ").");
    }

    /// <summary>The pickup's native amount (<see cref="ItemList.AmmoPoints"/>): a consumable counts its uses, a resource pack points out of 100.</summary>
    private static float Ammo(ItemDataBlock block, float ratio)
        => ItemList.AmmoPoints(block.inventorySlot is InventorySlot.Consumable or InventorySlot.ConsumableHeavy ? block.ConsumableAmmoMax : 0, ratio);
}
