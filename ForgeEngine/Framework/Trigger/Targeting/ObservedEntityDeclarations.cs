using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The event-role selectors: the rows that answer with a role of the event being dispatched instead of
/// reading a world. Every one of them reads <see cref="EvaluationContext.Actors"/>, the one delivery table, and
/// never rebuilds a role-to-port mapping here: the four roles the catalog marks nullable answer null when the
/// event does not carry them, and `self` — the one role no event may leave out — is refused by name through
/// <see cref="RuntimeActorContext.Require"/>, the same `actor-missing` the kernel raises for a role a contract
/// says it answers with.</summary>
internal static class ObservedEntityDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Row(Blocks.SelectorTargetSelf.Id,
            new HandlerShape().Outputs("target"), (context, outputs) => Role(context, outputs, "self", required: true)),
        ObservedDeclaration.Row(Blocks.SelectorTargetOwner.Id,
            new HandlerShape().Outputs("target"), (context, outputs) => Role(context, outputs, "owner", required: false)),
        ObservedDeclaration.Row(Blocks.SelectorTargetSource.Id,
            new HandlerShape().Outputs("target"), (context, outputs) => Role(context, outputs, "source", required: false)),
        ObservedDeclaration.Row(Blocks.SelectorTargetInstigator.Id,
            new HandlerShape().Outputs("target"), (context, outputs) => Role(context, outputs, "instigator", required: false)),
        ObservedDeclaration.Row(Blocks.SelectorTargetEventTarget.Id,
            new HandlerShape().Outputs("target"), (context, outputs) => Role(context, outputs, "event-target", required: false)));

    /// <summary>The role's reference as the selector's only output: the four nullable roles answer null when the
    /// event does not carry them, and `self` refuses the step by name instead of answering a placeholder.</summary>
    private static void Role(EvaluationContext context, EvaluationOutputs outputs, string role, bool required)
        => ObservedEvaluation.Value(outputs, required ? context.Actors.Require(role) : context.Actors.Get(role));
}
