using System;
using System.Collections.Generic;
using System.Globalization;
using Agents;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeEnemy.Native;

/// <summary>One tracked life's wake rules, plus the once-per-life flags that keep the once-per-type record off the
/// hot path after its first write.</summary>
internal sealed class EnemyWakeLife
{
    internal EnemyWakeLife(EnemyWakeRule rule, uint typeId, string? source) { Rule = rule; TypeId = typeId; Source = source; }

    internal EnemyWakeRule Rule { get; }
    internal uint TypeId { get; }
    internal string? Source { get; }
    internal bool SneakReported, GunfireReported, DetectionReported, HitreactReported;
}

/// <summary>
/// The wake rules of a sleeping enemy (`forge.enemyWake`, the website's 「唤醒」 group).
///
/// What the game does, build 20403457 (GameAssembly.dll, reviewed in evidence/enemy-behavior-hooks.json):
///   * Hearing. `EB_Hibernating.UpdateDetection` (and the two patrolling behaviours) call
///     `EnemyDetection.UpdateHibernationDetection`, which skips every target farther than the gunfire distance
///     (`GetWeaponDetectionDistance`: `weaponDetectionDistanceMax` lerped to `Min` by node distance) and then asks
///     `DetectOnNoiseDistance_Conditional_AnimatedWindow(agentTarget, movementDetectionDistance, …, out output)`. That
///     method stores the target's current `Agent.NoiseType` in `AgentTarget.noiseType` and answers the detection value:
///     1000 (at once) for a shot anywhere inside the gunfire distance or anything within 1 m; 0.95 for a walk inside the
///     walking distance; for a sneak, a walk's value only while the noise wave is on, or else only closer than a fixed
///     1.9 m after the wave's quiet phase — there is no per-type sneak member.
///   * Damage. `Dam_EnemyDamageBase.ProcessReceivedDamage` (host) calls `EnemyAgent.RegisterDamageInflictor`, which
///     calls `EnemyAI.SetDetectedAgent(attacker, DamageDetection)`: the attacker's target is marked auto-detected and
///     the next hibernation detection wakes the enemy. A hit over the stagger threshold also asks
///     `ES_Hitreact.CanHitreact` and then `ActivateState`, which throws the sleeper out of its hibernate state and
///     alerts its neighbours; `CanHitreact` has no case for a sleeping enemy.
///
/// So the walking and gunfire distances stay the native `EnemyDetection` block the website exports, and the three
/// values with no native member are applied here, on the host only:
///   * the noise window's postfix replaces the answer for a sneak (inside the authored distance and not occluded: at
///     least a walk's 0.95; otherwise 0) and, when gunfire does not wake, for a shot (0);
///   * with damage not waking, native target registration runs; only this hit's new automatic wake is removed,
///     and a non-lethal hit reaction is refused.
/// Each hook returns after one boolean read when no loaded profile authors its rule, and the noise window reads the
/// target's noise before its one keyed lookup, so a walking or idle target never reaches the lookup.
/// </summary>
internal sealed partial class EnemyModule
{
    // The state is on the existing Entry (world + life + exact native instance), not on an enemy type.
    // The dense native GlobalID index is only an index of those same Entries: hot hooks do no dictionary/string read.
    private Entry?[]? _stationaryLives; // allocated by the first stationary enemy
    private int _stationaryCount;
    internal bool StationaryActive => _stationaryCount != 0;
    private void ReadStationary(EvaluationContext context, EvaluationOutputs outputs)
    {
        if (!context.Query.Available || !context.TryEntity(EnemyStationaryContract.ReadEnemy, out var reference)
            || Resolve(reference) is not { } entry || !entry.Enemy.Alive)
        {
            Log.Reason(RuntimeLogLevel.Error, EnemyLogCodes.StationaryRefused, ResultCode.StaleEntity);
            throw new RuntimeContractException(ResultCode.StaleEntity, "The enemy life is not current.");
        }
        bool value = entry.Stationary && entry.Enemy.Alive;
        Log.Reason(RuntimeLogLevel.Info, EnemyLogCodes.StationaryRead, value ? "enabled" : "disabled", reference.Id);
        outputs.Boolean(0, value);
    }
    private Entry? StationaryEntry(EnemyAgent? enemy)
    {
        if (!StationaryActive || !Session.IsHost || enemy == null) return null;
        var entry = _stationaryLives![enemy.GlobalID];
        if (entry == null || !entry.Stationary || entry.EnemyPointer != enemy.Pointer
            || entry.Reference.WorldEpoch != _kernel.WorldEpoch || !enemy.Alive) return null;
        return entry;
    }
    /// <summary>The locomotion states that carry an enemy from one place to another: walking or running a path, flying,
    /// jumping a shortcut or a ladder. Keeping an enemy still refuses exactly these; every other state (waking, screaming,
    /// attacking, reacting to a hit, dying) is the game's own.</summary>
    internal static bool IsMovementState(ES_StateEnum state) => state is ES_StateEnum.PathMove or ES_StateEnum.PathMoveFlyer
        or ES_StateEnum.FloaterFly or ES_StateEnum.ShortcutJump or ES_StateEnum.ClimbLadder or ES_StateEnum.Jump;
    internal bool StationaryLocomotionAllows(EnemyLocomotion locomotion, ES_StateEnum state)
        => !IsMovementState(state) || StationaryEntry(locomotion.m_agent) == null;
    private void ForgetStationary(Entry entry)
    {
        if (!entry.Stationary) return;
        entry.Stationary = false;
        if (ReferenceEquals(_stationaryLives?[entry.GlobalId], entry))
        { _stationaryLives![entry.GlobalId] = null; _stationaryCount--; }
    }
    private void WriteStationaryFlag(Entry entry, bool enabled)
    {
        if (!enabled) { ForgetStationary(entry); return; }
        if (!entry.Stationary)
        {
            entry.Stationary = true;
            (_stationaryLives ??= new Entry?[ushort.MaxValue + 1])[entry.GlobalId] = entry;
            _stationaryCount++;
        }
    }
    private void ClearStationary()
    { _stationaryLives = null; _stationaryCount = 0; }

    internal CommandResult Stationary(CommandContext context)
    {
        if (!CanExecute || !context.IsHost) return RefuseStationary(ResultCode.AuthorityOrPhase);
        if (context.Boolean(EnemyStationaryContract.Enabled) is not { } enabled)
            return RefuseStationary(ResultCode.StationaryEnabledRequired);
        var request = new StationaryRequest(this, enabled, ActionEffectHandle(context));
        return context.EachTarget(ref request, StationaryStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained);
    }

    private readonly record struct StationaryRequest(EnemyModule Module, bool Enabled, RuntimeHandle? Handle);

    private static readonly TargetStep<StationaryRequest> StationaryStep = static (ref StationaryRequest request, EntityReference target, int _, RowWriter rows) =>
    {
        var outcome = request.Module.SetStationary(target, request.Enabled, request.Handle);
        rows.Boolean(Blocks.ActionEnemyStationary.RowEnabled, request.Enabled).End(outcome);
        bool confirmed = outcome.Commit == Commit.Confirmed;
        request.Module.Log.Reason(confirmed ? RuntimeLogLevel.Info : RuntimeLogLevel.Error,
            confirmed ? EnemyLogCodes.StationaryChanged : EnemyLogCodes.StationaryRefused,
            confirmed ? request.Enabled ? "enabled" : "disabled" : outcome.Code, target.Id, EnemyStationaryContract.Binding);
    };
    private CommandResult RefuseStationary(string code)
    {
        Log.Reason(RuntimeLogLevel.Error, EnemyLogCodes.StationaryRefused, code);
        return CommandResult.Rejected(code);
    }
    private BehaviorOutcome SetStationary(EntityReference target, bool enabled, RuntimeHandle? handle = null)
    {
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        if (!entry.Enemy.Alive) return BehaviorOutcome.Already(ResultCode.StationaryEnemyDead);
        var ai = entry.Enemy.AI;
        var behaviour = ai?.m_behaviour;
        var locomotion = ai?.m_locomotion;
        if (behaviour == null || locomotion == null) return BehaviorOutcome.Refused(ResultCode.StationaryStateUnavailable);
        var result = InstallActionState(new ActionState
        {
            Entry = entry,
            Kind = ActionStateKind.Stationary,
            Ai = ai,
            Behaviour = behaviour,
            Locomotion = locomotion
        }, new(Type: enabled ? 1 : 0), handle);
        return result.Commit == Commit.Confirmed ? new(Status.Succeeded, Commit.Confirmed, ResultCode.StationaryChanged) : result;
    }
    /// <summary>A walk's detection value outside the noise wave, which the game answers for a close sneak too.</summary>
    internal const float WalkHeard = 0.95f;

    /// <summary>Whether any loaded profile changes what a sleeping enemy hears.</summary>
    internal bool WakeHearingAuthored { get; private set; }

    /// <summary>Whether any loaded profile keeps a sleeping enemy asleep when hit.</summary>
    internal bool WakeDamageAuthored { get; private set; }

    /// <summary>The lives whose rule changes hearing, by the native pointer of their detection component, which is the
    /// only identity the noise window's own instance carries without a further read.</summary>
    private readonly Dictionary<IntPtr, EnemyWakeLife> _wakeByDetection = new();

    /// <summary>Starts one life's wake rules at the spawn hook. The entry the spawn hook just filed is the life.</summary>
    private void StartWake(EnemyAgent enemy, uint typeId, string? source, EnemyWakeRule rule)
    {
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry) || entry.EnemyPointer != enemy.Pointer) return;
        var life = new EnemyWakeLife(rule, typeId, source);
        entry.Wake = life;
        if (!rule.ChangesHearing || enemy.AI?.m_detection is not { } detection || detection.Pointer == IntPtr.Zero) return;
        entry.WakeDetection = detection.Pointer;
        _wakeByDetection[detection.Pointer] = life;
    }

    /// <summary>Clears a key a life left behind without its despawn being seen, before a new life whose detection
    /// component has the same native pointer is profiled: the new life hears by its own rule or by the game's.</summary>
    private void DropStaleWake(EnemyAgent enemy)
    {
        if (!WakeHearingAuthored || _wakeByDetection.Count == 0 || enemy.AI?.m_detection is not { } detection) return;
        _wakeByDetection.Remove(detection.Pointer);
    }

    /// <summary>Drops a life's key when the life ends or is replaced. A key another life has since taken is left alone.</summary>
    private void ForgetWake(Entry entry)
    {
        ForgetStationary(entry);
        ForgetWakeLock(entry);
        if (entry.WakeDetection != IntPtr.Zero && _wakeByDetection.TryGetValue(entry.WakeDetection, out var life)
            && ReferenceEquals(life, entry.Wake))
            _wakeByDetection.Remove(entry.WakeDetection);
        entry.Wake = null;
        entry.WakeDetection = IntPtr.Zero;
    }

    /// <summary>
    /// The noise window's answer for one target, after the life's rule. <paramref name="output"/> is the game's own
    /// answer; the return is the one the game keeps. A target that neither sneaks nor shoots, a life with no hearing
    /// rule and a client all keep the game's answer.
    /// </summary>
    internal float HeardNoise(EnemyDetection detection, AgentTarget? target, float output)
    {
        if (WakeLockActive && IsWakeLockedAsleep(detection.m_ai?.m_enemyAgent)) return 0f;
        if (!WakeHearingAuthored || target == null) return output;
        var noise = target.noiseType;
        bool sneak = noise == Agent.NoiseType.Sneak;
        if (!sneak && noise is not (Agent.NoiseType.Shoot or Agent.NoiseType.ShootSilenced)) return output;
        if (!Session.IsHost || !_wakeByDetection.TryGetValue(detection.Pointer, out var life)) return output;
        var rule = life.Rule;
        float authored;
        if (sneak)
        {
            if (rule.SneakDistance is not float distance) return output;
            authored = !target.m_isOccluded && target.m_distance < distance ? MathF.Max(output, WalkHeard) : 0f;
        }
        else
        {
            if (rule.GunfireWakes != false) return output;
            authored = 0f;
        }
        if (authored != output && !(sneak ? life.SneakReported : life.GunfireReported))
        {
            if (sneak) life.SneakReported = true; else life.GunfireReported = true;
            if (TypeRecordDue(RuntimeLogLevel.Info, EnemyLogCodes.WakeRuleApplied, life.TypeId, sneak ? "sneak" : "gunfire"))
                WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.WakeRuleApplied, life.TypeId, life.Source, null, null, new[]
                {
                    RuntimeLogValue.Of("rule", sneak ? "sneak" : "gunfire"), RuntimeLogValue.Of("noise", noise.ToString()),
                    RuntimeLogValue.Of("distance", (double)target.m_distance), RuntimeLogValue.Of("vanilla", (double)output),
                    RuntimeLogValue.Of("authored", (double)authored)
                });
        }
        return authored;
    }

    /// <summary>Whether detection may automatically wake this life. This is a wake decision, never permission to
    /// skip SetDetectedAgent: that receiver also registers the attacker and increments attack buildup.</summary>
    internal bool DamageDetects(EnemyAI ai, AgentTargetDetectionType detectionType)
    {
        if (WakeLockActive && IsWakeLockedAsleep(ai.m_enemyAgent)) return false;
        if (!WakeDamageAuthored || detectionType != AgentTargetDetectionType.DamageDetection || !Session.IsHost) return true;
        var enemy = ai.m_enemyAgent;
        return KeptAsleep(enemy) == null;
    }

    internal readonly record struct DamageDetectionScope(EnemyModule? Owner, EntityReference Reference,
        IntPtr AI, IntPtr Agent, IntPtr ExistingTarget);

    internal DamageDetectionScope BeginDamageDetection(EnemyAI ai, Agent? agent, AgentTargetDetectionType type)
    {
        // 0x155DC0F: the same-attacker branch does not write autoDetect at all. Never clear another source's flag.
        if (type != AgentTargetDetectionType.DamageDetection || agent == null || !Session.IsHost
            || ai.m_lastAgentDetected?.Pointer == agent.Pointer || DamageDetects(ai, type)) return default;
        var enemy = ai.m_enemyAgent;
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry) || Resolve(entry.Reference) != entry
            || entry.EnemyPointer != enemy.Pointer || enemy.AI?.Pointer != ai.Pointer || !enemy.Alive
            || ai.m_behaviourData?.Targets is not { } targets) return default;
        // GetTarget is mutating (0x1566FC0 creates/registers). Read the existing list without creating anything.
        IntPtr existing = IntPtr.Zero;
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            // GetTarget keys by GlobalID, including a cached target whose destroyed actor is rebound this call.
            if (target == null || target.m_globalID != agent.GlobalID) continue;
            if (target.m_autoDetect) return default;
            existing = target.Pointer;
            break;
        }
        return new(this, entry.Reference, ai.Pointer, agent.Pointer, existing);
    }

    internal void EndDamageDetection(EnemyAI ai, DamageDetectionScope scope)
    {
        if (!ReferenceEquals(scope.Owner, this) || !Session.IsHost || Resolve(scope.Reference) is not { } entry
            || !entry.Enemy.Alive || entry.EnemyPointer != ai.m_enemyAgent?.Pointer || entry.Enemy.AI?.Pointer != ai.Pointer
            || !((entry.WakeLocked || entry.Wake is { Rule.KeepsAsleepOnDamage: true })
                && ai.m_behaviour is { } current && IsHibernation(current.m_currentStateName))
            || ai.Pointer != scope.AI || ai.m_lastAgentDetected?.Pointer != scope.Agent
            || ai.m_lastAgentTargetDetected is not { } target || target.m_agent?.Pointer != scope.Agent
            || scope.ExistingTarget != IntPtr.Zero && target.Pointer != scope.ExistingTarget
            || target.m_detectionType != AgentTargetDetectionType.DamageDetection || !target.m_autoDetect
            || ai.m_behaviourData?.Targets is not { } targets) return;
        // Only change the new bit from this exact native registration; keep caches, type, propagation and buildup.
        for (int i = 0; i < targets.Count; i++)
            if (targets[i]?.Pointer == target.Pointer)
            {
                target.m_autoDetect = false;
                if (entry.Wake is { Rule.KeepsAsleepOnDamage: true } life)
                    WakeKept(life, entry.Enemy, "damage-detection", null);
                return;
            }
    }

    /// <summary>Whether the game's accepted `CanHitreact` answer is kept: false for a non-lethal reaction of a sleeping life
    /// whose rule keeps it asleep when hit, on the host. A lethal reaction (`ToDeath` and above) always goes through.</summary>
    internal bool HitreactAsked(EnemyAgent? enemy, ES_HitreactType hitreact)
    {
        if (hitreact < ES_HitreactType.ToDeath && WakeLockActive && IsWakeLockedAsleep(enemy)) return false;
        if (!WakeDamageAuthored || hitreact >= ES_HitreactType.ToDeath || !Session.IsHost) return true;
        if (KeptAsleep(enemy) is not { } life) return true;
        WakeKept(life, enemy!, "damage-hitreact", hitreact);
        return false;
    }

    /// <summary>The life's rule when it keeps this sleeping life asleep on a hit; null for any other life or state.
    /// Asleep is the behaviour machine's own `Hibernating` state, which it leaves only once the wake-up has run.</summary>
    private EnemyWakeLife? KeptAsleep(EnemyAgent? enemy)
    {
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry) || entry.EnemyPointer != enemy.Pointer
            || entry.Wake is not { Rule.KeepsAsleepOnDamage: true } life) return null;
        return enemy.AI?.m_behaviour is { } behaviour && IsHibernation(behaviour.m_currentStateName) ? life : null;
    }

    private void WakeKept(EnemyWakeLife life, EnemyAgent enemy, string rule, ES_HitreactType? hitreact)
    {
        bool first = hitreact == null ? !life.DetectionReported : !life.HitreactReported;
        if (first)
        {
            if (hitreact == null) life.DetectionReported = true; else life.HitreactReported = true;
            if (TypeRecordDue(RuntimeLogLevel.Info, EnemyLogCodes.WakeRuleApplied, life.TypeId, rule))
                WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.WakeRuleApplied, life.TypeId, life.Source, null, null,
                    hitreact is { } kind
                        ? new[] { RuntimeLogValue.Of("rule", rule), RuntimeLogValue.Of("hitreact", kind.ToString()) }
                        : new[] { RuntimeLogValue.Of("rule", rule) });
        }
        if (!Log.Logs(RuntimeLogLevel.Trace)) return;
        Log.Write(RuntimeLogLevel.Trace, EnemyLogCodes.WakeKept, new RuntimeLogFacts
        {
            Entity = EntityKinds.Enemy + ":" + enemy.GlobalID.ToString(CultureInfo.InvariantCulture),
            Values = hitreact is { } reaction
                ? new[] { RuntimeLogValue.Of("enemyType", (long)life.TypeId), EnemyLabel(life.TypeId), RuntimeLogValue.Of("rule", rule), RuntimeLogValue.Of("hitreact", reaction.ToString()) }
                : new[] { RuntimeLogValue.Of("enemyType", (long)life.TypeId), EnemyLabel(life.TypeId), RuntimeLogValue.Of("rule", rule) }
        });
    }
}
