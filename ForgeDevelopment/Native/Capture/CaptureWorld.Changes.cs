using System.Collections.Generic;
using Enemies;
using LevelGeneration;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ForgeDevelopment.Native;

internal static partial class CaptureWorld
{
    private static readonly CaptureObjectIndex<EnemyAgent> ChangeEnemies = new(ChangeEnemyIdentity, ForgeCore.NativeObjects.Same);
    internal static void TrackChangeEnemy(EnemyAgent enemy)
    {
        if (enemy != null && !enemy.WasCollected) ChangeEnemies.Add(enemy.GetInstanceID(), enemy);
    }
    internal static void ForgetChangeEnemy(EnemyAgent enemy)
    {
        if (enemy != null && !enemy.WasCollected) ChangeEnemies.Remove(enemy.GetInstanceID(), enemy);
    }
    private static string ChangeEnemyIdentity(EnemyAgent enemy)
        => "enemy/" + enemy.GlobalID.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Only enemies have an authoritative lifecycle index. Other families return unknown, and reset
    /// keeps all membership unknown until the normal full snapshot has bootstrapped the new world.</summary>
    internal static bool? ChangeIdentityPresent(string identity)
    {
        if (!ChangesReady || !identity.StartsWith("enemy/", System.StringComparison.Ordinal)) return null;
        return ChangeEnemies.TryGet(identity, out var enemy) && enemy != null && !enemy.WasCollected
            && enemy.gameObject.activeInHierarchy;
    }
    internal static bool ChangesReady { get; private set; }
    internal static void ResetChanges() { ChangeEnemies.Clear(); _changeIndex = null; ChangesReady = false; }
    /// <summary>The live enemies for a snapshot. Once the lifecycle index is bootstrapped it is read instead of asking the
    /// scene again: the scene search is one uninterruptible native call that cost a 50-60 ms frame on every snapshot, and
    /// the index already holds exactly the enemies the spawn and despawn hooks saw. The first snapshot of a world
    /// still searches the scene, which bootstraps the index.</summary>
    private static EnemyAgent[]? LiveEnemies()
    {
        if (!ChangesReady) return Objects<EnemyAgent>();
        var live = new List<EnemyAgent>();
        foreach (var enemy in ChangeEnemies.Read())
            if (enemy != null && !enemy.WasCollected && enemy.gameObject.activeInHierarchy) live.Add(enemy);
        return live.ToArray();
    }
    private static void ObserveChangeEnemies(EnemyAgent[] enemies)
    {
        foreach (var enemy in enemies) TrackChangeEnemy(enemy);
        ChangesReady = true;
    }

    // The fixed objects a change record covers (doors, generators, exits, puzzles) and the node-to-zone map are found
    // by walking every component under the floor, which allocates a wrapper for each one. They do not move or appear
    // between level builds, so the walk is kept and repeated only after ChangeIndexMaxAgeMilliseconds, which is how
    // an object created after generation is still found. Rows are still read afresh on every sweep.
    private enum ChangeKind : byte { SecurityDoor, WeakDoor, Generator, Cluster, HsuActivator, Exit, Puzzle }
    private readonly record struct ChangeItem(UnityObject Item, ChangeKind Kind, LG_Zone? Zone);
    private sealed class ChangeIndex
    {
        internal readonly List<ChangeItem> Items = new();
        internal readonly Dictionary<int, LG_Zone> ZoneOf = new();
        internal double BuiltAt;
    }
    internal const double ChangeIndexMaxAgeMilliseconds = 30000;
    /// <summary>The clock the index age is read from; a test replaces it to age the index without waiting.</summary>
    internal static System.Func<double> ChangeClock = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private static ChangeIndex? _changeIndex;

    private static IEnumerable<CaptureRow?> BuildChangeIndex(LG_Floor? floor, ChangeIndex index)
    {
        var zones = floor?.allZones;
        var zoneOf = index.ZoneOf;
        if (zones != null)
            for (var zoneIndex = 0; zoneIndex < zones.Count; zoneIndex++)
            {
                var zone = zones[zoneIndex];
                yield return null;
                if (zone == null || zone.WasCollected) continue;
                var nodes = zone.m_courseNodes; if (nodes == null) continue;
                for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
                {
                    var node = nodes[nodeIndex];
                    if (node != null && !node.WasCollected) zoneOf[node.NodeID] = zone;
                    yield return null;
                }
            }
        if (floor == null || floor.WasCollected) yield break;
        // One typed native search per kind instead of wrapping every component under the floor to test its type.
        var root = floor.transform;
        foreach (var kind in ChangeKinds)
        {
            foreach (var item in FindChangeItems(root, kind))
                if (item != null && !item.WasCollected) index.Items.Add(new ChangeItem(item, kind, ZoneOf(item, zoneOf)));
            yield return null;
        }
    }

    private static readonly ChangeKind[] ChangeKinds = (ChangeKind[])System.Enum.GetValues(typeof(ChangeKind));
    private static IEnumerable<UnityObject> FindChangeItems(Transform root, ChangeKind kind)
        => kind switch
        {
            ChangeKind.SecurityDoor => root.GetComponentsInChildren<LG_SecurityDoor>(true),
            ChangeKind.WeakDoor => root.GetComponentsInChildren<LG_WeakDoor>(true),
            ChangeKind.Generator => root.GetComponentsInChildren<LG_PowerGenerator_Core>(true),
            ChangeKind.Cluster => root.GetComponentsInChildren<LG_PowerGeneratorCluster>(true),
            ChangeKind.HsuActivator => root.GetComponentsInChildren<LG_HSUActivator_Core>(true),
            ChangeKind.Exit => root.GetComponentsInChildren<LG_LevelExitGeo>(true),
            _ => root.GetComponentsInChildren<ChainedPuzzles.ChainedPuzzleInstance>(true)
        };

    private static CaptureRow ChangeItemRow(ChangeItem entry)
    {
        var item = entry.Item; var zone = entry.Zone;
        return entry.Kind switch
        {
            ChangeKind.SecurityDoor => ChangeRow((LG_SecurityDoor)item, zone, DoorRow),
            ChangeKind.WeakDoor => ChangeRow((LG_WeakDoor)item, zone, WeakDoorRow),
            ChangeKind.Generator => ChangeRow((LG_PowerGenerator_Core)item, zone, GeneratorRow),
            ChangeKind.Cluster => ChangeRow((LG_PowerGeneratorCluster)item, zone, ClusterRow),
            ChangeKind.HsuActivator => ChangeRow((LG_HSUActivator_Core)item, zone, HsuActivatorRow),
            ChangeKind.Exit => ChangeRow((LG_LevelExitGeo)item, zone, ExitRow),
            _ => ChangeRow((ChainedPuzzles.ChainedPuzzleInstance)item, zone, PuzzleRow)
        };
    }

    /// <summary>Only change-record families, read one budgeted unit at a time. Null yields charge traversal/index
    /// work even when it produces no row. Full snapshots keep their separate low-frequency collector.</summary>
    internal static IEnumerable<CaptureRow?> ChangeRows()
    {
        var index = _changeIndex;
        if (index == null || ChangeClock() - index.BuiltAt >= ChangeIndexMaxAgeMilliseconds)
        {
            var building = new ChangeIndex();
            foreach (var step in BuildChangeIndex(CurrentFloor(), building)) yield return step;
            building.BuiltAt = ChangeClock();
            // An index that found nothing is a floor that is not built yet, not a level without zones or doors.
            if (building.Items.Count != 0 || building.ZoneOf.Count != 0) _changeIndex = building;
            index = building;
        }
        var zoneOf = index.ZoneOf;
        foreach (var entry in index.Items)
        {
            var item = entry.Item;
            if (item == null || item.WasCollected) { yield return null; continue; }
            yield return ChangeItemRow(entry);
        }
        var players = PlayerAgents();
        if (players != null)
            for (var playerIndex = 0; playerIndex < players.Count; playerIndex++)
            {
                var player = players[playerIndex];
                if (player == null || player.WasCollected) { yield return null; continue; }
                var zone = ZoneOf(player, zoneOf); var row = PlayerRow(player, zone);
                if (zone != null && row.Zone == null) row.Zone = zone.AliasName; row.Field("active", Active(player)); yield return row;
            }
        foreach (var enemy in ChangeEnemies.Read())
        {
            if (enemy == null || enemy.WasCollected || !enemy.gameObject.activeInHierarchy) { yield return null; continue; }
            var zone = ZoneOf(enemy, zoneOf); var row = EnemyRow(enemy, zone);
            if (zone != null && row.Zone == null) row.Zone = zone.AliasName; row.Field("active", Active(enemy)); yield return row;
        }
    }
    private static CaptureRow ChangeRow<T>(T item, LG_Zone? zone,
        System.Func<T, LG_Zone?, CaptureRow> build) where T : UnityEngine.Object
    {
        var row = build(item, zone);
        if (zone != null && row.Zone == null) row.Zone = zone.AliasName;
        row.Field("active", Active(item)); return row;
    }

}

// Subscribed by DevelopmentGameHooks to ForgeCore's one patch of the sync spawn and despawn; this index observes only diagnostic objects.
internal static class CaptureEnemySpawned
{
    internal static void Spawned(EnemySync __instance)
    {
        try { if (CaptureRegistry.Active && __instance.m_agent != null) { CaptureRegistry.InvalidateChangeMembership(); CaptureWorld.TrackChangeEnemy(__instance.m_agent); } }
        catch (System.Exception error) { CaptureRegistry.Log("enemy-index-spawn", error); }
    }
}
internal static class CaptureEnemyDespawned
{
    internal static void Despawned(EnemySync __instance)
    {
        try { if (CaptureRegistry.Active && __instance.m_agent != null) { CaptureRegistry.InvalidateChangeMembership(); CaptureWorld.ForgetChangeEnemy(__instance.m_agent); } }
        catch (System.Exception error) { CaptureRegistry.Log("enemy-index-despawn", error); }
    }
}
