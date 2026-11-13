using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>PNG or JPEG bytes to a texture, the one decoder the libraries and packages share. The header is read first
/// (<see cref="Png.TrySize"/>, <see cref="Png.TryJpegSize"/>) so a file that declares more than it may is refused before anything
/// allocates, and a texture the game could not decode is destroyed instead of left behind. <see cref="Queue"/> spreads the work over
/// frames; <c>Decode</c> does it at once. Main thread only: the game's decoder and compressor are Unity calls.</summary>
public static class Images
{
    /// <summary>Milliseconds of queued image work a frame may take; a step is started while the frame has used less. One step (decoding,
    /// or compressing, one picture) cannot be split, so a frame takes at least the first one however long it runs.</summary>
    public const double FrameBudgetMs = 4;

    static readonly Queue<ImageJob> Jobs = new();
    static readonly Stopwatch Watch = new();
    static bool _pumping;

    /// <summary>Decodes <paramref name="image"/> as <c>Decode</c> would with the same options, in two steps on later frames (the decode,
    /// then the DXT compression and dropping the CPU copy), within <see cref="FrameBudgetMs"/> a frame, in the order queued. On the main
    /// thread <paramref name="done"/> gets the texture (the caller owns it) and an empty failure, or null and why. Cancel the job to drop it;
    /// a texture already decoded for it is destroyed and <paramref name="done"/> is not called.</summary>
    public static ImageJob Queue(byte[] image, Action<Texture2D?, string> done, bool linear = false, bool mipChain = false, int maxSide = 8192,
        bool normalMap = false, bool compress = false)
        => Enqueue(new ImageJob(image, done, linear, mipChain, maxSide, normalMap, compress));

    /// <summary>Uploads texture data that is already in the GPU format (compressed blocks made ahead of time, say) in one step on a
    /// later frame, in the same queue and frame budget as <see cref="Queue"/>: a <paramref name="width"/> x <paramref name="height"/>
    /// <paramref name="format"/> texture (with Unity's full mip chain when <paramref name="mipChain"/>), filled from
    /// <paramref name="data"/> after <paramref name="offset"/> with <c>LoadRawTextureData</c> (levels largest first, rows from the
    /// bottom), then sent to the GPU without a CPU copy. Nothing is decoded or compressed, so the step costs a copy and the upload.
    /// <paramref name="done"/> as for <see cref="Queue"/>; the failure names why the game refused the data (too little for the size).</summary>
    public static ImageJob QueueRaw(byte[] data, int offset, int width, int height, TextureFormat format, bool mipChain, bool linear, Action<Texture2D?, string> done)
        => Enqueue(new ImageJob(data, done, linear, mipChain, 0, false, false) { Raw = true, Offset = offset, Width = width, Height = height, Format = format });

    static ImageJob Enqueue(ImageJob job)
    {
        Jobs.Enqueue(job);
        if (!_pumping) { _pumping = true; Frame.OnUpdate("NAinfini.ForgeCore", Pump); }
        return job;
    }

    static void Pump()
    {
        if (Jobs.Count == 0) return;
        Watch.Restart();
        do
        {
            var job = Jobs.Peek();
            if (job.Cancelled)
            {
                Jobs.Dequeue();
                if (job.Texture != null) UnityEngine.Object.Destroy(job.Texture);
                continue;
            }
            if (job.Raw)
            {
                Jobs.Dequeue();
                job.Texture = Upload(job, out var refused);
                job.Finish(refused);
                continue;
            }
            if (job.Texture == null)
            {
                job.Texture = Read(job.Image, out var failure, job.Linear, false, job.MipChain, job.MaxSide, 0, 0, job.NormalMap, job.Compress);
                if (job.Texture == null) { Jobs.Dequeue(); job.Finish(failure); continue; }
                if (job.Compress) continue; // compressed on the next step
            }
            Jobs.Dequeue();
            Finish(job.Texture, false, job.NormalMap, job.Compress);
            job.Finish("");
        } while (Jobs.Count > 0 && Watch.Elapsed.TotalMilliseconds < FrameBudgetMs);
    }

    /// <summary>The texture of <paramref name="image"/>, or null with <paramref name="failure"/> naming why. <paramref name="linear"/> keeps the pixels
    /// as numbers (a metal map) instead of sRGB colour; <paramref name="readable"/> keeps a CPU copy for <c>GetPixels</c>; <paramref name="mipChain"/> builds mips
    /// (call <c>Apply(true, ...)</c> yourself after changing the filter). <paramref name="maxSide"/> caps either side; a non-zero
    /// <paramref name="width"/> x <paramref name="height"/> demands exactly that size. <paramref name="normalMap"/> reads a tangent-space normal map
    /// (OpenGL +Y, x in red) and stores it linear in the layout the game's own normal maps have on its gear shader (DXT5nm: x in alpha, y in
    /// green, red and blue 1), which both of Unity's normal unpackings read alike. <paramref name="compress"/> compresses it to DXT (a quarter
    /// of raw RGBA) when both sides are a multiple of 4, and leaves it raw otherwise. The caller owns the texture and destroys it.</summary>
    internal static Texture2D? Decode(byte[] image, out string failure, bool linear = false, bool readable = false, bool mipChain = false, int maxSide = 8192,
        int width = 0, int height = 0, bool normalMap = false, bool compress = false)
    {
        var texture = Read(image, out failure, linear, readable, mipChain, maxSide, width, height, normalMap, compress);
        if (texture != null) Finish(texture, readable, normalMap, compress);
        return texture;
    }

    // The decode: header checks, LoadImage and the normal-map swizzle. Keeps the CPU copy when Finish still needs it.
    static Texture2D? Read(byte[] image, out string failure, bool linear, bool readable, bool mipChain, int maxSide, int width, int height, bool normalMap, bool compress)
    {
        failure = "";
        if (!Png.TrySize(image, out var declaredWidth, out var declaredHeight) && !Png.TryJpegSize(image, out declaredWidth, out declaredHeight))
        { failure = "the file is not a PNG or JPEG"; return null; }
        if (declaredWidth > maxSide || declaredHeight > maxSide) { failure = "the picture is larger than " + maxSide + " pixels on a side"; return null; }
        if (width != 0 && (declaredWidth != width || declaredHeight != height)) { failure = "the picture is not " + width + " x " + height; return null; }
        // Swizzling and compressing need the pixels; the CPU copy goes once they are done.
        var keepPixels = readable || normalMap || compress;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain && !normalMap, linear || normalMap);
        var native = new Il2CppStructArray<byte>(image);
        try
        {
            if (!ImageConversion.LoadImage(texture, native, !keepPixels))
            {
                failure = "the game could not decode the picture";
                UnityEngine.Object.Destroy(texture);
                return null;
            }
        }
        catch (Exception error)
        {
            failure = "the game could not decode the picture: " + error;
            UnityEngine.Object.Destroy(texture);
            return null;
        }
        finally { GC.KeepAlive(native); }
        if (!normalMap) return texture;
        // LoadImage gives RGB24 for a picture without alpha; the swizzled copy needs an alpha channel.
        var pixels = texture.GetPixels32();
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            pixels[i] = new Color32(255, p.g, 255, p.r);
        }
        var swizzled = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, mipChain, true);
        swizzled.SetPixels32(pixels);
        swizzled.Apply(mipChain, false);
        UnityEngine.Object.Destroy(texture);
        return swizzled;
    }

    static unsafe Texture2D? Upload(ImageJob job, out string failure)
    {
        failure = "";
        var texture = new Texture2D(job.Width, job.Height, job.Format, job.MipChain, job.Linear);
        try
        {
            fixed (byte* p = job.Image) texture.LoadRawTextureData((IntPtr)(p + job.Offset), job.Image.Length - job.Offset);
            texture.Apply(false, true);
            return texture;
        }
        catch (Exception error)
        {
            failure = "the game refused the texture data: " + error.Message;
            UnityEngine.Object.Destroy(texture);
            return null;
        }
    }

    // The compression and dropping the CPU copy kept for it.
    static void Finish(Texture2D texture, bool readable, bool normalMap, bool compress)
    {
        if (compress && texture.width % 4 == 0 && texture.height % 4 == 0) texture.Compress(true);
        if ((normalMap || compress) && !readable) texture.Apply(false, true);
    }
}

/// <summary>One picture queued with <see cref="Images.Queue"/>.</summary>
public sealed class ImageJob
{
    internal readonly byte[] Image;
    internal readonly bool Linear, MipChain, NormalMap, Compress;
    internal readonly int MaxSide;
    internal Texture2D? Texture; // decoded, waiting for its second step
    internal bool Raw; // QueueRaw: Image holds GPU-format data
    internal int Offset, Width, Height;
    internal TextureFormat Format;
    readonly Action<Texture2D?, string> _done;

    internal ImageJob(byte[] image, Action<Texture2D?, string> done, bool linear, bool mipChain, int maxSide, bool normalMap, bool compress)
    {
        Image = image; _done = done; Linear = linear; MipChain = mipChain; MaxSide = maxSide; NormalMap = normalMap; Compress = compress;
    }

    /// <summary>True once cancelled; its callback never runs.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>True once its callback ran.</summary>
    public bool Done { get; private set; }

    /// <summary>Drops the job if its callback has not run yet.</summary>
    public void Cancel() { if (!Done) Cancelled = true; }

    internal void Finish(string failure)
    {
        Done = true;
        var texture = Texture;
        Texture = null;
        _done(texture, failure);
    }
}
