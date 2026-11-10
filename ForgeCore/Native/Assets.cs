using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace ForgeCore;

/// <summary>Asset bundles, keyed by the SHA-256 of their bytes: the same content is loaded once and the loaded bundle is shared, wherever
/// it sits and whichever mod asks, so two mods or two levels never load it twice; a missing file, a damaged bundle or a missing asset
/// returns null and logs one error under the mod's name.</summary>
public static class Assets
{
    private static readonly Dictionary<string, AssetBundle> Bundles = new(StringComparer.Ordinal);

    /// <summary>The bundle at <paramref name="path"/> (relative paths are taken from the calling mod's own folder only by the caller).</summary>
    public static AssetBundle? Load(string modId, string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return Fail<AssetBundle>(modId, "core-asset-bundle-missing", "Asset bundle " + full + " is missing.");
        try
        {
            string id;
            using (var stream = File.OpenRead(full)) id = Hash.Sha256Hex(stream);
            if (Bundles.TryGetValue(id, out var loaded) && loaded != null && !loaded.WasCollected) return loaded;
            var bundle = AssetBundle.LoadFromFile(full);
            if (bundle == null) return Fail<AssetBundle>(modId, "core-asset-bundle-not-loaded", "Asset bundle " + full + " could not be loaded (damaged, or built for another Unity version).");
            Keep(bundle);
            Bundles[id] = bundle;
            return bundle;
        }
        catch (Exception error) { return Fail<AssetBundle>(modId, "core-asset-bundle-load-failed", "Asset bundle " + full + " could not be loaded: " + error); }
    }

    /// <summary>A bundle from bytes (an embedded resource, a shader bundle written into the DLL), loaded once per content; <paramref name="key"/>
    /// names it in errors.</summary>
    public static AssetBundle? LoadBytes(string modId, string key, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return Fail<AssetBundle>(modId, "core-asset-bundle-no-bytes", "Asset bundle " + key + " has no bytes.");
        var id = Hash.Sha256Hex(bytes);
        if (Bundles.TryGetValue(id, out var loaded) && loaded != null && !loaded.WasCollected) return loaded;
        try
        {
            var bundle = AssetBundle.LoadFromMemory(bytes);
            if (bundle == null) return Fail<AssetBundle>(modId, "core-asset-bundle-bytes-not-loaded", "Asset bundle " + key + " could not be loaded from its bytes.");
            Keep(bundle);
            Bundles[id] = bundle;
            return bundle;
        }
        catch (Exception error) { return Fail<AssetBundle>(modId, "core-asset-bundle-load-failed", "Asset bundle " + key + " could not be loaded: " + error); }
    }

    /// <summary>Starts loading the bundle at <paramref name="path"/> over several frames: poll <see cref="BundleLoad.IsDone"/> from a coroutine
    /// or a per-frame step. The file's SHA-256 is read first and, when <paramref name="expectedSha256"/> is given, must match it (any letter
    /// case). Unlike <see cref="Load"/> the bundle is not shared or kept: the caller owns it and may unload it.</summary>
    public static BundleLoad LoadAsync(string modId, string path, string? expectedSha256 = null)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return BundleLoad.Failed(modId, "core-asset-bundle-missing", "Asset bundle " + full + " is missing.");
            string id;
            using (var stream = File.OpenRead(full)) id = Hash.Sha256Hex(stream);
            if (expectedSha256 != null && !string.Equals(id, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return BundleLoad.Failed(modId, "core-asset-bundle-changed", "Asset bundle " + full + " changed after it was registered.");
            return new BundleLoad(modId, full, AssetBundle.LoadFromFileAsync(full));
        }
        catch (Exception error) { return BundleLoad.Failed(modId, "core-asset-bundle-load-failed", "Asset bundle " + path + " could not be loaded: " + error); }
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
            return Fail<T>(modId, "core-asset-not-in-bundle", "Asset " + name + " (" + typeof(T).Name + ") is not in bundle " + bundle.name + ".");
        }
        catch (Exception error) { return Fail<T>(modId, "core-asset-read-failed", "Asset " + name + " could not be read from bundle " + bundle.name + ": " + error); }
    }

    /// <summary>Marks a loaded asset (or an object a mod keeps, such as a texture, mesh or pooled root) so the game's unused-asset
    /// sweep, which runs on every scene change, never unloads it. Returns the asset so a load can be written as one expression.</summary>
    public static T Keep<T>(T asset) where T : UnityEngine.Object
    {
        if (asset == null) throw new ArgumentNullException(nameof(asset));
        asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return asset;
    }

    internal static T? Fail<T>(string modId, string code, string message) where T : class
    {
        Log.For(modId).ErrorOnce(code, message);
        return null;
    }
}

/// <summary>A bundle <see cref="Assets.LoadAsync"/> is loading: <see cref="IsDone"/> turns true when the load ended, then <see cref="Bundle"/>
/// holds the bundle, or null when it failed (one error is logged under the mod's name).</summary>
public sealed class BundleLoad
{
    private readonly string _modId, _path;
    private AssetBundleCreateRequest? _request;
    private AssetBundle? _bundle;

    internal BundleLoad(string modId, string path, AssetBundleCreateRequest request) { _modId = modId; _path = path; _request = request; }

    private BundleLoad(string modId, string code, string message) { Assets.Fail<AssetBundle>(modId, code, message); _modId = modId; _path = ""; }

    internal static BundleLoad Failed(string modId, string code, string message) => new(modId, code, message);

    public bool IsDone
    {
        get
        {
            if (_request == null) return true;
            if (!_request.isDone) return false;
            _bundle = _request.assetBundle;
            _request = null;
            if (_bundle == null) Assets.Fail<AssetBundle>(_modId, "core-asset-bundle-not-loaded", "Asset bundle " + _path + " could not be loaded (damaged, or built for another Unity version).");
            return true;
        }
    }

    public AssetBundle? Bundle => IsDone ? _bundle : null;
}
