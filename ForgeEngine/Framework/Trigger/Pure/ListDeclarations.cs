using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Targeting;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The list family of the pure tier: the row that reads one item out of a list a step already carries. It
/// never walks the world — the list arrives on the port — so it stays a `pure` row. The list is text, integers,
/// numbers, switches or chosen enemy, weapon and tool types, the class its structural `value_type` parameter chose.
/// A resource list is a collection of references the frame carries; the row passes one on and never resolves it.
///
/// The `clamp` members are the whole out-of-range policy and none of them answers an empty value. `clamp` takes the
/// first member for an index below the list and the last for an index past the end; `wrap` counts from the other end
/// for a negative index and modulo for an index past the end, so the row is a rotation read; `refuse` fails the step
/// by name for either side. An index inside the list is the same member under all three. A list with no members has
/// no item to take, so it is refused whatever the policy.</summary>
internal static class ListDeclarations
{
    /// <summary>The row's own members, in the catalog's order: the compiled value is the member's index into this
    /// list, so the order is the wire and the handler reads the member by position.</summary>
    internal static readonly string[] ValueTypes = { "string", "integer", "number", "boolean", "resource:enemy", "resource:weapon", "resource:tool", "vector3" };

    internal static IReadOnlyList<ObservedNode> Nodes { get; } = new[]
    {
        PureModule.Row(Blocks.ModifierListElementAt.Id,
            new HandlerShape().Inputs("items", "index").Outputs("value").Parameters("value_type", "clamp"), ElementAt)
    };

    /// <summary>The position of one item of a list of <paramref name="count"/> members under the policy that index was
    /// written with. An index the policy refuses, or any index into an empty list, is a named contract failure.</summary>
    internal static int Position(int count, long index, string clamp)
    {
        if (count == 0) throw new RuntimeContractException("pure-operation", "An empty list has no item to take.");
        switch (clamp)
        {
            case "wrap":
                var wrapped = index % count;
                return (int)(wrapped < 0 ? wrapped + count : wrapped);
            case "clamp":
                return (int)Math.Clamp(index, 0, count - 1);
            case "refuse":
                if (index < 0 || index >= count)
                    throw new RuntimeContractException("pure-operation", "The index " + index + " is outside a list of " + count + ".");
                return (int)index;
            default:
                throw new RuntimeContractException("pure-operation", "Unknown clamp member: " + clamp);
        }
    }

    private static void ElementAt(EvaluationContext context, EvaluationOutputs outputs)
    {
        var valueType = context.Parameters.TryGetProperty("value_type", out var member) && member.ValueKind == JsonValueKind.String
            ? member.GetString()! : ValueTypes[0];
        if (Array.IndexOf(ValueTypes, valueType) < 0)
            throw new RuntimeContractException("pure-operation", "Unknown value type member: " + valueType);
        var items = context.Inputs.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array ? list : default;
        var count = items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
        var chosen = items[Position(count, Index(context.Inputs.GetProperty("index")), context.Parameters.GetProperty("clamp").GetString()!)];
        if (!(Matches(chosen, valueType))) throw new RuntimeContractException("pure-operation", "A list of " + valueType + " requires " + valueType + " members.");
        outputs.Json(0, chosen);
    }

    internal static bool Matches(JsonElement item, string valueType) => valueType switch
    {
        _ when valueType.StartsWith("resource:", StringComparison.Ordinal) => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("resourceKind", out var kind) && kind.ValueEquals(valueType.AsSpan("resource:".Length)),
        "string" => item.ValueKind == JsonValueKind.String,
        "boolean" => item.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "vector3" => item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == 3
            && item.EnumerateArray().All(axis => axis.ValueKind == JsonValueKind.Number),
        "integer" => item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var whole) && whole == Math.Truncate(whole),
        _ => item.ValueKind == JsonValueKind.Number
    };

    /// <summary>The index the number port carries, rounded down: a computed position such as a tier read from a
    /// number record selects the item it has reached. Another rounding is the author's own Calculate step.</summary>
    private static long Index(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            && number >= long.MinValue && number <= long.MaxValue
            ? (long)Math.Floor(number)
            : throw new RuntimeContractException("pure-operation", "An index requires a finite number.");
}
