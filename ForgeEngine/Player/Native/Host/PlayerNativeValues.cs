using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>Conversions at the SDK/native boundary shared by player command handlers.</summary>
internal static class PlayerNativeValues
{
    internal static double[]? Coordinates(Vector3 value)
        => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z)
            ? new double[] { value.x, value.y, value.z } : null;
}
