using ForgeEngine.Framework;

namespace ForgeMap;

internal static class EnemySpawnContract
{
    public const string Handler = "gtfo.map.enemy_spawn";
    public static readonly HandlerShape Shape = new HandlerShape().Inputs("zone", "enemy", "position", "direction")
        .Outputs("result", "spawned").Parameters("mode");
    public static readonly HandlerSlot Zone = Shape.InputSlot("zone"), EnemyType = Shape.InputSlot("enemy"),
        Position = Shape.InputSlot("position"), Direction = Shape.InputSlot("direction"), Mode = Shape.ParameterSlot("mode");
    public static ProviderBinding Binding() => new(ModuleDefinition.ProviderId + ".binding.enemy_spawn", Blocks.ActionEnemySpawn.Id,
        Handler, "execute", new[] { "spawn.enemy" }, Shape);
}
