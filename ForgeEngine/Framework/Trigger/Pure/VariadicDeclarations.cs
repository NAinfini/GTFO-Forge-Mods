using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Targeting;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The variadic family: the calculation card and the logic card, each one ordered computation whose inputs
/// the plan expands with the catalog's own `input_count` bound and whose `operation` member picks what it computes.
/// The shape names no port, because a capability that expands its ports per plan has no registration-time layout;
/// the handler reads the frame's own port order.</summary>
internal static class VariadicDeclarations
{
    internal static IReadOnlyList<ObservedNode> Nodes { get; } = new[]
    {
        PureModule.Row(Blocks.ModifierValueAdd.Id,
            new HandlerShape(), PureModule.Calculate),
        PureModule.Row(Blocks.ConditionPredicateAll.Id,
            new HandlerShape(), PureModule.Logic)
    };
}
