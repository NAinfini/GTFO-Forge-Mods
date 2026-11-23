using System;
using Agents;
using Enemies;
using Player;

namespace ForgeWeapon.Native;

/// <summary>The second native source of `forge.trigger.combat.melee_hit`: the replicated melee damage a remote
/// player's swing becomes on the host. `Dam_EnemyDamageBase.ReceiveMeleeDamage(pFullDamageData)` is the master-side
/// packet receiver (the method is `protected`, so it is patched by name), and the packet carries the attacker
/// (`pAgent source`, resolved through its own `TryGet`), the limb (`byte limbID`) and the gear category — but not
/// the charge, and its damage field is a packed `UFloat16` whose decode scale belongs to the receiver's own body.
/// The damage this half publishes is therefore not decoded out of the packet: it is the receiver's own health
/// before and after the body ran, which is the damage that really landed, and a hit the receiver's rules reduced
/// to nothing publishes nothing at all.
///
/// Who publishes what is `MeleeHitLedger`'s rule: this half publishes only for an attacker that is neither this
/// machine's own player nor a bot, because those two swings are already published by the hit entries themselves
/// (`WeaponMeleeHitFacts`), and the shared ledger keeps a hit from being published twice if a packet arrives twice.
/// A swing whose attacker this process cannot name as a player publishes nothing and says so.
///
/// The hooks are read-only with respect to the game: they read the receiver's health around a body the game is
/// already running and never touch that body's arguments or result.</summary>
internal static class WeaponMeleeRemoteFacts
{
    /// <summary>One receive body in flight. The damage entry does not re-enter itself, so one slot is enough; the
    /// pointer is kept so a postfix that does not belong to the pending prefix (a nested or interleaved receive)
    /// is ignored rather than paired with the wrong health.</summary>
    private readonly record struct Pending(IntPtr Receiver, float Health, pAgent Source, int Limb, uint Category);

    private static Pending? _pending;

    /// <summary>The prefix: remember what the receiver held before the body applied the packet, and which attacker
    /// and limb the packet named. A receiver whose health cannot be read is not remembered at all, so the postfix
    /// has nothing to pair and publishes nothing.</summary>
    internal static void Entered(Dam_EnemyDamageBase receiver, pAgent source, int limb, uint category)
    {
        _pending = null;
        if (receiver == null || WeaponMeleeHitFacts.Current?.ObservesRemote != true) return;
        float health;
        try { health = receiver.Health; }
        catch (Exception failure) { NativeCallFailure.Note("WeaponMeleeRemoteFacts.Entered", failure); return; }
        if (!float.IsFinite(health)) return;
        _pending = new Pending(receiver.Pointer, health, source, limb, category);
    }

    /// <summary>The postfix: the health the body left behind is the damage that landed, and the packet's attacker
    /// is who dealt it. Everything this half cannot answer — an attacker that is not a remote player, a receiver
    /// whose owner is not an enemy life, a hit that changed no health — publishes nothing rather than a guess.</summary>
    internal static void Left(Dam_EnemyDamageBase receiver)
    {
        var saved = _pending;
        _pending = null;
        if (saved is not { } pending || receiver == null || receiver.Pointer != pending.Receiver) return;
        float after;
        try { after = receiver.Health; }
        catch (Exception failure) { NativeCallFailure.Note("WeaponMeleeRemoteFacts.Left", failure); return; }
        if (!float.IsFinite(after)) return;
        float damage = pending.Health - after;
        if (damage <= 0f) return;
        Agent? attacker = null;
        try { if (!pending.Source.TryGet(out attacker)) attacker = null; }
        catch (Exception failure) { NativeCallFailure.Note("WeaponMeleeRemoteFacts.Left#2", failure); attacker = null; }
        var player = attacker?.TryCast<PlayerAgent>();
        if (player == null) return;
        EnemyAgent? enemy;
        try { enemy = receiver.Owner; }
        catch (Exception failure) { NativeCallFailure.Note("WeaponMeleeRemoteFacts.Left#3", failure); return; }
        if (enemy == null) return;
        WeaponMeleeHitFacts.Current?.RemoteHit(player, enemy, pending.Limb, damage, pending.Category);
    }
}
