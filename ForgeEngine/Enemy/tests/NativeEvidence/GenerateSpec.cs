using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mono.Cecil;

// Regenerates the frozen API specification instead of hand-editing it. The mechanical fields come from the
// interop assemblies the specification locks (declaring type, signature, assembly, visibility flags) and from
// the shipped plugin IL (forge hooks, forge callers); the reviewed fields of an existing entry (id, area,
// evidenceLevel, nativeCallPhase, nativeRva, dataEvidence) are carried over untouched, except that a pointer into the
// behaviour record is resolved again from the member's name, since it names a row by index. A game member the plugin
// starts using needs an entry in ObservedMembers below: an unlisted member fails the run instead of being frozen
// with a guessed area or a guessed data evidence pointer.
internal static class GenerateSpec
{
    private sealed record Observed(string Type, string Member, string Area)
    {
        // A read member is named as the plugin reads it (get_m_ai); the record names the native field it declares
        // (m_ai), which is the declaration's last word.
        internal string Field => Member.StartsWith("get_", StringComparison.Ordinal) ? Member[4..] : Member;
        // A member the plugin calls rather than patches or reads as a field is reviewed by the damage window
        // record, which freezes its declaration, RVA and call sites; the behaviour record does not describe it.
        // A called member no reviewed record describes is frozen at metadata level instead: the specification's
        // own signature-locked interop declares it and the entry claims nothing beyond that declaration, so it cites
        // no data file, carries no RVA and leaves the call phase unknown.
        internal string Evidence { get; init; } = BehaviourEvidence;
    }

    // The behaviour observation batch reads these members; the independent record of the hooks, the read fields
    // and their offsets is evidence/enemy-behavior-hooks.json, which these entries cite as data evidence. The
    // area follows the evidence group the type already belongs to (EnemyDetection/EnemyAI/EnemyBehaviour and the
    // target types it samples read as ai-perception, the locomotion state machine as movement-space, the scout
    // scream and registration as birthing-scout). readFields documents a native field declaration with its
    // offset; hookMembers documents a method body the evidence reviewed by RVA. Adding a member the plugin reads
    // means adding its area here; its evidence pointer is looked up from the type and member name, so a member
    // that record does not describe fails the run instead of freezing a pointer to nothing.
    private static readonly Observed[] ObservedMembers =
    {
        // T2 test adapters use these declarations directly. This pins signatures and IL callers only;
        // it does not claim a native body review or an executed game stimulus.
        // The projectile hooks (chaos fire, fire, collision, life end) and the target and course-node hooks are pinned the same way:
        // signatures and IL hook kinds only, no claim of a reviewed native body or an executed game stimulus.
        new("EAB_ProjectileShooter", "FireChaos", "attacks-projectiles") { Evidence = InteropEvidence },
        new("ProjectileTargeting", "OnFire", "attacks-projectiles") { Evidence = InteropEvidence },
        new("ProjectileTargeting", "OnDestroy", "attacks-projectiles") { Evidence = InteropEvidence },
        new("ProjectileTargeting", "get_m_endLifeTime", "attacks-projectiles") { Evidence = InteropEvidence },
        new("ProjectileBase", "Collision", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "OnDestroy", "identity") { Evidence = InteropEvidence },
        new("Enemies.EnemyAI", "InjectPropagatedTarget", "ai-perception") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "set_CourseNode", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyAllocator", "get_Current", "identity") { Evidence = InteropEvidence },
        new("Agents.Agent", "get_Position", "movement-space") { Evidence = InteropEvidence },
        new("Agents.Agent", "get_EyePosition", "movement-space") { Evidence = InteropEvidence },
        // The enemy.remove readback lists the course node's own enemy collection.
        new("AIGraph.AIG_CourseNode", "get_m_enemiesInNode", "movement-space") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageLimb", "BulletDamage", "damage-limbs-death") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageLimb", "SetBaseDamagable", "damage-limbs-death") { Evidence = InteropEvidence },
        new("LayerManager", "get_LAYER_ENEMY_DAMAGABLE", "damage-limbs-death") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyDataBlock>", "get_name", "identity") { Evidence = InteropEvidence },
        // Test resource lookup reads the native ID index without GetBlock's missing-ID log branch.
        new("GameData.GameDataBlockBase`1<T>", "get_s_blockByID", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<T>", "get_name", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<T>", "get_internalEnabled", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyBalancingDataBlock>", "GetBlock", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyBalancingDataBlock>", "get_name", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyBalancingDataBlock>", "get_internalEnabled", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyMovementDataBlock>", "GetBlock", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyMovementDataBlock>", "get_name", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyMovementDataBlock>", "get_internalEnabled", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyDetectionDataBlock>", "GetBlock", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyDetectionDataBlock>", "get_name", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyDetectionDataBlock>", "get_internalEnabled", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyBehaviorDataBlock>", "GetBlock", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyBehaviorDataBlock>", "get_name", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyBehaviorDataBlock>", "get_internalEnabled", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyPopulationDataBlock>", "get_name", "identity") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyPopulationDataBlock>", "get_internalEnabled", "identity") { Evidence = InteropEvidence },
        // T2 reads these exact game fields back after applying an authored block. The reviewed dump declares the
        // concrete fields/properties and the generic persistent-ID accessor; the hash-locked interop exposes the
        // closed-type accessors, which is the call shape the shipped plugin IL actually uses.
        new("LevelGeneration.Dimension", "TryGetCourseNodeFromPos", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_EnemyDetectionData", "spawn-requirements") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyDetectionDataBlock>", "get_persistentID", "spawn-requirements") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_EnemyBehaviorData", "spawn-requirements") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyBehaviorDataBlock>", "get_persistentID", "spawn-requirements") { Evidence = InteropEvidence },
        new("GameData.EnemyDetectionDataBlock", "get_weaponDetectionDistanceMin", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyDetectionDataBlock", "get_weaponDetectionDistanceMax", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyDetectionDataBlock", "get_detectionNodeDistanceMax", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyDetectionDataBlock", "get_movementDetectionDistance", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyDetectionDataBlock", "get_detectionBuildupSpeed", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyDetectionDataBlock", "get_detectionCooldownSpeed", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_GlobalAnimSpeedMulti", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_PathMoveAnimSpeedMulti", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_MoveSlowerWithinTargetDistance", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_RotationLerp", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_RotationLerpWhenNotMoving", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_BlendIntoAttackAnim", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_BlendIntoScreamAnim", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_PathMoveAnimDampTime", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_MoveSlowerWhenInPlayerRoom", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_AllowMovmentBlendOutOfPathMove", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyMovementDataBlock", "get_ForceDisableAnimator", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyBehaviorDataBlock", "get_MeleeAttackDistance", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.MinMaxValue", "get_Min", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.MinMaxValue", "get_Max", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyBehaviorDataBlock", "get_RangedAttackDistance", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyBehaviorDataBlock", "get_StrafeDistance", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.EnemyBehaviorDataBlock", "get_RushTargetDistance", "ai-perception") { Evidence = InteropEvidence },
        new("GameData.AbilityData", "get_Cooldown", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_RequireTagForDetection", "identity") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_WalkSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_RunSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_StrafeWalkSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_StrafeRunSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_ClimbSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_InPlayerRoomSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_JumpMoveSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_TentacleAttackWindUpLen", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_AllowMelee180", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_NeedsArmsToMove", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyDataBlock", "get_BehaviorDataId", "spawn-requirements") { Evidence = InteropEvidence },
        new("GameData.EnemyDataBlock", "get_DetectionDataId", "spawn-requirements") { Evidence = InteropEvidence },
        // Child spawning and S3 use these locked-interop declarations in the final Native DLL.
        // This records declaration/use coverage only; no native phase, RVA or game behaviour is claimed.
        new("AIGraph.AIG_CourseNode", "get_m_dimension", "birthing-scout") { Evidence = InteropEvidence },
        new("Agents.AgentTarget", "get_m_autoDetect", "ai-perception") { Evidence = InteropEvidence },
        new("Agents.AgentTarget", "get_m_globalID", "ai-perception") { Evidence = InteropEvidence },
        new("Agents.AgentTarget", "set_m_autoDetect", "ai-perception") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageBase", "get_DamageLimbsWithDestruction", "damage-limbs-death") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageBase", "set_DamageLimbs", "damage-limbs-death") { Evidence = InteropEvidence },
        new("EAB_Birthing", "Trigger", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "get_m_childrenAllowedSpawn", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "get_m_childrenCost", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "get_m_childrenPerBirth", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "get_m_groupData", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "get_m_groupDataBlock", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "set_m_groupData", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "set_m_groupDataBlock", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "set_m_pauseTimer", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_SpawnChildrenWave", "DoTrigger", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_SpawnChildrenWave", "get_waveData", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_SpawnChildrenWave/GenericEnemyWaveData", "get_WavePopulation", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_SpawnChildrenWave/GenericEnemyWaveData", "get_WaveSettings", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_SpawnChildrenWave/GenericEnemyWaveData", "set_SpawnDelay", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_SpawnChildrenWave/GenericEnemyWaveData", "set_WavePopulation", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_SpawnChildrenWave/GenericEnemyWaveData", "set_WaveSettings", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyAI", "get_m_behaviourData", "ai-perception") { Evidence = InteropEvidence },
        new("Enemies.EnemyAI", "get_m_lastAgentDetected", "ai-perception") { Evidence = InteropEvidence },
        new("Enemies.EnemyAI", "get_m_lastAgentTargetDetected", "ai-perception") { Evidence = InteropEvidence },
        new("Enemies.EnemyAbilities", "get_AbilityComps", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyAbilities/AbilityCompsWrapper", "get_Comps", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyBehaviourData", "get_Targets", "ai-perception") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroup", "OnGroupSpawnCompleted", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroup", "get_BirthersAbility", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroup", "get_CurrentCourseNode", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroup", "get_m_nextSpawn", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroup", "get_m_wavePopulationData", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroup", "set_m_nextSpawn", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroup", "set_m_wavePopulationData", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "AddEnemyData", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "GeneratePlacements", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "GetPopMultiplier", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "GetTotalEnemyCount", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "get_m_enemyAmount", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "get_m_enemyDataMode_Forced", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "get_m_enemyData_Forced", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "get_m_group", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyGroupScoreSpawnData", "set_m_popData", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_BirtherGiveBirth", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_m_animator", "movement-space") { Evidence = InteropEvidence },
        new("EnemyCostManager", "GetEnemyCost", "birthing-scout") { Evidence = InteropEvidence },
        new("EnemyCostManager", "GetPointsLeft", "birthing-scout") { Evidence = InteropEvidence },
        new("EnemyCostManager", "get_Current", "birthing-scout") { Evidence = InteropEvidence },
        new("EnemyCostManager", "get_m_enemyTypeCosts", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.AbilityData", "get_AbilityType", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.AbilityData", "get_Enabled", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyDataBlock", "get_AI_Abilities", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyGroupCompositionData", "get_Distribution", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyGroupCompositionData", "get_Role", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyGroupDataBlock", "get_Difficulty", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyGroupDataBlock", "get_Roles", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyPopulationDataBlock", "get_RoleDatas", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyRoleData", "get_Cost", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyRoleData", "get_Difficulty", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyRoleData", "get_Enemy", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyRoleData", "get_Role", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.EnemyRoleData", "get_Weight", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.ExpeditionData", "get_EnemyPopulation", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.ExpeditionInTierData", "get_Expedition", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyGroupDataBlock>", "GetBlock", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyGroupDataBlock>", "get_name", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyGroupDataBlock>", "get_persistentID", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyPopulationDataBlock>", "GetBlock", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyPopulationDataBlock>", "get_persistentID", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.SurvivalWavePopulationDataBlock>", "GetBlock", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.SurvivalWaveSettingsDataBlock>", "GetBlock", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.SurvivalWaveSettingsDataBlock>", "get_internalEnabled", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.SurvivalWaveSettingsDataBlock>", "get_name", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.SurvivalWaveSettingsDataBlock>", "get_persistentID", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.SurvivalWaveSettingsDataBlock>", "set_internalEnabled", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.SurvivalWaveSettingsDataBlock>", "set_name", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.SurvivalWaveSettingsDataBlock>", "set_persistentID", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWavePopulationDataBlock", ".ctor", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWavePopulationDataBlock", "set_WaveRoleBoss", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWavePopulationDataBlock", "set_WaveRoleMiniBoss", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWavePopulationDataBlock", "set_WaveRoleSpecial", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWavePopulationDataBlock", "set_WaveRoleStandard", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWavePopulationDataBlock", "set_WaveRoleWeakling", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", ".ctor", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_chanceToRandomizeSpawnDirectionPerGroup", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_chanceToRandomizeSpawnDirectionPerWave", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_filterType", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_overrideWaveSpawnType", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_pauseBeforeStart", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_pauseBetweenGroups", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_populationFilter", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_populationPointsMinPerGroup", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_populationPointsPerGroupEnd", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_populationPointsPerGroupStart", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_populationPointsPerWaveEnd", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_populationPointsPerWaveStart", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_populationRampOverTime", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_survivalWaveSpawnType", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_wavePauseMax", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_wavePauseMax_atCost", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_wavePauseMin", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_wavePauseMin_atCost", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_chanceToRandomizeSpawnDirectionPerGroup", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_chanceToRandomizeSpawnDirectionPerWave", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_filterType", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_overrideWaveSpawnType", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_pauseBeforeStart", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_pauseBetweenGroups", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_populationFilter", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_populationPointsMinPerGroup", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_populationPointsPerGroupEnd", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_populationPointsPerGroupStart", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_populationPointsPerWaveEnd", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_populationPointsPerWaveStart", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_populationPointsTotal", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_populationRampOverTime", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_survivalWaveSpawnType", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_wavePauseMax", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_wavePauseMax_atCost", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_wavePauseMin", "birthing-scout") { Evidence = InteropEvidence },
        new("GameData.SurvivalWaveSettingsDataBlock", "set_m_wavePauseMin_atCost", "birthing-scout") { Evidence = InteropEvidence },
        new("LevelGeneration.Dimension", "get_DimensionIndex", "birthing-scout") { Evidence = InteropEvidence },
        new("Mastermind/MastermindEvent", "StopEvent", "birthing-scout") { Evidence = InteropEvidence },
        new("RundownManager", "get_ActiveExpedition", "birthing-scout") { Evidence = InteropEvidence },
        new("SurvivalWave", "SetSettings", "birthing-scout") { Evidence = InteropEvidence },
        new("SurvivalWave", "get_m_populationDataID", "birthing-scout") { Evidence = InteropEvidence },

        new("GameData.GameDataBlockBase`1<GameData.EnemyDataBlock>", "GetBlock", "spawn-requirements") { Evidence = InteropEvidence },
        // A content generation changed in the lobby builds the prefab of each Forge enemy row again through the game's own
        // generator and swaps it into the prefab table; signatures and IL callers only.
        new("Enemies.EnemyPrefabManager", "get_Current", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyPrefabManager", "get_m_enemyPrefabs", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyPrefabManager", "GenerateEnemy", "appearance-animation") { Evidence = InteropEvidence },
        // The allocator's own spawn lookup and replication list take the rebuilt prefab in the old one's place (a new enemy is
        // appended the way EnemyAllocator.GetEnemyPrefabs appends it at setup).
        new("Enemies.EnemyAllocator", "get_m_enemyTypeLookup", "identity") { Evidence = InteropEvidence },
        new("Enemies.EnemyAllocator", "get_m_enemyReplicationManager", "cleanup-replication") { Evidence = InteropEvidence },
        new("SNetwork.SNet_ReplicationManager`2<Enemies.pEnemySpawnData,Enemies.EnemyReplicator>", "get_m_prefabLookup", "cleanup-replication") { Evidence = InteropEvidence },
        new("SNetwork.SNet_ReplicationManager`2<Enemies.pEnemySpawnData,Enemies.EnemyReplicator>", "get_m_prefabs", "cleanup-replication") { Evidence = InteropEvidence },
        new("SNetwork.SNet_ReplicationManager`2<Enemies.pEnemySpawnData,Enemies.EnemyReplicator>", "get_m_prefabsSyncVersion", "cleanup-replication") { Evidence = InteropEvidence },
        new("SNetwork.SNet_ReplicationManager`2<Enemies.pEnemySpawnData,Enemies.EnemyReplicator>", "AddPrefab", "cleanup-replication") { Evidence = InteropEvidence },
        new("Enemies.ES_Base", "get_m_stateEnum", "movement-space") { Evidence = InteropEvidence },
        new("ES_HitreactBase", "get_HitreactForbidden", "damage-limbs-death") { Evidence = InteropEvidence },
        // Eq-enemy actions: the melee and explosion damage entries, the hit-reaction timers the stagger readback
        // reads, the enemy seed and the synced attribute-modifier entries. Signatures and IL callers only; the native
        // bodies are reviewed in evidence/eq-enemy-native-entries.json and eq-enemy-second-round-native.json.
        new("ES_HitreactBase", "get_m_endTime", "damage-limbs-death") { Evidence = InteropEvidence },
        new("ES_HitreactBase", "get_m_retriggerTime", "damage-limbs-death") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_JumpLand", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_JumpInAir", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_JumpStart", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_die", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_hibernateDie", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_hurtBig", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_hurtSmall", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_attackFire", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_attackWindUp_NotLocalTarget", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_attackWindUp", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_releaseFromGlue", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_stuckInGlue", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_scream", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_hibernateWakeUp", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_hibernateDetectionStart", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_heartbeatPulse", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_hibernateIdle", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_run", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemySFXDataBlock", "set_SFX_ID_walk", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_EnemySFXData", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_Sound", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashMoveOnGround", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashJumpLand", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashJumpStart", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashClimbLadder", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHibernationIn", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashMelee180Sequences", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashMeleeSequences", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashMeleeWalkSequences", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashAbilityUse", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashAbilityFires", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHibernationWakeupTurns", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHibernationWakeups", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHearbeats", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashScreamTurns", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashScreams", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHitHeavys_Lt", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHitHeavys_Rt", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHitHeavys_Bwd", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHitHeavys_Fwd", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHitLights_Lt", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHitLights_Rt", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHitLights_Bwd", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_s_hashHitLights_Fwd", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_Anim", "appearance-animation") { Evidence = InteropEvidence },
        // The model assembly reads the shipped body prefab path from the enemy's model data.
        new("Enemies.EnemyAgent", "get_MainModelData", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.ModelData", "get_ModelFile", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_Seed", "identity") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageBase", "MeleeDamage", "damage-limbs-death") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageBase", "ExplosionDamage", "damage-limbs-death") { Evidence = InteropEvidence },
        new("ES_Hitreact", "ActivateState", "damage-limbs-death") { Evidence = InteropEvidence },
        new("ES_HitreactFlyer", "ActivateState", "damage-limbs-death") { Evidence = InteropEvidence },
        new("ES_HitreactFlyer", "CanHitreact", "damage-limbs-death") { Evidence = InteropEvidence },

        new("Enemies.EnemyDetection", "UpdateTargets", "ai-perception"),
        new("Enemies.EnemyDetection", "get_m_ai", "ai-perception"),
        new("Enemies.EnemyDetection", "get_m_biggestDetectionBuildup", "ai-perception"),
        new("Enemies.EnemyAI", "get_m_behaviour", "ai-perception"),
        new("Enemies.EnemyAI", "get_m_detection", "ai-perception"),
        new("Enemies.EnemyAI", "get_m_locomotion", "movement-space"),
        new("Enemies.EnemyBehaviour", "get_m_ai", "ai-perception"),
        new("Enemies.EnemyBehaviour", "get_m_currentStateName", "ai-perception"),
        new("Enemies.EnemyLocomotion", "get_ScoutScream", "movement-space"),
        new("Enemies.ES_ScoutScream", "get_m_state", "birthing-scout"),
        new("ES_ScoutDetection", "OnTargetRegistered", "birthing-scout"),
        new("ES_ScoutDetection", "get_m_owner", "birthing-scout"),
        new("Agents.AgentAI", "get_IsTargetValid", "ai-perception"),
        new("Agents.AgentAI", "get_Target", "ai-perception"),
        new("Agents.AgentTarget", "get_m_position", "ai-perception"),
        new("Agents.AgentTarget", "get_m_agent", "ai-perception"),
        // The one damage entry point a Forge hit is submitted through; the damage window record carries its
        // declaration, RVA and call sites.
        new("Dam_EnemyDamageBase", "BulletDamage", "damage-limbs-death") { Evidence = DamageWindowEvidence },
        // The members the native port uses beyond the original observation batch, grouped by the area
        // their records already belong to. Each one is looked up in enemy-behavior-hooks.json — a field read
        // by its declaration, a hook target or a called member by its reviewed body — so an area here without
        // a record there stops the run instead of freezing a pointer to nothing.
        // identity
        new("Agents.pAgent", "TryGet", "identity"),
        new("Agents.pEnemyAgent", "TryGet", "identity"),
        new("Enemies.EnemyAgent", "get_EnemyTaggedTimer", "identity"),
        new("Enemies.EnemyAgent", "get_IsTagged", "identity"),
        new("Enemies.EnemyAgent", "get_LastDamageInflictor", "identity"),
        new("Enemies.EnemyAgent", "get_MainModelGO", "identity"),
        new("Enemies.EnemySync", "OnDespawn", "identity"),
        new("Enemies.EnemySync", "OnSpawn", "identity"),
        new("ToolSyncManager", "DoTagEnemy", "identity"),
        // spawn-requirements
        new("Enemies.EnemyAI", "get_m_group", "spawn-requirements"),
        new("Enemies.EnemyGroup", "get_Data", "spawn-requirements"),
        new("Enemies.EnemyGroup", "get_GroupType", "spawn-requirements"),
        new("Enemies.EnemyGroup", "get_Members", "spawn-requirements"),
        new("Enemies.EnemyGroup", "RegisterMember", "spawn-requirements"),
        new("Enemies.EnemyGroup", "SurvivalSpawning", "spawn-requirements"),
        new("Enemies.EnemyGroup", "get_SurvivalWave", "spawn-requirements"),
        new("Enemies.EnemyGroup", "get_PatrolFrustration", "spawn-requirements"),
        new("Mastermind", "GameStateUpdate", "spawn-requirements"),
        new("Mastermind", "get_Current", "spawn-requirements"),
        new("Mastermind", "get_m_activeGroups", "spawn-requirements"),
        new("Mastermind/MastermindEvent", "get_EventID", "spawn-requirements"),
        new("SurvivalWave", "OnDespawn", "spawn-requirements"),
        new("SurvivalWave", "OnSpawn", "spawn-requirements"),
        new("SurvivalWave", "RegisterGroup", "spawn-requirements"),
        new("SurvivalWave", "SpawnGroup", "spawn-requirements"),
        new("SurvivalWave", "MastermindUpdate", "spawn-requirements"),
        new("SurvivalWave", "StopEvent", "spawn-requirements"),
        new("SurvivalWave", "get_m_settings", "spawn-requirements"),
        new("SurvivalWave", "get_m_populationPointsSpentTotal", "spawn-requirements"),
        new("GameData.SurvivalWaveSettingsDataBlock", "get_m_populationPointsTotal", "spawn-requirements") { Evidence = InteropEvidence },
        // GTFO-API's initialization event can already have fired before the plugin loads, so the runtime checks
        // the game's published load flag once before subscribing. The locked interop declaration proves only the
        // member shape; its event-order behaviour remains a game validation item.
        new("GameData.GameDataInit", "get_IsLoaded", "spawn-requirements") { Evidence = InteropEvidence },
        // The on-screen cap share (EnemyCapCostHooks.cs, EnemyModule.CapCost.cs): the allocation hook by its reviewed
        // body, the share and its dimension by their offsets, and the cost manager's add, sum and cap by their bodies.
        new("Enemies.EnemyAI", "ModeChange", "spawn-requirements"),
        new("Enemies.EnemyAgent", "get_m_enemyCost", "spawn-requirements"),
        new("Agents.Agent", "get_m_dimensionIndex", "spawn-requirements"),
        new("EnemyCostManager", "AddCost", "spawn-requirements"),
        new("EnemyCostManager", "GetTotalCost", "spawn-requirements"),
        new("EnemyCostManager", "get_AllowedTotalCost", "spawn-requirements"),
        // health
        new("Dam_EnemyDamageLimb", "get_m_health", "health"),
        new("Dam_EnemyDamageLimb", "get_m_healthMax", "health"),
        new("Dam_EnemyDamageLimb", "set_m_health", "health"),
        new("Dam_EnemyDamageLimb", "set_m_healthMax", "health"),
        // damage-limbs-death
        new("Dam_EnemyDamageBase", "AddToTotalGlueVolume", "damage-limbs-death"),
        new("Dam_EnemyDamageBase", "ClearGlue", "damage-limbs-death"),
        new("Dam_EnemyDamageBase", "ProcessReceivedDamage", "damage-limbs-death"),
        new("Dam_EnemyDamageBase", "RemoveGlueVolume", "damage-limbs-death"),
        new("Dam_EnemyDamageBase", "get_AttachedGlueVolume", "damage-limbs-death"),
        // The push handler submits one force through the receiver's force channel. `catalog/native/native-runtime.json`
        // carries the member on this type and `EnemyPushContract` names it as the entry the enemy binding of
        // `forge.action.combat.impulse` uses, but no behaviour or damage-window record reviews its body, so it is frozen by the locked interop
        // declaration alone — the same level the other receiver entries of this area already carry.
        new("Dam_EnemyDamageBase", "SendExplosionForce", "damage-limbs-death") { Evidence = InteropEvidence },
        // A model attachment hit part (Native/Content/EnemyModelAssemblies.cs) is one vanilla custom limb with the
        // EggSack break effect copied into its own CD_TriggerEffect. The bodies these entries reach are reviewed by the
        // model-limb windows in NativeHealth.cs; the entries freeze the locked interop declarations the plugin names.
        new("Dam_EnemyDamageLimb_Custom", "get_m_effectOnDestroy", "damage-limbs-death") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageLimb_Custom", "set_m_effectOnDestroy", "damage-limbs-death") { Evidence = InteropEvidence },
        new("CharacterDestruction.CD_TriggerEffect", "get_m_linkedEffect", "damage-limbs-death") { Evidence = InteropEvidence },
        new("CharacterDestruction.CD_TriggerEffect", "set_m_linkedEffect", "damage-limbs-death") { Evidence = InteropEvidence },
        new("CharacterDestruction.CD_TriggerEffect", "set_m_atTransform", "damage-limbs-death") { Evidence = InteropEvidence },
        new("CharacterDestruction.CD_TriggerEffect", "set_m_linkToTransform", "damage-limbs-death") { Evidence = InteropEvidence },
        new("CharacterDestruction.CD_TriggerEffect", "Trigger", "damage-limbs-death") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageLimb", "DestroyLimb", "damage-limbs-death"),
        new("Dam_EnemyDamageLimb", "SetLimbDamageType", "damage-limbs-death"),
        new("Dam_EnemyDamageLimb", "set_m_armorDamageMulti", "damage-limbs-death"),
        new("Dam_EnemyDamageLimb", "set_m_weakspotDamageMulti", "damage-limbs-death"),
        new("ES_HitreactBase", "ActivateState", "damage-limbs-death"),
        new("ES_HitreactBase", "CanHitreact", "damage-limbs-death"),
        new("ES_HitreactBase", "get_CurrentReactionType", "damage-limbs-death"),
        new("ES_HitreactBase", "set_HitreactForbidden", "damage-limbs-death"),
        new("Enemies.EnemyAgent", "OnDead", "damage-limbs-death"),
        new("Enemies.EnemyLocomotion", "get_Hitreact", "damage-limbs-death"),
        // ai-perception
        new("Enemies.EnemyBehaviour", "ChangeState", "ai-perception"),
        new("Enemies.EnemyDetection", "set_m_detectionBuildupSpeed", "ai-perception"),
        new("Enemies.EnemyDetection", "set_m_detectionCooldownSpeed", "ai-perception"),
        new("Enemies.EnemyDetection", "set_m_movementDetectionDistance", "ai-perception"),
        new("Enemies.SquidBossBehaviour", "ActivateWeakspotIfNeeded", "ai-perception"),
        new("Enemies.SquidBossBehaviour", "get_Phase", "ai-perception"),
        new("Enemies.SquidBossBehaviour", "set_Phase", "ai-perception"),
        // movement-space
        new("AIGraph.AIG_CourseNode", "get_m_zone", "movement-space"),
        new("Agents.Agent", "get_CourseNode", "movement-space"),
        new("Agents.Agent", "get_Forward", "movement-space"),
        new("Agents.AgentAI", "get_m_mode", "movement-space"),
        new("Agents.AgentAI", "get_m_navMeshAgent", "movement-space"),
        new("Enemies.ES_HibernateWakeUp", "ActivateState", "movement-space"),
        new("Enemies.EnemyAI", "get_NavmeshAgentGoal", "movement-space"),
        new("Enemies.EnemyAI", "set_NavmeshAgentGoal", "movement-space"),
        new("Enemies.EnemyAI", "NavmeshAgentWarp", "movement-space") { Evidence = InteropEvidence },
        // The melee trial's mid-walk placement reads where the warp put the navigation agent, then sets the body there.
        new("UnityEngine.AI.INavigation", "get_nextPosition", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "set_Position", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_HibernateWakeup", "movement-space"),
        new("LevelGeneration.LG_Layer", "get_m_type", "movement-space"),
        new("LevelGeneration.LG_Zone", "get_LocalIndex", "movement-space"),
        new("LevelGeneration.LG_Zone", "get_m_dimensionIndex", "movement-space"),
        new("LevelGeneration.LG_Zone", "get_m_layer", "movement-space"),
        new("UnityEngine.AI.INavigation", "get_speed", "movement-space"),
        new("UnityEngine.AI.INavigation", "set_speed", "movement-space"),
        // abilities
        new("AIGraph.AIG_CourseNode", "TryGetCourseNode", "abilities"),
        new("Enemies.EnemyAbilities", "GetAbility", "abilities"),
        new("Enemies.EnemyAbilities", "get_AllComps", "abilities"),
        new("EnemyAbility", "get_m_abilityType", "abilities"),
        new("NM_NoiseData", ".ctor", "abilities"),
        new("NoiseManager", "MakeNoise", "abilities"),
        // birthing-scout
        new("EAB_Birthing", "set_m_childrenMax", "birthing-scout"),
        new("EAB_Birthing", "set_m_childrenPerBirth", "birthing-scout"),
        new("EAB_Birthing", "set_m_childrenPerBirthMin", "birthing-scout"),
        new("EAB_Birthing", "set_m_maxDelayUntilNextBirth", "birthing-scout"),
        new("EAB_Birthing", "set_m_minDelayUntilNextBirth", "birthing-scout"),
        // attacks-projectiles
        new("Enemies.ES_Base", "IsChargingAttack", "attacks-projectiles"),
        new("Enemies.ES_Base", "IsPerformingAttack", "attacks-projectiles"),
        new("Enemies.ES_Base", "get_m_ai", "attacks-projectiles"),
        new("Enemies.ES_Base", "get_m_enemyAgent", "attacks-projectiles"),
        new("Enemies.ES_EnemyAttackBase", "RecieveAttackStart", "attacks-projectiles"),
        new("Enemies.ES_EnemyAttackBase", "get_m_attackTarget", "attacks-projectiles"),
        new("Enemies.ES_EnemyAttackBase", "get_m_lastAttackIndex", "attacks-projectiles"),
        new("Enemies.EnemyLocomotion", "get_ShooterAttack", "attacks-projectiles"),
        new("Enemies.EnemyLocomotion", "get_StrikerAttack", "attacks-projectiles"),
        new("Enemies.EnemyLocomotion", "get_TankAttack", "attacks-projectiles"),
        new("Enemies.EnemyLocomotion", "get_TankMultiTargetAttack", "attacks-projectiles"),
        new("ProjectileManager", "GetNextSyncID", "attacks-projectiles"),
        new("ProjectileManager", "WantToSpawnGlueOnEnemyAgent", "attacks-projectiles"),
        // appearance-animation
        new("EV_Sphere", ".ctor", "appearance-animation"),
        new("EV_Sphere", "set_maxRadius", "appearance-animation"),
        new("EV_Sphere", "set_minRadius", "appearance-animation"),
        new("EV_Sphere", "set_position", "appearance-animation"),
        new("EffectVolume", "set_contents", "appearance-animation"),
        new("EffectVolume", "set_effectOrder", "appearance-animation"),
        new("EffectVolume", "set_invert", "appearance-animation"),
        new("EffectVolume", "set_modification", "appearance-animation"),
        new("EffectVolume", "set_modificationScale", "appearance-animation"),
        new("EffectVolumeManager", "RegisterVolume", "appearance-animation"),
        new("EffectVolumeManager", "UnregisterVolume", "appearance-animation"),
        new("FogSphereAllocator", ".ctor", "appearance-animation"),
        new("FogSphereAllocator", "Deallocate", "appearance-animation"),
        new("FogSphereAllocator", "SetDensity", "appearance-animation"),
        new("FogSphereAllocator", "SetPositionRange", "appearance-animation"),
        new("FogSphereAllocator", "SetRadiance", "appearance-animation"),
        new("FogSphereAllocator", "TryAllocate", "appearance-animation"),
        // cleanup-replication
        new("Enemies.EnemyAgent", "get_Sync", "cleanup-replication"),
        new("Enemies.EnemySync", "get_Replicator", "cleanup-replication"),
        new("SNetwork.IReplicator", "Despawn", "cleanup-replication"),
        new("SNetwork.SNet", "get_SessionHub", "cleanup-replication"),
        new("SNetwork.SNet_Player", "get_Lookup", "cleanup-replication"),
        new("SNetwork.SNet_SessionHub", "get_PlayersInSession", "cleanup-replication"),
        // The profile appliers' own writes and reads, the zone/area reads the entity rows answer with, and the
        // appearance sources' prefab lists. Each one is declared by the hash-locked interop alone: no reviewed
        // behaviour or damage-window record describes its body or its field offset, so it is frozen at metadata
        // level and cites no data file.
        new("AIGraph.AIG_CourseNode", "get_m_area", "movement-space") { Evidence = InteropEvidence },
        new("LevelGeneration.LG_Zone", "get_m_areas", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "set_RequireTagForDetection", "identity") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "set_m_enemyCost", "spawn-requirements") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_Hibernate", "movement-space") { Evidence = InteropEvidence },
        // The stuck state the glue fact reads its from-hibernate flag from: the movement machine's own state
        // instance and the packet that instance received. Both are read through the interop's own accessors, and
        // no reviewed behaviour record describes the state machine's body, so they are frozen at metadata level.
        new("Enemies.ES_Hibernate", "set_m_detectingColorVec", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.ES_Hibernate", "set_m_heartbeatColorVec", "movement-space") { Evidence = InteropEvidence },
        // The wake-up state's own self-wake-up colour: the one per-instance member of that type the glow row
        // writes, reached through the locomotion's `HibernateWakeup`. The propagated-wake-up colour beside it is a
        // static; the look swaps it around one enemy's wake-up instead (listed with the look below).
        new("Enemies.EnemyLocomotion", "get_HibernateWakeup", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.ES_HibernateWakeUp", "set_m_selfWakeupColor", "movement-space") { Evidence = InteropEvidence },
        // The multi-tentacle attack's glow colour. The component is found by narrowing the ability table, because
        // the build's `AgentAbility` vocabulary has no member for it.
        new("EAB_MovingEnemyTentacleMultiple", "set_m_attackGlowColor", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.ES_ShooterAttack", "set_m_attackGlowColor_End", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_AnimHandle", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "set_AnimHandle", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_AnimHandleName", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "InitController", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_WalkSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_RunSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_StrafeWalkSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_StrafeRunSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_ClimbSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_InPlayerRoomSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_JumpMoveSpeed", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_TentacleAttackWindUpLen", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_AllowMelee180", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_NeedsArmsToMove", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_MeleeAttackFwd", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_MeleeAttackFwd", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "get_MeleeAttackBwd", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion/AnimatorControllerHandle", "set_MeleeAttackBwd", "movement-space") { Evidence = InteropEvidence },
        new("UnityEngine.AI.NavMeshAgentExtention/NavMeshAgentProxy", "get_m_agent", "movement-space") { Evidence = InteropEvidence },
        // Read-only authoring diagnostics: Unity's actual path state, never inferred from a goal write.
        new("UnityEngine.AI.NavMeshAgent", "get_pathPending", "movement-space") { Evidence = InteropEvidence },
        new("UnityEngine.AI.NavMeshAgent", "get_pathStatus", "movement-space") { Evidence = InteropEvidence },
        new("UnityEngine.AI.NavMeshAgent", "get_hasPath", "movement-space") { Evidence = InteropEvidence },
        new("UnityEngine.AI.NavMeshAgent", "get_remainingDistance", "movement-space") { Evidence = InteropEvidence },
        // AIModule now joins the locked input closure; keep its pre-existing navigation calls explicit too.
        new("UnityEngine.AI.NavMeshAgent", "set_stoppingDistance", "movement-space") { Evidence = InteropEvidence },
        new("UnityEngine.AI.NavMesh", "Raycast", "movement-space") { Evidence = InteropEvidence },
        new("UnityEngine.AI.NavMesh", "SamplePosition", "movement-space") { Evidence = InteropEvidence },
        new("UnityEngine.AI.NavMeshHit", "get_position", "movement-space") { Evidence = InteropEvidence },
        new("UnityEngine.AI.INavigation", "get_stoppingDistance", "movement-space") { Evidence = InteropEvidence },
        new("AirNavigation.FlyingAirGraphAgent", "set_m_stoppingDistance", "movement-space") { Evidence = InteropEvidence },
        new("CB_CustomPrefab", "get_prefabs", "appearance-animation") { Evidence = InteropEvidence },
        new("sCB_CustomPrefabDef", "get_Prefab", "appearance-animation") { Evidence = InteropEvidence },
        // The look's per-life half and its prefab material pass (EnemyModuleLook*.cs, EnemyLookHooks.cs): the
        // material references the pass rebinds, the default glow a pulse lerps from, the gates EEC's pulse routine
        // reads, and the tag marker the text is written to. No reviewed
        // behaviour record describes these bodies or offsets, so each is frozen by the locked interop declaration.
        new("Enemies.EnemyAgent", "get_MaterialHandler", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "get_m_materialRefs", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "CharacterBuilderParse", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "get_m_supportsEllipsoids", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "set_m_supportsEllipsoids", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "get_m_supportsInnerGlow", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "set_m_supportsInnerGlow", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "get_m_supportsBlobs", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "set_m_supportsBlobs", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "get_m_supportsGlue", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "set_m_supportsGlue", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "set_m_materialRefs", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", ".ctor", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", "AddFeatureSupport", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", "get_m_renderers", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", "set_m_renderers", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", "get_m_featureSupport", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", "set_m_featureSupport", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", "set_m_hasBeenMadeUnique", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "InstantiationInitialize", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "get_m_isMaterialRefsUnique", "appearance-animation") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "set_m_isMaterialRefsUnique", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "ForceState", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "ChangeState", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_Knockdown", "movement-space") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageLimb", "get_m_type", "damage-limbs-death") { Evidence = InteropEvidence },
        new("CharacterMaterialHandler", "get_m_defaultGlowColor", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", "get_m_material", "appearance-animation") { Evidence = InteropEvidence },
        new("MaterialRef", "set_m_material", "appearance-animation") { Evidence = InteropEvidence },
        new("Agents.AgentAI", "get_Mode", "ai-perception") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_IsHibernationDetecting", "ai-perception") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "IsAttacking", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_ScoutDetection", "birthing-scout") { Evidence = InteropEvidence },
        new("ES_ScoutDetection", "get_m_antennaDetection", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "get_m_wantsToHaveTendrils", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_m_tagMarker", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "SyncPlaceNavMarkerTag", "appearance-animation") { Evidence = InteropEvidence },
        // The rest of the marker: the sprite part the icon swap and the size write, the game's own blink entry, the
        // balancing block whose tag time caps the blink window, and the scanner sound index `AllowMarkingOnHibernate`
        // clears at spawn.
        new("NavMarker", "get_m_enemySubObj", "appearance-animation") { Evidence = InteropEvidence },
        new("CoroutineManager", "BlinkIn", "appearance-animation") { Evidence = InteropEvidence },
        new("GameData.EnemyBalancingDataBlock", "get_TagTime", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "get_ScannerData", "appearance-animation") { Evidence = InteropEvidence },
        new("Gear.EnemyScannerDataObject", "set_m_soundIndex", "appearance-animation") { Evidence = InteropEvidence },
        // The propagated wake-up: the hooked wake-up entry and the static colour it hands the glow, swapped for one
        // enemy's call and put back. The state's owner (`ES_Base.get_m_enemyAgent`) and the balancing block above
        // (`EnemyAgent.get_EnemyBalancingData`) are already frozen by their first users.
        new("Enemies.ES_HibernateWakeUp", "DoWakeup", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.ES_HibernateWakeUp", "get_m_propagatedWakeupColor", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.ES_HibernateWakeUp", "set_m_propagatedWakeupColor", "appearance-animation") { Evidence = InteropEvidence },
        // The enemy action triggers and their output gaps (contract §4.5 B2/B3, EnemyActionHooks.cs and
        // EnemyModule.ActionFacts.cs): the patched members by their reviewed body, the read fields by their offset.
        new("Enemies.ES_StrikerMelee", "RecieveMeleeAttack", "attacks-projectiles"),
        new("Enemies.ES_Scream", "OnStateShouldBeActivated", "ai-perception"),
        new("Enemies.ES_ScreamFlyer", "ActivateState", "ai-perception"),
        new("Enemies.ES_Hibernate", "MasterStartDetection", "ai-perception"),
        new("Enemies.ES_Hibernate", "MasterEndDetection", "ai-perception"),
        new("EAB_ProjectileShooter", "FireAtAgent", "attacks-projectiles"),
        new("EAB_FogSphere", "DoTrigger", "attacks-projectiles"),
        new("EAB_Birthing", "OnGaveBirth", "birthing-scout"),
        new("Enemies.EB_StateWithDuration", "ChangeToThisState", "attacks-projectiles"),
        new("Enemies.EB_SpitOut_Pouncer", "get_m_spitOutPosition", "attacks-projectiles"),
        new("Dam_PlayerDamageBase", "ReceiveTentacleGrabStop", "attacks-projectiles"),
        new("Dam_EnemyDamageBase", "ReceiveBulletDamage", "damage-limbs-death"),
        new("Dam_EnemyDamageBase", "ReceiveExplosionDamage", "damage-limbs-death"),
        new("Dam_EnemyDamageBase", "ReceiveMeleeDamage", "damage-limbs-death"),
        new("Dam_EnemyDamageBase", "ReceivePushDamage", "damage-limbs-death"),
        new("Dam_EnemyDamageBase", "ReceiveFireDamage", "damage-limbs-death"),
        new("Dam_PlayerDamageBase", "get_GrabbedByTank", "attacks-projectiles"),
        new("Dam_PlayerDamageBase", "get_GrabSource", "attacks-projectiles"),
        new("Enemies.EB_StateBase", "get_m_ai", "attacks-projectiles"),
        new("Enemies.ES_Hibernate", "get_m_lastDetectPos", "ai-perception"),
        new("EnemyAbility", "get_m_owner", "abilities"),
        new("Enemies.ES_HibernateWakeUp", "get_m_isPropagatedWakeup", "movement-space"),
        new("Enemies.ES_ScoutScream", "get_m_agentTarget", "birthing-scout"),
        new("Enemies.ES_ShooterAttack", "OnAttackWindUp", "attacks-projectiles"),
        new("Enemies.ES_ShooterAttack", "OnAttackPerform", "attacks-projectiles"),
        new("Enemies.ES_ShooterAttackFlyer", "OnAttackWindUp", "attacks-projectiles"),
        new("Enemies.ES_ShooterAttackFlyer", "OnAttackPerform", "attacks-projectiles"),
        new("Enemies.ES_StrikerAttack", "OnAttackWindUp", "attacks-projectiles"),
        new("Enemies.ES_StrikerAttack", "OnAttackPerform", "attacks-projectiles"),
        new("Enemies.ES_TankAttack", "OnAttackWindUp", "attacks-projectiles"),
        new("Enemies.ES_TankAttack", "OnAttackPerform", "attacks-projectiles"),
        new("Enemies.ES_TankMultiTargetAttack", "OnAttackWindUp", "attacks-projectiles"),
        new("Enemies.ES_TankMultiTargetAttack", "OnAttackPerform", "attacks-projectiles"),
        new("Enemies.EB_Hibernating", "OnNoiseDetected", "ai-perception"),
        new("Enemies.EB_Patrolling", "OnNoiseDetected", "ai-perception"),
        new("EAB_Alarm", "Trigger", "abilities"),
        new("EAB_Alarm", "Update", "abilities"),
        new("EAB_Alarm", "get_m_chargingUp", "abilities"),
        new("EAB_Alarm", "get_m_alarmTriggered", "abilities"),
        new("Enemies.EB_Consume_Pouncer", "ActivateState", "attacks-projectiles"),
        new("PLOC_GrabbedByTank", "IncomingTankTentacleGrab", "attacks-projectiles"),
        new("Agents.AgentTarget", "get_m_detectionType", "ai-perception"),
        new("NM_NoiseData", "get_noiseMaker", "ai-perception"),
        new("NM_NoiseData", "get_position", "ai-perception"),
        new("NM_NoiseData", "get_type", "ai-perception"),
        new("PLOC_Base", "get_m_owner", "attacks-projectiles"),
        // The wake rules (EnemyWakeHooks.cs, EnemyModule.Wake.cs): the hibernation noise window, the damage path's
        // detection and the hit-reaction gate by their reviewed bodies, and the three target fields the window
        // leaves for its caller by their offsets.
        new("Enemies.EnemyDetection", "DetectOnNoiseDistance_Conditional_AnimatedWindow", "ai-perception"),
        new("Enemies.EnemyAI", "SetDetectedAgent", "ai-perception"),
        new("ES_Hitreact", "CanHitreact", "damage-limbs-death"),
        new("Agents.AgentTarget", "get_m_distance", "ai-perception"),
        new("Agents.AgentTarget", "get_m_isOccluded", "ai-perception"),
        new("Agents.AgentTarget", "get_noiseType", "ai-perception"),
        // The per-ability fields (Native/Behavior/EnemyModule.AbilityFields.cs, EnemyAbilityFieldHooks.cs): the scout feeler's
        // layout and tendrils, the scout scream's static colours and immortality, the birthing and snatcher data
        // written at spawn, the released fog sphere's handler, and the scanner colour's state entries.
        new("Enemies.ScoutAntennaDetection", "Setup", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "SetState", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "get_m_owner", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "get_m_dirAngStep", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "set_m_dirAngStep", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "get_m_dirAngOffset", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "set_m_dirAngOffset", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "get_m_randomAngleIndex", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntennaDetection", "set_m_timerWaitOut", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntenna", "Init", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntenna", "set_m_maxDistance", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntenna", "set_m_stepDistance", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntenna", "set_m_colorDefault", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntenna", "set_m_colorDetection", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntenna", "get_m_propertyBlock", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntenna", "get_m_renderer", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ScoutAntenna", "get_s_ColorID", "birthing-scout") { Evidence = InteropEvidence },
        new("UnityEngine.MaterialPropertyBlock", "SetColor", "birthing-scout") { Evidence = InteropEvidence },
        new("UnityEngine.Renderer", "SetPropertyBlock", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ES_ScoutScream", "CommonUpdate", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ES_ScoutScream", "ActivateState", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ES_ScoutScream", "RecieveScream", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ES_ScoutScream", "get_s_screamChargeupColor", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ES_ScoutScream", "set_s_screamChargeupColor", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ES_ScoutScream", "get_s_screamPopColor", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.ES_ScoutScream", "set_s_screamPopColor", "birthing-scout") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageBase", "set_IsImortal", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "set_m_childrenCost", "birthing-scout") { Evidence = InteropEvidence },
        new("EAB_Birthing", "set_m_childrenAllowedSpawn", "birthing-scout") { Evidence = InteropEvidence },
        new("Enemies.PouncerBehaviour", "get_m_data", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerBehaviour", "set_m_data", "attacks-projectiles") { Evidence = InteropEvidence },
        new("UnityEngine.Object", "Instantiate", "attacks-projectiles") { Evidence = InteropEvidence },
        new("UnityEngine.Object", "set_hideFlags", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "get_m_DashCooldown", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_m_DashCooldown", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_DashMaxTime", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_ChargeDuration", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_ConsumeDuration", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_DashEndPhaseDistance", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_ChargeStaggerDamageThreshold", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_DashStaggerDamageThreshold", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_StaggerDuration", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_FaceToTargetDuringProwlingDistance", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_StaggerLeadsToAfterHeld", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_EnableOffMeshLinkDash", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_LOSRequiredForDash", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_DashStaggerUsesHeavyHitreaction", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "get_DashMovementModifier", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_DashMovementModifier", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "get_HeldStateData", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_HeldStateData", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "get_DamageShapeData", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.PouncerDataContainer", "set_DamageShapeData", "attacks-projectiles") { Evidence = InteropEvidence },
        new("EAB_FogSphere", "get_m_activeFogSpheres", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_colorMin", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_colorMax", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_intensityMin", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_intensityMax", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_rangeMin", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_rangeMax", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_densityMin", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_densityMax", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_densityAmountMin", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_densityAmountMax", "attacks-projectiles") { Evidence = InteropEvidence },
        new("FogSphereHandler", "set_m_totalLength", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Enemies.EnemyAgent", "set_ScannerColor", "appearance-animation") { Evidence = InteropEvidence },
        new("ES_PathMove", "CommonEnter", "appearance-animation") { Evidence = InteropEvidence },
        new("ES_PathMoveFlyer", "CommonEnter", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.ES_Hibernate", "CommonEnter", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.ES_Hibernate", "CommonExit", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.ES_Hibernate", "DoStartDetection", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.ES_Hibernate", "DoEndDetection", "appearance-animation") { Evidence = InteropEvidence },
        new("Enemies.ES_HibernateWakeUp", "Enter", "appearance-animation") { Evidence = InteropEvidence },
        new("ES_ScoutDetection", "CommonEnter", "appearance-animation") { Evidence = InteropEvidence },
        // Test stimuli and presentation readers added with the behavior test service; metadata-level entries only.
        new("Agents.AgentAI", "SetTarget", "ai-perception") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageBase", "get_m_glueTargets", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Dam_EnemyDamageBase", "GetGlueTarget", "attacks-projectiles") { Evidence = InteropEvidence },
        new("Dam_PlayerDamageBase", "get_m_nextRegen", "health") { Evidence = InteropEvidence },
        new("Dam_PlayerDamageBase", "set_m_nextRegen", "health") { Evidence = InteropEvidence },
        new("Dam_SyncedDamageBase", "MeleeDamage", "damage-limbs-death") { Evidence = InteropEvidence },
        new("Dam_SyncedDamageBase", "TentacleAttackDamage", "damage-limbs-death") { Evidence = InteropEvidence },
        new("EffectVolume", "get_contents", "movement-space") { Evidence = InteropEvidence },
        new("EffectVolume", "get_effectOrder", "movement-space") { Evidence = InteropEvidence },
        new("EffectVolume", "get_invert", "movement-space") { Evidence = InteropEvidence },
        new("EffectVolume", "get_modification", "movement-space") { Evidence = InteropEvidence },
        new("EffectVolume", "get_modificationScale", "movement-space") { Evidence = InteropEvidence },
        new("EffectVolumeManager", "get_volumes", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_KnockdownRecover", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.EnemyLocomotion", "get_Scream", "movement-space") { Evidence = InteropEvidence },
        new("Enemies.ES_ScreamBase", "ActivateState", "movement-space") { Evidence = InteropEvidence },
        new("EV_Sphere", "get_maxRadius", "movement-space") { Evidence = InteropEvidence },
        new("EV_Sphere", "get_minRadius", "movement-space") { Evidence = InteropEvidence },
        new("EV_Sphere", "get_position", "movement-space") { Evidence = InteropEvidence },
        new("FogSphereAllocator", "get_IsAllocated", "movement-space") { Evidence = InteropEvidence },
        new("FogSphereAllocator", "get_m_sphere", "movement-space") { Evidence = InteropEvidence },
        new("PreLitVolume/FogSphere", "get_m_data", "movement-space") { Evidence = InteropEvidence },
        new("GameData.EnemyBalancingDataBlock", "get_MeleeAttackDamage", "spawn-requirements") { Evidence = InteropEvidence },
        new("GameData.EnemyBalancingDataBlock", "get_TentacleAttackDamage", "spawn-requirements") { Evidence = InteropEvidence },
        new("GameData.GameDataBlockBase`1<GameData.EnemyDataBlock>", "get_s_blockByID", "identity") { Evidence = InteropEvidence },
        new("IGlueTarget", "get_GlueCapacity", "attacks-projectiles") { Evidence = InteropEvidence },
        new("IGlueTarget", "get_GlueTargetEnemyAgent", "attacks-projectiles") { Evidence = InteropEvidence },
        new("IGlueTarget", "get_GlueTargetSubIndex", "attacks-projectiles") { Evidence = InteropEvidence },
        new("IGlueTarget", "get_IgnoreGlue", "attacks-projectiles") { Evidence = InteropEvidence },
        new("NavMarker", "get_IsVisible", "appearance-animation") { Evidence = InteropEvidence },
        new("NavMarker", "get_m_trackingObj", "appearance-animation") { Evidence = InteropEvidence },
        new("NavMarkerComponent", "get_m_sprites", "appearance-animation") { Evidence = InteropEvidence },
        new("Player.PlayerAgent", "get_Damage", "identity") { Evidence = InteropEvidence },
        new("Player.PlayerAgent", "get_Owner", "identity") { Evidence = InteropEvidence },
        new("Player.PlayerManager", "get_PlayerAgentsInLevel", "identity") { Evidence = InteropEvidence },
        new("SNetwork.SNet_Player", "get_IsBot", "identity") { Evidence = InteropEvidence },
        new("ToolSyncManager", "WantToTagEnemy", "identity") { Evidence = InteropEvidence },
    };

    private const string BehaviourEvidence = "enemy-behavior-hooks.json";
    private const string DamageWindowEvidence = "e10-damage-window.json";
    private const string InteropEvidence = "locked-interop-declaration";
    private static readonly JsonSerializerOptions Json = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string[] args)
    {
        try { return Generate(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"GENERATE ERROR: {error.GetType().Name}: {error.Message}");
            return 2;
        }
    }

    private static int Generate(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: NativeEvidence --generate <BepInEx> <ForgeEngine.dll> <frozen spec.json> <output.json>");
            return 2;
        }
        string bep = Path.GetFullPath(args[0]);
        string specPath = Path.GetFullPath(args[2]);
        string output = Path.GetFullPath(args[3]);
        string specDirectory = Path.GetDirectoryName(specPath)!;
        var root = JsonNode.Parse(File.ReadAllText(specPath))?.AsObject()
            ?? throw new InvalidDataException("Empty specification.");
        if (root["schemaVersion"]!.GetValue<string>() != ForgeCore.FormatVersion.Current)
            throw new InvalidDataException("Only schema 2 specifications can be regenerated.");

        // Every entry this generator can add cites a record of the behaviour observation batch, so the pointers are
        // resolved against that record before anything is written: a member the record does not describe exactly
        // once has no traceable source and stops the run.
        var evidence = ReviewedEvidence(specDirectory);

        // The specification's own input locks are verified, never rewritten: a drifted interop means the frozen
        // metadata describes another build, so the run has to fail instead of silently rebasing the evidence.
        var contracts = new List<AssemblyDefinition>();
        var interopFiles = new List<string>();
        var declaringFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root["assemblies"]!.AsArray())
        {
            string file = entry!["file"]!.GetValue<string>();
            string path = Path.Combine(bep, "interop", file);
            string signature = ForgeTestInputs.InteropSignature.Sha256(path);
            if (signature != entry["signatureSha256"]!.GetValue<string>())
                throw new InvalidDataException($"Interop signature drift for {file}: {signature}.");
            var assembly = AssemblyDefinition.ReadAssembly(path);
            contracts.Add(assembly);
            interopFiles.Add(file);
            foreach (var type in assembly.MainModule.Types.SelectMany(ForgeIl.Walk))
                declaringFile.TryAdd(Open(type.FullName), file);
        }

        var methods = root["methods"]!.AsArray();
        var usage = ForgeIl.Read(Path.GetFullPath(args[1]), interopFiles.ToHashSet(StringComparer.Ordinal));

        // Reviewed rows keep their order, id, area, evidence level and data evidence; only the IL-derived use
        // lists are refreshed, and a row whose frozen metadata no longer matches the locked interop fails.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var owners = new Dictionary<(string Type, string Name), int>();
        var refreshed = new List<string>();
        foreach (var row in methods)
        {
            var frozen = row!.AsObject();
            string typeName = frozen["type"]!.GetValue<string>();
            string signature = frozen["signature"]!.GetValue<string>();
            if (!ids.Add(frozen["id"]!.GetValue<string>()) || !signatures.Add(signature))
                throw new InvalidDataException("Duplicate frozen id or signature: " + signature);
            var declaration = Declared(contracts, typeName, signature)
                ?? throw new InvalidDataException("Frozen entry no longer resolves in the locked interop: " + signature);
            foreach (string flag in new[] { "isStatic", "isVirtual", "isPublic" })
            {
                bool actual = flag switch
                {
                    "isStatic" => declaration.IsStatic,
                    "isVirtual" => declaration.IsVirtual,
                    _ => declaration.IsPublic,
                };
                if (frozen[flag]!.GetValue<bool>() != actual)
                    throw new InvalidDataException($"{flag} drift on {signature}: locked interop says {actual}.");
            }
            string methodName = Member(signature);
            var hooks = usage.Hooks.TryGetValue((typeName, methodName), out var hook)
                ? hook.Kinds : new SortedSet<string>(StringComparer.Ordinal);
            var callers = usage.Calls.TryGetValue(signature, out var call)
                ? call.Callers : new SortedSet<string>(StringComparer.Ordinal);
            if (!Strings(frozen["forgeHooks"]!).SequenceEqual(hooks) || !Strings(frozen["forgeCallers"]!).SequenceEqual(callers))
                refreshed.Add($"{frozen["id"]!.GetValue<string>()}: hooks [{string.Join(",", hooks)}], callers [{string.Join(",", callers)}]");
            frozen["forgeHooks"] = Array(hooks);
            frozen["forgeCallers"] = Array(callers);
            // A pointer into the behaviour record names a row by index, so it is resolved again from the member's
            // name: a row added to or dropped from that record must not leave the entry citing its neighbour.
            var cited = frozen["dataEvidence"]!.AsArray();
            if (cited.Count == 1 && (string?)cited[0]!["file"] == BehaviourEvidence
                && evidence.TryGetValue((typeName, methodName), out var source) && source.File == BehaviourEvidence)
            {
                string pointer = source.Pointer;
                if ((string?)cited[0]!["pointer"] != pointer)
                    refreshed.Add($"{frozen["id"]!.GetValue<string>()}: pointer {(string?)cited[0]!["pointer"]} -> {pointer}");
                cited[0]!["pointer"] = pointer;
            }
            if (hooks.Count > 0) owners[(typeName, methodName)] = owners.GetValueOrDefault((typeName, methodName)) + 1;
        }

        // A hook the plugin adds needs exactly one frozen owner; a game API it starts calling needs one entry.
        var added = new List<(string Area, JsonObject Row)>();
        foreach (var (key, hook) in usage.Hooks)
        {
            if (owners.GetValueOrDefault(key) > 1)
                throw new InvalidDataException($"{key.Type}::{key.Name} is claimed by {owners[key]} frozen entries.");
            if (owners.ContainsKey(key)) continue;
            var row = NewRow(contracts, declaringFile, hook.Type, key.Name,
                Required(contracts, hook.Type, key.Name).FullName, hook.ScopeFile);
            row["forgeHooks"] = Array(hook.Kinds);
            if (usage.Calls.TryGetValue(row["signature"]!.GetValue<string>(), out var called))
                row["forgeCallers"] = Array(called.Callers);
            Add(added, ids, signatures, evidence, row);
        }
        foreach (var (signature, call) in usage.Calls)
        {
            if (signatures.Contains(signature)) continue;
            var row = NewRow(contracts, declaringFile, call.DeclaringType, Member(signature), signature, call.ScopeFile);
            row["forgeCallers"] = Array(call.Callers);
            if (usage.Hooks.TryGetValue((call.DeclaringType, Member(signature)), out var hook)) row["forgeHooks"] = Array(hook.Kinds);
            Add(added, ids, signatures, evidence, row);
        }

        // New entries join the end of their area group, which is how the specification already orders the batch
        // that added them; an area the specification does not have yet is appended as its own block.
        var groups = methods.Select(m => m!["area"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToList();
        foreach (string area in added.Select(a => a.Area).Distinct(StringComparer.Ordinal))
            if (!groups.Contains(area)) groups.Add(area);
        var ordered = new List<JsonNode?>();
        foreach (string area in groups)
        {
            ordered.AddRange(methods.Where(m => m!["area"]!.GetValue<string>() == area));
            ordered.AddRange(added.Where(a => a.Area == area)
                .OrderBy(a => a.Row["id"]!.GetValue<string>(), StringComparer.Ordinal).Select(a => (JsonNode?)a.Row));
        }
        methods.Clear();
        foreach (var row in ordered) methods.Add(row);

        // The data evidence a cited member points at is locked by content hash, like the interop assemblies.
        var locked = root["dataEvidenceFiles"]!.AsArray();
        var relocked = new List<string>();
        foreach (string path in root["methods"]!.AsArray().SelectMany(m => m!["dataEvidence"]!.AsArray())
            .Select(d => d!["file"]!.GetValue<string>()).Distinct(StringComparer.Ordinal))
        {
            string full = Path.Combine(specDirectory, path);
            if (!File.Exists(full)) throw new InvalidDataException("Cited data evidence is missing: " + path);
            // These text records are checked out as LF; raw capture records keep their original bytes.
            byte[] data = File.ReadAllBytes(full);
            if ((path == BehaviourEvidence || path == DamageWindowEvidence) && data.Contains((byte)'\r'))
                throw new InvalidDataException("Cited data evidence must use LF line endings before locking: " + path);
            string hash = Convert.ToHexString(SHA256.HashData(data));
            var entry = locked.FirstOrDefault(e => e!["path"]!.GetValue<string>() == path);
            if (entry == null) locked.Add(new JsonObject { ["path"] = path, ["sha256"] = hash });
            else if (entry["sha256"]!.GetValue<string>() != hash)
            {
                relocked.Add($"{path}: {entry["sha256"]!.GetValue<string>()} -> {hash}");
                entry["sha256"] = hash;
            }
        }

        string text = root.ToJsonString(Json).Replace("\r\n", "\n") + "\n";
        string temporary = output + ".generating";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, output, true);
        Console.WriteLine($"GENERATED {output}: methods={root["methods"]!.AsArray().Count} (+{added.Count}), refreshed={refreshed.Count}");
        foreach (var row in added) Console.WriteLine("  new " + row.Row["id"]!.GetValue<string>());
        foreach (var row in refreshed) Console.WriteLine("  refreshed " + row);
        foreach (var file in relocked) Console.WriteLine("  relocked " + file);
        return 0;
    }

    private static void Add(List<(string Area, JsonObject Row)> added, HashSet<string> ids, HashSet<string> signatures,
        IReadOnlyDictionary<(string Type, string Member), (string File, string Pointer)> evidence, JsonObject row)
    {
        string type = row["type"]!.GetValue<string>();
        string member = Member(row["signature"]!.GetValue<string>());
        var observed = Reviewed(type, member);
        string id = $"{observed.Area}.{type}.{member}.0";
        if (!ids.Add(id) || !signatures.Add(row["signature"]!.GetValue<string>()))
            throw new InvalidDataException("Generated entry duplicates a frozen id or signature: " + id);
        row["id"] = id;
        row["area"] = observed.Area;
        if (observed.Evidence != InteropEvidence)
        {
            var source = evidence[(observed.Type, observed.Member)];
            row["dataEvidence"] = new JsonArray(new JsonObject
            { ["file"] = source.File, ["pointer"] = source.Pointer });
        }
        added.Add((observed.Area, row));
    }

    private static Observed Reviewed(string type, string member)
        => ObservedMembers.SingleOrDefault(o => o.Type == type && o.Member == member)
            ?? throw new InvalidDataException($"No reviewed area/data evidence for {type}::{member}; add it to ObservedMembers.");

    private static JsonObject NewRow(IReadOnlyList<AssemblyDefinition> contracts, Dictionary<string, string> declaringFile,
        string typeName, string member, string signature, string scopeFile)
    {
        var declaration = Required(contracts, typeName, signature);
        if (declaration.Name != member)
            throw new InvalidDataException($"Interop declaration does not match {typeName}::{member}.");
        return new JsonObject
        {
            ["id"] = "",
            ["area"] = "",
            ["assembly"] = declaringFile.GetValueOrDefault(Open(typeName), scopeFile),
            ["type"] = typeName,
            ["signature"] = signature,
            ["isStatic"] = declaration.IsStatic,
            ["isVirtual"] = declaration.IsVirtual,
            ["isPublic"] = declaration.IsPublic,
            ["evidenceLevel"] = "metadata",
            ["nativeCallPhase"] = "unknown",
            ["forgeHooks"] = new JsonArray(),
            ["forgeCallers"] = new JsonArray(),
            ["dataEvidence"] = new JsonArray(),
        };
    }

    // A hook target is frozen by its declaration: the plugin only patches it, so the locked interop declaration
    // supplies the one signature the auditor resolves. A called member is frozen by its IL reference string.
    private static MethodDefinition Required(IReadOnlyList<AssemblyDefinition> contracts, string typeName, string signature)
        => Declared(contracts, typeName, signature)
            ?? throw new InvalidDataException($"No interop declaration for {typeName}::{signature}.");

    private static MethodDefinition? Declared(IReadOnlyList<AssemblyDefinition> contracts, string typeName, string signature)
    {
        var matches = contracts.SelectMany(a => a.MainModule.Types).SelectMany(ForgeIl.Walk)
            .Where(t => Open(t.FullName) == Open(typeName))
            .SelectMany(t => t.Methods)
            .Where(m => signature.Contains("::") ? ForgeIl.SignatureMatches(m, signature) : m.Name == signature)
            .ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"Ambiguous interop declaration for {typeName}::{signature}.");
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string Member(string signature) => Regex.Match(signature, @"::([^(]+)\(").Groups[1].Value;
    private static string[] Strings(JsonNode array) => array.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)v).ToArray());
    private static string Open(string fullName) => ForgeIl.Open(fullName);

    // The behaviour record names a type as the interop dump does ("EnemyDetection"); the plugin's IL reference and
    // the specification entry carry the namespace, so the lookup compares the type name alone.
    private static string Short(string typeName)
    {
        string open = ForgeIl.Open(typeName);
        int separator = open.LastIndexOf('.');
        return separator < 0 ? open : open[(separator + 1)..];
    }

    // Each observed member maps to the one behaviour record that describes it: a read field to its declaration
    // and a reviewed hook body to its RVA. The pointer names that record by index, so the auditor resolves it as
    // the source of this entry and not merely as the batch it came from.
    private static Dictionary<(string Type, string Member), (string File, string Pointer)> ReviewedEvidence(string specDirectory)
    {
        var pointers = new Dictionary<(string Type, string Member), (string File, string Pointer)>();
        foreach (var observed in ObservedMembers)
        {
            if (observed.Evidence == InteropEvidence) continue;
            if (observed.Evidence != BehaviourEvidence)
            {
                pointers[(observed.Type, observed.Member)] = DamageWindowPointer(specDirectory, observed);
                continue;
            }
            string path = Path.Combine(specDirectory, BehaviourEvidence);
            if (!File.Exists(path))
                throw new InvalidDataException($"The reviewed behaviour record {BehaviourEvidence} is missing.");
            var evidence = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            int field = FieldIndex(evidence, observed);
            int hook = HookIndex(evidence, observed);
            if (field < 0 && hook < 0)
                throw new InvalidDataException($"{BehaviourEvidence} does not describe {observed.Type}::{observed.Member}.");
            if (field >= 0 && hook >= 0)
                throw new InvalidDataException($"{BehaviourEvidence} describes {observed.Type}::{observed.Member} twice.");
            pointers[(observed.Type, observed.Member)] = (BehaviourEvidence, field >= 0
                ? $"readFields[{field}].declaration" : $"hookMembers[{hook}].nativeRva");
        }
        return pointers;
    }

    // A called member's source is the damage window's own method row, which freezes the declaration, the RVA and
    // the call sites of that entry point. Exactly one row may describe it.
    private static (string File, string Pointer) DamageWindowPointer(string specDirectory, Observed observed)
    {
        string path = Path.Combine(specDirectory, DamageWindowEvidence);
        if (!File.Exists(path))
            throw new InvalidDataException($"The reviewed damage record {DamageWindowEvidence} is missing.");
        var rows = JsonNode.Parse(File.ReadAllText(path))!.AsObject()["damageMethods"]!.AsArray();
        int found = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!string.Equals((string?)rows[i]!["type"], observed.Type, StringComparison.Ordinal)
                || !string.Equals((string?)rows[i]!["name"], observed.Member, StringComparison.Ordinal)) continue;
            if (found >= 0)
                throw new InvalidDataException($"{DamageWindowEvidence} describes {observed.Type}::{observed.Member} more than once.");
            found = i;
        }
        if (found < 0)
            throw new InvalidDataException($"{DamageWindowEvidence} does not describe {observed.Type}::{observed.Member}.");
        return (DamageWindowEvidence, $"damageMethods[{found}].nativeRva");
    }

    // Both lookups require exactly one record: a member described twice has no single source to cite.
    private static int FieldIndex(JsonObject evidence, Observed observed)
        => Unique(evidence["readFields"]!.AsArray(), observed.Type,
            record => ((string?)record["declaration"] ?? "").EndsWith(" " + observed.Field, StringComparison.Ordinal));

    private static int HookIndex(JsonObject evidence, Observed observed)
        => Unique(evidence["hookMembers"]!.AsArray(), observed.Type,
            record => string.Equals((string?)record["name"], observed.Member, StringComparison.Ordinal));

    private static int Unique(JsonArray records, string type, Func<JsonNode, bool> matches)
    {
        int found = -1;
        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record == null) continue;
            if (Short((string?)record["type"] ?? "") != Short(type)) continue;
            if (!matches(record)) continue;
            if (found >= 0) throw new InvalidDataException($"{BehaviourEvidence} describes {type} more than once.");
            found = i;
        }
        return found;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
