using System;
using Agents;
using Enemies;
using HarmonyLib;
using SNetwork;

namespace ForgeCore;

public static partial class Combat
{
    private static Action<EnemyAgent, Agent?>? _killed;
    private static Action<Dam_EnemyDamageBase, pBulletDamageData>? _bulletHit;

    /// <summary>Raised on the host after a bullet hit (<c>Dam_EnemyDamageBase.ReceiveBulletDamage</c>, the game's own receiver for every
    /// stock and Forge bullet) took a living enemy to dead: the enemy and the packet's source agent (null when the packet names none or the
    /// game no longer resolves it). It fires on the same call stack as the hit, after the game has run it, so the enemy already reads dead.
    /// Only bullet hits are observed; melee, explosion and fire kills are other receivers and are not reported. A handler that throws is
    /// one <c>ErrorOnce</c> line and the others still run. The first subscription puts the patch on. Main thread only.</summary>
    public static event Action<EnemyAgent, Agent?>? Killed
    {
        add
        {
            Patches.Ensure(Modules.CombatKills, typeof(CombatKillHooks));
            _killed += value;
        }
        remove => _killed -= value;
    }

    /// <summary>Raised on every machine for each bullet packet the game's own receiver Dam_EnemyDamageBase.ReceiveBulletDamage is about to apply to an
    /// enemy, before the game applies it (the enemy still reads as before the hit). A handler that throws is one ErrorOnce line and the others still
    /// run. The first subscription puts the patch on. Main thread only.</summary>
    public static event Action<Dam_EnemyDamageBase, pBulletDamageData>? EnemyBulletHit
    {
        add
        {
            Patches.Ensure(Modules.CombatKills, typeof(CombatKillHooks));
            _bulletHit += value;
        }
        remove => _bulletHit -= value;
    }

    internal static void RaiseBulletHit(Dam_EnemyDamageBase receiver, pBulletDamageData data)
    {
        var handlers = _bulletHit;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<Dam_EnemyDamageBase, pBulletDamageData>)handler)(receiver, data); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A Combat.EnemyBulletHit handler failed: " + error); }
        }
    }

    internal static void RaiseKilled(EnemyAgent enemy, Agent? source)
    {
        var handlers = _killed;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<EnemyAgent, Agent?>)handler)(enemy, source); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A Combat.Killed handler failed: " + error); }
        }
    }
}

/// <summary>Put on by the first <see cref="Combat.Killed"/> subscription. The prefix runs before every other patch on the receiver and
/// notes whether the enemy was alive on the host; the postfix runs after all of them and reports the enemy if it is now dead.</summary>
internal static class CombatKillHooks
{
    [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveBulletDamage))]
    [HarmonyPrefix, HarmonyPriority(int.MaxValue)]
    private static void Prefix(Dam_EnemyDamageBase __instance, pBulletDamageData __0, out bool __state)
    {
        __state = SNet.IsMaster && __instance.Owner != null && __instance.Owner.Alive;
        Combat.RaiseBulletHit(__instance, __0);
    }

    [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveBulletDamage))]
    [HarmonyPostfix, HarmonyPriority(int.MinValue)]
    private static void Postfix(Dam_EnemyDamageBase __instance, pBulletDamageData __0, bool __state)
    {
        if (!__state || __instance.Owner == null || __instance.Owner.Alive) return;
        try
        {
            Agent? source = null;
            if (__0.source.TryGet(out var found)) source = found;
            Combat.RaiseKilled(__instance.Owner, source);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Observing a bullet kill failed: " + error); }
    }
}
