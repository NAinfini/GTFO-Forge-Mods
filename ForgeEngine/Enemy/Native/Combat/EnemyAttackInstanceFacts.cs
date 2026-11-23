using System;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The two facts a received damage transaction settles, read on the one window that already spans it.
///
/// `forge.trigger.combat.killed` (`next / target / source? / damage_kind`) is published only when a committed
/// damage call took the enemy from alive to not alive: the catalog says "this damage killed the target", and
/// the enemy's own death flow is a different row (`forge.trigger.enemy.death_started`), which enters death
/// without necessarily settling as a kill. The window is `ProcessReceivedDamage`, the only funnel every hit
/// passes through, so a kill reached from an explosion, a melee swing or a Forge submission is the same
/// reading.
///
/// `forge.trigger.combat.staggered` (`next / target / source?`) is the one fact of the two the receiver's own
/// arguments alone cannot settle: the hit names the reaction it is asking for, and the enemy's hitreact state
/// machine is asked afterwards what reaction it actually stands on. A hit the receiver refused, a hit that asked
/// for no reaction or for a kill, and a hit the receiver applied while its own immunity or retrigger rule left no
/// such reaction behind are all not staggers — the same standard the `stagger` action's submissions are held to.
///
/// Nothing here writes the world: every fact is a reading of a commit the game already made.</summary>
internal sealed partial class EnemyModule
{

    /// <summary>An observation of one native damage window. It is opened before the receiver runs and closed
    /// after it returns, so every reading in it is the transaction's own.</summary>
    internal sealed class DamageInstanceObservation
    {
        private readonly EnemyModule _owner;
        private bool _consumed;
        internal EntityReference Target { get; }
        internal IntPtr ReceiverPointer { get; }
        internal IntPtr EnemyPointer { get; }
        /// <summary>Whether the enemy was alive when the call was entered; a fact is published only for a
        /// transition out of life, so death already in progress is not reported a second time.</summary>
        internal bool AliveBefore { get; }
        /// <summary>The `damage_kind` member the receive entry around the call named, or `NoDamageKind`.</summary>
        internal int Kind { get; }
        /// <summary>The receiver's `limbID` argument, `-1` when the hit named no part.</summary>
        internal int Limb { get; }
        /// <summary>The receiver's `position` argument when it was finite.</summary>
        internal double[]? Position { get; }
        internal EntityReference? Source { get; }
        internal EntityReference? Equipment { get; }
        internal DamageInstanceObservation(EnemyModule owner, EntityReference target, IntPtr receiver,
            IntPtr enemy, bool aliveBefore, int kind, int limb, double[]? position,
            EntityReference? source, EntityReference? equipment)
        {
            _owner = owner; Target = target; ReceiverPointer = receiver; EnemyPointer = enemy; AliveBefore = aliveBefore;
            Kind = kind; Limb = limb; Position = position; Source = source; Equipment = equipment;
        }
        internal bool TryConsume(EnemyModule owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true; return true;
        }
    }

    /// <summary>Opens the window. A hit no plan subscribes to is never observed at all, so the cost with no
    /// consumer is the subscription lookup and nothing else. The authority gate is the one every other
    /// host-owned fact in this module passes (<see cref="CanExecute"/>): both windows share one native pair, so a
    /// peer that may not publish the health facts may not publish these either.</summary>
    internal DamageInstanceObservation? BeforeDamageInstance(Dam_EnemyDamageBase damage, Agents.Agent? damageSource, int limbId = -1,
        Vector3? position = null)
    {
        CheckThread();
        if (!CanExecute || !CanPublishDamageInstance || damage == null || damage.Owner == null) return null;
        if (!_entities.TryGetValue(damage.Owner.GlobalID, out var entry)
            || !IsLive(entry) || entry.EnemyPointer != damage.Owner.Pointer
            || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer
            || !float.IsFinite(damage.Health)) return null;
        var source = ResolveAgent(damageSource);
        // Capture while the exact weapon call is open. The receive packets alone cannot name equipment;
        // an explicit Forge damage request is a separate settlement, even if an attack's event caused it.
        var kind = EffectiveDamageKind(damage);
        var equipment = _requestedDamageReceiver != damage.Pointer && (kind is DamageKindDirect or DamageKindMelee)
            ? CombatShotAttribution.EquipmentOf(source) : null;
        return new DamageInstanceObservation(this, entry.Reference, damage.Pointer, entry.EnemyPointer,
            entry.Enemy.Alive, kind, limbId, position is { } point ? VectorPort(point) : null, source, equipment);
    }

    /// <summary>Closes the window and publishes what the commit settled. A receiver that did not move health
    /// publishes nothing: a rejected hit and a hit the receiver's own rules reduce to nothing are
    /// indistinguishable from here, so neither may be claimed.</summary>
    internal void AfterDamageInstance(Dam_EnemyDamageBase damage, DamageInstanceObservation? before)
    {
        CheckThread();
        // Consume on the first delivery, including a delivery that took nothing off: there is no later window.
        if (before == null || !before.TryConsume(this) || !CanExecute || !CanPublishDamageInstance) return;
        if (damage == null || damage.Pointer != before.ReceiverPointer || !float.IsFinite(damage.Health)) return;
        var entry = Resolve(before.Target);
        if (entry == null || entry.EnemyPointer != before.EnemyPointer || damage.Owner == null
            || damage.Owner.Pointer != before.EnemyPointer || entry.Enemy.Damage == null
            || entry.Enemy.Damage.Pointer != damage.Pointer) return;
        double healthAfter = Math.Max(0, damage.Health);
        bool alive = entry.Enemy.Alive;
        // The kill is the transition this call caused. Health at or below zero alone is not a kill — a
        // receiver can leave a corpse on its feet — and `Alive` alone is not this transaction's doing unless
        // the call is the one that took the last of it.
        if (before.AliveBefore && !alive && healthAfter <= 0) PublishKilled(entry, before);
    }

    /// <summary>Publishes the staggered row for a hit the closing half of the window reports. Three readings have
    /// to agree: the receiver's own answer says the damage landed, the hit asked for one of the stagger-class
    /// reactions, and the enemy's hitreact state machine still stands on exactly that reaction. Only the last one
    /// separates a stagger from a hit the receiver applied while its own immunity or retrigger rule swallowed the
    /// reaction — the applied answer alone cannot, and a reaction still current from an earlier hit can leave the
    /// same reading behind, which is the one claim this fact cannot rule out.</summary>
    internal void AfterDamageStaggered(Dam_EnemyDamageBase damage, ES_HitreactType hitreact, bool applied)
    {
        CheckThread();
        if (!applied || !IsStaggerReaction(hitreact)) return;
        if (!_staggeredGate.HasSubscribers || !CanObserveFacts) return;
        if (damage == null || damage.Owner == null) return;
        if (!_entities.TryGetValue(damage.Owner.GlobalID, out var entry) || !IsLive(entry)
            || entry.EnemyPointer != damage.Owner.Pointer || entry.Enemy.Damage == null
            || entry.Enemy.Damage.Pointer != damage.Pointer) return;
        var locomotion = entry.Enemy.Locomotion;
        if (locomotion == null || locomotion.Pointer == IntPtr.Zero) return;
        ES_HitreactType current;
        try
        {
            var hitreactState = locomotion.Hitreact;
            if (hitreactState == null || hitreactState.Pointer == IntPtr.Zero) return;
            current = hitreactState.CurrentReactionType;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return; }
        if (current != hitreact) return;
        // `hit_type` is the `enemy_hitreact` member (micro, light, heavy): the stagger-class reactions are the three
        // consecutive `ES_HitreactType` values from `Micro`, and `IsStaggerReaction` already refused every other one.
        var source = ReadInflictor(entry);
        var equipment = CombatShotAttribution.EquipmentOf(source);
        // The inflictor the receiver registered inside this same call, for the kinds a registered entity resolver
        // claims. An explosion packet carries no agent at all and a Forge submission submits none, so the optional
        // port is absent there rather than filled with a guessed reference.
        if (!IsLive(entry)) return;
        var fact = _staggeredGate.Begin(_registration);
        fact.Values.Entity(Blocks.TriggerCombatStaggered.OutTarget, entry.Reference);
        fact.Values.Enum(Blocks.TriggerCombatStaggered.OutHitType, (int)hitreact - (int)ES_HitreactType.Micro);
        if (source != null) fact.Values.Entity(Blocks.TriggerCombatStaggered.OutSource, source);
        if (equipment != null) fact.Values.Entity(Blocks.TriggerCombatStaggered.OutEquipment, equipment);
        RaiseDamageFact(fact, "gtfo.enemy.stagger");
    }

    /// <summary>A plan that subscribes to the window's own fact (the kill) is what makes a window worth opening. The
    /// stagger the closing half of the same call reports is another gate: opening an observation is what the
    /// prefix pays for, and the stagger needs none of it, so a plan that only wants staggers never pays for it.</summary>
    private bool CanPublishDamageInstance => _killedGate.HasSubscribers && CanObserveFacts;

    private void PublishKilled(Entry entry, DamageInstanceObservation before)
    {
        // The game registers the inflictor inside the same call, so the reading is this transaction's own. An
        // agent no registered kind claims — a Forge submission submits none at all — is an absent port rather
        // than a guessed reference.
        var source = ReadInflictor(entry);
        // The kind is the one the receive entry around the call named (`EnemyActionHooks`); a hit no receive
        // entry named — a Forge submission, a fire tick — carries none rather than a guessed `direct`. The part and
        // the point are the receiver's own `limbID` and `position` arguments.
        if (!IsLive(entry)) return;
        var fact = _killedGate.Begin(_registration);
        fact.Values.Entity(Blocks.TriggerCombatKilled.OutTarget, entry.Reference);
        if (source != null) fact.Values.Entity(Blocks.TriggerCombatKilled.OutSource, source);
        if (before.Kind >= 0) fact.Values.Enum(Blocks.TriggerCombatKilled.OutDamageKind, before.Kind);
        if (before.Limb >= 0) fact.Values.Integer(Blocks.TriggerCombatKilled.OutLimb, before.Limb);
        if (before.Position is { } at) fact.Values.Vector(Blocks.TriggerCombatKilled.OutPosition, at[0], at[1], at[2]);
        fact.Values.Entity(Blocks.TriggerCombatKilled.OutEquipment, source == before.Source ? before.Equipment : null);
        RaiseDamageFact(fact, "gtfo.enemy.kill");
    }

    /// <summary>One fact of the damage window, published under the scope of the half that read it. Whether any
    /// plan wants the fact is the caller's own gate, because the window's subscription tests differ: the two
    /// observations share one test taken before the call, and the stagger is a binding a plan can hold alone. The
    /// caller re-resolves the subject after the final native read and before beginning the fact, so a life
    /// that ended inside the same call publishes nothing.</summary>
    private void RaiseDamageFact(EventWriter fact, string scope)
    {
        // Runtime owns causal propagation and dispatch, and writes its own `event.rejected` for a refusal; a
        // rejection must never reopen this native observation.
        fact.Raise(scope + ":" + _kernel.WorldEpoch + ":" + checked(++_eventSequence),
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch);
    }

    /// <summary>The `direct` member of the declared `damage_kind` set (`direct, melee, explosion, dot, shrapnel,
    /// collision, fall, environment, reflection`): the kind the bullet receive entry names.</summary>
    internal const int DamageKindDirect = 0;
}
