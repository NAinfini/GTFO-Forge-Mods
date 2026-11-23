using System;
using Enemies;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>The three node-list actions this package's own native write paths carry:
/// `forge.action.enemy.kill`, `forge.action.enemy.mark` and `forge.action.enemy.target`.
///
/// Each row names exactly one native submission:
/// <list type="bullet">
/// <item>`kill` ends the target enemies' lives through ForgeCore `Combat.Kill`, whatever their health, melee resistance
/// or immortal flag: the game's damage funnel `ProcessReceivedDamage` with `DamageMax`, the step every received hit
/// ends in, so health observers see one drop to zero as for a one-shot kill. On the host the death hit reaction clears
/// `Alive` inside the call
/// (the ragdoll and `EnemyAgent.OnDead` come frames later), so the commit is confirmed by reading the agent's own
/// liveness back at once — a target that still reads alive is an unknown commit, never a claimed one.</item>
/// <item>`mark` builds Forge's own `NavMarker` on the enemy's model object through `GuiManager.NavMarkerLayer`,
/// then colours it with `NavMarker.SetColor`. It is the one marking path in the build that accepts a colour and
/// a caller-set lifetime at all (`%TEMP%\nativeinv\ni-markcolor.md`, route A): the game's own BioTracker tag has
/// a one-field packet, so no colour ever crosses the wire, and the vanilla tag's red is left alone. The marker is
/// per-machine presentation, so the row is `execution: presentation` (ruling 122.3): the host decides the step,
/// and each addressed player runs this handler on its own machine, which is why the gate below is the
/// presentation one and the result claims no commit.</item>
/// <item>`target` points each chosen enemy at one player through the game's own `EnemyAI.SetTarget(Agent)`. The
/// agent is the player entity's own native instance, resolved by the kind's registered owner through the
/// kernel, so this action never invents an agent for a reference another package minted.</item>
/// </list>
///
/// Every request is checked before the first target, because an action that cannot be carried out as asked must
/// not half-apply; a refusal names its own reason. `kill` and `target` gate on the module's own authority
/// (`CanExecute`: the registration, the startup state, the gameplay flag and the game's master flag), the same
/// gate every other host write in this package passes; `mark` gates on `CanPresent`, because the machine that
/// performs it is a recipient rather than the master.</summary>
internal sealed partial class EnemyModule
{
    internal const string KillBinding = EnemyNodeEffectContract.KillBinding;
    internal const string RemoveBinding = EnemyNodeEffectContract.RemoveBinding;
    internal const string MarkBinding = EnemyNodeEffectContract.MarkBinding;
    internal const string TargetBinding = EnemyNodeEffectContract.TargetBinding;
    internal const string KillHandler = EnemyNodeEffectContract.KillHandler;
    internal const string RemoveHandler = EnemyNodeEffectContract.RemoveHandler;
    internal const string MarkHandler = EnemyNodeEffectContract.MarkHandler;
    internal const string TargetHandler = EnemyNodeEffectContract.TargetHandler;

    /// <summary>The one visibility policy `mark` implements. The catalog's row declares it as the structural
    /// choice an author makes; every machine that renders a NavMarker would see it, so `team` is what a marker
    /// that is not routed to named players can honestly be.</summary>
    private const string TeamVisibility = "team";
    /// <summary>The bound on one marker's requested lifetime: a day of plan time. A longer request is refused
    /// rather than kept as a value the arithmetic below cannot represent, and the day is the world's own rate times
    /// its seconds so the bound means the same span in every world.</summary>
    private const double MaximumDurationSeconds = 60d * 60d * 24d;
    private const double MinimumColorComponent = 0.0, MaximumColorComponent = 1.0;

    /// <summary>`forge.action.enemy.kill`. The native call is the only place the world is written: a failure
    /// observed after it stays unknown, and every other target is still attempted.</summary>
    internal CommandResult Kill(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        var module = this;
        return context.EachTarget(ref module, KillStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyKill.RowTargetCount);
    }

    private static readonly TargetStep<EnemyModule> KillStep = static (ref EnemyModule module, EntityReference target, int _, RowWriter rows)
        => module.KillOne(target, rows);

    private void KillOne(EntityReference target, RowWriter rows)
    {
        var entry = Resolve(target);
        if (entry == null) { rows.End(Status.Rejected, UnresolvedCode(target)); return; }
        var enemy = entry.Enemy;
        Dam_EnemyDamageBase? receiver;
        try
        {
            if (!enemy.Alive) { rows.End(Status.Succeeded, ResultCode.AlreadyDead); return; }
            receiver = enemy.Damage;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Rejected, ResultCode.HealthReceiverUnavailable); return; }
        // The instant-death entry is the receiver's own, so a receiver that is not set up or belongs to
        // another agent is not this life's to end.
        if (receiver == null || receiver.Pointer == IntPtr.Zero || !receiver.IsSetup
            || receiver.Owner == null || receiver.Owner.Pointer != entry.EnemyPointer)
        { rows.End(Status.Rejected, ResultCode.MissingHealthReceiver); return; }
        var receiverPointer = receiver.Pointer;
        if (!CanExecute) { rows.End(Status.Rejected, ResultCode.AuthorityOrPhase); return; }

        try { ForgeCore.Combat.Kill(receiver); }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Failed, ResultCode.NativeCommitException); return; }

        try
        {
            if (!CanExecute) { rows.End(Status.Failed, ResultCode.AuthorityOrPhase); return; }
            var current = Resolve(target);
            if (current == null || !ReferenceEquals(current, entry) || enemy.Damage == null
                || enemy.Damage.Pointer != receiverPointer)
            { rows.End(Status.Failed, ResultCode.ReceiverChangedDuringCommit); return; }
            // The call reports nothing, so the only proof of a committed kill is the agent's own liveness.
            if (enemy.Alive) { rows.End(Status.Failed, ResultCode.DeathUnseen); return; }
        }
        catch (Exception caught) { EnemyLog.Contained(caught); rows.End(Status.Failed, ResultCode.ReadbackException); return; }
        rows.End(Status.Succeeded, ResultCode.Committed);
    }

    /// <summary>`forge.action.enemy.remove`. The world is written once per target, through the game's own
    /// replication despawn: an enemy's replicator is asked to go away, which is the path a despawned enemy always
    /// takes, and no life ends, no death settlement runs and no corpse is left behind.
    ///
    /// The call reports nothing, so the proof of a committed removal is this module's own despawn observation:
    /// the reference this provider minted for the target no longer resolves once the game's despawn hook has run.
    /// A target still resolvable after the call is an unknown commit, never a claimed one; every other target is
    /// still attempted.</summary>
    internal CommandResult Remove(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        var module = this;
        return context.EachTarget(ref module, RemoveStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.ReadbackException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyRemove.RowTargetCount);
    }

    private static readonly TargetStep<EnemyModule> RemoveStep = static (ref EnemyModule module, EntityReference target, int _, RowWriter rows)
        => rows.End(module.RemoveOne(target));

    private BehaviorOutcome RemoveOne(EntityReference target)
    {
        var entry = Resolve(target);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(target));
        var enemy = entry.Enemy;
        try
        {
            // As `kill` answers a dead target: nothing living is left to take away, so the row succeeds untouched.
            if (!enemy.Alive) return BehaviorOutcome.Already(ResultCode.AlreadyDead);
            var replicator = enemy.Sync?.Replicator;
            if (replicator == null) return BehaviorOutcome.Refused(ResultCode.ReplicatorUnavailable);
            if (!CanExecute) return BehaviorOutcome.Refused(ResultCode.AuthorityOrPhase);
            try { replicator.Despawn(); }
            catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }
            // The game's own despawn is what this module observes, so the removal is confirmed by the
            // reference it minted no longer answering — not by the call's own return.
            if (Resolve(target) != null) return BehaviorOutcome.Unseen(ResultCode.RemovalUnseen);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        return BehaviorOutcome.Committed();
    }

    /// <summary>`forge.action.enemy.mark`. The request check runs before the first target: an unknown visibility
    /// policy, a malformed colour or an out-of-range duration refuses the whole command rather than marking part
    /// of a set. Every marker built in one dispatch shares one `marker` handle, which is what a later `cancel`
    /// removes them through.
    ///
    /// The row is this provider's one `presentation` step (ruling 122.3), so the handler runs on each addressed
    /// player's own machine: the gate is the presentation one, the per-row commit column is the tier's own `none`,
    /// and the aggregate reports the placements as a non-committing result — a NavMarker is local presentation,
    /// not world state, and the tier refuses a handler that claims otherwise. The conclusion is the presentation
    /// one, so the rows go through the presentation skeleton.</summary>
    internal CommandResult Mark(CommandContext context)
    {
        if (!CanPresent) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        if (context.Text(EnemyNodeEffectContract.MarkVisibilitySlot) != TeamVisibility)
            return CommandResult.Rejected(ResultCode.VisibilityUnsupported);
        if (!TryColor(context, out var color)) return CommandResult.Rejected(ResultCode.InvalidColor);
        if (!TryDuration(context, _kernel.TicksPerSecond, out var duration)) return CommandResult.Rejected(ResultCode.DurationOutOfRange);
        if (!ForgeUI.UIKit.NavMarkersAvailable) return CommandResult.Rejected(ResultCode.MarkerLayerUnavailable);

        var request = new MarkRequest(this, color, duration);
        var outcome = context.EachTargetPresented(ref request, MarkStep, EntityKinds.Enemy, report: EnemyRows.Contained,
            countColumn: Blocks.ActionEnemyMark.RowTargetCount);
        if (request.Batch is { } batch)
        {
            try
            {
                // The kernel holds the batch this dispatch placed for the handle's declared lifetime and asks this
                // provider whether anything in it is still there, so a mark that outlived its handle is one the
                // kernel releases rather than one a second table would have to notice.
                _registration.RegisterNative(request.Marker, batch, native => native is MarkerBatch placed && placed.AnyLive());
                _registration.RegisterCancel(request.Marker, () => RemoveMarked(batch));
            }
            // The marks stay placed and expire on their own; only the handle's cancel is lost.
            catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.MarkHandleFailed, error); }
            context.Outputs.Handle(Blocks.ActionEnemyMark.OutMarker, request.Marker);
        }
        // A presented mark commits no world state, so the conclusion is the presentation tier's own; how many of the
        // targets were placed is in the rows.
        return outcome;
    }

    /// <summary>What every marker of one dispatch shares; the handle and the batch are made with the first marker.</summary>
    private struct MarkRequest
    {
        internal MarkRequest(EnemyModule module, Color color, long duration)
        { Module = module; Color = color; Duration = duration; Marker = default; Batch = null; }
        internal readonly EnemyModule Module;
        internal readonly Color Color;
        internal readonly long Duration;
        internal RuntimeHandle Marker;
        internal MarkerBatch? Batch;
    }

    private static readonly TargetStep<MarkRequest> MarkStep = static (ref MarkRequest request, EntityReference target, int _, RowWriter rows)
        => request.Module.MarkOne(ref request, target, rows);

    private void MarkOne(ref MarkRequest request, EntityReference target, RowWriter rows)
    {
        // A refusal row reports no lifetime; a placed one reports the one its marker holds.
        static void Refuse(RowWriter open, string code) => open.Integer(Blocks.ActionEnemyMark.RowDuration, 0)
            .End(Status.Rejected, Commit.None, code);
        var entry = Resolve(target);
        if (entry == null) { Refuse(rows, UnresolvedCode(target)); return; }
        var enemy = entry.Enemy;
        GameObject? model;
        try
        {
            if (!enemy.Alive) { Refuse(rows, ResultCode.NotAlive); return; }
            model = enemy.MainModelGO;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); Refuse(rows, ResultCode.EnemyModelUnavailable); return; }
        // The game's own tag placement declines silently when the enemy has no model object; a marker with
        // nothing to track would be a handle onto nothing, so it is refused by name.
        if (model == null) { Refuse(rows, ResultCode.EnemyModelUnavailable); return; }

        ForgeUI.NavMarkerHandle? placed;
        try
        {
            // The marker is built first and the handle is cast only once one really exists, so a dispatch
            // that placed nothing mints nothing: a handle that named no marker would be a name for nothing.
            placed = ForgeUI.UIKit.AddEnemyMarker(model, request.Color);
            if (placed == null) { Refuse(rows, ResultCode.MarkerUnplaced); return; }
            if (request.Batch == null)
            {
                request.Marker = _registration.CastEffectHandle("encounter");
                request.Batch = new MarkerBatch();
                // The expiry pump sweeps the batches this provider placed, so the batch joins the ledger the
                // moment the first marker of the dispatch exists — the handle registers it as its native object
                // once the whole dispatch is placed.
                _batches.Add(request.Batch);
            }
        }
        catch (Exception caught)
        {
            EnemyLog.Contained(caught);
            rows.Integer(Blocks.ActionEnemyMark.RowDuration, 0).End(Status.Failed, Commit.None, ResultCode.NativeCommitException);
            return;
        }
        // The marker tracks an instance the reference no longer names, so it is taken back before the row says none.
        if (!ReferenceEquals(Resolve(target), entry))
        { RemoveMarker(placed); Refuse(rows, ResultCode.ReceiverChangedDuringCommit); return; }
        // The lifetime the row reports is the one the marker really holds, in the unit the column declares: the
        // recorded expiry minus the tick the placement happened on, which is what a caller can rely on rather
        // than what it asked for.
        // No lifetime asked for (absent or zero ticks) is a marker that stays until its enemy life ends or its handle is
        // cancelled, never one that is gone on the next tick.
        var duration = request.Duration;
        long expiresAt = duration == 0 ? long.MaxValue : _kernel.CurrentTick + duration;
        // A mark is presentation-local work: the marker was placed and none of it commits world state, which is
        // the one non-committing success the result rules allow — `partial + none`, never `succeeded + none`.
        rows.Integer(Blocks.ActionEnemyMark.RowDuration, duration).End(Status.Partial, Commit.None, ResultCode.MarkerPlaced);
        request.Batch!.Records.Add(new MarkerRecord(target, entry.EnemyPointer, placed, expiresAt));
    }

    /// <summary>`forge.action.enemy.target`. The player is the entity's own native instance, read by the kind's
    /// own lookup — the module's registered agent resolver — so a reference no provider can turn back into an
    /// instance is refused before the first enemy is touched. A reference of another kind is refused even earlier,
    /// by name.</summary>
    internal CommandResult Target(CommandContext context)
    {
        if (!CanExecute) return CommandResult.Rejected(ResultCode.AuthorityOrPhase);
        if (!context.TryEntity(EnemyNodeEffectContract.TargetPlayerSlot, out var target))
            return CommandResult.Rejected(ResultCode.TargetUnavailable);
        if (!EntityKinds.Is(target, EntityKinds.Player))
            return CommandResult.Rejected(ResultCode.TargetNotAPlayer);
        Agents.Agent? player;
        try { player = _agentOf(target) as Agents.Agent; }
        catch (Exception caught) { EnemyLog.Contained(caught); return CommandResult.Rejected(ResultCode.TargetUnavailable); }
        // A null agent is either a reference the kind's own owner does not answer for, or an instance the game's
        // table no longer holds; both are the same refusal at this boundary, and both happen before any enemy is
        // touched.
        if (player == null || player.Pointer == IntPtr.Zero) return CommandResult.Rejected(ResultCode.TargetUnavailable);

        var request = new TargetRequest(this, player);
        return context.EachTarget(ref request, TargetStep, EntityKinds.Enemy,
            exceptionCode: ResultCode.NativeCommitException, report: EnemyRows.Contained, countColumn: Blocks.ActionEnemyTarget.RowTargetCount);
    }

    private readonly record struct TargetRequest(EnemyModule Module, Agents.Agent Player);

    private static readonly TargetStep<TargetRequest> TargetStep = static (ref TargetRequest request, EntityReference reference, int _, RowWriter rows)
        => rows.End(request.Module.TargetOne(request.Player, reference));

    /// <summary>One enemy's target, written through `EnemyAI.SetTarget(Agent)` — the game's own entry that takes
    /// the enemy's behaviour-data target for that agent and makes it the AI's current target. Only this enemy is
    /// changed: `EnemyAgent.PropagateTargetFull/Limited` hand a target to the other enemies around this one and
    /// leave this one alone, so they are not this row. The commit is confirmed by reading the AI's target back.</summary>
    private BehaviorOutcome TargetOne(Agents.Agent player, EntityReference reference)
    {
        var entry = Resolve(reference);
        if (entry == null) return BehaviorOutcome.Refused(UnresolvedCode(reference));
        var enemy = entry.Enemy;
        EnemyAI? ai;
        try
        {
            if (!enemy.Alive) return BehaviorOutcome.Refused(ResultCode.NotAlive);
            ai = enemy.AI;
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Refused(ResultCode.EnemyStateUnavailable); }
        if (ai == null) return BehaviorOutcome.Refused(ResultCode.EnemyStateUnavailable);
        if (!CanExecute) return BehaviorOutcome.Refused(ResultCode.AuthorityOrPhase);
        try { ai.SetTarget(player); }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.NativeCommitException); }
        try
        {
            if (!ReferenceEquals(Resolve(reference), entry) || enemy.AI?.Pointer != ai.Pointer)
                return BehaviorOutcome.Unseen(ResultCode.ReceiverChangedDuringCommit);
            if (ai.Target?.m_agent?.Pointer != player.Pointer) return BehaviorOutcome.Unseen(ResultCode.UnexpectedStateReadback);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return BehaviorOutcome.Unseen(ResultCode.ReadbackException); }
        return BehaviorOutcome.Committed();
    }

    /// <summary>The requested colour as the Unity value the native setter takes. The row's `color` port is a
    /// `vector3` (red, green, blue) and `opacity` is the fourth channel, because the catalog has no colour port
    /// type; each component has to be a finite number inside the closed unit interval, and an opacity outside it
    /// is refused rather than clamped.</summary>
    private static bool TryColor(CommandContext context, out Color color)
    {
        color = default;
        if (context.Vector(EnemyNodeEffectContract.MarkColorSlot) is not { } components
            || components.X < MinimumColorComponent || components.X > MaximumColorComponent
            || components.Y < MinimumColorComponent || components.Y > MaximumColorComponent
            || components.Z < MinimumColorComponent || components.Z > MaximumColorComponent) return false;
        float alpha = 1f;
        if (context.Present(EnemyNodeEffectContract.MarkOpacitySlot))
        {
            double requested = context.Number(EnemyNodeEffectContract.MarkOpacitySlot) ?? double.NaN;
            if (!double.IsFinite(requested) || requested < MinimumColorComponent || requested > MaximumColorComponent)
                return false;
            alpha = (float)requested;
        }
        color = new Color((float)components.X, (float)components.Y, (float)components.Z, alpha);
        return true;
    }

    /// <summary>The requested marker lifetime in plan ticks. An absent port is "no caller-set lifetime", which the marker
    /// answers with zero and keeps until its enemy life ends or its handle is cancelled; a present null is refused. The
    /// value the result reports is always the number the step asked for. The day-long ceiling is that span at the rate the running world reported, so the
    /// bound is a length of time rather than a tick count only one rate could mean.</summary>
    private static bool TryDuration(CommandContext context, double ticksPerSecond, out long duration)
    {
        duration = 0;
        if (!context.Present(EnemyNodeEffectContract.MarkDurationSlot)) return true;
        var value = context.Integer(EnemyNodeEffectContract.MarkDurationSlot);
        if (value == null) return false;
        duration = value.Value;
        return duration >= 0 && duration <= MaximumDurationSeconds * ticksPerSecond;
    }

    /// <summary>The same step read the other way: a native length in seconds as plan ticks at the rate the running
    /// world reported, rounded to the nearest tick and saturated rather than wrapped, so a length the clock cannot
    /// measure is published as the longest tick there is instead of as a negative one.</summary>
    private long Ticks(double seconds)
    {
        double ticks = Math.Round(seconds * _kernel.TicksPerSecond, MidpointRounding.AwayFromZero);
        if (ticks <= 0) return 0;
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }
}
