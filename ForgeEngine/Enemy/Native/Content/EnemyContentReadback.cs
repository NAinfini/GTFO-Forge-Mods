using System;
using System.Collections.Generic;
using System.Linq;
using Enemies;
using ForgeCore;
using ForgeEngine.GameBindings;
using GameData;

namespace ForgeEnemy.Native;

/// <summary>What the enemy tables and the enemy allocator hold now, for the author read <c>AuthorReads.EntryRows</c> (contract I-RELOAD,
/// tier D): every row Forge owns with whether it has a prefab, a spawn-lookup slot and a replication index, the whole replication list in
/// index order, and which rows came from content entries and which from a Rundown's Enemy rows. Read only.</summary>
internal static class EnemyContentReadback
{
    internal static (object Json, string Digest) Read()
    {
        var owned = NativeDataBlockTables.OwnedRows().Where(row => row.Type == "Enemy").ToArray();
        var prefabs = EnemyPrefabManager.Current?.m_enemyPrefabs;
        var allocator = EnemyAllocator.Current;
        var lookup = allocator?.m_enemyTypeLookup;
        var replication = allocator?.m_enemyReplicationManager;
        var forge = owned.Select(row => row.Id).ToHashSet();
        var idOfPrefab = new Dictionary<IntPtr, uint>();
        if (lookup != null) foreach (var pair in lookup) if (pair.Value != null) idOfPrefab[pair.Value.Pointer] = pair.Key;
        var list = new List<(int Index, uint Id, string Name, bool Forge)>();
        var held = replication?.m_prefabs;
        for (int at = 0; held != null && at < held.Count; at++)
        {
            var prefab = held[at];
            uint id = prefab != null && idOfPrefab.TryGetValue(prefab.Pointer, out var found) ? found : 0;
            uint index = (uint)at;
            if (prefab != null && !replication!.TryGetPrefabID(prefab, out index)) index = uint.MaxValue;
            list.Add(((int)index, id, NameOf(id), forge.Contains(id)));
        }
        int Index(uint id)
        {
            var at = list.FindIndex(row => row.Id == id);
            return at < 0 ? -1 : list[at].Index;
        }
        var entryRows = (ContentEntryGeneration.Standing?.Published ?? Array.Empty<(string, uint)>()).Where(row => row.Item1 == "Enemy").Select(row => row.Item2).ToHashSet();
        var rundownRows = (ForgeMap.Native.MapHost.Session?.Content?.Published.Packs ?? Array.Empty<ForgeEngine.Framework.RundownPack>())
            .SelectMany(pack => pack.Blocks).Where(block => block.Type == "Enemy").Select(block => block.PersistentId).ToHashSet();
        var lines = string.Concat(list.Select(row => row.Index + ":" + row.Id + "\n"));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lines))).ToLowerInvariant();
        return (new
        {
            rows = owned.Select(row => new
            {
                id = row.Id,
                source = entryRows.Contains(row.Id) ? "entry" : rundownRows.Contains(row.Id) ? "rundown" : "other",
                prefab = prefabs != null && prefabs.ContainsKey(row.Id),
                spawn = lookup != null && lookup.ContainsKey(row.Id),
                index = Index(row.Id)
            }).ToArray(),
            entryNames = entryRows.Select(NameOf).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            rundownNames = rundownRows.Select(NameOf).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            replicationCount = list.Count,
            replication = string.Join(";", list.Select(row => row.Index + ":" + row.Id)),
            forge = list.Where(row => row.Forge).Select(row => new { index = row.Index, id = row.Id, name = row.Name }).ToArray()
        }, digest);
    }

    private static string NameOf(uint id) => id == 0 ? "" : EnemyDataBlock.GetBlock(id)?.name ?? "";
}
