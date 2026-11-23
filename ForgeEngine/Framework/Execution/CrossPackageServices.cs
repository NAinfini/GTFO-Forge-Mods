namespace ForgeEngine.Framework;

// Services the engine publishes for other plugins. PublishService keys on the exact type, so each one is a named
// delegate: a bare Func/Action of the same shape from a third party can never collide with it (service-conflict).

/// <summary>Resolves the native enemy a reference names (an Enemies.EnemyAgent), or null when it is gone or stale.</summary>
public delegate object? EnemyAgentResolver(EntityReference reference);

/// <summary>The readback text of the compiled map generation for a world epoch.</summary>
public delegate string MapGenerationReadback(long world);
