using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The row that returns a generator to its unpowered start: `forge.action.map.generator_reset`. A powered generator
/// has its cell taken out through the generator's own interaction entry (`LG_PowerGenerator_Core.AttemptInteract` with
/// `RemoveCell`), which is the state change the game itself makes when a cell leaves a generator; what the game then
/// does with the cell (release it, hand it back) is the game's own. A generator that is not powered is already in the
/// requested state and is reported as such without a write. Recipients are generators the map placed, addressed as
/// `gtfo.map_object` entities of the generator category.
/// </summary>
internal static class GeneratorResetContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.generator_reset";
    public const string HandlerName = "gtfo.map.generator_reset";
    public const string ResetPermission = ModuleDefinition.ProviderId + ".generator.reset";

    public static readonly HandlerShape Shape = new HandlerShape().Inputs("generators").Outputs("result");
    public static readonly HandlerSlot Generators = Shape.InputSlot("generators");

    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionMapGeneratorReset.Id, HandlerName, "execute", new[] { ResetPermission }, Shape)
    };
}
