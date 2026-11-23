using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The row `forge.action.presentation.success_screen`: the title text and the music of the game's own
/// expedition-success page, written on the machine of each viewer the plan named. It is a `presentation` tier row —
/// what it writes is one machine's own menu — and its native half is <c>ForgeMap.Native.SuccessScreenActions</c>,
/// which holds the page's originals for the world and puts them back when the world changes.</summary>
internal static class SuccessScreenContract
{
    public const string HandlerName = "gtfo.presentation.success_screen";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.presentation.success_screen";

    /// <summary>The handler shape: the viewers whose own success page is written, the literal title, the music
    /// sound event id and the stored title block. The row has no parameters.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("viewers", "title", "music", "title_id").Outputs("result");
    public static readonly HandlerSlot Viewers = Shape.InputSlot("viewers");
    public static readonly HandlerSlot Title = Shape.InputSlot("title");
    public static readonly HandlerSlot Music = Shape.InputSlot("music");
    public static readonly HandlerSlot TitleId = Shape.InputSlot("title_id");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionPresentationSuccessScreen.Id, HandlerName, "execute", new[] { "presentation.screen" }, Shape)
    };
}
