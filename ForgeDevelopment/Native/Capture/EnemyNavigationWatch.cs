using System;
using System.Collections.Generic;
using AIGraph;
using Enemies;
using LevelGeneration;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeDevelopment.Native;

/// <summary>Read-only polling, once a second in a level: one row when a door's traversable state changes (both side nodes, the door's own
/// nodes, how many enemies are heading through it) and one row when a fighting enemy has stood still for three seconds (both agents' course
/// nodes against what the voxel lookup says, enemy type, where the watch first saw it (within a second of its spawn), movement mode, next hop, line of sight, the target's position and navigation
/// position, and the enemy's own nav mesh path answer toward its goal). Dev tool only; it patches nothing.</summary>
internal static class EnemyNavigationWatch
{
    private const float IntervalSeconds = 1f, StuckSeconds = 3f, MovedSqr = .25f;
    private const int MaxEnemyRows = 64;

    private sealed class EnemySample { public Vector3 Position, FirstSeen; public float Since; public bool Logged; public bool Seen; }

    private static float _next;
    private static int _enemyRows;
    private static readonly Dictionary<IntPtr, bool> Gates = new();
    private static readonly Dictionary<IntPtr, EnemySample> Enemies = new();

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _next) return;
        _next = now + IntervalSeconds;
        var nodes = AIG_CourseNode.s_allNodes;
        if (nodes == null || nodes.Count == 0 || GameStateManager.CurrentStateName != eGameStateName.InLevel)
        {
            Gates.Clear(); Enemies.Clear(); _enemyRows = 0;
            return;
        }
        for (int index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (node == null) continue;
            DoorsOf(node);
            EnemiesOf(node, now);
        }
        foreach (var sample in Enemies.Values) sample.Seen = false;
    }

    private static void DoorsOf(AIG_CourseNode node)
    {
        var portals = node.m_portals;
        if (portals == null) return;
        for (int index = 0; index < portals.Count; index++)
        {
            var portal = portals[index];
            if (portal == null || portal.m_nodeA?.Pointer != node.Pointer) continue;
            var gate = portal.Gate;
            if (gate == null) continue;
            bool traversable = gate.IsTraversable;
            if (Gates.TryGetValue(gate.Pointer, out var known) && known == traversable) continue;
            bool first = !Gates.ContainsKey(gate.Pointer);
            Gates[gate.Pointer] = traversable;
            if (!first) DoorRow(portal, gate, traversable);
        }
    }

    private static void DoorRow(AIG_CoursePortal portal, LG_Gate gate, bool traversable)
    {
        int gateNodes = 0, walkable = 0;
        var list = gate.Nodes;
        if (list != null) for (int index = 0; index < list.Count; index++) { gateNodes++; if (list[index]?.IsTraversable == true) walkable++; }
        int heading = 0;
        foreach (var other in AIG_CourseNode.s_allNodes)
        {
            var enemies = other?.m_enemiesInNode;
            if (enemies == null) continue;
            for (int index = 0; index < enemies.Count; index++)
                if (enemies[index]?.AI?.m_courseNavigation?.m_navPortal?.Pointer == portal.Pointer) heading++;
        }
        RecDiag.Record(false, "development.door-navigation", "development.door-navigation gate=" + gate.name + " traversable=" + traversable,
            new("gate", gate.name), new("traversable", traversable), new("position", Text(gate.transform.position)),
            new("nodeA", portal.m_nodeA?.NodeID ?? -1), new("nodeB", portal.m_nodeB?.NodeID ?? -1),
            new("nodeAOwnsCluster", OwnsCluster(portal.m_nodeA)), new("nodeBOwnsCluster", OwnsCluster(portal.m_nodeB)),
            new("gateNodes", gateNodes), new("gateNodesWalkable", walkable), new("enemiesHeadingThrough", heading));
    }

    private static void EnemiesOf(AIG_CourseNode node, float now)
    {
        var enemies = node.m_enemiesInNode;
        if (enemies == null) return;
        for (int index = 0; index < enemies.Count; index++)
        {
            var enemy = enemies[index];
            if (enemy == null || enemy.WasCollected) continue;
            var position = enemy.Position;
            if (!Enemies.TryGetValue(enemy.Pointer, out var sample))
            {
                Enemies[enemy.Pointer] = new() { Position = position, FirstSeen = position, Since = now, Seen = true };
                continue;
            }
            sample.Seen = true;
            if ((position - sample.Position).sqrMagnitude > MovedSqr)
            {
                sample.Position = position; sample.Since = now; sample.Logged = false;
                continue;
            }
            if (sample.Logged || now - sample.Since < StuckSeconds || _enemyRows >= MaxEnemyRows) continue;
            var state = enemy.AI?.m_behaviour?.m_currentStateName ?? EB_States.Dead;
            if (state < EB_States.InCombat || state > EB_States.InCombat_MoveToNextNode_DestroyDoor) continue;
            sample.Logged = true; _enemyRows++;
            EnemyRow(enemy, state, now - sample.Since, sample.FirstSeen);
        }
    }

    // Same-node (enemyNode == targetNode) and the distance
    // (enemyPosition to targetPosition) are left to the reader, which has both inputs.
    private static void EnemyRow(EnemyAgent enemy, EB_States state, float stillSeconds, Vector3 firstSeen)
    {
        var ai = enemy.AI; var navigation = ai.m_courseNavigation;
        var target = ai.Target; var agent = target?.m_agent;
        var enemyNode = enemy.CourseNode; var playerNode = agent?.CourseNode;
        var portal = navigation?.m_navPortal; var gate = portal?.Gate;
        RecDiag.Record(false, "development.enemy-navigation", "development.enemy-navigation state=" + state + " still=" + stillSeconds,
            new("state", state.ToString()), new("enemyType", enemy.EnemyData?.name ?? enemy.EnemyDataID.ToString()),
            new("enemyPosition", Text(enemy.Position)), new("enemyFirstSeen", Text(firstSeen)),
            new("enemyNode", enemyNode?.NodeID ?? -1), new("enemySampledNode", Sampled(enemy.DimensionIndex, enemy.Position)),
            new("targetNode", playerNode?.NodeID ?? -1), new("targetSampledNode", agent == null ? -1 : Sampled(agent.DimensionIndex, agent.Position)),
            new("mode", navigation?.m_navigationalMode.ToString() ?? "none"), new("nextNode", navigation?.m_navNode?.NodeID ?? -1),
            new("nextGate", gate == null ? "none" : gate.name + (gate.IsTraversable ? " open" : " closed")),
            new("targetLineOfSight", target?.m_hasLineOfSight ?? false),
            new("targetPosition", agent == null ? "none" : Text(agent.Position)), new("targetNavigation", agent == null ? "none" : Text(agent.NavigationPosition)),
            new("navPath", NavPath(ai)), new("navGoal", Text(ai.NavmeshAgentGoal)));
    }

    /// <summary>The ground agent's own path answer toward <c>NavmeshAgentGoal</c>: whether it stands on the nav mesh, the Unity path status
    /// (PathPartial or PathInvalid means the goal is not reachable on the mesh from here) and whether a path is still being computed.</summary>
    private static string NavPath(EnemyAI ai)
    {
        var ground = ai.m_navMeshAgent?.TryCast<NavMeshAgentExtention.NavMeshAgentProxy>()?.m_agent;
        if (ground == null) return "no-ground-agent";
        if (!ground.enabled) return "agent-disabled";
        return (ground.isOnNavMesh ? "on-mesh " : "off-mesh ") + ground.pathStatus + (ground.pathPending ? " pending" : "")
            + FormattableString.Invariant($" left={ground.remainingDistance:0.0}");
    }

    private static bool OwnsCluster(AIG_CourseNode? node) => node?.m_nodeCluster?.m_courseNode?.Pointer == node?.Pointer && node != null;

    /// <summary>The node the native voxel lookup gives for a position; -1 when it gives none, which is what a cluster without a course node looks like.</summary>
    private static int Sampled(eDimensionIndex dimension, Vector3 position)
        => AIG_CourseNode.TryGetCourseNode(dimension, position, 0f, out var node) && node != null ? node.NodeID : -1;

    private static string Text(Vector3 value) => FormattableString.Invariant($"({value.x:0.0},{value.y:0.0},{value.z:0.0})");
}
