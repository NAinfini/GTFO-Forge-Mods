using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ChainedPuzzles;
using Enemies;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>
/// The detail half of a snapshot: sections only a snapshot writes, because they answer questions asked at a moment
/// (after a reload, a throw, a door alarm) rather than per frame. Weapons are read from each owner's backpack, which is
/// the game's own inventory table and covers bots and remote players; deployables, thrown items and scan routes from
/// the active scene; waves from the mastermind's event list and the doors' wave data. Every read goes through
/// <see cref="Safe(Func{string?})"/>, so a renamed member is a null field, not a lost snapshot.
/// </summary>
internal static partial class CaptureWorld
{
    private const int MaxRoutePositions = 64;

    private static IEnumerable<int> FillDetail(CaptureSnapshot snapshot, CaptureOptions options, LevelObjectSet level, Dictionary<int, LG_Zone> zoneOf)
    {
        foreach (var step in FillSession(snapshot, options)) yield return step;
        foreach (var step in FillFlashlights(snapshot, options, zoneOf)) yield return step;
        foreach (var step in FillWeapons(snapshot, options)) yield return step;
        foreach (var step in FillHolders(snapshot, options)) yield return step;
        if (options.IncludeLoadout) foreach (var step in FillLoadout(snapshot, options)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Deployables, "Object.FindObjectsOfType<SentryGunInstance>", Objects<SentryGunInstance>(), zoneOf, SentryRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Deployables, "Object.FindObjectsOfType<MineDeployerInstance>", Objects<MineDeployerInstance>(), zoneOf, MineRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.ThrownItems, "Object.FindObjectsOfType<GrenadeBase>", Objects<GrenadeBase>(), zoneOf, ThrownRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.ThrownItems, "Object.FindObjectsOfType<GlueGrenadeInstance>", Objects<GlueGrenadeInstance>(), zoneOf, ThrownRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.ThrownItems, "Object.FindObjectsOfType<GlowstickInstance>", Objects<GlowstickInstance>(), zoneOf, ThrownRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Waves, "Mastermind.Current.m_events: SurvivalWave", SurvivalWaves(), zoneOf, (wave, _) => WaveRow(wave, zoneOf))) yield return step;
        FillMastermind(snapshot);
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Waves, "LG_Floor hierarchy: LG_SecurityDoor.ActiveEnemyWaveData", level.Doors, zoneOf, DoorWaveRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.EnemyDetail, "Enemy lifecycle index", LiveEnemies(), zoneOf, EnemyDetailRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.ScanRoutes, "Object.FindObjectsOfType<CP_BasicMovable>", Objects<CP_BasicMovable>(), zoneOf, ScanRouteRow)) yield return step;
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Objectives, "LG_Floor hierarchy: LG_DimensionPortal", level.Portals, zoneOf, PortalRow)) yield return step;
    }

    /// <summary>The live light that the inventory applied on wield, alongside the gear/part identities. Reading
    /// a settings DataBlock alone cannot prove the rendered light changed, or that a second gear stayed unchanged.</summary>
    private static IEnumerable<int> FillFlashlights(CaptureSnapshot snapshot, CaptureOptions options, Dictionary<int, LG_Zone> zoneOf)
    {
        yield return 0;
        foreach (var step in Fill(snapshot, options, CaptureSections.Environment, "PlayerManager.PlayerAgentsInLevel.Inventory.m_flashlight",
            PlayerAgents(), zoneOf, FlashlightRow)) yield return step;
    }

    private static CaptureRow FlashlightRow(PlayerAgent player, LG_Zone? zone)
    {
        var row = new CaptureRow("playerFlashlight", "playerFlashlight/" + Safe(() => player.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture), "?"), "inventory flashlight");
        row.Field("owner", Safe(() => player.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture)));
        row.Field("entity", Safe(() => EntityOf(player)));
        row.Field("enabled", Safe(() => player.Inventory?.FlashlightEnabled.ToString()));
        row.Field("wielded", Safe(() => player.Inventory?.WieldedItem == null ? "none" : EntityOf(player.Inventory.WieldedItem)));
        row.Field("wieldedPointer", Safe(() => player.Inventory?.WieldedItem?.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)));
        row.Field("partPointer", Safe(() => player.Inventory?.m_currentGearPartFlashlight?.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)));
        row.Field("settingsId", Safe(() => player.Inventory?.m_currentGearPartFlashlight?.m_settingsID.ToString(CultureInfo.InvariantCulture)));
        row.Field("lightPointer", Safe(() => player.Inventory?.m_flashlight?.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)));
        row.Field("lightEnabled", Safe(() => player.Inventory?.m_flashlight?.enabled.ToString()));
        row.Field("intensity", Safe(() => player.Inventory?.m_flashlight?.intensity.ToString(CultureInfo.InvariantCulture)));
        row.Field("range", Safe(() => player.Inventory?.m_flashlight?.range.ToString(CultureInfo.InvariantCulture)));
        row.Field("spotAngle", Safe(() => player.Inventory?.m_flashlight?.spotAngle.ToString(CultureInfo.InvariantCulture)));
        row.Field("color", Safe(() => player.Inventory?.m_flashlight == null ? null : Color(player.Inventory.m_flashlight.color)));
        return row;
    }

    private static CaptureRow PortalRow(LG_DimensionPortal portal, LG_Zone? zone)
    {
        var row = new CaptureRow("dimensionPortal", "portal/" + Safe(() => portal.m_serialNumber.ToString(CultureInfo.InvariantCulture), "?"), "dimension portal");
        row.Field("serial", Safe(() => portal.m_serialNumber.ToString(CultureInfo.InvariantCulture)));
        row.Field("pointer", Safe(() => portal.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)));
        row.Field("instanceId", Safe(() => portal.GetInstanceID().ToString(CultureInfo.InvariantCulture)));
        row.Field("entity", Safe(() => EntityOf(portal)));
        row.Field("targetDimension", Safe(() => portal.m_targetDimension.ToString()));
        row.Field("position", Safe(() => Vec(portal.transform.position)));
        row.Field("state", Safe(() => RecReflect.Describe(portal.m_stateReplicator?.State)));
        return row;
    }

    /// <summary>The session's player list as the network layer holds it: which slot is this machine, which is the
    /// master, which is a bot. A presentation step addressed to one player is only checkable against this list.</summary>
    private static IEnumerable<int> FillSession(CaptureSnapshot snapshot, CaptureOptions options)
    {
        var section = snapshot.Section(CaptureSections.Session, "SNet.SessionHub.PlayersInSession");
        Il2CppSystem.Collections.Generic.List<SNet_Player>? players;
        try { players = SNet.SessionHub == null ? null : SNet.SessionHub.PlayersInSession; }
        catch (Exception error) { Log("session", error); yield break; }
        if (players == null) yield break;
        for (var i = 0; i != players.Count; i++)
        {
            yield return 0;
            if (section.Rows.Count >= options.MaxRowsPerSection) { Note(snapshot, section, players.Count); break; }
            var player = players[i];
            if (player == null || player.WasCollected) continue;
            var row = new CaptureRow("sessionPlayer", "sessionPlayer/" + i.ToString(CultureInfo.InvariantCulture), Safe(() => player.NickName) ?? "player");
            row.Field("slot", Safe(() => player.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture)));
            row.Field("local", Safe(() => player.IsLocal.ToString()));
            row.Field("master", Safe(() => player.IsMaster.ToString()));
            row.Field("bot", Safe(() => player.IsBot.ToString()));
            row.Field("inSlot", Safe(() => player.IsInSlot.ToString()));
            section.Rows.Add(row);
        }
    }

    /// <summary>Every player whose backpack the manager may hold: the agents in the level (bots included) and the
    /// session list (the lobby, where no agent exists yet). One player is listed once.</summary>
    private static List<SNet_Player> BackpackOwners()
    {
        var owners = new List<SNet_Player>();
        var seen = new HashSet<IntPtr>();
        try
        {
            var agents = PlayerAgents();
            if (agents != null)
                for (var i = 0; i != agents.Count; i++) AddOwner(owners, seen, agents[i].Owner);
            var session = SNet.SessionHub == null ? null : SNet.SessionHub.PlayersInSession;
            if (session != null)
                for (var i = 0; i != session.Count; i++) AddOwner(owners, seen, session[i]);
        }
        catch (Exception error) { Log("backpack_owners", error); }
        return owners;
    }

    private static void AddOwner(List<SNet_Player> owners, HashSet<IntPtr> seen, SNet_Player? owner)
    {
        if (owner != null && !owner.WasCollected && seen.Add(owner.Pointer)) owners.Add(owner);
    }

    private static IEnumerable<int> FillWeapons(CaptureSnapshot snapshot, CaptureOptions options)
    {
        var section = snapshot.Section(CaptureSections.Weapons, "PlayerBackpackManager.TryGetBackpack(owner).Slots");
        foreach (var owner in BackpackOwners())
        {
            yield return 0;
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BackpackItem>? slots;
            try { slots = PlayerBackpackManager.TryGetBackpack(owner, out var backpack) && backpack != null ? backpack.Slots : null; }
            catch (Exception error) { Log("backpack", error); continue; }
            if (slots == null) continue;
            var ownerSlot = Safe(() => owner.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture), "?");
            for (var i = 0; i != slots.Length; i++)
            {
                yield return 0;
                if (section.Rows.Count >= options.MaxRowsPerSection) { Note(snapshot, section, slots.Length); yield break; }
                var item = slots[i];
                if (item == null) continue;
                section.Rows.Add(WeaponRow(ownerSlot, i, item));
            }
        }
    }

    private static CaptureRow WeaponRow(string ownerSlot, int index, BackpackItem item)
    {
        var slotIndex = index.ToString(CultureInfo.InvariantCulture);
        var row = new CaptureRow("weapon", "weapon/" + ownerSlot + "/" + slotIndex, Safe(() => item.Name) ?? "item");
        row.Field("owner", ownerSlot);
        row.Field("slotIndex", slotIndex);
        row.Field("itemId", Safe(() => item.ItemID.ToString(CultureInfo.InvariantCulture)));
        row.Field("status", Safe(() => item.Status.ToString()));
        row.Field("isLoaded", Safe(() => item.IsLoaded.ToString()));
        row.Field("gearInstanceText", Safe(() => item.GearIDRange == null ? "none" : item.GearIDRange.PlayfabItemInstanceId));
        row.Field("gearItemId", Safe(() => item.GearIDRange == null ? "none" : item.GearIDRange.PlayfabItemId));
        global::Item? instance = null;
        try { instance = item.Instance; }
        catch (Exception error) { Log("backpack_item", error); }
        if (instance == null || instance.WasCollected) { row.Field("instance", "none"); return row; }
        row.Field("instance", Safe(() => RecReflect.Describe(instance)));
        ItemFields(row, instance);
        return row;
    }

    /// <summary>What a live item answers about itself: its IL2CPP class, owner, the two slot numbers and the two item
    /// ids the game keeps apart, the Forge entity it resolves to, and for equippables the archetype and part state
    /// firing and animation read.</summary>
    private static void ItemFields(CaptureRow row, global::Item instance)
    {
        row.Field("class", Safe(() => NativeClass(instance)));
        row.Field("ownerAgentSlot", Safe(() => instance.Owner == null ? "none" : instance.Owner.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture)));
        row.Field("itemSlot", Safe(() => instance.pItemData.slot.ToString()));
        row.Field("itemIdGearCrc", Safe(() => instance.pItemData.itemID_gearCRC.ToString(CultureInfo.InvariantCulture)));
        row.Field("dataBlockId", Safe(() => instance.ItemDataBlock == null ? "none" : instance.ItemDataBlock.persistentID.ToString(CultureInfo.InvariantCulture)));
        row.Field("dataBlockSlot", Safe(() => instance.ItemDataBlock == null ? "none" : instance.ItemDataBlock.inventorySlot.ToString()));
        row.Field("entity", Safe(() => EntityOf(instance)));
        var equippable = instance.TryCast<ItemEquippable>();
        if (equippable == null) return;
        row.Field("wieldedFlag", Safe(() => equippable.m_isWielded.ToString()));
        row.Field("firstPerson", Safe(() => equippable.m_isFirstPerson.ToString()));
        row.Field("clip", Safe(() => equippable.GetCurrentClip().ToString(CultureInfo.InvariantCulture)));
        row.Field("clipMax", Safe(() => equippable.GetMaxClip().ToString(CultureInfo.InvariantCulture)));
        // Id and pointer together: a clone of the block keeps the id and changes the pointer, which is the question
        // an override readback asks.
        row.Field("archetype", Safe(() => equippable.ArchetypeData == null ? "none"
            : BlockIdentity(equippable.ArchetypeData.persistentID, equippable.ArchetypeData.Pointer)));
        row.Field("archetypeShotDelay", Safe(() => equippable.ArchetypeData == null ? null : equippable.ArchetypeData.ShotDelay.ToString(CultureInfo.InvariantCulture)));
        row.Field("archetypeReloadTime", Safe(() => equippable.ArchetypeData == null ? null : equippable.ArchetypeData.DefaultReloadTime.ToString(CultureInfo.InvariantCulture)));
        row.Field("archetypeBurst", Safe(() => equippable.ArchetypeData == null ? null : equippable.ArchetypeData.BurstShotCount.ToString(CultureInfo.InvariantCulture)));
        row.Field("archetypeHipFireSpread", Safe(() => equippable.ArchetypeData?.HipFireSpread.ToString(CultureInfo.InvariantCulture)));
        row.Field("archetypeAimSpread", Safe(() => equippable.ArchetypeData?.AimSpread.ToString(CultureInfo.InvariantCulture)));
        row.Field("archetypeShotgunConeSize", Safe(() => equippable.ArchetypeData?.ShotgunConeSize.ToString(CultureInfo.InvariantCulture)));
        row.Field("recoil", Safe(() => equippable.RecoilData == null ? "none"
            : BlockIdentity(equippable.RecoilData.persistentID, equippable.RecoilData.Pointer)));
        row.Field("recoilHorizontalMin", Safe(() => equippable.RecoilData?.horizontalScale?.Min.ToString(CultureInfo.InvariantCulture)));
        row.Field("recoilVerticalMin", Safe(() => equippable.RecoilData?.verticalScale?.Min.ToString(CultureInfo.InvariantCulture)));
        row.Field("recoilSpring", Safe(() => equippable.RecoilData?.spring.ToString(CultureInfo.InvariantCulture)));
        row.Field("recoilPowerMin", Safe(() => equippable.RecoilData?.power?.Min.ToString(CultureInfo.InvariantCulture)));
        row.Field("parts", Safe(() => PartTransforms(equippable.GearPartHolder)));
        row.Field("flashlightPartPointer", Safe(() => equippable.AttachedFlashlight?.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)));
        row.Field("flashlightSettingsId", Safe(() => equippable.AttachedFlashlight?.m_settingsID.ToString(CultureInfo.InvariantCulture)));
        var bullet = equippable.TryCast<global::Gear.BulletWeapon>();
        if (bullet != null)
        {
            row.Field("weaponArchetype", Safe(() => bullet.m_archeType == null ? "none"
                : NativeClass(bullet.m_archeType) + "@" + bullet.m_archeType.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)));
            row.Field("weaponArchetypeData", Safe(() => bullet.m_archeType == null || bullet.m_archeType.m_archetypeData == null ? "none"
                : BlockIdentity(bullet.m_archeType.m_archetypeData.persistentID, bullet.m_archeType.m_archetypeData.Pointer)));
            row.Field("weaponArchetypeShotDelay", Safe(() => bullet.m_archeType == null || bullet.m_archeType.m_archetypeData == null ? null
                : bullet.m_archeType.m_archetypeData.ShotDelay.ToString(CultureInfo.InvariantCulture)));
            row.Field("burstMax", Safe(() => bullet.m_burstMax.ToString(CultureInfo.InvariantCulture)));
            row.Field("burstCurrent", Safe(() => bullet.m_burstCurrentCount.ToString(CultureInfo.InvariantCulture)));
            row.Field("burstFiring", Safe(() => bullet.IsCurrentlyBurstFiring.ToString()));
            row.Field("lastFireTime", Safe(() => bullet.m_lastFireTime.ToString(CultureInfo.InvariantCulture)));
        }
        var melee = equippable.TryCast<global::Gear.MeleeWeaponFirstPerson>();
        if (melee != null)
        {
            row.Field("meleeState", Safe(() => melee.CurrentStateName.ToString()));
            row.Field("meleeAnimation", Safe(() => melee.MeleeAnimationData == null ? "none"
                : BlockIdentity(melee.MeleeAnimationData.persistentID, melee.MeleeAnimationData.Pointer)));
            row.Field("meleeMaxDamageChargeTime", Safe(() => melee.MeleeAnimationData == null ? null
                : melee.MeleeAnimationData.MaxDamageChargeTime.ToString(CultureInfo.InvariantCulture)));
            row.Field("meleeSpeedModifier", Safe(() => melee.MeleeSpeedModifier.ToString(CultureInfo.InvariantCulture)));
            // Read the active native state's cached threshold as well as the definition. An override/restore can
            // update one without the other. No charge state means unavailable, never zero or an invented false.
            row.Field("meleeChargeElapsed", Safe(() => melee.CurrentState?.TryCast<global::Gear.MWS_ChargeUp>()?.m_elapsed.ToString(CultureInfo.InvariantCulture)));
            row.Field("meleeChargeMaxDamageTime", Safe(() => melee.CurrentState?.TryCast<global::Gear.MWS_ChargeUp>()?.m_maxDamageTime.ToString(CultureInfo.InvariantCulture)));
            row.Field("meleeChargeDone", Safe(() => melee.CurrentState?.TryCast<global::Gear.MWS_ChargeUp>()?.m_chargeUpDone.ToString()));
        }
        var remoteMelee = equippable.TryCast<global::Gear.MeleeWeaponThirdPerson>();
        if (remoteMelee != null)
        {
            row.Field("isCharging", Safe(() => remoteMelee.IsCharging.ToString()));
            row.Field("isDischarging", Safe(() => remoteMelee.IsDischarging.ToString()));
        }
        var thrower = equippable.TryCast<ThrowingWeapon>();
        if (thrower != null)
        {
            row.Field("hasThrown", Safe(() => thrower.m_hasThrown.ToString()));
            row.Field("inThrowCharge", Safe(() => thrower.m_inThrowCharge.ToString()));
            row.Field("inThrowRelease", Safe(() => thrower.m_inThrowRelease.ToString()));
            row.Field("throwStrength", Safe(() => thrower.m_throwStrength.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string BlockIdentity(uint persistentId, IntPtr pointer)
        => persistentId.ToString(CultureInfo.InvariantCulture) + "@" + pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Each gear part's local position and rotation, in one field, so a part an animation or an override
    /// moved shows up as a changed field between two snapshots.</summary>
    private static string PartTransforms(global::Gear.GearPartHolder? holder)
    {
        if (holder == null || holder.WasCollected) return "none";
        var text = new StringBuilder(256);
        Part(text, "front", holder.FrontPart);
        Part(text, "receiver", holder.ReceiverPart);
        Part(text, "receiverAttachment", holder.ReceiverPartAttachment);
        Part(text, "stock", holder.StockPart);
        Part(text, "sight", holder.SightPart);
        Part(text, "mag", holder.MagPart);
        Part(text, "flashlight", holder.FlashlightPart);
        Part(text, "toolMain", holder.ToolMainPart);
        Part(text, "toolMainAttachment", holder.ToolMainPartAttachment);
        Part(text, "toolGrip", holder.ToolGripPart);
        Part(text, "toolDelivery", holder.ToolDeliveryPart);
        Part(text, "toolDeliveryAttachment", holder.ToolDeliveryPartAttachment);
        Part(text, "toolPayload", holder.ToolPayloadPart);
        Part(text, "toolTargeting", holder.ToolTargetingPart);
        Part(text, "toolScreen", holder.ToolScreenPart);
        Part(text, "meleeHead", holder.MeleeHeadPart);
        Part(text, "meleeNeck", holder.MeleeNeckPart);
        Part(text, "meleeHandle", holder.MeleeHandlePart);
        Part(text, "meleePommel", holder.MeleePommelPart);
        return text.Length == 0 ? "none" : text.ToString();
    }

    private static void Part(StringBuilder text, string name, GameObject? part)
    {
        if (part == null || part.WasCollected) return;
        var transform = part.transform;
        if (text.Length != 0) text.Append(';');
        text.Append(name).Append('@').Append(Vec(transform.localPosition)).Append('/').Append(Vec(transform.localEulerAngles));
    }

    /// <summary>One row per player agent for what it holds: the inventory's class (local or synced), the wielded
    /// item's real class and entity, and the first-person holder, which only the machine that drives it has.</summary>
    private static IEnumerable<int> FillHolders(CaptureSnapshot snapshot, CaptureOptions options)
    {
        var section = snapshot.Section(CaptureSections.Weapons, "PlayerManager.PlayerAgentsInLevel: Inventory + FPItemHolder");
        var agents = PlayerAgents();
        if (agents == null) yield break;
        for (var i = 0; i != agents.Count; i++)
        {
            yield return 0;
            if (section.Rows.Count >= options.MaxRowsPerSection) { Note(snapshot, section, agents.Count); break; }
            var player = agents[i];
            if (player.WasCollected) continue;
            var slot = Safe(() => player.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture), "?");
            var row = new CaptureRow("weaponHolder", "weaponHolder/" + slot, Safe(() => player.PlayerName) ?? "player");
            row.Field("inventoryClass", Safe(() => player.Inventory == null ? "none" : NativeClass(player.Inventory)));
            row.Field("wielded", Safe(() => player.Inventory == null || player.Inventory.WieldedItem == null ? "none" : RecReflect.Describe(player.Inventory.WieldedItem)));
            row.Field("wieldedClass", Safe(() => player.Inventory == null || player.Inventory.WieldedItem == null ? "none" : NativeClass(player.Inventory.WieldedItem)));
            row.Field("wieldedSlot", Safe(() => player.Inventory == null ? null : player.Inventory.WieldedSlot.ToString()));
            row.Field("wieldedEntity", Safe(() => player.Inventory == null || player.Inventory.WieldedItem == null ? "none" : EntityOf(player.Inventory.WieldedItem)));
            row.Field("fpHolder", Safe(() => player.FPItemHolder == null ? "none" : RecReflect.Describe(player.FPItemHolder)));
            row.Field("fpItemState", Safe(() => player.FPItemHolder == null || player.FPItemHolder.CurrentItemState == null ? "none" : NativeClass(player.FPItemHolder.CurrentItemState)));
            row.Field("fpAimTrigger", Safe(() => player.FPItemHolder == null ? null : player.FPItemHolder.ItemAimTrigger.ToString()));
            row.Field("playerEntity", Safe(() => EntityOf(player)));
            section.Rows.Add(row);
        }
    }

    /// <summary>The gear pool the lobby offers per slot, with the instance id text each entry carries. Online and
    /// offline setups fill it from different sources, which is what the manager row says.</summary>
    private static IEnumerable<int> FillLoadout(CaptureSnapshot snapshot, CaptureOptions options)
    {
        var section = snapshot.Section(CaptureSections.Weapons, "Gear.GearManager.Current.m_gearPerSlot");
        {
            var manager = global::Gear.GearManager.Current;
            if (manager == null) yield break;
            var head = new CaptureRow("loadout", "loadout/manager", "gear manager");
            head.Field("offlineSetupDone", Safe(() => manager.m_offlineSetupDone.ToString()));
            head.Field("playfabLoggedIn", Safe(() => manager.m_playfabLoggedIn.ToString()));
            section.Rows.Add(head);
            var perSlot = manager.m_gearPerSlot;
            if (perSlot == null) yield break;
            for (var slot = 0; slot != perSlot.Length; slot++)
            {
                yield return 0;
                var gear = perSlot[slot];
                if (gear == null) continue;
                for (var i = 0; i != gear.Count; i++)
                {
                    yield return 0;
                    if (section.Rows.Count >= options.MaxRowsPerSection) { Note(snapshot, section, gear.Count); yield break; }
                    var range = gear[i];
                    if (range == null) continue;
                    var row = new CaptureRow("loadout",
                        "loadout/" + slot.ToString(CultureInfo.InvariantCulture) + "/" + i.ToString(CultureInfo.InvariantCulture),
                        Safe(() => range.PlayfabItemName) ?? "gear");
                    row.Field("slot", slot.ToString(CultureInfo.InvariantCulture));
                    row.Field("instanceText", Safe(() => range.PlayfabItemInstanceId));
                    row.Field("itemId", Safe(() => range.PlayfabItemId));
                    section.Rows.Add(row);
                }
            }
        }

    }

    private static CaptureRow SentryRow(SentryGunInstance sentry, LG_Zone? zone)
    {
        var row = DeployableRow(sentry, "sentry");
        row.Field("groundOffset", Safe(() => Vec(sentry.GroundOffset)));
        row.Field("ammo", Safe(() => sentry.Ammo.ToString(CultureInfo.InvariantCulture)));
        row.Field("alive", Safe(() => sentry.Alive.ToString()));
        row.Field("locallyOwned", Safe(() => sentry.IsLocallyOwned.ToString()));
        row.Field("deployed", Safe(() => sentry.m_isDeployed.ToString()));
        row.Field("replicator", Safe(() => sentry.Replicator == null ? "none" : RecReflect.Describe(sentry.Replicator)));
        return row;
    }

    private static CaptureRow MineRow(MineDeployerInstance mine, LG_Zone? zone)
    {
        var row = DeployableRow(mine, "mine");
        row.Field("mode", Safe(() => mine.Mode.ToString()));
        row.Field("alive", Safe(() => mine.Alive.ToString()));
        row.Field("replicator", Safe(() => mine.Replicator == null ? "none" : RecReflect.Describe(mine.Replicator)));
        row.Field("courseNode", Safe(() => mine.CourseNode == null ? null : mine.CourseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        return row;
    }

    /// <summary>The identity a placed deployable carries on each machine: native pointer, sync id, owner slot, who
    /// placed it and the Forge entity. Two machines' rows for one deployable are matched by sync id and position.</summary>
    private static CaptureRow DeployableRow(ItemEquippable item, string family)
    {
        var row = new CaptureRow("deployable", "deployable/" + Safe(() => item.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"), family);
        row.Field("pointer", item.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture));
        row.Field("syncId", Safe(() => item.SyncID.ToString(CultureInfo.InvariantCulture)));
        row.Field("owner", Safe(() => item.Owner == null ? "none" : item.Owner.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture)));
        row.Field("locallyPlaced", Safe(() => item.LocallyPlaced.ToString()));
        row.Field("gearInstanceText", Safe(() => item.GearIDRange == null ? "none" : item.GearIDRange.PlayfabItemInstanceId));
        row.Field("position", Safe(() => Vec(item.transform.position)));
        row.Field("rotation", Safe(() => Vec(item.transform.eulerAngles)));
        row.Field("parts", Safe(() => PartTransforms(item.GearPartHolder)));
        row.Field("entity", Safe(() => EntityOf(item)));
        return row;
    }

    private static CaptureRow ThrownRow(global::Item item, LG_Zone? zone)
    {
        var row = new CaptureRow("thrownItem", "thrownItem/" + Safe(() => item.GetInstanceID().ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => NativeClass(item)) ?? "item");
        row.Field("pointer", item.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture));
        row.Field("syncId", Safe(() => item.SyncID.ToString(CultureInfo.InvariantCulture)));
        row.Field("owner", Safe(() => item.Owner == null ? "none" : item.Owner.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture)));
        row.Field("itemIdGearCrc", Safe(() => item.pItemData.itemID_gearCRC.ToString(CultureInfo.InvariantCulture)));
        row.Field("position", Safe(() => Vec(item.transform.position)));
        row.Field("entity", Safe(() => EntityOf(item)));
        return row;
    }

    /// <summary>The survival waves the mastermind holds. They are read from its event list rather than the scene,
    /// because the list is what the game iterates, finished waves included. The console's alarm readback reads the same list.</summary>
    internal static List<SurvivalWave>? SurvivalWaves()
    {
        try
        {
            var mastermind = Mastermind.Current;
            var events = mastermind == null ? null : mastermind.m_events;
            if (events == null) return null;
            var waves = new List<SurvivalWave>(events.Count);
            for (var i = 0; i != events.Count; i++)
            {
                var wave = events[i] == null ? null : events[i].TryCast<SurvivalWave>();
                if (wave != null && !wave.WasCollected) waves.Add(wave);
            }
            return waves;
        }
        catch (Exception error) { Log("waves", error); return null; }
    }

    private static CaptureRow WaveRow(SurvivalWave wave, Dictionary<int, LG_Zone> zoneOf)
    {
        var row = new CaptureRow("wave", "wave/" + Safe(() => wave.EventID.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => wave.m_settings == null ? null : wave.m_settings.name) ?? "wave");
        row.Field("state", Safe(() => wave.m_state.ToString()));
        row.Field("eventType", Safe(() => wave.EventType.ToString()));
        row.Field("settings", Safe(() => wave.m_settings == null ? "none" : wave.m_settings.persistentID.ToString(CultureInfo.InvariantCulture)));
        row.Field("population", Safe(() => wave.m_populationDataID.ToString(CultureInfo.InvariantCulture)));
        row.Field("spawnType", Safe(() => wave.m_spawnType.ToString()));
        row.Field("courseNode", Safe(() => wave.m_courseNode == null ? "none" : wave.m_courseNode.NodeID.ToString(CultureInfo.InvariantCulture)));
        row.Field("pointsSpent", Safe(() => wave.m_populationPointsSpentTotal.ToString(CultureInfo.InvariantCulture)));
        row.Field("startedAt", Safe(() => wave.m_eventStartTime.ToString(CultureInfo.InvariantCulture)));
        // A wave is not under a zone in the hierarchy; its spawn node says where it is.
        row.Zone = Safe(() => ZoneOfNode(wave.m_courseNode, zoneOf)?.AliasName);
        return row;
    }

    private static void FillMastermind(CaptureSnapshot snapshot)
    {
        var section = snapshot.Section(CaptureSections.Waves, "Mastermind.Current");
        Mastermind? mastermind;
        try { mastermind = Mastermind.Current; }
        catch (Exception error) { Log("mastermind", error); return; }
        if (mastermind == null || mastermind.WasCollected) return;
        var row = new CaptureRow("mastermind", "mastermind/current", "mastermind");
        row.Field("events", Safe(() => mastermind.m_events == null ? null : mastermind.m_events.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("activeGroups", Safe(() => mastermind.m_activeGroups == null ? null : mastermind.m_activeGroups.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("nextEventId", Safe(() => mastermind.m_nextEventID.ToString(CultureInfo.InvariantCulture)));
        row.Field("survivalWaveSituation", Safe(() => Mastermind.HasSurvivalWaveSituation.ToString()));
        section.Rows.Add(row);
    }

    /// <summary>The wave data a security door carries: the group in front of it and in its area. An alarm or blood
    /// door's spawn is matched to its door through these ids.</summary>
    private static CaptureRow DoorWaveRow(LG_SecurityDoor door, LG_Zone? zone)
    {
        var row = new CaptureRow("doorWave", "doorWave/" + Safe(() => door.m_serialNumber.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => RecReflect.Describe(door)) ?? "?");
        row.Field("doorStatus", Safe(() => door.LastStatus.ToString()));
        row.Field("hasActiveEnemyWave", Safe(() => door.ActiveEnemyWaveData == null ? "none" : door.ActiveEnemyWaveData.HasActiveEnemyWave.ToString()));
        row.Field("groupInFrontOfDoor", Safe(() => door.ActiveEnemyWaveData == null ? null : door.ActiveEnemyWaveData.EnemyGroupInfrontOfDoor.ToString(CultureInfo.InvariantCulture)));
        row.Field("groupInArea", Safe(() => door.ActiveEnemyWaveData == null ? null : door.ActiveEnemyWaveData.EnemyGroupInArea.ToString(CultureInfo.InvariantCulture)));
        row.Field("groupsInArea", Safe(() => door.ActiveEnemyWaveData == null ? null : door.ActiveEnemyWaveData.EnemyGroupsInArea.ToString(CultureInfo.InvariantCulture)));
        return row;
    }

    /// <summary>The per-enemy detail too costly for the per-frame pass: scale at three levels and the live state of
    /// every damage limb. The id matches the enemy's row in the enemies section.</summary>
    private static CaptureRow EnemyDetailRow(EnemyAgent enemy, LG_Zone? zone)
    {
        var row = new CaptureRow("enemyDetail", "enemy/" + Safe(() => enemy.GlobalID.ToString(CultureInfo.InvariantCulture), "?"),
            Safe(() => enemy.EnemyData == null ? null : enemy.EnemyData.name) ?? "enemy");
        row.Field("sizeMultiplier", Safe(() => enemy.SizeMultiplier.ToString(CultureInfo.InvariantCulture)));
        row.Field("scale", Safe(() => Vec(enemy.transform.localScale)));
        row.Field("modelScale", Safe(() => enemy.MainModelTransform == null ? "none" : Vec(enemy.MainModelTransform.localScale)));
        row.Field("modelLossyScale", Safe(() => enemy.MainModelTransform == null ? "none" : Vec(enemy.MainModelTransform.lossyScale)));
        row.Field("limbProfile", Safe(() => LimbProfile(enemy)));
        return row;
    }

    /// <summary>`index:health/max wWeakspot aArmor` per limb, joined by `;`.</summary>
    private static string LimbProfile(EnemyAgent enemy)
    {
        var limbs = enemy.Damage == null ? null : enemy.Damage.DamageLimbs;
        if (limbs == null) return "none";
        var text = new StringBuilder(32 * limbs.Length);
        for (var i = 0; i != limbs.Length; i++)
        {
            var limb = limbs[i];
            if (limb == null || limb.WasCollected) continue;
            if (text.Length != 0) text.Append(';');
            text.Append(i.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(limb.m_health.ToString("0.###", CultureInfo.InvariantCulture)).Append('/')
                .Append(limb.m_healthMax.ToString("0.###", CultureInfo.InvariantCulture)).Append(" w")
                .Append(limb.m_weakspotDamageMulti.ToString("0.###", CultureInfo.InvariantCulture)).Append(" a")
                .Append(limb.m_armorDamageMulti.ToString("0.###", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    /// <summary>A movable scan's route. The id is the scene path, which both machines build the same way, so two
    /// machines' routes for one scan are compared by id.</summary>
    private static CaptureRow ScanRouteRow(CP_BasicMovable movable, LG_Zone? zone)
    {
        var row = new CaptureRow("scanRoute", "scanRoute" + Path(movable.transform), Safe(() => movable.name) ?? "movable");
        row.Field("count", Safe(() => movable.ScanPositions == null ? "0" : movable.ScanPositions.Count.ToString(CultureInfo.InvariantCulture)));
        row.Field("positions", Safe(() => Positions(movable.ScanPositions)));
        row.Field("amount", Safe(() => movable.m_amountOfPositions.ToString(CultureInfo.InvariantCulture)));
        row.Field("static", Safe(() => movable.m_useStaticBioscanPoints.ToString()));
        row.Field("position", Safe(() => Vec(movable.transform.position)));
        return row;
    }

    private static string Positions(Il2CppSystem.Collections.Generic.List<Vector3>? positions)
    {
        if (positions == null) return "none";
        var text = new StringBuilder(24 * Math.Min(positions.Count, MaxRoutePositions));
        for (var i = 0; i != positions.Count && i != MaxRoutePositions; i++)
        {
            if (i != 0) text.Append(';');
            text.Append(Vec(positions[i]));
        }
        if (positions.Count > MaxRoutePositions) text.Append(";+").Append((positions.Count - MaxRoutePositions).ToString(CultureInfo.InvariantCulture));
        return text.ToString();
    }

    /// <summary>The five ammo pools of a player's backpack, `standard,special,class,resourcePack,consumable`.</summary>
    private static string AmmoOf(PlayerAgent player)
    {
        if (player.Owner == null) return "none";
        if (!PlayerBackpackManager.TryGetBackpack(player.Owner, out var backpack) || backpack == null || backpack.AmmoStorage == null) return "none";
        var storage = backpack.AmmoStorage;
        return Pool(storage.StandardAmmo) + "," + Pool(storage.SpecialAmmo) + "," + Pool(storage.ClassAmmo) + ","
            + Pool(storage.ResourcePackAmmo) + "," + Pool(storage.ConsumableAmmo);
    }

    private static string Pool(InventorySlotAmmo? ammo)
        => ammo == null ? "-" : ammo.AmmoInPack.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>One character per backpack slot: `1` loaded, `0` not loaded, `-` empty slot.</summary>
    private static string SlotsLoaded(PlayerAgent player)
    {
        if (player.Owner == null) return "none";
        if (!PlayerBackpackManager.TryGetBackpack(player.Owner, out var backpack) || backpack == null || backpack.Slots == null) return "none";
        var slots = backpack.Slots;
        var text = new char[slots.Length];
        for (var i = 0; i != slots.Length; i++) text[i] = slots[i] == null ? '-' : slots[i].IsLoaded ? '1' : '0';
        return new string(text);
    }

    /// <summary>The native life of an object (<see cref="NativeLife"/>) as <c>address@world.instance</c>, or "unresolved" for a
    /// collected or non-Unity object.</summary>
    private static string EntityOf(object instance)
        => NativeLife.Of(instance as UnityEngine.Object) is { } life
            ? life.Pointer.ToString(CultureInfo.InvariantCulture) + "@" + life.WorldEpoch.ToString(CultureInfo.InvariantCulture)
                + "." + life.Instance.ToString(CultureInfo.InvariantCulture)
            : "unresolved";

    /// <summary>The IL2CPP class of an object. The interop wrapper is typed by the declaring member, so a wrapper
    /// typed as a base class still names the derived class only here.</summary>
    private static string NativeClass(Il2CppObjectBase value)
    {
        if (value.WasCollected || value.Pointer == IntPtr.Zero) return "collected";
        var klass = IL2CPP.il2cpp_object_get_class(value.Pointer);
        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "unknown";
        var space = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
        return string.IsNullOrEmpty(space) ? name : space + "." + name;
    }
}
