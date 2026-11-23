using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The collection selectors: the rows that answer with a set built from the sets a plan already holds.
/// Every one of them declares the same structural `empty` policy — the catalog's own set, applied to the set the
/// handler answered with, never to the world — and none of them reads the world at all: the candidates arrive as
/// ports, and the references they answer with are checked by the kernel before a consumer sees them.</summary>
internal static class ObservedCollectionDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Row(Blocks.SelectorTargetDistinct.Id,
            new HandlerShape().Inputs("candidates").Outputs("targets").Parameters("empty"), Distinct),
        ObservedDeclaration.Row(Blocks.SelectorTargetLimit.Id,
            new HandlerShape().Inputs("candidates", "max_targets").Outputs("targets").Parameters("empty"), Limit),
        ObservedDeclaration.Row(Blocks.SelectorTargetRandom.Id,
            new HandlerShape().Inputs("candidates", "count", "seed", "weights").Outputs("targets")
                .Parameters("seed_mode", "allow_repeats", "empty"), Random),
        ObservedDeclaration.Row(Blocks.SelectorTargetUnion.Id,
            new HandlerShape().Inputs("a", "b").Outputs("targets").Parameters("empty", "operation"), Combine));

    // ---- handlers ------------------------------------------------------------------------------------------

    private static void Distinct(EvaluationContext context, EvaluationOutputs outputs)
        => ObservedEvaluation.Targets(context, outputs, ReferenceCollections.Distinct(ObservedEvaluation.Collection(context, "candidates")));
    private static void Limit(EvaluationContext context, EvaluationOutputs outputs)
        => ObservedEvaluation.Targets(context, outputs, ReferenceCollections.Limit(ObservedEvaluation.Collection(context, "candidates"),
            ObservedEvaluation.Selection(context, "max_targets")).Selected);
    /// <summary>The random draw. Without `weights` every candidate is equally likely; with them the draw is the
    /// weighted sampling, one weight per candidate, and `allow_repeats` chooses between drawing with or without
    /// replacement in both cases.</summary>
    private static void Random(EvaluationContext context, EvaluationOutputs outputs)
    {
        var candidates = ObservedEvaluation.Collection(context, "candidates");
        var seed = ObservedEvaluation.Seed(context, "seed", ObservedEvaluation.ParameterText(context, "seed_mode"));
        var count = ObservedEvaluation.Selection(context, "count");
        var repeats = ObservedEvaluation.OptionalParameterFlag(context, "allow_repeats", false);
        if (!context.Inputs.TryGetProperty("weights", out var weights) || weights.ValueKind == JsonValueKind.Null)
            ObservedEvaluation.Targets(context, outputs, ReferenceCollections.Random(candidates, seed, count, repeats).Selected);
        else ObservedEvaluation.Targets(context, outputs, ReferenceCollections.Weighted(candidates,
            weights.EnumerateArray().Select(weight => weight.GetDouble()).ToArray(), seed, count, repeats));
    }

    /// <summary>The set operation the `operation` member picks; an unwritten member is the set's first, the union.
    /// </summary>
    private static void Combine(EvaluationContext context, EvaluationOutputs outputs)
    {
        var a = ObservedEvaluation.Collection(context, "a");
        var b = ObservedEvaluation.Collection(context, "b");
        ObservedEvaluation.Targets(context, outputs, (ObservedEvaluation.OptionalParameterText(context, "operation") ?? "union") switch
        {
            "union" => ReferenceCollections.Union(a, b),
            "intersection" => ReferenceCollections.Intersection(a, b),
            "difference" => ReferenceCollections.Difference(a, b),
            var other => throw new RuntimeContractException("pure-operation", "Unknown set operation: " + other)
        });
    }
}
