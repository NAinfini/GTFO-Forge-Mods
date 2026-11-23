using System;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    internal const string DeathStartedBinding = ProviderId + ".binding.death_started";
    internal const string LimbBrokenBinding = ProviderId + ".binding.limb_broken";
    internal const int MaximumObservedLimbs = 256;

    internal sealed class LifecycleObservation
    {
        private readonly EnemyModule _owner;
        private bool _consumed;
        internal EntityReference Target { get; }
        internal IntPtr EnemyPointer { get; }
        internal IntPtr ReceiverPointer { get; }
        internal IntPtr LimbPointer { get; }
        internal int LimbId { get; }
        internal LifecycleObservation(EnemyModule owner, EntityReference target,
            IntPtr enemy, IntPtr receiver, IntPtr limb, int limbId)
        {
            _owner = owner; Target = target; EnemyPointer = enemy;
            ReceiverPointer = receiver; LimbPointer = limb; LimbId = limbId;
        }
        internal bool TryConsume(EnemyModule owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    private bool CanObserveFacts => _kernel.StartupState == RuntimeStartupState.Ready && CanExecute;
    private EnemyGate _deathStartedGate = null!, _limbBrokenGate = null!;

    internal LifecycleObservation? BeforeDeath(EnemyAgent enemy)
    {
        CheckThread();
        if (!CanObserveFacts || enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry)
            || Resolve(entry.Reference) != entry || enemy.Pointer != entry.EnemyPointer
            || entry.DeathObservation != null) return null;
        var observation = new LifecycleObservation(this, entry.Reference, entry.EnemyPointer,
            IntPtr.Zero, IntPtr.Zero, -1);
        // Claim once per life, even without consumers. Failed/unknown native completion is not replayed.
        entry.DeathObservation = observation;
        return observation;
    }

    internal void AfterDeath(EnemyAgent enemy, LifecycleObservation? before)
    {
        CheckThread();
        if (before == null || before.LimbId != -1 || !before.TryConsume(this) || !CanObserveFacts) return;
        var entry = Resolve(before.Target);
        if (entry == null || !ReferenceEquals(entry.DeathObservation, before)
            || enemy == null || enemy.Pointer != before.EnemyPointer
            || enemy.GlobalID != entry.Enemy.GlobalID || enemy.Alive) return;
        if (!CanObserveFacts || Resolve(before.Target) != entry || enemy.Pointer != before.EnemyPointer
            || enemy.Alive) return;
        // The life is over here, so the values that lived with it go with it: the kernel cannot tell a dead enemy
        // from a live one, which is why the provider that observed the death says so.
        ForgetStationary(entry);
        ForgetWakeLock(entry);
        ReleaseEnemyScope(before.Target);
        // OnDead carries no verifiable killer, so the row declares no source.
        string? eventId = null;
        if (!_deathStartedGate.HasSubscribers) eventId = SkipUnconsumedLifecycleFact(before.Target, keepTraceId: true);
        else if (TryBeginLifecycleFact(_deathStartedGate, before.Target, out var fact))
        {
            fact.Values.Entity(Blocks.TriggerEnemyDeathStarted.OutEnemy, before.Target);
            eventId = RaiseLifecycleFact(fact);
        }
        // One trace line per published death, keyed by the life and the event it published, so a death path can
        // be matched to exactly one fact. The damage kind is the receive entry the death happened inside, -1 on a
        // path no hit drove (InstantDead, a despawn, a scripted kill).
        if (eventId != null && Log.Logs(RuntimeLogLevel.Trace))
            Log.Write(RuntimeLogLevel.Trace, EnemyLogCodes.DeathStarted, new RuntimeLogFacts
            {
                Binding = DeathStartedBinding,
                EventId = eventId,
                Entity = before.Target.Id,
                Values = new[] { RuntimeLogValue.Of("life", before.Target.LifeEpoch),
                    RuntimeLogValue.Of("damageKind", (long)(enemy.Damage is { } receiver ? EffectiveDamageKind(receiver) : NoDamageKind)) }
            });
    }

    internal LifecycleObservation? BeforeLimbBreak(Dam_EnemyDamageLimb limb)
    {
        CheckThread();
        if (!CanObserveFacts || limb == null || limb.Pointer == IntPtr.Zero) return null;
        var damage = limb.m_base;
        if (damage == null || damage.Owner == null
            || !_entities.TryGetValue(damage.Owner.GlobalID, out var entry)
            || Resolve(entry.Reference) != entry) return null;
        int id = limb.m_limbID;
        if (id < 0 || id >= MaximumObservedLimbs || entry.LimbObservations.ContainsKey(id)) return null;
        var observation = new LifecycleObservation(this, entry.Reference, entry.EnemyPointer,
            damage.Pointer, limb.Pointer, id);
        if (!MatchesLimb(entry, limb, observation) || limb.IsDestroyed
            || !MatchesLimb(entry, limb, observation) || limb.IsDestroyed
            || !CanObserveFacts || Resolve(entry.Reference) != entry) return null;
        return entry.LimbObservations.TryAdd(id, observation) ? observation : null;
    }

    internal void AfterLimbBreak(Dam_EnemyDamageLimb limb, LifecycleObservation? before)
    {
        CheckThread();
        if (before == null || before.LimbId < 0 || !before.TryConsume(this) || !CanObserveFacts) return;
        var entry = Resolve(before.Target);
        if (entry == null || !entry.LimbObservations.TryGetValue(before.LimbId, out var captured)
            || !ReferenceEquals(captured, before) || !MatchesLimb(entry, limb, before)) return;
        bool destroyed = limb.IsDestroyed;
        if (!MatchesLimb(entry, limb, before) || limb.IsDestroyed != destroyed
            || !CanObserveFacts || Resolve(before.Target) != entry) return;
        if (!destroyed)
        {
            // A proven no-op may have a later genuine transition. This token remains consumed.
            entry.LimbObservations.Remove(before.LimbId); return;
        }
        if (!_limbBrokenGate.HasSubscribers) { SkipUnconsumedLifecycleFact(before.Target, keepTraceId: false); return; }
        // The committed limb transition carries only equipment attributed to the current damage call.
        var equipment = CombatShotAttribution.EquipmentOf(ReadInflictor(entry));
        if (!TryBeginLifecycleFact(_limbBrokenGate, before.Target, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerCombatLimbBroken.OutTarget, before.Target);
        fact.Values.Integer(Blocks.TriggerCombatLimbBroken.OutLimb, before.LimbId);
        if (equipment != null) fact.Values.Entity(Blocks.TriggerCombatLimbBroken.OutEquipment, equipment);
        RaiseLifecycleFact(fact);
    }

    private bool MatchesLimb(Entry entry, Dam_EnemyDamageLimb limb, LifecycleObservation observation)
    {
        if (limb == null || limb.Pointer != observation.LimbPointer || limb.m_limbID != observation.LimbId
            || entry.EnemyPointer != observation.EnemyPointer) return false;
        var damage = entry.Enemy.Damage;
        if (damage == null || damage.Pointer == IntPtr.Zero || damage.Pointer != observation.ReceiverPointer
            || !damage.IsSetup || damage.Owner == null || damage.Owner.Pointer != entry.EnemyPointer
            || damage.Owner.GlobalID != entry.Enemy.GlobalID
            || limb.m_base == null || limb.m_base.Pointer != observation.ReceiverPointer) return false;
        var limbs = damage.DamageLimbs;
        if (limbs == null || limbs.Length > MaximumObservedLimbs || observation.LimbId >= limbs.Length) return false;
        var indexed = limbs[observation.LimbId];
        return indexed != null && indexed.Pointer == observation.LimbPointer;
    }

    /// <summary>Drops one retired life's `enemy`-scoped variables. The scope kind is the kernel's own, not a
    /// second spelling, and only the reference a plan wrote under is released: a subject the store never saw
    /// costs one lookup and writes nothing. The kernel requires the subject's epoch to be the live world's, so a
    /// life whose world has already ended is skipped rather than refused — the world change cleared the whole
    /// scope already.</summary>
    private void ReleaseEnemyScope(EntityReference target)
    {
        if (target.WorldEpoch != _kernel.WorldEpoch) return;
        _kernel.ReleaseVariableScope(VariableScopeKinds.Enemy, target);
    }

    // Keep the original sequence watermark and death trace even while no plan consumes this fact. Identity,
    // observation claims and cleanup have already run; only the unused payload/event construction is skipped.
    private string? SkipUnconsumedLifecycleFact(EntityReference target, bool keepTraceId)
    {
        if (!CanObserveFacts || Resolve(target) == null) return null;
        long sequence = checked(++_eventSequence);
        return keepTraceId && Log.Logs(RuntimeLogLevel.Trace)
            ? "gtfo.enemy.lifecycle:" + _kernel.WorldEpoch + ":" + sequence : null;
    }

    /// <summary>Begins one lifecycle fact written by output position, or answers false when the life no longer
    /// resolves.</summary>
    private bool TryBeginLifecycleFact(EnemyGate gate, EntityReference target, out EventWriter fact)
    {
        fact = default;
        if (!CanObserveFacts || Resolve(target) == null) return false;
        fact = gate.Begin(_registration);
        return true;
    }

    /// <summary>Publishes one lifecycle fact and answers the event id it was published under. Runtime owns causal
    /// propagation and dispatch and writes its own `event.rejected` for a refusal; a rejection must never reopen
    /// this native observation.</summary>
    private string RaiseLifecycleFact(EventWriter fact)
    {
        var eventId = "gtfo.enemy.lifecycle:" + _kernel.WorldEpoch + ":" + checked(++_eventSequence);
        fact.Raise(eventId, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch);
        return eventId;
    }
}
