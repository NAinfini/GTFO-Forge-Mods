using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>Shows one placed model's text in one selected language column on explicitly addressed clients: the text
/// the model owns, or the optional `text` input (a template's output) in its place.</summary>
internal static class ModelTextContract
{
    public const string HandlerName = "gtfo.presentation.model_text";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.action.presentation.model_text";
    public const string Permission = "presentation.model_text";
    public static readonly HandlerShape Shape = new HandlerShape().Inputs("model", "recipients", "language", "text").Outputs("result");
    public static readonly HandlerSlot ModelSlot = Shape.InputSlot("model");
    public static readonly HandlerSlot RecipientsSlot = Shape.InputSlot("recipients");
    public static readonly HandlerSlot LanguageSlot = Shape.InputSlot("language");
    public static readonly HandlerSlot TextSlot = Shape.InputSlot("text");

    public static ProviderBinding[] DeclareBindings() => new[]
    {
        new ProviderBinding(BindingId, Blocks.ActionPresentationModelText.Id, HandlerName, "execute", new[] { Permission }, Shape)
    };
}
