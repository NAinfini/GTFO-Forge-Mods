using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>
/// The one bot-policy row this provider implements: `forge.action.bot.order`, a single native action request
/// submitted to each named bot. The row's graph, its ports, its result row and its refusal codes are the reviewed
/// Primitive's own, declared from the block table; this contract only names the handler, its shape and the
/// permission the row's recipient contract requires.
///
/// The seven orders are the vocabulary the row's `order` parameter declares, in that order. They are what the game
/// itself offers as descriptors (`PlayerBotActionFollow`, `Travel`, `Attack`, `UseFirearm`, `Melee`, `Revive`,
/// `Unlock`); the row carries one request each and nothing about how a bot decides, which is why generation,
/// population and AI assets are not part of it.
/// </summary>
internal static class BotOrderContract
{
    public const string CapabilityId = Blocks.ActionBotOrder.Id;
    public const string HandlerName = "gtfo.bot.order";

    /// <summary>The permission the row's own recipient contract names: a request written into a bot's action
    /// queue.</summary>
    public const string Permission = "bot.order";

    /// <summary>This provider's binding id for the row: the capability's own suffix under the Player provider.</summary>
    public static string BindingId => ModuleDefinition.ProviderId + ".binding.bot_order";

    /// <summary>The seven orders, in the order the row's inline parameter list declares them: a plan's compiled
    /// member index is an index into this list, which is why it is read from here and not re-spelled per call.</summary>
    public static readonly string[] Orders = { "follow", "travel", "attack", "use_firearm", "melee", "revive", "unlock" };

    /// <summary>The handler's ports: the recipient collection, the optional anchor the order is about and the
    /// optional position a travel names. The two structural parameters are the row's own, so the shape declares
    /// only the ports.</summary>
    public static readonly HandlerShape OrderShape = new HandlerShape()
        .Inputs("bots", "anchor", "position").Outputs("result").Parameters("order", "priority");

    public static readonly HandlerSlot BotsSlot = OrderShape.InputSlot("bots");
    public static readonly HandlerSlot AnchorSlot = OrderShape.InputSlot("anchor");
    public static readonly HandlerSlot PositionSlot = OrderShape.InputSlot("position");
    public static readonly HandlerSlot OrderSlot = OrderShape.ParameterSlot("order");
    public static readonly HandlerSlot PrioritySlot = OrderShape.ParameterSlot("priority");

    public static HandlerShape Shape() => OrderShape;

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.player.binding.bot_order", Blocks.ActionBotOrder.Id, HandlerName, "execute", new[] { Permission }, OrderShape)
    };
}
