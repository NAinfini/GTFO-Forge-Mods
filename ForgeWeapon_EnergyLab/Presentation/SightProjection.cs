using System.Numerics;

namespace ForgeWeaponEnergyLabExperimental.Presentation;

internal static class SightProjection
{
    // Vanilla primary layer: ALTERNATIVE_PROJECTION_MODE, _ProjDist1 = 100 glass-local units.
    // Sight_12's native hierarchy has unit scale, matching this bundle's metre-scale optic coordinates.
    // All coordinates are optic-local. Rotation of the optic therefore rotates its virtual point.
    internal const float VirtualDistance = 100f;

    internal static bool TryOffset(Vector3 eye, float planeDepth, out Vector3 offset)
    {
        offset = default;
        if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z) ||
            !float.IsFinite(planeDepth) || eye.Z >= planeDepth) return false;
        var weight = VirtualDistance / (VirtualDistance + planeDepth - eye.Z);
        offset = new Vector3(eye.X * weight, eye.Y * weight, 0f);
        return true;
    }
}
