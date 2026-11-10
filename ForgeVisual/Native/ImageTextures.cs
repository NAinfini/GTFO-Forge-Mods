using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeVisual;

/// <summary>Decoded PNG images, one texture per key (the content hash the caller names it by), kept until <see cref="Dispose"/>.
/// A key whose bytes failed to decode stays failed, so a broken image is refused the same way every time instead of being
/// decoded again.</summary>
public sealed class ImageTextures : IDisposable
{
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly bool _surface, _keep;

    /// <param name="surface">Textures for a 3D surface (a model's): a mip chain, trilinear filtering and repeating UVs. Off, they are flat boards: no mips, bilinear, clamped.</param>
    /// <param name="keep">Mark every texture with <see cref="ForgeCore.Assets.Keep"/>, for a cache that outlives a level's unused-asset sweep.</param>
    public ImageTextures(bool surface = false, bool keep = false)
    {
        _surface = surface;
        _keep = keep;
    }

    /// <summary>The texture of <paramref name="png"/> (expected <paramref name="width"/> x <paramref name="height"/>, checked against the PNG
    /// header before anything decodes); on failure <paramref name="code"/> names why (<c>image-decode-failed</c>) and <paramref name="error"/> is the exception the decoder threw, if it threw.
    /// <paramref name="linear"/> keeps the pixels as they are (a normal map's numbers are not colours); the default reads them as sRGB colour. One picture read both ways is two textures.</summary>
    public bool TryGet(string key, byte[] png, int width, int height, out Texture2D? texture, out string code, out Exception? error, bool linear = false)
    {
        code = ""; error = null;
        var slot = linear ? key + "|linear" : key;
        if (_failures.TryGetValue(slot, out var prior)) { texture = null; code = prior; return false; }
        if (_textures.TryGetValue(slot, out texture)) return true;
        // ForgeCore reads the header first, so a PNG of another size is refused before it decodes, and destroys a texture the game could not decode.
        texture = ForgeCore.Images.Decode(png, out var failure, linear, readable: _surface, mipChain: _surface, maxSide: Math.Max(width, height), width: width, height: height);
        if (texture == null || texture.width != width || texture.height != height)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
            _failures[slot] = code = "image-decode-failed";
            if (failure.Length != 0) error = new InvalidOperationException(failure);
            return false;
        }
        texture.name = "Forge image " + slot;
        texture.filterMode = _surface ? FilterMode.Trilinear : FilterMode.Bilinear;
        texture.wrapMode = _surface ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        if (_surface) texture.Apply(true, true);
        if (_keep) ForgeCore.Assets.Keep(texture);
        _textures.Add(slot, texture);
        return true;
    }

    /// <summary>Destroys every texture and forgets the failures.</summary>
    public void Dispose()
    {
        foreach (var texture in _textures.Values) if (texture != null) UnityEngine.Object.Destroy(texture);
        _textures.Clear();
        _failures.Clear();
    }
}
