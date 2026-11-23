using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgePlayer.Native;

internal sealed partial class AgentModifierAdapter
{
    /// <summary>The `forge.action.combat.attribute_apply` handler. The row's `modifiers` port group names one or more
    /// `(attribute, amount)` members, and one command writes all of them on each recipient: a movement preset is
    /// speed and acceleration in one command, under one effect handle, so the step's `effect` duration restores
    /// both. The whole request check runs before the first recipient, because a command that cannot be carried out
    /// as asked must not half-apply: an attribute outside the table, a no-op member, an unbounded or zero amount and
    /// an over-wide recipient set are all refused by name, and a command whose effect handle cannot be read writes
    /// nothing.
    ///
    /// A recipient takes every member or none: its write budget is reserved for all of them up front, and a member
    /// the cap or the native entry stops on revokes the members that recipient already took, because half of the
    /// set is not the state the plan asked for.
    ///
    /// The effect's handle is the kernel's, not this provider's: the plan's own `effect` block is what gave the
    /// step a duration, a layer count and a cancellation, and the same handle is what the kernel hands back in the
    /// restore callback below. A step with no `effect` block has no handle at all, and a modification nothing can
    /// name lasts until its life ends or the world does.</summary>
    internal CommandResult Apply(CommandContext context)
    {
        CheckThread();
        // The identity half owns the readiness, authority and fault gate: a command that reaches here while it
        // says no would be writing where the same half refuses to read.
        if (_players() is not { } players || !players.CanCommit)
            return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        // `source` carries no faction or targeting restriction on this row (self, teammate and hostile are all
        // valid sources); it is still a required, kernel-validated entity reference.
        if (!context.TryEntity(AgentModifierContract.ApplySourceSlot, out _))
            throw new RuntimeContractException("shape-port", "attribute_apply source");
        var operation = context.Text(AgentModifierContract.ApplyOperationSlot);
        if (operation is not ("add" or "subtract")) return Outcome.Rejected(ResultCode.OperationUnsupported);
        if (!TryReadMembers(context, operation == "subtract", out var members, out var memberCode))
            return Outcome.Rejected(memberCode);
        var targets = context.Entities(AgentModifierContract.ApplyTargetsSlot);
        // One row is written per recipient; the result budget is the row budget.
        if (targets.Length > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        // The kernel answers an empty recipient collection itself, so nothing here means the port is not a
        // collection of entities.
        if (targets.Length == 0) return Outcome.Rejected(ResultCode.MissingField);
        // The optional cap bounds what this card has stacked on one recipient and member. The operation is
        // additive, so its own unit is 0: a cap above it is an upper bound, one below it a lower bound, and the
        // unit itself names no bound. It clamps the provider's own live total, which is what "the combined value
        // this card stacked" means; the native table's total also holds every other source's contribution and is
        // deliberately not read here.
        double? cap = null;
        if (context.Present(AgentModifierContract.ApplyCapSlot))
        {
            double capValue = context.Number(AgentModifierContract.ApplyCapSlot) ?? double.NaN;
            if (!double.IsFinite(capValue) || capValue == 0)
                return Outcome.Rejected(ResultCode.InvalidCap);
            cap = capValue;
        }

        // The kernel's handle is what this command's modifications are filed under, because it is what the kernel
        // calls back with when the effect ends. A value the kernel did not cast for this step — a card that asked
        // for no effect has none — leaves the group unnamed; the modifications are still in the ledger and the
        // sweep and the world's own end release them.
        var key = context.EffectHandleId ?? default;
        var named = key.IsCast;
        var refresh = named && !context.EffectAddLayer && _groups.TryGetValue(key, out var running) ? running : null;
        var enemyReceiver = ForgeEnemy.Native.EnemyHost.Session?.Module;
        var request = new Request
        {
            Members = members,
            EnemyReceiver = enemyReceiver,
            Cap = cap,
            Key = key,
            Named = named,
            Refresh = refresh,
            TargetCount = targets.Length
        };

        var rows = context.Rows;
        var written = new List<Applied>(targets.Length * members.Length);
        int committed = 0, queued = 0;
        bool deferring = false;
        for (int index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            // Once one recipient waits for the budget every later one waits behind it, and a command that arrives
            // while earlier ones still wait goes behind them (first in, first written).
            deferring |= committed > 0 && _pending.Count != 0;
            Outcome row = default;
            double amount = 0;
            var result = deferring ? Written.Deferred
                : WriteRecipient(request, target, context.SimulationTick, committed == 0, written, out row, out amount);
            if (result == Written.Deferred)
            {
                deferring = true;
                request.Waiting.Enqueue(target);
                row = new Outcome(Status.Partial, Commit.None, ResultCode.ModifierBudget);
                amount = 0;
                queued++;
            }
            else if (result == Written.Committed) committed++;
            rows.Begin(index, target).Number(Blocks.ActionCombatAttributeApply.RowAmount, amount)
                .Integer(Blocks.ActionCombatAttributeApply.RowTargetCount, targets.Length).End(row.Status, row.Commit, row.Code);
        }
        if (request.Waiting.Count != 0) _pending.Enqueue(request);

        // The handle is not this row's output: the kernel publishes the effect handle its card asked for into
        // this capability's own `modifier` port, so a module that returned one would be a second writer of it.
        FileWritten(request, written);
        // A command with recipients still waiting has written at least one recipient (the first is never
        // deferred), so it is a partial with a confirmed commit whose reason says the rest follow on later ticks.
        if (queued != 0) return new Outcome(Status.Partial, Commit.Confirmed, ResultCode.ModifierBudget);
        return rows.Conclude(doneWhenSucceeded: true);
    }

    /// <summary>What writing one recipient came to, for the queue: its row is the <see cref="Outcome"/> beside it.</summary>
    private enum Written { Committed, Rejected, Unknown, Deferred }

    /// <summary>One `attribute_apply` command's recipient set: what every recipient takes, and the recipients the
    /// per-tick write budget has not reached yet. The budget only spreads the writes over ticks; a recipient that
    /// waits is written later with the same members, cap and effect handle, never refused.</summary>
    private sealed class Request
    {
        internal (AgentModifier Modifier, double Submitted)[] Members = null!;
        internal EnemyAttributeValue[]? EnemyMembers;
        internal ForgeEnemy.Native.EnemyModule? EnemyReceiver;
        internal double? Cap;
        internal RuntimeHandle Key;
        internal bool Named;
        internal Group? Refresh;
        internal int TargetCount;
        internal readonly Queue<EntityReference> Waiting = new();
    }

    /// <summary>Files what a command wrote under the effect handle the kernel cast for its step: a stacking
    /// application or a recipient new to a running effect joins the group the handle already has.</summary>
    private void FileWritten(Request request, List<Applied> written)
    {
        if (!request.Named || written.Count == 0) return;
        if (!_groups.TryGetValue(request.Key, out var group)) _groups.Add(request.Key, group = new Group { Key = request.Key });
        foreach (var entry in written) group.Ids.Add(entry.Id);
    }

    /// <summary>Writes one recipient's whole member set, or reports that this tick's budget has no room for it
    /// (<see cref="Written.Deferred"/>, nothing written). <paramref name="mustWrite"/> spends the budget even when
    /// it is already used, so a command always makes progress.</summary>
    private Written WriteRecipient(Request req, EntityReference target, long tick, bool mustWrite,
        List<Applied> written, out Outcome row, out double reported)
    {
        reported = 0;
        static Outcome Row(Status status, Commit state, string code) => new(status, state, code);
        // Both game entries raise a null reference without the modifier manager, so a session whose manager does not
        // exist is refused before anything is submitted.
        if (_players() is not { CanCommit: true } players || !ForgeCore.AgentModifiers.Ready)
        { row = Row(Status.Rejected, Commit.None, ResultCode.AuthorityOrPhase); return Written.Rejected; }
        var members = req.Members;
        // Enemy identity, supported members, native readback and cleanup belong to the enemy receiver.
        // It uses this same action and kernel effect, never a player fallback or another scheduler.
        if (EntityKinds.Is(target, EntityKinds.Enemy))
        {
            if (req.EnemyReceiver == null)
            { row = Row(Status.Rejected, Commit.None, ResultCode.UnsupportedRecipient); return Written.Rejected; }
            if (!TryReserveWrites(tick, members.Length, mustWrite)) { row = default; return Written.Deferred; }
            req.EnemyMembers ??= members.Select(member => new EnemyAttributeValue((int)member.Modifier, (float)member.Submitted)).ToArray();
            EnemyAttributeReceipt receipt;
            try { receipt = req.EnemyReceiver.ApplyEnemyAttributes(target, req.EnemyMembers, req.Cap, req.Named ? req.Key : null); }
            catch (Exception error)
            {
                _report("player.attribute-apply-enemy-exception: " + error.GetType().Name);
                receipt = new(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException, 0);
            }
            reported = receipt.Amount;
            row = Row(receipt.Status, receipt.Commit, receipt.Code);
            return receipt.Commit == Commit.Confirmed ? Written.Committed
                : receipt.Commit == Commit.Unknown ? Written.Unknown : Written.Rejected;
        }
        // A map object or an unknown kind is refused before any native read.
        if (!EntityKinds.Is(target, PlayerKind))
        { row = Row(Status.Rejected, Commit.None, ResultCode.UnsupportedRecipient); return Written.Rejected; }
        // A refresh of a running effect (the kernel's `EffectAddLayer` is false) restarts the clock only: what
        // this recipient already holds under the handle stays as it is, and nothing new is written for it.
        if (req.Refresh != null && HeldUnder(req.Refresh, target) is { } held)
        {
            reported = held.Submitted;
            row = Row(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
            return Written.Committed;
        }
        var agent = players.CurrentAgent(target);
        if (agent == null)
        { row = Row(Status.Rejected, Commit.None, ResultCode.StaleEntity); return Written.Rejected; }
        var cap = req.Cap;
        // An earlier unknown contribution cannot be treated as zero to make a new capped write fit.
        // Refuse before any member is submitted; unrelated recipients and attributes remain independent.
        if (cap.HasValue && HoldsUnconfirmed(target, members))
        { row = Row(Status.Rejected, Commit.None, ResultCode.ReadbackUnconfirmed); return Written.Rejected; }
        if (!TryReserveWrites(tick, members.Length, mustWrite)) { row = default; return Written.Deferred; }

        var taken = _taken;
        taken.Clear();
        string? failure = null;
        bool recipientPartial = false;
        foreach (var (modifier, submitted) in members)
        {
            double contribution = submitted;
            if (cap.HasValue)
            {
                double current = Total(target, modifier);
                double bounded = cap.Value > 0
                    ? Math.Min(current + submitted, cap.Value)
                    : Math.Max(current + submitted, cap.Value);
                contribution = bounded - current;
            }
            // A clamp that leaves nothing to submit is a real no-op — the cap already holds — and the native
            // entry registers nothing for a zero contribution, so the member is refused by name rather than
            // written, and the members this recipient already took are revoked with it.
            if (contribution == 0)
            {
                failure = Revoke(taken, _revoke, "attribute-apply-cap-refused")
                    ? cap.HasValue ? ResultCode.CapReached : ResultCode.AmountOutOfRange : ResultCode.NativeCommitException;
                break;
            }
            ForgeCore.ModifierTable? previous;
            try { previous = ForgeCore.AgentModifiers.TableOf(agent); }
            catch (Exception error)
            {
                _report("player.attribute-apply-read-exception: " + error.GetType().Name);
                failure = Revoke(taken, _revoke, "attribute-apply-read-refused")
                    ? ResultCode.StaleEntity : ResultCode.NativeCommitException;
                break;
            }
            uint id;
            try { id = ForgeCore.AgentModifiers.Add(agent, modifier, (float)contribution); }
            catch (Exception error)
            {
                // The call entered the native modification path; whether it had already registered an id is not
                // observable from here, so the commit stays unknown, what this recipient already took stays in
                // the ledger and the handle's group.
                // The game's own stack is the only trace of which native member failed, so it travels with the type;
                // an IL2CPP stack names methods, never a player.
                var trace = (error.Message ?? "").Replace('\r', ' ').Replace('\n', '|');
                _report("player.attribute-apply-commit-exception: " + error.GetType().Name + ": "
                    + (trace.Length > 900 ? trace[..900] : trace));
                failure = ResultCode.NativeCommitException;
                break;
            }
            // A zero answer is the native call failing or registering nothing (the hook layer turns a native
            // exception into a default return, and a real counter cannot run out inside a session). It is
            // reported as a failed native commit, never as an exhausted id; there is no id to release and none
            // to invent, and the members this recipient already took are revoked.
            if (id == 0)
            {
                _report("player.attribute-apply-native-no-id: " + modifier);
                Revoke(taken, _revoke, "attribute-apply-member-refused");
                failure = ResultCode.NativeCommitException;
                break;
            }
            // File the returned id before reading it. A missing or throwing native read must still leave
            // the id available to the same cleanup path, without inventing an accepted contribution.
            var entry = new Applied
            {
                Id = id,
                Target = target,
                Modifier = modifier,
                Handle = req.Key,
                NativeInstance = previous
            };
            _entries.Add(id, entry);
            taken.Add(entry);
            try
            {
                var instance = ForgeCore.AgentModifiers.TableOf(agent)
                    ?? throw new InvalidOperationException("Native modifier instance missing.");
                entry.NativeInstance ??= instance;
                // The life the write was made on is no longer the one the reference names: whether the member landed
                // on it is not observable from here.
                if (players.CurrentAgent(target)?.Pointer != agent.Pointer)
                { failure = ResultCode.ReceiverChangedDuringCommit; break; }
                if (instance.Pointer != entry.NativeInstance.Pointer
                    || !ForgeCore.AgentModifiers.TryRead(agent, id, modifier, out var value)
                    || !float.IsFinite(value.Original) || !float.IsFinite(value.Cached)
                    || !float.IsFinite(value.TimeStamp) || value.Original != value.Cached)
                    throw new InvalidOperationException("Native modifier contribution not confirmed.");
                entry.Submitted = value.Cached;
                entry.ContributionConfirmed = true;
                Track(target, modifier, entry.Submitted);
                if (taken.Count == 1) reported = entry.Submitted;
                recipientPartial |= value.Cached != (float)contribution;
            }
            catch (Exception error)
            {
                _report("player.attribute-apply-readback-exception: " + error.GetType().Name);
                failure = ResultCode.NativeCommitException;
                break;
            }
        }
        if (failure == null)
        {
            written.AddRange(taken);
            row = Row(recipientPartial ? Status.Partial : Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
            return Written.Committed;
        }
        if (failure is not (ResultCode.NativeCommitException or ResultCode.ReceiverChangedDuringCommit))
        {
            // A clean refusal: nothing of the set was left on this recipient.
            reported = 0;
            row = Row(Status.Rejected, Commit.None, failure);
            return Written.Rejected;
        }
        // The native entry threw, or the recipient changed under the write: whether that member registered is not
        // observable, so the commit is unknown.
        // What did land stays filed under the handle, so a cancel, the effect's end, the life's end or the
        // world's still releases it.
        written.AddRange(taken);
        row = Row(Status.Failed, Commit.Unknown, failure);
        return Written.Unknown;
    }

    /// <summary>What a running group already holds on one recipient: its first filed entry for that target.</summary>
    private Applied? HeldUnder(Group group, EntityReference target)
    {
        foreach (var id in group.Ids)
            if (_entries.TryGetValue(id, out var entry) && entry.Target == target) return entry;
        return null;
    }

    /// <summary>Whether the recipient holds an unconfirmed contribution of any member this command would write.</summary>
    private bool HoldsUnconfirmed(EntityReference target, (AgentModifier Modifier, double Submitted)[] members)
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.ContributionConfirmed || entry.Target != target) continue;
            foreach (var member in members)
                if (member.Modifier == entry.Modifier) return true;
        }
        return false;
    }

    /// <summary>The members of the row's `modifiers` group, in the group's own order, with `subtract` already
    /// applied as the sign the native table reads as its opposite. The kernel expanded the group into
    /// `attribute_i`/`amount_i` for i = 1..n and validated every port it expanded, so the members are exactly the
    /// consecutive indices present; an enum port arrives as its member name.</summary>
    private static bool TryReadMembers(CommandContext context, bool negate,
        out (AgentModifier Modifier, double Submitted)[] members, out string code)
    {
        members = Array.Empty<(AgentModifier, double)>();
        var count = context.Count(AgentModifierContract.ApplyAttributeSlot);
        if (count <= 0) { code = ResultCode.AttributeUnknown; return false; }
        var read = new (AgentModifier Modifier, double Submitted)[count];
        for (var index = 0; index < count; index++)
        {
            var attribute = context.Text(AgentModifierContract.ApplyAttributeSlot, index);
            if (!AgentModifierValues.TryParse(attribute, out var modifier)) { code = ResultCode.AttributeUnknown; return false; }
            // `None` is a member of the native enum but not an attribute anything can carry: applying it would
            // register an id that contributes nothing, which is why the ruled shape drops it from the shared set.
            if (modifier == AgentModifier.None) { code = ResultCode.AttributeNoOp; return false; }
            double amount = context.Number(AgentModifierContract.ApplyAmountSlot, index) ?? double.NaN;
            if (!double.IsFinite(amount) || amount == 0 || Math.Abs(amount) > MaximumAmount) { code = ResultCode.AmountOutOfRange; return false; }
            read[index] = (modifier, negate ? -amount : amount);
        }
        members = read;
        code = "";
        return true;
    }

    /// <summary>Revokes what one recipient already took of a member set it cannot complete, through the same native
    /// clear every release uses. An id whose clear throws stays in the ledger for the world-end pass.</summary>
    private bool Revoke(List<Applied> taken, List<uint> scratch, string reason)
    {
        if (taken.Count == 0) return true;
        scratch.Clear();
        foreach (var entry in taken) scratch.Add(entry.Id);
        Clear(scratch, reason);
        taken.RemoveAll(entry => !_entries.ContainsKey(entry.Id));
        return taken.Count == 0;
    }

    /// <summary>Consume the shared cancellation contract; this provider's ledger only checks whether its own
    /// native restoration failed after the kernel invoked the producing provider.</summary>
    internal CommandResult Cancel(CommandContext context)
    {
        CheckThread();
        return EffectCancelRuntime.Execute(_registration, context, _canCommit ??= () => _players() is { CanCommit: true },
            _holdsGroup ??= HoldsGroup);
    }

    // Made on the first cancel and kept, so a cancel allocates no delegate.
    private Func<bool>? _canCommit;
    private Func<RuntimeHandle, bool>? _holdsGroup;

}
