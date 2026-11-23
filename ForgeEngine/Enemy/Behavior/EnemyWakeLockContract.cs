using System;
using ForgeEngine.Framework;

namespace ForgeEnemy;

internal static class EnemyWakeLockContract
{
    public const string Binding = ModuleDefinition.ProviderId + ".binding.enemy.wake_lock";
    public const string Handler = "gtfo.enemy.action.enemy.wake_lock";
    public const string ReadHandler = "gtfo.enemy.query.enemy.wake_locked";
    public static readonly HandlerShape ReadShape = new HandlerShape().Inputs("enemy").Outputs("value");
    public static readonly HandlerSlot ReadEnemy = ReadShape.InputSlot("enemy");
    public static readonly HandlerShape Shape = new HandlerShape().Inputs("enemies", "enabled").Outputs("result");
    public static readonly HandlerSlot Enemies = Shape.InputSlot("enemies");
    public static readonly HandlerSlot Enabled = Shape.InputSlot("enabled");
    internal static ProviderBinding DeclareBinding() => new(Binding, Blocks.ActionEnemyWakeLock.Id, Handler, "execute",
        new[] { "gtfo.enemy.behavior.write" }, Shape);
    internal static ProviderBinding ReadBinding() => new(ModuleDefinition.ProviderId + ".binding.query.enemy.wake_locked",
        Blocks.QueryEnemyWakeLocked.Id, ReadHandler, "observe", Array.Empty<string>(), ReadShape);
}
