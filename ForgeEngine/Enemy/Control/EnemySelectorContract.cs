using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>Binding identity. The generated block table's row is the semantic authority.</summary>
internal static class EnemySelectorContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.enemies";
    public const string HandlerName = "gtfo.enemy.enemies";
    public static readonly HandlerShape Shape = new HandlerShape().Inputs("zone", "center", "radius").Outputs("targets");
    public const string BindingRowJson = """
    {"id":"forge.module.gtfo.enemy.binding.target.enemies","capabilityId":"forge.selector.target.enemies",
     "providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.enemies","role":"observe",
     "status":"implemented","dependencies":[],"requires":[]}
    """;
}
