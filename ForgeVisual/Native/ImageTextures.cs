using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeVisual;

/// <summary>Decoded PNG images, one texture per key (the content hash the caller names it by), kept until <see cref="Dispose"/>.
/// A key whose bytes failed to decode stays failed, so a broken image is refused the same way every time instead of being
/// decoded again.</summary>
public sealed class ImageTextures : IDisposable
{
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);

    /// <summary>The texture of <paramref name="png"/> (expected <paramref name="width"/> x <paramref name="height"/>); on failure
    /// <paramref name="code"/> names why (<c>image-decode-failed</c>) and <paramref name="error"/> is the exception the decoder threw, if it threw.</summary>
    public bool TryGet(string key, byte[] png, int width, int height, out Texture2D? texture, out string code, out Exception? error)
    {
        code = ""; error = null;
        if (_failures.TryGetValue(key, out var prior)) { texture = null; code = prior; return false; }
        if (_textures.TryGetValue(key, out texture)) return true;
        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            name = "Forge image " + key,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        var native = new Il2CppStructArray<byte>(png);
        try
        {
            if (!ImageConversion.LoadImage(texture, native, true) || texture.width != width || texture.height != height)
                code = "image-decode-failed";
        }
        catch (Exception decodeError)
        {
            code = "image-decode-failed"; error = decodeError;
        }
        finally { GC.KeepAlive(native); }
        if (code.Length != 0) { Fail(key, texture, code); texture = null; return false; }
        _textures.Add(key, texture);
        return true;
    }

    private void Fail(string key, Texture2D texture, string code)
    {
        UnityEngine.Object.Destroy(texture);
        _failures[key] = code;
    }

    /// <summary>Destroys every texture and forgets the failures.</summary>
    public void Dispose()
    {
        foreach (var texture in _textures.Values) if (texture != null) UnityEngine.Object.Destroy(texture);
        _textures.Clear();
        _failures.Clear();
    }
}
