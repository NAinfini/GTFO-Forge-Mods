using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `forge.action.presentation.interaction_text` row: the one interaction-prompt row of this provider,
/// which the EOS security-door feature's two stopped rows fold into (`door_interact_text` and `door_text_glitch`,
/// rulings R1/R2). One capability writes one object's prompt — replace it, wrap it, break it up, or put the game's
/// own wording back — so an author never has to name two rows to say "this door reads wrong now".
///
/// The row is a `presentation` tier row, which is the kernel's own tier for "the host owns the decision, the
/// recipient owns the write": the prompt is drawn by the machine the player sits at (`Interact_Base.
/// InteractionMessage`), so every machine has to hold the rule for its own prompt rather than the host holding it
/// for all of them. The host decides when the step runs; each addressed client writes its own copy.
///
/// The EOS feature's two glitch styles keep their behaviour and lose their names: `style1`/`style2` become the
/// generic `hex`/`decrypt`, which is what the styles really are. Everything else the feature carried — the
/// replace text, the prefix and the postfix — is one `text` input and three modes, because the three edits are
/// mutually exclusive in the prompt the player reads and a request that carried all of them at once would be
/// three orders for one line of text.
///
/// Nothing here writes door state: a rewritten prompt cannot unlock, open or alarm anything. That is also why the
/// row declares no `host` authority: a presentation write commits no world state, and the result rows say so.</summary>
internal static class InteractionTextContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.presentation.interaction_text";
    public const string HandlerName = "gtfo.presentation.interaction_text";

    /// <summary>The permission the prompt's own edit needs. It is the presentation tier's permission for text the
    /// game draws, so a plan declares that it may rewrite what a player reads before it can.</summary>
    public const string Permission = "presentation.interaction_text";

    /// <summary>The handler's own port set: the map objects it addresses and the text it writes, in the row's own
    /// order. The written text is a real input the handler reads, so it is declared here as well — a capability
    /// port the shape does not cover is a row the registry refuses.</summary>
    public static HandlerShape Shape { get; } = new HandlerShape()
        .Inputs("targets", "text").Outputs("result").Parameters("mode", "style", "refresh_interval");
    public static readonly HandlerSlot Targets = Shape.InputSlot("targets");
    public static readonly HandlerSlot Text = Shape.InputSlot("text");
    public static readonly HandlerSlot Mode = Shape.ParameterSlot("mode");
    public static readonly HandlerSlot Style = Shape.ParameterSlot("style");
    public static readonly HandlerSlot RefreshInterval = Shape.ParameterSlot("refresh_interval");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionPresentationInteractionText.Id, HandlerName, "execute", new[] { Permission }, Shape)
    };
}
