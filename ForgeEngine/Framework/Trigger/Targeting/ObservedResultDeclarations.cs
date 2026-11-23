using System;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>One shared action-result reader. The producer's validated row set arrives through the kernel's
/// current activation; this evaluator neither looks up receipts nor changes the producer's target or outcome.</summary>
internal static class ObservedResultDeclarations
{
    public const string CapabilityId = Blocks.QueryResultRead.Id;
    public const string BindingId = "forge.module.trigger.binding.result_read";
    public const string HandlerName = "trigger.state.result_read";
    private static readonly string[] Statuses = RuntimeEnumSets.Members("execution_outcome").ToArray();

    public static ObservedFamily Family { get; } = ObservedFamily.Declare(new ObservedNode(
        CapabilityId, "observe", BindingId, HandlerName,
        new HandlerShape().Inputs("result", "row_index").Outputs("count", "status", "committed", "code").Parameters("mode"),
        Read));

    internal static void Read(EvaluationContext context, EvaluationOutputs outputs)
    {
        if (context.TryInputCell("result", out var cell) && cell.Reference is RuntimeKernel.ResultRowSet set)
        { ReadRegion(context, outputs, set); return; }
        if (!context.Inputs.TryGetProperty("result", out var rows))
            throw new RuntimeContractException("from-step-value", context.NodeId + ".result");
        var count = rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength()
            : rows.ValueKind == JsonValueKind.Object ? 1 : -1;
        if (count < 0) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowIncomplete, context.NodeId + ".result");
        if (!context.Parameters.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("missing-constant", context.NodeId + ".mode");
        // Count mode has no row_index input or row-value outputs in its resolved contract. Counting never
        // selects a row, including where the producer wrote a legitimate empty set.
        if (mode.GetString() == "count") { outputs.Integer(0, count); return; }
        if (mode.GetString() != "row") throw new RuntimeContractException("invalid-enum", context.NodeId + ".mode");
        if (!context.Inputs.TryGetProperty("row_index", out var selected) || selected.ValueKind != JsonValueKind.Number
            || !selected.TryGetInt64(out var index))
            throw new RuntimeContractException("missing-input", context.NodeId + ".row_index");
        if (index < 0 || index >= count)
            throw new RuntimeContractException(RuntimeAbiCodes.ResultIndexOutOfRange, context.NodeId + ".row_index");
        var row = rows.ValueKind == JsonValueKind.Array ? rows[(int)index] : rows;
        var status = Array.IndexOf(Statuses, row.GetProperty("status").GetString());
        if (status < 0) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, context.NodeId + ".status");
        bool? committed = row.GetProperty("committed").GetString() switch
        {
            CommitStates.None => false,
            CommitStates.Confirmed => true,
            CommitStates.Unknown => null,
            _ => throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, context.NodeId + ".committed")
        };
        outputs.Integer(0, count);
        outputs.Enum(1, status);
        if (committed is { } known) outputs.Boolean(2, known); else outputs.Null(2);
        outputs.Text(3, row.GetProperty("code").GetString()!);
    }

    /// <summary>The same read taken from the rows where the producer wrote them: the count is the set's own and the
    /// three fixed columns are read by position, so no document of the rows is written for it.</summary>
    private static void ReadRegion(EvaluationContext context, EvaluationOutputs outputs, RuntimeKernel.ResultRowSet set)
    {
        if (!context.Parameters.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("missing-constant", context.NodeId + ".mode");
        if (mode.ValueEquals("count")) { outputs.Integer(0, set.Count); return; }
        if (!mode.ValueEquals("row")) throw new RuntimeContractException("invalid-enum", context.NodeId + ".mode");
        if (!context.TryInputCell("row_index", out var selected) || !selected.Valid || selected.Count >= 0 || selected.Kind != ValueKind.Integer)
            throw new RuntimeContractException("missing-input", context.NodeId + ".row_index");
        var index = selected.Integer;
        if (index < 0 || index >= set.Count)
            throw new RuntimeContractException(RuntimeAbiCodes.ResultIndexOutOfRange, context.NodeId + ".row_index");
        var row = set.Row;
        var frame = new Frame(set.Space.Slots, row.Slot, set.Space.Strings.Memory);
        var slot = row.RowSlot((int)index);
        var status = Array.IndexOf(Statuses, row.Columns[1].Members![frame.EnumIndex(slot + row.Columns[1].Offset)]);
        if (status < 0) throw new RuntimeContractException(RuntimeAbiCodes.ResultRowStatus, context.NodeId + ".status");
        var commit = row.Columns[2].Members![frame.EnumIndex(slot + row.Columns[2].Offset)];
        outputs.Integer(0, set.Count);
        outputs.Enum(1, status);
        if (commit == CommitStates.Unknown) outputs.Null(2); else outputs.Boolean(2, commit == CommitStates.Confirmed);
        outputs.Text(3, frame.String(slot + row.Columns[3].Offset));
    }
}
