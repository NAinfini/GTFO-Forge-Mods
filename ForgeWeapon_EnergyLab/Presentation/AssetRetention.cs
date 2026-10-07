using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

internal static class AssetRetention
{
    /// <summary>Keeps an asset that only this plugin references. Unity's unused-asset sweep at level
    /// transitions cannot see references held by managed plugin code and would destroy it.</summary>
    internal static T Keep<T>(T asset) where T : Object
    {
        asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return asset;
    }
}
