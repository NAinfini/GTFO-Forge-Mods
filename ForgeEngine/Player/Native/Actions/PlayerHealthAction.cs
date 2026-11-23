using System;
using Agents;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>The player binding of canonical healing. The native incremental entry receives the requested
/// delta and the actual source, while result rows report only the same receiver's observed health change.</summary>
internal static class PlayerHealthAction
{
    internal const double MinimumAmount = 0.000001;
    internal const double MaximumAmount = 1000000;

    private readonly record struct Regen(float Previous, float Next);

    /// <summary>What every recipient of one heal command shares, handed to <see cref="HealTarget"/> by reference.</summary>
    private struct HealCommand
    {
        internal PlayerIdentityModule Players;
        internal EntityReference Source;
        internal object? SourceInstance;
        internal double Requested;
        internal double? Cap;
        internal string Policy;
        internal bool CancelRegen;
    }

    private static readonly TargetStep<HealCommand> HealStep = HealTarget;

    private static void HealTarget(ref HealCommand command, EntityReference target, int index, RowWriter rows)
    {
        var row = Heal(command.Players, new PlayerHealRequest(target, command.Source, command.SourceInstance, command.Requested,
            command.Cap, command.Policy, command.CancelRegen), out var amount);
        rows.Number(Blocks.ActionCombatHeal.RowAmount, amount).End(row.Status, row.Commit, row.Code);
    }

    private const string SourceGone = "The heal source does not exist or is no longer valid.";

    internal static CommandResult Execute(PlayerIdentityModule? players, CommandContext context)
    {
        if (players == null || !players.CanCommit) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!context.TryEntity(PlayerHealthContract.SourceSlot, out var sourceReference))
        {
            PlayerCaughtFailures.Note("heal-state-changed", "site=source-slot-unresolved");
            return Outcome.Rejected(ResultCode.StateChangedBeforeCommit).WithDetail(SourceGone);
        }
        var policy = context.Text(PlayerHealthContract.OverhealPolicySlot);
        double requested = context.Number(PlayerHealthContract.AmountSlot) ?? double.NaN;
        if (!double.IsFinite(requested) || requested < MinimumAmount || requested > MaximumAmount)
            return Outcome.Rejected(ResultCode.AmountOutOfRange);
        double? cap = null;
        if (context.Present(PlayerHealthContract.CapSlot))
        {
            double value = context.Number(PlayerHealthContract.CapSlot) ?? double.NaN;
            if (!double.IsFinite(value) || value <= 0) return Outcome.Rejected(ResultCode.InvalidCap);
            cap = value;
        }
        var command = new HealCommand
        {
            Players = players,
            Source = sourceReference,
            SourceInstance = context.EntityInstance(sourceReference),
            Requested = requested,
            Cap = cap,
            Policy = policy ?? "clamp",
            CancelRegen = context.Boolean(PlayerHealthContract.CancelRegenSlot) == true
        };
        // A heal that fell short is `partial + confirmed`, so a row is done when it succeeded.
        return context.EachTarget(ref command, HealStep, EntityKinds.Player, CommandResult.MaximumFacts,
            ResultCode.NativeCommitException, doneWhenSucceeded: true, countColumn: Blocks.ActionCombatHeal.RowTargetCount);
    }

    /// <summary>The same recipient body in the words another domain's heal receipt carries (the enemy provider heals a
    /// player recipient through it).</summary>
    internal static PlayerHealReceipt HealOne(PlayerIdentityModule? players, PlayerHealRequest request)
    {
        var row = Heal(players, request, out var amount);
        return new(row.Status, row.Commit, row.Code, amount);
    }

    /// <summary>One player recipient: the exact life is read, the native incremental entry is asked once with the
    /// requested delta and the actual source, and the same receiver's health is read back for the receipt.</summary>
    private static Outcome Heal(PlayerIdentityModule? players, PlayerHealRequest request, out double amount)
    {
        amount = 0;
        if (players == null || !players.CanCommit) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var target = request.Target;
        var policy = request.OverhealPolicy;
        double requested = request.Amount;
        double? cap = request.Cap;
        bool cancelRegen = request.CancelRegen;
        if (!double.IsFinite(requested) || requested < MinimumAmount || requested > MaximumAmount)
            return Outcome.Rejected(ResultCode.AmountOutOfRange);
        if (cap.HasValue && (!double.IsFinite(cap.Value) || cap.Value <= 0))
            return Outcome.Rejected(ResultCode.InvalidCap);

        if (!EntityKinds.Is(target, EntityKinds.Player))
            return Outcome.Rejected(ResultCode.UnsupportedRecipient);

        PlayerHealthSnapshot before;
        Agent source;
        float delta;
        Regen regen = default;
        try
        {
            if (!PlayerHealthReceiver.TryRead(players, target, out before, out var code))
                return Outcome.Rejected(code);
            var resolved = Source(players, request.SourceInstance, request.Source);
            if (resolved == null)
            {
                PlayerCaughtFailures.Note("heal-state-changed", "site=source-agent-unresolved source=" + request.Source.Id + " instance="
                    + (request.SourceInstance?.GetType().Name ?? "null") + " currentAgent=" + (players.CurrentAgent(request.Source) != null));
                return Outcome.Rejected(ResultCode.StateChangedBeforeCommit);
            }
            source = resolved;
            double ceiling = cap.HasValue ? Math.Min(before.Maximum, cap.Value) : before.Maximum;
            double wanted = before.Health + requested;
            if (wanted > ceiling && policy == "discard")
                return Outcome.Rejected(ResultCode.WouldOverheal);
            delta = (float)Math.Max(0, Math.Min(ceiling, wanted) - before.Health);
            // Less than one native step to go is nothing the entry can commit; the receiver is already at its ceiling.
            if (delta <= ForgeCore.Combat.HealthPacketStep(before.Maximum)) delta = 0;
            if (cancelRegen && !PrepareRegen(players, before, out regen))
                return Outcome.Rejected(ResultCode.CancelRegenUnsupported);
            if (!players.CanCommit || !PlayerHealthReceiver.TryRead(players, target, out var again, out _)
                || again != before || Source(players, request.SourceInstance, request.Source)?.Pointer != source.Pointer)
            {
                PlayerHealthReceiver.TryRead(players, target, out var latest, out var latestCode);
                PlayerCaughtFailures.Note("heal-state-changed", FormattableString.Invariant($"site=preflight-recheck canCommit={players.CanCommit} before={before.Health}/{before.Maximum} again={latest.Health}/{latest.Maximum} readCode={latestCode} sameAgent={latest.AgentPointer == before.AgentPointer} sameReceiver={latest.ReceiverPointer == before.ReceiverPointer} sameSource={Source(players, request.SourceInstance, request.Source)?.Pointer == source.Pointer}"));
                return Outcome.Rejected(Changed(players, before));
            }
        }
        catch (Exception error)
        { PlayerCaughtFailures.Capture(ResultCode.PreflightException, error); return Outcome.Rejected(ResultCode.PreflightException); }

        if (delta > 0)
        {
            try
            {
                if (!PlayerHealthReceiver.TrySubmit(players, before, delta, source))
                {
                    PlayerHealthReceiver.TryRead(players, target, out var latest, out var latestCode);
                    PlayerCaughtFailures.Note("heal-state-changed", FormattableString.Invariant($"site=submit canCommit={players.CanCommit} before={before.Health}/{before.Maximum} now={latest.Health}/{latest.Maximum} readCode={latestCode} sameAgent={latest.AgentPointer == before.AgentPointer} sameReceiver={latest.ReceiverPointer == before.ReceiverPointer}"));
                    return Outcome.Rejected(Changed(players, before));
                }
            }
            catch (Exception error)
            {
                PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error);
                return Outcome.FailedUnknown(ResultCode.NativeCommitException);
            }
        }

        double actual = 0;
        if (delta > 0)
            try
            {
                if (!players.CanCommit || !PlayerHealthReceiver.TryRead(players, target, out var after, out _)
                    || after.AgentPointer != before.AgentPointer || after.ReceiverPointer != before.ReceiverPointer)
                    return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
                if (after.Maximum != before.Maximum || after.Health < before.Health)
                {
                    NoteReadback(target, before, after, delta, requested);
                    return Outcome.FailedUnknown(ResultCode.UnexpectedHealthReadback);
                }
                actual = (double)after.Health - before.Health;
            }
            catch (Exception error)
            {
                PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error);
                return Outcome.FailedUnknown(ResultCode.ReadbackException);
            }

        // A requested timer change is prepared before healing and committed only after health is observed.
        // A failure here must retain the health commit, or the uncertain timer write, in the result.
        if (cancelRegen)
        {
            try
            {
                if (!ApplyRegen(players, before, regen))
                {
                    amount = actual;
                    return actual > 0 ? new(Status.Partial, Commit.Confirmed, ResultCode.CancelRegenUnsupported)
                        : Outcome.Rejected(ResultCode.CancelRegenUnsupported);
                }
            }
            catch (Exception error)
            {
                PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error);
                amount = actual;
                return new(actual > 0 ? Status.Partial : Status.Failed, Commit.Unknown, ResultCode.ReadbackException);
            }
        }
        if (delta > 0 && actual == 0 && !cancelRegen)
        {
            // The host-side entry applies the heal inside the call, so an unchanged readback is not a landing that is still on its way.
            if (PlayerHealthReceiver.TryRead(players, target, out var settled, out _)) NoteReadback(target, before, settled, delta, requested);
            return Outcome.Rejected(ResultCode.UnexpectedHealthReadback);
        }
        // Both native packets quantize against HealthMax. One step is rounding, not a partial effect.
        bool incomplete = delta > 0 && (actual == 0 || actual + Math.Max(MinimumAmount, ForgeCore.Combat.HealthPacketStep(before.Maximum)) < delta);
        amount = actual;
        return incomplete ? Outcome.Partial() : Outcome.Succeeded();
    }

    /// <summary>The facts a refused heal readback was judged against, for the next run to read: the health the receiver held before the
    /// entry, what it holds now, and the delta the entry was asked for.</summary>
    private static void NoteReadback(EntityReference target, PlayerHealthSnapshot before, PlayerHealthSnapshot after, float delta, double requested)
        => PlayerCaughtFailures.Note("heal-readback", FormattableString.Invariant($"target={target.Id} requested={requested} delta={delta} before={before.Health}/{before.Maximum} after={after.Health}/{after.Maximum} sameAgent={after.AgentPointer == before.AgentPointer} sameReceiver={after.ReceiverPointer == before.ReceiverPointer} alive={after.Alive}"));

    private static Agent? Source(PlayerIdentityModule players, object? instance, EntityReference reference)
    {
        // Only an actual current Agent can be encoded as pAgent. An equipment source (a syringe: the heal primitive admits
        // gtfo.equipment) stays the named source of the heal; the agent the packet carries is the player that holds the item and
        // applies it, which is the one the game's own syringe heal names. Recipients are never derived from the item.
        var agent = players.CurrentAgent(reference) ?? instance as Agent ?? (instance as ItemEquippable)?.Owner;
        return agent != null && agent.Pointer != IntPtr.Zero ? agent : null;
    }

    /// <summary>Why a heal was refused before its native entry: the life or the receiver the preflight read is no
    /// longer the one the reference names, or the same receiver's state (or the source, or the authority) moved.</summary>
    private static string Changed(PlayerIdentityModule players, PlayerHealthSnapshot before)
    {
        var agent = players.CurrentAgent(before.Target);
        var damage = agent?.Damage;
        return agent == null || agent.Pointer != before.AgentPointer || damage == null || damage.Pointer != before.ReceiverPointer
            ? ResultCode.ReceiverChangedDuringCommit : ResultCode.StateChangedBeforeCommit;
    }

    private static bool PrepareRegen(PlayerIdentityModule players, PlayerHealthSnapshot expected, out Regen regen)
    {
        regen = default;
        var agent = players.CurrentAgent(expected.Target);
        var damage = agent?.Damage;
        var block = agent?.PlayerData;
        if (agent == null || agent.Pointer != expected.AgentPointer || damage == null
            || damage.Pointer != expected.ReceiverPointer || block == null || block.Pointer == IntPtr.Zero) return false;
        float delay = block.healthRegenStartDelayAfterDamage, previous = damage.m_nextRegen, now = Time.time;
        float next = Math.Max(previous, now + delay);
        if (!float.IsFinite(delay) || delay < 0 || !float.IsFinite(previous) || !float.IsFinite(now) || !float.IsFinite(next)) return false;
        regen = new(previous, next);
        return true;
    }

    private static bool ApplyRegen(PlayerIdentityModule players, PlayerHealthSnapshot expected, Regen regen)
    {
        var agent = players.CurrentAgent(expected.Target);
        var damage = agent?.Damage;
        if (!players.CanCommit || agent == null || agent.Pointer != expected.AgentPointer || damage == null
            || damage.Pointer != expected.ReceiverPointer || damage.m_nextRegen != regen.Previous) return false;
        damage.m_nextRegen = regen.Next;
        if (damage.m_nextRegen != regen.Next) throw new InvalidOperationException("Regeneration delay did not read back.");
        return true;
    }
}
