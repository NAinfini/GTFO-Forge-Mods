using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>PNG bytes to a texture, the one decoder the libraries share. The header is read first (<see cref="Png.TrySize"/>) so a
/// file that declares more than it may is refused before anything allocates, and a texture the game could not decode is destroyed
/// instead of left behind. Main thread only.</summary>
public static class Images
{
    /// <summary>The texture of <paramref name="png"/>, or null with <paramref name="failure"/> naming why. <paramref name="linear"/> keeps the pixels
    /// as numbers (a normal map) instead of sRGB colour; <paramref name="readable"/> keeps a CPU copy for <c>GetPixels</c>; <paramref name="mipChain"/> builds mips
    /// (call <c>Apply(true, ...)</c> yourself after changing the filter). <paramref name="maxSide"/> caps either side; a non-zero
    /// <paramref name="width"/> x <paramref name="height"/> demands exactly that size. The caller owns the texture and destroys it.</summary>
    public static Texture2D? Decode(byte[] png, out string failure, bool linear = false, bool readable = false, bool mipChain = false, int maxSide = 8192, int width = 0, int height = 0)
    {
        failure = "";
        if (!Png.TrySize(png, out var declaredWidth, out var declaredHeight)) { failure = "the file is not a PNG"; return null; }
        if (declaredWidth > maxSide || declaredHeight > maxSide) { failure = "the picture is larger than " + maxSide + " pixels on a side"; return null; }
        if (width != 0 && (declaredWidth != width || declaredHeight != height)) { failure = "the picture is not " + width + " x " + height; return null; }
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain, linear);
        var native = new Il2CppStructArray<byte>(png);
        try
        {
            if (ImageConversion.LoadImage(texture, native, !readable)) return texture;
            failure = "the game could not decode the picture";
        }
        catch (Exception error) { failure = "the game could not decode the picture: " + error; }
        finally { GC.KeepAlive(native); }
        UnityEngine.Object.Destroy(texture);
        return null;
    }
}
