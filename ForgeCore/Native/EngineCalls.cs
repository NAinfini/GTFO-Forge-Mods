using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgeCore;

/// <summary>
/// Direct engine calls for per-frame reads and turns (the outline's and the world features': enemy health bars, world waypoints, hit
/// feedback; the visual part's bone chains and pose copies; ForgeReplay's enemy samples). The generated wrappers of these Unity members
/// go through the game's boxing invoke, which allocates on the game's heap every call; these resolve the engine's own internal calls once
/// and write straight into a local. All are present in this game build (their names are in <c>GameAssembly.dll</c>'s internal-call
/// table); the renderer bounds call is optional (<see cref="HasBounds"/>). The engine raises in native code for a destroyed object, so a
/// caller tests the object first (<see cref="NativeObjects.Gone(UnityEngine.Object?)"/>, or <see cref="NativeObjects.GoneAt"/> on the
/// pointer it holds). Main thread only.
/// </summary>
public static class EngineCalls
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadVector(IntPtr self, out Vector3 value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadQuaternion(IntPtr self, out Quaternion value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WriteQuaternion(IntPtr self, ref Quaternion value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadMatrix(IntPtr self, out Matrix4x4 value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadInt(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadScene(out PhysicsSceneHandle scene);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool RaycastTest(ref PhysicsSceneHandle scene, ref RayData ray, float maxDistance, int layerMask, int triggers);

    /// <summary>Unity's <c>PhysicsScene</c>: one handle.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PhysicsSceneHandle { public int Handle; }

    /// <summary>Unity's <c>Ray</c>: origin, then a unit direction.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RayData { public Vector3 Origin, Direction; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadBounds(IntPtr self, out BoundsData value);

    /// <summary>Unity's <c>Bounds</c>: centre, then half-size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BoundsData { public Vector3 Center, Extents; }

    private static ReadBounds? _bounds;
    private static ReadVector? _position;
    private static ReadQuaternion? _rotation;
    private static WriteQuaternion? _setRotation;
    private static ReadMatrix? _view, _projection, _localToWorld;
    private static ReadInt? _pixelHeight;
    private static RaycastTest? _raycast;
    private static PhysicsSceneHandle _scene;
    private static bool _sceneRead;
    private static string? _failure;

    /// <summary>After <see cref="Prepare"/>: the renderer bounds call exists (each user says what it does without it).</summary>
    internal static bool HasBounds => _bounds != null;

    /// <summary>Resolves every call once (later calls return at once). False (and <paramref name="why"/>) when the engine lacks one of
    /// them; nothing else here may be called then.</summary>
    public static bool Prepare(out string why)
    {
        why = _failure ?? "";
        if (_failure != null) return false;
        if (_position != null) return true;
        try
        {
            _position = IL2CPP.ResolveICall<ReadVector>("UnityEngine.Transform::get_position_Injected");
            _rotation = IL2CPP.ResolveICall<ReadQuaternion>("UnityEngine.Transform::get_rotation_Injected");
            _setRotation = IL2CPP.ResolveICall<WriteQuaternion>("UnityEngine.Transform::set_rotation_Injected");
            _localToWorld = IL2CPP.ResolveICall<ReadMatrix>("UnityEngine.Transform::get_localToWorldMatrix_Injected");
            _view = IL2CPP.ResolveICall<ReadMatrix>("UnityEngine.Camera::get_worldToCameraMatrix_Injected");
            _projection = IL2CPP.ResolveICall<ReadMatrix>("UnityEngine.Camera::get_projectionMatrix_Injected");
            _pixelHeight = IL2CPP.ResolveICall<ReadInt>("UnityEngine.Camera::get_pixelHeight");
            _raycast = IL2CPP.ResolveICall<RaycastTest>("UnityEngine.PhysicsScene::Internal_RaycastTest_Injected");
            var scene = IL2CPP.ResolveICall<ReadScene>("UnityEngine.Physics::get_defaultPhysicsScene_Injected");
            scene(out _scene);
            _sceneRead = true;
            try { _bounds = IL2CPP.ResolveICall<ReadBounds>("UnityEngine.Renderer::get_bounds_Injected"); }
            catch (Exception) { _bounds = null; }
            return true;
        }
        catch (Exception error)
        {
            _position = null;
            why = _failure = error.ToString();
            return false;
        }
    }

    internal static Vector3 Position(Transform t) => Position(t.Pointer);

    internal static Quaternion Rotation(Transform t) => Rotation(t.Pointer);

    /// <summary>The world position of the transform at <paramref name="transform"/> (a held <c>Transform</c> wrapper's <c>Pointer</c>).</summary>
    public static Vector3 Position(IntPtr transform)
    {
        _position!(transform, out var p);
        return p;
    }

    /// <summary>The world rotation of the transform at <paramref name="transform"/>.</summary>
    public static Quaternion Rotation(IntPtr transform)
    {
        _rotation!(transform, out var q);
        return q;
    }

    /// <summary>Sets the world rotation of the transform at <paramref name="transform"/>.</summary>
    public static void SetRotation(IntPtr transform, Quaternion rotation) => _setRotation!(transform, ref rotation);

    /// <summary>The local-to-world matrix of the transform at <paramref name="transform"/>: its position is column 3, a point in its own
    /// space goes to the world as <c>TransformPoint</c> puts it (<see cref="Point"/>).</summary>
    public static Matrix4x4 LocalToWorld(IntPtr transform)
    {
        _localToWorld!(transform, out var m);
        return m;
    }

    /// <summary><paramref name="m"/> applied to the point <paramref name="v"/>, in managed code (the interop's matrix and vector operators
    /// and constructors are calls into the game).</summary>
    public static Vector3 Point(in Matrix4x4 m, Vector3 v) => new()
    {
        x = m.m00 * v.x + m.m01 * v.y + m.m02 * v.z + m.m03,
        y = m.m10 * v.x + m.m11 * v.y + m.m12 * v.z + m.m13,
        z = m.m20 * v.x + m.m21 * v.y + m.m22 * v.z + m.m23,
    };

    /// <summary>A renderer's world bounds (centre and half-size): where its body is drawn this frame, a skinned body following its bones.
    /// False when the engine lacks the call.</summary>
    internal static bool Bounds(Renderer r, out Vector3 center, out Vector3 extents)
    {
        center = extents = default;
        if (_bounds == null) return false;
        _bounds(r.Pointer, out var b);
        center = b.Center;
        extents = b.Extents;
        return true;
    }

    /// <summary>The height of the camera's picture in pixels: the screen's, or its target texture's when it renders into one (ForgeFrameGen's render size).</summary>
    internal static int PixelHeight(Camera camera) => _pixelHeight!(camera.Pointer);

    /// <summary>The camera's world-to-camera and projection matrices.</summary>
    internal static void Matrices(Camera camera, out Matrix4x4 view, out Matrix4x4 projection)
    {
        _view!(camera.Pointer, out view);
        _projection!(camera.Pointer, out projection);
    }

    /// <summary>True when something on <paramref name="mask"/> lies between <paramref name="from"/> and <paramref name="to"/>
    /// (triggers ignored). Plain float maths: the interop's vector operators allocate.</summary>
    internal static bool Blocked(Vector3 from, Vector3 to, int mask)
    {
        float dx = to.x - from.x, dy = to.y - from.y, dz = to.z - from.z;
        var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (distance < 0.01f || !_sceneRead) return false;
        var ray = new RayData { Origin = from, Direction = new Vector3(dx / distance, dy / distance, dz / distance) };
        return _raycast!(ref _scene, ref ray, distance, mask, (int)QueryTriggerInteraction.Ignore);
    }
}
