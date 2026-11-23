using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Targeting;

namespace ForgeEngine.Trigger.Pure;

/// <summary>The text family: the `g-text` row, which fills one template sentence with up to eight typed values, and
/// the reviewed fragment row, which fills one native text fragment's placeholder in a template. Both rows are `pure`,
/// so they read nothing and the host that runs the plan evaluates them.
///
/// Each of the eight value slots is an optional input typed by its own structural `type_k` (and, for an option value,
/// `set_k`) parameter; how a value is written — decimals, the two flag texts, the option names — is the placeholder's
/// own spec in the template, so every language column writes its own and the row declares no format parameter. The
/// fragment row's structural parameter names which native placeholder to fill; the marker's own spelling is the single
/// implementation's (`TextNodes.ReplaceFragment`), so it is not a second authoring port.</summary>
internal static class TextDeclarations
{
    internal static readonly string[] TextInputs = new[] { "template" }
        .Concat(Enumerable.Range(1, TextTemplates.SlotCount).Select(slot => "value_" + slot)).ToArray();
    internal static readonly string[] TextParameters = Enumerable.Range(1, TextTemplates.SlotCount)
        .SelectMany(slot => new[] { "type_" + slot, "set_" + slot }).ToArray();

    internal static IReadOnlyList<ObservedNode> Nodes { get; } = new[]
    {
        PureModule.Row(Blocks.ModifierValueText.Id,
            new HandlerShape().Inputs(TextInputs).Outputs("value").Parameters(TextParameters), PureModule.Text),
        PureModule.Row(Blocks.ModifierTextFragment.Id,
            new HandlerShape().Inputs("template", "value").Outputs("value").Parameters("fragment"), PureModule.TextFragment)
    };
}
