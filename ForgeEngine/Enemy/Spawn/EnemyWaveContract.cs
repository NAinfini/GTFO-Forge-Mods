using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The four wave facts that belong to this package (ruling: the wave facts are ForgeEnemy's, the map
/// side owns placement and scheduling data). It sits in the game-independent Enemy assembly because the runtime
/// module, the manifest and the website read it there; the native observation is <c>EnemyWaveFacts</c> in the
/// native assembly, which publishes through these row ids.
///
/// The rows themselves are `forge.trigger.*` rows, so they are declared once by the runtime's trigger contract
/// (`ForgeEngine/Framework/Policy/TriggerContracts.cs`): `forge.trigger.objective.{wave_started,wave_spawned,
/// wave_exhausted,wave_cleared}`, with the catalog's ports. This file carries the binding half only.
///
/// The rows declare no `wave` handle output: a handle is minted only by the Map `wave_start` action that began the
/// wave, and an observation provider mints none. The wave instance identity travels in the event's own scope id: the
/// stable resource address exported by Map and registered against the native EventID before spawn. Epochs only
/// reject stale registrations.
/// The other ports are published with real native values.</summary>
internal static class EnemyWaveContract
{
    /// <summary>A binding id is this provider's own name for the row and the capability id is the canonical name
    /// it carries; the two namespaces are separate maps in the kernel registry, so the two never share a
    /// spelling.</summary>
    public const string WaveStartedBinding = ModuleDefinition.ProviderId + ".binding.wave_started";
    public const string WaveSpawnedBinding = ModuleDefinition.ProviderId + ".binding.wave_spawned";
    public const string WaveExhaustedBinding = ModuleDefinition.ProviderId + ".binding.wave_exhausted";
    public const string WaveClearedBinding = ModuleDefinition.ProviderId + ".binding.wave_cleared";

    /// <summary>The handler names the `observe` rows carry. A trigger event arrives as one whole frame, so no
    /// handler and no shape is resolved for these bindings; the names are the rows' own discriminators.</summary>
    public const string WaveStartedHandler = "gtfo.enemy.wave_started";
    public const string WaveSpawnedHandler = "gtfo.enemy.wave_spawned";
    public const string WaveExhaustedHandler = "gtfo.enemy.wave_exhausted";
    public const string WaveClearedHandler = "gtfo.enemy.wave_cleared";

    /// <summary>Every binding this family declares, in the order the registry rows list them. A native hook
    /// returns before reading the game while none of them has a plan.</summary>
    public static readonly string[] Bindings =
    {
        WaveStartedBinding, WaveSpawnedBinding, WaveExhaustedBinding, WaveClearedBinding
    };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(WaveStartedBinding, Blocks.TriggerObjectiveWaveStarted.Id, WaveStartedHandler, "observe", new[] { "gtfo.enemy.waves.read" }),
        new ProviderBinding(WaveSpawnedBinding, Blocks.TriggerObjectiveWaveSpawned.Id, WaveSpawnedHandler, "observe", new[] { "gtfo.enemy.waves.read" }),
        new ProviderBinding(WaveExhaustedBinding, Blocks.TriggerObjectiveWaveExhausted.Id, WaveExhaustedHandler, "observe", new[] { "gtfo.enemy.waves.read" }),
        new ProviderBinding(WaveClearedBinding, Blocks.TriggerObjectiveWaveCleared.Id, WaveClearedHandler, "observe", new[] { "gtfo.enemy.waves.read" }) { Waitable = true }
    };
}
