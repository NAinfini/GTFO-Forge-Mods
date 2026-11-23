using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using AssetShards;
using ForgeMap.Generation;
using ForgeEngine.GameBindings;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;
using UnityEngine;
using UnityEngine.SceneManagement;
using ForgeCore;

namespace ForgeMap.Native;

internal static partial class CompiledMapGenerator
{
    private sealed partial class Run
    {
        // A short list scanned linearly: one entry per distinct connector prefab in this generation (tens at most); the Run keeps no Dictionary.
        private readonly List<(nint Pointer, GameObject Prefab, bool HasGate)> ConnectorPrefabs = new();
        /// <summary>The loading priority before the bundle wait raised it; null while it is not raised.</summary>
        internal UnityEngine.ThreadPriority? LoadingPriorityBefore;

        /// <summary>Freeze static gate presence on first use of each native prefab for this generation only.
        /// WithGates pools contain both plain InternalGate and its PlugDoorAlign subclass; instance gate binding
        /// is still checked after spawning. A different managed wrapper of the same live native prefab shares this read.</summary>
        internal bool PrefabHasGate(GameObject prefab, string path)
        {
            if (prefab is null || prefab.WasCollected || prefab == null) throw new GenerationRefusal("gen-resource-unloaded", path);
            nint pointer = prefab.Pointer;
            for (int index = 0; index < ConnectorPrefabs.Count; index++)
            {
                var frozen = ConnectorPrefabs[index];
                if (frozen.Pointer == pointer && !frozen.Prefab.WasCollected && frozen.Prefab != null && frozen.Prefab.Pointer == pointer) return frozen.HasGate;
            }
            bool hasGate = prefab.GetComponentsInChildren<LG_InternalGate>(true).Length != 0;
            for (int index = 0; index < ConnectorPrefabs.Count; index++) if (ConnectorPrefabs[index].Pointer == pointer) { ConnectorPrefabs[index] = (pointer, prefab, hasGate); return hasGate; }
            ConnectorPrefabs.Add((pointer, prefab, hasGate));
            return hasGate;
        }
    }

    /// <summary>One acquired bundle: when its callback arrived, as the generation log reports it.</summary>
    private sealed record BundleArrival(double WaitMs, int WaitFrames, bool FocusedAtRequest, bool FocusedAtCompletion, object QueueBefore, object QueueAtCompletion);
    private readonly record struct ResourceRow(string List, string Prefab);
    private sealed class FrozenResourceSet(LG_LoadComplexDataSetResourcesJob job)
    {
        internal readonly LG_LoadComplexDataSetResourcesJob Job = job;
        internal ResourceRow[] Rows = Array.Empty<ResourceRow>();
    }

    private static void LoadFrozenResources(Run run, LG_FactoryBatch batch)
    {
        var retained = new List<LG_FactoryJob>(); int removed = 0;
        foreach (var job in batch.Jobs)
            if (job.TryCast<LG_LoadComplexDataSetResourcesJob>() != null) removed++;
            else retained.Add(job);
        batch.Jobs.Clear(); foreach (var job in retained) batch.Jobs.Enqueue(job);
        // The plan freezes sets; several sets may share the same shard bundle.
        // Prepare every set, but acquire each bundle exactly once through the native async queue.
        var waits = new List<FrozenResourceSet>?[5]; int queued = 0;
        var sets = new List<FrozenResourceSet>();
        foreach (uint id in run.LevelProgram.RequiredResourceSets)
        {
            var set = (ComplexResourceSetDataBlock)NativeDataBlockTables.RequireBlock("ComplexResourceSet", id);
            var bundle = (int)set.ComplexType switch
            {
                0 => AssetBundleName.Complex_Mining,
                1 => AssetBundleName.Complex_Service,
                2 => AssetBundleName.Complex_Tech,
                _ => throw new GenerationRefusal("gen-resource-missing", id + "/complex-bundle")
            };
            long prepareAt = Stopwatch.GetTimestamp();
            set.PrepareForBuild(); // Same first-load preparation as native job RVA 0x1354360.
            double prepareMs = (Stopwatch.GetTimestamp() - prepareAt) * 1000d / Stopwatch.Frequency;
            bool acquire = waits[(int)bundle] == null;
            var group = waits[(int)bundle] ??= new();
            var wait = new LG_LoadComplexDataSetResourcesJob(set);
            var frozen = new FrozenResourceSet(wait);
            // Every frozen set must run PopulateInitial/FullResourceArrays in the native
            // completion callback, including sets that share a single acquired bundle.
            wait.m_loadingStarted = true; group.Add(frozen); batch.Jobs.Enqueue(wait); sets.Add(frozen);
            run.Record("resources", id.ToString(), new
            {
                resourceSet = id,
                complex = (int)set.ComplexType,
                bundle = (int)bundle,
                acquired = acquire,
                frozen = true,
                nativeAsyncQueue = true,
                prepareMs
            });
        }
        // A set may list a room of another complex than its own: the exporter copies the rooms of every complex into the generated
        // main set. The native populate resolves each row by its prefab path through the loaded-asset table and throws
        // KeyNotFoundException for a row nobody loaded, so the bundle of the complex that holds the row is acquired as well, the
        // way a vanilla level that uses a second complex loads it. That bundle has no set to populate of its own.
        int extraBundles = 0;
        foreach (var set in sets) set.Rows = ResourceRows(set.Job.m_block);
        foreach (var set in sets)
            foreach (var (_, prefab) in set.Rows)
                if (RowBundle(prefab) is { } rowBundle && waits[(int)rowBundle] == null) { waits[(int)rowBundle] = new(); extraBundles++; }
        // A placed model is cloned from an original spawner of its source prefab, and only a loaded prefab can be captured
        // (RoomRecipeHost.CaptureLoadedSources). The one source the models share is a Mining room, which a Tech level's
        // sets never list, so the bundle of every source this level's own models use is acquired the same way.
        foreach (var request in RoomRecipeParts.RequestsFor(run.LevelReference))
            if (RowBundle(request.Key) is { } sourceBundle && waits[(int)sourceBundle] == null) { waits[(int)sourceBundle] = new(); extraBundles++; }
        var arrivals = new BundleArrival?[waits.Length]; int pending = 0;
        foreach (var group in waits) if (group != null) pending++;
        // Unity integrates the queued shard scenes on the main thread in a slice of each frame that this priority sets (Normal:
        // 10 ms, High: 50 ms). A machine with long frames then needs thousands of frames for a cold load (a client on integrated
        // graphics took 100 s and more while the host waited at the elevator), so the scenes get the larger slice until the last
        // bundle is in; the batch holds the elevator ride either way.
        if (pending > 0 && run.LoadingPriorityBefore == null)
        {
            run.LoadingPriorityBefore = Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.High;
        }
        for (int index = 0; index < waits.Length; index++)
        {
            if (waits[index] == null) continue;
            queued++;
            var queueBefore = ResourceQueue();
            int queuedFrame = Time.frameCount; bool focusedAtRequest = Application.isFocused;
            long queuedAt = Stopwatch.GetTimestamp(); int bundleIndex = index;
            AssetShardManager.LoadAllShardsForBundleAsync((AssetBundleName)index,
                (Il2CppSystem.Action)(Action)(() =>
                {
                    if (run.Failed || run.Epoch != NativeHookRegistration.WorldEpoch) return;
                    try
                    {
                        arrivals[bundleIndex] = new((Stopwatch.GetTimestamp() - queuedAt) * 1000d / Stopwatch.Frequency, Time.frameCount - queuedFrame,
                            focusedAtRequest, Application.isFocused, queueBefore, ResourceQueue());
                        // No set populates before every requested bundle is in: a row may live in a bundle that another set asked for.
                        if (--pending == 0)
                        {
                            RestoreLoadingPriority(run);
                            RequireRowsLoaded(waits);
                            FinishFrozenSets(run, waits, arrivals, extraBundles);
                        }
                    }
                    catch (Exception error) { Refuse(run, error); }
                }), 1, LoadSceneMode.Additive);
            run.Record("resource-queue", index.ToString(), new
            {
                bundle = index,
                queueBefore,
                queueAfterRequest = ResourceQueue(),
                requestCallMs = (Stopwatch.GetTimestamp() - queuedAt) * 1000d / Stopwatch.Frequency
            });
        }
        run.Record("resource-jobs", run.Level, new
        {
            removedNativeJobs = removed,
            retainedJobs = retained.Count,
            frozenSets = run.LevelProgram.RequiredResourceSets.Length,
            queuedBundles = queued,
            extraBundles
        });
    }

    /// <summary>Puts back the loading priority the run raised for its bundle wait; called when the last bundle arrives and when the
    /// run ends without it.</summary>
    private static void RestoreLoadingPriority(Run run)
    {
        if (run.LoadingPriorityBefore is not { } before) return;
        run.LoadingPriorityBefore = null;
        Application.backgroundLoadingPriority = before;
    }

    private static void FinishFrozenSets(Run run, List<FrozenResourceSet>?[] groups, BundleArrival?[] arrivals, int extraBundles)
    {
        for (int index = 0; index < groups.Length; index++)
        {
            if (groups[index] is not { Count: > 0 } group || arrivals[index] is not { } arrival) continue;
            long populateAt = Stopwatch.GetTimestamp();
            foreach (var set in group) set.Job.ComplexAssetBundleLoaded();
            double populateMs = (Stopwatch.GetTimestamp() - populateAt) * 1000d / Stopwatch.Frequency;
            int complex = (int)group[0].Job.m_block.ComplexType;
            run.Record("resource-ready", complex.ToString(),
                new
                {
                    sets = group.Count,
                    nativeArraysPopulated = true,
                    bundleWaitMs = arrival.WaitMs,
                    populateMs,
                    acquireCalls = 1,
                    completionCallbacks = 1,
                    shardsAtATime = 1,
                    loadSceneMode = "Additive",
                    waitFrames = arrival.WaitFrames,
                    focusedAtRequest = arrival.FocusedAtRequest,
                    focusedAtCompletion = arrival.FocusedAtCompletion,
                    extraBundles,
                    queueBefore = arrival.QueueBefore,
                    queueAtCompletion = arrival.QueueAtCompletion
                });
            if (MapLog.Logs(RuntimeLogLevel.Info)) MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.GenerationResources, "Native shard bundle and frozen resource arrays ready",
                RuntimeLogValue.Of("complex", complex),
                RuntimeLogValue.Of("sets", group.Count), RuntimeLogValue.Of("acquireCalls", 1), RuntimeLogValue.Of("callbacks", 1),
                RuntimeLogValue.Of("bundleWaitMs", arrival.WaitMs.ToString("F3", CultureInfo.InvariantCulture)),
                RuntimeLogValue.Of("populateMs", populateMs.ToString("F3", CultureInfo.InvariantCulture)),
                RuntimeLogValue.Of("waitFrames", arrival.WaitFrames),
                RuntimeLogValue.Of("waitFrameMeanMs", arrival.WaitFrames > 0 ? (arrival.WaitMs / arrival.WaitFrames).ToString("F3", CultureInfo.InvariantCulture) : "unavailable"),
                RuntimeLogValue.Of("focusedAtRequest", arrival.FocusedAtRequest),
                RuntimeLogValue.Of("focusedAtCompletion", arrival.FocusedAtCompletion),
                RuntimeLogValue.Of("extraBundles", extraBundles));
        }
    }

    /// <summary>The bundle of the complex a prefab path belongs to (Assets/AssetPrefabs/Complex/Tech/... is Complex_Tech); a path
    /// outside the three complexes names none.</summary>
    private static AssetBundleName? RowBundle(string prefab)
    {
        const string marker = "/Complex/";
        int at = prefab.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        int start = at + marker.Length, end = prefab.IndexOf('/', start);
        if (end < 0) return null;
        return prefab.Substring(start, end - start).ToUpperInvariant() switch
        {
            "MINING" => AssetBundleName.Complex_Mining,
            "SERVICE" => AssetBundleName.Complex_Service,
            "TECH" => AssetBundleName.Complex_Tech,
            _ => null
        };
    }

    /// <summary>Every row must be loaded before any set populates; the native populate would otherwise end in a bare
    /// KeyNotFoundException without saying which prefab it was.</summary>
    private static void RequireRowsLoaded(List<FrozenResourceSet>?[] groups)
    {
        foreach (var group in groups)
        {
            if (group == null) continue;
            foreach (var set in group)
                foreach (var (list, prefab) in set.Rows)
                    if (ForgeCore.GameAssets.Get<GameObject>(prefab) == null)
                        throw new GenerationRefusal("gen-resource-unloaded", set.Job.m_block.persistentID + "/" + list,
                            "prefab=" + prefab + " is not loaded by any acquired bundle");
        }
    }

    private sealed record ResourceListReader(string Name, Func<ComplexResourceSetDataBlock, object?> Get,
        Func<object, int>? Count, Func<object, int, ResourceData?>? Item);

    // The interop type carries every list twice, once as the property and once as its compiler backing property.
    // Bind its managed wrappers once: generation then calls typed delegates instead of GetValue/Invoke per row.
    private static readonly ResourceListReader[] ResourceLists = Array.ConvertAll(Array.FindAll(
        typeof(ComplexResourceSetDataBlock).GetProperties(BindingFlags.Public | BindingFlags.Instance),
        property => property.CanRead && !property.Name.Contains("BackingField") && property.GetIndexParameters().Length == 0 && property.PropertyType.IsGenericType
            && property.PropertyType.GetGenericArguments().Length == 1 && property.PropertyType.GetGenericArguments()[0] == typeof(ResourceData)), BindResourceList);

    private static ResourceListReader BindResourceList(PropertyInfo property)
    {
        var count = property.PropertyType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)?.GetMethod;
        var item = property.PropertyType.GetMethod("get_Item", new[] { typeof(int) });
        var set = Expression.Parameter(typeof(ComplexResourceSetDataBlock), "set");
        var get = Expression.Lambda<Func<ComplexResourceSetDataBlock, object?>>(Expression.Convert(Expression.Property(set, property), typeof(object)), set).Compile();
        if (count?.ReturnType != typeof(int) || item == null || !typeof(ResourceData).IsAssignableFrom(item.ReturnType))
            return new(property.Name, get, null, null);
        var rows = Expression.Parameter(typeof(object), "rows"); var index = Expression.Parameter(typeof(int), "index");
        var typedRows = Expression.Convert(rows, property.PropertyType);
        return new(property.Name, get,
            Expression.Lambda<Func<object, int>>(Expression.Call(typedRows, count), rows).Compile(),
            Expression.Lambda<Func<object, int, ResourceData?>>(Expression.Convert(Expression.Call(typedRows, item, index), typeof(ResourceData)), rows, index).Compile());
    }

    /// <summary>Every prefab row of a set, the way the native populate walks its lists.</summary>
    private static ResourceRow[] ResourceRows(ComplexResourceSetDataBlock set)
    {
        var manifest = new List<ResourceRow>();
        foreach (var reader in ResourceLists)
        {
            if (reader.Get(set) is not { } rows) continue;
            var count = reader.Count; var item = reader.Item;
            if (count == null || item == null)
                throw new GenerationRefusal("gen-resource-unloaded", set.persistentID + "/" + reader.Name, "the resource list cannot be read by count and index");
            int length = count(rows);
            for (int index = 0; index < length; index++)
                if (item(rows, index) is { Prefab: { Length: > 0 } prefab })
                    manifest.Add(new(reader.Name, prefab));
        }
        return manifest.ToArray();
    }
    // Observe only at generation request/completion boundaries. No hook, polling or frame work is added.
    private static object ResourceQueue()
    {
        var current = AssetShardManager.m_currentShardQueue;
        var waiting = new List<string>();
        if (current?.WaitingForShards != null) foreach (var shard in current.WaitingForShards) waiting.Add(shard);
        return new
        {
            activeBundle = current?.BundleName,
            remaining = current?.Shards?.Count ?? 0,
            waiting = waiting.ToArray(),
            backlog = AssetShardManager.m_shardLoaderBacklog?.Count ?? 0,
            deferredCallbacks = AssetShardManager.s_callbackQueue?.m_queue?.Count ?? 0
        };
    }
}
