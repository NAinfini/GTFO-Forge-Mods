using System;
using System.Diagnostics.CodeAnalysis;

namespace ForgeCore;

/// <summary>Liveness of a game object without the Unity <c>==</c> overload. That overload is a call into the game on every use, which in
/// per-frame loops over many objects (enemy slots, placed-model batches) cost a tenth of a millisecond a frame; a destroyed object has a
/// zeroed native pointer, so reading that field answers the same question in managed code. Main thread only.</summary>
public static class NativeObjects
{
    /// <summary>True when <paramref name="target"/> is null, its managed wrapper was collected, or its native object was destroyed (the one definition of "gone" for every package).</summary>
    public static bool Gone([NotNullWhen(false)] UnityEngine.Object? target) => target is null || target.WasCollected || target.m_CachedPtr == IntPtr.Zero;
}
