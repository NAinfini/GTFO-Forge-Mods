using System;
using ForgeCore;
using GameData;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>One summoning ball that appeared in the world, as the game's own item replication reports it on every peer.</summary>
internal readonly record struct BeaconSpawn(Item Item, ulong Owner, Vector3 Position, ushort Replicator)
{
    internal bool Local => Owner != 0UL && Owner == OrdnanceSession.Local;
}

/// <summary>
/// The summoning ball as a game item: a copy of the glow stick (<c>ItemDataBlock</c> 114) with its own id, in the consumable slot
/// with exactly one charge. First person, third person and world instance all stay the glow stick's, so the game's own
/// <c>Gear.ThrowingWeapon</c> handles charging, the throw, replication and flight. Every peer registers the same block; it is a
/// fixed part of the wire contract, outside the definition set.
/// </summary>
internal static class BeaconItem
{
    /// <summary>Fixed 16-bit wire id, reserved only after checking the block and all loaded item prefab tables.</summary>
    internal const uint BlockId = Messages.BeaconItemId;
    internal const string BlockName = "FORGE_ORDNANCE_Beacon";
    private const uint GlowStickId = 114;
    private static ItemDataBlock? _registered;

    internal static bool Ready { get; private set; }
    /// <summary>Why call-ins are off on this peer because of the ball block, else null.</summary>
    internal static string? Failure { get; private set; }

    /// <summary>Every peer's ball spawn, after the game has created the instance. The hook for a custom look: swap the
    /// renderers of <see cref="BeaconSpawn.Item"/> here.</summary>
    internal static event Action<BeaconSpawn>? Spawned;
    // One controller owns local calls. The callback is captured at the native request, never at a later spawn notification.
    internal static Func<Action<ISyncedItem, PlayerAgent>?>? ThrowCallback;

    internal static void CaptureThrow(pItemData data, ref ItemReplicationManager.delItemCallback callback)
    {
        if (data.itemID_gearCRC != BlockId) return;
        var observed = ThrowCallback?.Invoke();
        if (observed == null) { Plugin.Error("A summoning ball throw had no active local call to bind."); return; }
        var original = callback;
        callback = new Action<ISyncedItem, PlayerAgent>((item, owner) =>
        {
            try { observed(item, owner); }
            catch (Exception error) { Plugin.Error("Identifying a thrown summoning ball failed: " + error); }
            if (original != null) original.Invoke(item, owner);
        });
    }

    /// <summary>Adds the block unless it is already there. Idempotent: it runs after game data initializes and again, as a
    /// guard, before the game builds its item prefab tables.</summary>
    internal static void Register()
    {
        try
        {
            var existing = ItemDataBlock.GetBlock(BlockId);
            if (_registered != null && existing != null && existing.Pointer == _registered.Pointer)
            {
                Ready = true;
                Failure = null;
                return;
            }
            if (existing != null)
                throw new InvalidOperationException("item id " + BlockId + " is already used by '" + existing.name + "'");
            var tables = ItemSpawnManager.m_loadedPrefabsPerItemMode;
            if (tables != null)
                for (var mode = 0; mode < tables.Length; mode++)
                {
                    var table = tables[mode];
                    if (table != null && table.ContainsKey(BlockId))
                        throw new InvalidOperationException("item prefab id " + BlockId + " is already registered in mode " + mode);
                }
            var source = ItemDataBlock.GetBlock(GlowStickId);
            if (source == null) throw new InvalidOperationException("the glow stick item block " + GlowStickId + " does not exist");
            var block = DataBlocks.Clone(source, BlockName, BlockId);
            block.inventorySlot = InventorySlot.Consumable;
            block.ConsumableAmmoMin = 1;
            block.ConsumableAmmoMax = 1;
            var added = DataBlocks.Add(Plugin.Id, block);
            if (added != DataBlockResult.Added || ItemDataBlock.GetBlock(BlockId)?.Pointer != block.Pointer)
                throw new InvalidOperationException("the block was not added (" + added + ")");
            _registered = block;
            Ready = true;
            Failure = null;
        }
        catch (Exception error)
        {
            Ready = false;
            Failure = "the summoning ball item could not be registered (" + error + ")";
            Plugin.Error("Forge Ordnance: " + Failure + "; call-ins are disabled.");
        }
    }

    internal static void NotifySpawn(pItemSpawnData spawnData, ItemReplicator replicator)
    {
        if (spawnData.itemData.itemID_gearCRC != BlockId) return;
        var item = replicator?.Item;
        if (replicator == null || item == null || item.WasCollected) { Plugin.Error("A summoning ball spawned without an item instance."); return; }
        ulong owner = 0UL;
        if (spawnData.owner.TryGetPlayer(out var player) && player != null) owner = player.Lookup;
        var id = new SNetwork.SNetStructs.pReplicator();
        id.SetID(replicator.TryCast<SNetwork.IReplicator>() ?? throw new InvalidOperationException("The ball has no native replicator identity."));
        var spawn = new BeaconSpawn(item, owner, spawnData.Position, id.keyPlusOne);
        Spawned?.Invoke(spawn);
    }
}

/// <summary>Subscribed to ForgeCore's <c>Items.Spawned</c>, the one patch of <c>ItemReplicationManager.OnItemSpawn</c>.</summary>
internal static class BeaconSpawnHook
{
    internal static void OnSpawned(pItemSpawnData spawnData, ItemReplicator replicator)
    {
        try { BeaconItem.NotifySpawn(spawnData, replicator); }
        catch (Exception error) { Plugin.Error("Summoning ball spawn hook failed: " + error); }
    }
}

/// <summary>The game builds its item prefab tables from the item blocks in these two places; the ball block must already be in
/// the list. <see cref="GTFO.API.GameDataAPI.OnGameDataInitialized"/> normally gets there first, so these only guard the order.</summary>
[HarmonyPatch(typeof(ItemSpawnManager), nameof(ItemSpawnManager.Setup))]
internal static class BeaconBeforeItemSetup
{
    [HarmonyPrefix]
    private static void Prefix() => BeaconItem.Register();
}

[HarmonyPatch(typeof(ItemSpawnManager), "SetupItemPrefabs")]
internal static class BeaconBeforeItemPrefabs
{
    [HarmonyPrefix]
    private static void Prefix() => BeaconItem.Register();
}

/// <summary>ThrowItem stores a unique native callbackID and invokes it before OnItemSpawn's postfix.
/// This substantial native method lets an expired A and a new B retain independent identities.</summary>
[HarmonyPatch(typeof(ItemReplicationManager), nameof(ItemReplicationManager.ThrowItem))]
internal static class BeaconThrowRequest
{
    [HarmonyPrefix]
    private static void Prefix(pItemData data, ref ItemReplicationManager.delItemCallback callback)
        => BeaconItem.CaptureThrow(data, ref callback);
}
