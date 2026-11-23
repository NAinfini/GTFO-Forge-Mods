using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Targeting;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The two-point family: the distance between two positions and the unit direction from one to the other.
/// Both ports carry the catalog's metre unit, and the answer is the catalog's own output — metres for the distance,
/// an unqualified unit vector for the direction. Neither row reads an entity: the positions arrive as values, which
/// is what keeps the family in the `pure` tier.</summary>
internal static class VectorDeclarations
{
    internal static IReadOnlyList<ObservedNode> Nodes { get; } = new[]
    {
        PureModule.Row(Blocks.ModifierValueDistance.Id,
            new HandlerShape().Inputs("from", "to").Outputs("value"), PureModule.Distance),
        PureModule.Row(Blocks.ModifierValueDirection.Id,
            new HandlerShape().Inputs("from", "to").Outputs("value"), PureModule.Direction)
    };
}
