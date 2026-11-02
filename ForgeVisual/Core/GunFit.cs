using System;
using System.Numerics;

namespace ForgeVisual;

/// <summary>
/// Where a gun model goes on a game gun so the game's hands land on it. Every point is in the game gun's own frame (+Z along the
/// barrel, +Y up, +X right); the model's points are measured with the model facing +Z and at its own size, its origin on the gun's.
/// The model is scaled so its two hand points are as far apart along the barrel as the game's two hand nodes (the grips stay under
/// the hands whatever the model's length), then moved so its right hand point is on the game's right hand node along the barrel,
/// its two hand points are as close to the game's as one common height allows, and its bore is on the game's bore across. When both
/// have a sight point, the height is instead the one that puts the model's sight on the game's (the game aims by its sight height).
/// Equal scale limits fix the scale (a model already at the size it should have).
/// </summary>
internal static class GunFit
{
    /// <summary>Hand points closer than this along the barrel give no scale (one hand, or both on one grip).</summary>
    internal const float MinSpan = 0.05f;

    /// <summary>The scale and the move (in the game gun's frame, after scaling) of the model; null when a point is not a finite number.</summary>
    internal static (float Scale, Vector3 Move)? Solve(Vector3 nativeRight, Vector3 nativeLeft, Vector3? nativeMuzzle,
        Vector3 modelRight, Vector3 modelLeft, Vector3? modelMuzzle, float minScale, float maxScale,
        Vector3? nativeSight = null, Vector3? modelSight = null)
    {
        if (!Finite(nativeRight) || !Finite(nativeLeft) || !Finite(modelRight) || !Finite(modelLeft)
            || (nativeMuzzle is { } nm && !Finite(nm)) || (modelMuzzle is { } mm && !Finite(mm))
            || (nativeSight is { } ns && !Finite(ns)) || (modelSight is { } ms && !Finite(ms))
            || !(minScale > 0f) || !(maxScale >= minScale)) return null;
        var nativeSpan = nativeLeft.Z - nativeRight.Z;
        var modelSpan = modelLeft.Z - modelRight.Z;
        var scale = nativeSpan >= MinSpan && modelSpan >= MinSpan ? Math.Clamp(nativeSpan / modelSpan, minScale, maxScale) : 1f;
        var z = nativeRight.Z - scale * modelRight.Z;
        var y = nativeSight is { } sn && modelSight is { } sm
            ? sn.Y - scale * sm.Y
            : ((nativeRight.Y - scale * modelRight.Y) + (nativeLeft.Y - scale * modelLeft.Y)) * 0.5f;
        var x = nativeMuzzle is { } n && modelMuzzle is { } m ? n.X - scale * m.X : nativeRight.X - scale * modelRight.X;
        return (scale, new Vector3(x, y, z));
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
