using System;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>The vector reads the shot bodies (hitscan, bullet projectile, projectile launch) share.</summary>
internal static class ShotVectors
{
    /// <summary>One vector port as a game vector, or false when it is absent, not finite or outside the Single range.</summary>
    public static bool TryRead(CommandContext context, HandlerSlot slot, out Vector3 value)
    {
        value = default;
        if (context.Vector(slot) is not { } parts
            || !double.IsFinite(parts.X) || !double.IsFinite(parts.Y) || !double.IsFinite(parts.Z)
            || Math.Abs(parts.X) > float.MaxValue || Math.Abs(parts.Y) > float.MaxValue || Math.Abs(parts.Z) > float.MaxValue)
            return false;
        value = new Vector3((float)parts.X, (float)parts.Y, (float)parts.Z);
        return true;
    }

    /// <summary>One unit vector, or false for a direction with no length or a non-finite component. The cast is only ever
    /// handed a heading the game can normalize itself.</summary>
    public static bool Unit(Vector3 value, out Vector3 unit)
    {
        unit = value;
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z)) return false;
        var length = Math.Sqrt((double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z);
        if (!double.IsFinite(length) || length == 0d) return false;
        unit = new Vector3((float)(value.x / length), (float)(value.y / length), (float)(value.z / length));
        return true;
    }
}
