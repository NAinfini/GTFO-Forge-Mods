using Enemies;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeEnemy.Native;

/// <summary>
/// The on-screen cap share of one aggressive life (`forge.enemySpawn.CostTowardsCap`, contract §4.5 D4).
///
/// What the game does, build 20403457 (GameAssembly.dll, reviewed in evidence/enemy-behavior-hooks.json):
///   * `EnemyAI.ModeChange` (0x155D720) is the only allocation. When the AI's mode is `Agressive` and the agent's
///     `m_enemyCost` is not above zero, it reads `EnemyCostManager.GetEnemyCost(EnemyData.EnemyType)` — the
///     per-type table `m_enemyTypeCosts` — stores it in `m_enemyCost` and adds it with
///     `EnemyCostManager.AddCost(m_dimensionIndex, float)`.
///   * `EnemyAgent.set_Alive(false)` and `EnemyAgent.OnDestroy` free exactly `m_enemyCost` with
///     `RemoveCost(m_dimensionIndex, float)` when it is above zero, then zero it.
///   * The cap test `IsAllowed` (from `SurvivalWave.MastermindUpdate`) and `GetPointsLeft` (from
///     `EnemyGroup.SurvivalSpawning`) compare `AllowedTotalCost` with that per-dimension sum.
///   * The wave budget is a separate read: `EnemyGroup.SurvivalSpawning` calls `GetEnemyCost(waveRole)` itself,
///     subtracts it from the group's score and hands it to `SurvivalWave.RegisterCost`, before the enemy exists.
///     Nothing on that path reads `m_enemyCost`, so a life's cap share can change without touching the wave budget.
///
/// So the share is corrected right after the game allocated it: the prefix remembers whether the life held a share,
/// the postfix sees the share the game just added, and the host replaces it with the type's authored value through
/// the game's own `AddCost` (one call carrying the difference) and `m_enemyCost`, which is what the game frees on
/// death. A client keeps the game's own count; only the host's count gates a wave.
/// </summary>
internal sealed partial class EnemyModule
{
    /// <summary>Whether any loaded profile authors a cap share. The mode-change hook returns before reading the game
    /// when none does, so an installation without the field pays one boolean read per mode change.</summary>
    internal bool CapCostAuthored { get; private set; }

    /// <summary>The share the life held before the game's own mode change ran, or 1 (a share already held) when this
    /// peer does not correct shares: the postfix corrects only a share the game allocated in this very call.</summary>
    internal float CapCostBefore(EnemyAI ai)
    {
        if (!CapCostAuthored || !Session.IsHost) return 1f;
        return ai.m_enemyAgent is { } enemy ? enemy.m_enemyCost : 1f;
    }

    /// <summary>
    /// Replaces the share the game allocated in this mode change with the type's authored share. A life that
    /// already held one, a mode change that allocated nothing, and a type with no authored share keep the game's
    /// own count. An authored share of zero frees what the game added and leaves `m_enemyCost` at zero, which is
    /// the game's own "holds no share" state: nothing is freed on death, and a later aggressive turn is allocated
    /// and corrected again.
    /// </summary>
    internal void AfterModeChange(EnemyAI ai, float before)
    {
        if (!CapCostAuthored || before > 0f || !Session.IsHost) return;
        if (ai.m_enemyAgent is not { } enemy) return;
        float allocated = enemy.m_enemyCost;
        if (!(allocated > 0f)) return;
        var profiles = _profiles;
        // The same block the game just read the enemy type from, so the share is the one of the type it counted.
        if (profiles == null || enemy.EnemyData is not { } data) return;
        uint typeId = data.persistentID;
        var profile = profiles.Resolve(typeId);
        if (profile.Fields?.CostTowardsCap is not float authored) return;
        var dimension = enemy.m_dimensionIndex;
        float delta = authored - allocated;
        if (delta != 0f) EnemyCostManager.AddCost(dimension, delta);
        enemy.m_enemyCost = authored;
        if (TypeRecordDue(RuntimeLogLevel.Info, EnemyLogCodes.CapCostApplied, typeId))
            WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.CapCostApplied, typeId, profile.Source, null, null, new[]
            {
                RuntimeLogValue.Of("vanilla", (double)allocated), RuntimeLogValue.Of("authored", (double)authored)
            });
        if (!Log.Logs(RuntimeLogLevel.Trace)) return;
        Log.Write(RuntimeLogLevel.Trace, EnemyLogCodes.CapCost, new RuntimeLogFacts
        {
            Entity = EntityKinds.Enemy + ":" + enemy.GlobalID.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Values = new[]
            {
                RuntimeLogValue.Of("enemyType", (long)typeId), EnemyLabel(typeId), RuntimeLogValue.Of("dimension", (long)(int)dimension),
                RuntimeLogValue.Of("vanilla", (double)allocated), RuntimeLogValue.Of("authored", (double)authored),
                RuntimeLogValue.Of("total", (double)EnemyCostManager.GetTotalCost(dimension)),
                RuntimeLogValue.Of("allowed", (double)EnemyCostManager.AllowedTotalCost)
            }
        });
    }
}
