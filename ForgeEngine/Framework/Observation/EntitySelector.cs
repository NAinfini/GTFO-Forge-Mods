using ForgeEngine.Trigger.Targeting;

namespace ForgeEngine.Framework;

/// <summary>One budgeted complete roster query, shared by the player and enemy providers.</summary>
internal static class EntitySelector
{
    /// <summary>Answers the roster on the evaluator's only output, `targets`. A row that declares the optional
    /// `zone`, `center` and `radius` inputs (<paramref name="scoped"/>) narrows the roster to them before it is
    /// counted; with none of them wired it is the whole roster, as before.</summary>
    public static void Evaluate(EvaluationContext context, EvaluationOutputs outputs, string entityKind, bool scoped = false)
    {
        if (!context.Query.TryCandidates(entityKind, scoped ? Scope(context) : default, out var candidates, out var code))
            throw new RuntimeContractException(code, "The candidate source refused the read: " + code);
        outputs.Entities(0, candidates);
    }

    private static EntityCandidateScope Scope(EvaluationContext context)
    {
        var zone = context.Inputs.TryGetProperty("zone", out var named) && named.ValueKind == System.Text.Json.JsonValueKind.Object
            ? ObservedSpaceDeclarations.ZoneReference(context) : null;
        var center = ObservedEvaluation.OptionalInputVector(context, "center");
        var radius = context.Inputs.TryGetProperty("radius", out var size) && size.ValueKind == System.Text.Json.JsonValueKind.Number
            ? size.GetDouble() : (double?)null;
        // A sphere is its centre and its radius together: half of one is not a smaller scope, it is no scope the
        // author finished writing, and reading it as "everywhere" would answer the whole level.
        if (center == null != (radius == null) || center is { Length: not 3 } || radius is { } r && !(r >= 0 && double.IsFinite(r)))
            throw new RuntimeContractException("missing-field", "A range needs both a center and a radius of zero or more.");
        return center == null
            ? new EntityCandidateScope { Zone = zone }
            : new EntityCandidateScope { Zone = zone, Within = true, X = center[0], Y = center[1], Z = center[2], Radius = radius!.Value };
    }
}
