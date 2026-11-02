using System.Text.Json;
using AIGraph;
using ForgeOrdnance;
using GameData;
using UnityEngine;

internal static class DeliveryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const ulong thrower = 71;
        const uint throwId = 19;
        var blockA = new ItemDataBlock { persistentID = 101, name = "ammo", inventorySlot = InventorySlot.ResourcePack };
        var blockB = new ItemDataBlock { persistentID = 102, name = "medi", inventorySlot = InventorySlot.ResourcePack };

        void Reset()
        {
            Deliveries.Clear();
            ItemReplicationManager.ResetSpawns();
            ItemSpawnManager.ReadyToSpawnItems = true;
            ItemSpawnManager.PickupPrefabs.Clear();
            ItemSpawnManager.PickupPrefabs.Add(blockA.persistentID);
            ItemSpawnManager.PickupPrefabs.Add(blockB.persistentID);
            ItemDataBlock.Blocks.Clear();
            ItemDataBlock.Blocks.Add(blockA.persistentID, blockA);
            ItemDataBlock.Blocks.Add(blockB.persistentID, blockB);
            OrdnanceAssets.PodAppearance = null;
            Plugin.Errors.Clear();
        }

        OrdnanceExecution Execution(OrdnanceArrivalMode arrival = OrdnanceArrivalMode.Drop)
        {
            using var json = JsonDocument.Parse("{\"Type\":\"Item\"}");
            var parameters = new ItemParameters(new[] { new ItemEntry(blockA.persistentID, 1, 1f), new ItemEntry(blockB.persistentID, 1, 1f) });
            var definition = new OrdnanceDefinition("test.items", "Items", "Supply", 0x3FA9FF, "", "", new[] { Direction.Up },
                1, 1, 1, ItemPayload.TypeName, json.RootElement.Clone(), parameters, "test.json");
            return new OrdnanceExecution(definition, default, Quaternion.Euler(0, 0, 0), new AIG_CourseNode(), null, thrower, throwId, arrival);
        }

        static void RunScheduled()
        {
            var work = Deliveries.Scheduled.Single();
            Deliveries.Scheduled.Clear();
            work();
        }

        Reset();
        var first = new ItemInLevel();
        var second = new ItemInLevel();
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator { Item = first });
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator { Item = second });
        new ItemPayload().Execute(Execution());
        check(Deliveries.Scheduled.Count == 1, "item delivery schedules one delayed placement after validating every pickup prefab");
        check(Deliveries.ScheduledDelays.Single() == PodTimeline.StockDelay && PodTimeline.StockDelay + PodTimeline.NetworkLead <= PodTimeline.OpenDelay,
            "the supplies are spawned at touchdown plus the settle beat, at least the network lead before the doors start to open");
        RunScheduled();
        check(Deliveries.Held.Count == 2 && Deliveries.Held.All(h => h.Thrower == thrower && h.ThrowId == throwId)
            && ReferenceEquals(Deliveries.Held[0].Item, first) && ReferenceEquals(Deliveries.Held[1].Item, second),
            "every spawned supply pickup is shut away until its pod opens");
        check(!PodTimeline.MayTake(true, false) && PodTimeline.MayTake(true, true) && PodTimeline.MayTake(false, false),
            "a supply cannot be taken while its pod is shut, can once the doors open, and is free when the pod is gone");
        check(ItemReplicationManager.SpawnCalls == 2 && Deliveries.Watched.Count == 1 && Deliveries.Failures.Count == 0
            && ReferenceEquals(Deliveries.Watched[0].Items[0], first) && ReferenceEquals(Deliveries.Watched[0].Items[1], second),
            "a fully readable item delivery watches the actual native pickups until they are taken");
        check(new[] { first, second }.All(p => p.CourseNode != null && p.Sync.Interactions.Count == 1
                && p.Sync.Interactions[0].Type == LevelGeneration.ePickupItemInteractionType.Place && p.Sync.Interactions[0].Ammo == 100f && p.Sync.Interactions[0].Force),
            "each pickup is placed through its own replicated pickup state with a full pack (100) and its course node");
        check(Deliveries.Scheduled.Count == 2, "every placed pickup schedules one read-back of its replicated state");
        foreach (var readBack in Deliveries.Scheduled.ToArray()) readBack();
        Deliveries.Scheduled.Clear();
        check(Plugin.Errors.Count == 0,
            "the read-back finds the full amount at the asked place");

        Reset();
        first = new ItemInLevel();
        second = new ItemInLevel();
        second.Sync.IgnorePlace = true;
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator { Item = first });
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator { Item = second });
        new ItemPayload().Execute(Execution());
        RunScheduled();
        foreach (var readBack in Deliveries.Scheduled.ToArray()) readBack();
        Deliveries.Scheduled.Clear();
        check(Plugin.Errors.Count == 1 && Plugin.Errors[0].Contains("reads back amount 0 (wanted 100)"),
            "a pickup whose replicated state keeps an empty pack is reported, not passed as full");

        Reset();
        first = new ItemInLevel();
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator { Item = first });
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator());
        new ItemPayload().Execute(Execution());
        RunScheduled();
        check(ItemReplicationManager.SpawnCalls == 2 && Deliveries.Watched.Count == 0 && Deliveries.Failures.SequenceEqual(new[] { (thrower, throwId) })
            && !first.WasCollected,
            "a partial native readback reports failure instead of stranding the pod, without removing a readable pickup");

        Reset();
        first = new ItemInLevel();
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator { Item = first });
        ItemReplicationManager.ThrowOnCall = 2;
        new ItemPayload().Execute(Execution());
        var escaped = false;
        try { RunScheduled(); } catch { escaped = true; }
        check(!escaped && ItemReplicationManager.SpawnCalls == 2 && Deliveries.Watched.Count == 0
            && Deliveries.Failures.SequenceEqual(new[] { (thrower, throwId) }) && !first.WasCollected,
            "an asynchronous spawn exception becomes a failed delivery and leaves earlier native pickups available");

        Reset();
        first = new ItemInLevel();
        second = new ItemInLevel();
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator { Item = first });
        ItemReplicationManager.SpawnResults.Enqueue(new ItemReplicator { Item = second });
        new ItemPayload().Execute(Execution(OrdnanceArrivalMode.Direct));
        check(Deliveries.ScheduledDelays.Single() == 0f, "a direct supply skips the pod: its pickups are placed at once, not at the pod's stock time");
        RunScheduled();
        check(ItemReplicationManager.SpawnCalls == 2 && Deliveries.Held.Count == 0 && Deliveries.Watched.Count == 0 && Deliveries.Failures.Count == 0
            && new[] { first, second }.All(p => p.Sync.Interactions.Count == 1 && p.Sync.Interactions[0].Type == LevelGeneration.ePickupItemInteractionType.Place),
            "a direct supply places every pickup through its replicated state on the ground, with no pod to shut them in or report empty");
        foreach (var readBack in Deliveries.Scheduled.ToArray()) readBack();
        Deliveries.Scheduled.Clear();
        check(Plugin.Errors.Count == 0, "a direct supply reads back clean");

        Reset();
        ItemSpawnManager.PickupPrefabs.Remove(blockB.persistentID);
        var refused = false;
        try { new ItemPayload().Execute(Execution()); } catch (InvalidOperationException) { refused = true; }
        check(refused && Deliveries.Scheduled.Count == 0 && ItemReplicationManager.SpawnCalls == 0,
            "a missing pickup prefab fails execution before any delivery work or native spawn is scheduled");

        Reset();
        new ItemPayload().Execute(Execution());
        ItemSpawnManager.ReadyToSpawnItems = false;
        RunScheduled();
        check(ItemReplicationManager.SpawnCalls == 0 && Deliveries.Failures.SequenceEqual(new[] { (thrower, throwId) }),
            "native item readiness lost after acceptance terminates the delayed delivery explicitly");

        Deliveries.Clear();
        ItemReplicationManager.ResetSpawns();
        ItemSpawnManager.PickupPrefabs.Clear();
        ItemDataBlock.Blocks.Clear();
        Plugin.Errors.Clear();
        var placedState = new LevelGeneration.pPickupItemState { status = LevelGeneration.ePickupItemStatus.PlacedInLevel };
        var takenState = new LevelGeneration.pPickupItemState { status = LevelGeneration.ePickupItemStatus.PickedUp };
        OrdnanceCues.Played.Clear();
        Deliveries.SupplyAtPosition = true;
        SupplyPickupSound.Changed(placedState, takenState, true);
        SupplyPickupSound.Changed(placedState, placedState, false);
        SupplyPickupSound.Changed(takenState, takenState, false);
        check(OrdnanceCues.Played.Count == 0, "pickup recall, placement and repeated picked-up state do not replay supply sounds");
        Deliveries.SupplyAtPosition = false;
        SupplyPickupSound.Changed(placedState, takenState, false);
        check(OrdnanceCues.Played.Count == 0, "ordinary pickups outside delivered supply bays do not play Ordnance sounds");
        Deliveries.SupplyAtPosition = true;
        SupplyPickupSound.Changed(placedState, takenState, false);
        check(OrdnanceCues.Played.Count == 1 && OrdnanceCues.Played[0].Cue == "supply.pickup",
            "a settled supply pickup plays once at the source position");
        Deliveries.SupplyAtPosition = false;
        OrdnanceCues.Played.Clear();
    }
}
