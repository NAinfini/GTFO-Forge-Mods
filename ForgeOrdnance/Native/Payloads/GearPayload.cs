using System;
using System.Collections.Generic;
using System.Text.Json;
using GameData;
using Gear;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>Multipliers over the vanilla sentry of the same gear block; 1 leaves a value as the game has it.</summary>
internal sealed record SentryStrength(float Damage, float FireRate, float Ammo, float Range, float Turn, float Acquire);

internal sealed record GearParameters(uint GearId, float AmmoRatio, float Lifetime, bool Pickable, SentryStrength Strength, string Look);

/// <summary>Built-in payload <c>Gear</c>: an <c>Instance</c> of any offline gear block, which is how the game stands a
/// deployed sentry gun. <c>{ "Type": "Gear", "Gear": 24 | "name", "AmmoRatio": 0..1, "Lifetime": 0..3600 seconds (0 = stays),
/// "Pickable": true|false, "Look": "Pack:item" (optional: a turret look a package registered with <see cref="OrdnanceTurrets.RegisterLook"/>; without it the game's own sentry look stays),
/// "DamageScale", "FireRateScale", "AmmoScale", "RangeScale", "TurnScale", "AcquireScale": 0.1..5, default 1 }</c>, the last six being multipliers over the vanilla sentry. Ammo is a fraction of the summoner's class ammunition pool. It arrives in the turret shell:
/// every peer plays the shell (<see cref="TurretShell"/>) and the host stands the sentry when the shell opens.</summary>
internal sealed class GearPayload : IOrdnancePayload
{
    internal const string TypeName = "Gear";
    /// <summary>ItemDataBlock <c>GEAR_SentryGun</c>: the base item of every gear the game can stand as an instance.</summary>
    private const uint SentryItemId = 97;
    private const string SentryFlashPath = "Assets/AssetPrefabs/Effects/Weapon/MuzzleFlash/Sentrygun_MuzzleFlash_3RD.prefab";
    private const string TracerPath = "Assets/AssetPrefabs/FX_Effects/FX_Tracer.prefab";

    private sealed class Expiry
    {
        internal SentryGunInstance? Sentry;
        internal ulong Identity;
        internal float ExpiresAt;
        internal bool Announced;
    }

    private sealed class MarkedSentry
    {
        internal SentryGunInstance Sentry = null!;
        internal ulong Identity;
        internal bool NoPickup;
        internal uint LookKey;
    }

    private static readonly Dictionary<ushort, Expiry> Expiring = new();
    private static readonly Dictionary<IntPtr, MarkedSentry> Identities = new();
    private static readonly List<ushort> ExpiryKeys = new();
    /// <summary>Host only: the strength a summoned sentry was asked for, by Ordnance identity. Damage, firing and detection run on the host.</summary>
    private static readonly Dictionary<ulong, SentryStrength> Strengths = new();
    private static ulong _nextIdentity;

    public object Parse(JsonElement payload)
    {
        PayloadReader.OnlyKnown(payload, "Gear", "AmmoRatio", "Lifetime", "Pickable", "Look", "DamageScale", "FireRateScale", "AmmoScale", "RangeScale", "TurnScale", "AcquireScale");
        var (id, name) = PayloadReader.Reference(payload, "Gear");
        var block = id.HasValue ? GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(id.Value) : GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(name!);
        if (block == null || block.WasCollected) throw new PayloadParameterException("Gear", "no offline gear block " + (id.HasValue ? id.Value.ToString() : "'" + name + "'") + " in this install.");
        if (string.IsNullOrEmpty(block.GearJSON)) throw new PayloadParameterException("Gear", "the gear block has no GearJSON.");
        var baseItem = new GearIDRange(block.GearJSON).GetCompID(eGearComponent.BaseItem);
        if (baseItem != SentryItemId) throw new PayloadParameterException("Gear", "gear block " + block.persistentID + " is not a sentry gun (base item " + baseItem + ", sentry guns use " + SentryItemId + ").");
        return new GearParameters(block.persistentID, (float)PayloadReader.Number(payload, "AmmoRatio", 1, 0, 1),
            (float)PayloadReader.Number(payload, "Lifetime", 0, 0, 3600), PayloadReader.Bool(payload, "Pickable", true),
            new SentryStrength(Scale(payload, "DamageScale"), Scale(payload, "FireRateScale"), Scale(payload, "AmmoScale"),
                Scale(payload, "RangeScale"), Scale(payload, "TurnScale"), Scale(payload, "AcquireScale")),
            PayloadReader.Look(payload, OrdnanceTurrets.IsRegistered));
    }

    private static float Scale(JsonElement payload, string field) => (float)PayloadReader.Number(payload, field, 1, 0.1, 5);

    public void Execute(OrdnanceExecution execution)
    {
        var p = (GearParameters)execution.Parameters;
        var block = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(p.GearId) ?? throw new InvalidOperationException("Offline gear block " + p.GearId + " is gone.");
        // A sentry set up without these throws inside the spawn callback and leaves a half-built gun.
        foreach (var path in new[] { SentryFlashPath, TracerPath })
            if (ForgeCore.GameAssets.Get<UnityEngine.Object>(path) == null)
                throw new InvalidOperationException("Effect asset not loaded, the sentry cannot be set up: " + path);
        var owner = execution.Summoner ?? PlayerManager.GetLocalPlayerAgent() ?? throw new InvalidOperationException("No player agent owns the spawn.");
        var pool = ClassPool(owner);
        var range = new GearIDRange(block.GearJSON);
        var identity = NextIdentity();
        range.PublicGearName = SentryIdentity.Marker(identity, p.Pickable, OrdnanceTurrets.KeyOf(p.Look));
        var lifetime = p.Lifetime;
        var position = execution.Position;
        var rotation = execution.Rotation;
        var node = execution.CourseNode;
        var gearId = block.persistentID;
        Deliveries.HostAfter(ArrivalPlan.ContentsAfter(execution.Arrival, DeliveryTiming.SentrySpawnDelay), () =>
        {
            if (owner == null || owner.WasCollected) throw new InvalidOperationException("The sentry's owner left before the shell opened.");
            if (node == null || node.WasCollected) throw new InvalidOperationException("The landing course node is gone.");
            var ammoPool = pool * p.Strength.Ammo;
            Strengths[identity] = p.Strength;
            ItemReplicationManager.SpawnGear(range, Spawned(lifetime, gearId, identity, p.Pickable, p.Look), ItemMode.Instance, position, rotation, node, owner, p.AmmoRatio * ammoPool, ammoPool);
        });
    }

    public bool HasOwnArrival => true;

    public bool SupportsDirect => true;

    public void OnArrived(OrdnancePresentation presentation) => Deliveries.StartShell(presentation, OrdnanceTurrets.KeyOf(((GearParameters)presentation.Parameters).Look));

    /// <summary>The class ammunition pool the game spends as a sentry's ammunition, read from the owner's class slot item.</summary>
    private static float ClassPool(PlayerAgent owner)
    {
        if (owner.Owner == null || !PlayerBackpackManager.TryGetBackpack(owner.Owner, out var backpack) || backpack?.Slots == null)
            throw new InvalidOperationException("The summoner's backpack is not available.");
        var slot = (int)InventorySlot.GearClass;
        var item = slot < backpack.Slots.Length ? backpack.Slots[slot]?.Instance?.TryCast<ItemEquippable>() : null;
        if (item == null || item.Pointer == IntPtr.Zero) throw new InvalidOperationException("The summoner has no class slot item to read the ammunition pool from.");
        var pool = item.GetClassAmmoMaxCap(owner.Owner);
        if (pool <= 0f) throw new InvalidOperationException("The class ammunition pool reads as empty.");
        return pool;
    }

    private static ItemReplicationManager.delItemCallback Spawned(float lifetime, uint gearId, ulong identity, bool pickable, string look)
        => new Action<ISyncedItem, PlayerAgent>((item, _) =>
        {
            try
            {
                var sentry = item.TryCast<SentryGunInstance>();
                if (sentry == null) throw new InvalidOperationException("Gear " + gearId + " spawned something that is not a sentry gun.");
                Register(sentry, identity, !pickable, OrdnanceTurrets.KeyOf(look));
                if (lifetime > 0f && SNet.IsMaster)
                {
                    var id = new SNetStructs.pReplicator();
                    id.SetID(sentry.Replicator);
                    Expiring[id.keyPlusOne] = new Expiry { Sentry = sentry, Identity = identity, ExpiresAt = Time.time + lifetime };
                    OrdnanceNetwork.Broadcast(Messages.SentryExpiryEvent, new SentryExpiryMessage { Replicator = id.keyPlusOne, Identity = identity, Remaining = lifetime });
                }
            }
            catch (Exception error) { Plugin.Error("Gear payload spawn callback failed: " + error); }
        });

    internal static void ApplyExpiry(SentryExpiryMessage message)
    {
        if (message.Replicator == 0 || message.Identity == 0 || !float.IsFinite(message.Remaining) || message.Remaining < 0f) return;
        ObserveIdentity(message.Identity);
        var expires = Time.time + message.Remaining;
        if (Expiring.TryGetValue(message.Replicator, out var existing))
        {
            // A repeated hello replays the same timer; it must not extend the lifetime.
            if (existing.Identity == message.Identity) existing.ExpiresAt = Math.Min(existing.ExpiresAt, expires);
            else Expiring[message.Replicator] = new Expiry { Identity = message.Identity, ExpiresAt = expires };
            return;
        }
        Expiring[message.Replicator] = new Expiry { Identity = message.Identity, ExpiresAt = expires };
    }

    internal static IEnumerable<SentryExpiryMessage> ExpiryState()
    {
        foreach (var (id, expiry) in Expiring)
            yield return new SentryExpiryMessage { Replicator = id, Identity = expiry.Identity, Remaining = Math.Max(0f, expiry.ExpiresAt - Time.time) };
    }

    /// <summary>Expiry timers are looked at this often; a sentry's lifetime is seconds, so a tenth of a second is not visible.</summary>
    private const float TimerCheckSeconds = 0.1f;
    private static float _nextTimerCheck;

    /// <summary>Every peer resolves native spawns and keeps its timer across migration; only the current host despawns.</summary>
    internal static void Tick(float now)
    {
        if (Expiring.Count == 0) return;
        if (now < _nextTimerCheck) return;
        _nextTimerCheck = now + TimerCheckSeconds;
        // Entries are removed while the keys are walked.
        ExpiryKeys.Clear();
        ExpiryKeys.AddRange(Expiring.Keys);
        foreach (var id in ExpiryKeys)
        {
            if (!Expiring.TryGetValue(id, out var expiry)) continue;
            var sentry = expiry.Sentry;
            if (sentry is null)
            {
                // The native gear spawn and our expiry packet can arrive in either order.
                var nativeId = new SNetStructs.pReplicator { keyPlusOne = id };
                ulong? actualIdentity = null;
                SentryGunInstance? candidate = null;
                if (nativeId.TryGetID(out var replicator) && replicator != null)
                {
                    candidate = replicator.TryCast<GearReplicator>()?.Item?.TryCast<SentryGunInstance>();
                    if (candidate != null && TryIdentity(candidate, out var identity)) actualIdentity = identity;
                }
                switch (SentryIdentity.ResolveTimer(expiry.Identity, actualIdentity, now, expiry.ExpiresAt))
                {
                    case SentryTimerResolution.Bind:
                        sentry = candidate ?? throw new InvalidOperationException("A sentry timer matched without a native sentry instance.");
                        break;
                    case SentryTimerResolution.Wait:
                        continue;
                    default:
                        Expiring.Remove(id);
                        continue;
                }
                expiry.Sentry = sentry;
            }
            // Pickup, destruction and checkpoint cleanup destroy the native sentry (Unity null): its timer goes with it.
            if (sentry == null || sentry.WasCollected) { Expiring.Remove(id); continue; }
            if (now < expiry.ExpiresAt) continue;
            if (!expiry.Announced)
            {
                // Every peer keeps the same timer, so each plays the power-down once; only the host removes the sentry.
                expiry.Announced = true;
                OrdnanceTurrets.RaiseExpiring(OrdnanceTurrets.TryLook(sentry, out var lookId) ? lookId : "", sentry, sentry.transform.position);
                OrdnanceFx.SentryExpired(sentry.transform.position);
            }
            if (!SNet.IsMaster) continue;
            try
            {
                ItemReplicationManager.DeSpawn(sentry.Replicator);
                Expiring.Remove(id);
            }
            catch (Exception error) { Plugin.Error("Sentry despawn failed: " + error); }
        }
    }

    private static ulong NextIdentity()
    {
        do { _nextIdentity++; } while (_nextIdentity == 0);
        return _nextIdentity;
    }

    private static void ObserveIdentity(ulong identity)
    {
        if (identity > _nextIdentity) _nextIdentity = identity;
    }

    internal static void Register(SentryGunInstance sentry, ulong identity, bool noPickup = false, uint lookKey = 0)
    {
        ObserveIdentity(identity);
        SentryIdentity.SentryMark? known = Identities.TryGetValue(sentry.Pointer, out var old) && old.Sentry != null && !old.Sentry.WasCollected
            ? new SentryIdentity.SentryMark(old.Identity, old.NoPickup, old.LookKey) : null;
        var mark = SentryIdentity.Merge(known, new SentryIdentity.SentryMark(identity, noPickup, lookKey));
        Identities[sentry.Pointer] = new MarkedSentry { Sentry = sentry, Identity = mark.Identity, NoPickup = mark.NoPickup, LookKey = mark.LookKey };
    }

    internal static void Forget(SentryGunInstance sentry) => Identities.Remove(sentry.Pointer);

    internal static bool TryIdentity(SentryGunInstance sentry, out ulong identity) => TryIdentity(sentry, out identity, out _);

    internal static bool TryIdentity(SentryGunInstance sentry, out ulong identity, out uint lookKey)
    {
        identity = 0;
        lookKey = 0;
        if (!Identities.TryGetValue(sentry.Pointer, out var known)) return false;
        // Native pointers can be reused after a sentry is destroyed; an old marker must not transfer to the new object.
        if (known.Sentry == null || known.Sentry.WasCollected)
        {
            Identities.Remove(sentry.Pointer);
            return false;
        }
        identity = known.Identity;
        lookKey = known.LookKey;
        return true;
    }

    /// <summary>Gives the sentry its own copy of the archetype (the shared block also drives every vanilla sentry) scaled by the
    /// requested multipliers. Runs on the host once the sentry's components are set up.</summary>
    internal static void ApplyStrength(SentryGunInstance sentry)
    {
        if (!SNet.IsMaster || !TryIdentity(sentry, out var identity) || !Strengths.TryGetValue(identity, out var s)) return;
        Strengths.Remove(identity);
        var firing = sentry.m_firing?.TryCast<SentryGunInstance_Firing_Bullets>() ?? throw new InvalidOperationException("The sentry has no bullet firing component.");
        var detection = sentry.m_detection?.TryCast<SentryGunInstance_Detection>() ?? throw new InvalidOperationException("The sentry has no detection component.");
        var vanilla = firing.m_archetypeData ?? throw new InvalidOperationException("The sentry firing component has no archetype.");
        var own = new ArchetypeDataBlock();
        foreach (var property in typeof(ArchetypeDataBlock).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0) property.SetValue(own, property.GetValue(vanilla));
        own.Damage = vanilla.Damage * s.Damage;
        own.StaggerDamageMulti = vanilla.StaggerDamageMulti * s.Damage;
        own.ShotDelay = vanilla.ShotDelay / s.FireRate;
        own.BurstDelay = vanilla.BurstDelay / s.FireRate;
        own.Sentry_DetectionMaxRange = vanilla.Sentry_DetectionMaxRange * s.Range;
        // Range is one multiplier for what the sentry sees and how far its shots count: the damage falloff distances and the ray length
        // grow with the detection range, or a target far away is seen and cannot be hit.
        own.DamageFalloff = new UnityEngine.Vector2(vanilla.DamageFalloff.x * s.Range, vanilla.DamageFalloff.y * s.Range);
        firing.MaxRayDist *= s.Range;
        own.Sentry_RotationSpeed = vanilla.Sentry_RotationSpeed * s.Turn;
        own.Sentry_StartFireDelay = vanilla.Sentry_StartFireDelay / s.Acquire;
        firing.m_archetypeData = own;
        detection.m_archetypeData = own;
    }

    internal static void Clear()
    {
        Strengths.Clear();
        _nextTimerCheck = 0f;
        Expiring.Clear();
        Identities.Clear();
    }
}

/// <summary>Every peer: records the replicated Ordnance identity, and disables pickup when its marker says so.</summary>
internal static class SentryMarkerHook
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.SentrySpawned.Before(call => Prefix(call.Sentry, call.Data));
        ForgeCore.GameHooks.SentrySpawned.After((call, _) => Postfix(call.Sentry, call.Data));
    }

    // OnGearSpawnComplete may run before OnSpawn returns, so the identity must be available to its look hook up front.
    private static void Prefix(SentryGunInstance __instance, pGearSpawnData spawnData)
    {
        try
        {
            GearPayload.Forget(__instance);
            if (SentryIdentity.TryMarker(spawnData.gearIDRange.publicName.data, out var identity, out _, out var lookKey))
                GearPayload.Register(__instance, identity, false, lookKey);
        }
        catch (Exception error) { Plugin.Error("Could not read an Ordnance sentry marker: " + error); }
    }

    private static void Postfix(SentryGunInstance __instance, pGearSpawnData spawnData)
    {
        try
        {
            if (!SentryIdentity.TryMarker(spawnData.gearIDRange.publicName.data, out var identity, out var pickable, out var lookKey)) return;
            GearPayload.Register(__instance, identity, !pickable, lookKey);
            OrdnanceTurrets.RaiseOnline(OrdnanceTurrets.IdOf(lookKey), __instance);
            if (pickable) return;
            ForgeCore.HoldPoint.Disable(__instance.m_interactPickup ?? throw new InvalidOperationException("The sentry has no pickup interaction to disable."));
            Dimension.RemoveWarpable(__instance.TryCast<IWarpableObject>() ?? throw new InvalidOperationException("The sentry is not a warpable object."));
        }
        catch (Exception error) { Plugin.Error("Could not make the sentry unpickable: " + error); }
    }
}
