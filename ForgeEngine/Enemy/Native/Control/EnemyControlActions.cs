using System;
using AirNavigation;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeEngine.Framework;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeEnemy.Native;

/// <summary>Host-authoritative enemy control actions: the enemy a plan asks about is the one this module already
/// tracks, and each action submits exactly one native entry point the game itself calls.
///
/// The three rows implemented here are `forge.action.enemy.awaken` (ES_HibernateWakeUp.ActivateState),
/// `forge.action.enemy.sleep` (EnemyBehaviour.ChangeState(EB_States.Hibernating)) and
/// `forge.action.enemy.move_to` (EnemyAI.NavmeshAgentGoal). Each is a member of the enemy's own state machine or
/// AI, so the write is a transition the game's own replication already carries:
/// EnemyBehaviour.UpdateState calls EnemySync.ReplicateAIState (0x1568038), which reaches UpdateStateData
/// (0x15810F9) and TryCollectCaptureData (0x15813CB), and a client applies the replicated behaviour state through
/// EnemySync.IncomingState -> EnemyBehaviour.ChangeState (0x1580A6A). The locomotion state a wake enters is
/// replicated the same way: ES_HibernateWakeUp.RecieveStateData forces it on a client (0x17FDC6F).
///
/// Three rows of this family are not implemented, and no partial version of them is offered:
/// `forge.action.enemy.target_set` (it must answer with a `lease` handle), `forge.action.enemy.target_clear` and
/// `forge.action.enemy.state_request` (both consume `lease` handles). The runtime's handle pool is the kernel's:
/// `RuntimeKernel.CreateHandle` is private and `RuntimeModuleHandle` exposes only Publish/CancelScope/Dispose, so
/// a provider cannot mint the handle its own row declares nor read one back. A handle this assembly invented
/// would be a second handle namespace the kernel does not check, which is worse than reporting the gap, so the
/// gap is reported instead (see `ForgeEnemy/evidence/enemy-control-actions.json`).
///
/// What the native paths cannot express is not offered: the wake call has no alert amount and no gradual wake,
/// and the sleep state has no timer and no damage-only filter, so the cards carry none of them. Wake direction comes from the source's current,
/// provider-owned position. Arrival tolerance is written on the concrete navigation receiver and read through
/// INavigation.stoppingDistance; unknown receiver types are refused before any write. A move_to that carries
/// an `area` is served: the field is read through the shared definition table and the point the enemies are sent to
/// is a navigation point inside its volume (`EnemyModule.AreaDestination.cs`).</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The declared `sleep_policy` members. `when_idle` is the one this provider can check natively and
    /// still honour, so it is served; nothing else is accepted.</summary>
    private const string SleepImmediate = "immediate";
    private const string SleepWhenIdle = "when_idle";

    /// <summary>Native `EB_States.Hibernating` and `EB_States.SquidBoss_Hibernating`. The same pair the
    /// behaviour facts treat as the sleeping baseline, so `awakened` and this action agree on what sleep is.</summary>
    private const int NativeHibernating = (int)EB_States.Hibernating;
    private const int NativeSquidBossHibernating = (int)EB_States.SquidBoss_Hibernating;
    /// <summary>Native `ES_StateEnum.HibernateWakeUp`: the locomotion state the wake call enters.</summary>
    internal const int NativeHibernateWakeUp = (int)ES_StateEnum.HibernateWakeUp;
    /// <summary>Native `AgentMode.Patrolling`: with no valid target, this is the game's own idle.</summary>
    private const int NativeModePatrolling = (int)Agents.AgentMode.Patrolling;
    private const double MinimumDestinationComponent = -float.MaxValue;
    private const double MaximumDestinationComponent = float.MaxValue;
    private const double MinimumSpeed = 0;
    private const double MaximumSpeed = float.MaxValue;

    /// <summary>The wake-up the catalog asks for. `ES_HibernateWakeUp.ActivateState` is the game's own entry: its
    /// three callers are all hibernation detection paths (EB_Hibernating.UpdateDetection 0x166CC65,
    /// EB_Hibernating.OnNoiseDetected 0x166C2BB and 0x166C124), so what this submits is the transition the game
    /// makes when something wakes an enemy.
    ///
    /// The source is resolved before the first target by the kernel's budgeted entity observation, including its
    /// world and life.
    /// The native detection path passes its normalized source-minus-enemy direction and distance; a coincident
    /// source has zero direction and distance. It does not become an AI target or propagate the wake.
    ///
    /// The call applies the wake immediately, so a later rejection cannot un-wake it: every failure after the
    /// call is reported as an unknown commit rather than as a refusal, and only a read of the locomotion state
    /// the game entered confirms the commit.</summary>
    internal CommandResult Awaken(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        if (!context.TryEntity(EnemyControlContract.AwakenSourceSlot, out var source))
            return CommandResult.Rejected(ResultCode.SourceMissing);
        if (!_kernel.IsEntityCurrent(source)) return CommandResult.Rejected(ResultCode.StaleOrUnsupportedSource);
        var sourceQuery = _kernel.InspectEntities(new[] { source });
        var sourceSnapshot = sourceQuery.Items.Count == 1 ? sourceQuery.Items[0].Snapshot : null;
        if (sourceSnapshot == null)
            return CommandResult.Rejected(sourceQuery.Status == "rejected" ? sourceQuery.Code : ResultCode.SourcePositionUnavailable);
        // The author's reason has no native field; it travels as the result's detail, so a log or trace of the
        // step says why this wake was asked for.
        var reason = context.Text(EnemyControlContract.AwakenReasonSlot);
        var request = new AwakenRequest(this, source, sourceSnapshot);
        var outcome = context.EachTarget(ref request, AwakenStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyAwaken.RowTargetCount);
        if (!string.IsNullOrEmpty(reason))
            outcome = outcome.WithDetail("reason=" + (reason.Length > MaximumReasonChars ? reason[..MaximumReasonChars] : reason));
        return Reported(outcome, "awaken");
    }

    /// <summary>How much of an author's wake reason the result detail carries, well inside the detail's byte cap.</summary>
    private const int MaximumReasonChars = 120;

    private readonly record struct AwakenRequest(EnemyModule Module, EntityReference Source, RuntimeEntitySnapshot SourceSnapshot);

    private static readonly TargetStep<AwakenRequest> AwakenStep = static (ref AwakenRequest request, EntityReference target, int _, RowWriter rows)
        => rows.End(request.Module.AwakenOne(request, target));

    private BehaviorOutcome AwakenOne(in AwakenRequest request, EntityReference target)
    {
        var source = request.Source;
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        var enemy = entry.Enemy;
        // The behaviour machine, not the locomotion machine, owns the wake: the game's own wake path ends in
        // EnemyBehaviour.ChangeState, so the behaviour state decides whether an enemy is still asleep.
        EnemyBehaviour? behaviour;
        EnemyLocomotion? locomotion;
        try
        {
            if (!enemy.Alive) return BehaviorOutcome.Refused(ResultCode.NotAlive);
            if (IsWakeLockedAsleep(enemy)) return BehaviorOutcome.Refused(ResultCode.WakeLocked);
            var ai = enemy.AI;
            behaviour = ai?.m_behaviour;
            locomotion = ai?.m_locomotion;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.BehaviorUnavailable); }
        if (behaviour == null || locomotion == null) return BehaviorOutcome.Refused(ResultCode.BehaviorUnavailable);
        var behaviourPointer = behaviour.Pointer;
        var locomotionPointer = locomotion.Pointer;
        int before;
        ES_HibernateWakeUp? wakeup;
        try
        {
            before = (int)behaviour.m_currentStateName;
            wakeup = locomotion.HibernateWakeup;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.BehaviorUnavailable); }
        if (!EnemyEntityObserver.Hibernating(before)) return BehaviorOutcome.Already(ResultCode.AlreadyAwake);
        if (wakeup == null || wakeup.Pointer == IntPtr.Zero) return BehaviorOutcome.Refused(ResultCode.WakeReceiverMissing);
        var wakeupPointer = wakeup.Pointer;
        Vector3 direction;
        float distance;
        try
        {
            var position = enemy.Position;
            double x = request.SourceSnapshot.Position[0] - position.x;
            double y = request.SourceSnapshot.Position[1] - position.y;
            double z = request.SourceSnapshot.Position[2] - position.z;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (!double.IsFinite(length) || length > float.MaxValue) return BehaviorOutcome.Refused(ResultCode.WakeDirectionInvalid);
            distance = (float)length;
            direction = length == 0 ? Vector3.zero : new Vector3((float)(x / length), (float)(y / length), (float)(z / length));
            if (!_kernel.IsEntityCurrent(source)) return BehaviorOutcome.Refused(ResultCode.StaleOrUnsupportedSource);
            if (!CanExecute) return BehaviorOutcome.Refused(ResultCode.AuthorityOrPhase);
            if (Resolve(target) != entry || enemy.AI?.m_behaviour?.Pointer != behaviourPointer
                || enemy.AI?.m_locomotion?.Pointer != locomotionPointer || locomotion.HibernateWakeup?.Pointer != wakeupPointer)
                return BehaviorOutcome.Refused(ResultCode.ReceiverChangedDuringCommit);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.WakeDirectionUnavailable); }

        // The native call is the only place the world is written. Whatever it did, a failure observed after
        // it stays unknown.
        try { wakeup.ActivateState(direction, distance, 0f, false); }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }

        try
        {
            if (!CanExecute) return BehaviorOutcome.Unseen(ResultCode.AuthorityOrPhase);
            if (!_kernel.IsEntityCurrent(source)) return BehaviorOutcome.Unseen(ResultCode.SourceChangedDuringCommit);
            var current = Resolve(target);
            if (current == null || !ReferenceEquals(current, entry) || behaviour.Pointer != behaviourPointer
                || locomotion.Pointer != locomotionPointer || enemy.AI?.m_behaviour?.Pointer != behaviourPointer
                || enemy.AI?.m_locomotion?.Pointer != locomotionPointer || locomotion.HibernateWakeup?.Pointer != wakeupPointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            // The wake is committed when the game's own locomotion machine reports the state the wake call
            // enters. The behaviour machine follows through ES_HibernateWakeUp.Exit, a frame later, so it is
            // not the readback this action waits for.
            if ((int)locomotion.CurrentStateEnum != NativeHibernateWakeUp) return BehaviorOutcome.Unseen(ResultCode.WakeUnseen);
            if (!_kernel.IsEntityCurrent(source)) return BehaviorOutcome.Unseen(ResultCode.SourceChangedDuringCommit);
            if (!CanExecute || Resolve(target) != entry || enemy.AI?.m_behaviour?.Pointer != behaviourPointer
                || enemy.AI?.m_locomotion?.Pointer != locomotionPointer || locomotion.HibernateWakeup?.Pointer != wakeupPointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        return BehaviorOutcome.Committed();
    }

    /// <summary>The sleep the catalog asks for, submitted as the one native transition that means it:
    /// `EnemyBehaviour.ChangeState(EB_States.Hibernating)`. `ChangeState` writes `m_currentStateName` only when
    /// the incoming state differs (0x1567380), so the write is a real transition, and the behaviour state it
    /// writes is what `EnemySync` replicates and what the awakened fact samples.
    ///
    /// A timed sleep is a plan-side composition (`delay` then `awaken`), not a native field, and what wakes a
    /// sleeper is the game's own decision. `sleep_policy: when_idle` is served by checking the game's
    /// own idle before writing, so an enemy that is fighting is refused instead of being frozen mid-attack.
    ///
    /// The commit is confirmed by reading the state back; a state the machine did not take is an unknown commit,
    /// never a claimed one.</summary>
    internal CommandResult Sleep(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        var policy = context.Text(EnemyControlContract.SleepPolicySlot);
        if (policy is not (SleepImmediate or SleepWhenIdle)) return CommandResult.Rejected(ResultCode.SleepPolicyUnsupported);
        var request = new SleepRequest(this, policy == SleepWhenIdle);
        return Reported(context.EachTarget(ref request, SleepStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemySleep.RowTargetCount), "sleep");
    }

    private readonly record struct SleepRequest(EnemyModule Module, bool WhenIdle);

    private static readonly TargetStep<SleepRequest> SleepStep = static (ref SleepRequest request, EntityReference target, int _, RowWriter rows)
        => rows.End(request.Module.SleepOne(request.WhenIdle, target));

    private BehaviorOutcome SleepOne(bool whenIdle, EntityReference target)
    {
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        var enemy = entry.Enemy;
        EnemyBehaviour? behaviour;
        EnemyLocomotion? locomotion;
        try
        {
            if (!enemy.Alive) return BehaviorOutcome.Refused(ResultCode.NotAlive);
            var ai = enemy.AI;
            behaviour = ai?.m_behaviour;
            locomotion = ai?.m_locomotion;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.BehaviorUnavailable); }
        if (behaviour == null) return BehaviorOutcome.Refused(ResultCode.BehaviorUnavailable);
        var behaviourPointer = behaviour.Pointer;
        var locomotionPointer = locomotion?.Pointer ?? IntPtr.Zero;
        int before;
        try { before = (int)behaviour.m_currentStateName; }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.BehaviorUnavailable); }
        if (before == NativeHibernating) return BehaviorOutcome.Already(ResultCode.AlreadyHibernating);
        // The boss has its own hibernation state and its own scripted schedule; sending it to the ordinary
        // sleeper would leave the two machines disagreeing about which sleep this is.
        if (before == NativeSquidBossHibernating) return BehaviorOutcome.Refused(ResultCode.BossHibernationUnsupported);

        if (whenIdle)
        {
            bool idle;
            try
            {
                var ai = behaviour.m_ai;
                var detection = ai?.m_detection;
                idle = ai != null && (int)ai.m_mode == NativeModePatrolling && !ai.IsTargetValid
                    && detection != null && detection.m_biggestDetectionBuildup <= 0;
            }
            catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.BehaviorUnavailable); }
            if (!idle) return BehaviorOutcome.Refused(ResultCode.NotIdle);
        }

        try { behaviour.ChangeState(EB_States.Hibernating); }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }

        try
        {
            if (!CanExecute) return BehaviorOutcome.Unseen(ResultCode.AuthorityOrPhase);
            var current = Resolve(target);
            if (current == null || !ReferenceEquals(current, entry) || behaviour.Pointer != behaviourPointer
                || (locomotion != null && locomotion.Pointer != locomotionPointer))
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            if ((int)behaviour.m_currentStateName != NativeHibernating) return BehaviorOutcome.Unseen(ResultCode.SleepUnseen);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        return BehaviorOutcome.Committed();
    }

    /// <summary>The navigation request, submitted as `EnemyAI.NavmeshAgentGoal` — the member every native move
    /// behaviour writes (EB_InCombat_MoveToTarget 0x17F0457, EB_Patrolling 0x17F2570/0x17F25DC/0x17F2744,
    /// EB_Patrolling_Investigate 0x17F184B/0x17F1B22, EB_InCombat_MoveToPoint 0x1671121 and the flyer and
    /// pouncer move states). Writing it is how the game itself tells an enemy where to walk, and the AI that
    /// consumes it is the host's, so the movement itself is host-simulated and replicated as path data.
    ///
    /// The destination is the plan's coordinate, or — when the request carries an `area` — a navigation point
    /// inside that field's volume around that same coordinate, solved once for the command by the game's own mesh
    /// query (`EnemyModule.AreaDestination.cs`). A field this install does not ship, a field reference that names no
    /// field, and a volume that holds no navigation point are each refused by name before any target is read.
    ///
    /// INavigation exposes stoppingDistance only as a getter. Its ground proxy forwards to NavMeshAgent, whose
    /// stoppingDistance is writable; FlyingAirGraphAgent reads its own m_stoppingDistance. These two concrete
    /// receivers carry the requested arrival tolerance, which is then confirmed through the interface getter.
    ///
    /// The goal is written before it is read back; a write that did not take is an unknown commit, never a
    /// claimed one.</summary>
    internal CommandResult MoveTo(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        double requestedTolerance = context.Number(EnemyControlContract.MoveArrivalToleranceSlot) ?? double.NaN;
        if (!double.IsFinite(requestedTolerance) || requestedTolerance < 0 || requestedTolerance > float.MaxValue)
            return CommandResult.Rejected(ResultCode.ArrivalToleranceOutOfRange);
        float arrivalTolerance = (float)requestedTolerance;
        if (requestedTolerance > 0 && arrivalTolerance == 0)
            return CommandResult.Rejected(ResultCode.ArrivalToleranceOutOfRange);
        // The area port names a field definition this install ships. A definition this install does not have is a
        // different request from the bare coordinate, so it is refused rather than silently treated as the bare one.
        AreaFieldDefinition? area = null;
        if (context.Present(EnemyControlContract.MoveAreaSlot))
        {
            if (!context.TryResource(EnemyControlContract.MoveAreaSlot, out var areaReference)
                || areaReference.ResourceKind != AreaFieldDocument.ResourceKind)
                return CommandResult.Rejected(ResultCode.AreaReferenceInvalid);
            // The engine's one area-field table for the current world, the same one the trigger rows measure against.
            area = ForgeEngine.Trigger.Native.TriggerHost.Fields?.Current.Definition(areaReference.ResourceId);
            if (area == null) return CommandResult.Rejected(ResultCode.AreaFieldUnloaded);
        }
        var destination = context.Vector(EnemyControlContract.MoveDestinationSlot);
        if (destination is not { } coordinates
            || !double.IsFinite(coordinates.X) || !double.IsFinite(coordinates.Y) || !double.IsFinite(coordinates.Z)
            || coordinates.X < MinimumDestinationComponent || coordinates.X > MaximumDestinationComponent
            || coordinates.Y < MinimumDestinationComponent || coordinates.Y > MaximumDestinationComponent
            || coordinates.Z < MinimumDestinationComponent || coordinates.Z > MaximumDestinationComponent)
            return CommandResult.Rejected(ResultCode.DestinationInvalid);
        // `speed` is a required input of this capability and the row's own `speed` column is a number with no
        // absent form, so a frame that carries no speed is refused whole rather than answered with a row whose
        // speed nobody asked for and nobody read. A compiled plan always carries one: the plan loader requires
        // every non-optional input to be wired.
        double requested = context.Number(EnemyControlContract.MoveSpeedSlot) ?? double.NaN;
        if (!double.IsFinite(requested) || requested < MinimumSpeed || requested > MaximumSpeed)
            return CommandResult.Rejected(ResultCode.SpeedOutOfRange);
        double speed = requested;

        // The goal is solved once for the whole command: the plan's coordinate is the field's centre, and a volume
        // that holds no navigation point refuses the command instead of sending anybody to a coordinate off the mesh.
        var centre = new Vector3((float)coordinates.X, (float)coordinates.Y, (float)coordinates.Z);
        var goal = centre;
        if (area != null && !TryAreaDestination(area, centre, out goal))
            return CommandResult.Rejected(ResultCode.DestinationUnreachable);

        var request = new MoveToRequest(this, goal, speed, arrivalTolerance, ActionEffectHandle(context));
        return Reported(context.EachTarget(ref request, MoveToStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyMoveTo.RowTargetCount), "move_to");
    }

    private readonly record struct MoveToRequest(EnemyModule Module, Vector3 Goal, double Speed, float ArrivalTolerance, RuntimeHandle? Handle);

    /// <summary>The row reports the navigation speed it is about: the value the step read back where it made the
    /// reading, and the value the plan asked for on a row that never reached the agent. The declared `speed` column
    /// has no absent form, and a refusal row still knows the speed the step asked for.</summary>
    private static readonly TargetStep<MoveToRequest> MoveToStep = static (ref MoveToRequest request, EntityReference target, int _, RowWriter rows) =>
    {
        var outcome = request.Module.MoveToOne(request, target, out var applied);
        rows.Number(Blocks.ActionEnemyMoveTo.RowSpeed, applied).End(outcome);
    };

    private BehaviorOutcome MoveToOne(in MoveToRequest request, EntityReference target, out double applied)
    {
        var goal = request.Goal; var speed = request.Speed; var arrivalTolerance = request.ArrivalTolerance;
        applied = speed;
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        var enemy = entry.Enemy;
        EnemyAI? ai;
        try
        {
            if (!enemy.Alive) return BehaviorOutcome.Refused(ResultCode.NotAlive);
            ai = enemy.AI;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.NavigationUnavailable); }
        if (ai == null) return BehaviorOutcome.Refused(ResultCode.NavigationUnavailable);
        var aiPointer = ai.Pointer;
        // The speed the row reports is a reading, so the navigation component is required for every target: an
        // agent without one cannot answer the column this row declares, and is refused by name rather than
        // reported with a speed nobody read.
        INavigation? navigation;
        try { navigation = ai.m_navMeshAgent; }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.NavigationUnavailable); }
        if (navigation == null) return BehaviorOutcome.Refused(ResultCode.NavigationUnavailable);
        var navigationPointer = navigation.Pointer;
        NavMeshAgent? ground;
        FlyingAirGraphAgent? air;
        try
        {
            ground = navigation.TryCast<NavMeshAgentExtention.NavMeshAgentProxy>()?.m_agent;
            air = ground == null ? navigation.TryCast<FlyingAirGraphAgent>() : null;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.NavigationUnavailable); }
        if (ground == null && air == null) return BehaviorOutcome.Refused(ResultCode.ArrivalToleranceUnsupported);
        var tolerancePointer = ground?.Pointer ?? air!.Pointer;
        if (tolerancePointer == IntPtr.Zero) return BehaviorOutcome.Refused(ResultCode.NavigationUnavailable);
        try
        {
            if (!CanExecute) return BehaviorOutcome.Refused(ResultCode.AuthorityOrPhase);
            if (Resolve(target) != entry || enemy.AI?.Pointer != aiPointer || ai.m_navMeshAgent?.Pointer != navigationPointer
                || (ground != null ? navigation.TryCast<NavMeshAgentExtention.NavMeshAgentProxy>()?.m_agent?.Pointer
                    : navigation.TryCast<FlyingAirGraphAgent>()?.Pointer) != tolerancePointer)
                return BehaviorOutcome.Refused(ResultCode.ReceiverChangedDuringCommit);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.NavigationUnavailable); }

        try
        {
            var installed = InstallActionState(new ActionState
            {
                Entry = entry,
                Kind = ActionStateKind.Move,
                Ai = ai,
                Navigation = navigation,
                Ground = ground,
                Air = air
            },
                new(X: goal.x, Y: goal.y, Z: goal.z, Speed: (float)speed, Tolerance: arrivalTolerance), request.Handle);
            if (installed.Commit != Commit.Confirmed) return installed;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }

        try
        {
            if (!CanExecute) return BehaviorOutcome.Unseen(ResultCode.AuthorityOrPhase);
            var current = Resolve(target);
            if (current == null || !ReferenceEquals(current, entry) || ai.Pointer != aiPointer
                || navigation.Pointer != navigationPointer || enemy.AI?.Pointer != aiPointer
                || ai.m_navMeshAgent?.Pointer != navigationPointer
                || (ground != null ? navigation.TryCast<NavMeshAgentExtention.NavMeshAgentProxy>()?.m_agent?.Pointer
                    : navigation.TryCast<FlyingAirGraphAgent>()?.Pointer) != tolerancePointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            // The goal is what this action writes, so the goal is what it reads back: a value the AI did not
            // keep, or kept as something else, is not a movement the host ever asked for.
            var read = ai.NavmeshAgentGoal;
            if (!float.IsFinite(read.x) || !float.IsFinite(read.y) || !float.IsFinite(read.z)
                || read.x != goal.x || read.y != goal.y || read.z != goal.z)
                return BehaviorOutcome.Unseen(ResultCode.UnexpectedGoalReadback);
            // The speed is the second write, and the readback is its only proof: a value the agent did not keep
            // is not a movement this host ever asked for.
            double currentSpeed = navigation.speed;
            if (!double.IsFinite(currentSpeed) || Math.Abs(currentSpeed - speed) > 0.001)
                return BehaviorOutcome.Unseen(ResultCode.UnexpectedSpeedReadback);
            float currentTolerance = navigation.stoppingDistance;
            if (!float.IsFinite(currentTolerance) || currentTolerance != arrivalTolerance)
                return BehaviorOutcome.Unseen(ResultCode.UnexpectedArrivalToleranceReadback);
            if (!CanExecute || Resolve(target) != entry || enemy.AI?.Pointer != aiPointer || ai.m_navMeshAgent?.Pointer != navigationPointer
                || (ground != null ? navigation.TryCast<NavMeshAgentExtention.NavMeshAgentProxy>()?.m_agent?.Pointer
                    : navigation.TryCast<FlyingAirGraphAgent>()?.Pointer) != tolerancePointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            applied = currentSpeed;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        return BehaviorOutcome.Committed();
    }

    private readonly ForgeCore.OnceReporter _refusals = new();

    /// <summary>One command's conclusion, with the refusal record below. `action` names the command in that
    /// record.</summary>
    private CommandResult Reported(CommandResult result, string action)
    {
        // The step record of a refused command is Info, which this provider's log level does not write; a command that did
        // nothing says why once here, so the stop is never a bare "stopped". The rows carry each target's own code.
        // The 1st, 10th, 100th ... command with the same action, status and code writes; a bot refusing every tick must not flood.
        if (result.Status is CommandStatuses.Rejected or CommandStatuses.Failed or CommandStatuses.Partial && Log.Logs(RuntimeLogLevel.Error)
            && _refusals.ShouldReport(action + "|" + result.Status + "|" + result.Code))
            Log.Write(RuntimeLogLevel.Error, EnemyLogCodes.ActionRefused,
                new RuntimeLogFacts { Detail = action + " " + result.Status + " " + result.Code });
        return result;
    }
}
