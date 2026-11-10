using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgePerformance.LoadShedding;

// The game's instanced decoration (IRF) updates and draws every feature in its own LateUpdate, once per frame
// per feature, and passes no camera to Draw, so every camera that renders its layer draws it again.
//  - WorldCameraOnly: a Draw call without a camera gets the main camera, the one the game already passes to Update.
//  - OffscreenSkip: a static feature whose fixed bounds are fully outside the main camera frustum skips its whole
//    LateUpdate for that frame (compute dispatch, copy count and draw).
// Both flags are read by the hooks on every call; the local test probe flips them by reflection to compare in
// one level. Reads of the game's fields are raw reads at the offsets checked in SheddingHooks.VerifyLayout.
internal static class IrfCulling
{
    internal static bool WorldCameraOnly;
    internal static bool OffscreenSkip;

    // InstancedRenderFeature
    internal const int FeatureForceStatic = 0x39, FeatureDynamicTransforms = 0x48, FeatureRenderer = 0x50, FeatureEnemyRoot = 0x58;
    // InstancedRenderFeatureRenderer
    internal const int RendererStaticBounds = 0x94, RendererUpdatedOnce = 0xC1, RendererGameObjectIsStatic = 0x12C;

    private static readonly float[] Planes = new float[IrfSkipPolicy.PlaneCount * 4];
    private static Il2CppStructArray<Plane>? _planeBuffer;
    private static Camera? _main;
    private static int _frame = -1;
    private static int _planeState; // 0 not computed this frame, 1 ready, 2 failed
    private static float _cameraX, _cameraY, _cameraZ;
    private static long _considered, _skipped, _redirected;
    private static string? _failure;

    internal static void Configure(SheddingSettings settings)
    {
        WorldCameraOnly = settings.IrfWorldCameraOnly;
        OffscreenSkip = settings.IrfOffscreenSkip;
        ResetFrame();
    }

    internal static void Reset()
    {
        WorldCameraOnly = OffscreenSkip = false;
        _considered = _skipped = _redirected = 0; _failure = null;
        ResetFrame();
    }

    internal static (long Considered, long Skipped, long Redirected, string? Failure) Counts => (_considered, _skipped, _redirected, _failure);

    // Level counts start again for the next level; the flags stay.
    internal static void BeginLevel()
    {
        _considered = _skipped = _redirected = 0; _failure = null;
        ResetFrame();
    }

    private static void ResetFrame()
    {
        _main = null; _frame = -1; _planeState = 0;
    }

    private static Camera? MainCamera()
    {
        var frame = Time.frameCount;
        if (frame == _frame) return _main;
        _frame = frame; _planeState = 0; _main = Camera.main;
        return _main;
    }

    // Draw hook: only a call without a camera is changed.
    internal static void RedirectDraw(ref Camera camera)
    {
        if (camera != null) return;
        var main = MainCamera();
        if (main == null) return;
        camera = main;
        _redirected++;
    }

    // LateUpdate hook: true means this feature does nothing this frame.
    internal static bool ShouldSkip(IntPtr feature)
    {
        _considered++;
        var main = MainCamera();
        if (main == null) return false;
        if (_planeState == 0) _planeState = LoadPlanes(main) ? 1 : 2;
        if (_planeState != 1) return false;
        var renderer = Marshal.ReadIntPtr(feature, FeatureRenderer);
        if (renderer == IntPtr.Zero) return false;
        var fixedBounds = Marshal.ReadByte(renderer, RendererGameObjectIsStatic) != 0 || Marshal.ReadByte(feature, FeatureForceStatic) != 0;
        var skip = IrfSkipPolicy.CanSkip(
            Marshal.ReadByte(feature, FeatureDynamicTransforms) != 0, Marshal.ReadIntPtr(feature, FeatureEnemyRoot) != IntPtr.Zero,
            Marshal.ReadByte(renderer, RendererUpdatedOnce) != 0, fixedBounds,
            Planes, _cameraX, _cameraY, _cameraZ,
            Read(renderer, RendererStaticBounds), Read(renderer, RendererStaticBounds + 4), Read(renderer, RendererStaticBounds + 8),
            Read(renderer, RendererStaticBounds + 12), Read(renderer, RendererStaticBounds + 16), Read(renderer, RendererStaticBounds + 20));
        if (skip) _skipped++;
        return skip;
    }

    private static float Read(IntPtr pointer, int offset) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer, offset));

    private static bool LoadPlanes(Camera camera)
    {
        try
        {
            _planeBuffer ??= new Il2CppStructArray<Plane>(IrfSkipPolicy.PlaneCount);
            GeometryUtility.CalculateFrustumPlanes(camera, _planeBuffer);
            for (var i = 0; i < IrfSkipPolicy.PlaneCount; i++)
            {
                var plane = _planeBuffer[i];
                var normal = plane.normal;
                Planes[i * 4] = normal.x; Planes[i * 4 + 1] = normal.y; Planes[i * 4 + 2] = normal.z; Planes[i * 4 + 3] = plane.distance;
            }
            var position = camera.transform.position;
            _cameraX = position.x; _cameraY = position.y; _cameraZ = position.z;
            return true;
        }
        catch (Exception error)
        {
            // Reported at the end of the level; every feature then keeps its native behaviour.
            _failure ??= error.GetType().Name + ": " + error.Message;
            return false;
        }
    }
}
