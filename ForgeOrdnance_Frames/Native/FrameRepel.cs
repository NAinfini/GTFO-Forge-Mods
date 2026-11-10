using System;
using System.Collections.Generic;
using Enemies;
using Player;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>Host: a raised barrier shoves out enemies that walked into the dome, through the game's own push (the player
/// shove), ForgeCore's <c>Combat.Push</c> (<c>Dam_SyncedDamageBase.PushDamage(dam, sourceAgent, position, direction)</c>), which reads the health before and after.
///
/// What the game does with it (read from <c>Dam_EnemyDamageBase.ReceivePushDamage</c> in this build): nothing for an
/// immortal enemy or one whose <c>EnemyBalancingData.CanBePushed</c> is false; otherwise it registers the source agent,
/// tells the enemy's abilities it was hit, and starts a hitreact on the enemy's locomotion in the push direction - Heavy when
/// <c>dam</c> is above zero, Micro when the enemy is glued past its tolerance. No health is taken in that receiver. Whether
/// the Heavy hitreact moves the enemy back out of the dome is not yet seen in the game.
///
/// Budget: each suit checks four times a second, pushes at most <see cref="Budget"/> enemies per check, and each enemy at
/// most once per <see cref="EnemyCooldown"/> seconds.</summary>
internal static class FrameRepel
{
    internal const float Interval = 0.25f;
    private const int Budget = 6;
    private const float EnemyCooldown = 0.75f;
    private const float Strength = 1f;

    /// <summary>The wreck's blast: this far and this many enemies, shoved once.</summary>
    private const float WreckReach = 4f;
    private const int WreckBudget = 12;

    private static readonly Dictionary<IntPtr, float> NextPush = new();
    private static readonly List<IntPtr> Expired = new();

    internal static void Host(FrameUnit unit, PlayerAgent pilot, float now)
    {
        var r = unit.Record;
        var centre = new Vector3(r.X, r.Y + 1.2f, r.Z);
        var pushed = 0;
        // Each living enemy once (ForgeCore's query).
        foreach (var found in ForgeCore.Enemies.Near(centre, r.Spec.Shield!.Radius))
        {
            if (pushed >= Budget) break;
            var enemy = found!;
            var p = enemy.Position;
            if (!FrameRules.Repels(r, p.x, p.y, p.z)) continue;
            if (NextPush.TryGetValue(enemy.Pointer, out var next) && now < next) continue;
            NextPush[enemy.Pointer] = now + EnemyCooldown;
            var away = p - centre;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = pilot.Rotation * Vector3.forward;
            away.y = 0f;
            try { ForgeCore.Combat.Push(enemy.Damage, Strength, pilot, p, away.normalized); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Exosuit barrier push failed: " + error); return; }
            pushed++;
        }
        if (pushed > 0) unit.Visual?.Pulse(now);
        if (NextPush.Count > 128) Prune(now);
    }

    /// <summary>Host: the suit is wrecked and its pilot thrown out: the enemies around it are shoved once by the blast, which gives the pilot a moment (the game's own push: no damage).</summary>
    internal static void Wrecked(PlayerAgent pilot, Vector3 at)
    {
        var pushed = 0;
        foreach (var found in ForgeCore.Enemies.Near(at, WreckReach))
        {
            if (pushed++ >= WreckBudget) break;
            var enemy = found!;
            var away = enemy.Position - at;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = pilot.Rotation * Vector3.forward;
            away.y = 0f;
            try { ForgeCore.Combat.Push(enemy.Damage, Strength, pilot, enemy.Position, away.normalized); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("frames-wreck-push-failed", "exosuit wreck push failed: " + error); return; }
        }
    }

    /// <summary>Not the host: whether an enemy the game would shove stands inside the raised dome (the host's own rule, without the
    /// shove: an immortal enemy or one whose <c>CanBePushed</c> is false is left out, as the game's receiver ignores it), so the push
    /// pulse rings on this peer exactly when it rings on the host. Measured from <paramref name="feet"/>, the suit as drawn here: a
    /// client's record holds the host's last sent place, up to 2 s old while the suit walks.</summary>
    internal static bool AnyInside(FrameRecord r, Vector3 feet)
    {
        if (!FrameRules.BarrierUp(r)) return false;
        var centre = feet + Vector3.up * 1.2f;
        var reach = r.Spec.Shield!.Radius * r.Spec.Shield.Radius;
        foreach (var enemy in ForgeCore.Enemies.Near(centre, r.Spec.Shield.Radius))
            if ((enemy!.Position - centre).sqrMagnitude <= reach && Pushable(enemy)) return true;
        return false;
    }

    private static bool Pushable(EnemyAgent enemy) => enemy.EnemyBalancingData is { CanBePushed: true } && !enemy.Damage.IsImortal;

    private static void Prune(float now)
    {
        Expired.Clear();
        foreach (var entry in NextPush) if (entry.Value <= now) Expired.Add(entry.Key);
        for (var i = 0; i < Expired.Count; i++) NextPush.Remove(Expired[i]);
    }

    internal static void Clear()
    {
        NextPush.Clear();
        Expired.Clear();
    }
}
