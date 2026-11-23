using ForgeEngine.Framework;

namespace ForgeMap;

internal static class WorldLabelContract
{
    public const string Handler = "gtfo.map.world_label";
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("target", "viewers", "enabled", "style", "text", "position", "offset", "text_resource", "size", "color", "rotation", "background_color", "background_opacity", "background_padding")
        .Outputs("result").Parameters("facing");
    public static ProviderBinding Binding() => new(ModuleDefinition.ProviderId + ".binding.world_label", Blocks.ActionPresentationWorldLabel.Id,
        Handler, "execute", new[] { EnvironmentContract.NavMarkerPermission }, Shape);
}
