using System;
using ForgeMap.Fields;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>
/// The global wave group's native half: writes the level's `forge.globalWave.Settings` numbers
/// (<see cref="GlobalWavePlan"/>) onto the two components the game keeps them on, once for each world the session
/// sees. The write goes through the components' own public members wherever one exists — `SetCooldownFactor` and
/// the static `AllowedTotalCost` property — initial heat goes through `SetHeat`, while authored base-weight slots
/// are updated in place. Native array identities and entries beyond the authored five enemy types remain intact;
/// missing or undersized base-weight tables are refused before any field is written.
///
/// Both managers are host-side state: the machine that owns a wave is the one that draws the next type and
/// replicates the spawn, so the session calls this on the host only. A level build where the population manager
/// is still absent returns NotReady for the session's existing in-level frame to retry.
/// </summary>
internal enum GlobalWaveApplyResult { Applied, NoConfiguration, NotReady, Failed }

internal static class GlobalWaveApplier
{
    private static GlobalWavePlan? _plan;

    internal static void Install(GlobalWavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Restore();
        _plan = plan.IsEmpty ? null : plan;
    }

    /// <summary>Applies one level once it and its population manager are readable. NotReady is deliberately
    /// retryable; every other result is terminal for this world epoch.</summary>
    internal static GlobalWaveApplyResult Apply(MapLevelReference? level)
    {
        if (_plan == null) return GlobalWaveApplyResult.NoConfiguration;
        if (level == null) return GlobalWaveApplyResult.NotReady;
        if (_plan.For(level) is not { } tuning) return GlobalWaveApplyResult.NoConfiguration;
        try
        {
            var population = EnemyPopulationManager.Current;
            if (population == null || population.WasCollected)
                return GlobalWaveApplyResult.NotReady;
            var baseWeights = population.m_baseWeightTable;
            if (!ValidTable(tuning.BaseWeights, baseWeights?.Length, MapFieldSchema.BaseWeights))
                return GlobalWaveApplyResult.Failed;
            // A previous world's contribution is given back before this world's numbers are recorded against the
            // values the game holds now.
            Restore();
            _written = new Written { Population = population };
            if (tuning.BaseWeights is { } weights)
            {
                _written.BaseWeights = new float[weights.Length];
                _written.BaseWeightsBaseline = new float[weights.Length];
                for (int i = 0; i < weights.Length; i++)
                {
                    _written.BaseWeightsBaseline[i] = baseWeights![i];
                    _written.BaseWeights[i] = weights[i];
                    baseWeights[i] = weights[i];
                }
            }
            if (tuning.HeatsAtStart is { } heat)
            {
                _written.Heats = heat;
                _written.HeatsBaseline = new float?[heat.Length];
                for (int i = 0; i < heat.Length; i++)
                {
                    _written.HeatsBaseline[i] = HeatOf(population, (eEnemyType)i);
                    population.SetHeat((eEnemyType)i, heat[i]);
                }
            }
            if (tuning.MaxHeat is { } maxHeat)
            {
                _written.MaxHeatBaseline = population.m_maxHeat;
                _written.MaxHeat = maxHeat;
                population.m_maxHeat = maxHeat;
            }
            if (tuning.HeatCooldownSpeed is { } speed)
            {
                _written.CooldownBaseline = population.m_cooldownFactor;
                _written.Cooldown = speed;
                population.SetCooldownFactor(speed);
            }
            // The cap is written through the game's own property rather than its backing field: the value the draw
            // compares against is whatever that property answers with.
            if (tuning.AllowedTotalCost is { } allowed)
            {
                _written.AllowedBaseline = EnemyCostManager.AllowedTotalCost;
                _written.Allowed = allowed;
                EnemyCostManager.AllowedTotalCost = allowed;
            }
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplied, "block=" + MapFieldSchema.GlobalWaveBlock + " level=" + level + " "
                + tuning.Describe(), RuntimeLogValue.Of("block", MapFieldSchema.GlobalWaveBlock));
            return GlobalWaveApplyResult.Applied;
        }
        catch (Exception error)
        {
            // Whatever was written before the failing setter is given back at once, and the log says whether that worked.
            var rolledBack = Restore();
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyFailed,
                "group=global-wave level=" + level + " writes=" + (rolledBack ? "rolled-back" : "partial-or-unknown") + " error=" + error.GetType().Name + ": " + error.Message,
                RuntimeLogValue.Of("group", "global-wave"), RuntimeLogValue.Of("exception", error.GetType().Name));
            return GlobalWaveApplyResult.Failed;
        }
    }

    /// <summary>A checkpoint reload puts the wave heat back where the level started. The game resets heat only in
    /// `Builder.OnLevelCleanup` (`ResetHeat`, then `ResetCooldownSpeed`); a reload rebuilds nothing and
    /// `EnemyPopulationManager` is not part of the checkpoint capture, so without this every retry keeps the heat the
    /// failed attempt drew. `ResetHeat` writes each type's `GetBaseHeatForType` (the level-start value), then this
    /// world's authored start heats go back on, so <see cref="Restore"/> still recognises them. Host only, like the draw.</summary>
    internal static void CheckpointRestored()
    {
        try
        {
            var population = EnemyPopulationManager.Current;
            if (population == null || population.WasCollected) return;
            population.ResetHeat();
            var heats = _written?.Population == population ? _written.Heats : null;
            if (heats != null)
                for (int i = 0; i < heats.Length; i++) population.SetHeat((eEnemyType)i, heats[i]);
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.WaveHeatReset, "wave heat back to the level start after a checkpoint reload"
                + (heats != null ? " (authored start heats)" : ""), RuntimeLogValue.Of("authored", heats != null));
        }
        catch (Exception error)
        {
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyFailed, "group=global-wave heat-reset error=" + error.GetType().Name + ": " + error.Message,
                RuntimeLogValue.Of("group", "global-wave"), RuntimeLogValue.Of("exception", error.GetType().Name));
        }
    }

    /// <summary>What this package wrote and the value each field held before it, so the world's end can give back
    /// exactly this contribution. A field whose current value is no longer the one written belongs to a later
    /// writer (the game itself moves the heats on every draw) and is left alone.</summary>
    private sealed class Written
    {
        internal EnemyPopulationManager Population = null!;
        internal float[]? BaseWeights, BaseWeightsBaseline;
        internal float[]? Heats;
        internal float?[]? HeatsBaseline;
        internal float? MaxHeat, MaxHeatBaseline, Cooldown, CooldownBaseline, Allowed, AllowedBaseline;
    }

    private static Written? _written;

    /// <summary>Gives back the previous world's contribution: every field still holding the value this package wrote
    /// returns to the value it had before. The population manager belongs to that world, so a collected one has
    /// nothing left to restore; the static cost cap outlives it and is always checked.</summary>
    internal static bool Restore()
    {
        var written = _written;
        _written = null;
        if (written == null) return true;
        // The static cap outlives the population manager, so a failing manager access never skips it.
        var restored = RestorePopulation(written);
        try
        {
            if (written.Allowed is { } allowed && EnemyCostManager.AllowedTotalCost == allowed)
                EnemyCostManager.AllowedTotalCost = written.AllowedBaseline!.Value;
        }
        catch (Exception error)
        {
            Failed(error);
            restored = false;
        }
        return restored;
    }

    private static bool RestorePopulation(Written written)
    {
        try
        {
            var population = written.Population;
            if (population != null && !population.WasCollected)
            {
                if (written.BaseWeights != null && population.m_baseWeightTable is { } table)
                    for (int i = 0; i < written.BaseWeights.Length && i < table.Length; i++)
                        if (table[i] == written.BaseWeights[i]) table[i] = written.BaseWeightsBaseline![i];
                if (written.Heats != null)
                    for (int i = 0; i < written.Heats.Length; i++)
                        if (written.HeatsBaseline![i] is { } before && HeatOf(population, (eEnemyType)i) == written.Heats[i])
                            population.SetHeat((eEnemyType)i, before);
                if (written.MaxHeat is { } maxHeat && population.m_maxHeat == maxHeat)
                    population.m_maxHeat = written.MaxHeatBaseline!.Value;
                if (written.Cooldown is { } cooldown && population.m_cooldownFactor == cooldown)
                    population.SetCooldownFactor(written.CooldownBaseline!.Value);
            }
            return true;
        }
        catch (Exception error)
        {
            Failed(error);
            return false;
        }
    }

    private static void Failed(Exception error)
        => MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyFailed,
            "group=global-wave restore=partial-or-unknown error=" + error.GetType().Name + ": " + error.Message,
            RuntimeLogValue.Of("group", "global-wave"), RuntimeLogValue.Of("exception", error.GetType().Name));

    private static float? HeatOf(EnemyPopulationManager population, eEnemyType type)
    {
        foreach (var heat in population.m_heatTypes)
            if (heat != null && heat.m_enemyType == type) return heat.m_heat;
        return null;
    }

    private static bool ValidTable(float[]? authored, int? nativeLength, string field)
    {
        if (authored == null || (nativeLength is { } length && length >= authored.Length)) return true;
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyRefused,
            "group=global-wave field=" + field + " code=native-table-missing: the native table cannot hold the authored enemy types",
            RuntimeLogValue.Of("group", "global-wave"), RuntimeLogValue.Of("field", field),
            RuntimeLogValue.Of("code", "native-table-missing"));
        return false;
    }
}

