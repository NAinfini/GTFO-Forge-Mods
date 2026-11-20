using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace ForgeFrameGen;

/// <summary>Texture LOD bias while the world renders below the screen's resolution: log2(render / screen) − 1 (the DLSS programming guide's
/// value), added to every loaded mipmapped <see cref="Texture2D"/> except the ones sprites draw from (the HUD and the menus are drawn at
/// the screen's resolution). Unity 2019.4 has no global bias, so each texture is set and its own value remembered;
/// <see cref="Restore"/> puts back every value this class still owns (one another script changed since is left alone).</summary>
internal static class MipBias
{
    private static Dictionary<IntPtr, (Texture2D Texture, float Original)> Biased = new();
    private static readonly HashSet<IntPtr> Interface = new();
    private static float _bias;

    internal static int Count => Biased.Count;

    /// <summary>Sets <paramref name="bias"/> on every loaded mipmapped texture, the ones already biased included; 0 restores. Called when the
    /// render size changes and when the game state changes (a level loads its textures).</summary>
    internal static void Apply(float bias)
    {
        if (bias == 0f)
        {
            Restore();
            return;
        }
        var watch = Stopwatch.StartNew();
        Interface.Clear();
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            var texture = found.TryCast<Sprite>()?.texture;
            if (texture != null) Interface.Add(texture.Pointer);
        }
        // Only the textures loaded now are kept: one a level unloaded is dropped with its wrapper, and an object that later takes its
        // address is read afresh (a wrapper whose object was collected is not that texture's).
        var loaded = new Dictionary<IntPtr, (Texture2D Texture, float Original)>(Biased.Count);
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Texture2D>()))
        {
            var texture = found.TryCast<Texture2D>();
            if (texture == null || texture.mipmapCount <= 1) continue;
            bool known = Biased.TryGetValue(texture.Pointer, out var entry) && !entry.Texture.WasCollected;
            if (Interface.Contains(texture.Pointer))
            {
                if (known && Math.Abs(texture.mipMapBias - (entry.Original + _bias)) < 1e-4f) texture.mipMapBias = entry.Original;
                continue;
            }
            if (!known) entry = (texture, texture.mipMapBias);
            else if (Math.Abs(texture.mipMapBias - (entry.Original + _bias)) >= 1e-4f) entry = (texture, texture.mipMapBias); // another script set it since
            loaded[texture.Pointer] = entry;
            texture.mipMapBias = entry.Original + bias;
        }
        Biased = loaded;
        _bias = bias;
        if (ForgeCore.Log.Developer) Plugin.Info.LogInfo("Mip bias " + bias.ToString("F3") + " on " + Biased.Count + " textures in " + watch.ElapsedMilliseconds + " ms");
    }

    internal static void Restore()
    {
        foreach (var (texture, original) in Biased.Values)
            if (!texture.WasCollected && texture != null && Math.Abs(texture.mipMapBias - (original + _bias)) < 1e-4f)
                texture.mipMapBias = original;
        Biased.Clear();
        _bias = 0f;
    }
}
