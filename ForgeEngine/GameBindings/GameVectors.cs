namespace ForgeEngine.GameBindings;

/// <summary>The one conversion from the runtime's allocation-free vector cell to Unity's float vector.</summary>
internal static class GameVectors
{
    /// <summary>Three finite coordinates as a Unity vector; a missing vector, or a coordinate the float cannot hold,
    /// is no point at all and is never clamped.</summary>
    public static bool TryUnity((double X, double Y, double Z)? value, out UnityEngine.Vector3 vector)
    {
        vector = default;
        if (value is not { } point
            || !float.IsFinite((float)point.X) || !float.IsFinite((float)point.Y) || !float.IsFinite((float)point.Z))
            return false;
        vector = new UnityEngine.Vector3((float)point.X, (float)point.Y, (float)point.Z);
        return true;
    }

    /// <summary><see cref="TryUnity"/> as a nullable: null where there is no point.</summary>
    public static UnityEngine.Vector3? Unity((double X, double Y, double Z)? value)
        => TryUnity(value, out var vector) ? vector : null;
}
