using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>One resource a step declares as its cost: the kind, the compile-time instance and the amount. The
/// reference is a constant the plan compiled — never a value another node computes — so the same three words are
/// what <see cref="RuntimeResourceProvider"/>'s owner resolves and reserves.</summary>
internal sealed record CostItem(string ResourceKind, string ResourceId, long Amount);

/// <summary>One cost owner failure, with the item whose release or commit could not be confirmed.</summary>
public sealed record RuntimeCostFault(string ResourceKind, string ResourceId, long Amount, string Code, string Detail);

/// <summary>
/// One action step's own cost group (plan §3.2 I-PLAN 结果行, §4.3 事务与资源): `cost_policy` is `none` or
/// `require`, and `cost` is the list of resources the step pays, in declaration order. A card that names a cost
/// list without a policy is refused, because the runtime does not guess whether the card pays; a card that names
/// `none` and a list is refused for the same reason.
///
/// There is no reservation handle here and no transaction: the kernel reserves each item through the provider that
/// owns its kind, commits the whole declaration once the step's command committed, and releases everything it held
/// when the step wrote nothing. Compensation for a committed write is the author's own reverse action, never an
/// implicit rollback.
/// </summary>
internal sealed record CostOptions(bool Required, IReadOnlyList<CostItem> Items)
{
    /// <summary>The two step fields the group is written as, and the ceiling on one declaration.</summary>
    internal static readonly string[] Fields = { "cost_policy", "cost" };
    internal const int MaximumItems = 16;

    /// <summary>
    /// The step's own cost group, or null where the card declares none. `cost_policy` alone is how a card says it
    /// pays nothing; `require` without a list, a list without a policy, and an amount below one are all refusals
    /// that name the field they are about, because a cost that cannot be charged is not a cost.
    /// </summary>
    internal static CostOptions? Parse(JsonElement step, string nodeId)
    {
        var hasPolicy = step.TryGetProperty("cost_policy", out var policyValue) && policyValue.ValueKind != JsonValueKind.Null;
        var hasCost = step.TryGetProperty("cost", out var costValue) && costValue.ValueKind != JsonValueKind.Null;
        if (!hasPolicy && !hasCost) return null;
        var policy = "";
        if (hasPolicy)
        {
            RuntimeJson.Require(policyValue.ValueKind == JsonValueKind.String, "cost-policy", "cost_policy");
            policy = policyValue.GetString()!;
            RuntimeJson.Require(policy is "none" or "require", "cost-policy",
                "cost_policy must be `none` or `require`.");
        }
        RuntimeJson.Require(hasPolicy, "cost-policy",
            "cost needs cost_policy: the runtime does not guess whether the card pays.");
        if (!hasCost)
        {
            RuntimeJson.Require(policy == "none", "cost-policy", "cost_policy `require` needs a cost list.");
            return null;
        }
        RuntimeJson.Require(policy == "require", "cost-policy",
            "a cost list belongs to cost_policy `require`: `none` spends nothing.");
        RuntimeJson.Require(costValue.ValueKind == JsonValueKind.Array, "cost-shape", "cost must be an array.");
        var rows = costValue.EnumerateArray().ToArray();
        if (!(rows.Length is >= 1 and <= MaximumItems)) throw new RuntimeContractException("cost-budget", "cost carries between one and " + MaximumItems + " items.");
        var items = new List<CostItem>(rows.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            RuntimeJson.Shape(row, "resource amount");
            var resource = row.GetProperty("resource");
            // The reference is the compiled one plus the kind no port could carry: a cost item is not wired to
            // anything, so the kind it charges is part of the item itself.
            RuntimeJson.Shape(resource, "resourceKind id revision");
            var kind = RuntimeJson.Text(resource, "resourceKind");
            RuntimeJson.Require(Wire.ResourceKinds.Contains(kind), RuntimeAbiCodes.ResourceKind, kind);
            var id = RuntimeJson.Text(resource, "id");
            RuntimeJson.Text(resource, "revision");
            if (!(seen.Add(kind + "\0" + id))) throw new RuntimeContractException("cost-duplicate", "cost names one resource once: " + kind + " " + id + ".");
            if (!(row.TryGetProperty("amount", out var amountValue)
                && amountValue.ValueKind == JsonValueKind.Number)) throw new RuntimeContractException("cost-amount", nodeId + "." + id);
            var amount = amountValue.GetDouble();
            if (!(double.IsFinite(amount) && amount == Math.Truncate(amount)
                && amount is >= 1 and <= RuntimeJson.MaxSafeInteger)) throw new RuntimeContractException("cost-amount", nodeId + "." + id + " must cost a whole number of at least one.");
            items.Add(new CostItem(kind, id, (long)amount));
        }
        return new CostOptions(true, items);
    }
}

internal sealed partial class KernelExecutor
{
    /// <summary>
    /// One step's reservations, held between the moment the step is charged and the moment its command says what
    /// it wrote. <see cref="Reserved"/> is false when one item could not be charged: the kernel has attempted to give
    /// back everything it held before it. A confirmed refund leaves `insufficient-cost`; an uncertain refund
    /// reports `cost-release-failed` without running the action.
    /// </summary>
    internal sealed class CostHold
    {
        internal bool Reserved;
        /// <summary>What a release could not confirm: its owner threw before or after changing its balance.
        /// The step reports this amount as neither consumed nor confirmed released.</summary>
        internal long Unreleased;
        internal long Released;
        internal readonly List<(RuntimeResourceCost Owner, string Kind, string Id, long Amount)> Held = new();
    }

    /// <summary>How many cost release or commit calls failed, and the last one. Other holdings are still settled;
    /// the author layer can read the failure code and item rather than assume every amount was confirmed.</summary>
    internal long CostFaultCount { get; private set; }
    internal RuntimeCostFault? LastCostFault { get; private set; }

    /// <summary>
    /// Charges one step's cost before anything of the step runs, in the declaration order the card wrote: the
    /// provider that owns each item's kind answers whether it can hold that much, and the first item it cannot is
    /// the step's `insufficient-cost` — which writes no state and leaves no reservation behind. A step that
    /// declares no cost answers null, which is the whole price of the feature for a card that does not pay.
    /// </summary>
    internal CostHold? ReserveCost(ResolvedStep step)
    {
        if (step.Cost is not { Required: true } cost) return null;
        var hold = new CostHold { Reserved = true };
        foreach (var item in cost.Items)
        {
            if (!registry.Tables.ResourceProviders.TryGetValue(item.ResourceKind, out var registered) || registered.Provider.Cost == null)
            {
                // The plan loader refuses a cost no owner can charge, so this is reachable only for a provider that
                // unregistered since: the step is refused rather than run for free.
                hold.Reserved = false; break;
            }
            var owner = registered.Provider.Cost;
            bool held;
            try { held = owner.Reserve(item.ResourceId, item.Amount); }
            catch (Exception error) { NativeFault.Note("runtime.cost.reserve", error); held = false; }
            if (!held) { hold.Reserved = false; break; }
            hold.Held.Add((owner, item.ResourceKind, item.ResourceId, item.Amount));
        }
        if (!hold.Reserved) ReleaseCost(hold);
        return hold;
    }

    /// <summary>The settlement of a reservation a step still held when it threw: the failure result decides, so a step
    /// that wrote nothing gives everything back and one that may have written is charged, exactly as a step that
    /// returned the same result would be. A hold already settled or handed on is null and changes nothing.</summary>
    internal CommandResult SettleOpenHold(CommandResult result, ref CostHold? hold)
    {
        var open = hold; hold = null;
        return open == null ? result : SettleCost(result, open, null);
    }

    /// <summary>
    /// Settles one step's charge against what its command wrote: a command that wrote nothing — a refusal, a
    /// cancellation, an expiry — gives every reservation back. A commit that throws is unknown: it is not retried,
    /// other items are still attempted, and consumed counts only calls that returned normally. The row's own
    /// conclusion becomes failed/unknown so a reader cannot mistake that amount for the full declared charge.
    /// </summary>
    internal CommandResult SettleCost(CommandResult result, CostHold? hold, ResultRowSet? rows)
    {
        if (hold == null) return result;
        var consumed = 0L; var released = 0L; var unknown = 0L;
        if (result.CommitState == CommitStates.None)
        {
            ReleaseCost(hold);
            released = hold.Released;
            unknown = hold.Unreleased;
            if (unknown != 0) result = CostSettlementFailure(result, RuntimeLogReasonCodes.CostReleaseFailed, unknown, released);
        }
        else
        {
            foreach (var (owner, kind, id, amount) in hold.Held)
            {
                try { owner.Commit(id, amount); consumed += amount; }
                catch (Exception error)
                {
                    unknown += amount;
                    if (CostFaultCount < long.MaxValue) CostFaultCount++;
                    LastCostFault = new RuntimeCostFault(kind, id, amount, RuntimeLogReasonCodes.CostCommitFailed,
                        CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message));
                }
            }
            if (unknown != 0)
                result = CostSettlementFailure(result, RuntimeLogReasonCodes.CostCommitFailed, unknown, released);
        }
        hold.Held.Clear();
        // The typed rows are the step's result: the two amounts are written into their own declared columns and the
        // receipt is rendered from them, so the rows a reader sees and the rows the charge wrote are one value. A
        // step that wrote no rows has no row to report the amounts on, and the charge's own settlement is the whole
        // record of what it paid — there is no second, untyped place a row could be written.
        if (rows is not { } written) return result;
        var writer = new FrameWriter(resultArena.Slots, written.Row.Slot, written.Row.Span, resultArena.Strings);
        for (var index = 0; index < written.Count; index++)
        {
            ResultRowAbi.SetCost(ref writer, written.Row, index, consumed, released);
            if (unknown != 0) ResultRowAbi.SetCostFailure(ref writer, written.Row, index, result.Code);
        }
        return WithRows(result, written, settled: true);
    }

    // Only contract words and confirmed quantities enter the log. Never copy a handler/owner exception's
    // arbitrary message into this diagnostic. The action's known outcome survives the settlement failure.
    private static readonly HashSet<string> CostDiagnosticCodes = new[] { typeof(RuntimeAbiCodes), typeof(RuntimeLogReasonCodes) }
        .SelectMany(type => type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);

    private static CommandResult CostSettlementFailure(CommandResult action, string code, long unknown, long released)
    {
        var detail = CommandResult.TruncateDetail("Action " + action.Status + "/" + action.CommitState + "/"
            + (CostDiagnosticCodes.Contains(action.Code) ? action.Code : "provider-code-withheld")
            + "; cost unknown=" + unknown + "; confirmed released=" + released + ".");
        var result = CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, code,
            detail, action.Outputs, action.Facts.ToArray());
        result.CostSettlementLogDetail = detail;
        return result;
    }

    /// <summary>Attempts every refund once. A throwing owner leaves its amount unknown; other items still run, and
    /// the failure is counted as `cost-release-failed` rather than lost.</summary>
    internal void ReleaseCost(CostHold hold)
    {
        foreach (var (owner, kind, id, amount) in hold.Held)
        {
            try { owner.Release(id, amount); hold.Released += amount; }
            catch (Exception error)
            {
                hold.Unreleased += amount;
                if (CostFaultCount < long.MaxValue) CostFaultCount++;
                LastCostFault = new RuntimeCostFault(kind, id, amount, RuntimeLogReasonCodes.CostReleaseFailed,
                    CommandResult.TruncateDetail(error.GetType().Name + ": " + error.Message));
            }
        }
        hold.Held.Clear();
    }
}
