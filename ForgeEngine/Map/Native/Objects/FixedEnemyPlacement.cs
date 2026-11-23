using System;
using System.Reflection;
using System.Runtime.InteropServices;
using AIGraph;
using Agents;
using Enemies;
using Il2CppInterop.Runtime;
using UnityEngine;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// Places an authored enemy through the native allocator's replicated spawn entry. A fixed placement names an
/// EnemyDataBlock, not an EnemyGroup population. The allocator preserves its pose and agent mode and explicitly
/// supports a null group (groupReplicatorKey = 0). The caller runs on the host alone.
/// A debug group would select the requested type, but EGS_DebugSpawn.Update replaces its rotation with a random
/// one; calling the allocator directly preserves the authored rotation as well.
/// </summary>
internal static class FixedEnemyPlacement
{
    /// <summary>
    /// Places one enemy by its own native type and observes the returned agent's type. A native call that throws
    /// is caught here and answers false with the reason
    /// named, so a caller refuses its row instead of reporting a placement that never happened.
    /// </summary>
    internal static bool TryPlace(Vector3 position, Quaternion rotation, AIG_CourseNode node, uint typeId,
        AgentMode mode, out EnemyAgent? spawned, out string? refusal)
    {
        var placed = TrySpawn(position, rotation, node, typeId, mode, out var enemy, out refusal, out _, out _);
        // A row only owns the agent when the allocator returned the requested type.
        spawned = enemy != null && !enemy.WasCollected && enemy.EnemyDataID == typeId ? enemy : null;
        return placed;
    }

    internal static bool TrySpawn(Vector3 position, Quaternion rotation, AIG_CourseNode node, uint typeId,
        AgentMode mode, out EnemyAgent? enemy, out string? refusal, out bool attempted, out ForgeCore.SpawnRefusal preflight)
    {
        enemy = null; refusal = null; attempted = false; preflight = ForgeCore.SpawnRefusal.None;
        FixedEnemySpawnInitialized.PatrolRefusal = null;
        if (node == null || node.WasCollected)
        {
            refusal = "the course node the point resolved to is gone";
            return false;
        }
        if (!ForgeCore.EnemySpawn.Ready)
        {
            refusal = "the native enemy allocator is unavailable";
            return false;
        }
        try
        {
            // Dimension_Desert_Boss_Arena supplies the objects a SquidBoss needs; a normal geomorph does not.
            // Check before the allocator replicates a half-initialized life.
            preflight = ForgeCore.EnemySpawn.Preflight(typeId, out var missing);
            if (preflight == ForgeCore.SpawnRefusal.EnemyTypeMissing) { refusal = "enemy-type-missing"; return false; }
            if (preflight == ForgeCore.SpawnRefusal.ArenaMissing)
            {
                refusal = "squid-boss-arena-missing: the native boss arena must supply " + missing;
                return false;
            }
            attempted = true;
            enemy = ForgeCore.EnemySpawn.Request(typeId, node, mode, position, rotation);
            if (enemy == null || enemy.WasCollected || enemy.EnemyDataID != typeId)
            {
                refusal = "the native enemy allocator did not return the requested enemy type " + typeId;
                Withdraw(ref enemy, ref refusal);
                return false;
            }
            var ai = enemy.AI;
            if (ai == null || ai.m_behaviour == null || ai.m_locomotion == null)
            {
                refusal = "the native enemy was spawned without its initialized state machines";
                Withdraw(ref enemy, ref refusal);
                return false;
            }
            var expectedMode = mode;
            if (mode == AgentMode.Hibernate && enemy.EnemyBehaviorData?.IsFlyer == true
                && enemy.EnemyBehaviorData.Flyer_HibernateRemap != AgentMode.Off)
                expectedMode = enemy.EnemyBehaviorData.Flyer_HibernateRemap;
            if (mode == AgentMode.Hibernate && (ai.Mode != expectedMode || expectedMode == AgentMode.Hibernate
                && (ai.m_behaviour.m_currentStateName != EB_States.Hibernating || ai.m_locomotion.CurrentStateEnum != ES_StateEnum.Hibernate)))
            {
                refusal = "sleep-state-unobserved: the spawned enemy did not enter native hibernation";
                Withdraw(ref enemy, ref refusal);
                return false;
            }
            if (mode == AgentMode.Scout)
            {
                var patrolRefusal = FixedEnemySpawnInitialized.PatrolRefusal;
                FixedEnemySpawnInitialized.PatrolRefusal = null;
                if (patrolRefusal != null
                    || ai.Mode != AgentMode.Scout || ai.m_behaviour.m_currentStateName != EB_States.Patrolling
                    || Session.IsHost && (ai.m_scoutPath == null || !ai.m_scoutPath.IsValid))
                {
                    // A scout without its native patrol path would fail inside the game's own patrol state every
                    // update, so the half-initialized life is taken out of the world with the refusal.
                    refusal = patrolRefusal != null
                        ? "scout-patrol-unavailable: " + patrolRefusal
                        : "scout-state-unobserved: the spawned scout did not enter its native patrol path";
                    Withdraw(ref enemy, ref refusal);
                    return false;
                }
            }
            return true;
        }
        catch (Exception error)
        {
            refusal = "the native enemy allocator threw " + error;
            return false;
        }
    }

    private static IntPtr placementMethod;

    /// <summary>Up to <paramref name="wanted"/> random placements (position and facing) of the cluster's own scored list: the game's
    /// <c>AIG_NodeCluster.TryGetPlacements</c>, which the game's group spawn and the scout patrol read. A cluster nobody has scored
    /// yet has no list; the game's own scoring entry builds it and the request is repeated. Null when the cluster still supplies none.</summary>
    internal static Il2CppSystem.Collections.Generic.List<Placement>? ClusterPlacements(AIG_NodeCluster cluster, int wanted)
    {
        if (!TryGetPlacements(cluster, wanted, out var placements))
        {
            cluster.CreatePlacementList();
            if (!TryGetPlacements(cluster, wanted, out placements)) return null;
        }
        return placements;
    }

    private static unsafe bool TryGetPlacements(AIG_NodeCluster cluster, int wanted,
        out Il2CppSystem.Collections.Generic.List<Placement>? placements)
    {
        // This interop wrapper emits an open List<T> constructor scoped to Modules-ASM for its out value.
        // Invoke the identical native method, then wrap its returned pointer as the actual closed BCL type.
        if (placementMethod == IntPtr.Zero)
            placementMethod = (IntPtr)(typeof(AIG_NodeCluster).GetField(
                "NativeMethodInfoPtr_TryGetPlacements_Public_Boolean_Int32_byref_List_1_Placement_0",
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                ?? throw new InvalidOperationException("native-placement-method-unavailable"));
        if (placementMethod == IntPtr.Zero) throw new InvalidOperationException("native-placement-method-unavailable");
        IntPtr list = IntPtr.Zero, exception = IntPtr.Zero;
        void** args = stackalloc void*[2];
        args[0] = &wanted;
        args[1] = &list;
        var result = IL2CPP.il2cpp_runtime_invoke(placementMethod, IL2CPP.Il2CppObjectBaseToPtrNotNull(cluster), args, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        if (result == IntPtr.Zero) throw new InvalidOperationException("native-placement-result-unavailable");
        placements = list == IntPtr.Zero ? null : new Il2CppSystem.Collections.Generic.List<Placement>(list);
        return Marshal.ReadByte(IL2CPP.il2cpp_object_unbox(result)) != 0;
    }

    /// <summary>A refused row leaves no half-initialized life in the world: the enemy is despawned and dropped, or the
    /// refusal says why it could not be.</summary>
    private static void Withdraw(ref EnemyAgent? enemy, ref string? refusal)
    {
        if (enemy == null || enemy.WasCollected) return;
        var removal = TryRemove(enemy);
        if (removal != null) refusal += "; the refused enemy could not be removed: " + removal;
        else enemy = null;
    }

    /// <summary>The game's own replication despawn (the same entry the enemy-remove action uses): no life ends and
    /// no corpse is left. Answers the failure reason, or null when the despawn was submitted.</summary>
    private static string? TryRemove(EnemyAgent enemy)
    {
        try
        {
            var replicator = enemy.Sync?.Replicator;
            if (replicator == null) return "the enemy has no replicator";
            replicator.Despawn();
            return null;
        }
        catch (Exception error) { return error.ToString(); }
    }
}

// Native OnSpawn initializes modes and behaviour for group-less enemies too, but its scout-path branch
// requires a group. Complete that path once; native sleepers and flyer remapping remain untouched.
internal static class FixedEnemySpawnInitialized
{
    /// <summary>
    /// Why the host could not give the scout just spawned on this thread its native patrol path, or null. The
    /// native spawn entry calls this postfix synchronously inside <c>EnemyAllocator.SpawnEnemy</c>, so
    /// <see cref="FixedEnemyPlacement.TrySpawn"/> clears it before that call and reads it right after. A hook that
    /// runs inside the game's own callback never throws for an unsupported state: the throw would repeat for every
    /// scout and abort the callback; the refusal is returned once, to the placement that asked.
    /// </summary>
    internal static string? PatrolRefusal;

    /// <summary>ForgeCore's <c>GameHooks.EnemySyncSpawned</c>, after the body and every other postfix.</summary>
    internal static void Spawned(EnemySync sync, pEnemySpawnData spawnData)
    {
        if (spawnData.groupReplicatorKey != 0 || spawnData.mode != AgentMode.Scout) return;
        try { PatrolRefusal = CompletePatrol(sync, spawnData); }
        catch (Exception error) { PatrolRefusal = "the native patrol setup threw " + error; }
    }

    private static string? CompletePatrol(EnemySync sync, pEnemySpawnData spawnData)
    {
        var ai = sync.m_agent?.AI;
        if (ai == null || ai.m_behaviour == null || ai.m_locomotion == null)
            return "a group-less enemy has no initialized native state machines";
        var path = new ScoutPath();
        if (Session.IsHost)
        {
            // The same native patrol-point provider used by EnemyGroup.GetScoutPath (RVA 0x1574100).
            // EnemySync.OnSpawn runs before the allocator's later node setup, so the spawn packet is the native
            // source of the requested course node here. It chooses movement goals only; the birth pose is unchanged.
            if (!spawnData.courseNode.TryGet(out var spawnNode) || spawnNode == null || spawnNode.WasCollected)
                return "the scout's spawn packet has no available course node";
            var cluster = spawnNode.m_nodeCluster;
            if (cluster == null) return "the scout's spawn packet course node has no node cluster";
            var placements = FixedEnemyPlacement.ClusterPlacements(cluster, 4);
            if (placements == null) return "the node cluster supplies no patrol placements even after its placement list was created";
            var points = EnemyGroupScoreSpawnData.GetPatrolPoints(cluster, placements, 4);
            if (points == null || points.Count == 0)
                return "the native patrol provider returned no movement goals";
            var data = path.m_pathData;
            data.pathSteps = (byte)points.Count;
            path.m_pathData = data;
            for (var i = 0; i < points.Count; i++) path.SetPosition(i, points[i]);
            path.Initialize();
        }
        // Native OnSpawn constructs an empty path on clients; only the host selects movement goals.
        ai.m_scoutPath = path;
        // The typed entry publishes its current-state name; the machine already entered this state in OnSpawn.
        ai.m_behaviour.ChangeState(EB_States.Patrolling);
        return null;
    }

}
