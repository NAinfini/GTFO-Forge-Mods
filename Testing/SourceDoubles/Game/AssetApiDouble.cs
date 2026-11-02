namespace GTFO.API
{
    /// <summary>GTFO-API's loaded-asset entry over the project's AssetShardManager double: the key is upper-cased, a missing key is false.</summary>
    public static class AssetAPI
    {
        public static bool ContainsAsset(string name) => AssetShards.AssetShardManager.s_loadedAssetsLookup!.ContainsKey(name.ToUpper());
        public static T GetLoadedAsset<T>(string name) where T : UnityEngine.Object => AssetShards.AssetShardManager.GetLoadedAsset<T>(name, false)!;
    }
}
