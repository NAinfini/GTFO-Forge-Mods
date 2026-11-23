using System;
using System.Collections.Generic;
using Agents;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;

namespace ForgeWeapon.Native;

/// <summary>The three enemy-damage windows this package reads: the bullet packet and the melee packet attribute a hit
/// to the equipment that dealt it, and the master-side melee receiver feeds the remote-hit facts. They are observed
/// through Runtime's shared receiver entries, never declared, so another Forge package observing the same receiver
/// stays a read-only subscriber. Each window is leased to one world and renewed when the world changes.</summary>
internal sealed class WeaponDamageObservers : IDisposable
{
    private const string AttributionConsumer = "ForgeWeapon.DamageAttribution";
    private const string RemoteConsumer = "ForgeWeapon.MeleeRemote";
    private readonly WeaponNativeSession _owner;
    private readonly List<NativeHookLease> _leases = new();

    internal WeaponDamageObservers(WeaponNativeSession owner) => _owner = owner;

    /// <summary>Ends every window this package holds and observes the receivers again for <paramref name="worldEpoch"/>.
    /// A refused observation withdraws the damage feature with Runtime's own reason; the other observations stay.</summary>
    internal void Renew(long worldEpoch)
    {
        EndAll();
        Observe(AttributionConsumer, worldEpoch, GameEnemyDamageEntry.Bullet, BulletEnter, BulletLeave);
        Observe(AttributionConsumer, worldEpoch, GameEnemyDamageEntry.Melee, MeleeEnter, MeleeLeave);
        Observe(RemoteConsumer, worldEpoch, GameEnemyDamageEntry.Melee, RemoteEnter, RemoteLeave);
    }

    private void Observe(string consumer, long worldEpoch, GameEnemyDamageEntry entry,
        Func<GameEnemyDamageCall, object?> entered, Action<GameEnemyDamageCall, object?> left)
    {
        try { _leases.Add(GameEnemyDamage.Observe(consumer, worldEpoch, entry, HarmonyLib.Priority.First, entered, left)); }
        catch (Exception refusal) when (refusal is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _owner.Withdraw(WeaponFeature.Damage, consumer + "/" + entry + ": " + refusal.Message);
        }
    }

    private object? BulletEnter(GameEnemyDamageCall call)
        => WeaponDamagePacketEquipment.Enter(call.Bullet.source, call.Bullet.gearCategoryId);
    private static void BulletLeave(GameEnemyDamageCall call, object? state)
        => WeaponCombatObserver.LeaveAttribution(state as WeaponCombatObserver.Attribution?);
    private object? MeleeEnter(GameEnemyDamageCall call)
        => WeaponDamagePacketEquipment.Enter(call.Melee.source, call.Melee.gearCategoryId);
    private static void MeleeLeave(GameEnemyDamageCall call, object? state)
        => WeaponCombatObserver.LeaveAttribution(state as WeaponCombatObserver.Attribution?);
    private static object? RemoteEnter(GameEnemyDamageCall call)
    {
        WeaponNativeSession.Offer(WeaponFeature.Damage)?.Guard(static (_, packet) =>
            WeaponMeleeRemoteFacts.Entered(packet.Receiver, packet.Source, packet.Limb, packet.Category),
            (Receiver: call.Receiver, Source: call.Melee.source, Limb: (int)call.Melee.limbID, Category: call.Melee.gearCategoryId));
        return null;
    }
    private static void RemoteLeave(GameEnemyDamageCall call, object? state)
        => WeaponNativeSession.Offer(WeaponFeature.Damage)?.Guard(static (_, receiver) => WeaponMeleeRemoteFacts.Left(receiver), call.Receiver);

    /// <summary>Ends every window. A window closes on its own once its world ends; this releases the leases early.</summary>
    internal void EndAll()
    {
        foreach (var lease in _leases) lease.Dispose();
        _leases.Clear();
    }

    public void Dispose() => EndAll();
}

internal static class WeaponDamagePacketEquipment
{
    internal static WeaponCombatObserver.Attribution? Enter(pAgent source, uint category)
    {
        var session = WeaponNativeSession.Offer(WeaponFeature.Damage);
        if (session is not { NativeAttributionObserved: true }) return null;
        if (!source.TryGet(out var agent)) agent = null;
        return session.Guard(static (live, call) => Enter(live, call.Agent, call.Category),
            (Agent: agent, Category: category), default(WeaponCombatObserver.Attribution?));
    }

    private static WeaponCombatObserver.Attribution? Enter(WeaponNativeSession session, Agents.Agent? agent, uint category)
    {
        var player = agent?.TryCast<PlayerAgent>();
        var owner = session.Adapter.OwnerOf(player);
        // A still-open native weapon call identifies the equipment. A client's gun runs no such call here, and the packet has no
        // instance id: the item the sender holds answers when its gear category is the packet's, and nothing else does.
        return WeaponCombatObserver.EnterAttribution(owner, CombatShotAttribution.EquipmentOf(owner) ?? session.ClientFacts.HeldEquipment(player, category));
    }
}
