using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>Binding identity and handler shape; the capability and its ports come from the block table.</summary>
internal static class PlayerSelectorContract
{
    public const string CapabilityId = Blocks.SelectorTargetPlayers.Id;
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.players";
    public const string HandlerName = "gtfo.player.players";
    public static readonly HandlerShape Shape = new HandlerShape().Outputs("targets");
}
