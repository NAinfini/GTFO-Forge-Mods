using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace ForgeCore;

/// <summary>Asset bundles. The same file (or the same embedded bytes) is loaded once and the loaded bundle is shared, so two mods
/// or two levels never load it twice; a missing file, a damaged bundle or a missing asset returns null and logs one error under the
/// mod's name.</summary>
public static class Assets
{
    private static readonly Dictionary<string, AssetBundle> Bundles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bundle at <paramref name="path"/> (relative paths are taken from the calling mod's own folder only by the caller).</summary>
    public static AssetBundle? Load(string modId, string path)
    {
        var full = Path.GetFullPath(path);
        if (Bundles.TryGetValue(full, out var loaded) && loaded != null && !loaded.WasCollected) return loaded;
        if (!File.Exists(full)) return Fail<AssetBundle>(modId, "Asset bundle " + full + " is missing.");
        try
        {
            var bundle = AssetBundle.LoadFromFile(full);
            if (bundle == null) return Fail<AssetBundle>(modId, "Asset bundle " + full + " could not be loaded (damaged, or built for another Unity version).");
            Keep(bundle);
            Bundles[full] = bundle;
            return bundle;
        }
        catch (Exception error) { return Fail<AssetBundle>(modId, "Asset bundle " + full + " could not be loaded: " + error); }
    }

    /// <summary>A bundle from bytes (an embedded resource, a shader bundle written into the DLL), loaded once per mod and key.</summary>
    public static AssetBundle? LoadBytes(string modId, string key, byte[] bytes)
    {
        var id = modId + "|" + key;
        if (Bundles.TryGetValue(id, out var loaded) && loaded != null && !loaded.WasCollected) return loaded;
        if (bytes == null || bytes.Length == 0) return Fail<AssetBundle>(modId, "Asset bundle " + key + " has no bytes.");
        try
        {
            var bundle = AssetBundle.LoadFromMemory(bytes);
            if (bundle == null) return Fail<AssetBundle>(modId, "Asset bundle " + key + " could not be loaded from its bytes.");
            Keep(bundle);
            Bundles[id] = bundle;
            return bundle;
        }
        catch (Exception error) { return Fail<AssetBundle>(modId, "Asset bundle " + key + " could not be loaded: " + error); }
    }

    /// <summary>The asset <paramref name="name"/> of type <typeparamref name="T"/> in <paramref name="bundle"/>.</summary>
    public static T? Get<T>(string modId, AssetBundle bundle, string name) where T : UnityEngine.Object
    {
        if (bundle == null) throw new ArgumentNullException(nameof(bundle));
        try
        {
            var asset = bundle.LoadAsset(name, Il2CppType.Of<T>());
            var typed = asset == null ? null : ((Il2CppObjectBase)asset).TryCast<T>();
            if (typed != null)
            {
                Keep(typed);
                return typed;
            }
            return Fail<T>(modId, "Asset " + name + " (" + typeof(T).Name + ") is not in bundle " + bundle.name + ".");
        }
        catch (Exception error) { return Fail<T>(modId, "Asset " + name + " could not be read from bundle " + bundle.name + ": " + error); }
    }

    /// <summary>Marks a loaded asset (or an object a mod keeps, such as a texture, mesh or pooled root) so the game's unused-asset
    /// sweep, which runs on every scene change, never unloads it. Returns the asset so a load can be written as one expression.</summary>
    public static T Keep<T>(T asset) where T : UnityEngine.Object
    {
        if (asset == null) throw new ArgumentNullException(nameof(asset));
        asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return asset;
    }

    private static T? Fail<T>(string modId, string message) where T : class
    {
        Log.For(modId).ErrorOnce(message);
        return null;
    }
}
