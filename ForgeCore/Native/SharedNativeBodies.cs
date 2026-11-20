using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ForgeCore;

/// <summary>Native bodies the game build shares between methods (identical code folding). A patch on a shared body runs for every
/// method on it, with an instance of another type, and two patches on one body re-enter each other; so a Forge patch target whose
/// body is shared is refused unless a loaded Forge package registers that exact body in its embedded <c>shared-native-bodies.json</c>
/// (target, RVA in GameAssembly.dll, every other method on the body). Tools/Forge/check-patch-bodies.py reads the same files and
/// checks the targets it can read from the sources against the game's dump. The body index walks every method of every loaded
/// class once, at the first question, and keeps only the shared bodies. Main thread only.</summary>
public static class SharedNativeBodies
{
    /// <summary>The reason a refused target carries, the same as ForgePerformance's own check writes.</summary>
    public const string Reason = "shared-native-method-body";
    internal const string RefusedCode = "native-hook-shared-body-refused";
    private const string Resource = "shared-native-bodies.json";
    private static Dictionary<IntPtr, List<IntPtr>>? _shared;
    private static readonly Dictionary<Assembly, Entry[]> Registries = new();
    /// <summary>Where the one index line goes (the developer log); set at ForgeCore's load.</summary>
    internal static Action<string>? Notice;

    /// <summary><see cref="Reason"/> when the native body at <paramref name="body"/> (the entry of <paramref name="target"/>) is shared
    /// with another method and no loaded Forge package registers exactly that body and set for it, else null.</summary>
    public static string? Refusal(MethodBase target, IntPtr body)
    {
        if (body == IntPtr.Zero || !(_shared ??= Index()).TryGetValue(body, out var onBody)) return null;
        return Decide(target.DeclaringType?.Name + "." + target.Name, body.ToInt64() - GetModuleHandle("GameAssembly.dll").ToInt64(),
            onBody.ConvertAll(MethodName), Registered());
    }

    /// <summary><see cref="Reason"/> unless <paramref name="registered"/> names <paramref name="target"/> at <paramref name="rva"/> with
    /// exactly the methods <paramref name="onBody"/> (every method on the body, the target included) less the target.</summary>
    internal static string? Decide(string target, long rva, IReadOnlyCollection<string> onBody, IEnumerable<Entry> registered)
    {
        var names = onBody.OrderBy(name => name, StringComparer.Ordinal).ToList();
        foreach (var entry in registered)
        {
            if (entry.Target != target || entry.Rva != rva) continue;
            if (entry.Sharers.Append(target).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(names, StringComparer.Ordinal)) return null;
        }
        return Reason;
    }

    /// <summary>"Type.Method" of every method on the body at <paramref name="body"/> when it is shared, else empty.</summary>
    public static IReadOnlyList<string> Sharers(IntPtr body)
        => body != IntPtr.Zero && (_shared ??= Index()).TryGetValue(body, out var onBody) ? onBody.ConvertAll(MethodName) : Array.Empty<string>();

    /// <summary>The registered entries of every loaded Forge assembly that embeds the registry; read again for an assembly loaded later.</summary>
    private static IEnumerable<Entry> Registered()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || assembly.GetName().Name?.StartsWith("Forge", StringComparison.Ordinal) != true) continue;
            if (!Registries.TryGetValue(assembly, out var entries)) Registries.Add(assembly, entries = Read(assembly));
            foreach (var entry in entries) yield return entry;
        }
    }

    internal static Entry[] Read(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(Resource);
        return stream == null ? Array.Empty<Entry>() : Parse(stream);
    }

    internal static Entry[] Parse(System.IO.Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().Select(e => new Entry(e.GetProperty("target").GetString()!,
            Convert.ToInt64(e.GetProperty("rva").GetString()!.Substring(2), 16),
            e.GetProperty("sharers").EnumerateArray().Select(s => s.GetString()!).ToArray())).ToArray();
    }

    internal sealed record Entry(string Target, long Rva, string[] Sharers);

    /// <summary>Every body more than one method info points at, with those infos: <paramref name="infos"/> in walk order.</summary>
    internal static Dictionary<IntPtr, List<IntPtr>> Shared(IEnumerable<(IntPtr Body, IntPtr Info)> infos)
    {
        var owners = new Dictionary<IntPtr, IntPtr>();
        var shared = new Dictionary<IntPtr, List<IntPtr>>();
        foreach (var (body, info) in infos)
        {
            if (body == IntPtr.Zero) continue;
            if (!owners.TryGetValue(body, out var first)) { owners.Add(body, info); continue; }
            if (first == info) continue;
            if (!shared.TryGetValue(body, out var onBody)) shared.Add(body, onBody = new List<IntPtr> { first });
            if (!onBody.Contains(info)) onBody.Add(info);
        }
        return shared;
    }

    private static Dictionary<IntPtr, List<IntPtr>> Index()
    {
        var started = Stopwatch.GetTimestamp();
        var shared = Shared(Methods());
        Notice?.Invoke("core-native-body-index shared=" + shared.Count + " ms="
            + ((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        return shared;
    }

    private static IEnumerable<(IntPtr, IntPtr)> Methods()
    {
        var assemblies = DomainAssemblies(Domain(), out var count);
        for (ulong i = 0; i < count.ToUInt64(); i++)
        {
            var image = AssemblyImage(Marshal.ReadIntPtr(assemblies, checked((int)i * IntPtr.Size)));
            var classes = ImageClassCount(image).ToUInt64();
            for (uint j = 0; j < classes; j++)
            {
                var klass = ImageClass(image, j);
                var iterator = IntPtr.Zero;
                IntPtr info;
                while ((info = ClassMethods(klass, ref iterator)) != IntPtr.Zero) yield return (Marshal.ReadIntPtr(info), info);
            }
        }
    }

    private static string MethodName(IntPtr info) =>
        Marshal.PtrToStringAnsi(ClassName(MethodClass(info))) + "." + Marshal.PtrToStringAnsi(MethodNameOf(info));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string module);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_domain_get", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Domain();
    [DllImport("GameAssembly", EntryPoint = "il2cpp_domain_get_assemblies", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr DomainAssemblies(IntPtr domain, out UIntPtr count);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_assembly_get_image", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AssemblyImage(IntPtr assembly);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_image_get_class_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr ImageClassCount(IntPtr image);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_image_get_class", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ImageClass(IntPtr image, uint index);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_class_get_methods", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ClassMethods(IntPtr klass, ref IntPtr iterator);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_method_get_name", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MethodNameOf(IntPtr method);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_method_get_class", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MethodClass(IntPtr method);
    [DllImport("GameAssembly", EntryPoint = "il2cpp_class_get_name", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ClassName(IntPtr klass);
}
