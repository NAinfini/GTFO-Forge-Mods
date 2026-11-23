using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>Host authority gate and native write paths for the two registered `C-enemy-combat` rows.
///
/// `forge.action.combat.stagger` writes through the game's one hitreact entry point,
/// `ES_HitreactBase.CanHitreact(ES_HitreactType, bool)` followed by
/// `ActivateState(ES_HitreactType, ImpactDirection, bool, Agent, Vector3, DamageNoiseLevel)`, which the game
/// reaches the same way from its own damage path. The reaction strength is a structural parameter on the row
/// because that entry point cannot omit it. The attacker is the native agent of the row's `source` role, resolved
/// through the owner of that exact life, and the impact direction and position come from where that agent stands
/// relative to the target; a source no owner claims passes no attacker and the neutral direction. The noise level
/// stays the neutral value.
///
/// `forge.action.combat.attack_interrupt` names its recipients directly — `enemies`, entities of the `gtfo.enemy`
/// kind — and interrupts whatever attack they are performing or charging. The attack is proven through the
/// locomotion's own attack states (`ES_EnemyAttackBase.IsPerformingAttack`/`IsChargingAttack`) and the interruption
/// itself is the same hitreact state machine `stagger` uses, because an attack state is left only by the state
/// machine that owns it. An enemy that is not mid-attack is refused with `enemy-not-attacking` and a `none` commit
/// state, before any native call: nothing there was interrupted and nothing may be claimed.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The declared `reaction` enum set's members, in the catalog's own order, each paired with the
    /// `ES_HitreactType` it names. `ToDeath` and `InstantRagdollDeath` are deliberately not members: those are
    /// kills and belong to `combat.damage`, not to a stagger. A structural enum parameter crosses the handler
    /// boundary as the member name the kernel resolved the compiled index into, so the name is what this handler
    /// reads and this one table is what turns it back into the native value.
    ///
    /// The incoming damage window reads the same three when it answers whether a received hit staggered its
    /// target, so the row that submits a stagger and the row that reports one cannot disagree about which
    /// reactions are stagger-class.</summary>
    private static readonly (string Name, ES_HitreactType Reaction)[] StaggerReactions =
    {
        ("micro", ES_HitreactType.Micro), ("light", ES_HitreactType.Light), ("heavy", ES_HitreactType.Heavy)
    };

    /// <summary>Whether one reaction is a member of the declared set above.</summary>
    internal static bool IsStaggerReaction(ES_HitreactType reaction)
    {
        foreach (var member in StaggerReactions) if (member.Reaction == reaction) return true;
        return false;
    }

    /// <summary>The native reaction one declared member name names. A name outside the set is refused by the
    /// caller rather than mapped onto a neighbouring member.</summary>
    private static bool TryStaggerReaction(string? member, out ES_HitreactType reaction)
    {
        foreach (var entry in StaggerReactions)
            if (entry.Name == member) { reaction = entry.Reaction; return true; }
        reaction = default;
        return false;
    }

    /// <summary>The declared `immunity_policy` member names, in the catalog's own order, and the indexes the
    /// policy branches below compare against.</summary>
    private const string ImmunityPolicyRespectName = "respect";
    private const string ImmunityPolicyIgnoreName = "ignore";
    private const int ImmunityPolicyRespect = 0;
    private const int ImmunityPolicyIgnore = 1;

    /// <summary>The one reaction an interruption is submitted with. The interrupt row has no reaction parameter:
    /// a caller interrupting an attack is not choosing how hard to hit, and `Light` is the member the game's own
    /// attack interruption leaves behind. The readback of `CurrentReactionType` is what proves the write landed.</summary>
    private const ES_HitreactType InterruptReaction = ES_HitreactType.Light;

    /// <summary>Multi-target stagger. One row is written per recipient in the plan's own order. The hitreact
    /// entry point returns void, so the readback of `CurrentReactionType` is the only proof a write landed; a
    /// submission whose readback disagrees stays unknown rather than being claimed, and every refusal before the
    /// native call is a rejected row.</summary>
    internal CommandResult Stagger(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        // A structural enum parameter arrives as the member name the kernel resolved the compiled index into, so
        // each is read by name and turned back into the value this handler branches on; a name the declared set
        // does not carry is refused rather than mapped onto a neighbouring member.
        string? reactionName = context.Text(EnemyCombatContract.StaggerReactionSlot);
        if (reactionName == null || !TryStaggerReaction(reactionName, out var reaction)) return CommandResult.Rejected(ResultCode.ReactionUnsupported);
        int immunity = context.Text(EnemyCombatContract.StaggerImmunitySlot) switch
        {
            ImmunityPolicyRespectName => ImmunityPolicyRespect,
            ImmunityPolicyIgnoreName => ImmunityPolicyIgnore,
            _ => -1
        };
        if (immunity < 0) return CommandResult.Rejected(ResultCode.ImmunityPolicyUnsupported);
        // `source` is a required, kernel-validated role. It is resolved through the owner of that exact life to the
        // native attacker the hitreact entry point takes; a source no owner claims (stale or another kind) passes
        // no attacker rather than an invented one.
        if (!context.TryEntity(EnemyCombatContract.StaggerSourceSlot, out var source))
            throw new RuntimeContractException("shape-port", "stagger source");
        Agent? attacker;
        try { attacker = NativeAgentOf(source); }
        catch (RuntimeContractException error) { return CommandResult.Rejected(error.Code, error.Message); }
        var request = new StaggerRequest(this, attacker, reaction, reactionName, immunity);
        var outcome = context.EachTarget(ref request, StaggerStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionCombatStagger.RowTargetCount);
        if (request.Diagnostics == null) return outcome;
        return outcome.WithDetail(RuntimeJson.From(new { diagnostics = request.Diagnostics }).GetRawText());
    }

    /// <summary>What every stagger target shares. <see cref="Diagnostics"/> is made only when a target has one to
    /// report: the micro adapter or a failed immunity override.</summary>
    private struct StaggerRequest
    {
        internal StaggerRequest(EnemyModule module, Agent? attacker, ES_HitreactType reaction, string reactionName, int immunity)
        { Module = module; Attacker = attacker; Reaction = reaction; ReactionName = reactionName; Immunity = immunity; Diagnostics = null; }
        internal readonly EnemyModule Module;
        internal readonly Agent? Attacker;
        internal readonly ES_HitreactType Reaction;
        internal readonly string ReactionName;
        internal readonly int Immunity;
        internal List<object>? Diagnostics;
    }

    private static readonly TargetStep<StaggerRequest> StaggerStep = static (ref StaggerRequest request, EntityReference target, int _, RowWriter rows) =>
    {
        var outcome = request.Module.StaggerOne(ref request, target, out var observedReaction);
        rows.Text(Blocks.ActionCombatStagger.RowReaction, observedReaction)
            .End(outcome.Status, outcome.Code);
    };

    /// <summary>One stagger target. <paramref name="observedReaction"/> is the reaction the row reports: the
    /// requested one, or `light` where the ground micro adapter submitted that instead.</summary>
    private BehaviorOutcome StaggerOne(ref StaggerRequest request, EntityReference target, out string observedReaction)
    {
        observedReaction = request.ReactionName;
        var attacker = request.Attacker; var reaction = request.Reaction; var immunity = request.Immunity;
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        var enemy = entry.Enemy;
        if (enemy.Damage == null || !enemy.Damage.IsSetup || enemy.Damage.Pointer == IntPtr.Zero)
            return BehaviorOutcome.Refused(ResultCode.MissingHealthReceiver);
        if (enemy.Damage.Owner == null || enemy.Damage.Owner.Pointer != entry.EnemyPointer)
            return BehaviorOutcome.Refused(ResultCode.HealthReceiverOwnerMismatch);
        if (!enemy.Alive || !(enemy.Damage.Health > 0)) return BehaviorOutcome.Refused(ResultCode.NotAlive);
        var locomotion = enemy.Locomotion;
        if (locomotion == null || locomotion.Pointer == IntPtr.Zero) return BehaviorOutcome.Refused(ResultCode.MissingLocomotion);
        if (locomotion.m_agent?.Pointer != entry.EnemyPointer) return BehaviorOutcome.Refused(ResultCode.MissingLocomotion);
        if (locomotion.Hitreact == null || locomotion.Hitreact.Pointer == IntPtr.Zero)
            return BehaviorOutcome.Refused(ResultCode.NoHitreactEntry);

        var hitreact = locomotion.Hitreact;
        var hitreactPointer = hitreact.Pointer;
        // Ground ActivateState(Micro) only updates its packet cache and returns. Light is its least
        // available native interrupt; expose that adaptation and the actual reaction, rather than a false Micro.
        bool microAdapter;
        ES_HitreactType nativeReaction;
        float beforeEnd, beforeRetrigger;
        IntPtr beforeState;
        HitOrigin origin;
        try
        {
            origin = HitOrigin.Of(enemy, attacker);
            microAdapter = reaction == ES_HitreactType.Micro && hitreact.TryCast<ES_HitreactFlyer>() == null;
            nativeReaction = microAdapter ? ES_HitreactType.Light : reaction;
            observedReaction = microAdapter ? "light" : request.ReactionName;
            beforeEnd = hitreact.m_endTime; beforeRetrigger = hitreact.m_retriggerTime;
            beforeState = locomotion.CurrentState?.Pointer ?? IntPtr.Zero;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.ReadbackException); }
        ActionKey? overrideHandle = null;
        BehaviorOutcome outcome = BehaviorOutcome.Committed();
        try
        {
            try
            {
                if (immunity == ImmunityPolicyIgnore && hitreact.HitreactForbidden)
                {
                    overrideHandle = new ActionKey(default, checked(++_staggerOverrideSequence));
                    outcome = InstallActionState(new ActionState
                    {
                        Entry = entry,
                        Kind = ActionStateKind.Stagger,
                        Locomotion = locomotion,
                        Hitreact = hitreact
                    }, new(Type: 0), overrideHandle);
                    if (outcome.Commit != Commit.Confirmed)
                    {
                        (request.Diagnostics ??= new()).Add(new { target, reason = "immunity-override-failed", nativeCode = outcome.Code });
                        outcome = outcome.Commit == Commit.None
                            ? BehaviorOutcome.Refused(outcome.Code == ResultCode.ReceiverChangedDuringCommit ? outcome.Code : ResultCode.HitreactGateException)
                            : BehaviorOutcome.Unseen(outcome.Code is ResultCode.NativeCommitException or ResultCode.ReadbackException
                                ? outcome.Code : ResultCode.UnexpectedReactionReadback);
                    }
                }
                if (outcome.Commit == Commit.Confirmed
                    && !hitreact.CanHitreact(nativeReaction, immunity == ImmunityPolicyIgnore))
                    outcome = BehaviorOutcome.Refused(ResultCode.StaggerImmune);
            }
            catch (Exception caught) { EnemyLog.Contained(caught); outcome = BehaviorOutcome.Refused(ResultCode.HitreactGateException); }
            if (outcome.Commit == Commit.Confirmed)
            {
                try
                {
                    if (!CanExecute) outcome = BehaviorOutcome.Refused(ResultCode.AuthorityOrPhase);
                    else if (Resolve(target) != entry || enemy.Locomotion?.Pointer != locomotion.Pointer
                        || locomotion.Hitreact?.Pointer != hitreactPointer)
                        outcome = BehaviorOutcome.Refused(ResultCode.ReceiverChangedDuringCommit);
                    else hitreact.ActivateState(nativeReaction, origin.Direction, origin.AttackerIsPlayer, attacker,
                        origin.Position, DamageNoiseLevel.Normal);
                }
                catch (Exception caught) { EnemyLog.Contained(caught); outcome = BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }
            }
        }
        finally
        {
            if (overrideHandle is { } loan)
            {
                try { RestoreActionKey(loan, ActionStateKind.Stagger); }
                catch (Exception error)
                {
                    // The same Runtime ledger retains the failed loan for its next cleanup tick.
                    Log.Exception(RuntimeLogLevel.Error, "enemy.stagger-restore-failed", error);
                    (request.Diagnostics ??= new()).Add(new { target, reason = "stagger-restore-failed" });
                    outcome = BehaviorOutcome.Unseen(ResultCode.NativeCommitException);
                }
            }
        }
        if (outcome.Commit != Commit.Confirmed) return outcome;
        try
        {
            if (!CanExecute) return BehaviorOutcome.Unseen(ResultCode.AuthorityOrPhase);
            var current = Resolve(target);
            if (current == null || !ReferenceEquals(current, entry) || enemy.Locomotion?.Pointer != locomotion.Pointer
                || locomotion.Hitreact == null || locomotion.Hitreact.Pointer != hitreactPointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            if (hitreact.CurrentReactionType != nativeReaction || locomotion.CurrentState?.Pointer != hitreactPointer
                || locomotion.CurrentStateEnum != hitreact.m_stateEnum || !float.IsFinite(hitreact.m_endTime)
                || !float.IsFinite(hitreact.m_retriggerTime)
                || beforeState == hitreactPointer && hitreact.m_endTime == beforeEnd && hitreact.m_retriggerTime == beforeRetrigger)
                return BehaviorOutcome.Unseen(ResultCode.UnexpectedReactionReadback);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        if (microAdapter) (request.Diagnostics ??= new()).Add(new
        {
            target,
            adapter = "Light",
            reason = "native-ground-micro-has-no-interrupt",
            requestedReaction = "micro",
            nativeReaction = "light"
        });
        return BehaviorOutcome.Committed();
    }

    private long _staggerOverrideSequence;

    /// <summary>The impact direction, attacker kind and position a hitreact write carries, from where the attacker
    /// stands relative to the struck enemy's facing. No attacker gives the neutral values.</summary>
    internal readonly record struct HitOrigin(ImpactDirection Direction, bool AttackerIsPlayer, Vector3 Position)
    {
        internal static HitOrigin Of(EnemyAgent enemy, Agent? attacker)
        {
            if (attacker == null) return new(ImpactDirection.Unspecified, false, Vector3.zero);
            var position = attacker.Position;
            var at = enemy.Position;
            var delta = new Vector3(position.x - at.x, 0f, position.z - at.z);
            var forward = enemy.Forward;
            float ahead = delta.x * forward.x + delta.z * forward.z;
            float side = delta.x * forward.z - delta.z * forward.x;
            var direction = Math.Abs(side) > Math.Abs(ahead)
                ? side > 0f ? ImpactDirection.Right : ImpactDirection.Left
                : ahead >= 0f ? ImpactDirection.Front : ImpactDirection.Back;
            return new(direction, attacker.TryCast<global::Player.PlayerAgent>() != null, position);
        }
    }

    /// <summary>Multi-target attack interrupt. One row is written per recipient in the plan's own order, and the
    /// row's subject is the enemy: nothing about the attack that was interrupted is reported, because the attack
    /// state is what the write consumed, not what the caller keeps. An enemy whose locomotion shows no performing
    /// or charging attack is refused with `enemy-not-attacking` and commits nothing — that is the whole answer for
    /// an enemy that has nothing to interrupt, and it is decided before any native write.</summary>
    internal CommandResult AttackInterrupt(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        // `source` is the required context role every action of this family carries; it is resolved to the native
        // attacker the hitreact entry point takes, and an unclaimed source passes none.
        if (!context.TryEntity(EnemyCombatContract.AttackInterruptSourceSlot, out var source))
            throw new RuntimeContractException("shape-port", "attack_interrupt source");
        Agent? attacker;
        try { attacker = NativeAgentOf(source); }
        catch (RuntimeContractException error) { return CommandResult.Rejected(error.Code, error.Message); }
        var request = new InterruptRequest(this, attacker);
        return context.EachTarget(ref request, InterruptStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionCombatAttackInterrupt.RowTargetCount);
    }

    private readonly record struct InterruptRequest(EnemyModule Module, Agent? Attacker);

    private static readonly TargetStep<InterruptRequest> InterruptStep = static (ref InterruptRequest request, EntityReference target, int _, RowWriter rows)
        => request.Module.InterruptOne(request.Attacker, target, rows);

    private void InterruptOne(Agent? attacker, EntityReference target, RowWriter rows)
    {
        var entry = Resolve(target);
        if (entry == null) { rows.End(Status.Rejected, UnresolvedCode(target)); return; }
        var enemy = entry.Enemy;
        if (enemy.Damage == null || !enemy.Damage.IsSetup || enemy.Damage.Pointer == IntPtr.Zero)
        { rows.End(Status.Rejected, ResultCode.MissingHealthReceiver); return; }
        if (enemy.Damage.Owner == null || enemy.Damage.Owner.Pointer != entry.EnemyPointer)
        { rows.End(Status.Rejected, ResultCode.HealthReceiverOwnerMismatch); return; }
        if (!enemy.Alive || !(enemy.Damage.Health > 0)) { rows.End(Status.Rejected, ResultCode.NotAlive); return; }
        var locomotion = enemy.Locomotion;
        if (locomotion == null || locomotion.Pointer == IntPtr.Zero)
        { rows.End(Status.Rejected, ResultCode.MissingLocomotion); return; }

        // The only evidence that this enemy has an attack to interrupt is its own attack states. A read that
        // throws proves nothing, so it is a refusal rather than an assumed attack.
        bool inFlight;
        try { inFlight = EnemyCombatWrite.IsAttacking(locomotion); }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Rejected, ResultCode.AttackStateUnreadable); return; }
        if (!inFlight) { rows.End(Status.Rejected, ResultCode.EnemyNotAttacking); return; }
        if (locomotion.Hitreact == null || locomotion.Hitreact.Pointer == IntPtr.Zero)
        { rows.End(Status.Rejected, ResultCode.NoHitreactEntry); return; }

        var hitreact = locomotion.Hitreact;
        var hitreactPointer = hitreact.Pointer;
        // The interruption is the game's own hitreact state machine, the same entry `stagger` uses: an attack
        // state is left only by the state machine that owns it. The game's gate is asked and its answer taken;
        // this row has no immunity policy, so nothing here overrides it. A gate that throws is a refusal — no
        // write was attempted — while a write that throws is an unknown commit, because the call may have
        // reached the state machine before it failed.
        bool allowed;
        try { allowed = hitreact.CanHitreact(InterruptReaction, false); }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Rejected, ResultCode.HitreactGateException); return; }
        if (!allowed) { rows.End(Status.Rejected, ResultCode.AttackInterruptForbidden); return; }
        HitOrigin origin;
        try { origin = HitOrigin.Of(enemy, attacker); }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Rejected, ResultCode.ReadbackException); return; }
        try
        {
            hitreact.ActivateState(InterruptReaction, origin.Direction, origin.AttackerIsPlayer, attacker,
                origin.Position, DamageNoiseLevel.Normal);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Failed, ResultCode.NativeCommitException); return; }
        try
        {
            if (!CanExecute) { rows.End(Status.Failed, ResultCode.AuthorityOrPhase); return; }
            var current = Resolve(target);
            if (current == null || !ReferenceEquals(current, entry) || enemy.Locomotion?.Pointer != locomotion.Pointer
                || locomotion.Hitreact == null || locomotion.Hitreact.Pointer != hitreactPointer)
            { rows.End(Status.Failed, ResultCode.ReceiverChangedDuringCommit); return; }
            if (locomotion.Hitreact.CurrentReactionType != InterruptReaction)
            { rows.End(Status.Failed, ResultCode.UnexpectedReactionReadback); return; }
        }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Failed, ResultCode.ReadbackException); return; }
        rows.End(Status.Succeeded, ResultCode.Committed);
    }
}

/// <summary>The native state questions and the one native write path this family owns, kept in its own type so the
/// game assembly is reached from exactly one audited member and the handlers stay readable without it.
///
/// `EnemyLocomotion` owns the enemy's attack states, one field per attack archetype, all derived from
/// `ES_EnemyAttackBase`. That is the only route from an enemy to its own attack: the attack state names its owner
/// and never the other way round, so the four declared fields are what "this enemy is attacking" means.</summary>
internal static class EnemyCombatWrite
{
    /// <summary>Whether any attack state this locomotion owns reports a hit still in flight — performing or
    /// charging, which are the two questions `ES_EnemyAttackBase` answers. A state that is absent is not attacking,
    /// and a state whose wrapper has no native instance is not asked.</summary>
    internal static bool IsAttacking(EnemyLocomotion locomotion)
    {
        foreach (var attack in AttackStates(locomotion))
            if (attack != null && attack.Pointer != IntPtr.Zero
                && (attack.IsPerformingAttack() || attack.IsChargingAttack())) return true;
        return false;
    }

    /// <summary>The four attack states `EnemyLocomotion` declares, in its own declaration order. This is the whole
    /// set the build puts on an enemy: striker, tank, tank multi-target and shooter.</summary>
    private static ES_EnemyAttackBase?[] AttackStates(EnemyLocomotion locomotion) => new ES_EnemyAttackBase?[]
    {
        locomotion.StrikerAttack, locomotion.TankAttack, locomotion.TankMultiTargetAttack, locomotion.ShooterAttack
    };

}
