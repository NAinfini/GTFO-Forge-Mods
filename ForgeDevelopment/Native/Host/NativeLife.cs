using System;

namespace ForgeDevelopment.Native;

/// <summary>One native object's life as the console tells lives apart: its address, Unity instance id and the world
/// (<see cref="RecTime.WorldEpoch"/>) it was seen in. A pooled object reused in another world, or another object at a reused
/// address, is a different life; nothing is guessed from position or name.</summary>
internal sealed record NativeLife(long Pointer, int Instance, long WorldEpoch)
{
    internal static NativeLife? Of(UnityEngine.Object? value)
        => value == null || value.WasCollected || value.Pointer == IntPtr.Zero ? null
            : new(value.Pointer.ToInt64(), value.GetInstanceID(), RecTime.WorldEpoch);

    /// <summary>Whether <paramref name="value"/> is still exactly this life in the current world.</summary>
    internal bool IsCurrent(UnityEngine.Object? value) => Of(value) == this;
}
