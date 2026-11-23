using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The one cached main camera the camera-facing signs, labels and zone outlines share. `Camera.main` finds
/// the camera by tag and wraps it again on every call, and each of those readers asked for it on every tick; the
/// camera object itself only changes with the scene or when the game swaps its main camera, so the cached one is
/// kept while it is alive and is looked up again when it is not or when the game state changes.
/// Main thread only.</summary>
internal static class MapCamera
{
    private static Camera? _camera;
    private static bool _attached;

    internal static Camera? Current()
    {
        var camera = _camera;
        if (camera != null && !camera.WasCollected) return camera;
        camera = Camera.main;
        _camera = camera != null && !camera.WasCollected ? camera : null;
        _transform = null;
        return _camera;
    }

    // The transform getter wraps the native object again on each call and most readers ask every tick, so it is kept with the camera.
    private static Transform? _transform;

    internal static Transform? Transform()
    {
        var camera = Current();
        if (camera == null) return null;
        if (_transform is not { WasCollected: false } cached) _transform = cached = camera.transform;
        return cached;
    }

    /// <summary>Takes the cache for one session: the game-state change drops it, so a scene's camera is looked up
    /// again. The session gives it back with <see cref="Detach"/>.</summary>
    internal static void Attach()
    {
        if (_attached) return;
        _attached = true;
        ForgeCore.GameEvents.StateChanged += StateChanged;
    }

    internal static void Detach()
    {
        _camera = null; _transform = null;
        if (!_attached) return;
        _attached = false;
        ForgeCore.GameEvents.StateChanged -= StateChanged;
    }

    private static void StateChanged() { _camera = null; _transform = null; }
}
