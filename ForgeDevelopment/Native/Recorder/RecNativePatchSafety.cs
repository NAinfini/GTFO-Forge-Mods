using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;

namespace ForgeDevelopment.Native;

// Read-only metadata discovery. Runtime admits original bytes and owns the bridge, installation and provenance; which native
// bodies the game shares between methods is ForgeCore's index (SharedNativeBodies).
internal static class RecNativePatchSafety
{
    internal static bool ValueReceiver(Type? type)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (current.IsValueType || current.FullName == "Il2CppSystem.ValueType") return true;
        return false;
    }
    internal static string? RejectionReason(MethodBase method)
    {
        try
        {
            var field = PointerField(method);
            if (field == null) return !method.IsStatic && ValueReceiver(method.DeclaringType) ? "non-native-value-type-helper" : null;
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return "unverified-native-architecture";
            if (!OperatingSystem.IsWindows()) return "unverified-native-platform";
            if (field.GetValue(null) is not IntPtr info || info == IntPtr.Zero) return "native-body-unavailable";
            if (!NativeMemory.Readable(info, IntPtr.Size, false)) return "native-method-info-unreadable";
            var body = Marshal.ReadIntPtr(info);
            if (body == IntPtr.Zero) return "native-body-unavailable";
            return ForgeCore.SharedNativeBodies.Refusal(method, body);
        }
        catch (Exception error) { return "native-metadata-unreadable:" + error.GetType().Name; }
    }
    internal static string ObservationScope(MethodBase method)
        => PointerField(method) == null ? "managed-method" : "native-method";

    // Which static field holds a generated method's native info pointer is reflection metadata of a loaded
    // assembly and does not change, but resolving it walks the declaring type each time (8+ calls per hook).
    // The field's value, which can change when a body is patched, is still read on every call.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, FieldInfo?> PointerFields = new();
    private static FieldInfo? PointerField(MethodBase method)
        => PointerFields.GetOrAdd(method, static m => Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m));
}
