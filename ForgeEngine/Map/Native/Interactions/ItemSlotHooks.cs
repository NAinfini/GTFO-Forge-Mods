using System;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of `forge.trigger.interaction.item_placed` and `forge.trigger.interaction.item_removed`: the
/// game's own committed insertion and removal, and the reads that turn one of them into the three subjects the fact
/// carries.
///
/// Both entries are `CarryItemInteractionUtils` statics (global namespace, dump.cs 575422):
/// - `TryInsertItem(SNet_Player player, pItemData itemData, iCarryItemInteractionTarget itemInteractionTarget,
///   AlterCustomDataAction action)` (dump.cs 575430, RVA 0x1539A50). Its direct callers are the four receptacles'
///   own interaction bodies — `LG_PowerGenerator_Core.AttemptInteract` (dump.cs 695235), `LG_HSUActivator_Core
///   .AttemptInteract` (693462), `LG_DimensionPortal.AttemptInteract` (692676) and `LG_InteractWorldEventTrigger
///   .OnStateChange` (697188) — and its body returns `true` at once unless `Session.IsHost` (dump.cs 440630): only
///   the master takes the item out of the player's backpack and drops it at the host. A client's insertion reaches
///   the master through the receptacle's state replicator: `SNet_StateReplicator.InteractWithState` (dump.cs 442549)
///   sends the interaction to `SNet.Master` when this peer is not the master, and the master's replicator hands it
///   to the same `AttemptInteract`. The postfix therefore publishes on the master, once per committed insertion.
/// - `TryPickupRemovedItem(SNet_Player player, Item insertedLevelItem, AlterCustomDataAction action)` (dump.cs
///   575436, RVA 0x1539D60), whose one direct caller is `LG_PowerGenerator_Core.AttemptInteract` and whose body is
///   master-only in the same way. The HSU and the dimension portal declare removal callbacks
///   (`AttemptHSURemoved`, `AttemptPortalKeyRemoved`) whose bodies are the game's shared empty stub, so a power
///   cell taken out of its generator is the one removal the game performs. The entry takes no host, so the host
///   is the generator whose `AttemptInteract` is running: a prefix on that body names its own receptacle
///   (`m_powerCellInteraction`, dump.cs 695083) for the removal it calls, and a removal outside that body names no
///   host and is refused by the port's name.
///
/// The subjects are read in each entry's prefix, before the master's body moves the item:
/// - `host`: the insertion target itself, keyed by its zone and its own object identity exactly as the level
///   objects are (<see cref="LevelObjectObservation.KeyOf"/>), under the `host` category of `gtfo.map_object`.
/// - `item`: the level item, keyed by its pickup sync (`ItemInLevel.GetSyncComponent`, dump.cs 694836, a
///   `LG_PickupItem_Sync`, dump.cs 694747) exactly as the pickup facts key it. An insertion names the item by its
///   data, which the game's own validation resolves with `PlayerBackpackManager.TryGetItemInLevelFromItemData`
///   (dump.cs 658701); a removal is handed the level item itself.
/// - `actor`: the `SNet_Player` the entry is handed, named by the player identity.
/// A subject that does not read is handed on as null, and the publisher refuses the fact by that port's name.
/// </summary>
internal static class ItemSlotObservation
{
    /// <summary>The three subjects one insertion or removal names, read before the body ran.</summary>
    internal sealed record Subjects(string? Host, string? Item, EntityReference? Actor, EntityReference? ItemReference = null);

    /// <summary>The receptacle of the generator whose interaction body is running, for the removal that body
    /// performs. Set and cleared by <see cref="PowerGeneratorInteraction"/> on the simulation thread.</summary>
    internal static iCarryItemInteractionTarget? RemovalHost;

    internal static Subjects ReadInsertion(iCarryItemInteractionTarget? target, SNet_Player? player, pItemData itemData)
    {
        var (levelItem, reference) = ReadItem(itemData, "item-slot-hooks.read-insertion");
        return new Subjects(HostKey(target), levelItem, Actor(player), reference);
    }

    /// <summary>The captured level-item instance a replicated item record names, the reference the insertion and
    /// pickup facts publish; null when the record names no level item. The generator's cell fact reads its `item`
    /// port through this, so it is the same identity.</summary>
    internal static EntityReference? ReferenceOf(pItemData itemData) => ReadItem(itemData, "item-slot-hooks.reference-of").Reference;

    private static (string? Key, EntityReference? Reference) ReadItem(pItemData itemData, string site)
    {
        try
        {
            if (!PlayerBackpackManager.TryGetItemInLevelFromItemData(itemData, out var item)) return (null, null);
            var key = ItemKey(item, out var reference);
            return (key, reference);
        }
        catch (Exception containedError) { MapLog.Contained(site, containedError); return (null, null); }
    }

    internal static Subjects ReadRemoval(SNet_Player? player, Item? insertedLevelItem)
    {
        string? levelItem = null; EntityReference? reference = null;
        try { levelItem = ItemKey(insertedLevelItem, out reference); }
        catch (Exception containedError) { MapLog.Contained("item-slot-hooks.read-removal", containedError); levelItem = null; reference = null; }
        return new Subjects(HostKey(RemovalHost), levelItem, Actor(player), reference);
    }

    private static string? HostKey(iCarryItemInteractionTarget? target)
    {
        try
        {
            var component = target?.TryCast<UnityEngine.Component>();
            if (component == null) return null;
            // A target that is part of a shuttlebox the level built is that shuttlebox, named by its own map-object
            // address, so a plan mounted on the shuttlebox hears the insertion; any other target is a generic host.
            if (ShuttleboxRegistry.AddressOfPart(component.transform) is { } shuttlebox) return shuttlebox.ToString();
            return LevelObjectObservation.KeyOf(component, ItemSlotContract.HostCategory,
                LevelObjectObservation.SerialOf(component.gameObject));
        }
        catch (Exception containedError) { MapLog.Contained("item-slot-hooks.host-key", containedError); return null; }
    }

    /// <summary>The item identity of the pickup fact (<see cref="LevelObjectObservation.ItemIdentityKey"/>) and the
    /// captured instance it names. An item the level-object half cannot capture has no shared identity and no key.</summary>
    private static string? ItemKey(Item? item, out EntityReference? reference)
    {
        reference = null;
        var inLevel = item?.TryCast<ItemInLevel>();
        var sync = inLevel?.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (sync == null || MapHost.Session is not { Levels: { } module } session) return null;
        session.SpawnRules?.Flush();
        reference = LevelInstanceObservation.Item(sync.item, module);
        return reference == null || LevelObjectObservation.SerialOf(sync.gameObject) is not { } serial ? null
            : LevelObjectObservation.ItemIdentityKey(serial, reference, module);
    }

    private static EntityReference? Actor(SNet_Player? player)
    {
        var session = MapHost.Session;
        if (player == null || session == null) return null;
        return session.TryOptionalPlayerService(static (s, value) => s.ReferenceOf(value), player,
            "item-slot-actor", out EntityReference? reference) ? reference : null;
    }
}

// The committed insertion. The body answers whether the item went in; a refused insertion publishes nothing.
[HarmonyPatch(typeof(CarryItemInteractionUtils), nameof(CarryItemInteractionUtils.TryInsertItem))]
internal static class CarryItemInserted
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static bool Prefix(SNet_Player player, pItemData itemData, iCarryItemInteractionTarget itemInteractionTarget,
        ref bool __result, out ItemSlotObservation.Subjects? __state)
    {
        __state = null;
        try
        {
            if (Session.IsHost && FixedItemRules.Refuses(itemInteractionTarget, itemData)) { __result = false; return false; }
        }
        catch (Exception containedError) { MapLog.Contained("item-slot-hooks.insert-rule", containedError); }
        __state = ItemSlotObservation.ReadInsertion(itemInteractionTarget, player, itemData);
        return true;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(bool __result, ItemSlotObservation.Subjects? __state)
    {
        if (!__result || __state == null) return;
        if (MapHost.Session is { } session)
            session.Run(session.ItemSlots, facts => facts.ItemPlaced(__state.Host, __state.Item, __state.Actor, __state.ItemReference),
                "item-slot observation");
    }
}

// The committed removal. The body answers whether the item came out; a refused removal publishes nothing.
[HarmonyPatch(typeof(CarryItemInteractionUtils), nameof(CarryItemInteractionUtils.TryPickupRemovedItem))]
internal static class CarryItemRemoved
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Prefix(SNet_Player player, Item insertedLevelItem, out ItemSlotObservation.Subjects? __state)
        => __state = ItemSlotObservation.ReadRemoval(player, insertedLevelItem);

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(bool __result, ItemSlotObservation.Subjects? __state)
    {
        if (!__result || __state == null) return;
        if (MapHost.Session is { } session)
            session.Run(session.ItemSlots, facts => facts.ItemRemoved(__state.Host, __state.Item, __state.Actor, __state.ItemReference),
                "item-slot observation");
    }
}

// The generator's own interaction body: the one caller of the removal, whose receptacle is the removal's host.
[HarmonyPatch(typeof(LG_PowerGenerator_Core), nameof(LG_PowerGenerator_Core.AttemptInteract))]
internal static class PowerGeneratorInteraction
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(LG_PowerGenerator_Core __instance)
    {
        try { ItemSlotObservation.RemovalHost = __instance.m_powerCellInteraction; }
        catch (Exception containedError) { MapLog.Contained("item-slot-hooks.removal-host", containedError); ItemSlotObservation.RemovalHost = null; }
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix() => ItemSlotObservation.RemovalHost = null;
}
