using System;
using System.Collections.Generic;
using System.Text.Json;
using GameData;
using Gear;
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
/// every peer plays the shell (<see cref="TurretShell"/>): it bores in, throws its lids off, the look rises out of it barrel up, the barrel turns level, and
/// only then does the host stand the game's sentry (so it neither aims nor fires before); at the end the head stows again.</summary>
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
        /// <summary>Host: it has been seen able to fire (a fresh sentry reads no ammunition until it is set up), and it has been called back.</summary>
        internal bool Armed, Recalled;
        /// <summary>This peer's copy of the sentry has its multipliers (only the host's is ever scaled: damage, firing and detection run there).</summary>
        internal bool Strengthened;
    }

    private static readonly Dictionary<ushort, Expiry> Expiring = new();
    private static readonly Dictionary<IntPtr, MarkedSentry> Identities = new();
    private static readonly List<ushort> ExpiryKeys = new();
    /// <summary>Every peer: the strength a summoned sentry was asked for, by Ordnance identity (kept from the call's arrival, so a peer that becomes
    /// host stands the same multipliers on the sentries already there). Damage, firing and detection run on the host.</summary>
    private static readonly Dictionary<ulong, SentryStrength> Strengths = new();

    public object Parse(JsonElement payload)
    {
        PayloadReader.OnlyKnown(payload, "Gear", "AmmoRatio", "Lifetime", "Pickable", "Look", "DamageScale", "FireRateScale", "AmmoScale", "RangeScale", "TurnScale", "AcquireScale");
        var (id, name) = ForgeCore.StrictJson.Reference(payload, "", "Gear");
        var block = id.HasValue ? GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(id.Value) : GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(name!);
        if (block == null || block.WasCollected) throw new PayloadParameterException("Gear", "no offline gear block " + (id.HasValue ? id.Value.ToString() : "'" + name + "'") + " in this install.");
        if (string.IsNullOrEmpty(block.GearJSON)) throw new PayloadParameterException("Gear", "the gear block has no GearJSON.");
        var baseItem = new GearIDRange(block.GearJSON).GetCompID(eGearComponent.BaseItem);
        if (baseItem != SentryItemId) throw new PayloadParameterException("Gear", "gear block " + block.persistentID + " is not a sentry gun (base item " + baseItem + ", sentry guns use " + SentryItemId + ").");
        return new GearParameters(block.persistentID, (float)ForgeCore.StrictJson.Number(payload, "", "AmmoRatio", 1, 0, 1),
            (float)ForgeCore.StrictJson.Number(payload, "", "Lifetime", 0, 0, 3600), ForgeCore.StrictJson.Bool(payload, "", "Pickable", true),
            new SentryStrength(Scale(payload, "DamageScale"), Scale(payload, "FireRateScale"), Scale(payload, "AmmoScale"),
                Scale(payload, "RangeScale"), Scale(payload, "TurnScale"), Scale(payload, "AcquireScale")),
            PayloadReader.Look(payload, OrdnanceTurrets.IsRegistered));
    }

    private static float Scale(JsonElement payload, string field) => (float)ForgeCore.StrictJson.Number(payload, "", field, 1, 0.1, 5);

    public void Execute(OrdnanceExecution execution)
    {
        var p = (GearParameters)execution.Parameters;
        var block = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(p.GearId) ?? throw new InvalidOperationException("Offline gear block " + p.GearId + " is gone.");
        // A sentry set up without these throws inside the spawn callback and leaves a half-built gun.
        foreach (var path in new[] { SentryFlashPath, TracerPath })
            if (ForgeCore.GameAssets.Get<UnityEngine.Object>(path) == null)
                throw new InvalidOperationException("Effect asset not loaded, the sentry cannot be set up: " + path);
        var owner = execution.Summoner ?? ForgeCore.Players.Local ?? throw new InvalidOperationException("No player agent owns the spawn.");
        if (!ForgeCore.Gear.SentryAmmoReady(owner)) throw new InvalidOperationException("The summoner's class ammunition pool cannot be read (no backpack, no class slot item, or an empty pool).");
        var range = new GearIDRange(block.GearJSON);
        var identity = SentryIdentity.For(execution.SummonerLookup, execution.ThrowId);
        range.PublicGearName = SentryIdentity.Marker(identity, p.Pickable, OrdnanceTurrets.KeyOf(p.Look));
        var lifetime = p.Lifetime;
        var position = execution.Position;
        var rotation = execution.Rotation;
        var node = execution.CourseNode;
        var gearId = block.persistentID;
        // The sentry stands when the head has risen and turned its barrel level, so it neither aims nor fires before that.
        Deliveries.HostAfter(OrdnanceDelivery.ShellReadyDelay(execution.Arrival, BurrowOf(p.Look)), () =>
        {
            try
            {
                // The summoner may have left while the shell bored in: the host's own player stands the sentry, as at the call.
                var standing = owner != null && !owner.WasCollected ? owner : ForgeCore.Players.Local ?? throw new InvalidOperationException("The sentry's owner left before the shell opened and the host has no player agent.");
                if (node == null || node.WasCollected) throw new InvalidOperationException("The landing course node is gone.");
                ForgeCore.Gear.SpawnSentry(range, Spawned(lifetime, gearId, identity, p.Pickable, p.Look), position, rotation, node, standing, p.AmmoRatio * p.Strength.Ammo, p.Strength.Ammo);
            }
            catch
            {
                // No sentry will stand: the shell must not stay up for nothing (the failure itself is logged by the caller of this step).
                Deliveries.ReportRecall(identity);
                throw;
            }
        });
    }

    public bool HasOwnArrival => true;

    public bool SupportsDirect => true;

    public void OnArrived(OrdnancePresentation presentation)
    {
        Strengths[SentryIdentity.For(presentation.SummonerLookup, presentation.ThrowId)] = ((GearParameters)presentation.Parameters).Strength;
        Deliveries.StartShell(presentation);
    }

    /// <summary>The look's burrow; a sentry that keeps the game's own look has no head to raise, so it stands once the base has bored in and opened.</summary>
    private static BurrowSpec BurrowOf(string look)
        => OrdnanceTurrets.TryGet(look, out _, out var spec) ? spec.Burrow : new BurrowSpec { RiseHeight = 0.1f, RiseSeconds = 0f };

    public OrdnanceShellAppearance? GetShellAppearance(OrdnanceDefinition definition)
    {
        var p = (GearParameters)definition.Parameters;
        var appearance = new OrdnanceShellAppearance(BurrowOf(p.Look), p.Lifetime);
        if (!OrdnanceTurrets.TryGet(p.Look, out var model, out var spec)) return appearance;
        appearance.Head = model;
        appearance.HeadScale = spec.WorldScale;
        appearance.Gun = spec.Head;
        appearance.LookId = p.Look;
        return appearance;
    }

    private static ItemReplicationManager.delItemCallback Spawned(float lifetime, uint gearId, ulong identity, bool pickable, string look)
        => new Action<ISyncedItem, PlayerAgent>((item, _) =>
        {
            try
            {
                var sentry = item.TryCast<SentryGunInstance>();
                if (sentry == null) throw new InvalidOperationException("Gear " + gearId + " spawned something that is not a sentry gun.");
                Register(sentry, identity, !pickable, OrdnanceTurrets.KeyOf(look));
                if (lifetime > 0f && ForgeCore.Session.IsHost)
                {
                    var id = new SNetStructs.pReplicator();
                    id.SetID(sentry.Replicator);
                    Expiring[id.keyPlusOne] = new Expiry { Sentry = sentry, Identity = identity, ExpiresAt = Time.time + lifetime };
                    OrdnanceNetwork.Broadcast(Messages.SentryExpiryEvent, new SentryExpiryMessage { Replicator = id.keyPlusOne, Identity = identity, Remaining = lifetime });
                }
            }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Gear payload spawn callback failed: " + error); }
        });

    internal static void ApplyExpiry(SentryExpiryMessage message)
    {
        if (message.Replicator == 0 || message.Identity == 0 || !float.IsFinite(message.Remaining) || message.Remaining < 0f) return;
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
        if (Expiring.Count == 0 && Identities.Count == 0) return;
        if (now < _nextTimerCheck) return;
        _nextTimerCheck = now + TimerCheckSeconds;
        if (ForgeCore.Session.IsHost) RecallEmpty(now);
        TakeBackShellsOfGoneSentries();
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
                Deliveries.RecallOf(expiry.Identity);
            }
            if (!ForgeCore.Session.IsHost) continue;
            try
            {
                // Every peer's shell takes the turret back before the sentry goes.
                Deliveries.ReportRecall(expiry.Identity);
                ForgeCore.Items.Despawn(sentry.Replicator);
                Expiring.Remove(id);
            }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Sentry despawn failed: " + error); }
        }
    }

    private static readonly List<IntPtr> GonePointers = new();

    /// <summary>Every peer: a sentry the game destroyed (picked up, shot down, or removed with a checkpoint) takes its shell back, so a shell whose
    /// sentry has no lifetime (<c>Lifetime</c> 0) does not stand empty for the rest of the level.</summary>
    private static void TakeBackShellsOfGoneSentries()
    {
        GonePointers.Clear();
        foreach (var (pointer, known) in Identities)
            if (known.Sentry == null || known.Sentry.WasCollected) GonePointers.Add(pointer);
        foreach (var pointer in GonePointers)
        {
            var identity = Identities[pointer].Identity;
            Identities.Remove(pointer);
            Deliveries.RecallOf(identity);
        }
    }

    /// <summary>Host: a sentry that cannot be picked up and cannot fire any more ends now, by the same path as running out of time: its timer is brought
    /// to now on every peer and the host removes it.</summary>
    private static void RecallEmpty(float now)
    {
        foreach (var known in Identities.Values)
        {
            var sentry = known.Sentry;
            if (!known.NoPickup || known.Recalled || sentry == null || sentry.WasCollected) continue;
            var cost = sentry.CostOfBullet;
            if (cost <= 0f) continue;
            var can = sentry.Ammo >= cost;
            if (!known.Armed) { known.Armed = can; continue; }
            if (can) continue;
            known.Recalled = true;
            var id = new SNetStructs.pReplicator();
            id.SetID(sentry.Replicator);
            if (Expiring.TryGetValue(id.keyPlusOne, out var expiry)) { expiry.Sentry = sentry; expiry.ExpiresAt = now; }
            else Expiring[id.keyPlusOne] = new Expiry { Sentry = sentry, Identity = known.Identity, ExpiresAt = now };
            OrdnanceNetwork.Broadcast(Messages.SentryExpiryEvent, new SentryExpiryMessage { Replicator = id.keyPlusOne, Identity = known.Identity, Remaining = 0f });
        }
    }

    internal static void Register(SentryGunInstance sentry, ulong identity, bool noPickup = false, uint lookKey = 0)
    {
        MarkedSentry? old = Identities.TryGetValue(sentry.Pointer, out var held) && held.Sentry != null && !held.Sentry.WasCollected ? held : null;
        SentryIdentity.SentryMark? known = old == null ? null : new SentryIdentity.SentryMark(old.Identity, old.NoPickup, old.LookKey);
        var mark = SentryIdentity.Merge(known, new SentryIdentity.SentryMark(identity, noPickup, lookKey));
        // The same sentry registered again keeps what it was given (its multipliers); a different identity is a different sentry.
        Identities[sentry.Pointer] = new MarkedSentry { Sentry = sentry, Identity = mark.Identity, NoPickup = mark.NoPickup, LookKey = mark.LookKey, Strengthened = old != null && old.Identity == mark.Identity && old.Strengthened };
    }

    /// <summary>This peer became the host: the sentries already standing get the multipliers their calls asked for, as the old host had given them
    /// (damage, firing and detection run here now), and count as having fired (one already out of ammunition is recalled, not left standing).</summary>
    internal static void TakeOver()
    {
        foreach (var known in Identities.Values)
        {
            if (known.Sentry == null || known.Sentry.WasCollected) continue;
            known.Armed = true;
            if (known.Strengthened) continue;
            try { ApplyStrength(known.Sentry); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Applying a sentry's strength after the host changed failed: " + error); }
        }
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
        if (!ForgeCore.Session.IsHost || !TryIdentity(sentry, out var identity) || !Strengths.TryGetValue(identity, out var s)) return;
        if (Identities.TryGetValue(sentry.Pointer, out var marked))
        {
            if (marked.Strengthened) return;
            marked.Strengthened = true;
        }
        var firing = sentry.m_firing?.TryCast<SentryGunInstance_Firing_Bullets>() ?? throw new InvalidOperationException("The sentry has no bullet firing component.");
        var detection = sentry.m_detection?.TryCast<SentryGunInstance_Detection>() ?? throw new InvalidOperationException("The sentry has no detection component.");
        var vanilla = firing.m_archetypeData ?? throw new InvalidOperationException("The sentry firing component has no archetype.");
        var own = ForgeCore.Gear.CopyArchetype(vanilla);
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
        catch (Exception error) { Plugin.Logger.ErrorOnce("Could not read an Ordnance sentry marker: " + error); }
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
        catch (Exception error) { Plugin.Logger.ErrorOnce("Could not make the sentry unpickable: " + error); }
    }
}
