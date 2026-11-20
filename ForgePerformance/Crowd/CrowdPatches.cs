using System;
using System.Collections.Generic;
using AIGraph;
using CullingSystem;
using Enemies;
using HarmonyLib;
using ForgePerformance.LoadShedding;
using UnityEngine.AI;

namespace ForgePerformance.Crowd;

/// <summary>The probe's timed game methods, ClientHiddenSyncThrottle (inside EnemyTransformSync.Update) and HitReactKeepsAgent
/// (inside EnemyAI.SetNavMeshAgent while ES_Hitreact.Enter runs). A target is patched when the probe is on or the option that
/// acts inside it is configured on; otherwise nothing is patched. Each target is patched through <see cref="EntryAdmission"/>;
/// a refused one is left out and logged once. The game method always runs unless the option that owns the call decides
/// otherwise.</summary>
// patch-targets: GameStateManager.Update, GameStateManager.FixedUpdate, UpdateBehaviourGroup.UpdateMember, UpdateDetectionGroup.UpdateMember, UpdateLocomotionGroup.UpdateMember, FixedUpdateLocomotionGroup.UpdateMember, UpdateNetworkGroup.UpdateMember, UpdateNodeGroup.UpdateMember, ES_PathMove.Update, EnemyMaterialFeeder.Update, C_MovingCuller.Update, TentacleBase.Update, TentacleBase.LateUpdate, MovingEnemyTentacleBase.LateUpdate, SolverManager.LateUpdate, PlayerEnemyCollision.LateUpdate, FPSCamera.LateUpdate, FPSCamera.OnPreCull, LocalPlayerAgent.Update, LocalPlayerAgent.LateUpdate, GPUCurvyManager.LateUpdate, CL_ShadowAtlas.LateUpdate, FrameRateLimiter.LateUpdate, IndirectBatcherCamera.LateUpdate, ControlRigLinker.LateUpdate, AkInitializer.LateUpdate, MapDetails.LateUpdate
internal static class CrowdPatches
{
    private static readonly Harmony Patcher = new("NAinfini.ForgePerformance.Crowd");
    private static int _hitreactDepth;
    // EnemyAI pointers whose navigation HitReactKeepsAgent stopped instead of switching off; resumed by the next switch-on.
    private static readonly HashSet<IntPtr> Kept = new();
    internal static bool SyncHooked { get; private set; }
    internal static bool HitreactHooked { get; private set; }
    internal static long KeptTotal, SyncSkippedTotal;

    /// <summary>Takes every patch <see cref="Install"/> put on off again (the plugin's unload).</summary>
    internal static void Uninstall()
    {
        Patcher.UnpatchSelf();
        SyncHooked = HitreactHooked = false;
        Kept.Clear();
    }

    internal static void Install(SheddingSettings settings, Action<string> error)
    {
        var all = settings.Probe;
        bool Patch(bool wanted, Type patch) => wanted && EntryAdmission.PatchClass(Patcher, patch, error);
        // A timing-only point resolved by name in its own assembly (Modules-ASM unless named), so the probe needs no reference to
        // the assembly that declares it and scans no other assembly.
        void Time(Type patch, string type, string method)
        {
            var target = Type.GetType(type.Contains(',') ? type : type + ", Modules-ASM") is { } declaring ? AccessTools.DeclaredMethod(declaring, method) : null;
            EntryAdmission.Patch(target, type + "." + method, Patcher,
                () => Patcher.Patch(target, new HarmonyMethod(patch, "Prefix"), new HarmonyMethod(patch, "Postfix")), error);
        }
        Patch(all, typeof(SpawnedTime));
        Patch(all, typeof(WakeTime));
        Patch(all, typeof(ReachableTime));
        var nav = Patch(all || settings.HitReactKeepsAgent, typeof(NavSwitch));
        HitreactHooked = nav && Patch(true, typeof(HitreactDepth));
        Patch(settings.FarAnimatorThrottle, typeof(PathMoveExit));
        Patch(all, typeof(LightTime));
        Patch(all, typeof(ShadowTime));
        SyncHooked = Patch(all || settings.ClientHiddenSyncThrottle, typeof(SyncThrottle));
        Patch(all, typeof(DamageTime));
        Patch(all, typeof(ProjectileTime));
        Patch(all, typeof(ProjectileSpawnTime));
        if (!all) return;
        // EnemyUpdateManager's four updates are timed by ForgeDevelopment, and Mastermind.GameStateUpdate and
        // EnemyAllocator.SpawnEnemy are patched by ForgeEngine; the probe times what runs around and inside them instead
        // (a spawn shows as EnemyAllocator.OnEnemySpawned and this mod's own EnemyAgent.Setup prefix). Animator, skinning and
        // the physics step are native phases; IRF.InstancedRenderFeature.LateUpdate is this mod's own ExtraCuts patch.
        Time(typeof(GameStateTime), "GameStateManager", "Update");
        Time(typeof(GameStateFixedTime), "GameStateManager", "FixedUpdate");
        Time(typeof(BehaviourGroupTime), "Enemies.EnemyUpdateManager+UpdateBehaviourGroup", "UpdateMember");
        Time(typeof(DetectionGroupTime), "Enemies.EnemyUpdateManager+UpdateDetectionGroup", "UpdateMember");
        Time(typeof(LocomotionGroupTime), "Enemies.EnemyUpdateManager+UpdateLocomotionGroup", "UpdateMember");
        Time(typeof(LocomotionFixedGroupTime), "Enemies.EnemyUpdateManager+FixedUpdateLocomotionGroup", "UpdateMember");
        Time(typeof(NetworkGroupTime), "Enemies.EnemyUpdateManager+UpdateNetworkGroup", "UpdateMember");
        Time(typeof(NodeGroupTime), "Enemies.EnemyUpdateManager+UpdateNodeGroup", "UpdateMember");
        Time(typeof(PathMoveTime), "ES_PathMove", "Update");
        Time(typeof(MaterialFeederTime), "EnemyMaterialFeeder, Assets-ASM", "Update");
        Time(typeof(MovingCullerTime), "CullingSystem.C_MovingCuller", "Update");
        Time(typeof(TentacleTime), "TentacleBase", "Update");
        Time(typeof(TentacleLateTime), "TentacleBase", "LateUpdate");
        Time(typeof(EnemyTentacleLateTime), "MovingEnemyTentacleBase", "LateUpdate");
        Time(typeof(IkLateTime), "RootMotion.SolverManager, Plugins-ASM", "LateUpdate");
        Time(typeof(CollisionTime), "PlayerEnemyCollision", "LateUpdate");
        Time(typeof(CameraLateTime), "FPSCamera", "LateUpdate");
        Time(typeof(CameraPreCullTime), "FPSCamera", "OnPreCull");
        Time(typeof(LocalPlayerTime), "Player.LocalPlayerAgent", "Update");
        Time(typeof(LocalPlayerLateTime), "Player.LocalPlayerAgent", "LateUpdate");
        Time(typeof(CurvyLateTime), "GPUCurvyManager", "LateUpdate");
        Time(typeof(ShadowAtlasLateTime), "CL_ShadowAtlas", "LateUpdate");
        Time(typeof(FrameLimiterLateTime), "FrameRateLimiter", "LateUpdate");
        Time(typeof(IndirectBatcherLateTime), "IndirectBatcherCamera", "LateUpdate");
        Time(typeof(ControlRigLateTime), "ControlRigLinker", "LateUpdate");
        Time(typeof(AudioLateTime), "AkInitializer, Wwise-ASM", "LateUpdate");
        Time(typeof(MapDetailsLateTime), "MapDetails", "LateUpdate");
    }

    private static class GameStateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.GameStateUpdate, __state); }
    private static class GameStateFixedTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.GameStateFixed, __state); }
    private static class CurvyLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.CurvyLate, __state); }
    private static class AudioLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.AudioLate, __state); }
    private static class BehaviourGroupTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.EnemyBehaviourGroup, __state); }
    private static class DetectionGroupTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.EnemyDetectionGroup, __state); }
    private static class LocomotionGroupTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.EnemyLocomotionGroup, __state); }
    private static class LocomotionFixedGroupTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.EnemyLocomotionFixedGroup, __state); }
    private static class NetworkGroupTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.EnemyNetworkGroup, __state); }
    private static class NodeGroupTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.EnemyNodeGroup, __state); }
    private static class PathMoveTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.PathMoveUpdate, __state); }
    private static class MaterialFeederTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.MaterialFeeder, __state); }
    private static class MovingCullerTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.MovingCuller, __state); }
    private static class TentacleTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.TentacleUpdate, __state); }
    private static class TentacleLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.TentacleLate, __state); }
    private static class EnemyTentacleLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.EnemyTentacleLate, __state); }
    private static class IkLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.IkLate, __state); }
    private static class CollisionTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.PlayerEnemyCollision, __state); }
    private static class CameraLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.CameraLate, __state); }
    private static class CameraPreCullTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.CameraPreCull, __state); }
    private static class LocalPlayerTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.LocalPlayerUpdate, __state); }
    private static class LocalPlayerLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.LocalPlayerLate, __state); }
    private static class ShadowAtlasLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.ShadowAtlasLate, __state); }
    private static class FrameLimiterLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.FrameLimiterLate, __state); }
    private static class IndirectBatcherLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.IndirectBatcherLate, __state); }
    private static class ControlRigLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.ControlRigLate, __state); }
    private static class MapDetailsLateTime { private static void Prefix(out long __state) => __state = Probe.Begin(); private static void Postfix(long __state) => Probe.End(ProbePoint.MapDetailsLate, __state); }

    internal static void EndLevel() => Kept.Clear();

    [HarmonyPatch(typeof(EnemyAllocator), nameof(EnemyAllocator.OnEnemySpawned))]
    private static class SpawnedTime
    {
        private static void Prefix(out long __state) => __state = Probe.Begin();
        private static void Postfix(long __state) => Probe.End(ProbePoint.EnemySpawned, __state);
    }
    [HarmonyPatch(typeof(ES_HibernateWakeUp), nameof(ES_HibernateWakeUp.DoHibernateWakeupPropagation))]
    private static class WakeTime
    {
        private static void Prefix(out long __state) => __state = Probe.Begin();
        private static void Postfix(long __state) => Probe.End(ProbePoint.WakePropagation, __state);
    }
    [HarmonyPatch(typeof(AIG_CourseGraph), nameof(AIG_CourseGraph.GetReachableEnemiesInNodes))]
    private static class ReachableTime
    {
        private static void Prefix(out long __state) => __state = Probe.Begin();
        private static void Postfix(long __state, Il2CppSystem.Collections.Generic.List<EnemyAgent>? __result)
        {
            Probe.End(ProbePoint.ReachableQuery, __state);
            if (__state != 0 && __result != null) Probe.ReachableEnemies += __result.Count;
        }
    }
    [HarmonyPatch(typeof(C_Light), nameof(C_Light.UpdateVisible))]
    private static class LightTime
    {
        private static void Prefix(out long __state) => __state = Probe.Begin();
        private static void Postfix(long __state) => Probe.End(ProbePoint.LightVisible, __state);
    }
    [HarmonyPatch(typeof(CL_ShadowGenerator), nameof(CL_ShadowGenerator.ProcessUpdates))]
    private static class ShadowTime
    {
        private static void Prefix(out long __state) => __state = Probe.Begin();
        private static void Postfix(long __state) => Probe.End(ProbePoint.ShadowRedraw, __state);
    }
    [HarmonyPatch(typeof(Dam_SyncedDamageBase), nameof(Dam_SyncedDamageBase.Update))]
    private static class DamageTime
    {
        private static void Prefix(out long __state) => __state = Probe.Begin();
        private static void Postfix(long __state) => Probe.End(ProbePoint.DamageBaseUpdate, __state);
    }
    [HarmonyPatch(typeof(ProjectileTargeting), nameof(ProjectileTargeting.Update))]
    private static class ProjectileTime
    {
        private static void Prefix(out long __state) => __state = Probe.Begin();
        private static void Postfix(long __state) => Probe.End(ProbePoint.ProjectileUpdate, __state);
    }
    [HarmonyPatch(typeof(ProjectileManager), nameof(ProjectileManager.SpawnProjectileType))]
    private static class ProjectileSpawnTime
    {
        private static void Prefix(out long __state) => __state = Probe.Begin();
        private static void Postfix(long __state) => Probe.End(ProbePoint.ProjectileSpawn, __state);
    }

    // ClientHiddenSyncThrottle: an enemy the crowd tick listed (client, out of sight, far) runs the game's update one frame in
    // HiddenSyncInterval, staggered by its pointer. The host's own update returns at once and is never listed.
    [HarmonyPatch(typeof(EnemyTransformSync), nameof(EnemyTransformSync.Update))]
    private static class SyncThrottle
    {
        private static bool Prefix(EnemyTransformSync __instance, out long __state)
        {
            var self = __instance.Pointer;
            if (CrowdSwitches.ClientHiddenSyncThrottle && CrowdLoad.ThrottledSyncs.Contains(self)
                && (CrowdLoad.Frame + (int)((self.ToInt64() >> 4) & 0xffff)) % CrowdSwitches.HiddenSyncInterval != 0)
            {
                SyncSkippedTotal++;
                Probe.Count(ProbePoint.TransformSyncSkipped);
                __state = 0;
                return false;
            }
            __state = Probe.Begin();
            return true;
        }
        private static void Postfix(long __state) => Probe.End(ProbePoint.TransformSync, __state);
    }

    // FarAnimatorThrottle: only a walking (PathMove) enemy is throttled, so its animator is switched back on when that state
    // exits, which the game does before the next state's Enter calls CrossFade or Play. ES_PathMove.Exit and SyncExit share
    // one native body (RVA 0x16E7C00, check-patch-bodies.py), so this one patch also runs for a client's SyncExit; a second
    // patch on the same address would overflow the stack.
    [HarmonyPatch(typeof(ES_PathMove), nameof(ES_PathMove.Exit))]
    private static class PathMoveExit
    {
        private static void Prefix(ES_PathMove __instance) => CrowdLoad.WalkEnding(__instance.m_enemyAgent);
    }

    [HarmonyPatch(typeof(ES_Hitreact), nameof(ES_Hitreact.Enter))]
    private static class HitreactDepth
    {
        private static void Prefix() => _hitreactDepth++;
        private static Exception? Finalizer(Exception? __exception) { _hitreactDepth--; return __exception; }
    }

    // EnemyAI.SetNavMeshAgent(false) switches the agent off: set_enabled(false), set_updatePosition(false) and avoidance None.
    // HitReactKeepsAgent, only for the call ES_Hitreact.Enter (the host's stagger entry) makes, leaves the agent on and stopped
    // with the same updatePosition and avoidance, so it stays in the crowd. The next SetNavMeshAgent(true) clears isStopped and
    // then runs the game's own switch-on (enable, updatePosition, avoidance, the position check and warp).
    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.SetNavMeshAgent))]
    private static class NavSwitch
    {
        private static bool Prefix(EnemyAI __instance, bool __0, out long __state)
        {
            __state = 0;
            if (!__0 && _hitreactDepth > 0 && CrowdSwitches.HitReactKeepsAgent && Keep(__instance))
            {
                KeptTotal++; Probe.Count(ProbePoint.HitreactKeptAgent);
                return false;
            }
            if (__0 && Kept.Count != 0 && Kept.Remove(__instance.Pointer))
            {
                var agent = __instance.m_navMeshAgent;
                if (agent != null && agent.enabled) agent.isStopped = false;
            }
            __state = Probe.Begin();
            return true;
        }
        private static void Postfix(bool __0, long __state) => Probe.End(__0 ? ProbePoint.NavAgentOn : ProbePoint.NavAgentOff, __state);

        private static bool Keep(EnemyAI ai)
        {
            var agent = ai.m_navMeshAgent;
            if (agent == null || !agent.enabled) return false;
            agent.isStopped = true;
            agent.updatePosition = false;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;
            Kept.Add(ai.Pointer);
            return true;
        }
    }
}
