using System;
using System.Globalization;
using ForgeMap.Fields;
using ForgeEngine.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The item spawn rules' native half (`forge.itemSpawn.*`): each rule becomes one more of the game's own per-zone
/// distribution jobs, queued into the factory's `Distribution` batch for the zone it names.
///
/// - `ResourceSpawns` (+ `PreferEmpty.Count` copies): `new LG_Distribute_ResourcePacksPerZone(zone, type, amount,
///   weights)` (dump.cs:689813), the job a zone's own resource packs are distributed by. The amount is the authored
///   multiplier times the expedition balance's own per-zone figure for that pack kind
///   (`RundownManager.ActiveExpeditionBalanceData`, `HealthPerZone` / `WeaponAmmoPerZone` / `ToolAmmoPerZone` /
///   `DisinfectionPerZone`), and the weights are the zone's own placement weights for that kind
///   (`ExpeditionZoneData.HealthPlacement` …), so an extra rule lands where the zone's own packs of that kind land.
/// - `ForceSpawn`: `new LG_Distribute_PickupItemsPerZone(zone, count, pickupType, itemId, weights)` (dump.cs:689772),
///   the job that carries an item id and zone placement weights. The pickup type follows the item block's own
///   inventory slot; a consumable job carries the zone's own consumable distribution block, as the zone's own
///   consumable job does.
///
/// The jobs are injected (`LG_Factory.InjectJob`, dump.cs) when a layer's `LG_DistributionSetup` job has finished
/// (batch `DistributionSetup`, before `Distribution` starts), for the zones of that job's own layer. Generation is
/// local and deterministic, so every peer reads the same documents and queues the same jobs in the same order; a
/// host-only injection would make the peers' levels differ. Nothing goes over the network.
/// </summary>
internal static class ItemSpawnApplier
{
    private static SystemObjectPlan? _plan;
    private static bool _faultReported;

    internal static void Activate(SystemObjectPlan plan) { _plan = plan.AnyItemSpawns ? plan : null; _faultReported = false; }

    internal static void Distribute(LG_DistributionSetup setup)
    {
        try
        {
            if (setup == null || _plan?.For(LevelIdentity.Read()) is not { HasItemSpawns: true } level) return;
            var layer = setup.m_layer;
            var zones = layer == null || layer.WasCollected ? null : layer.m_zones;
            if (zones == null) return;
            for (int i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                if (zone == null || zone.WasCollected || ZoneIndex.Coordinates(zone) is not { } at) continue;
                var key = new MapZoneKey(at.Dimension, at.Layer, at.Zone);
                foreach (var rule in level.ResourceSpawns)
                    if (rule.Zone == key) Resource(zone, rule);
                foreach (var rule in level.ForceSpawns)
                    if (rule.Zone == key) Force(zone, rule);
            }
        }
        catch (Exception error) { Fault(error); }
    }

    private static void Resource(LG_Zone zone, ResourceSpawnRule rule)
    {
        var data = zone.m_settings?.m_zoneData;
        var balance = RundownManager.ActiveExpeditionBalanceData;
        if (data == null || balance == null)
        {
            Refuse(rule.Object, "zone-data-missing", "the zone's own data block or the expedition balance did not read");
            return;
        }
        (float perZone, ZonePlacementWeights weights) = rule.PackType switch
        {
            0 => (balance.HealthPerZone, data.HealthPlacement),
            1 => (balance.WeaponAmmoPerZone, data.WeaponAmmoPlacement),
            2 => (balance.ToolAmmoPerZone, data.ToolAmmoPlacement),
            _ => (balance.DisinfectionPerZone, data.DisinfectionPlacement)
        };
        float amount = (float)(perZone * rule.Amount);
        if (!float.IsFinite(amount) || amount < 0)
        {
            Refuse(rule.Object, "resource-amount-invalid", "the native per-zone amount times the authored multiplier is not a finite non-negative quantity");
            return;
        }
        for (int copy = 0; copy < rule.Copies; copy++)
            LG_Factory.InjectJob(new LG_Distribute_ResourcePacksPerZone(zone, (eResourceContainerSpawnType)rule.PackType, amount, weights),
                LG_Factory.BatchName.Distribution);
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplied, "block=" + MapFieldSchema.ResourceSpawnBlock + " object=" + rule.Object
                + " zone=" + Zone(rule.Zone) + " PackType=" + rule.PackName + " amount=" + amount.ToString("R", CultureInfo.InvariantCulture)
                + " copies=" + rule.Copies,
                RuntimeLogValue.Of("block", MapFieldSchema.ResourceSpawnBlock), RuntimeLogValue.Quote("object", rule.Object));
    }

    private static void Force(LG_Zone zone, ForceSpawnRule rule)
    {
        var block = GameDataBlockBase<ItemDataBlock>.GetBlock(rule.ItemId);
        if (block == null)
        {
            Refuse(rule.Object, "item-block-missing", "item=" + rule.ItemId.ToString(CultureInfo.InvariantCulture));
            return;
        }
        int slot = (int)block.inventorySlot;
        if (PickupTypeOf(slot) is not { } type)
        {
            Refuse(rule.Object, "slot-not-distributable", "item=" + rule.ItemId.ToString(CultureInfo.InvariantCulture)
                + " slot=" + slot.ToString(CultureInfo.InvariantCulture));
            return;
        }
        var weights = new ZonePlacementWeights { Start = rule.Start, Middle = rule.Middle, End = rule.End };
        var job = new LG_Distribute_PickupItemsPerZone(zone, rule.Count, type, rule.ItemId, weights);
        if (type == ePickupItemType.Consumable && zone.m_settings?.m_zoneData is { } data)
            job.m_consumableDistributionData = GameDataBlockBase<ConsumableDistributionDataBlock>.GetBlock(data.ConsumableDistributionInZone);
        LG_Factory.InjectJob(job, LG_Factory.BatchName.Distribution);
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplied, "block=" + MapFieldSchema.ForceSpawnBlock + " object=" + rule.Object
                + " zone=" + Zone(rule.Zone) + " item=" + rule.ItemId.ToString(CultureInfo.InvariantCulture) + " type=" + type
                + " count=" + rule.Count,
                RuntimeLogValue.Of("block", MapFieldSchema.ForceSpawnBlock), RuntimeLogValue.Quote("object", rule.Object));
    }

    /// <summary>The pickup kind one inventory slot is distributed as: consumables (5, 6), pocket items (7) and
    /// carried items (8). Every other slot has no per-zone pickup distribution and is refused by name.</summary>
    internal static ePickupItemType? PickupTypeOf(int inventorySlot) => inventorySlot switch
    {
        5 or 6 => ePickupItemType.Consumable,
        7 => ePickupItemType.SmallGenericPickup,
        8 => ePickupItemType.BigGenericPickup,
        _ => null
    };

    private static string Zone(MapZoneKey key) => "gtfo.zone:" + key.Dimension.ToString(CultureInfo.InvariantCulture) + ":"
        + key.Layer.ToString(CultureInfo.InvariantCulture) + ":" + key.Zone.ToString(CultureInfo.InvariantCulture);

    private static void Refuse(string id, string code, string detail)
        => MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyRefused, "group=item-spawn object=" + id + " code=" + code + ": "
            + detail, RuntimeLogValue.Of("group", "item-spawn"), RuntimeLogValue.Quote("object", id), RuntimeLogValue.Of("code", code));

    /// <summary>A failure inside generation is reported once and adds nothing more; it never throws into the level
    /// builder.</summary>
    private static void Fault(Exception error)
    {
        if (_faultReported) return;
        _faultReported = true;
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyFailed, "group=item-spawn error=" + error.GetType().Name + ": "
            + error.Message, RuntimeLogValue.Of("group", "item-spawn"), RuntimeLogValue.Of("exception", error.GetType().Name));
    }
}

/// <summary>A layer's distribution setup has finished: the zone's containers are laid out and the `Distribution`
/// batch has not started, so the authored jobs are queued into it now.</summary>
[HarmonyPatch(typeof(LG_DistributionSetup), nameof(LG_DistributionSetup.Build))]
internal static class ItemSpawnDistribution
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_DistributionSetup __instance, bool __result)
    {
        if (__result) ItemSpawnApplier.Distribute(__instance);
    }
}

