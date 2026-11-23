using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>Host authority gate for every AI behaviour fact. The sampling is driven by native entries, so nothing here
/// writes the world: it only reads one registered enemy and publishes the transitions it observed.</summary>
internal sealed partial class EnemyModule
{
    internal const string AwakenedBinding = ProviderId + ".binding.awakened";
    internal const string TargetAcquiredBinding = ProviderId + ".binding.target_acquired";
    internal const string TargetLostBinding = ProviderId + ".binding.target_lost";
    internal const string ScoutDetectionBinding = ProviderId + ".binding.scout_detection";
    internal const string ScoutScreamBinding = ProviderId + ".binding.scout_scream";

    /// <summary>PERF(P5): the kernel gate of every binding this file publishes, resolved once at attach. The behaviour
    /// sampling is skipped while none of them has a plan. The target pair is part of the set for the same reason as the
    /// rest: a fact a plan subscribed to is a reason to read the AI, and a fact omitted here would be published never
    /// instead of once.</summary>
    private EnemyGate _awakenedGate = null!, _targetAcquiredGate = null!, _targetLostGate = null!,
        _scoutDetectionGate = null!, _scoutScreamGate = null!;

    /// <summary>Resolved once, right after the registration exists.</summary>
    private void CreateBehaviorGates()
    {
        _awakenedGate = Gate(AwakenedBinding);
        _targetAcquiredGate = Gate(TargetAcquiredBinding);
        _targetLostGate = Gate(TargetLostBinding);
        _scoutDetectionGate = Gate(ScoutDetectionBinding);
        _scoutScreamGate = Gate(ScoutScreamBinding);
    }

    /// <summary>Whether a plan consumes the scout scream fact.</summary>
    internal bool ScoutScreamObserved => _scoutScreamGate.HasSubscribers;

    /// <summary>Whether a plan consumes one of the behaviour facts that the native entries sample.</summary>
    internal bool BehaviorObserved => _awakenedGate.HasSubscribers || _targetAcquiredGate.HasSubscribers
        || _targetLostGate.HasSubscribers || _scoutDetectionGate.HasSubscribers || _scoutScreamGate.HasSubscribers;

    // A sample costs one reading of one registered enemy; with no plan subscribed to any behaviour fact it must
    // not read the native AI at all. The gates are read first: `CanObserveFacts` reaches the host's Il2Cpp authority
    // reads through `CanExecute`, so a build with no behaviour plan pays neither.
    private bool CanPublishBehavior => BehaviorObserved && CanObserveFacts;

    /// <summary>Per-life behaviour watermarks. They live with the entry, so a despawn, a new life or a world
    /// change drops them and the next life starts from its own observed state.</summary>
    internal sealed class Behavior
    {
        internal int State;
        internal int ScoutPhase = -1;
        /// <summary>The life's last observed target lock: whether the AI held a valid target, and which entity it
        /// held. The reference is the one the kernel resolved for the sample, or null when the sample's target was
        /// not nameable; both halves are watermarks, so a lock that survives a frame publishes nothing.</summary>
        internal bool TargetValid;
        internal EntityReference? Target;
        /// <summary>The native pointer of the agent <see cref="Target"/> was resolved from. PERF(P5): a held lock on
        /// the same agent is not resolved again; zero means no agent, or one whose instance has no pointer to compare,
        /// which is resolved every time.</summary>
        internal IntPtr TargetAgent;

        /// <summary>The state an enemy is first seen in is a baseline, not a transition: a life that never
        /// changes state publishes nothing, and the first state change is a real one.</summary>
        internal Behavior(EnemyAgent enemy, ushort globalId)
        {
            if (!EnemyBehaviorFactsObserver.Read(enemy, globalId, out var sample)) return;
            State = sample.BehaviourState;
            ScoutPhase = sample.ScoutScreamPhase;
            TargetValid = sample.HasValidTarget;
        }
    }

    /// <summary>Lives that currently hold a state only a native timer or validity ends: a target lock (the AI stops
    /// holding it without any call the module can hook) and a tag (its native timer runs out on the game's side).
    /// Rising edges arrive from the native entries; the falling edges are looked for here, once per kernel tick, on
    /// these lives only, instead of on every awake enemy every frame.</summary>
    private readonly HashSet<Entry> _holders = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _holderScratch = new();

    private void SetHolder(Entry entry)
    {
        if (entry.Behavior.TargetValid || entry.Tagged) _holders.Add(entry);
        else _holders.Remove(entry);
    }

    /// <summary>A native entry that can change the AI's state or target lock ran for this enemy: reads its behaviour
    /// once and publishes what changed against the life's watermarks.</summary>
    internal void ObserveEnemyBehaviorEvent(EnemyAgent? enemy)
    {
        CheckThread();
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry)) return;
        ObserveEnemyBehavior(enemy, entry);
        SetHolder(entry);
    }

    /// <summary>A scout's scream began: the previous scream's last phase is not this one's baseline, so the phase
    /// watermark starts over before the first phase is read.</summary>
    internal void ScoutScreamBegan(EnemyAgent? enemy)
    {
        CheckThread();
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry) || entry.EnemyPointer != enemy.Pointer) return;
        entry.Behavior.ScoutPhase = -1;
        ObserveEnemyBehavior(enemy, entry);
    }

    /// <summary>The enemy's course node was set (EnemyAgent.set_CourseNode).</summary>
    internal void ObserveEnemyZoneEvent(EnemyAgent? enemy)
    {
        CheckThread();
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry)) return;
        ObserveEnemyZone(enemy, entry);
    }

    /// <summary>The once-per-tick look at the lives in <see cref="_holders"/>, run by the session's frame driver
    /// (it publishes facts, which a kernel lifecycle observer may not).</summary>
    internal void ObserveHoldersInFrame()
    {
        CheckThread();
        if (_holders.Count == 0 || !(BehaviorObserved || TagStateObserved)) return;
        _holderScratch.Clear();
        _holderScratch.AddRange(_holders);
        foreach (var entry in _holderScratch)
        {
            if (!IsLive(entry)) { _holders.Remove(entry); continue; }
            if (entry.Behavior.TargetValid && BehaviorObserved) ObserveEnemyBehavior(entry.Enemy, entry);
            if (entry.Tagged && TagStateObserved) ObserveTagState(entry.Enemy, entry);
            SetHolder(entry);
        }
        _holderScratch.Clear();
    }
    /// <summary>One reading of one registered enemy. The
    /// sample is a value and the life is matched by the GlobalID cached on its entry, so a reading allocates nothing of
    /// its own until a fact is published.</summary>
    private void ObserveEnemyBehavior(EnemyAgent enemy, Entry entry)
    {
        CheckThread();
        if (!CanPublishBehavior || entry.EnemyPointer != enemy.Pointer) return;
        var behavior = entry.Behavior;
        var entryPointer = entry.EnemyPointer; var reference = entry.Reference;
        // PERF(E1): the common reading holds every watermark. One reading says so, and then nothing could be published
        // and the confirming second reading (the guard of a publish) has nothing to protect.
        if (!EnemyBehaviorFactsObserver.ReadFirst(enemy, entry.GlobalId, out var first)) return;
        var sample = first.ToSample();
        if (HoldsWatermarks(behavior, in sample)) return;
        // A different life, a stale registration or a changed instance is not a fact.
        if (!EnemyBehaviorFactsObserver.Confirm(enemy, in first) || !IsLive(entry) || entry.EnemyPointer != entryPointer) return;
        ObserveStateChange(entry, behavior, in sample);
        ObserveTargetChange(reference, behavior, in sample);
        ObserveScoutScream(entry, behavior, in sample);
    }

    /// <summary>Whether a sample changes none of the life's watermarks, so that the three observers below would only
    /// return: the same state, the same scout phase, and either no lock held on either side or the same lock on the
    /// same native agent that is still current (the held-lock case of <see cref="ObserveTargetChange"/>).</summary>
    private bool HoldsWatermarks(Behavior behavior, in EnemyBehaviorFactsObserver.Sample sample)
    {
        if (sample.BehaviourState != behavior.State || sample.ScoutScreamPhase != behavior.ScoutPhase
            || sample.HasValidTarget != behavior.TargetValid) return false;
        if (!sample.HasValidTarget) return behavior.Target is null;
        var agentPointer = sample.Target?.m_agent?.Pointer ?? IntPtr.Zero;
        return agentPointer != IntPtr.Zero && agentPointer == behavior.TargetAgent
            && behavior.Target is { } held && _kernel.IsEntityCurrent(held);
    }

    /// <summary>The native readings `EnemyModule.ActionFacts` adds to three of these facts (contract §4.5 B3): the
    /// player who woke the enemy, how it detected them and whether the wake-up was propagated; how the AI detected the
    /// target it acquired; and the player a scout screams about. Declared here so this file stays the one publish
    /// path of these facts.</summary>
    partial void ReadWakeFacts(Entry entry, in EnemyBehaviorFactsObserver.Sample sample,
        ref EntityReference? waker, ref int? cause, ref bool? propagated);
    partial void ReadAcquireFacts(in EnemyBehaviorFactsObserver.Sample sample, ref int? how);
    partial void ReadScoutScreamFacts(Entry entry, ref EntityReference? target);

    /// <summary>Native frame entry (ES_ScoutDetection.OnTargetRegistered). The scout has already registered the
    /// target natively; this only reports it, with the position the native target carries.</summary>
    internal void ObserveScoutDetection(EnemyAgent? scout, AgentTarget? target)
    {
        CheckThread();
        if (!_scoutDetectionGate.HasSubscribers || !CanObserveFacts || scout == null || target == null) return;
        if (!_entities.TryGetValue(scout.GlobalID, out var entry) || entry.EnemyPointer != scout.Pointer) return;
        var position = target.m_position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return;
        // An unresolved target is an absent port, never a null reference standing in for a real one.
        var resolved = ResolveAgent(target.m_agent);
        if (!CanPublishBehaviorFact(_scoutDetectionGate, entry.Reference)) return;
        var fact = _scoutDetectionGate.Begin(_registration);
        fact.Values.Entity(Blocks.TriggerEnemyScoutDetection.OutEnemy, entry.Reference);
        if (resolved != null) fact.Values.Entity(Blocks.TriggerEnemyScoutDetection.OutTarget, resolved);
        fact.Values.Vector(Blocks.TriggerEnemyScoutDetection.OutPosition, position.x, position.y, position.z);
        RaiseBehavior(fact);
    }

    /// <summary>The one state edge this module still publishes: leaving a hibernating state is the wake-up. The
    /// per-life state watermark is what decides it, so a life that never leaves hibernation publishes nothing and
    /// a state change between two awake states is not a fact here.</summary>
    private void ObserveStateChange(Entry entry, Behavior behavior, in EnemyBehaviorFactsObserver.Sample sample)
    {
        if (sample.BehaviourState == behavior.State) return;
        if (EnemyEntityObserver.Hibernating(behavior.State) && !EnemyEntityObserver.Hibernating(sample.BehaviourState)
            && _awakenedGate.HasSubscribers)
        {
            // The behaviour machine names no cause; the waker and the propagated flag are native readings of the
            // same sample, absent when they do not read.
            EntityReference? waker = null; int? cause = null; bool? propagated = null;
            ReadWakeFacts(entry, in sample, ref waker, ref cause, ref propagated);
            if (CanPublishBehaviorFact(_awakenedGate, entry.Reference))
            {
                var fact = _awakenedGate.Begin(_registration);
                fact.Values.Entity(Blocks.TriggerEnemyAwakened.OutEnemy, entry.Reference);
                if (waker != null) fact.Values.Entity(Blocks.TriggerEnemyAwakened.OutWaker, waker);
                if (cause.HasValue) fact.Values.Enum(Blocks.TriggerEnemyAwakened.OutCause, cause.Value);
                if (propagated.HasValue) fact.Values.Boolean(Blocks.TriggerEnemyAwakened.OutPropagated, propagated.Value);
                RaiseBehavior(fact);
            }
        }
        behavior.State = sample.BehaviourState;
    }

    /// <summary>
    /// The target lock, published as two facts: an acquisition when the AI begins holding one, and a loss when it
    /// stops. The native validity flag is the fact — the game answers "is there a target" itself — while the
    /// entity is a port the kernel may fail to name, so a lock whose target belongs to no registered kind is
    /// still a lock and publishes without that port rather than being dropped. The watermark is the pair, so a
    /// held lock publishes once and only a real change publishes again: an unnameable lock that becomes a
    /// different unnameable lock is not a second acquisition of the same nothing. A lock that moves straight from
    /// one entity to another is that second acquisition and no loss in between — the facts are the two edges of
    /// the validity flag, and the port on the acquisition is what says which entity the new lock names.
    /// </summary>
    private void ObserveTargetChange(EntityReference reference, Behavior behavior, in EnemyBehaviorFactsObserver.Sample sample)
    {
        var agent = sample.HasValidTarget ? sample.Target?.m_agent : null;
        var agentPointer = agent?.Pointer ?? IntPtr.Zero;
        // PERF(P5): the same held lock on the same native agent is the common frame. While the reference it resolved
        // to is still current, the agent's one current life is that reference, so resolving again would answer the
        // same and nothing is published; a lock that never resolved, a new agent, or a life that ended (a player's
        // respawn under the same agent) is resolved through the kernel again.
        if (sample.HasValidTarget && behavior.TargetValid && agentPointer != IntPtr.Zero && agentPointer == behavior.TargetAgent
            && behavior.Target is { } held && _kernel.IsEntityCurrent(held))
            return;
        var target = sample.HasValidTarget ? ResolveAgent(agent) : null;
        behavior.TargetAgent = agentPointer;
        if (sample.HasValidTarget == behavior.TargetValid && target == behavior.Target) return;
        bool acquired = sample.HasValidTarget && !behavior.TargetValid;
        var previous = behavior.Target;
        behavior.TargetValid = sample.HasValidTarget;
        behavior.Target = target;
        // A changed target under a still-valid flag is a fresh acquisition of the new one; only the flag falling
        // is a loss, and the loss names the entity the lock held until now when the kernel could name it.
        if (acquired || sample.HasValidTarget)
        {
            if (!_targetAcquiredGate.HasSubscribers) return;
            int? how = null;
            ReadAcquireFacts(in sample, ref how);
            if (CanPublishBehaviorFact(_targetAcquiredGate, reference))
            {
                var fact = _targetAcquiredGate.Begin(_registration);
                fact.Values.Entity(Blocks.TriggerEnemyTargetAcquired.OutEnemy, reference);
                if (target != null) fact.Values.Entity(Blocks.TriggerEnemyTargetAcquired.OutTarget, target);
                if (how.HasValue) fact.Values.Enum(Blocks.TriggerEnemyTargetAcquired.OutHow, how.Value);
                RaiseBehavior(fact);
            }
        }
        else if (CanPublishBehaviorFact(_targetLostGate, reference))
        {
            var fact = _targetLostGate.Begin(_registration);
            fact.Values.Entity(Blocks.TriggerEnemyTargetLost.OutEnemy, reference);
            if (previous != null) fact.Values.Entity(Blocks.TriggerEnemyTargetLost.OutTarget, previous);
            RaiseBehavior(fact);
        }
    }

    private void ObserveScoutScream(Entry entry, Behavior behavior, in EnemyBehaviorFactsObserver.Sample sample)
    {
        var reference = entry.Reference;
        if (sample.ScoutScreamPhase < 0)
        {
            behavior.ScoutPhase = -1;
            return;
        }
        if (sample.ScoutScreamPhase == behavior.ScoutPhase) return;
        behavior.ScoutPhase = sample.ScoutScreamPhase;
        if (!_scoutScreamGate.HasSubscribers) return;
        EntityReference? target = null;
        ReadScoutScreamFacts(entry, ref target);
        if (CanPublishBehaviorFact(_scoutScreamGate, reference))
        {
            var fact = _scoutScreamGate.Begin(_registration);
            fact.Values.Entity(Blocks.TriggerEnemyScoutScream.OutEnemy, reference);
            fact.Values.Enum(Blocks.TriggerEnemyScoutScream.OutPhase, sample.ScoutScreamPhase);
            if (target != null) fact.Values.Entity(Blocks.TriggerEnemyScoutScream.OutTarget, target);
            RaiseBehavior(fact);
        }
    }

    private bool CanPublishBehaviorFact(EnemyGate gate, EntityReference subject)
        => gate.HasSubscribers && CanObserveFacts && Resolve(subject) != null;

    /// <summary>The one publish path for every behaviour fact, under the scope of the window that read it: the
    /// behaviour sampling publishes under the behaviour scope and the ability ledger under the ability scope, so a fact's
    /// own id says which window observed it. Both count from this module's one event sequence. A refusal is the
    /// kernel's to record (`event.rejected` under the binding) and never reopens this native observation.</summary>
    private void RaiseBehavior(EventWriter fact, string scope = "gtfo.enemy.behavior")
    {
        // Every caller checks CanPublishBehaviorFact after its last native read and before beginning the fact.
        // The writer cannot call back into native code or change the life it just validated.
        fact.Raise(scope + ":" + _kernel.WorldEpoch + ":" + checked(++_eventSequence),
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch);
    }

}
