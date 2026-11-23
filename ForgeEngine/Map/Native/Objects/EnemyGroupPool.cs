using System;
using System.Collections.Generic;
using System.Linq;
using Enemies;
using ForgeEngine.Framework;
using GameData;
using HarmonyLib;

namespace ForgeMap.Native;

/// <summary>The native random enemy pool, scoped to the level's own Rundown package (contract 敌人与随机池). Every installed Rundown
/// package numbers its generated group difficulties inside itself, and the game draws a zone's random group from every EnemyGroup row
/// of the wanted type and difficulty (<c>EnemySpawnManager.TryCreateEnemyGroupRandomizer</c>, cached per type and difficulty for the
/// process), so another package's group with the same pair would be drawn. A group a Forge package published enters the pool only
/// while the level's Rundown is that package or declares it as a dependency; rows of no Forge package (the game's, MTFO's) always
/// enter, and a level of no Forge Rundown draws from those alone. A group referenced by its number is not drawn and is untouched.
/// The excluded set is decided once per build, when the factory starts, together with clearing the game's cache so no draw of the
/// previous level is reused; one record per build says what was left out.</summary>
internal static class EnemyGroupPool
{
    private static readonly HashSet<uint> Excluded = new();
    private static readonly HashSet<ushort> Empty = new();
    private static IDisposable? _started;

    /// <summary>Follows the factory's builds from now on (idempotent).</summary>
    internal static void Start() => _started ??= ForgeCore.FactoryEvents.OnBuildStart(BuildStarted);

    private static void BuildStarted()
    {
        Excluded.Clear();
        Empty.Clear();
        EnemySpawnManager.m_groupRandomizers?.Clear();
        if (MapHost.Session?.Content?.Published is not { EnemyGroupOwners.Count: > 0 } published) return;
        var level = LevelIdentity.Read();
        var pack = level is { } identity ? published.Packs.FirstOrDefault(row => row.RundownId == identity.RundownId) : null;
        var admitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pack != null)
        {
            admitted.Add(pack.Package);
            foreach (var pin in pack.Dependencies) admitted.Add(pin.Substring(0, pin.LastIndexOf('-')));
        }
        var excluded = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, package) in published.EnemyGroupOwners)
        {
            if (admitted.Contains(package)) continue;
            Excluded.Add(id);
            excluded[package] = excluded.GetValueOrDefault(package) + 1;
        }
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.EnemyGroupPool,
            "The random enemy pool of this build leaves out the groups of Forge packages the level's Rundown neither is nor depends on.",
            RuntimeLogValue.Of("level", level?.ToString() ?? "unreadable"), RuntimeLogValue.Quote("package", pack?.Package ?? ""),
            RuntimeLogValue.Of("admitted", published.EnemyGroupOwners.Count - Excluded.Count), RuntimeLogValue.Of("excluded", Excluded.Count),
            RuntimeLogValue.Quote("excludedPackages", string.Join(",", excluded.Select(row => row.Key + "=" + row.Value))));
    }

    /// <summary>Before the game builds the pool of one type and difficulty: when an excluded group would be among its candidates, the
    /// pool of the remaining candidates is put in the game's cache, built by the game's own constructor, and the original returns it.
    /// When only excluded groups have the pair, the call answers false as the original does for a pair no group has.</summary>
    internal static bool Scope(eEnemyGroupType type, eEnemyRoleDifficulty difficulty, ref EnemyGroupRandomizer? randomizer, ref bool result)
    {
        if (Excluded.Count == 0) return true;
        var cache = EnemySpawnManager.m_groupRandomizers;
        ushort key = ByteTools.GetByteKey((byte)type, (byte)difficulty);
        if (cache == null || cache.ContainsKey(key)) return true;
        Il2CppSystem.Collections.Generic.List<EnemyGroupDataBlock>? kept = null;
        bool trimmed = false;
        foreach (var block in GameDataBlockBase<EnemyGroupDataBlock>.GetAllBlocks())
        {
            if (block == null || block.Type != type || block.Difficulty != difficulty) continue;
            if (Excluded.Contains(block.persistentID)) trimmed = true;
            else (kept ??= new()).Add(block);
        }
        if (!trimmed) return true;
        if (kept != null)
        {
            cache[key] = new EnemyGroupRandomizer(kept);
            return true;
        }
        randomizer = null;
        result = false;
        if (Empty.Add(key))
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.EnemyGroupPoolEmpty,
                "The level asks for a random enemy group that only other Forge packages have; no group is drawn.",
                RuntimeLogValue.Of("type", (int)type), RuntimeLogValue.Of("difficulty", (int)difficulty));
        return false;
    }
}

/// <summary><c>EnemySpawnManager.TryCreateEnemyGroupRandomizer</c>, the game's one builder of a zone's random group pool
/// (<see cref="EnemyGroupPool"/>).</summary>
[HarmonyPatch(typeof(EnemySpawnManager), nameof(EnemySpawnManager.TryCreateEnemyGroupRandomizer))]
internal static class EnemyGroupPoolScope
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(eEnemyGroupType type, eEnemyRoleDifficulty difficulty, ref EnemyGroupRandomizer? groupRandomizer, ref bool __result)
        => EnemyGroupPool.Scope(type, difficulty, ref groupRandomizer, ref __result);
}
