using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The pure comparison condition retained by the trigger provider. World reads live in the Data/Observation
/// layer; life state, health, position and other observed values are read first and then compared here. This
/// prevents predicates from becoming a second hidden world-read vocabulary.</summary>
internal static class ObservedConditionDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        // ---- the typed comparison --------------------------------------------------------------------------
        // The catalog's one `g-compare` row (rule 146.4b): the two operands are typed by the structural
        // `value_type` parameter, so a number, a count, a piece of text, an entity reference and an option value
        // are all compared by one row instead of one row per class; an option value's set has no order, so only
        // equal and not-equal are answered for it. It is a `query` row and not a `pure` one because the `entity`
        // member is a world value (rule 142.1); the comparison itself still reads no world, which is why its
        // handler never touches `context.Query`.
        ObservedDeclaration.Row(Blocks.ConditionPredicateCompare.Id,
            new HandlerShape().Inputs("left", "right", "operator", "tolerance").Outputs("value").Parameters("value_type"),
            CompareHandler));

    // ---- handlers ------------------------------------------------------------------------------------------

    /// <summary>The typed comparison. The operator arrives as the member name the kernel resolved from its
    /// compiled index, and `value_type` is the member the plan compiled — or nothing, when the author left the
    /// optional parameter unwritten, which resolves to the parameter's first member exactly as the contract does.
    /// `tolerance` is optional because only the numeric members read it.</summary>
    private static void CompareHandler(EvaluationContext context, EvaluationOutputs outputs)
        => ObservedEvaluation.Value(outputs, PureConditions.Compare(
            context.RequiredInput("left"), context.RequiredInput("right"),
            ObservedEvaluation.Comparison(ObservedEvaluation.PortText(context, "operator")),
            context.Inputs.TryGetProperty("tolerance", out var tolerance) && tolerance.ValueKind == JsonValueKind.Number
                ? tolerance.GetDouble() : (double?)null,
            context.Parameters.TryGetProperty("value_type", out var valueType) && valueType.ValueKind == JsonValueKind.String
                ? valueType.GetString() : null));

}
