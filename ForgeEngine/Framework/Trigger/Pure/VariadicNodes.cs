using System;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The two ordered 1..32-input cards: the calculation and the logic card. Each takes the whole input list
/// in the order the author wired it and one operation member; no graph executor. One input is a card the contract's
/// `input_count` allows for every operation: a fold of one value is that value (add, subtract, multiply, divide,
/// minimum, maximum, all, any), exactly as the website preview's reduce answers, and the one-input operations
/// (`absolute`, `round`, `not`) read only the first input anyway. Set union and intersection are binary contracts and
/// live in <see cref="ReferenceCollections"/>.</summary>
internal static class VariadicNodes
{
    public const int MinimumInputs = 1;
    public const int MaximumInputs = 32;

    /// <summary>
    /// The calculation card. Add, multiply, minimum and maximum fold every input; subtract and divide start from
    /// the first input and apply each later one in turn; absolute and round read only the first input and take one
    /// input (more are accepted and ignored for a document that wired them). A zero
    /// divisor follows <paramref name="zeroPolicy"/>: `reject` refuses, `zero` makes the whole result 0, and
    /// `passthrough` skips that divisor. Round follows <paramref name="rounding"/>.
    /// </summary>
    public static double Calculate(ScalarOperation operation, ReadOnlySpan<double> values,
        DivisionZeroPolicy zeroPolicy = DivisionZeroPolicy.Reject, ScalarRounding rounding = ScalarRounding.Floor)
    {
        Count(values.Length);
        foreach (var value in values) PureNumbers.Input(value);
        switch (operation)
        {
            case ScalarOperation.Absolute: return ScalarNodes.Absolute(values[0]);
            case ScalarOperation.Round: return ScalarNodes.Round(values[0], rounding);
            case ScalarOperation.Divide:
                {
                    var quotient = values[0];
                    for (var index = 1; index < values.Length; index++)
                        quotient = ScalarNodes.Divide(quotient, values[index], zeroPolicy);
                    return quotient;
                }
        }
        var result = values[0];
        for (var index = 1; index < values.Length; index++)
            result = operation switch
            {
                ScalarOperation.Add => result + values[index],
                ScalarOperation.Subtract => result - values[index],
                ScalarOperation.Multiply => result * values[index],
                ScalarOperation.Minimum => Math.Min(result, values[index]),
                ScalarOperation.Maximum => Math.Max(result, values[index]),
                _ => throw new RuntimeContractException("pure-operation", "Unknown calculation operation.")
            };
        return PureNumbers.Result(result);
    }

    /// <summary>The logic card: `all` is true when every input is, `any` when one is, and `not` answers the
    /// opposite of the first input alone.</summary>
    public static bool Logic(LogicOperation operation, ReadOnlySpan<bool> values)
    {
        Count(values.Length);
        switch (operation)
        {
            case LogicOperation.All:
                foreach (var value in values) if (!value) return false;
                return true;
            case LogicOperation.Any:
                foreach (var value in values) if (value) return true;
                return false;
            case LogicOperation.Not:
                return !values[0];
            default:
                throw new RuntimeContractException("pure-operation", "Unknown logic operation.");
        }
    }

    private static void Count(int count)
    {
        if (count < MinimumInputs || count > MaximumInputs)
            throw new RuntimeContractException("pure-variadic-count", "Variadic computations require 1..32 ordered inputs.");
    }
}
