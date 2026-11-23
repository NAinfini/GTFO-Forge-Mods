using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Targeting;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The scalar family: the fixed number, the clamp, the seeded random draw and the range mapping. The
/// arithmetic itself — add through round — is the one calculation card in <see cref="VariadicDeclarations"/>. The draw is the
/// host's own row: a plan only runs on the host, so the value is evaluated there and reaches every client with the
/// execution that produced it — which is why it needs no level seed and reads none.</summary>
internal static class ScalarDeclarations
{
    internal static IReadOnlyList<ObservedNode> Nodes { get; } = new[]
    {
        PureModule.Row(Blocks.ModifierValueConstant.Id,
            new HandlerShape().Outputs("value").Parameters("value"), PureModule.Constant),
        PureModule.Row(Blocks.ModifierValueClamp.Id,
            new HandlerShape().Inputs("value", "minimum", "maximum").Outputs("value"), PureModule.Clamp),
        PureModule.Row(Blocks.ModifierValueRandomRange.Id,
            new HandlerShape().Inputs("minimum", "maximum", "seed").Outputs("value"), PureModule.RandomRange),
        PureModule.Row(Blocks.ModifierValueMapRange.Id,
            new HandlerShape().Inputs("value", "input_min", "input_max", "input_floor", "input_ceiling", "output_min",
                "output_max", "exponent", "flip").Outputs("value").Parameters("input_unit", "bounds"), PureModule.MapRange)
    };
}
