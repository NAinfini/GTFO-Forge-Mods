using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using WindVolume;
using AffectorSet = Il2CppSystem.Collections.Generic.HashSet<WindVolume.WindVolumeAffector>;

namespace ForgePerformance.LoadShedding;

// Resolve metadata once. These reads mirror the QA interop getters, but return
// native identities without constructing the managed wrappers in those getters.
internal static class WindNativePointers
{
    private static readonly IntPtr InstancesField = IL2CPP.GetIl2CppField(
        Il2CppClassPointerStore<WindVolumeAffector>.NativeClassPtr, "instances");
    private static readonly int CameraOffset = checked((int)IL2CPP.il2cpp_field_get_offset(
        IL2CPP.GetIl2CppField(Il2CppClassPointerStore<WindVolumeCamera>.NativeClassPtr, "m_camera")));
    private static readonly IntPtr TransformMethod = (IntPtr)(typeof(Component).GetField(
        "NativeMethodInfoPtr_get_transform_Public_get_Transform_0", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(Component).FullName, "get_transform")).GetValue(null)!;

    internal static unsafe IntPtr Registry()
    {
        var pointer = IntPtr.Zero;
        IL2CPP.il2cpp_field_static_get_value(InstancesField, &pointer);
        return pointer;
    }

    internal static IntPtr Camera(WindVolumeCamera owner) => Marshal.ReadIntPtr(owner.Pointer, CameraOffset);

    internal static AffectorSet WrapRegistry(IntPtr pointer) => new(pointer);
    internal static Camera WrapCamera(WindVolumeCamera owner, IntPtr pointer) => new(pointer);
    internal static Transform WrapTransform(Component component, IntPtr pointer) => new(pointer);

    internal static unsafe IntPtr Transform(Component component)
    {
        var exception = IntPtr.Zero;
        var pointer = IL2CPP.il2cpp_runtime_invoke(TransformMethod, component.Pointer, null, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        return pointer;
    }
}
