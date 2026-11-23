using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The module's own handle on the wave fact family: the observation state exists exactly while the
/// receiver does, and the native hooks reach it through the module they already guard on. It is created with the
/// registration — the simulation thread, where its gates are resolved — and held for the lifetime of the receiver,
/// so its thread check is the module's own thread. The declaration matches `EnemyModule.cs`'s own; a partial's
/// accessibility is part of the type, so the two move together.</summary>
internal sealed partial class EnemyModule
{
    private readonly EnemyWaveFacts _waveFacts;
    private EnemyWaveFacts WaveFacts => _waveFacts;

    /// <summary>The member's current life: the native pointer and GlobalID must match the tracked entry, and its
    /// reference must still resolve. Interop may return another managed wrapper for the same native life.</summary>
    internal EntityReference? ReferenceOf(EnemyAgent? agent)
    {
        if (agent == null || !_entities.TryGetValue(agent.GlobalID, out var entry)
            || entry.EnemyPointer != agent.Pointer) return null;
        return IsLive(entry) ? entry.Reference : null;
    }

    /// <summary>PERF(E3): the wave hooks' pre-checks, field reads only. With no wave plan a hook returns before it builds
    /// a call; the end-check, despawn and maintenance hooks also wait for a tracked wave, which is all they act on.</summary>
    internal bool WavesSubscribed => WaveFacts.Subscribed;
    internal bool WaveEndWatched => WaveFacts.Subscribed && WaveFacts.HasTrackedWaves;

    internal EnemyWaveFacts.WaveSpawnObservation? BeforeWaveSpawn(SurvivalWave wave) => WaveFacts.BeforeWaveSpawn(wave);
    internal void AfterWaveSpawn(SurvivalWave wave, EnemyWaveFacts.WaveSpawnObservation? before)
        => WaveFacts.AfterWaveSpawn(wave, before);
    internal EnemyWaveFacts.WaveGroupObservation? BeforeWaveGroupStep(SurvivalWave wave) => WaveFacts.BeforeWaveGroupStep(wave);
    internal void AfterWaveGroup(SurvivalWave wave, EnemyGroup group) => WaveFacts.AfterWaveGroup(wave, group);
    internal void AfterWaveGroupStep(SurvivalWave wave, EnemyWaveFacts.WaveGroupObservation? before, bool succeeded)
        => WaveFacts.AfterWaveGroupStep(wave, before, succeeded);
    internal void AfterGroupMember(EnemyGroup group, EnemyAgent member) => WaveFacts.AfterGroupMember(group, member);
    internal EnemyWaveFacts.GroupSpawningObservation? BeforeGroupSpawning(EnemyGroup group) => WaveFacts.BeforeGroupSpawning(group);
    internal void AfterGroupSpawning(EnemyGroup group, EnemyWaveFacts.GroupSpawningObservation? before, bool completed)
        => WaveFacts.AfterGroupSpawning(group, before, completed);
    internal EnemyWaveFacts.WaveEndObservation? BeforeWaveEndCheck(SurvivalWave wave) => WaveFacts.BeforeWaveEndCheck(wave);
    internal void AfterWaveEndCheck(EnemyWaveFacts.WaveEndObservation? observation) => WaveFacts.AfterWaveEndCheck(observation);
    internal EnemyWaveFacts.WaveStopObservation? BeforeWaveStop(SurvivalWave wave) => WaveFacts.BeforeWaveStop(wave, includeCancellationScope: true);
    internal void AfterWaveStop(EnemyWaveFacts.WaveStopObservation? observation, bool succeeded)
        => WaveFacts.AfterWaveStop(observation, succeeded);
    internal void AfterGroupMaintenance() => WaveFacts.AfterGroupMaintenance();
    internal void BeforeWaveDespawn(SurvivalWave wave) => WaveFacts.BeforeWaveDespawn(wave);
}
