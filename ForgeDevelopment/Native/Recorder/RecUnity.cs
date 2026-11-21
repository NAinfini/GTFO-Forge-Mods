using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeDevelopment.Native;

/// <summary>
/// The IL2CPP half of the reader. It answers the three questions the pure reader cannot: whether a game object is
/// still alive, what a wrapper's identity is, and how to read a field the interop type does not project as a
/// property. It also writes the engine's own value types, whose data lives in private native fields.
/// </summary>
internal sealed class RecIl2CppProbe : IRecValueProbe
{
    internal static readonly RecIl2CppProbe Instance = new();
    private readonly ConcurrentDictionary<(IntPtr Class, string Name), Lazy<Func<object, object?>?>> _nativeMembers = new();

    internal void ResetMemberCache() => _nativeMembers.Clear();

    // Hook arguments are observations, not lifetime leases. Even after a successful
    // original call, a wrapper may refer to a constructing or destroyed engine object.
    public string? NativeReadUnavailable(object value)
        => value is Il2CppObjectBase || value is Type type && typeof(Il2CppObjectBase).IsAssignableFrom(type)
            ? RecObservation.NativeReadUnavailable() : null;

    private const uint Vector3Hash = 0x8C1A3F27;
    private const uint Vector2Hash = 0x3E9B7D14;
    private const uint Vector4Hash = 0x51D2C6A8;
    private const uint QuaternionHash = 0xA7349E5B;
    private const uint ColorHash = 0x1F86B3D0;
    private const uint Color32Hash = 0x6D20F4A9;
    private const uint RectHash = 0xB5C81E63;
    private const uint BoundsHash = 0x27A9D4F1;
    private const uint IntPtrHash = 0xE40B7C95;

    /// <summary>Wrapper collection only. A nonzero GC-handle target does not prove
    /// that Unity's native component is initialized or alive.</summary>
    public bool IsDestroyed(object value)
    {
        try
        {
            if (value is Object unity)
            {
                var pointer = unity.Pointer;
                return pointer == IntPtr.Zero || unity.WasCollected;
            }
            if (value is Il2CppObjectBase interop) return interop.WasCollected || interop.Pointer == IntPtr.Zero;
            return false;
        }
        catch (Exception) { return true; } // Il2Cpp read guard: a wrapper that cannot be read is treated as destroyed.
    }

    /// <summary>Only the wrapper pointer, including for Unity objects. Never dereference
    /// an engine object merely to label an observation.</summary>
    public bool TryInstanceId(object value, out long id)
    {
        id = 0;
        try
        {
            if (value is Il2CppObjectBase interop)
            {
                id = interop.Pointer.ToInt64();
                return true;
            }
        }
        catch (Exception)
        {
            // A collected wrapper has no readable pointer. Use managed identity;
            // do not retry the same accessor or call the wrapper's native hash code.
        }
        return false;
    }

    public bool IsUnityObject(object value)
    {
        if (value is Object) return true;
        if (NativeReadUnavailable(value) != null) return false;
        var klass = NativeClass(value);
        return klass != IntPtr.Zero && IL2CPP.il2cpp_class_is_assignable_from(Il2CppClassPointerStore<Object>.NativeClassPtr, klass);
    }

    public IntPtr NativeClass(object value)
    {
        if (NativeReadUnavailable(value) != null) return IntPtr.Zero;
        try { return value is Il2CppObjectBase interop && !IsDestroyed(value) ? interop.ObjectClass : IntPtr.Zero; }
        catch (Exception) { return IntPtr.Zero; } // Il2Cpp read guard: no native class is IntPtr.Zero, which callers already test.
    }

    public string? NativeClassName(IntPtr nativeClass)
    {
        if (RecObservation.Active) return null;
        if (nativeClass == IntPtr.Zero) return null;
        try
        {
            var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(nativeClass));
            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(nativeClass));
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
        catch (Exception) { return null; } // Il2Cpp read guard: no class name is the null callers already handle.
    }

    public IEnumerable<string>? NativeFieldNames(object value)
    {
        if (NativeReadUnavailable(value) != null) return null;
        var klass = NativeClass(value);
        if (klass == IntPtr.Zero) return null;
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (; klass != IntPtr.Zero; klass = IL2CPP.il2cpp_class_get_parent(klass))
        {
            var iterator = IntPtr.Zero;
            IntPtr field;
            while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
            {
                if ((IL2CPP.il2cpp_field_get_flags(field) & (int)FieldAttributes.Static) != 0) continue;
                var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field));
                if (name != null && seen.Add(name)) fields.Add(name);
            }
        }
        return fields;
    }

    public bool TryScalar(object value, out object? scalar)
    {
        scalar = null;
        if (NativeReadUnavailable(value) != null) return false;
        if (value is not Il2CppObjectBase target || target.WasCollected) return false;
        var klass = NativeClass(value);
        if (klass == IntPtr.Zero) return false;
        var isEnum = IL2CPP.il2cpp_class_is_enum(klass);
        var type = isEnum ? IL2CPP.il2cpp_class_enum_basetype(klass) : IL2CPP.il2cpp_class_get_type(klass);
        var kind = IL2CPP.il2cpp_type_get_type(type);
        if (kind == 14) { scalar = IL2CPP.Il2CppStringToManaged(target.Pointer); return true; }
        if (kind is not (2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 24 or 25)) return false;
        var data = IL2CPP.il2cpp_object_unbox(target.Pointer);
        object raw = kind switch
        {
            2 => Marshal.ReadByte(data) != 0,
            3 => ((char)(ushort)Marshal.ReadInt16(data)).ToString(),
            4 => unchecked((sbyte)Marshal.ReadByte(data)),
            5 => Marshal.ReadByte(data),
            6 => Marshal.ReadInt16(data),
            7 => unchecked((ushort)Marshal.ReadInt16(data)),
            8 => Marshal.ReadInt32(data),
            9 => unchecked((uint)Marshal.ReadInt32(data)),
            10 => Marshal.ReadInt64(data),
            11 => unchecked((ulong)Marshal.ReadInt64(data)),
            12 => Marshal.PtrToStructure<float>(data),
            13 => Marshal.PtrToStructure<double>(data),
            24 => Marshal.ReadIntPtr(data).ToInt64(),
            _ => unchecked((ulong)Marshal.ReadIntPtr(data).ToInt64())
        };
        scalar = isEnum ? new { type = NativeClassName(klass), value = raw, status = "native-enum-numeric" } : raw;
        return true;
    }

    /// <summary>Resolve the concrete native class, including when the caller holds an interface/base wrapper.
    /// Prefer its generated accessors (which return typed values); otherwise enumerate fields and parents. Never
    /// call GetIl2CppField: it logs a miss before the caller can handle it. RecReflect caches this result, including null.</summary>
    public Func<object, object?>? ResolveNativeMember(IntPtr nativeClass, string name)
        => RecObservation.Active ? null : _nativeMembers.GetOrAdd((nativeClass, name), static key => new Lazy<Func<object, object?>?>(
            () => ResolveNativeMemberCore(key.Class, key.Name))).Value;

    private static Func<object, object?>? ResolveNativeMemberCore(IntPtr nativeClass, string name)
    {
        for (var klass = nativeClass; klass != IntPtr.Zero; klass = IL2CPP.il2cpp_class_get_parent(klass))
        {
            var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
            var className = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
            var fullName = string.IsNullOrEmpty(ns) ? className : ns + "." + className;
            if (ns == "System" || ns?.StartsWith("System.", StringComparison.Ordinal) == true) fullName = "Il2Cpp" + fullName;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = fullName == null ? null : assembly.GetType(fullName, false);
                if (type == null || type.ContainsGenericParameters || !typeof(Il2CppObjectBase).IsAssignableFrom(type)) continue;
                var read = RecReflect.ResolveManagedMember(type, name);
                var constructor = type.GetConstructor(new[] { typeof(IntPtr) });
                if (read != null && constructor != null)
                    return target => RecIl2CppProbe.Instance.NativeReadUnavailable(target) != null ? null : read(type.IsInstanceOfType(target) ? target
                        : constructor.Invoke(new object[] { ((Il2CppObjectBase)target).Pointer }));
            }
        }
        for (var klass = nativeClass; klass != IntPtr.Zero; klass = IL2CPP.il2cpp_class_get_parent(klass))
        {
            var iterator = IntPtr.Zero;
            IntPtr field;
            while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iterator)) != IntPtr.Zero)
            {
                if (!string.Equals(Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field)), name, StringComparison.Ordinal)) continue;
                var resolvedField = field;
                return target => ReadNativeField(resolvedField, (Il2CppObjectBase)target);
            }
        }
        return null;
    }

    private static object? ReadNativeField(IntPtr field, Il2CppObjectBase target)
    {
        if (RecIl2CppProbe.Instance.NativeReadUnavailable(target) != null) return null;
        if (target.WasCollected || target.Pointer == IntPtr.Zero) throw new InvalidOperationException("Native object is destroyed.");
        var boxed = IL2CPP.il2cpp_field_get_value_object(field, target.Pointer);
        if (boxed == IntPtr.Zero) return null;
        return new Il2CppSystem.Object(boxed);
    }

    public bool Handles(Type type)
    {
        var hash = Hash(type.FullName ?? type.Name);
        return hash is Vector3Hash or Vector2Hash or Vector4Hash or QuaternionHash or ColorHash or Color32Hash or RectHash or BoundsHash or IntPtrHash;
    }

    /// <summary>Writes the engine's value types member by member. The members are read through the same guarded reader
    /// as everything else, so a game build that renames one degrades to a reported error rather than a crash.</summary>
    public void Write(RecReflect.RecWriter writer, object value, int depth)
    {
        writer.Json.WriteStartObject();
        writer.Json.WriteString("$type", value.GetType().FullName ?? "");
        foreach (var member in Members(value.GetType()))
        {
            writer.Json.WritePropertyName(member);
            try { writer.WriteNamed(RecReflect.ReadMember(value, member), member, depth + 1); }
            catch (Exception error) { writer.Json.WriteString(member, "$error:" + error.GetType().Name); }
        }
        writer.Json.WriteEndObject();
    }

    private static readonly string[] Vector3Members = { "x", "y", "z" };
    private static readonly string[] Vector2Members = { "x", "y" };
    private static readonly string[] Vector4Members = { "x", "y", "z", "w" };
    private static readonly string[] QuaternionMembers = { "x", "y", "z", "w" };
    private static readonly string[] ColorMembers = { "r", "g", "b", "a" };
    private static readonly string[] RectMembers = { "x", "y", "width", "height" };
    private static readonly string[] BoundsMembers = { "center", "extents", "size", "min", "max" };
    private static readonly string[] IntPtrMembers = { "value" };

    private static string[] Members(Type type)
    {
        var hash = Hash(type.FullName ?? type.Name);
        if (hash == Vector3Hash) return Vector3Members;
        if (hash == Vector2Hash) return Vector2Members;
        if (hash == Vector4Hash) return Vector4Members;
        if (hash == QuaternionHash) return QuaternionMembers;
        if (hash == ColorHash || hash == Color32Hash) return ColorMembers;
        if (hash == RectHash) return RectMembers;
        if (hash == BoundsHash) return BoundsMembers;
        if (hash == IntPtrHash) return IntPtrMembers;
        return Array.Empty<string>();
    }

    /// <summary>FNV-1a over the type's name. It is computed rather than referenced so this file needs no compile-time
    /// knowledge of the engine's assemblies, and two builds with different type identities stay apart.</summary>
    private static uint Hash(string name) => ForgeCore.Fnv.MixUtf8(ForgeCore.Fnv.Offset32, name);
}

/// <summary>What the game-side reader needs: install the IL2CPP probe, read engine versions, and keep the camera
/// helpers the bookmark uses.</summary>
internal static class RecUnity
{
    internal static void Install()
    {
        RecIl2CppProbe.Instance.ResetMemberCache();
        RecReflect.Probe = RecIl2CppProbe.Instance;
    }

    internal static string GameVersion => Safe(() => Application.version, "unavailable") ?? "unavailable";
    internal static string UnityVersion => Safe(() => Application.unityVersion, "unavailable") ?? "unavailable";

    private static T? Safe<T>(Func<T> read, T? fallback = default)
    {
        try { return read(); }
        catch (Exception) { return fallback; } // Il2Cpp read guard: the caller passes the explicit fallback it records for an unreadable value.
    }
}

/// <summary>
/// The recorder's hotkeys. They are polled from the authoring monitor's Update, which is the only place the plugin
/// already runs per frame; the entries are config keys because the keys themselves are configuration.
/// </summary>
internal static class RecHotkeys
{
    internal static void Poll()
    {
        if (!RecSession.Active) return;
        if (Pressed(Settings.RecorderBookmarkKey.Value)) RecSession.Bookmark(Settings.RecorderBookmarkLabel.Value);
        if (Pressed(Settings.RecorderSnapshotKey.Value))
        {
            CaptureRegistry.SnapshotAll("hotkey");
            CaptureRegistry.SnapshotLayout("hotkey");
        }
        if (Pressed(Settings.RecorderScreenshotKey.Value)) RecSession.Screenshot("hotkey");
        if (Pressed(Settings.RecorderPanelKey.Value)) ExperimentPanel.Toggle();
    }

    private static bool Pressed(KeyCode key)
    {
        try { return key != KeyCode.None && Input.GetKeyDown(key); }
        catch (Exception) { return false; } // Input read guard: an unreadable key state is not pressed.
    }
}

/// <summary>
/// The screen-corner notice. A session that reached its budget says so where the player is looking, not only in a
/// log they will never read; it is drawn by the authoring monitor and disappears on its own.
/// </summary>
internal static class RecNotice
{
    private static string _text = "";
    private static long _until;
    private static GUIStyle? _style;

    internal static void Show(string message)
    {
        _text = RecReflect.Truncate(message, 200);
        _until = Environment.TickCount64 + 6000;
    }

    internal static void OnGui()
    {
        if (Environment.TickCount64 > _until || _text.Length == 0) return;
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.LowerLeft, wordWrap = false };
        _style.normal.textColor = Color.white;
        var rect = new Rect(12f, Screen.height - 48f, Screen.width - 24f, 40f);
        GUI.Label(rect, _text, _style);
    }
}
