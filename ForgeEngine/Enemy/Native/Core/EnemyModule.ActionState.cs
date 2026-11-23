using System;
using System.Collections.Generic;
using AirNavigation;
using Enemies;
using ForgeEngine.Framework;
using UnityEngine;
using UnityEngine.AI;
using ForgeCore;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    internal enum ActionStateKind { Limb, Phase, Move, DamageRule, Stationary, Stagger, WakeLock }
    internal readonly record struct ActionValue(int Type = 0, float Weakspot = 0, float Armor = 0,
        float X = 0, float Y = 0, float Z = 0, float Speed = 0, float Tolerance = 0,
        IncomingDamagePolicy Policy = default, int BehaviourState = 0, int LocomotionState = 0);
    /// <summary>What one layer is removed by: the kernel's effect handle for the step that wrote it, or this
    /// provider's own loan number for a stagger immunity override that lives inside one command. A layer with no
    /// key never ends and replaces the ones below it.</summary>
    internal readonly record struct ActionKey(RuntimeHandle Handle, long Loan)
    {
        internal static ActionKey? Of(RuntimeHandle? handle) => handle is { } value ? new ActionKey(value, 0) : null;
    }
    internal sealed record ActionLayer(ActionKey? Key, ActionValue Value)
    {
        internal double Blocked;
    }

    // Receivers are resolved at command/restore admission. The tick and damage paths never parse target IDs.
    internal sealed class ActionState
    {
        internal Entry Entry = null!;
        internal ActionStateKind Kind;
        internal int LimbId;
        internal int LimbIndex = -1;
        internal Dam_EnemyDamageLimb? Limb;
        internal SquidBossBehaviour? Boss;
        internal EnemyAI? Ai;
        internal EnemyBehaviour? Behaviour;
        internal EnemyLocomotion? Locomotion;
        internal ES_HitreactBase? Hitreact;
        internal INavigation? Navigation;
        internal NavMeshAgent? Ground;
        internal FlyingAirGraphAgent? Air;
        internal Dam_EnemyDamageBase? Damage;
        internal ActionValue Baseline;
        internal readonly List<ActionLayer> Layers = new();
        internal ActionValue Current => Layers.Count == 0 ? Baseline : Layers[^1].Value;
        internal Agents.Agent? Source;
        internal EntityReference? SourceReference;
        internal Entry? SourceLife;
        internal Func<bool>? SourceCurrentProbe;
        internal bool SourceCurrent;
        internal ActionKey? PendingRestore;
        internal long MoveWrites;
        internal Vector3 MoveBefore, MoveAfterGoal;

        internal bool SameReceiver(ActionState other) => ReferenceEquals(Entry, other.Entry)
            && Kind == other.Kind && LimbId == other.LimbId;
    }
    internal readonly List<ActionState> _actionStates = new();
    private sealed class ActionStateWriteException : Exception
    {
        internal string Code { get; }
        internal ActionStateWriteException(string code, Exception? inner = null, string? detail = null)
            : base(detail == null ? code : code + " (" + detail + ")", inner) => Code = code;
    }

    private bool ActionReceiverCurrent(ActionState state, bool cleanup = false)
    {
        var entry = state.Entry;
        if ((!cleanup && entry.Reference.WorldEpoch != _kernel.WorldEpoch)
            || (cleanup && (!_entities.TryGetValue(entry.GlobalId, out var current) || !ReferenceEquals(current, entry)))
            || entry.Enemy.Pointer != entry.EnemyPointer
            || entry.Enemy.GlobalID != entry.GlobalId) return false;
        return state.Kind switch
        {
            ActionStateKind.Limb => ActionLimbCurrent(state),
            ActionStateKind.Phase => state.Ai != null && entry.Enemy.AI?.Pointer == state.Ai.Pointer
                && state.Ai.m_enemyAgent?.Pointer == entry.EnemyPointer
                && state.Ai.m_behaviour?.Pointer == state.Boss?.Pointer,
            ActionStateKind.Move => state.Ai != null && entry.Enemy.AI?.Pointer == state.Ai.Pointer
                && state.Ai.m_navMeshAgent?.Pointer == state.Navigation?.Pointer
                && (state.Ground != null
                    ? state.Navigation?.TryCast<NavMeshAgentExtention.NavMeshAgentProxy>()?.m_agent?.Pointer == state.Ground.Pointer
                    : state.Navigation?.TryCast<FlyingAirGraphAgent>()?.Pointer == state.Air?.Pointer),
            ActionStateKind.DamageRule => state.Damage != null && entry.Enemy.Damage?.Pointer == state.Damage.Pointer
                && state.Damage.Owner?.Pointer == entry.EnemyPointer,
            ActionStateKind.Stationary or ActionStateKind.WakeLock => state.Ai != null && entry.Enemy.AI?.Pointer == state.Ai.Pointer
                && state.Ai.m_enemyAgent?.Pointer == entry.EnemyPointer
                && state.Ai.m_behaviour?.Pointer == state.Behaviour?.Pointer
                && state.Ai.m_locomotion?.Pointer == state.Locomotion?.Pointer
                && state.Locomotion?.m_agent?.Pointer == entry.EnemyPointer,
            ActionStateKind.Stagger => state.Locomotion != null && entry.Enemy.Locomotion?.Pointer == state.Locomotion.Pointer
                && state.Locomotion.m_agent?.Pointer == entry.EnemyPointer
                && state.Locomotion.Hitreact?.Pointer == state.Hitreact?.Pointer,
            _ => false
        };
    }

    private static bool ActionLimbCurrent(ActionState state)
    {
        var damage = state.Entry.Enemy.Damage;
        var limb = state.Limb;
        if (damage == null || !damage.IsSetup || damage.Owner?.Pointer != state.Entry.EnemyPointer
            || limb == null || limb.Pointer == IntPtr.Zero || limb.m_base?.Pointer != damage.Pointer
            || limb.m_limbID != state.LimbId || damage.DamageLimbs is not { } limbs) return false;
        if (state.LimbIndex >= 0 && state.LimbIndex < limbs.Length && limbs[state.LimbIndex]?.Pointer == limb.Pointer) return true;
        // Resolve/re-resolve array membership by the already owned native pointer. New command admission still
        // rejects duplicate limb IDs; cleanup must not lose its exact receiver because another part duplicates an ID.
        for (int index = 0; index < limbs.Length; index++)
            if (limbs[index]?.Pointer == limb.Pointer) { state.LimbIndex = index; return true; }
        return false;
    }

    private static ActionValue ReadActionState(ActionState state) => state.Kind switch
    {
        ActionStateKind.Limb => new(Type: (int)state.Limb!.m_type,
            Weakspot: state.Limb.m_weakspotDamageMulti, Armor: state.Limb.m_armorDamageMulti),
        ActionStateKind.Phase => new(Type: state.Boss!.Phase),
        ActionStateKind.Move => new(X: state.Ai!.NavmeshAgentGoal.x, Y: state.Ai.NavmeshAgentGoal.y,
            Z: state.Ai.NavmeshAgentGoal.z, Speed: state.Navigation!.speed, Tolerance: state.Navigation.stoppingDistance),
        ActionStateKind.DamageRule => state.Current,
        ActionStateKind.Stationary => new(Type: state.Entry.Stationary ? 1 : 0),
        ActionStateKind.WakeLock => new(Type: state.Entry.WakeLocked ? 1 : 0),
        ActionStateKind.Stagger => new(Type: state.Hitreact!.HitreactForbidden ? 1 : 0),
        _ => throw new InvalidOperationException("enemy-action-state-kind")
    };

    private void WriteActionState(ActionState state, ActionValue value, bool cleanup = false)
    {
        try
        {
            if (!ActionReceiverCurrent(state, cleanup))
                throw new ActionStateWriteException(ResultCode.ReceiverChangedDuringCommit);
        }
        catch (ActionStateWriteException) { throw; }
        catch (Exception error) { throw new ActionStateWriteException(ResultCode.ReadbackException, error); }
        // Only limb calculations run on every peer. AI, standing policies and reaction overrides remain host-owned;
        // peers retain their admitted contributions for the Runtime's existing host-migration snapshot.
        if (!Session.IsHost && state.Kind is not ActionStateKind.Limb)
        {
            // The logical value is shared; only the master's native AI guards enforce it.
            if (state.Kind == ActionStateKind.Stationary)
                WriteStationaryFlag(state.Entry, value.Type != 0);
            else if (state.Kind == ActionStateKind.WakeLock)
                WriteWakeLockFlag(state.Entry, value.Type != 0);
            return;
        }
        try
        {
            switch (state.Kind)
            {
                case ActionStateKind.Limb:
                    // SetLimbDamageType rewrites both multipliers natively, so the type goes first and the values after.
                    state.Limb!.SetLimbDamageType((eLimbDamageType)value.Type);
                    state.Limb.m_weakspotDamageMulti = value.Weakspot;
                    state.Limb.m_armorDamageMulti = value.Armor;
                    break;
                case ActionStateKind.Phase:
                    EnemyProfileWrite.SetPhase(state.Boss!, value.Type);
                    break;
                case ActionStateKind.Move:
                    bool traceMove = ForgeEngine.Plugin.ConfiguredMode == ForgeEngine.RuntimeMode.Authoring && Log.Logs(RuntimeLogLevel.Info);
                    if (traceMove) state.MoveBefore = state.Ai!.NavmeshAgentGoal;
                    state.Ai!.NavmeshAgentGoal = new Vector3(value.X, value.Y, value.Z);
                    if (traceMove) state.MoveAfterGoal = state.Ai.NavmeshAgentGoal;
                    state.Navigation!.speed = value.Speed;
                    if (state.Ground != null) state.Ground.stoppingDistance = value.Tolerance;
                    else state.Air!.m_stoppingDistance = value.Tolerance;
                    break;
                case ActionStateKind.DamageRule:
                    state.SourceReference = value.Policy.Source;
                    state.Source = value.Policy.Source is { } source ? _kernel.EntityInstance(source) as Agents.Agent : null;
                    state.SourceLife = value.Policy.Source is { } enemySource ? Resolve(enemySource) : null;
                    state.SourceCurrentProbe = state.Source != null && state.SourceLife == null && value.Policy.Source is { } playerSource
                        ? ForgePlayer.Native.PlayerHost.Session?.Module.BindCurrent(playerSource) : null;
                    state.SourceCurrent = ActionSourceCurrent(state);
                    if (state.SourceReference != null && !state.SourceCurrent)
                        throw new InvalidOperationException("source-filter-unavailable");
                    break;
                case ActionStateKind.Stationary:
                    WriteStationaryFlag(state.Entry, value.Type != 0);
                    // An enemy already walking stops where it is; one that is not, and one whose machine has no StandStill, is left to
                    // the movement gate alone. Nothing else about its state, sleep or wake is touched.
                    if (value.Type != 0 && state.Entry.Enemy.Alive && IsMovementState(state.Locomotion!.CurrentStateEnum)
                        && state.Locomotion.GetState((int)ES_StateEnum.StandStill) != null)
                        state.Locomotion.ChangeState(ES_StateEnum.StandStill);
                    break;
                case ActionStateKind.WakeLock:
                    // The lock is this module's own flag, read by the wake hooks; no native member is written.
                    WriteWakeLockFlag(state.Entry, value.Type != 0);
                    break;
                case ActionStateKind.Stagger:
                    state.Hitreact!.HitreactForbidden = value.Type != 0;
                    break;
            }
        }
        catch (Exception error) { throw new ActionStateWriteException(ResultCode.NativeCommitException, error); }
        try
        {
            if (!ActionReceiverCurrent(state, cleanup))
                throw new ActionStateWriteException(ResultCode.ReceiverChangedDuringCommit);
            if (!(state.Kind is ActionStateKind.Stationary or ActionStateKind.WakeLock && !state.Entry.Enemy.Alive))
            {
                var actual = ReadActionState(state);
                if (state.Kind == ActionStateKind.Move) TraceMoveReadback(state, value, actual, cleanup);
                if (actual != value)
                    throw new ActionStateWriteException(state.Kind == ActionStateKind.Limb
                        ? ResultCode.UnexpectedLimbReadback : ResultCode.UnexpectedStateReadback, null,
                        "kind=" + state.Kind + " cleanup=" + cleanup + " alive=" + state.Entry.Enemy.Alive
                        + " expected=" + value + " actual=" + actual);
            }
        }
        catch (ActionStateWriteException) { throw; }
        catch (Exception error) { throw new ActionStateWriteException(ResultCode.ReadbackException, error); }
    }

    private void TraceMoveReadback(ActionState state, ActionValue requested, ActionValue actual, bool cleanup)
    {
        if (ForgeEngine.Plugin.ConfiguredMode != ForgeEngine.RuntimeMode.Authoring || !Log.Logs(RuntimeLogLevel.Info)) return;
        long count = ++state.MoveWrites;
        if (!cleanup && !DiagnosticSample(count)) return;
        Log.Write(RuntimeLogLevel.Info, "enemy.move-readback", new RuntimeLogFacts
        {
            Entity = state.Entry.Reference.Id,
            Values = new[]
            {
                RuntimeLogValue.Of("phase", "goal-setter"), RuntimeLogValue.Of("cleanup", cleanup), RuntimeLogValue.Of("count", count),
                RuntimeLogValue.Of("life", state.Entry.Reference.LifeEpoch), RuntimeLogValue.Of("world", state.Entry.Reference.WorldEpoch),
                RuntimeLogValue.Of("requestedX", requested.X), RuntimeLogValue.Of("requestedY", requested.Y), RuntimeLogValue.Of("requestedZ", requested.Z),
                RuntimeLogValue.Of("beforeX", state.MoveBefore.x), RuntimeLogValue.Of("beforeY", state.MoveBefore.y), RuntimeLogValue.Of("beforeZ", state.MoveBefore.z),
                RuntimeLogValue.Of("afterGoalX", state.MoveAfterGoal.x), RuntimeLogValue.Of("afterGoalY", state.MoveAfterGoal.y), RuntimeLogValue.Of("afterGoalZ", state.MoveAfterGoal.z)
            }
        });
        // Goal settlement and physical arrival are different observations. Never call a restored goal an arrival,
        // nor a pending/partial path a completed restoration. These diagnostics do not change the write result.
        Log.Write(RuntimeLogLevel.Info, "enemy.move-readback", new RuntimeLogFacts
        {
            Entity = state.Entry.Reference.Id,
            Values = new[]
            {
                RuntimeLogValue.Of("phase", cleanup ? "cleanup" : "write"), RuntimeLogValue.Of("count", count),
                RuntimeLogValue.Of("life", state.Entry.Reference.LifeEpoch), RuntimeLogValue.Of("world", state.Entry.Reference.WorldEpoch),
                RuntimeLogValue.Of("nativePointer", state.Entry.EnemyPointer.ToString("x")),
                RuntimeLogValue.Of("navigationPointer", state.Navigation!.Pointer.ToString("x")),
                RuntimeLogValue.Of("requestedX", requested.X), RuntimeLogValue.Of("requestedY", requested.Y), RuntimeLogValue.Of("requestedZ", requested.Z),
                RuntimeLogValue.Of("actualX", actual.X), RuntimeLogValue.Of("actualY", actual.Y), RuntimeLogValue.Of("actualZ", actual.Z),
                RuntimeLogValue.Of("goalConfirmed", requested == actual),
                RuntimeLogValue.Of("speed", actual.Speed), RuntimeLogValue.Of("tolerance", actual.Tolerance)
            }
        });
        try
        {
            var position = state.Entry.Enemy.Position;
            var ground = state.Ground;
            Log.Write(RuntimeLogLevel.Info, "enemy.move-navigation", new RuntimeLogFacts
            {
                Entity = state.Entry.Reference.Id,
                Values = new[]
                {
                    RuntimeLogValue.Of("phase", cleanup ? "cleanup" : "write"), RuntimeLogValue.Of("life", state.Entry.Reference.LifeEpoch),
                    RuntimeLogValue.Of("positionX", position.x), RuntimeLogValue.Of("positionY", position.y), RuntimeLogValue.Of("positionZ", position.z),
                    RuntimeLogValue.Of("ground", ground != null), RuntimeLogValue.Of("pathPending", ground?.pathPending == true),
                    RuntimeLogValue.Of("pathStatus", ground == null ? -1 : (int)ground.pathStatus),
                    RuntimeLogValue.Of("hasPath", ground?.hasPath == true),
                    RuntimeLogValue.Of("remainingDistance", ground != null && float.IsFinite(ground.remainingDistance) ? ground.remainingDistance : -1),
                    RuntimeLogValue.Of("positionAtGoal", Vector3.Distance(position, new Vector3(requested.X, requested.Y, requested.Z)) <= requested.Tolerance)
                }
            });
        }
        catch (Exception error) { EnemyLog.Contained(error); }
    }

    private BehaviorOutcome InstallActionState(ActionState candidate, ActionValue value, RuntimeHandle? handle)
        => InstallActionState(candidate, value, ActionKey.Of(handle));
    private BehaviorOutcome InstallActionState(ActionState candidate, ActionValue value, ActionKey? key)
    {
        ActionState state = candidate;
        bool added;
        ActionValue previous;
        try
        {
            for (int index = 0; index < _actionStates.Count; index++)
            {
                var existing = _actionStates[index];
                if (!existing.SameReceiver(candidate)) continue;
                if (ActionReceiverCurrent(existing)) state = existing;
                else RemoveActionState(index);
                break;
            }
            added = ReferenceEquals(state, candidate);
            if (!ActionReceiverCurrent(state)) return BehaviorOutcome.Refused(ResultCode.ReceiverChangedDuringCommit);
            previous = ReadActionState(state);
            if (added) state.Baseline = previous;
        }
        catch (Exception error)
        {
            Log.Exception(RuntimeLogLevel.Error, "enemy.action-state-baseline-unavailable", error);
            return BehaviorOutcome.Refused(ResultCode.StateBaselineUnavailable);
        }

        // Register before entering native code: even a partially throwing setter has an owned restore path.
        var layer = new ActionLayer(key, value);
        state.Layers.Add(layer);
        if (added) _actionStates.Add(state);
        try { WriteActionState(state, value); }
        catch (Exception failure)
        {
            state.Layers.RemoveAt(state.Layers.Count - 1);
            try
            {
                WriteActionState(state, previous);
                if (added) _actionStates.Remove(state);
            }
            catch (Exception error)
            {
                // Keep the baseline and remaining layers for lifecycle cleanup; never claim rollback succeeded.
                Log.Exception(RuntimeLogLevel.Error, "enemy.action-state-restore-failed", error);
            }
            Log.Exception(RuntimeLogLevel.Error, "enemy.action-state-write-failed", failure.InnerException ?? failure);
            return BehaviorOutcome.Unseen(failure is ActionStateWriteException staged ? staged.Code : ResultCode.NativeCommitException);
        }
        // A permanent top layer can never expose an older layer again.
        if (key == null && state.Layers.Count > 1) state.Layers.RemoveRange(0, state.Layers.Count - 1);
        // An immunity loan never enters the snapshot (CaptureActionState), so it leaves nothing to send.
        if (state.Kind != ActionStateKind.Stagger) ActionStateChanged();
        return BehaviorOutcome.Committed();
    }

    private static RuntimeHandle? ActionEffectHandle(CommandContext context) => context.EffectHandleId;
    private bool ActionSourceCurrent(ActionState state)
        => state.Source != null && (state.SourceLife is { } sourceLife
            ? sourceLife.Reference.WorldEpoch == _kernel.WorldEpoch && sourceLife.Enemy.GlobalID == sourceLife.GlobalId
                && sourceLife.Enemy.Pointer == sourceLife.EnemyPointer
            : state.SourceCurrentProbe?.Invoke() == true);
    internal void RestoreNativeLimb(RuntimeEffectContext context) => RestoreActionHandle(context, ActionStateKind.Limb);
    internal void RestorePhaseSet(RuntimeEffectContext context) => RestoreActionHandle(context, ActionStateKind.Phase);
    internal void RestoreMoveTo(RuntimeEffectContext context) => RestoreActionHandle(context, ActionStateKind.Move);
    internal void RestoreStationary(RuntimeEffectContext context) => RestoreActionHandle(context, ActionStateKind.Stationary);
    internal void RestoreWakeLock(RuntimeEffectContext context) => RestoreActionHandle(context, ActionStateKind.WakeLock);
    private void RestoreActionHandle(RuntimeEffectContext context, ActionStateKind kind)
    {
        if (context.HandleId.IsCast) RestoreActionHandle(context.HandleId, kind);
    }
    private void RestoreActionHandle(RuntimeHandle handle, ActionStateKind kind) => RestoreActionKey(new ActionKey(handle, 0), kind);
    private void RestoreActionKey(ActionKey key, ActionStateKind kind)
    {
        List<Exception>? failures = null;
        for (int index = _actionStates.Count - 1; index >= 0; index--)
        {
            var state = _actionStates[index];
            if (state.Kind != kind) continue;
            var previous = state.Current;
            var layers = state.Layers.ToArray();
            if (state.Layers.RemoveAll(layer => layer.Key == key) == 0) continue;
            try
            {
                if (previous != state.Current && ActionReceiverCurrent(state)) WriteActionState(state, state.Current);
                state.PendingRestore = null;
                if (state.Layers.Count == 0) _actionStates.RemoveAt(index);
                if (kind != ActionStateKind.Stagger) ActionStateChanged();
            }
            catch (Exception error)
            {
                // Retain the canceled handle until its native restoration is observed, so a retry is real work.
                state.Layers.Clear();
                state.Layers.AddRange(layers);
                if (kind == ActionStateKind.Stagger) state.PendingRestore = key;
                (failures ??= new()).Add(error);
            }
        }
        if (failures != null) throw new AggregateException("Enemy action restoration failed.", failures);
    }

    internal void PumpActionState()
    {
        PumpActionAuthority();
        for (int index = _actionStates.Count - 1; index >= 0; index--)
        {
            // A restore can remove several states that share a handle, so the list may be shorter than the index.
            if (index >= _actionStates.Count) continue;
            var state = _actionStates[index];
            if (state.PendingRestore is { } pending)
            {
                RestoreActionKey(pending, state.Kind);
                continue;
            }
            // A damage rule filtered to a player source: the player's life has no event on this side, so the probe is
            // asked once per tick, and only while such a rule stands. The receiver being gone or dead is not asked here:
            // despawn and death remove the receiver's states (`ForgetActionState`, `RemoveReceiverActionStates`).
            if (state.SourceReference != null && state.SourceCurrent)
                state.SourceCurrent = ActionSourceCurrent(state);
        }
    }
    internal void ForgetActionState(EnemyAgent enemy)
    {
        for (int index = _actionStates.Count - 1; index >= 0; index--)
            if (_actionStates[index].SourceLife?.EnemyPointer == enemy.Pointer) _actionStates[index].SourceCurrent = false;
        RemoveReceiverActionStates(enemy);
    }
    /// <summary>Removes the states standing on one enemy, which is what its death or despawn ends.</summary>
    private void RemoveReceiverActionStates(EnemyAgent enemy)
    {
        for (int index = _actionStates.Count - 1; index >= 0; index--)
            if (_actionStates[index].Entry.EnemyPointer == enemy.Pointer) RemoveActionState(index);
    }
    internal void RemoveActionState(int index, bool retiring = true)
    {
        var state = _actionStates[index];
        // Damage rules have no native standing-rule setter: removing the owned contribution is the restore.
        bool current = ActionReceiverCurrent(state, cleanup: true);
        if (state.Kind != ActionStateKind.DamageRule && current)
        {
            try { WriteActionState(state, state.Baseline, cleanup: true); }
            // A cleanup restore that the game answers with a different state is a result for this one receiver, not a failure of
            // the module: a dead receiver's AI belongs to the game's death handling, and a living one is mid-despawn or has had the
            // same field rewritten by the game since. It is recorded under its own name and the state is dropped, so the despawn
            // or world transition that asked for the cleanup finishes and no other enemy is affected.
            catch (ActionStateWriteException error) when ((retiring || !state.Entry.Enemy.Alive)
                && error.Code is ResultCode.UnexpectedStateReadback or ResultCode.UnexpectedLimbReadback)
            {
                // Info, not Error: the receiver is leaving the world (this catch only runs for a retiring or dead receiver), and the
                // game's despawn path answers the goal setter with its own goal, so the mismatch is evidence, not a fault.
                Log.Exception(RuntimeLogLevel.Info, state.Entry.Enemy.Alive
                    ? "enemy.action-state-restore-readback-mismatch" : "enemy.action-state-restore-receiver-retired", error);
            }
        }
        if (state.Kind == ActionStateKind.Stationary && !current) ForgetStationary(state.Entry);
        else if (state.Kind == ActionStateKind.WakeLock && !current) ForgetWakeLock(state.Entry);
        _actionStates.RemoveAt(index);
        ActionStateChanged();
    }
    internal void ClearActionState()
    {
        List<Exception>? failures = null;
        for (int index = _actionStates.Count - 1; index >= 0; index--)
        {
            try { RemoveActionState(index); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures != null) throw new AggregateException("Enemy action cleanup failed.", failures);
    }
}
