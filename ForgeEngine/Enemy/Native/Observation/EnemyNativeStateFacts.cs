using System;
using System.Collections.Generic;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

internal readonly record struct EnemyNativeStateObservation(EntityReference? Enemy, IntPtr Component, int Before);

internal sealed partial class EnemyModule
{
    private EnemyGate _combatEnteredGate = null!, _combatLeftGate = null!,
        _knockedDownGate = null!, _recoveringGate = null!, _phaseChangedGate = null!;

    private void CreateNativeStateGates()
    {
        _combatEnteredGate = Gate(EnemyNativeStateContract.Binding(Blocks.TriggerEnemyCombatEntered.Id));
        _combatLeftGate = Gate(EnemyNativeStateContract.Binding(Blocks.TriggerEnemyCombatLeft.Id));
        _knockedDownGate = Gate(EnemyNativeStateContract.Binding(Blocks.TriggerEnemyKnockedDown.Id));
        _recoveringGate = Gate(EnemyNativeStateContract.Binding(Blocks.TriggerEnemyKnockdownRecovering.Id));
        _phaseChangedGate = Gate(EnemyNativeStateContract.Binding(Blocks.TriggerEnemyPhaseChanged.Id));
    }
    internal bool CombatStateObserved => _combatEnteredGate.HasSubscribers || _combatLeftGate.HasSubscribers;
    internal bool KnockdownStateObserved => _knockedDownGate.HasSubscribers || _recoveringGate.HasSubscribers;
    internal bool PhaseObserved => _phaseChangedGate.HasSubscribers;
    private bool _phaseTracking;
    private readonly List<Entry> _nativePhaseEntries = new();
    private const string SquidBossBasePrefix = "Assets/AssetPrefabs/Characters/Enemies/Bases/SquidBossBase";

    // The native combat family, including its movement/attack substates and the boss combat cycle.
    // Neither target validity nor the group's state participates in this classification.
    internal static bool? NativeInCombat(int state) => state switch
    {
        >= 5 and <= 11 or >= 14 and <= 18 or >= 20 and <= 28 => true,
        >= 0 and <= 4 or 12 or 13 or 19 => false,
        _ => null
    };

    internal EnemyNativeStateObservation BeforeNativeState(EnemyAgent? enemy, IntPtr component, int state)
    {
        CheckThread();
        return enemy != null && CanObserveFacts && EntryForState(enemy) is { } entry
            ? new(entry.Reference, component, state) : default;
    }

    internal void ObserveCombatChange(EnemyBehaviour behaviour, EnemyNativeStateObservation observation)
    {
        CheckThread();
        if (!CombatStateObserved || !CanObserveFacts || behaviour?.m_ai?.m_enemyAgent is not { } enemy) return;
        var entry = EntryForState(enemy);
        if (entry == null || entry.Reference != observation.Enemy || observation.Component != behaviour.Pointer
            || enemy.AI?.m_behaviour?.Pointer != behaviour.Pointer) return;
        var previous = NativeInCombat(observation.Before);
        var current = NativeInCombat((int)behaviour.m_currentStateName);
        if (previous is not { } was || current is not { } now || was == now) return;
        var gate = now ? _combatEnteredGate : _combatLeftGate;
        if (!CanPublishBehaviorFact(gate, entry.Reference)) return;
        var fact = gate.Begin(_registration);
        fact.Values.Entity(now ? Blocks.TriggerEnemyCombatEntered.OutEnemy : Blocks.TriggerEnemyCombatLeft.OutEnemy, entry.Reference);
        RaiseBehavior(fact);
    }

    internal void ObserveKnockdownChange(EnemyLocomotion locomotion, EnemyNativeStateObservation observation)
    {
        CheckThread();
        if (!KnockdownStateObserved || !CanObserveFacts || locomotion?.m_agent is not { } enemy) return;
        var entry = EntryForState(enemy);
        if (entry == null || entry.Reference != observation.Enemy || observation.Component != locomotion.Pointer
            || enemy.Locomotion?.Pointer != locomotion.Pointer) return;
        var current = locomotion.CurrentStateEnum;
        if ((int)current == observation.Before) return;
        var gate = current == ES_StateEnum.Knockdown ? _knockedDownGate
            : current == ES_StateEnum.KnockdownRecover ? _recoveringGate : null;
        if (gate == null || !CanPublishBehaviorFact(gate, entry.Reference)) return;
        var fact = gate.Begin(_registration);
        fact.Values.Entity(current == ES_StateEnum.Knockdown
            ? Blocks.TriggerEnemyKnockedDown.OutEnemy : Blocks.TriggerEnemyKnockdownRecovering.OutEnemy, entry.Reference);
        RaiseBehavior(fact);
    }

    /// <summary>The per-tick native phase sweep, called only from the session's frame driver: a phase change
    /// publishes a fact, which the kernel refuses inside a lifecycle notification.</summary>
    internal void ObserveNativePhasesInFrame()
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.EnemyPhasePoll);
        CheckThread();
        if (_disposed) return;
        ObserveNativePhases();
    }

    private void ObserveNativePhases()
    {
        if (!PhaseObserved || !CanObserveFacts)
        {
            if (_phaseTracking) foreach (var entry in _nativePhaseEntries)
            { entry.ObservedNativePhase = null; entry.PhaseBehaviour = IntPtr.Zero; }
            _phaseTracking = false;
            return;
        }
        _phaseTracking = true;
        foreach (var entry in _nativePhaseEntries) ObserveNativePhase(entry, true);
    }

    private void TrackNativePhase(Entry entry)
    {
        if (!entry.NativePhaseCandidate) return;
        _nativePhaseEntries.Add(entry);
        if (PhaseObserved) { _phaseTracking = true; ObserveNativePhase(entry, false); }
    }

    private void ForgetNativePhase(Entry entry)
    {
        entry.ObservedNativePhase = null;
        entry.PhaseBehaviour = IntPtr.Zero;
        if (!entry.NativePhaseCandidate) return;
        for (int index = _nativePhaseEntries.Count - 1; index >= 0; index--)
            if (ReferenceEquals(_nativePhaseEntries[index], entry))
            { _nativePhaseEntries.RemoveAt(index); break; }
        if (_nativePhaseEntries.Count == 0) _phaseTracking = false;
    }

    private void ClearNativePhases()
    {
        _nativePhaseEntries.Clear();
        _phaseTracking = false;
    }

    /// <summary>The base prefab list is frozen type data selected before the native life is built. Squid boss
    /// variants all add a SquidBossBase prefab; ordinary types do not. A missing, empty or unstable type reading
    /// cannot safely exclude a life, so it remains in the small observation set and the live read still decides.</summary>
    private static bool NativePhaseType(EnemyAgent enemy)
    {
        try
        {
            var type = enemy.EnemyData;
            var prefabs = type?.BasePrefabs;
            if (type == null || type.Pointer == IntPtr.Zero || prefabs == null || prefabs.Count == 0) return true;
            var pointer = type.Pointer;
            var typeId = type.persistentID;
            bool phase = false;
            foreach (var path in prefabs)
                if (path != null && path.StartsWith(SquidBossBasePrefix, StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                { phase = true; break; }
            var confirmed = enemy.EnemyData;
            return confirmed != null && confirmed.Pointer == pointer && confirmed.persistentID == typeId ? phase : true;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return true; }
    }

    // Native SetIncomingExtraStateData writes the phase field directly. Read the existing native state on the
    // kernel's own tick rather than intercepting only the property setter or introducing another scheduler.
    private void ObserveNativePhase(Entry entry, bool publish)
    {
        var enemy = entry.Enemy;
        var ai = enemy.AI;
        var behaviour = ai?.m_behaviour;
        int after = 0; SquidBossBehaviour boss = null!;
        bool stable = IsLive(entry) && ai?.m_enemyAgent?.Pointer == entry.EnemyPointer
            && EnemyPhaseReader.TryRead(behaviour, out after, out boss) && boss.m_ai?.Pointer == ai.Pointer;
        // PERF(E2): a phase that equals the watermark publishes nothing, so the confirming second reading (the guard
        // of a changed phase) is only paid by a frame that moved it.
        if (stable && !(entry.PhaseBehaviour == boss.Pointer && entry.ObservedNativePhase == after))
            stable = EnemyPhaseReader.TryRead(behaviour, out int check, out var same) && after == check && boss.Pointer == same.Pointer;
        if (!stable || enemy.AI?.m_behaviour?.Pointer != boss.Pointer)
        { entry.ObservedNativePhase = null; entry.PhaseBehaviour = IntPtr.Zero; return; }
        int? before = entry.PhaseBehaviour == boss.Pointer ? entry.ObservedNativePhase : null;
        entry.ObservedNativePhase = after;
        entry.PhaseBehaviour = boss.Pointer;
        if (!publish || before == null || before == after) return;
        if (!CanPublishBehaviorFact(_phaseChangedGate, entry.Reference)) return;
        var fact = _phaseChangedGate.Begin(_registration);
        fact.Values.Entity(Blocks.TriggerEnemyPhaseChanged.OutEnemy, entry.Reference);
        fact.Values.Integer(Blocks.TriggerEnemyPhaseChanged.OutPreviousPhase, before.Value);
        fact.Values.Integer(Blocks.TriggerEnemyPhaseChanged.OutPhase, after);
        RaiseBehavior(fact);
    }

    private Entry? EntryForState(EnemyAgent enemy)
        => _entities.TryGetValue(enemy.GlobalID, out var entry) && entry.EnemyPointer == enemy.Pointer
            && IsLive(entry) ? entry : null;
}
