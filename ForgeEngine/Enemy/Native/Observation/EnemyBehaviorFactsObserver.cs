using System;
using Agents;
using Enemies;

namespace ForgeEnemy.Native.Observation;

/// <summary>Build-specific, read-only AI behaviour sample of one registered enemy. It never requests a
/// transition, never writes the world and never infers allegiance; every value is read back from the native
/// behaviour and locomotion objects of the same life and from the AI's own target, and a sample is dropped rather
/// than returned when the instance identity moves while it is being read.</summary>
internal static class EnemyBehaviorFactsObserver
{
    /// <summary>One held reading. PERF(P5): a value, so a frame's sample costs no heap object; the pump reads one per
    /// registered enemy per frame while a behaviour plan is loaded.</summary>
    internal readonly struct Sample
    {
        internal Sample(int behaviourState, bool hasValidTarget, AgentTarget? target, int scoutScreamPhase)
        { BehaviourState = behaviourState; HasValidTarget = hasValidTarget; Target = target; ScoutScreamPhase = scoutScreamPhase; }
        internal int BehaviourState { get; }
        internal bool HasValidTarget { get; }
        internal AgentTarget? Target { get; }
        internal int ScoutScreamPhase { get; }
    }

    internal readonly struct Reading
    {
        internal Reading(IntPtr enemyPointer, ushort enemyId, IntPtr behaviourPointer, int behaviourState,
            bool hasValidTarget, AgentTarget? target, int scoutScreamPhase)
        {
            EnemyPointer = enemyPointer; EnemyId = enemyId; BehaviourPointer = behaviourPointer; BehaviourState = behaviourState;
            HasValidTarget = hasValidTarget; Target = target; ScoutScreamPhase = scoutScreamPhase;
        }
        internal IntPtr EnemyPointer { get; }
        internal ushort EnemyId { get; }
        internal IntPtr BehaviourPointer { get; }
        internal int BehaviourState { get; }
        internal bool HasValidTarget { get; }
        internal AgentTarget? Target { get; }
        internal int ScoutScreamPhase { get; }
        internal Sample ToSample() => new(BehaviourState, HasValidTarget, Target, ScoutScreamPhase);

        /// <summary>Field by field, the target by native object: each read hands back a new AgentTarget wrapper.</summary>
        internal bool Same(in Reading other)
            => EnemyPointer == other.EnemyPointer && EnemyId == other.EnemyId && BehaviourPointer == other.BehaviourPointer
               && BehaviourState == other.BehaviourState && HasValidTarget == other.HasValidTarget
               && ForgeCore.NativeObjects.Same(Target, other.Target) && ScoutScreamPhase == other.ScoutScreamPhase;
    }

    /// <summary>A reading that held across two reads, of the life whose cached <paramref name="globalId"/> the caller
    /// tracks. False when the instance moved while it was read or is another life's.</summary>
    internal static bool Read(EnemyAgent enemy, ushort globalId, out Sample sample)
    {
        sample = default;
        if (!ReadFirst(enemy, globalId, out var first) || !Confirm(enemy, in first)) return false;
        sample = first.ToSample();
        return true;
    }

    /// <summary>The first half of <see cref="Read"/>: one reading of the life whose cached id the caller tracks. A
    /// frame that finds nothing changed against its watermarks stops here; one that is about to publish confirms it
    /// with <see cref="Confirm"/> first.</summary>
    internal static bool ReadFirst(EnemyAgent enemy, ushort globalId, out Reading first)
        => ReadOnce(enemy, out first) && first.EnemyId == globalId;

    /// <summary>The second half of <see cref="Read"/>: the same reading again, which must be identical.</summary>
    internal static bool Confirm(EnemyAgent enemy, in Reading first)
        => ReadOnce(enemy, out var second) && first.Same(in second);

    private static bool ReadOnce(EnemyAgent enemy, out Reading reading)
    {
        reading = default;
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup || !enemy.Alive) return false;
        var pointer = enemy.Pointer; ushort id = enemy.GlobalID;
        var ai = enemy.AI;
        if (ai == null || ai.m_enemyAgent == null || ai.m_enemyAgent.Pointer != pointer) return false;
        var behaviour = ai.m_behaviour;
        var locomotion = ai.m_locomotion;
        if (behaviour == null || locomotion == null) return false;
        if (behaviour.m_ai == null || behaviour.m_ai.Pointer != ai.Pointer) return false;
        int state = (int)behaviour.m_currentStateName;
        // A native getter may re-enter; anything it can invalidate is re-read before the sample is returned.
        bool hasTarget = TryTarget(ai, out var target);
        int phase = ScoutScreamPhase(locomotion);
        if (enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup || !enemy.Alive) return false;
        reading = new(pointer, id, behaviour.Pointer, state, hasTarget, target, phase);
        return true;
    }

    private static bool TryTarget(EnemyAI ai, out AgentTarget? target)
    {
        target = null;
        bool valid;
        try { valid = ai.IsTargetValid; } catch (Exception caught) { EnemyLog.Contained(caught); return false; }
        if (!valid) return false;
        try { target = ai.Target; } catch (Exception caught) { EnemyLog.Contained(caught); target = null; }
        return true;
    }

    /// <summary>The scout scream phase index while the scout scream state is active, and -1 otherwise: the
    /// saved state object survives after its exit, so the state machine's own current state decides.</summary>
    private static int ScoutScreamPhase(EnemyLocomotion locomotion)
    {
        if ((int)locomotion.CurrentStateEnum != ScoutScreamState) return -1;
        var scream = locomotion.ScoutScream;
        if (scream == null) return -1;
        int phase = (int)scream.m_state;
        return phase is >= 0 and <= MaximumScoutScreamPhase ? phase : -1;
    }

    /// <summary>ES_StateEnum.ScoutScream; the wire value below is ES_ScoutScream.ScoutScreamState.</summary>
    private const int ScoutScreamState = 13;
    private const int MaximumScoutScreamPhase = 4;
}
