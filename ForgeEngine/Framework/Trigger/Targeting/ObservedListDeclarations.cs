using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The list family: the rows that answer about a candidate list instead of about a single entity. The
/// list is the one a selector already produced, so no row here walks the world to decide its answer, and every
/// row answers the same way for an empty list — an empty list is empty, contains nothing and counts zero. The
/// port is read exactly as the selector family reads its own candidate port: a list a step does not carry is an
/// empty list rather than a missing answer, which is what makes `is_empty` the gate for an empty selection and
/// `count` the count of one.</summary>
internal static class ObservedListDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Row(Blocks.ConditionListIsEmpty.Id,
            new HandlerShape().Inputs("items").Outputs("value"), IsEmptyHandler),
        ObservedDeclaration.Row(Blocks.ConditionListContains.Id,
            new HandlerShape().Inputs("items", "item").Outputs("value"), ContainsHandler),
        ObservedDeclaration.Row(Blocks.ModifierListCount.Id,
            new HandlerShape().Inputs("items").Outputs("value"), CountHandler));

    /// <summary>Whether the list has no members. The answer is the list's own length: the row never asks the world
    /// whether something exists, so an empty list can only mean empty.</summary>
    public static bool IsEmpty(IReadOnlyList<EntityReference> items) => items.Count == 0;

    /// <summary>Whether the list holds the reference, compared by the full identity the kernel itself compares —
    /// the same id from another life is another member — so the row can never answer for an entity the list does
    /// not hold.</summary>
    public static bool Contains(IReadOnlyList<EntityReference> items, EntityReference item) => items.Contains(item);

    /// <summary>How many members the list holds, as the catalog's own integer. A list no step carries counts zero
    /// for the same reason it is empty: the row counts the list it was handed, never a world it did not read.</summary>
    public static long Count(IReadOnlyList<EntityReference> items) => items.Count;

    // ---- handlers ------------------------------------------------------------------------------------------

    private static void IsEmptyHandler(EvaluationContext context, EvaluationOutputs outputs)
        => ObservedEvaluation.Value(outputs, IsEmpty(ObservedEvaluation.Collection(context, "items")));

    private static void ContainsHandler(EvaluationContext context, EvaluationOutputs outputs)
        => ObservedEvaluation.Value(outputs, Contains(ObservedEvaluation.Collection(context, "items"),
            ObservedEvaluation.Entity(context, "item")));

    private static void CountHandler(EvaluationContext context, EvaluationOutputs outputs)
        => outputs.Integer(0, Count(ObservedEvaluation.Collection(context, "items")));
}
