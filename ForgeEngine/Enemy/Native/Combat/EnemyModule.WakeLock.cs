using Enemies;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeEnemy.Native;

/// <summary>
/// `forge.action.enemy.wake_lock`: a locked sleeper is not woken by gunfire, damage, a player walking up to it, the
/// wake-up state or `forge.action.enemy.awaken`. It takes damage and dies as the game has it. It is independent of
/// the keep-still action: asleep and locked is a target dummy, awake and keep-still is a standing dummy.
///
/// The lock is a flag on the enemy's own life entry (world, life and native instance), enforced only on the host. It
/// holds while the enemy is in a hibernation state; an enemy that is awake is not touched by it, and a locked enemy that
/// later sleeps is held from then on. The wake hooks (`EnemyWakeHooks`, `EnemyBehaviourStateEntry`) ask
/// <see cref="IsWakeLockedAsleep"/>; the lock ends with the action's handle, the enemy's death or the world's end.
/// </summary>
internal sealed partial class EnemyModule
{
    private Entry?[]? _wakeLockedLives; // allocated by the first wake lock
    private int _wakeLockedCount;
    internal bool WakeLockActive => _wakeLockedCount != 0;

    private Entry? WakeLockedEntry(EnemyAgent? enemy)
    {
        if (!WakeLockActive || !Session.IsHost || enemy == null) return null;
        var entry = _wakeLockedLives![enemy.GlobalID];
        if (entry == null || !entry.WakeLocked || entry.EnemyPointer != enemy.Pointer
            || entry.Reference.WorldEpoch != _kernel.WorldEpoch || !enemy.Alive) return null;
        return entry;
    }

    private static bool IsHibernation(EB_States state) => state is EB_States.Hibernating or EB_States.SquidBoss_Hibernating;

    /// <summary>The lock applies: the enemy is locked, alive, current, and in a hibernation behaviour state.</summary>
    internal bool IsWakeLockedAsleep(EnemyAgent? enemy)
        => WakeLockedEntry(enemy) != null && enemy!.AI?.m_behaviour is { } behaviour && IsHibernation(behaviour.m_currentStateName);

    /// <summary>The behaviour machine's guard: a locked sleeper may only stay asleep or die.</summary>
    internal bool WakeLockBehaviourAllows(EnemyBehaviour behaviour, EB_States state)
        => !IsHibernation(behaviour.m_currentStateName) || state == EB_States.Dead || IsHibernation(state)
            || WakeLockedEntry(behaviour.m_ai?.m_enemyAgent) == null;

    private void ForgetWakeLock(Entry entry)
    {
        if (!entry.WakeLocked) return;
        entry.WakeLocked = false;
        if (ReferenceEquals(_wakeLockedLives?[entry.GlobalId], entry))
        { _wakeLockedLives![entry.GlobalId] = null; _wakeLockedCount--; }
    }

    private void WriteWakeLockFlag(Entry entry, bool enabled)
    {
        if (!enabled) { ForgetWakeLock(entry); return; }
        if (entry.WakeLocked) return;
        entry.WakeLocked = true;
        (_wakeLockedLives ??= new Entry?[ushort.MaxValue + 1])[entry.GlobalId] = entry;
        _wakeLockedCount++;
    }

    /// <summary>`forge.query.enemy.wake_locked`: the flag `forge.action.enemy.wake_lock` set on this enemy life,
    /// whether the enemy is asleep or awake right now. A dead, despawned or old-world enemy is refused, never read as unlocked.</summary>
    private void ReadWakeLocked(EvaluationContext context, EvaluationOutputs outputs)
    {
        if (!context.Query.Available || !context.TryEntity(EnemyWakeLockContract.ReadEnemy, out var reference)
            || Resolve(reference) is not { } entry || !entry.Enemy.Alive)
        {
            Log.Reason(RuntimeLogLevel.Error, EnemyLogCodes.WakeLockRefused, ResultCode.StaleEntity);
            throw new RuntimeContractException(ResultCode.StaleEntity, "The enemy life is not current.");
        }
        Log.Reason(RuntimeLogLevel.Info, EnemyLogCodes.WakeLockRead, entry.WakeLocked ? "enabled" : "disabled", reference.Id);
        outputs.Boolean(0, entry.WakeLocked);
    }

    private void ClearWakeLock()
    { _wakeLockedLives = null; _wakeLockedCount = 0; }

    internal CommandResult WakeLock(CommandContext context)
    {
        if (!CanExecute || !context.IsHost) return RefuseWakeLock(ResultCode.AuthorityOrPhase);
        if (context.Boolean(EnemyWakeLockContract.Enabled) is not { } enabled) return RefuseWakeLock(ResultCode.WakeLockEnabledRequired);
        var request = new WakeLockRequest(this, enabled, ActionEffectHandle(context));
        return context.EachTarget(ref request, WakeLockStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained);
    }

    private readonly record struct WakeLockRequest(EnemyModule Module, bool Enabled, RuntimeHandle? Handle);

    private static readonly TargetStep<WakeLockRequest> WakeLockStep = static (ref WakeLockRequest request, EntityReference target, int _, RowWriter rows) =>
    {
        var outcome = request.Module.SetWakeLock(target, request.Enabled, request.Handle);
        rows.Boolean(Blocks.ActionEnemyWakeLock.RowEnabled, request.Enabled).End(outcome);
        bool confirmed = outcome.Commit == Commit.Confirmed;
        request.Module.Log.Reason(confirmed ? RuntimeLogLevel.Info : RuntimeLogLevel.Error,
            confirmed ? EnemyLogCodes.WakeLockChanged : EnemyLogCodes.WakeLockRefused,
            outcome.Code == ResultCode.WakeLockEnemyDead ? outcome.Code : confirmed ? request.Enabled ? "enabled" : "disabled" : outcome.Code,
            target.Id, EnemyWakeLockContract.Binding);
    };

    private CommandResult RefuseWakeLock(string code)
    {
        Log.Reason(RuntimeLogLevel.Error, EnemyLogCodes.WakeLockRefused, code);
        return CommandResult.Rejected(code);
    }

    private BehaviorOutcome SetWakeLock(EntityReference target, bool enabled, RuntimeHandle? handle)
    {
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        // A corpse stays tracked until it despawns and a lock has nothing to hold on it; a batch over every enemy meets it.
        if (!entry.Enemy.Alive) return BehaviorOutcome.Already(ResultCode.WakeLockEnemyDead);
        var ai = entry.Enemy.AI;
        var behaviour = ai?.m_behaviour;
        var locomotion = ai?.m_locomotion;
        if (behaviour == null || locomotion == null) return BehaviorOutcome.Refused(ResultCode.WakeLockStateUnavailable);
        var result = InstallActionState(new ActionState
        {
            Entry = entry,
            Kind = ActionStateKind.WakeLock,
            Ai = ai,
            Behaviour = behaviour,
            Locomotion = locomotion
        }, new(Type: enabled ? 1 : 0), handle);
        return result.Commit == Commit.Confirmed ? new(Status.Succeeded, Commit.Confirmed, ResultCode.WakeLockChanged) : result;
    }
}
