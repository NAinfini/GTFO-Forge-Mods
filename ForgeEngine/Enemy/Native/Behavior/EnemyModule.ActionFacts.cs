using System;
using Agents;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The module half of the enemy action triggers (contract §4.5 B2, `EnemyActionTriggerContract`) and of the
/// enemy-side output gaps (B3) the behaviour sampling and the damage window fill. The Harmony classes are
/// `EnemyActionHooks.cs`; each one reads the gate below before it builds anything. Nothing here writes the world.
///
/// Every fact is published on this module's own host authority (`CanPublishNodeFacts`), about an enemy this module
/// tracks: a pointer that is not the life this module minted, or a life already gone, publishes nothing. A port the
/// game did not fill — a target no registered kind names, a position that does not read — is absent, never guessed.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>No receive entry named a kind for the damage call being processed.</summary>
    internal const int NoDamageKind = -1;
    /// <summary>Members of the shared `damage_kind` set (`direct, melee, explosion, dot, shrapnel, collision, …`) the
    /// enemy receive entries name: bullet is `direct`, melee is `melee`, explosion is `explosion`, fire is `dot`, and the
    /// shove is `collision` — the same mapping the player side's receive entries use.</summary>
    internal const int DamageKindMelee = 1, DamageKindExplosion = 2, DamageKindDot = 3, DamageKindCollision = 5;

    /// <summary>The kind the innermost open receive entry named, or <see cref="NoDamageKind"/>.</summary>
    private int _damageKind = NoDamageKind;

    private EnemyGate _meleeGate = null!, _screamGate = null!, _sleeperGate = null!, _projectileGate = null!,
        _fogGate = null!, _birthGate = null!, _releasedGate = null!, _zoneGate = null!, _attackGate = null!, _noiseGate = null!,
        _alarmGate = null!, _grabGate = null!, _hitWorldGate = null!, _expiredGate = null!;

    internal bool MeleeAttackObserved => _meleeGate.HasSubscribers;
    internal bool ScreamObserved => _screamGate.HasSubscribers;
    internal bool SleeperDetectingObserved => _sleeperGate.HasSubscribers;
    internal bool ProjectileFiredObserved => _projectileGate.HasSubscribers;
    internal bool FogReleasedObserved => _fogGate.HasSubscribers;
    internal bool GaveBirthObserved => _birthGate.HasSubscribers;
    internal bool ReleasedPlayerObserved => _releasedGate.HasSubscribers;
    /// <summary>Whether a plan consumes the zone fact the course-node entry samples.</summary>
    internal bool ZoneChangeObserved => _zoneGate.HasSubscribers;
    internal bool AttackPerformedObserved => _attackGate.HasSubscribers;
    internal bool HeardNoiseObserved => _noiseGate.HasSubscribers;
    internal bool AlarmRaisedObserved => _alarmGate.HasSubscribers;
    internal bool GrabbedPlayerObserved => _grabGate.HasSubscribers;

    /// <summary>Resolved once, from the constructor, while every binding of this provider is already a registry fact.</summary>
    private void CreateActionGates()
    {
        _meleeGate = Gate(EnemyActionTriggerContract.MeleeAttack.Binding);
        _screamGate = Gate(EnemyActionTriggerContract.Screamed.Binding);
        _sleeperGate = Gate(EnemyActionTriggerContract.SleeperDetecting.Binding);
        _projectileGate = Gate(EnemyActionTriggerContract.ProjectileFired.Binding);
        _fogGate = Gate(EnemyActionTriggerContract.FogReleased.Binding);
        _birthGate = Gate(EnemyActionTriggerContract.GaveBirth.Binding);
        _releasedGate = Gate(EnemyActionTriggerContract.ReleasedPlayer.Binding);
        _zoneGate = Gate(EnemyActionTriggerContract.ZoneChanged.Binding);
        _attackGate = Gate(EnemyActionTriggerContract.AttackPerformed.Binding);
        _noiseGate = Gate(EnemyActionTriggerContract.HeardNoise.Binding);
        _alarmGate = Gate(EnemyActionTriggerContract.AlarmRaised.Binding);
        _grabGate = Gate(EnemyActionTriggerContract.GrabbedPlayer.Binding);
        _hitWorldGate = Gate(EnemyActionTriggerContract.ProjectileHitWorld.Binding);
        _expiredGate = Gate(EnemyActionTriggerContract.ProjectileExpired.Binding);
    }

    /// <summary>A receive entry opens its kind for the call; the previous one is handed back to restore.</summary>
    internal int EnterDamageKind(int kind)
    {
        int previous = _damageKind;
        _damageKind = kind;
        return previous;
    }

    internal void LeaveDamageKind(int previous) => _damageKind = previous;

    internal void AfterMeleeAttack(EnemyAgent? enemy, bool forward)
    {
        CheckThread();
        if (ActionEntry(enemy, _meleeGate) is not { } entry) return;
        var target = CurrentTarget(entry);
        if (!TryBeginNodeFact(_meleeGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyMeleeAttack.OutEnemy, entry.Reference);
        fact.Values.Boolean(Blocks.TriggerEnemyMeleeAttack.OutForward, forward);
        if (target != null) fact.Values.Entity(Blocks.TriggerEnemyMeleeAttack.OutTarget, target);
        RaiseNodeFact(fact);
    }

    internal void AfterScream(EnemyAgent? enemy)
    {
        CheckThread();
        if (ActionEntry(enemy, _screamGate) is not { } entry) return;
        var target = CurrentTarget(entry);
        if (!TryBeginNodeFact(_screamGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyScreamed.OutEnemy, entry.Reference);
        if (target != null) fact.Values.Entity(Blocks.TriggerEnemyScreamed.OutTarget, target);
        RaiseNodeFact(fact);
    }

    internal void AfterSleeperDetection(EnemyAgent? enemy, bool detecting, Vector3 position)
    {
        CheckThread();
        if (ActionEntry(enemy, _sleeperGate) is not { } entry) return;
        if (!TryBeginNodeFact(_sleeperGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemySleeperDetecting.OutEnemy, entry.Reference);
        fact.Values.Boolean(Blocks.TriggerEnemySleeperDetecting.OutDetecting, detecting);
        if (IsFinitePoint(position)) fact.Values.Vector(Blocks.TriggerEnemySleeperDetecting.OutPosition, position.x, position.y, position.z);
        RaiseNodeFact(fact);
    }

    /// <summary>The native ability owner is the shooter, not its aimed target. Only a current tracked enemy life
    /// may emit this fact; this hook does not observe player projectile fire.</summary>
    internal void AfterProjectileFired(EnemyAgent? enemy, Agent? target)
    {
        CheckThread();
        if (ActionEntry(enemy, _projectileGate) is not { } entry) return;
        var resolved = ResolveAgent(target);
        var fact = _projectileGate.Begin(_registration);
        fact.Values.Entity(Blocks.TriggerEnemyProjectileFired.OutShooter, entry.Reference);
        if (resolved != null) fact.Values.Entity(Blocks.TriggerEnemyProjectileFired.OutTarget, resolved);
        RaiseNodeFact(fact);
    }

    internal void AfterFogReleased(EnemyAgent? enemy)
    {
        CheckThread();
        if (ActionEntry(enemy, _fogGate) is not { } entry) return;
        var position = entry.Enemy.Position;
        // The row's position is required: a release whose enemy does not stand anywhere readable is not published.
        if (!IsFinitePoint(position)) return;
        if (!TryBeginNodeFact(_fogGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyFogReleased.OutEnemy, entry.Reference);
        fact.Values.Vector(Blocks.TriggerEnemyFogReleased.OutPosition, position.x, position.y, position.z);
        RaiseNodeFact(fact);
    }

    /// <summary>The child is named only when the spawn path already registered it: a child this module does not track
    /// is not a fact it can name, and registering it here would run the profile before the spawn hook does.</summary>
    internal void AfterGaveBirth(EnemyAgent? mother, EnemyAgent? child)
    {
        CheckThread();
        if (ActionEntry(mother, _birthGate) is not { } entry) return;
        if (child == null || ResolveInstance(child) is not { } born) return;
        if (!TryBeginNodeFact(_birthGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyGaveBirth.OutEnemy, entry.Reference);
        fact.Values.Entity(Blocks.TriggerEnemyGaveBirth.OutChild, born);
        RaiseNodeFact(fact);
    }

    /// <summary>The snatcher's spit-out carries the point it spits towards; a tank tentacle's release carries none.</summary>
    internal void AfterPlayerReleased(EnemyAgent? enemy, bool hasPosition, Vector3 position)
    {
        CheckThread();
        if (ActionEntry(enemy, _releasedGate) is not { } entry) return;
        if (!TryBeginNodeFact(_releasedGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyReleasedPlayer.OutEnemy, entry.Reference);
        if (hasPosition && IsFinitePoint(position))
            fact.Values.Vector(Blocks.TriggerEnemyReleasedPlayer.OutPosition, position.x, position.y, position.z);
        RaiseNodeFact(fact);
    }

    /// <summary>The enemy behind a tank tentacle grab, read off the player's own grab source before the receiver clears
    /// it: the grab source is the tentacle's object, and the enemy is the tracked agent above it. A grab source no
    /// tracked enemy owns — a trap, or nothing — answers null.</summary>
    internal EnemyAgent? TankGrabSource(Dam_PlayerDamageBase? player)
    {
        CheckThread();
        if (player == null) return null;
        try
        {
            if (!player.GrabbedByTank) return null;
            var source = player.GrabSource;
            var enemy = source == null ? null : source.GetComponentInParent<EnemyAgent>();
            return enemy != null && ResolveInstance(enemy) != null ? enemy : null;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return null; }
    }

    /// <summary>`forge.trigger.enemy.zone_changed` when the game sets the enemy's course node
    /// (`EnemyAgent.set_CourseNode`): the node is compared by native pointer, and only a node change reads the zone.
    /// The first zone a life is seen in is its baseline and publishes nothing.</summary>
    private void ObserveEnemyZone(EnemyAgent enemy, Entry entry)
    {
        CheckThread();
        if (!CanPublishNodeFacts(_zoneGate) || entry.EnemyPointer != enemy.Pointer) return;
        IntPtr zonePointer;
        try
        {
            var node = enemy.CourseNode;
            if (node == null || node.Pointer == entry.ZoneCourseNode) return;
            entry.ZoneCourseNode = node.Pointer;
            var zone = node.m_zone;
            if (zone == null) return;
            zonePointer = zone.Pointer;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return; }
        if (zonePointer == entry.ZonePointer) return;
        bool baseline = entry.ZonePointer == IntPtr.Zero;
        entry.ZonePointer = zonePointer;
        if (baseline) return;
        EntityReference? where;
        try { where = ZoneOfEnemy(entry.Reference); }
        catch (RuntimeContractException) { return; }
        if (where is not { } zoneReference) return;
        if (!TryBeginNodeFact(_zoneGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyZoneChanged.OutEnemy, entry.Reference);
        fact.Values.Entity(Blocks.TriggerEnemyZoneChanged.OutZone, zoneReference);
        RaiseNodeFact(fact);
    }

    /// <summary>`enemy.attack_performed`: one attack state's own wind-up (`phase` 0) or strike (`phase` 1). The state
    /// names its enemy and the agent it attacks (`ES_EnemyAttackBase.m_attackTarget`); the strike's aim point is the
    /// override's own argument, and the multi-target tank's strike has none.</summary>
    internal void AfterAttackPhase(ES_EnemyAttackBase state, int phase, bool hasPosition, Vector3 position)
    {
        CheckThread();
        EnemyAgent? enemy;
        Agent? target;
        try { enemy = state.m_enemyAgent; target = state.m_attackTarget; }
        catch (Exception caught) { EnemyLog.Contained(caught); return; }
        if (ActionEntry(enemy, _attackGate) is not { } entry) return;
        var resolved = ResolveAgent(target);
        if (!TryBeginNodeFact(_attackGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyAttackPerformed.OutEnemy, entry.Reference);
        fact.Values.Enum(Blocks.TriggerEnemyAttackPerformed.OutPhase, phase);
        if (resolved != null) fact.Values.Entity(Blocks.TriggerEnemyAttackPerformed.OutTarget, resolved);
        if (hasPosition && IsFinitePoint(position))
            fact.Values.Vector(Blocks.TriggerEnemyAttackPerformed.OutPosition, position.x, position.y, position.z);
        RaiseNodeFact(fact);
    }

    /// <summary>`enemy.heard_noise`: a sleeping or patrolling enemy's own `OnNoiseDetected`. The maker is named only when
    /// it is a player (the build's one `INM_NoiseMaker`); the noise type is `NM_NoiseType`'s own order and the distance
    /// the listener's own 0-1 ratio. A noise with no readable position, an unknown type or a non-finite distance is
    /// not published.</summary>
    internal void AfterNoiseHeard(EnemyAgent? enemy, INM_NoiseMaker? maker, Vector3 position, int noiseType, float distance)
    {
        CheckThread();
        if (noiseType is < 0 or > 3 || !float.IsFinite(distance)) return;
        if (ActionEntry(enemy, _noiseGate) is not { } entry) return;
        if (!IsFinitePoint(position)) return;
        global::Player.PlayerAgent? player;
        try { player = maker?.TryCast<global::Player.PlayerAgent>(); }
        catch (Exception caught) { EnemyLog.Contained(caught); player = null; }
        var who = ResolvePlayer(player);
        if (!TryBeginNodeFact(_noiseGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyHeardNoise.OutEnemy, entry.Reference);
        fact.Values.Vector(Blocks.TriggerEnemyHeardNoise.OutPosition, position.x, position.y, position.z);
        fact.Values.Enum(Blocks.TriggerEnemyHeardNoise.OutNoiseType, noiseType);
        fact.Values.Number(Blocks.TriggerEnemyHeardNoise.OutDistance, distance);
        if (who != null) fact.Values.Entity(Blocks.TriggerEnemyHeardNoise.OutMaker, who);
        RaiseNodeFact(fact);
    }

    /// <summary>`enemy.alarm_raised`: the alarm ability's charge-up (`phase` 0) or alarm (`phase` 1), with the enemy
    /// that raised it, the row's subject. An enemy this module does not track publishes nothing.</summary>
    internal void AfterAlarm(EnemyAgent? enemy, int phase)
    {
        CheckThread();
        if (ActionEntry(enemy, _alarmGate) is not { } entry) return;
        if (!TryBeginNodeFact(_alarmGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyAlarmRaised.OutEnemy, entry.Reference);
        fact.Values.Enum(Blocks.TriggerEnemyAlarmRaised.OutPhase, phase);
        RaiseNodeFact(fact);
    }

    /// <summary>`enemy.grabbed_player`: a snatcher's consume (`grab_kind` 0) or a tank tentacle's grab (1). The player
    /// port is required, so a grab of a player no registered kind names is not published.</summary>
    internal void AfterPlayerGrabbed(EnemyAgent? enemy, global::Player.PlayerAgent? player, int kind)
    {
        CheckThread();
        if (ActionEntry(enemy, _grabGate) is not { } entry) return;
        if (ResolvePlayer(player) is not { } grabbed) return;
        if (!TryBeginNodeFact(_grabGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyGrabbedPlayer.OutEnemy, entry.Reference);
        fact.Values.Entity(Blocks.TriggerEnemyGrabbedPlayer.OutPlayer, grabbed);
        fact.Values.Enum(Blocks.TriggerEnemyGrabbedPlayer.OutGrabKind, kind);
        RaiseNodeFact(fact);
    }

    /// <summary>The `enemy_attack_kind` member of one attack state, by its own native class: shooter, flying shooter,
    /// striker, tank, multi-target tank; -1 for a state that is none of them. The flying shooter is asked first
    /// because it is a shooter too.</summary>
    internal static int AttackKindOf(ES_EnemyAttackBase? state)
    {
        if (state == null) return -1;
        try
        {
            if (state.TryCast<ES_ShooterAttackFlyer>() != null) return 1;
            if (state.TryCast<ES_ShooterAttack>() != null) return 0;
            if (state.TryCast<ES_StrikerAttack>() != null) return 2;
            if (state.TryCast<ES_TankAttack>() != null) return 3;
            if (state.TryCast<ES_TankMultiTargetAttack>() != null) return 4;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); }
        return -1;
    }

    /// <summary>The `enemy_detection_type` member of an AI target's own `AgentTarget.m_detectionType`: hibernation,
    /// ability, damage, propagated; -1 for not-detected, undefined or an unreadable target.</summary>
    private static int DetectionIndex(AgentTarget? target)
    {
        if (target == null) return -1;
        int value;
        try { value = (int)target.m_detectionType; }
        catch (Exception caught) { EnemyLog.Contained(caught); return -1; }
        return value is >= (int)AgentTargetDetectionType.HibernationDetection and <= (int)AgentTargetDetectionType.PropagatedDetection
            ? value - (int)AgentTargetDetectionType.HibernationDetection : -1;
    }

    /// <summary>`enemy.awakened` (B3): the player the AI holds on its first awake sample, and whether the wake-up state
    /// the enemy just left was a propagated one (`ES_HibernateWakeUp.m_isPropagatedWakeup`).</summary>
    partial void ReadWakeFacts(Entry entry, in EnemyBehaviorFactsObserver.Sample sample,
        ref EntityReference? waker, ref int? cause, ref bool? propagated)
    {
        waker = ResolvePlayer(sample.Target?.m_agent);
        if (DetectionIndex(sample.Target) is var detection and >= 0) cause = detection;
        try
        {
            var wakeup = entry.Enemy.Locomotion?.HibernateWakeup;
            if (wakeup != null) propagated = wakeup.m_isPropagatedWakeup;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); }
    }

    /// <summary>`enemy.target_acquired` (B3): how the AI detected the target it just acquired.</summary>
    partial void ReadAcquireFacts(in EnemyBehaviorFactsObserver.Sample sample, ref int? how)
    {
        if (DetectionIndex(sample.Target) is var detection and >= 0) how = detection;
    }

    /// <summary>`enemy.scout_scream` (B3): the player the scream state was activated for (`ES_ScoutScream.m_agentTarget`).</summary>
    partial void ReadScoutScreamFacts(Entry entry, ref EntityReference? target)
    {
        Agent? agent;
        try { agent = entry.Enemy.Locomotion?.ScoutScream?.m_agentTarget?.m_agent; }
        catch (Exception caught) { EnemyLog.Contained(caught); return; }
        target = ResolvePlayer(agent);
    }

    /// <summary>The tracked life a native agent names, on this module's host authority and the binding's own gate.</summary>
    private Entry? ActionEntry(EnemyAgent? enemy, EnemyGate gate)
    {
        if (enemy == null || !CanPublishNodeFacts(gate)) return null;
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry) || entry.EnemyPointer != enemy.Pointer
            || Resolve(entry.Reference) == null) return null;
        return entry;
    }

    /// <summary>The entity the enemy's AI holds right now (`EnemyAI.Target.m_agent`), when a registered kind names it.</summary>
    private EntityReference? CurrentTarget(Entry entry)
    {
        Agent? agent;
        try { agent = entry.Enemy.AI?.Target?.m_agent; }
        catch (Exception caught) { EnemyLog.Contained(caught); return null; }
        return ResolveAgent(agent);
    }

    /// <summary>A finite position as the `vector3` wire value, or null.</summary>
    private static double[]? VectorPort(Vector3 position)
        => IsFinitePoint(position) ? new[] { (double)position.x, (double)position.y, (double)position.z } : null;
}
