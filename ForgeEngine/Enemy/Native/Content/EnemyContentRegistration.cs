using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Enemies;
using ForgeEnemy.Content;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeCore;
using GameData;
using UnityEngine;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule : IContentEntryDomain
{
    // The store and models the module read at Load: every generation's entries start again from these.
    private EnemyProfileStore? _baseProfiles;
    private EnemyModelAssemblies? _baseModels;
    // The model owners the current generation's entries registered, removed when a later generation no longer has them.
    private HashSet<string> _entryModelOwners = new(StringComparer.Ordinal);
    private HashSet<string>? _nextModelOwners;

    /// <summary>Called by the production plugin while registration is open. Parsing alone never accepts content;
    /// the callback returns success only after the native tables and the prepared profile are installed.</summary>
    internal void RegisterContent()
    {
        _registration.RegisterContentDomain("enemy", entry => InstallContent(entry.Id, entry.BlockIds, entry.ContentJson, entry.Path));
        ContentEntryGeneration.RegisterDomain(this);
        GameRuntimeBridge.Generations.Changed += ContentStood;
    }

    internal ContentRegistrationResult InstallContent(string id, IReadOnlyDictionary<string, uint> issued, string json, string path)
    {
        try
        {
            var definition = EnemyContentDefinition.Read(id, issued, json, path);
            foreach (var nativeSource in definition.Sources) NativeDataBlockTables.RequireSourceLinks("Enemy", nativeSource.SourceId, nativeSource.Links);
            foreach (var original in definition.OriginalReferences) NativeDataBlockTables.RequireBlock(original.Type, original.Id);
            var profiles = _profiles ?? EnemyProfileStore.Empty();
            if (definition.Profile != null) profiles = profiles.With(definition.Profile);
            var childWaves = PrepareChildWaveProfiles(profiles);
            var models = Models;
            var modelRegistrations = new List<NativeModelRuntime.NativeModelRegistration>();
            foreach (var value in definition.Models)
            {
                using var modelDocument = JsonDocument.Parse(value.Json);
                var modelContent = NativeModelContent.Read(modelDocument.RootElement, "enemy");
                string modelOwner = "enemy:" + value.Id;
                modelRegistrations.Add(EnemyModelAssemblies.Prepare(modelOwner, modelContent, ImportedModelFiles.ForEntry(
                    BepInEx.Paths.BepInExRootPath, path, modelContent, failure => Log.Write(RuntimeLogLevel.Error, EnemyLogCodes.ProfileRefused,
                        new RuntimeLogFacts { Values = new[] { RuntimeLogValue.Of("code", failure.Code), RuntimeLogValue.Quote("source", failure.Relative) }, Detail = failure.Reason }))));
                models = models.With(value.PersistentId, modelOwner, modelContent);
            }
            var blocks = definition.Blocks.Select(block => new NativeDataBlockRegistration(
                block.Type, block.Name, block.PersistentId, block.Json, block.SourceId)).ToArray();
            try
            {
                foreach (var registration in modelRegistrations) registration.Commit();
                ContentEntryGeneration.Publish(blocks, strictFields: true);
            }
            catch
            {
                for (int index = modelRegistrations.Count - 1; index >= 0; index--) modelRegistrations[index].Rollback();
                throw;
            }
            // A generation that does not stand puts the previous registrations back, newest first.
            if (ContentEntryGeneration.Current is { } generation)
                foreach (var registration in modelRegistrations) { generation.OnUndo(registration.Rollback); generation.OnRelease(registration.Settle); }
            foreach (var value in definition.Models) _nextModelOwners?.Add("enemy:" + value.Id);
            // All validation, catalog cloning and native writes are complete. No logging or parsing can throw
            // between native commit and these assignments, so a refusal cannot leave the old profile attached.
            UseProfiles(profiles, childWaves);
            _models = models;

            return ContentRegistrationResult.Accept();
        }
        catch (Exception error)
        {
            return NativeContentRefusal.Of(error, id, "enemy-content-invalid");
        }
    }

    /// <summary>A generation's enemy entries start from the store and models read at Load; a generation that does not stand
    /// puts the current ones back.</summary>
    public void BeginEntries(ContentEntryGeneration generation)
    {
        var (profiles, childWaves, models, owners) = (_profiles, _childWaveProfiles, _models, _entryModelOwners);
        generation.OnUndo(() =>
        {
            if (profiles != null) UseProfiles(profiles, childWaves ?? PrepareChildWaveProfiles(profiles));
            _models = models;
            _entryModelOwners = owners;
            _nextModelOwners = null;
        });
        var baseProfiles = _baseProfiles ?? EnemyProfileStore.Empty();
        UseProfiles(baseProfiles, PrepareChildWaveProfiles(baseProfiles));
        _models = _baseModels;
        _nextModelOwners = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>After the entries: the game builds a new prefab for every enemy row this generation published, through its own
    /// <c>EnemyPrefabManager.GenerateEnemy</c> (once the game has built its prefabs; before that its own pass builds them), and drops
    /// the prefab of every enemy row the generation withdrew. The enemy allocator spawns from its own copy of that table and
    /// replicates by prefab index, both filled once by <c>EnemyAllocator.GetEnemyPrefabs</c> at setup (which cannot run twice: the
    /// replication list refuses a prefab it already holds); they are laid out again once the generation stands (<see cref="ContentStood"/>).
    /// The replaced and withdrawn prefabs are destroyed once the generation stands; a generation that does not stand gets the old
    /// prefabs back and destroys the new ones. Model registrations the generation no longer has are removed once it stands.</summary>
    public void EndEntries(ContentEntryGeneration generation)
    {
        var withdrawnOwners = _entryModelOwners.Where(owner => !_nextModelOwners!.Contains(owner)).ToArray();
        _entryModelOwners = _nextModelOwners!;
        _nextModelOwners = null;
        if (withdrawnOwners.Length != 0)
            generation.OnRelease(() => { foreach (var owner in withdrawnOwners) NativeModelRuntime.Remove(owner); });
        var prefabs = EnemyPrefabManager.Current?.m_enemyPrefabs;
        // The game has not built its enemy prefabs yet: its own pass builds every published row.
        if (prefabs == null || prefabs.Count == 0) return;
        foreach (var (type, id) in generation.Rows)
        {
            if (type != "Enemy") continue;
            var block = EnemyDataBlock.GetBlock(id) ?? throw new InvalidOperationException("enemy-prefab-block-missing: " + id);
            // The interop dictionary's TryGetValue cannot bind its out value (Method not found: __Canon..ctor); ContainsKey and the indexer can.
            GameObject? old = prefabs.ContainsKey(id) ? prefabs[id] : null;
            EnemyPrefabManager.GenerateEnemy(block);
            var built = prefabs.ContainsKey(id) ? prefabs[id] : null;
            if (built == null || (old != null && built.Pointer == old.Pointer))
                throw new InvalidOperationException("enemy-prefab-not-generated: " + id);
            generation.OnUndo(() =>
            {
                if (old != null) prefabs[id] = old; else prefabs.Remove(id);
                UnityEngine.Object.Destroy(built);
            });
            if (old != null) generation.OnRelease(() => UnityEngine.Object.Destroy(old));
        }
        foreach (var (type, id) in generation.Withdrawn)
        {
            if (type != "Enemy" || !prefabs.ContainsKey(id) || prefabs[id] is not { } old) continue;
            prefabs.Remove(id);
            generation.OnUndo(() => prefabs[id] = old);
            generation.OnRelease(() => UnityEngine.Object.Destroy(old));
        }
    }

    // The Forge enemy rows the allocator was last laid out with: a prefab of one of them whose row left the tables is dropped.
    private HashSet<uint> _laidOut = new();

    /// <summary>A content generation stands (contract I-RELOAD). A player who joins later fills the allocator at its own setup from the
    /// content it started with, so it is laid out here as that setup lays it out: Map's Rundown rows (which have no entry generation)
    /// get the game's prefab when they have none and lose it when their row left the tables, then <see cref="Allocate"/>. Before the game
    /// built its prefabs, or before the allocator is set up, the game's own passes read the tables later.</summary>
    private void ContentStood(ContentChanged change)
    {
        var prefabs = EnemyPrefabManager.Current?.m_enemyPrefabs;
        if (prefabs == null || prefabs.Count == 0) return;
        var forge = NativeDataBlockTables.OwnedRows().Where(row => row.Type == "Enemy").Select(row => row.Id).ToHashSet();
        var present = new HashSet<uint>();
        foreach (var block in EnemyDataBlock.GetAllBlocks())
        {
            if (block == null) continue;
            present.Add(block.persistentID);
            if (!forge.Contains(block.persistentID) || prefabs.ContainsKey(block.persistentID)) continue;
            EnemyPrefabManager.GenerateEnemy(block);
            if (!prefabs.ContainsKey(block.persistentID) || prefabs[block.persistentID] == null)
                throw new InvalidOperationException("enemy-prefab-not-generated: " + block.persistentID);
        }
        foreach (var id in _laidOut.Where(id => !present.Contains(id) && prefabs.ContainsKey(id)).ToArray())
        {
            var old = prefabs[id];
            prefabs.Remove(id);
            if (old != null) UnityEngine.Object.Destroy(old);
        }
        _laidOut = forge;
        Allocate(prefabs, forge);
    }

    /// <summary>The order a cold start with the standing content appends Forge's enemy rows to the table: the content entries in the order
    /// their generation published them, then the Rundown rows in the order Map published them (packs, then each pack's blocks), then
    /// any other Forge row in table order.</summary>
    private static List<uint> ColdStartOrder(HashSet<uint> forge, IEnumerable<uint> table)
    {
        var order = new List<uint>();
        var seen = new HashSet<uint>();
        void Add(uint id) { if (forge.Contains(id) && seen.Add(id)) order.Add(id); }
        foreach (var (type, id) in ContentEntryGeneration.Standing?.Published ?? Array.Empty<(string, uint)>())
            if (type == "Enemy") Add(id);
        foreach (var pack in ForgeMap.Native.MapHost.Session?.Content?.Published.Packs ?? Array.Empty<RundownPack>())
            foreach (var block in pack.Blocks)
                if (block.Type == "Enemy") Add(block.PersistentId);
        foreach (var id in table) Add(id);
        return order;
    }

    /// <summary>Lays the allocator out as <c>EnemyAllocator.GetEnemyPrefabs</c> lays it out at a cold start with this content: every
    /// enemy row that has a prefab, added with no sync version, the rows Forge does not own first in table order, then Forge's rows in
    /// <see cref="ColdStartOrder"/>. A replication index is decided by the row and its place, never by which prefab object the row has
    /// now, so a row whose prefab was built again keeps its index and every peer that holds the same tables gets the same list.
    /// The spawn lookup gets every row's prefab and loses rows without one, so no index points at a withdrawn or destroyed prefab.</summary>
    private static void Allocate(Il2CppSystem.Collections.Generic.Dictionary<uint, GameObject> prefabs, HashSet<uint> forge)
    {
        var allocator = EnemyAllocator.Current;
        if (allocator?.m_enemyTypeLookup == null || allocator.m_enemyReplicationManager == null) return;
        var lookup = allocator.m_enemyTypeLookup;
        var replication = allocator.m_enemyReplicationManager;
        GameObject? Prefab(uint id) => prefabs.ContainsKey(id) ? prefabs[id] : null;
        var table = new List<uint>();
        replication.ClearPrefabs();
        lookup.Clear();
        foreach (var block in EnemyDataBlock.GetAllBlocks())
        {
            if (block == null) continue;
            if (forge.Contains(block.persistentID)) table.Add(block.persistentID);
            else if (Prefab(block.persistentID) is { } prefab)
            {
                replication.AddPrefab(prefab, null);
                lookup[block.persistentID] = prefab;
            }
        }
        foreach (var id in ColdStartOrder(forge, table))
        {
            if (Prefab(id) is not { } prefab) continue;
            replication.AddPrefab(prefab, null);
            lookup[id] = prefab;
        }
    }
}
