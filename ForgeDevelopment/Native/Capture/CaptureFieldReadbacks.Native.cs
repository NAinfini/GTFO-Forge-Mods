using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace ForgeDevelopment.Native;

internal static class CaptureFieldReadbackNative
{
    private static CaptureFieldReadbacks.Entry[]? _entries;
    private static string? _loadError;
    private static readonly Dictionary<string, Type> Types = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, MethodInfo> Casts = new();

    internal static void Prepare()
    {
        if (_entries != null || _loadError != null) return;
        try
        {
            var directory = Path.GetDirectoryName(typeof(CaptureFieldReadbackNative).Assembly.Location)
                ?? throw new InvalidOperationException("readback package directory unavailable");
            var text = File.ReadAllText(Path.Combine(directory, "probes", "capture-readbacks.forge"));
            _entries = CaptureFieldReadbacks.Parse(text);
        }
        catch (Exception error) { _loadError = error.GetType().Name + ": " + error.Message; }
    }

    internal static IEnumerable<int> CollectSlices(CaptureSnapshot snapshot)
    {
        Prepare();
        if (_loadError != null)
        {
            snapshot.AcquisitionComplete = false;
            snapshot.FieldCompleteness = snapshot.CoverageCompleteness = "incomplete";
            snapshot.Section("nativeReadbacks", "capture-readbacks.forge/nativeReadbacks").Rows.Add(
                new CaptureRow("nativeReadback", "plan", "plan").Field("status", "root-unavailable")
                    .Field("error", _loadError).Value("value", RecProjectionValue.Error("unknown", _loadError)));
            yield break;
        }
        foreach (var step in CaptureFieldReadbacks.Collect(snapshot, _entries!,
            Roots, Cast, () => RecTime.Frame, Unavailable)) yield return step;
    }

    internal static void Reset()
    { _entries = null; _loadError = null; Types.Clear(); Casts.Clear(); }

    private static string? Unavailable(object value) => RecReflect.Probe.NativeReadUnavailable(value)
        ?? (RecReflect.Probe.IsDestroyed(value) ? "collected-or-null-native-wrapper" : null);

    private static Type Find(string name)
    {
        if (!Types.TryGetValue(name, out var type))
        {
            type = ExperimentTypes.Find(name) ?? throw new InvalidOperationException("Readback type unavailable: " + name);
            Types.Add(name, type);
        }
        return type;
    }

    private static object? Cast(object value, string name)
    {
        var type = Find(name);
        if (type.IsInstanceOfType(value)) return value;
        if (value is not Il2CppObjectBase native || native.WasCollected)
            throw new InvalidOperationException("Readback native wrapper unavailable");
        if (!Casts.TryGetValue(type, out var cast))
        {
            cast = typeof(Il2CppObjectBase).GetMethod("TryCast", Type.EmptyTypes)!.MakeGenericMethod(type);
            Casts.Add(type, cast);
        }
        return cast.Invoke(native, null);
    }

    private static IEnumerable<object> Roots(CaptureFieldReadbacks.Entry entry)
    {
        var type = Find(entry.Type);
        if (entry.Source == "static") yield return type;
        else if (entry.Source == "components")
        {
            if (!typeof(Component).IsAssignableFrom(type)) throw new InvalidOperationException("Readback type is not a Component");
            foreach (var component in UnityEngine.Object.FindObjectsOfType(Il2CppType.From(type)))
            {
                var wrapped = Cast(component, entry.Type);
                if (wrapped == null) throw new InvalidOperationException("Found component could not be cast to requested type");
                yield return wrapped;
            }
        }
        else if (entry.Source == "blocks")
        {
            using var read = new CaptureDataBlocks.TableRead(type, null);
            while (read.Next(out var block)) if (block != null) yield return block;
            if (!read.Complete) throw new InvalidOperationException("Readback table incomplete: " + read.Status);
        }
        else
        {
            var getter = type.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
                ?? throw new InvalidOperationException("Readback Current singleton getter unavailable");
            yield return getter.GetValue(null) ?? throw new InvalidOperationException("Readback singleton is null");
        }
    }
}
