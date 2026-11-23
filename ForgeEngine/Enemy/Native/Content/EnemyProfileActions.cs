using System;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>Host authority gate and native write path for `forge.action.enemy.phase_set`, and the read/write
/// surface the other `forge.enemy.profile` rows were surveyed against.
///
/// A SquidBoss phase is one of the few enemy values the game replicates on its own: `SquidBossBehaviour` overrides
/// `EnemyBehaviour.GetExtraStateDataForCapture` / `SetIncomingExtraStateData`, whose payload is
/// `EnemySync.pExtraBehaviourStateData` — a struct carrying exactly one field, `bossPhase`, a `byte` — sent over
/// `EnemySync.m_aiExtraBehaviourStatePacket`. So writing `Phase` through the game's own public setter on the host
/// is captured and sent by the game in the same frame instead of having to be replicated by Forge.
///
/// The setter itself validates nothing (it is an auto-property), so this handler owns the value domain: the phases
/// the game's own ability choice knows. Every boss state asks for its abilities with
/// `AbilityCondition = Phase + 1`, and `AbilityCondition` names only `BossPhase0`..`BossPhase5`, so a written phase
/// outside 0..5 matches no ability and is refused instead of being written.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The phases `AbilityCondition.BossPhase0`..`BossPhase5` select. The read row keeps the replicated
    /// byte's domain (<see cref="EnemyPhaseReader"/>), because the game's own weakspot step only increments the
    /// phase and nothing bounds it there.</summary>
    internal const int PhaseMinimum = EnemyPhaseReader.Minimum;
    internal const int PhaseMaximum = 5;

    /// <summary>Multi-target phase switch. One row is written per recipient in the plan's own order. A committed
    /// row is one whose phase was read back as the value that was set; a write whose readback disagrees stays
    /// unknown rather than being claimed, and a refusal before the native setter is a rejected row.
    ///
    /// `internal`, not `private`, so the focused suite can dispatch it through a kernel-built command context and
    /// assert the rows it writes; the registration reaches it by the handler name
    /// `EnemyProfileContract.PhaseSetHandler` registers in `EnemyActionFamilies`.</summary>
    internal CommandResult PhaseSet(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        long phaseValue = context.Integer(EnemyProfileContract.PhaseSlot) ?? long.MinValue;
        if (phaseValue < PhaseMinimum || phaseValue > PhaseMaximum) return CommandResult.Rejected(ResultCode.PhaseOutOfRange);
        int phase = (int)phaseValue;
        // `expected_phase` is optional: unwired, the switch applies whatever phase the enemy is in (-1 = no check).
        int expected = -1;
        if (context.Integer(EnemyProfileContract.ExpectedPhaseSlot) is { } expectedValue)
        {
            if (expectedValue < PhaseMinimum || expectedValue > PhaseMaximum) return CommandResult.Rejected(ResultCode.PhaseOutOfRange);
            expected = (int)expectedValue;
        }

        var request = new PhaseSetRequest(this, phase, expected, ActionEffectHandle(context));
        return context.EachTarget(ref request, PhaseSetStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyPhaseSet.RowTargetCount);
    }

    private readonly record struct PhaseSetRequest(EnemyModule Module, int Phase, int Expected, RuntimeHandle? Handle);

    /// <summary>The row reports the phase it is about: the value the step read back where it made the reading — the
    /// phase it set, or the phase it found when the request expected another — and the value the plan asked for on a
    /// row that never reached a phase receiver. The declared `phase` column has no absent form, and a refusal row
    /// still knows the phase the step asked for.</summary>
    private static readonly TargetStep<PhaseSetRequest> PhaseSetStep = static (ref PhaseSetRequest request, EntityReference target, int _, RowWriter rows) =>
    {
        var outcome = request.Module.PhaseSetOne(request, target, out var observed);
        rows.Integer(Blocks.ActionEnemyPhaseSet.RowPhase, observed).End(outcome);
    };

    private BehaviorOutcome PhaseSetOne(in PhaseSetRequest request, EntityReference target, out int observed)
    {
        var phase = request.Phase;
        observed = phase;
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        var enemy = entry.Enemy;
        // The phase lives on the enemy's behaviour machine, so everything the write depends on is re-proved
        // before the setter is reached: the AI, the behaviour instance behind the `enemy-type` the instance
        // was registered with, and the enemy pointer the reference names.
        var ai = enemy.AI;
        if (ai == null || ai.m_enemyAgent == null || ai.m_enemyAgent.Pointer != entry.EnemyPointer)
            return BehaviorOutcome.Refused(ResultCode.AiOwnerMismatch);
        if (ai.m_behaviour == null || ai.m_behaviour.Pointer == IntPtr.Zero || ai.m_behaviour.m_ai == null
            || ai.m_behaviour.m_ai.Pointer != ai.Pointer)
            return BehaviorOutcome.Refused(ResultCode.MissingPhaseReceiver);
        if (!EnemyProfileWrite.TryReadPhase(ai.m_behaviour, out int current, out var boss))
            return BehaviorOutcome.Refused(ResultCode.NotAPhaseReceiver);
        observed = current;
        if (request.Expected >= 0 && current != request.Expected) return BehaviorOutcome.Refused(ResultCode.PhaseMismatch);

        // Entering the native setter is the only place the world is written. The write is one call plus the
        // game's own weakspot step, and the readback is its only proof, so a failure after it stays unknown.
        int after;
        try
        {
            var installed = InstallActionState(new ActionState
            {
                Entry = entry,
                Kind = ActionStateKind.Phase,
                Ai = ai,
                Boss = boss
            }, new(Type: phase), request.Handle);
            if (installed.Commit != Commit.Confirmed) return installed;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }
        try
        {
            if (!CanExecute) return BehaviorOutcome.Unseen(ResultCode.AuthorityOrPhase);
            var currentEntry = Resolve(target);
            if (currentEntry == null || !ReferenceEquals(currentEntry, entry) || currentEntry.EnemyPointer != entry.EnemyPointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            var currentAi = enemy.AI;
            if (currentAi == null || currentAi.m_enemyAgent == null
                || currentAi.m_enemyAgent.Pointer != entry.EnemyPointer
                || currentAi.m_behaviour == null || currentAi.m_behaviour.m_ai == null
                || currentAi.m_behaviour.m_ai.Pointer != currentAi.Pointer
                || !EnemyProfileWrite.TryReadPhaseValue(currentAi.m_behaviour, out after))
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        observed = after;
        if (after != phase) return BehaviorOutcome.Unseen(ResultCode.UnexpectedPhaseReadback);
        return BehaviorOutcome.Committed();
    }
}

/// <summary>The one native write path this slice owns, kept in its own type so the game assembly is reached from
/// exactly one audited member and the handlers stay readable without it. The phase member itself — the
/// `SquidBossBehaviour` the write goes through and the phase it holds — is read by `EnemyPhaseReader`, the one
/// spelling the value row `forge.query.enemy.phase` reads through too.
///
/// Two rows of `forge.enemy.profile` were surveyed and are deliberately absent here:
///   * `limb_profile` — `Dam_EnemyDamageLimb.m_health`, `m_healthMax`, `m_weakspotDamageMulti` and
///     `m_armorDamageMulti` are all registered `public` interop fields, so they are writable; but none of them is
///     replicated. The game replicates damage as packets (`SNet_Packet<pBulletDamageData>` and the damage entry
///     points) and every peer computes its own limb health locally, so a single-sided field write is exactly the
///     host/client divergence the row's own evidence warns about. There is no synced limb-health entry point in
///     this build, so no write is attempted.
///   * `perception_profile` — `EnemyDetection.m_movementDetectionDistance`, `m_noiseDetectionRange`,
///     `m_detectionBuildupSpeed` and `m_noiseDetectionOn` are per-instance public fields with no replication
///     channel at all.</summary>
internal static class EnemyProfileWrite
{
    /// <summary>Reads a boss's current phase and hands back the behaviour instance the write goes through: the
    /// shared phase read, which is also the read row's own. A behaviour machine that is not a `SquidBossBehaviour`
    /// — or one whose cast the interop cannot perform — answers false instead of being treated as a boss at phase
    /// zero.</summary>
    internal static bool TryReadPhase(EnemyBehaviour behaviour, out int phase, out SquidBossBehaviour boss)
        => EnemyPhaseReader.TryRead(behaviour, out phase, out boss);

    /// <summary>Reads only the phase back, for the post-commit proof. The cast is re-done here because the write
    /// may have replaced nothing: this is the same instance the setter was called on, proven again.</summary>
    internal static bool TryReadPhaseValue(EnemyBehaviour behaviour, out int phase)
        => EnemyPhaseReader.TryRead(behaviour, out phase, out _);

    /// <summary>One phase submission through the game's own public setter, followed by the boss's own weakspot
    /// step so the phase the game now holds is acted on by the same native code the game uses for it. The setter
    /// performs no validation (an auto-property), so the caller has already bounded the value to the one byte the
    /// replicated field carries. `ActivateWeakspotIfNeeded` is itself a no-op when the current phase needs no
    /// change, so it is not a second decision.</summary>
    internal static void SetPhase(SquidBossBehaviour boss, int phase)
    {
        boss.Phase = phase;
        boss.ActivateWeakspotIfNeeded();
    }
}
