using System;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The player's view of the world for everything the kit draws over it (world points, enemy health bars, hit feedback): the camera the
/// player looks through (<see cref="HudScreen.WorldCamera"/>, looked up again every half second or when it is destroyed), its eye and its
/// world-to-clip transform, read once per frame through <see cref="EngineCalls"/>, so reading allocates nothing.
/// </summary>
internal static class WorldView
{
    private const float CameraRecheck = 0.5f;
    private static Camera? _camera;
    private static Transform? _transform;
    private static float _nextCamera;
    private static int _frame = -1;
    private static bool _ready, _failed;
    private static ClipTransform _clip;
    private static Vector3 _eye;

    /// <summary>The view of this frame; false while there is no camera or the engine calls are missing (logged once).</summary>
    internal static bool Read(out ClipTransform clip, out Vector3 eye)
    {
        var frame = Time.frameCount;
        if (frame != _frame)
        {
            _frame = frame;
            _ready = Refresh();
        }
        clip = _clip;
        eye = _eye;
        return _ready;
    }

    private static bool Refresh()
    {
        if (_failed) return false;
        if (!EngineCalls.Prepare(out var why))
        {
            _failed = true;
            Plugin.Error("ui-nothing-drawn-over-world-world-points", "Nothing is drawn over the world (world points, enemy health bars, hit feedback): an engine call is missing (" + why + ").");
            return false;
        }
        var now = Time.time;
        if (ForgeCore.NativeObjects.Gone(_camera) || ForgeCore.NativeObjects.Gone(_transform) || now >= _nextCamera)
        {
            _nextCamera = now + CameraRecheck;
            var camera = HudScreen.WorldCamera();
            if (camera == null) { _camera = null; _transform = null; return false; }
            if (_camera == null || _camera.Pointer != camera.Pointer) { _camera = camera; _transform = camera.transform; }
        }
        _eye = EngineCalls.Position(_transform!);
        _clip = EngineCalls.WorldToClip(_camera!);
        return true;
    }
}
