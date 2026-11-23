using System;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>One submission's conclusion, in the same three shapes every action in this package reports: a
/// confirmed commit, a refusal that never reached the world, and an attempt whose effect this side could not
/// observe. `Commit` is the state the result's own `committed` column carries; the `Committed()` factories are
/// the only members named for it.</summary>
internal readonly record struct BehaviorOutcome(Status Status, Commit Commit, string Code)
{
    internal static BehaviorOutcome Committed() => new(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
    /// <summary>The target already holds the state the action brings about: a confirmed no-op, named by its code.</summary>
    internal static BehaviorOutcome Already(string code) => new(Status.Succeeded, Commit.Confirmed, code);
    internal static BehaviorOutcome Refused(string code) => new(Status.Rejected, Commit.None, code);
    internal static BehaviorOutcome Unseen(string code) => new(Status.Failed, Commit.Unknown, code);
}

/// <summary>How every action of this package ends a result row: with the conclusion one target reached. The
/// `target_count` column is the action skeleton's, written from the count column each action hands it.</summary>
internal static class EnemyRows
{
    /// <summary>The commit state a row's status carries in this package: a success or a short success committed,
    /// a refusal committed nothing, and anything else is an attempt whose effect this side could not observe.</summary>
    internal static Commit CommitOf(Status status) => status switch
    {
        Status.Succeeded or Status.Partial => Commit.Confirmed,
        Status.Rejected => Commit.None,
        _ => Commit.Unknown
    };

    internal static void End(this RowWriter rows, BehaviorOutcome outcome) => rows.End(outcome.Status, outcome.Commit, outcome.Code);

    /// <summary>Ends the open row with a status and the commit state this package pairs with it.</summary>
    internal static void End(this RowWriter rows, Status status, string code) => rows.End(status, CommitOf(status), code);

    /// <summary>Logs the exception the action skeleton turned into a row's `failed + unknown`.</summary>
    internal static readonly Action<EntityReference, Exception> Contained = static (_, caught) => EnemyLog.Contained(caught);
}

/// <summary>`forge.action.enemy.ability`, decided without touching a game type.
///
/// The two decisions a request can carry are made before the first target, because a submission that cannot be
/// carried out as asked must not half-apply: `target_policy: nearest` has no native member behind it, an
/// `ability` reference this provider does not own is not an ability.
///
/// Per target the resource's exact type-local position is checked, the game's trigger is submitted, and the
/// result is claimed only while the world this attempt was made in is still the world it is reported in.</summary>
internal static class AbilityDecision
{
    internal static CommandResult Run(CommandContext context, EnemyModule.NativeBehaviorPorts ports, EnemyBehaviorLedger ledger,
        long worldEpoch)
    {
        if (!ports.CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        // `target_policy` declares one member, `current`: the game's own trigger (`EnemyAbilities.UseAbility`) acts on
        // the ability component and its owner's own target, and no native member names another agent. Picking the
        // nearest enemy first is a selector and `forge.action.enemy.target_set`, composed by the plan.
        if (!context.TryResource(EnemyBehaviorContract.AbilityResourceSlot, out var abilityReference))
            return CommandResult.Rejected(ResultCode.AbilityMissing);
        if (!EnemyAbilityResources.TryAbility(abilityReference, out var ability, out int index))
            return CommandResult.Rejected(ResultCode.AbilityKindUnsupported);
        var request = new Request(ports, ledger, worldEpoch, ability, index);
        return context.EachTarget(ref request, Step, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyAbility.RowTargetCount);
    }

    private readonly record struct Request(EnemyModule.NativeBehaviorPorts Ports, EnemyBehaviorLedger Ledger, long WorldEpoch, byte Ability, int Index);

    private static readonly TargetStep<Request> Step = static (ref Request request, EntityReference target, int _, RowWriter rows)
        => rows.End(One(request, target));

    /// <summary>One recipient: the resource's exact type-local position is checked, the game's trigger is
    /// submitted, and the result is claimed only while the world this attempt was made in is still the world it is
    /// reported in.</summary>
    private static BehaviorOutcome One(in Request request, EntityReference target)
    {
        var ports = request.Ports; var ledger = request.Ledger; var ability = request.Ability; var index = request.Index;
        if (!ports.IsCurrent(target.Id)) return BehaviorOutcome.Refused(EnemyModule.UnresolvedCode(target));
        if (!ports.IsAlive(target.Id)) return BehaviorOutcome.Refused(ResultCode.NotAlive);
        if (!ports.CanTrigger(target.Id)) return BehaviorOutcome.Refused(ResultCode.AbilitiesDisabled);
        if (!ports.HasAbility(target.Id, ability, index, out var selectedComponent))
            return BehaviorOutcome.Refused(ResultCode.AbilityNotRegistered);

        if (!ports.CanExecute) return BehaviorOutcome.Refused(ResultCode.AuthorityOrPhase);

        bool submitted, triggered;
        try { submitted = ports.TryUseAbility(target.Id, ability, index, out triggered); }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }
        // An attempt that could not be made at all is this side's refusal; an attempt the game itself
        // refused is the machine's own answer, and the two are reported apart.
        if (!submitted) return BehaviorOutcome.Refused(ResultCode.ReceiverUnavailable);
        if (!triggered) return BehaviorOutcome.Refused(ResultCode.AbilityRefused);

        try
        {
            if (!ports.CanExecute) return BehaviorOutcome.Unseen(ResultCode.AuthorityOrPhase);
            if (!ports.IsCurrent(target.Id)) return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            // The readback is the machine's own answer about the component the trigger reached.
            if (!ports.TryAbilityState(target.Id, ability, index, out var component, out bool finished)
                || component != selectedComponent)
                return BehaviorOutcome.Unseen(ResultCode.AbilityDisappeared);
            // The row this trigger replaces is read back before it goes. The game keeps one active ability
            // per agent, so a row still held here was ended by this trigger — but only a component the
            // machine still reports as unfinished was ended before its own lifetime ran out; a finished one
            // ended normally and publishes nothing, and one that can no longer be read is an unknown the
            // module is not told about either.
            var replaced = ledger.Find(target.Id, request.WorldEpoch);
            if (replaced != null)
            {
                bool readable = ports.TryAbilityState(target.Id, replaced.Ability, replaced.Index, out var oldComponent, out bool replacedFinished)
                    && oldComponent == replaced.Component;
                ledger.End(target.Id, request.WorldEpoch);
                if (readable)
                    ports.AbilityDropped(replaced, replacedFinished,
                        ForgeEnemy.EnemyAbilityInterruptedContract.ReasonSuperseded);
            }
            if (!finished) ledger.Start(new RunningBehavior(target.Id, ability, index, component, request.WorldEpoch));
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        return BehaviorOutcome.Committed();
    }
}

/// <summary>`forge.action.enemy.noise_emit`, decided without touching a game type. The position is the caller's or
/// the source's, the radii are the row's own inner and outer bounds, and the node the native struct needs is
/// resolved from the position inside the bridge: a position no node covers is refused before the native call,
/// because the node is what the game's propagation walks.</summary>
internal static class NoiseDecision
{
    internal const double MaximumRadius = float.MaxValue;
    /// <summary>The smallest outer radius the row accepts, the card's own bound: the game makes no noise at 0.</summary>
    internal const double MinimumRadius = 0.01;
    internal const double MinimumPositionComponent = -float.MaxValue;
    internal const double MaximumPositionComponent = float.MaxValue;

    internal static CommandResult Run(CommandContext context, EnemyModule.NativeBehaviorPorts ports)
    {
        if (!ports.CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        // The source port is optional: a noise at a given position needs no emitter, and without one the command
        // writes no row. A wired source must still be a current entity of this world.
        EntityReference? source = null;
        if (context.Present(EnemyBehaviorContract.NoiseSourceSlot))
        {
            if (!context.TryEntity(EnemyBehaviorContract.NoiseSourceSlot, out var named) || named.WorldEpoch != context.WorldEpoch)
                return CommandResult.Rejected(ResultCode.SourceMissing);
            source = named;
        }
        double radius = context.Number(EnemyBehaviorContract.NoiseRadiusSlot) ?? double.NaN;
        // The game makes no noise with an outer radius of 0 or less, so such a radius is refused here by name.
        if (!double.IsFinite(radius) || radius < MinimumRadius || radius > MaximumRadius)
            return CommandResult.Rejected(ResultCode.RadiusOutOfRange);
        // The inner radius is optional and defaults to the outer one, which is the single-radius noise the row
        // emitted before the port existed. Both native radius fields keep their own authored values; an inner
        // radius is a band inside the outer one, so it lies between 0 and the outer radius.
        double radiusMin = radius;
        if (context.Present(EnemyBehaviorContract.NoiseRadiusMinSlot))
        {
            radiusMin = context.Number(EnemyBehaviorContract.NoiseRadiusMinSlot) ?? double.NaN;
            if (!double.IsFinite(radiusMin) || radiusMin < 0 || radiusMin > radius)
                return CommandResult.Rejected(ResultCode.RadiusMinOutOfRange);
        }
        // `cross_door` defaults to true: the row's own description is that the noise spreads, and a node-local
        // stimulus would not leave the room it was made in. Only a written `false` keeps it in one area.
        bool crossDoor = context.Boolean(EnemyBehaviorContract.NoiseCrossDoorSlot) ?? true;
        // `origin` selects the emitter: the given position, or the source entity's own position. The enum's
        // declaration order names `position` as the default.
        string origin = context.Text(EnemyBehaviorContract.NoiseOriginSlot) ?? "position";
        if (origin != "position" && origin != "source") return CommandResult.Rejected(ResultCode.OriginUnsupported);
        // `noise_type` is the game's own `NM_NoiseType` (the `enemy_noise_type` set in member order). Absent keeps
        // `Detectable`, the ordinary noise a sleeper can hear; a member the set does not name is refused.
        int noiseType = context.Text(EnemyBehaviorContract.NoiseTypeSlot) switch
        {
            null or "detectable" => 1,
            "insta_detect" => 0,
            "investigate" => 2,
            "pulse_only" => 3,
            _ => -1
        };
        if (noiseType < 0) return CommandResult.Rejected(ResultCode.NoiseTypeUnsupported);
        (double X, double Y, double Z) position;
        if (origin == "source")
        {
            if (source is not { } emitter) return CommandResult.Rejected(ResultCode.SourceMissing);
            if (!ports.TrySourcePosition(emitter, out position, out var sourceCode))
                return CommandResult.Rejected(sourceCode);
        }
        else
        {
            if (context.Vector(EnemyBehaviorContract.NoisePositionSlot) is not { } carried
                || !double.IsFinite(carried.X) || !double.IsFinite(carried.Y) || !double.IsFinite(carried.Z)
                || carried.X < MinimumPositionComponent || carried.X > MaximumPositionComponent
                || carried.Y < MinimumPositionComponent || carried.Y > MaximumPositionComponent
                || carried.Z < MinimumPositionComponent || carried.Z > MaximumPositionComponent)
                return CommandResult.Rejected(ResultCode.InvalidPosition);
            position = carried;
        }
        bool emitted;
        string code;
        try { emitted = ports.TryEmitNoise(source, origin == "source", position, radiusMin, radius, crossDoor, noiseType, out code); }
        // A contract error raised after the game took the noise (the source life ended during the submission) keeps its own
        // code; the commit state stays unknown because the noise was already submitted.
        catch (RuntimeContractException error) { return Failed(error.Code); }
        catch (Exception caught) { EnemyLog.Contained(caught); return Failed(ResultCode.NativeCommitException); }
        if (!emitted) return CommandResult.Rejected(code);
        // The noise event carries no result the submitting side can read back: the game's own update consumes the
        // datum. What the row can carry is the source the caller named, and the commit is claimed only while the
        // world this call was made in is still the world it is reported in.
        if (!ports.CanExecute) return Outcome.FailedUnknown(ResultCode.AuthorityOrPhase);
        if (source is not { } recipient) return Outcome.Succeeded();
        var rows = context.Rows;
        rows.Begin(0, recipient).Number(Blocks.ActionEnemyNoiseEmit.RowRadius, radius).End(BehaviorOutcome.Committed());
        return rows.Conclude();
    }

    /// <summary>The one failure a noise command can report before its row exists. It carries no row set at all: the
    /// frame's `result` port is the step's rows, and a step that wrote none must not carry an empty one.</summary>
    private static CommandResult Failed(string code) => Outcome.FailedUnknown(code);

}
