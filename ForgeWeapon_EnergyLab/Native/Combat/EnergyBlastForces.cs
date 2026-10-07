using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

// Bullet packets and energy visuals use different channels. Match either arrival order before applying death force.
internal static class EnergyBlastForces
{
    private const int Capacity = 1024;
    private readonly record struct Blast(FireVisual Visual, float Until);
    private readonly record struct Death(IntPtr Enemy, float Until, bool Applied);
    private readonly record struct Claim(BlastForcePacket Packet, float Until);
    private static readonly Dictionary<(ulong Shooter, uint Sequence), Blast> Blasts = new();
    private static readonly Dictionary<(ulong Shooter, ushort Target), Death> Deaths = new();
    private static readonly Dictionary<(ulong Shooter, ushort Target), Claim> Claims = new();
    private static readonly List<(ulong Shooter, uint Sequence)> OldBlasts = new();
    private static readonly List<(ulong Shooter, ushort Target)> OldActors = new();

    internal static void Request(uint sequence, EnemyAgent enemy, float magnitude)
    {
        if (SNet.IsMaster) throw new InvalidOperationException("A host blast must send its native death force directly.");
        var target = new pEnemyAgent(); target.Set(enemy);
        EnergyNetwork.SendBlastForce(new() { Epoch = EnergyNetwork.Epoch, Sequence = sequence, Target = target, Magnitude = magnitude });
    }

    internal static void AcceptVisual(FireVisual visual)
    {
        if (!SNet.IsMaster || (EnergyMode)visual.Mode != EnergyMode.Blast || !visual.Hit(0)) return;
        if (Blasts.Count >= Capacity) throw new InvalidOperationException("Blast force visual budget exhausted.");
        Blasts[(visual.Shooter, visual.Sequence)] = new(visual, Time.time + EnergyProtocol.HelloLifetime);
        // Requests are sent during the discharge, before its final visual packet.
        OldActors.Clear();
        foreach (var claim in Claims)
            if (claim.Key.Shooter == visual.Shooter && claim.Value.Packet.Sequence == visual.Sequence) OldActors.Add(claim.Key);
        foreach (var key in OldActors) Apply(key);
        OldActors.Clear();
    }

    internal static void Receive(ulong sender, BlastForcePacket packet)
    {
        if (!SNet.IsMaster || packet.Epoch != EnergyNetwork.Epoch || packet.Sequence == 0 || packet.Target.pRep.keyPlusOne == 0 ||
            !float.IsFinite(packet.Magnitude) || packet.Magnitude < 0f)
            throw new InvalidOperationException("Invalid energy blast force request.");
        var key = (sender, packet.Target.pRep.keyPlusOne);
        if (Deaths.TryGetValue(key, out var death) && death.Applied || Claims.ContainsKey(key)) return;
        if (Claims.Count >= Capacity) throw new InvalidOperationException("Blast force request budget exhausted.");
        Claims.Add(key, new(packet, Time.time + EnergyProtocol.HelloLifetime));
        Apply(key);
    }

    internal static void Killed(ulong shooter, EnemyAgent enemy)
    {
        var target = new pEnemyAgent(); target.Set(enemy);
        var key = (shooter, target.pRep.keyPlusOne);
        if (Deaths.Count >= Capacity && !Deaths.ContainsKey(key)) throw new InvalidOperationException("Blast force death budget exhausted.");
        Deaths[key] = new(enemy.Pointer, Time.time + EnergyProtocol.HelloLifetime, false);
        Apply(key);
    }

    private static void Apply((ulong Shooter, ushort Target) key)
    {
        if (!Claims.TryGetValue(key, out var claim) || !Deaths.TryGetValue(key, out var death) || death.Applied ||
            !Blasts.TryGetValue((key.Shooter, claim.Packet.Sequence), out var blast)) return;
        Claims.Remove(key);
        var gear = EnergyGears.Match(blast.Visual.CategoryId, blast.Visual.Mode);
        if (gear == null || gear.Mode != EnergyMode.Blast ||
            claim.Packet.Magnitude > MathF.Min(gear.Numbers.ExplosionForce + 0.2f, MathF.Sqrt(3f) * 10f))
            throw new InvalidOperationException("Blast death force exceeds its registered native force.");
        if (!claim.Packet.Target.TryGet(out var enemy) || enemy == null || enemy.Alive || enemy.Pointer != death.Enemy)
            throw new InvalidOperationException("Blast death force target is no longer the enemy killed by its shooter.");
        // Consume before the native write; a partial send is never replayed by a duplicate packet.
        Deaths[key] = death with { Applied = true };
        enemy.Damage.SendExplosionForce(blast.Visual.End(0) - blast.Visual.Direction.normalized * 0.04f, claim.Packet.Magnitude);
    }

    internal static void Tick()
    {
        var now = Time.time;
        foreach (var blast in Blasts) if (now > blast.Value.Until) OldBlasts.Add(blast.Key);
        foreach (var key in OldBlasts) Blasts.Remove(key);
        OldBlasts.Clear();
        foreach (var death in Deaths) if (now > death.Value.Until) OldActors.Add(death.Key);
        foreach (var key in OldActors) Deaths.Remove(key);
        OldActors.Clear();
        foreach (var claim in Claims) if (now > claim.Value.Until) OldActors.Add(claim.Key);
        foreach (var key in OldActors)
        {
            Claims.Remove(key);
            Plugin.Error("Energy blast death force expired without its matching native death and visual: " +
                new InvalidOperationException("Unmatched blast force for shooter " + key.Shooter + ", target " + key.Target));
        }
        OldActors.Clear();
    }

    internal static void Clear()
    {
        Blasts.Clear(); Deaths.Clear(); Claims.Clear(); OldBlasts.Clear(); OldActors.Clear();
    }
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveBulletDamage))]
internal static class EnergyBlastDeath
{
    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    private static void Prefix(Dam_EnemyDamageBase __instance, out bool __state)
        => __state = SNet.IsMaster && __instance.Owner != null && __instance.Owner.Alive;

    [HarmonyPostfix, HarmonyPriority(int.MinValue)]
    private static void Postfix(Dam_EnemyDamageBase __instance, pBulletDamageData __0, bool __state)
    {
        if (!__state || __instance.Owner == null || __instance.Owner.Alive) return;
        try
        {
            if (!__0.source.TryGet(out var source) || source?.TryCast<PlayerAgent>()?.Owner is not { } player || player.IsLocal || player.IsBot) return;
            // Native observers can encode the ammo slot in gearCategoryId. The actual source and target identify the kill.
            EnergyBlastForces.Killed(player.Lookup, __instance.Owner);
        }
        catch (Exception error) { Plugin.Error("Energy blast native death observation failed: " + error); }
    }
}
