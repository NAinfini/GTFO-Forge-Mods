using System;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The calculation card's `operation` members, in the catalog's own order (the compiled value is the index).
/// </summary>
internal enum ScalarOperation { Add, Subtract, Multiply, Divide, Minimum, Maximum, Absolute, Round }
/// <summary>The logic card's `operation` members, in the catalog's own order.</summary>
internal enum LogicOperation { All, Any, Not }
internal enum ScalarComparison { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }
internal enum ScalarRounding { Floor, Ceiling, Nearest, Truncate }
/// <summary>The website divide node's structural zero_policy.</summary>
internal enum DivisionZeroPolicy { Reject, Zero, Passthrough }
/// <summary>What a range row's input value is measured in: the raw quantity itself, an offset already taken from
/// the window's own minimum, or the position in the window the author computed.</summary>
internal enum RangeInputUnit { Absolute, Relative, Normalized }
/// <summary>What a range row does with an input outside its window: saturate, extrapolate, or refuse.</summary>
internal enum RangeBounds { Clamp, Allow, Reject }

/// <summary>Stateless, dimensionless arithmetic. Graph units, domains and bindings belong to the public Runtime boundary.</summary>
internal static class ScalarNodes
{
    public static double Constant(double value) => PureNumbers.Result(PureNumbers.Input(value));

    public static double Divide(double a, double b, DivisionZeroPolicy policy)
    {
        PureNumbers.Input(a); PureNumbers.Input(b);
        if (b != 0d) return PureNumbers.Result(a / b);
        return policy switch
        {
            DivisionZeroPolicy.Reject => throw new RuntimeContractException("pure-division-by-zero", "The divisor must not be zero."),
            DivisionZeroPolicy.Zero => 0d,
            // The dividend passes through unchanged.
            DivisionZeroPolicy.Passthrough => PureNumbers.Result(a),
            _ => throw new RuntimeContractException("pure-operation", "Unknown division zero policy.")
        };
    }

    public static double Clamp(double value, double minimum, double maximum)
    {
        PureNumbers.Input(value); PureNumbers.Bounds(minimum, maximum);
        return PureNumbers.Result(Math.Max(minimum, Math.Min(maximum, value)));
    }

    /// <summary>
    /// The one range mapping: where the input sits in its own window, curved, optionally flipped, placed in the
    /// output window. The three input units are the three ways an author can have measured the value: the raw
    /// quantity, the amount it stands above the window's minimum, or the position in the window itself (0..1).
    ///
    /// `clamp` saturates the input to the window, `reject` refuses an input outside it, and `allow` carries the
    /// part outside the window linearly on top of the curve, so the same row extrapolates where a card asks it to.
    /// The curve is taken on the part inside the window in every mode: a fractional power of a negative position
    /// has no real answer, and refusing one would make `allow` unusable rather than permissive.
    ///
    /// The two thresholds are what the author counts as "nothing yet" and "already full": a value below the floor
    /// and a value at the ceiling both read as the threshold they passed, so a window wider than the part that
    /// matters still maps the part that matters across the whole output. A pair that is not a window — a floor at
    /// or above its ceiling — saturates nothing.
    /// </summary>
    public static double MapRange(double value, double inputMinimum, double inputMaximum, double inputFloor,
        double inputCeiling, double outputMinimum, double outputMaximum, RangeInputUnit unit, double exponent,
        bool flip, RangeBounds bounds)
    {
        PureNumbers.Input(value); PureNumbers.Input(exponent); PureNumbers.Bounds(inputMinimum, inputMaximum);
        PureNumbers.Bounds(outputMinimum, outputMaximum);
        PureNumbers.Input(inputFloor); PureNumbers.Input(inputCeiling);
        if (inputCeiling > inputFloor) value = Math.Max(inputFloor, Math.Min(inputCeiling, value));
        var span = inputMaximum - inputMinimum;
        if (span <= 0d)
            throw new RuntimeContractException("pure-reversed-range", "A range mapping needs a window wider than nothing.");
        var position = unit switch
        {
            RangeInputUnit.Absolute => (value - inputMinimum) / span,
            RangeInputUnit.Relative => value / span,
            RangeInputUnit.Normalized => value,
            _ => throw new RuntimeContractException("pure-operation", "Unknown range input unit.")
        };
        if (bounds == RangeBounds.Reject && (position < 0d || position > 1d))
            throw new RuntimeContractException("pure-range", "The input is outside the window this row maps.");
        var inside = Math.Max(0d, Math.Min(1d, position));
        var curve = exponent == 1d ? inside : Math.Pow(inside, exponent);
        var weight = curve + (bounds == RangeBounds.Allow ? position - inside : 0d);
        if (flip) weight = 1d - weight;
        return PureNumbers.Result(outputMinimum + (outputMaximum - outputMinimum) * weight);
    }

    public static double Absolute(double value) => PureNumbers.Result(Math.Abs(PureNumbers.Input(value)));

    public static double Round(double value, ScalarRounding mode)
    {
        PureNumbers.Input(value);
        // Website Math.round ties towards +infinity, unlike .NET's default ties-to-even.
        // Do not use Floor(value + 0.5): addition can round large integral inputs up.
        var lower = Math.Floor(value);
        var rounded = mode switch
        {
            ScalarRounding.Floor => lower,
            ScalarRounding.Ceiling => Math.Ceiling(value),
            ScalarRounding.Nearest => value - lower < 0.5d ? lower : lower + 1d,
            ScalarRounding.Truncate => Math.Truncate(value),
            _ => throw new RuntimeContractException("pure-operation", "Unknown rounding mode.")
        };
        if (rounded < -RuntimeJson.MaxSafeInteger || rounded > RuntimeJson.MaxSafeInteger)
            throw new RuntimeContractException("invalid-integer", "Rounded result must be a safe integer.");
        return PureNumbers.Result(rounded);
    }
}

/// <summary>Pure predicates; never reads an entity, infers a missing actor or mutates state.</summary>
internal static class PureConditions
{
    /// <summary>The value classes the catalog's parameterized comparison orders, in the parameter's own member
    /// order (`value_type` in `Tools/Forge/capability-rules-query.ts`, whose inline list this repeats because the
    /// compiled constant indexes it).</summary>
    public static readonly string[] ValueTypes = { "number", "integer", "string", "entity", "enum", "boolean" };

    /// <summary>The catalog's comparison member names, in the order the C# enum declares them, which is the order
    /// the `compare_operator` set carries.</summary>
    public static readonly string[] Operators = { "eq", "ne", "lt", "lte", "gt", "gte" };

    /// <summary>Website compare: tolerance widens equality and shifts the ordered comparisons towards acceptance.</summary>
    public static bool Compare(double left, double right, ScalarComparison operation, double tolerance)
    {
        PureNumbers.Input(left); PureNumbers.Input(right); PureNumbers.Input(tolerance);
        if (tolerance < 0d)
            throw new RuntimeContractException("pure-tolerance-range", "Comparison tolerance must not be negative.");
        return operation switch
        {
            ScalarComparison.Equal => Math.Abs(left - right) <= tolerance,
            ScalarComparison.NotEqual => Math.Abs(left - right) > tolerance,
            ScalarComparison.Less => left < right - tolerance,
            ScalarComparison.LessOrEqual => left <= right + tolerance,
            ScalarComparison.Greater => left > right + tolerance,
            ScalarComparison.GreaterOrEqual => left >= right - tolerance,
            _ => throw new RuntimeContractException("pure-operation", "Unknown comparison operation.")
        };
    }

    /// <summary>The typed comparison the catalog's `forge.condition.predicate.compare` row performs: one row for
    /// the five classes its structural `value_type` parameter offers, so `g-compare` is no longer one row per
    /// type. An option value (`enum`) is a member of the one set the row's `enum_set` parameter chose; the frame has
    /// already resolved both compiled indices back to member names, and a set has no order, so only `eq` and `ne`
    /// are answered and every ordered operator is refused. `valueType` is the member the plan compiled, `<c>null</c>` when the author left the optional
    /// parameter unwritten, which resolves to the first member exactly as the contract does.
    ///
    /// The numbers use <see cref="Compare(double,double,ScalarComparison,double)"/>, so tolerance keeps its one
    /// definition. Text compares by ordinal rank, which is what makes `eq` byte equality and the ordering a total
    /// order; entities compare by identity (id, world epoch, life epoch), which is the identity every other row of
    /// this vocabulary reads them by. A tolerance a non-numeric member cannot honour, a value of another class, and
    /// an unknown operator or member are all refused rather than answered as `false`, because a comparison that
    /// could not be made must not read like a world in which the answer is no.</summary>
    public static bool Compare(JsonElement left, JsonElement right, ScalarComparison operation, double? tolerance,
        string? valueType)
    {
        var member = valueType ?? ValueTypes[0];
        if (!ValueTypes.Contains(member, StringComparer.Ordinal))
            throw new RuntimeContractException("pure-operation", "Unknown value type member: " + member);
        if (member == "entity")
        {
            RequireNoTolerance(tolerance);
            var first = RuntimeJson.Entity(left); var second = RuntimeJson.Entity(right);
            var order = string.Equals(first.Id, second.Id, StringComparison.Ordinal) ? first.WorldEpoch.CompareTo(second.WorldEpoch)
                : string.CompareOrdinal(first.Id, second.Id);
            if (order == 0) order = first.LifeEpoch.CompareTo(second.LifeEpoch);
            return Ordered(order, operation);
        }
        if (member == "enum")
        {
            RequireNoTolerance(tolerance);
            if (operation is not (ScalarComparison.Equal or ScalarComparison.NotEqual))
                throw new RuntimeContractException("pure-operation", "An option value only compares equal or not equal.");
            return Ordered(string.CompareOrdinal(Text(left), Text(right)) == 0 ? 0 : 1, operation);
        }
        if (member == "boolean")
        {
            // A flag has no order either: true and false are only equal or not.
            RequireNoTolerance(tolerance);
            if (operation is not (ScalarComparison.Equal or ScalarComparison.NotEqual))
                throw new RuntimeContractException("pure-operation", "A flag only compares equal or not equal.");
            return Ordered(Flag(left) == Flag(right) ? 0 : 1, operation);
        }
        if (member == "string")
        {
            RequireNoTolerance(tolerance);
            return Ordered(string.CompareOrdinal(Text(left), Text(right)), operation);
        }
        var leftNumber = Number(left); var rightNumber = Number(right);
        // An integer comparison answers about counts, so a non-integral operand is refused rather than truncated:
        // the frame would otherwise accept a value its own contract says the port never carries.
        if (member == "integer" && (leftNumber != Math.Truncate(leftNumber) || rightNumber != Math.Truncate(rightNumber)))
            throw new RuntimeContractException("pure-operation", "An integer comparison requires integral operands.");
        return Compare(leftNumber, rightNumber, operation, tolerance ?? 0d);
    }

    private static bool Ordered(int order, ScalarComparison operation) => operation switch
    {
        ScalarComparison.Equal => order == 0,
        ScalarComparison.NotEqual => order != 0,
        ScalarComparison.Less => order < 0,
        ScalarComparison.LessOrEqual => order <= 0,
        ScalarComparison.Greater => order > 0,
        ScalarComparison.GreaterOrEqual => order >= 0,
        _ => throw new RuntimeContractException("pure-operation", "Unknown comparison operation.")
    };

    private static void RequireNoTolerance(double? tolerance)
    {
        if (tolerance != null)
            throw new RuntimeContractException("pure-operation", "Only a numeric comparison carries a tolerance.");
    }

    private static double Number(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? PureNumbers.Input(number)
            : throw new RuntimeContractException("pure-operation", "A numeric comparison requires numeric operands.");

    private static bool Flag(JsonElement value)
        => value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new RuntimeContractException("pure-operation", "A flag comparison requires flag operands.");

    private static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new RuntimeContractException("pure-operation", "A text comparison requires text operands.");

}

internal static class PureNumbers
{
    internal static double Input(double value)
    {
        if (!double.IsFinite(value))
            throw new RuntimeContractException("pure-invalid-number", "All inputs must be finite numbers.");
        return value;
    }

    internal static double Result(double value)
    {
        if (!double.IsFinite(value))
            throw new RuntimeContractException("pure-nonfinite-result", "Calculation overflowed or has no finite real result.");
        // Match JSON authoring output; negative zero is not a distinct persisted value.
        return value == 0d ? 0d : value;
    }

    internal static void Bounds(double minimum, double maximum)
    {
        Input(minimum); Input(maximum);
        if (minimum > maximum)
            throw new RuntimeContractException("pure-reversed-range", "Minimum must not exceed maximum.");
    }
}
