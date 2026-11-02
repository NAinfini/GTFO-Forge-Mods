using AssetShards;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityEngine
{
    public sealed class AsyncOperation : Object { }
}

namespace UnityEngine.SceneManagement
{
    public enum LoadSceneMode { Single = 0, Additive = 1 }
}

namespace AssetShards
{
    public enum AssetBundleName { Gear }
    public enum AssetBundleShard { S0, S1 }

    /// <summary>The interop dictionary the game's tables are: a table identity, key tests, an indexer and enumeration.</summary>
    public sealed class NativeTable<T>
    {
        private readonly Dictionary<string, T> _rows = new(StringComparer.Ordinal);
        public IntPtr Pointer { get; } = new(9000 + typeof(T).Name.Length);
        public bool ContainsKey(string key) => _rows.ContainsKey(key);
        public T this[string key] { get => _rows[key]; set => _rows[key] = value; }
        public void Remove(string key) => _rows.Remove(key);
        public void Clear() => _rows.Clear();
    }

    public static class AssetShardManager
    {
        public static bool StartupAssetsIsLoaded = true;
        public static NativeTable<Object>? s_loadedAssetsLookup = new();
        public static NativeTable<Object>? s_loadedManifests = new();
        public static NativeTable<int>? s_loadedShardDependencyCount = new();
        public static readonly List<string> Calls = new();

        public static string GetShardName(AssetBundleName bundle, AssetBundleShard shard) => bundle + "_" + shard;

        public static T? GetLoadedAsset<T>(string path, bool forceAssetDatabase) where T : Object
        {
            Calls.Add("get:" + path);
            // The game's body indexes the table and throws for a missing key.
            return (T)s_loadedAssetsLookup![path.ToUpper()];
        }

        public static bool LoadShardAsync(string shard, Action<AsyncOperation> done, UnityEngine.SceneManagement.LoadSceneMode mode)
        { Calls.Add("load:" + shard + ":" + mode); return true; }

        public static bool LoadShardAsync(AssetBundleName bundle, AssetBundleShard shard, Action<AsyncOperation> done,
            UnityEngine.SceneManagement.LoadSceneMode mode)
            => LoadShardAsync(GetShardName(bundle, shard), done, mode);

        public static void IncreaseShardDependencyCount(string shard) => Calls.Add("increase:" + shard);

        public static void UnloadShardAsync(string shard, Action<AsyncOperation>? done, string reason) => Calls.Add("unload:" + shard + ":" + reason);

        public static void Reset()
        {
            s_loadedAssetsLookup = new(); s_loadedManifests = new(); s_loadedShardDependencyCount = new(); Calls.Clear();
        }
    }
}

namespace ForgeCore.Tests
{
    /// <summary>The one loaded-asset lookup and shard loader (<c>GameAssets</c>) against a game table that throws for a missing key.</summary>
    internal static class GameAssetsChecks
    {
        internal static void Run(Action<bool, string> check)
        {
            AssetShardManager.Reset();
            var prefab = new GameObject();
            AssetShardManager.s_loadedAssetsLookup!["ASSETS/TEST/A.PREFAB"] = prefab;

            check(ReferenceEquals(GameAssets.Get<GameObject>("Assets/Test/A.prefab"), prefab),
                "game assets: a loaded path returns the loaded object, whatever the caller's case");
            AssetShardManager.Calls.Clear();
            check(GameAssets.Get<GameObject>("Assets/Test/B.prefab") == null && AssetShardManager.Calls.Count == 0,
                "game assets: a path that is not loaded is null and never reaches the game's throwing getter");
            prefab.WasCollected = true;
            check(GameAssets.Get<GameObject>("Assets/Test/A.prefab") == null, "game assets: a collected object is not returned");
            prefab.WasCollected = false;
            var empty = GameAssets.ReadShard("Shard");
            check(!empty.HasManifest && !empty.Registered && !empty.HasCount && empty.Count == 0, "game assets: an unknown shard reads as empty");
            AssetShardManager.s_loadedManifests!["Shard"] = new Object();
            AssetShardManager.s_loadedShardDependencyCount!["Shard"] = 2;
            var loaded = GameAssets.ReadShard("Shard");
            check(loaded.Registered && loaded.HasCount && loaded.Count == 2 && loaded.SameRegistration(GameAssets.ReadShard("Shard")),
                "game assets: a registered shard reads its manifest and count, and two readings of it are one registration");
            AssetShardManager.s_loadedManifests["Shard"] = new Object();
            check(!loaded.SameRegistration(GameAssets.ReadShard("Shard")) && loaded.SameTables(GameAssets.ReadShard("Shard")),
                "game assets: a replaced manifest is a different registration in the same tables");
            AssetShardManager.Reset();
            var started = GameAssets.LoadShard("Shard", _ => { });
            GameAssets.LoadShard(AssetBundleName.Gear, AssetBundleShard.S1, _ => { });
            GameAssets.IncreaseShardDependency("Shard");
            GameAssets.UnloadShard("Shard", "why");
            check(started && string.Join("|", AssetShardManager.Calls) == "load:Shard:Additive|load:Gear_S1:Additive|increase:Shard|unload:Shard:why",
                "game assets: shards load additively through the game's loader, are pinned and released by name");
            AssetShardManager.Reset();
        }
    }
}
