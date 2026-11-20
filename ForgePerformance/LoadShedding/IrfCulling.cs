using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgePerformance.LoadShedding;

// The game's instanced decoration (IRF) updates and draws every feature in its own LateUpdate, once per frame
// per feature, and passes no camera to Draw, so every camera that renders its layer draws it again.
//  - WorldCameraOnly: a Draw call without a camera gets the main camera, the one the game already passes to Update.
//  - OffscreenSkip: a static feature whose fixed bounds are fully outside the main camera frustum skips its whole
//    LateUpdate for that frame (compute dispatch, copy count and draw).
//  - PhysicsHalfRate: a feature whose plants spring and sway runs its compute update every other frame, staggered
//    across features, with the time of the skipped frame added to the next update so the sway keeps its speed.
// The flags are read by the hooks on every call; the local test probe flips them by reflection to compare in
// one level. Reads of the game's fields are raw reads at the offsets checked in SheddingHooks.VerifyLayout.
// The main camera and its planes are resolved once per frame, at the first hook call after the controller's Update
// tick marked a new frame (NewFrame), so a hook call costs no Time.frameCount, Camera.main or Unity null check.
internal static unsafe class IrfCulling
{
    internal static bool WorldCameraOnly;
    internal static bool OffscreenSkip;
    internal static bool PhysicsHalfRate;

    // InstancedRenderFeature
    internal const int FeatureForceStatic = 0x39, FeatureDescriptor = 0x40, FeatureDynamicTransforms = 0x48, FeatureDisablePhysics = 0x4A,
        FeatureRenderer = 0x50, FeatureEnemyRoot = 0x58;
    // InstancedRenderFeatureDescriptor
    internal const int DescriptorSpringBehaviour = 0x60;
    // InstancedRenderFeatureRenderer
    internal const int RendererStaticBounds = 0x94, RendererUpdatedOnce = 0xC1, RendererFeature = 0x58, RendererGameObjectIsStatic = 0x12C;

    private static readonly float[] Planes = new float[IrfSkipPolicy.PlaneCount * 4];
    private static Il2CppStructArray<Plane>? _planeBuffer;
    private static Camera? _main;
    private static bool _mainResolved;
    private static int _frame, _planeState; // _planeState: 0 not computed this frame, 1 ready, 2 failed
    private static float _delta, _previousDelta;
    private static readonly System.Collections.Generic.Dictionary<IntPtr, int> SkippedAt = new(); // renderer -> frame whose update it skipped
    private static long _physicsUpdates, _physicsSkipped;
    private static float _cameraX, _cameraY, _cameraZ;
    private static long _considered, _skipped, _redirected;
    private static string? _failure;

    internal static void Configure(SheddingSettings settings)
    {
        WorldCameraOnly = settings.IrfWorldCameraOnly;
        OffscreenSkip = settings.IrfOffscreenSkip;
        PhysicsHalfRate = settings.IrfPhysicsHalfRate;
        ResetFrame();
    }

    internal static void Reset()
    {
        WorldCameraOnly = OffscreenSkip = PhysicsHalfRate = false;
        _considered = _skipped = _redirected = _physicsUpdates = _physicsSkipped = 0; _failure = null;
        ResetFrame();
    }

    internal static (long Considered, long Skipped, long Redirected, string? Failure) Counts => (_considered, _skipped, _redirected, _failure);
    internal static (long Updates, long Skipped) PhysicsCounts => (_physicsUpdates, _physicsSkipped);

    // Level counts start again for the next level; the flags stay.
    internal static void BeginLevel()
    {
        _considered = _skipped = _redirected = _physicsUpdates = _physicsSkipped = 0; _failure = null;
        ResetFrame();
    }

    private static void ResetFrame()
    {
        _main = null; _mainResolved = false; _planeState = 0; _delta = _previousDelta = 0;
        SkippedAt.Clear();
    }

    // Called once per frame from the controller's Update tick, before any LateUpdate of this frame.
    internal static void NewFrame(int frame, float delta)
    {
        _frame = frame;
        _previousDelta = _delta; _delta = delta;
        _main = null; _mainResolved = false; _planeState = 0;
    }

    private static Camera? MainCamera()
    {
        if (_mainResolved) return _main;
        _mainResolved = true;
        var main = Camera.main;
        _main = main == null ? null : main;
        return _main;
    }

    // Draw hook: only a call without a camera is changed.
    internal static void RedirectDraw(ref Camera camera)
    {
        if (camera is not null) return;
        var main = MainCamera();
        if (main is null) return;
        camera = main;
        _redirected++;
    }

    // LateUpdate hook: true means this feature does nothing this frame.
    internal static bool ShouldSkip(IntPtr feature)
    {
        _considered++;
        var main = MainCamera();
        if (main is null) return false;
        if (_planeState == 0) _planeState = LoadPlanes(main) ? 1 : 2;
        if (_planeState != 1) return false;
        var f = (byte*)feature;
        var renderer = *(byte**)(f + FeatureRenderer);
        if (renderer == null) return false;
        var fixedBounds = renderer[RendererGameObjectIsStatic] != 0 || f[FeatureForceStatic] != 0;
        var bounds = (float*)(renderer + RendererStaticBounds);
        var skip = IrfSkipPolicy.CanSkip(
            f[FeatureDynamicTransforms] != 0, *(IntPtr*)(f + FeatureEnemyRoot) != IntPtr.Zero,
            renderer[RendererUpdatedOnce] != 0, fixedBounds,
            Planes, _cameraX, _cameraY, _cameraZ,
            bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], bounds[5]);
        if (skip) _skipped++;
        return skip;
    }

    // Renderer.Update hook (PhysicsHalfRate): false skips this frame's compute update of a feature whose plants spring
    // (descriptor SpringBehaviour not None and physics not disabled). Features that follow moving transforms or an enemy, and
    // ones not updated yet, run every frame. The skipped frame's time is added to the next update.
    internal static bool PhysicsUpdate(IntPtr renderer, ref float deltaTime)
    {
        var r = (byte*)renderer;
        var f = *(byte**)(r + RendererFeature);
        if (f == null || r[RendererUpdatedOnce] == 0 || f[FeatureDisablePhysics] != 0 || f[FeatureDynamicTransforms] != 0
            || *(IntPtr*)(f + FeatureEnemyRoot) != IntPtr.Zero) return true;
        var descriptor = *(byte**)(f + FeatureDescriptor);
        if (descriptor == null || *(int*)(descriptor + DescriptorSpringBehaviour) == 0) return true;
        if (((_frame ^ (int)((long)renderer >> 4)) & 1) != 0) { _physicsSkipped++; SkippedAt[renderer] = _frame; return false; }
        // Only a renderer skipped on the previous frame owes that frame's time; one that just became eligible runs on its own time.
        if (SkippedAt.Remove(renderer, out var skippedFrame) && skippedFrame == _frame - 1) deltaTime += _previousDelta;
        _physicsUpdates++;
        return true;
    }

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
