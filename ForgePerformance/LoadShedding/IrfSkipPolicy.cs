using System;

namespace ForgePerformance.LoadShedding;

// Pure rules for the two instanced-render (IRF) cuts. No game types, so they run offline.
internal static class IrfSkipPolicy
{
    // One-frame guard: the camera may still move after an IRF's LateUpdate, so a box is only skipped when it
    // is outside every frustum plane by this fixed margin plus this fraction of its distance from the camera.
    internal const float EdgeMargin = 1f;
    internal const float AngularGuard = 0.2f;
    internal const int PlaneCount = 6;

    // Whether this frame's LateUpdate of one IRF may be skipped: nothing in it could change what is drawn.
    // dynamicTransforms / enemyRoot / transforms that follow their object are recomputed every frame by the
    // native code, so those IRFs are never skipped. A static IRF skips only after its first transform update
    // and only when its fixed world bounds are fully outside the main camera frustum.
    internal static bool CanSkip(bool dynamicTransforms, bool hasEnemyRoot, bool updatedOnce, bool fixedBounds,
        ReadOnlySpan<float> planes, float cameraX, float cameraY, float cameraZ,
        float centerX, float centerY, float centerZ, float extentX, float extentY, float extentZ)
    {
        if (dynamicTransforms || hasEnemyRoot || !updatedOnce || !fixedBounds) return false;
        if (planes.Length != PlaneCount * 4) return false;
        if (!IsFinite(centerX) || !IsFinite(centerY) || !IsFinite(centerZ)
            || !IsFinite(extentX) || !IsFinite(extentY) || !IsFinite(extentZ)) return false;
        // A box that was never computed has no extent; keep the native behaviour for it.
        if (extentX < 0 || extentY < 0 || extentZ < 0 || (extentX == 0 && extentY == 0 && extentZ == 0)) return false;
        var dx = centerX - cameraX; var dy = centerY - cameraY; var dz = centerZ - cameraZ;
        var guard = EdgeMargin + AngularGuard * MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        for (var i = 0; i < PlaneCount; i++)
        {
            var nx = planes[i * 4]; var ny = planes[i * 4 + 1]; var nz = planes[i * 4 + 2]; var d = planes[i * 4 + 3];
            var reach = extentX * MathF.Abs(nx) + extentY * MathF.Abs(ny) + extentZ * MathF.Abs(nz);
            // Unity frustum planes face inward: a positive signed distance is inside.
            if (nx * centerX + ny * centerY + nz * centerZ + d + reach + guard < 0) return true;
        }
        return false;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
