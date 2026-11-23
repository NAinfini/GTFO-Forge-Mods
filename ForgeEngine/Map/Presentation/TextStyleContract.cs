using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Restyles one level text already shown on explicitly addressed clients — a world sign or a placed model's
/// text: its text colour, its background colour and opacity, and optionally its text. Unwired inputs keep what is
/// shown, so a behaviour that only turns a test sign green writes only the colour.</summary>
internal static class TextStyleContract
{
    public const string HandlerName = "gtfo.presentation.text_style";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.action.presentation.text_style";
    public const string Permission = "presentation.text_style";
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("target", "viewers", "text", "color", "background_color", "background_opacity").Outputs("result");
    public static readonly HandlerSlot TargetSlot = Shape.InputSlot("target");
    public static readonly HandlerSlot ViewersSlot = Shape.InputSlot("viewers");
    public static readonly HandlerSlot TextSlot = Shape.InputSlot("text");
    public static readonly HandlerSlot ColorSlot = Shape.InputSlot("color");
    public static readonly HandlerSlot BackgroundColorSlot = Shape.InputSlot("background_color");
    public static readonly HandlerSlot BackgroundOpacitySlot = Shape.InputSlot("background_opacity");

    public static ProviderBinding[] DeclareBindings() => new[]
    {
        new ProviderBinding(BindingId, Blocks.ActionPresentationTextStyle.Id, HandlerName, "execute", new[] { Permission }, Shape)
    };
}
