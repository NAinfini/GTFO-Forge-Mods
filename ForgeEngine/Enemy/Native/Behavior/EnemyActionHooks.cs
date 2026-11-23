using System;
using Agents;
using Enemies;
using HarmonyLib;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The enemy action triggers of contract §4.5 B2 (`EnemyActionTriggerContract`): one postfix per native
/// member `catalog/authoring/block-review.json` names, each on the member the game itself runs when the enemy commits to the
/// action. `forge.trigger.enemy.zone_changed` has no hook here: it rides the existing `EnemyBehaviorPump` frame
/// sample (`EnemyModule.ActionFacts.cs`).
///
/// PERF: every body reads its own cached subscription gate first and returns before touching the instance, building
/// a closure or allocating anything when no plan subscribes. The work then goes through the closure-free guard with a
/// `static` lambda and its inputs in a struct passed by reference. Nothing here writes the world.</summary>
internal static class EnemyActionHooks
{
    /// <summary>The inputs of one action hook, carried to the guarded module call by reference.</summary>
    internal struct Call
    {
        internal EnemyAgent? Enemy;
        internal Agent? Target;
        internal EnemyAgent? Child;
        internal Vector3 Position;
        internal bool Flag;
        internal Dam_PlayerDamageBase? Player;
        internal ES_EnemyAttackBase? Attack;
        internal int Phase;
        internal global::Player.PlayerAgent? Grabbed;
        internal INM_NoiseMaker? Maker;
        internal float Distance;
        internal bool SkipFlyer;
        internal pES_EnemyAttackData AttackData;
        internal EAB_FogSphere? Fog;
    }

    /// <summary>The attack-phase fact of one attack state's own override: the state names its enemy and its target.
    /// The plain shooter's hooks skip a state that is really a flying shooter, so a flying shooter's override that
    /// calls its base cannot publish the same phase twice.</summary>
    internal static void AttackPhase(ES_EnemyAttackBase state, int phase, bool hasPosition, Vector3 position, bool plainShooter)
    {
        if (EnemyHost.Offer(EnemyFeature.Attack) is not { } session || !session.Module.AttackPerformedObserved) return;
        var call = new Call { Attack = state, Phase = phase, Flag = hasPosition, Position = position, SkipFlyer = plainShooter };
        session.Guard(ref call, static (EnemyModule module, ref Call c) =>
        {
            if (c.SkipFlyer && c.Attack!.TryCast<ES_ShooterAttackFlyer>() != null) return;
            module.AfterAttackPhase(c.Attack!, c.Phase, c.Flag, c.Position);
        });
    }

    /// <summary>The noise fact of one behaviour state's own `OnNoiseDetected` override.</summary>
    internal static void Noise(EB_StateBase state, ref NM_NoiseData noise, float distance01)
    {
        if (EnemyHost.Offer(EnemyFeature.Noise) is not { } session || !session.Module.HeardNoiseObserved) return;
        var call = new Call
        {
            Enemy = state.m_ai?.m_enemyAgent,
            Maker = noise.noiseMaker,
            Position = noise.position,
            Phase = (int)noise.type,
            Distance = distance01
        };
        session.Guard(ref call, static (EnemyModule module, ref Call c) =>
            module.AfterNoiseHeard(c.Enemy, c.Maker, c.Position, c.Phase, c.Distance));
    }

    /// <summary>The alarm fact of one of the two starts of `EAB_Alarm`.</summary>
    internal static void Alarm(EAB_Alarm alarm, int phase)
    {
        if (EnemyHost.Offer(EnemyFeature.Alarm) is not { } session || !session.Module.AlarmRaisedObserved) return;
        var call = new Call { Enemy = alarm.m_owner, Phase = phase };
        session.Guard(ref call, static (EnemyModule module, ref Call c) => module.AfterAlarm(c.Enemy, c.Phase));
    }

    /// <summary>The grab fact of either grab entry.</summary>
    internal static void Grab(EnemyAgent? enemy, global::Player.PlayerAgent? player, int kind)
    {
        if (EnemyHost.Offer(EnemyFeature.Grab) is not { } session || !session.Module.GrabbedPlayerObserved) return;
        var call = new Call { Enemy = enemy, Grabbed = player, Phase = kind };
        session.Guard(ref call, static (EnemyModule module, ref Call c) => module.AfterPlayerGrabbed(c.Enemy, c.Grabbed, c.Phase));
    }
}

/// <summary>`ES_StrikerMelee.RecieveMeleeAttack(pES_StrikerMeleeData)`: the melee state's own start packet, which every
/// peer runs when an enemy swings. `ES_StrikerMelee` is not an `ES_EnemyAttackBase`, so `ability_used` never saw it.</summary>
[HarmonyPatch(typeof(ES_StrikerMelee), "RecieveMeleeAttack")]
internal static class EnemyMeleeAttackHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_StrikerMelee __instance, pES_StrikerMeleeData data)
    {
        if (EnemyHost.Offer(EnemyFeature.MeleeAttack) is not { } session || !session.Module.MeleeAttackObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = __instance.m_enemyAgent, Flag = data.FwdAttack };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterMeleeAttack(c.Enemy, c.Flag));
    }
}

/// <summary>`ES_Scream.OnStateShouldBeActivated(pES_EnemyScreamData)`: an awake enemy's scream packet, patched once by ForgeCore (`GameHooks.EnemyScreamed`).</summary>
internal static class EnemyScreamHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.EnemyScreamed.After(static (scream, _) =>
    {
        if (EnemyHost.Offer(EnemyFeature.Scream) is not { } session || !session.Module.ScreamObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = scream.m_enemyAgent };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterScream(c.Enemy));
    });
}

/// <summary>`ES_ScreamFlyer.ActivateState()`: the flyer's scream, which uses its own scream packet type.</summary>
[HarmonyPatch(typeof(ES_ScreamFlyer), nameof(ES_ScreamFlyer.ActivateState))]
internal static class EnemyScreamFlyerHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_ScreamFlyer __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.Scream) is not { } session || !session.Module.ScreamObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = __instance.m_enemyAgent };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterScream(c.Enemy));
    }
}

/// <summary>`ES_Hibernate.MasterStartDetection()`: the host's own decision that a sleeper starts sensing.</summary>
[HarmonyPatch(typeof(ES_Hibernate), nameof(ES_Hibernate.MasterStartDetection))]
internal static class EnemySleeperDetectionStartHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_Hibernate __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.SleeperDetection) is not { } session || !session.Module.SleeperDetectingObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = __instance.m_enemyAgent, Position = __instance.m_lastDetectPos, Flag = true };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterSleeperDetection(c.Enemy, c.Flag, c.Position));
    }
}

/// <summary>`ES_Hibernate.MasterEndDetection()`: the host's own decision that a sleeper calmed down.</summary>
[HarmonyPatch(typeof(ES_Hibernate), nameof(ES_Hibernate.MasterEndDetection))]
internal static class EnemySleeperDetectionEndHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_Hibernate __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.SleeperDetection) is not { } session || !session.Module.SleeperDetectingObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = __instance.m_enemyAgent, Position = __instance.m_lastDetectPos, Flag = false };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterSleeperDetection(c.Enemy, c.Flag, c.Position));
    }
}

/// <summary>`EAB_FogSphere.DoTrigger()` (0x14A2580): the fog-sphere ability releasing its sphere. The state member the
/// block review names, `ES_TriggerFogSphere.DoTriggerAbility()`, shares its native body (0x155D100) with the same
/// state's `RecieveTriggerPacket(pES_TriggerFogSphere)`, so a patch there would also run for a call of a different
/// signature; the ability's own override has a body of its own.</summary>
[HarmonyPatch(typeof(EAB_FogSphere), nameof(EAB_FogSphere.DoTrigger))]
internal static class EnemyFogReleasedHook
{
    [HarmonyPostfix]
    private static void Postfix(EAB_FogSphere __instance)
    {
        if (EnemyHost.Offer(EnemyFeature.Fog) is not { } session) return;
        // The authored fog-sphere values (EnemyModule.AbilityFields.cs) land on the handler this call released.
        if ((session.Module.AbilityHooks & ForgeEnemy.Profile.EnemyAbilityHooks.FogSphere) != 0)
        {
            var fog = new EnemyActionHooks.Call { Fog = __instance };
            session.Guard(ref fog, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.FogSphereReleased(c.Fog));
        }
        if (!session.Module.FogReleasedObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = __instance.m_owner };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterFogReleased(c.Enemy));
    }
}

/// <summary>`EAB_Birthing.OnGaveBirth(EnemyAgent)`: one child the mother's birthing ability spawned.</summary>
[HarmonyPatch(typeof(EAB_Birthing), nameof(EAB_Birthing.OnGaveBirth))]
internal static class EnemyGaveBirthHook
{
    [HarmonyPostfix]
    private static void Postfix(EAB_Birthing __instance, EnemyAgent child)
    {
        if (EnemyHost.Offer(EnemyFeature.Birth) is not { } session) return;
        if ((session.Module.AbilityHooks & ForgeEnemy.Profile.EnemyAbilityHooks.ChildSpawning) != 0)
        {
            var birth = (Ability: __instance, Child: child);
            session.Guard(ref birth, static (EnemyModule module, ref (EAB_Birthing Ability, EnemyAgent Child) c) => module.ChildBorn(c.Ability, c.Child));
        }
        if (!session.Module.GaveBirthObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = __instance.m_owner, Child = child };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterGaveBirth(c.Enemy, c.Child));
    }
}

/// <summary>`ActivateState(Vector3)` stores the point then tail-calls this inherited slot (28). Observe that
/// same call boundary: Runtime's entry admission refuses ActivateState's own entry (`unsupported-native-prologue:Movsd@3`,
/// `evidence/enemy-hooks.json` notHookedEntries), while this shared slot passes. Capture the stored
/// point before the state change, and filter the shared slot to the pouncer after the subscription gate.</summary>
[HarmonyPatch(typeof(EB_StateWithDuration), "ChangeToThisState")]
internal static class EnemySpitOutHook
{
    [HarmonyPrefix]
    private static void Prefix(EB_StateWithDuration __instance, out State __state)
    {
        __state = default;
        if (EnemyHost.Offer(EnemyFeature.PlayerRelease) is not { } session || !session.Module.ReleasedPlayerObserved) return;
        var call = new State { Behavior = __instance };
        session.Guard(ref call, static (EnemyModule module, ref State c) =>
        {
            if (c.Behavior!.TryCast<EB_SpitOut_Pouncer>() is not { } pouncer) return;
            c.Pouncer = pouncer;
            c.Position = pouncer.m_spitOutPosition;
        });
        __state = call;
    }

    [HarmonyPostfix]
    private static void Postfix(State __state)
    {
        if (__state.Pouncer is not { } pouncer || EnemyHost.Offer(EnemyFeature.PlayerRelease) is not { } session || !session.Module.ReleasedPlayerObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = pouncer.m_ai?.m_enemyAgent, Position = __state.Position, Flag = true };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterPlayerReleased(c.Enemy, c.Flag, c.Position));
    }

    internal struct State
    {
        internal EB_StateWithDuration? Behavior;
        internal EB_SpitOut_Pouncer? Pouncer;
        internal Vector3 Position;
    }
}

/// <summary>`Dam_PlayerDamageBase.ReceiveTentacleGrabStop(pMiniDamageData)`: a tank tentacle letting a player go. The
/// grab source is read in the prefix, before the receiver clears it; the release is published in the postfix.</summary>
[HarmonyPatch(typeof(Dam_PlayerDamageBase), "ReceiveTentacleGrabStop")]
internal static class EnemyTankGrabStopHook
{
    [HarmonyPrefix]
    private static void Prefix(Dam_PlayerDamageBase __instance, out EnemyAgent? __state)
    {
        __state = null;
        if (EnemyHost.Offer(EnemyFeature.PlayerRelease) is not { } session || !session.Module.ReleasedPlayerObserved) return;
        var call = new EnemyActionHooks.Call { Player = __instance };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => c.Enemy = module.TankGrabSource(c.Player));
        __state = call.Enemy;
    }

    [HarmonyPostfix]
    private static void Postfix(EnemyAgent? __state)
    {
        if (__state == null || EnemyHost.Offer(EnemyFeature.PlayerRelease) is not { } session || !session.Module.ReleasedPlayerObserved) return;
        var call = new EnemyActionHooks.Call { Enemy = __state };
        session.Guard(ref call, static (EnemyModule module, ref EnemyActionHooks.Call c) => module.AfterPlayerReleased(c.Enemy, c.Flag, c.Position));
    }
}

/// <summary>The damage-kind window of one enemy receive entry. `ProcessReceivedDamage` carries no kind, so each receive
/// entry that funnels into it names its own for the duration of the call; the damage window reads it and publishes it
/// as `damage_kind`. Nested entries restore the kind they found. Closed when no damage-window fact has a plan.</summary>
internal static class EnemyDamageKindWindow
{
    /// <summary>The prefix opened nothing, so the postfix has nothing to restore.</summary>
    private const int NotOpened = int.MinValue;

    internal static void Open(int kind, out int previous)
    {
        previous = NotOpened;
        if (EnemyHost.Offer(EnemyFeature.DamageKind) is not { } session || !(session.Module.DamageWindowObserved || session.Module.DamageRulesActive)) return;
        previous = session.Module.EnterDamageKind(kind);
    }

    internal static void Close(int previous)
    {
        if (previous != NotOpened && EnemyHost.Session is { } session) session.Module.LeaveDamageKind(previous);
    }

    // Small kinds only ever travel as these cached boxes, so a hit allocates nothing for its window state.
    private static readonly object[] Boxed = { 0, 1, 2, 3, 4, 5, 6, 7 };

    /// <summary>The Runtime shared entry's `entered` half: opens the window and answers the kind it found, or null
    /// when nothing was opened.</summary>
    internal static object? Enter(int kind)
    {
        Open(kind, out var previous);
        return previous == NotOpened ? null : (uint)previous < (uint)Boxed.Length ? Boxed[previous] : previous;
    }

    /// <summary>The shared entry's `left` half, run after the receiver returned or threw.</summary>
    internal static void Leave(object? state)
    {
        if (state is int previous) Close(previous);
    }
}

/// <summary>`Dam_EnemyDamageBase.ReceiveBulletDamage` and `ReceiveMeleeDamage` are also the weapon package's entries, so
/// the Runtime owns their one physical install and this package is a read-only window observer of each: the window
/// only names the damage kind (`EnemyModule.EnterDamageKind`) for the duration of the call and changes no argument,
/// return value or exception. One lease per entry and world.</summary>
internal static class EnemyDamageKindEntries
{
    private static ForgeEngine.GameBindings.NativeHookLease Bullet(long epoch)
        => ForgeEngine.GameBindings.GameEnemyDamage.Observe("forge.enemy.damage-kind.bullet", epoch,
            ForgeEngine.GameBindings.GameEnemyDamageEntry.Bullet, Priority.Normal,
            static _ => EnemyDamageKindWindow.Enter(EnemyModule.DamageKindDirect), static (_, state) => EnemyDamageKindWindow.Leave(state));

    private static ForgeEngine.GameBindings.NativeHookLease Melee(long epoch)
        => ForgeEngine.GameBindings.GameEnemyDamage.Observe("forge.enemy.damage-kind.melee", epoch,
            ForgeEngine.GameBindings.GameEnemyDamageEntry.Melee, Priority.Normal,
            static _ => EnemyDamageKindWindow.Enter(EnemyModule.DamageKindMelee), static (_, state) => EnemyDamageKindWindow.Leave(state));

    internal static ForgeEngine.GameBindings.DomainWorldObservation Observe(ForgeEngine.Framework.RuntimeModuleHandle owner, ForgeEngine.Framework.IForgeEngine kernel)
        => new("Enemy", owner, kernel, null, Bullet, Melee);

    /// <summary>Takes and releases both leases for the current world, so a Runtime that refuses either entry is known before
    /// the module registers. Null when both stand; otherwise the Runtime's refusal, which withdraws the damage-kind
    /// function (a hit whose kind cannot be named is not reported with a made-up one).</summary>
    internal static string? Probe(ForgeEngine.Framework.IForgeEngine kernel)
    {
        try
        {
            var epoch = kernel.Lifecycle.WorldEpoch;
            using var bullet = Bullet(epoch);
            using var melee = Melee(epoch);
            return null;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return "ReceiveBulletDamage/ReceiveMeleeDamage shared entry: " + error.Message;
        }
    }
}
/// <summary>`Dam_EnemyDamageBase.ReceiveExplosionDamage` is patched once by ForgeCore (`GameHooks.EnemyExplosionReceived`); the window names
/// the explosion kind for the duration of the call, however the body ends.</summary>
internal static class EnemyReceiveExplosionKind
{
    internal static void Subscribe() => ForgeCore.GameHooks.EnemyExplosionReceived.Around(
        static _ => { EnemyDamageKindWindow.Open(EnemyModule.DamageKindExplosion, out var previous); return previous; },
        static (_, previous, _) => EnemyDamageKindWindow.Close(previous),
        static (_, previous, _) => EnemyDamageKindWindow.Close(previous));
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), "ReceivePushDamage")]
internal static class EnemyReceivePushKind
{
    [HarmonyPrefix] private static void Prefix(out int __state) => EnemyDamageKindWindow.Open(EnemyModule.DamageKindCollision, out __state);
    [HarmonyPostfix] private static void Postfix(int __state) => EnemyDamageKindWindow.Close(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, int __state)
    {
        if (__exception != null) EnemyDamageKindWindow.Close(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), "ReceiveFireDamage")]
internal static class EnemyReceiveFireKind
{
    [HarmonyPrefix] private static void Prefix(out int __state) => EnemyDamageKindWindow.Open(EnemyModule.DamageKindDot, out __state);
    [HarmonyPostfix] private static void Postfix(int __state) => EnemyDamageKindWindow.Close(__state);
    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception, int __state)
    {
        if (__exception != null) EnemyDamageKindWindow.Close(__state);
        return __exception;
    }
}

// forge.trigger.enemy.attack_performed: the five attack states' own overrides (dump.cs 664918 to 665336). The base
// `ES_EnemyAttackBase.OnAttackPerform` sits on the shared empty body 0x353190 and is never patched. Phase members are
// `enemy_attack_phase`: 0 wind-up, 1 strike. `targetPos` is the overrides' own parameter name.

[HarmonyPatch(typeof(ES_ShooterAttack), nameof(ES_ShooterAttack.OnAttackWindUp))]
internal static class EnemyShooterWindUpHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_ShooterAttack __instance) => EnemyActionHooks.AttackPhase(__instance, 0, false, default, true);
}

[HarmonyPatch(typeof(ES_ShooterAttack), nameof(ES_ShooterAttack.OnAttackPerform))]
internal static class EnemyShooterPerformHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_ShooterAttack __instance, Vector3 targetPos) => EnemyActionHooks.AttackPhase(__instance, 1, true, targetPos, true);
}

[HarmonyPatch(typeof(ES_ShooterAttackFlyer), nameof(ES_ShooterAttackFlyer.OnAttackWindUp))]
internal static class EnemyShooterFlyerWindUpHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_ShooterAttackFlyer __instance) => EnemyActionHooks.AttackPhase(__instance, 0, false, default, false);
}

[HarmonyPatch(typeof(ES_ShooterAttackFlyer), nameof(ES_ShooterAttackFlyer.OnAttackPerform))]
internal static class EnemyShooterFlyerPerformHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_ShooterAttackFlyer __instance, Vector3 targetPos) => EnemyActionHooks.AttackPhase(__instance, 1, true, targetPos, false);
}

[HarmonyPatch(typeof(ES_StrikerAttack), nameof(ES_StrikerAttack.OnAttackWindUp))]
internal static class EnemyStrikerWindUpHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_StrikerAttack __instance) => EnemyActionHooks.AttackPhase(__instance, 0, false, default, false);
}

[HarmonyPatch(typeof(ES_StrikerAttack), nameof(ES_StrikerAttack.OnAttackPerform))]
internal static class EnemyStrikerPerformHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_StrikerAttack __instance, Vector3 targetPos) => EnemyActionHooks.AttackPhase(__instance, 1, true, targetPos, false);
}

[HarmonyPatch(typeof(ES_TankAttack), nameof(ES_TankAttack.OnAttackWindUp))]
internal static class EnemyTankWindUpHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_TankAttack __instance) => EnemyActionHooks.AttackPhase(__instance, 0, false, default, false);
}

[HarmonyPatch(typeof(ES_TankAttack), nameof(ES_TankAttack.OnAttackPerform))]
internal static class EnemyTankPerformHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_TankAttack __instance, Vector3 targetPos) => EnemyActionHooks.AttackPhase(__instance, 1, true, targetPos, false);
}

/// <summary>The multi-target tank's own non-virtual pair, `OnAttackWindUp(int)` and `OnAttackPerform()`; the strike
/// carries no aim point.</summary>
[HarmonyPatch(typeof(ES_TankMultiTargetAttack), nameof(ES_TankMultiTargetAttack.OnAttackWindUp), new[] { typeof(int) })]
internal static class EnemyTankMultiWindUpHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_TankMultiTargetAttack __instance) => EnemyActionHooks.AttackPhase(__instance, 0, false, default, false);
}

[HarmonyPatch(typeof(ES_TankMultiTargetAttack), nameof(ES_TankMultiTargetAttack.OnAttackPerform), new Type[0])]
internal static class EnemyTankMultiPerformHook
{
    [HarmonyPostfix]
    private static void Postfix(ES_TankMultiTargetAttack __instance) => EnemyActionHooks.AttackPhase(__instance, 1, false, default, false);
}

/// <summary>`EB_Hibernating.OnNoiseDetected(ref NM_NoiseData, float)` (dump.cs 659759): a sleeping enemy hearing a noise. ForgeCore
/// patches it once (`GameHooks.HibernatingNoiseHeard`); this reads the call after its body.</summary>
internal static class EnemyHibernatingNoiseHook
{
    internal static void Subscribe() => ForgeCore.GameHooks.HibernatingNoiseHeard.After(static (call, _) =>
    {
        var noise = call.Noise;
        EnemyActionHooks.Noise(call.State, ref noise, call.Distance01);
    });
}

/// <summary>`EB_Patrolling.OnNoiseDetected(ref NM_NoiseData, float)` (dump.cs 660193): a patrolling enemy hearing a noise.</summary>
[HarmonyPatch(typeof(EB_Patrolling), nameof(EB_Patrolling.OnNoiseDetected))]
internal static class EnemyPatrollingNoiseHook
{
    [HarmonyPostfix]
    private static void Postfix(EB_Patrolling __instance, ref NM_NoiseData noiseData, float distance01)
        => EnemyActionHooks.Noise(__instance, ref noiseData, distance01);
}

/// <summary>`enemy_alarm_phase` member 0, the charge-up start. `EAB_Alarm.StartChargeUp()` has no caller in this build: the
/// virtual `Trigger()` (dump.cs 533549, slot 8) inlines it and sets `m_chargingUp` (+0x88) itself, so the start is the
/// call of Trigger that turned `m_chargingUp` on.</summary>
[HarmonyPatch(typeof(EAB_Alarm), nameof(EAB_Alarm.Trigger))]
internal static class EnemyAlarmChargeUpHook
{
    [HarmonyPrefix]
    private static void Prefix(EAB_Alarm __instance, out bool __state) => __state = __instance.m_chargingUp;
    [HarmonyPostfix]
    private static void Postfix(EAB_Alarm __instance, bool __state)
    {
        if (!__state && __instance.m_chargingUp) EnemyActionHooks.Alarm(__instance, 0);
    }
}

/// <summary>`enemy_alarm_phase` member 1, the alarm start. `EAB_Alarm.StartAlarm()` has no caller in this build: `Update()`
/// (dump.cs 533561, runs only while the ability is enabled) inlines it when the charge-up timer runs out and sets
/// `m_alarmTriggered` (+0x89) itself, so the start is the update that turned `m_alarmTriggered` on.</summary>
[HarmonyPatch(typeof(EAB_Alarm), "Update")]
internal static class EnemyAlarmStartHook
{
    [HarmonyPrefix]
    private static void Prefix(EAB_Alarm __instance, out bool __state) => __state = __instance.m_alarmTriggered;
    [HarmonyPostfix]
    private static void Postfix(EAB_Alarm __instance, bool __state)
    {
        if (!__state && __instance.m_alarmTriggered) EnemyActionHooks.Alarm(__instance, 1);
    }
}

/// <summary>`EB_Consume_Pouncer.ActivateState(PlayerAgent)` (dump.cs 660907): a snatcher starting to consume the
/// player it caught; `enemy_grab_kind` member 0.</summary>
[HarmonyPatch(typeof(EB_Consume_Pouncer), nameof(EB_Consume_Pouncer.ActivateState))]
internal static class EnemyPouncerConsumeHook
{
    [HarmonyPostfix]
    private static void Postfix(EB_Consume_Pouncer __instance, global::Player.PlayerAgent target)
    {
        if (EnemyHost.Offer(EnemyFeature.Grab) is not { } session || !session.Module.GrabbedPlayerObserved) return;
        EnemyActionHooks.Grab(__instance.m_ai?.m_enemyAgent, target, 0);
    }
}

/// <summary>`PLOC_GrabbedByTank.IncomingTankTentacleGrab(pTankTentacleGrabData, EnemyAgent)` (dump.cs 539596): the
/// grabbed player's own locomotion taking the tank's grab packet. The packet's `Source` names the tank and the
/// state's owner is the player; `enemy_grab_kind` member 1.</summary>
[HarmonyPatch(typeof(PLOC_GrabbedByTank), nameof(PLOC_GrabbedByTank.IncomingTankTentacleGrab))]
internal static class EnemyTankTentacleGrabHook
{
    [HarmonyPostfix]
    private static void Postfix(PLOC_GrabbedByTank __instance, pTankTentacleGrabData data)
    {
        if (EnemyHost.Offer(EnemyFeature.Grab) is not { } session || !session.Module.GrabbedPlayerObserved) return;
        EnemyAgent? tank = null;
        try { if (!data.Source.TryGet(out tank)) tank = null; }
        catch (Exception caught) { EnemyLog.Contained(caught); tank = null; }
        EnemyActionHooks.Grab(tank, __instance.m_owner, 1);
    }
}
