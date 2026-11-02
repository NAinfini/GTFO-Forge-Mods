using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// Direct engine calls for the outline's per-frame reads (enemy and camera positions, the camera's rotation, the screen height). The
/// generated wrappers of these Unity members return their struct through the game's boxing invoke, which allocates on the game's heap
/// every call; these resolve the engine's own internal calls once and write straight into a local.
/// </summary>
internal static class EngineReads
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadVector(IntPtr self, out Vector3 value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadQuaternion(IntPtr self, out Quaternion value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadInt();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadBounds(IntPtr self, out Bounds value);

    private static ReadBounds? _bounds;
    private static ReadVector? _position;
    private static ReadQuaternion? _rotation;
    private static ReadInt? _screenHeight;
    private static string? _failure;

    /// <summary>Resolves every call once. False (and <paramref name="why"/>) when the engine lacks one of them.</summary>
    internal static bool Prepare(out string why)
    {
        why = _failure ?? "";
        if (_failure != null) return false;
        if (_position != null) return true;
        try
        {
            _position = IL2CPP.ResolveICall<ReadVector>("UnityEngine.Transform::get_position_Injected");
            _rotation = IL2CPP.ResolveICall<ReadQuaternion>("UnityEngine.Transform::get_rotation_Injected");
            _screenHeight = IL2CPP.ResolveICall<ReadInt>("UnityEngine.Screen::get_height");
            try { _bounds = IL2CPP.ResolveICall<ReadBounds>("UnityEngine.Renderer::get_bounds_Injected"); }
            catch (Exception error)
            {
                _bounds = null;
                Plugin.Logger.ErrorOnce("The renderer bounds call is missing, so an outline follows the enemy's agent instead of its drawn body during wake-up and climb-out animations: " + error);
            }
            return true;
        }
        catch (Exception error)
        {
            _position = null;
            why = _failure = error.ToString();
            return false;
        }
    }

    internal static Vector3 Position(Transform t)
    {
        _position!(t.Pointer, out var value);
        return value;
    }

    /// <summary>The centre of the renderer's world bounds: where its body is drawn this frame (a skinned body follows its bones, so it
    /// is not where the enemy's agent stands while a wake-up or climb-out animation plays). False when the engine lacks the call.</summary>
    internal static bool Center(Renderer r, out Vector3 center)
    {
        center = default;
        if (_bounds == null) return false;
        _bounds(r.Pointer, out var bounds);
        center = bounds.center;
        return true;
    }

    internal static Quaternion Rotation(Transform t)
    {
        _rotation!(t.Pointer, out var value);
        return value;
    }

    internal static int ScreenHeight() => _screenHeight!();
}
