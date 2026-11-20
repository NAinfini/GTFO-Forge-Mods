using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Iced.Intel;
using Il2CppInterop.Common;
using ForgePerformance.LoadShedding;
using Code = Iced.Intel.Code; // HarmonyLib has a Code too

namespace ForgePerformance.Crowd;

/// <summary>Admission of the native entry of every game method this mod patches. ForgeCore checks the entries Forge packages
/// patch, but ForgePerformance depends on no Forge package and may load before it or without it, so it checks its own targets:
/// the loaded GameAssembly.dll is the supported build; no other method shares the native body (identical code folding: a patch
/// would run for every sharer, unless shared-native-bodies.json registers that body with its exact address and set of methods; looked up in the running game unless the offline check already covers the target); every instruction of the file image that starts inside the bytes the detour overwrites is one
/// the backend relocates (ForgeCore's HookPrologue rule, read from the file so it also holds for an entry that already carries
/// a detour); the entry's live bytes equal the file image, or differ only inside those bytes on a method Harmony already
/// patched (its own detour, which one more patch writes again in the same place); and after the patch the entry differs from
/// the file image in no more than those bytes.</summary>
internal static class EntryAdmission
{
    // The pinned Dobby backend Il2CppInterop's Harmony support uses writes its near slot jump FF 25 rel32 at the entry.
    internal const int OverwriteLength = 6;
    private const int SnapshotLength = 32;
    // The code of the one line a refused or failed target writes; ForgeCore writes its own refused entries under the same code.
    internal const string RefusedCode = "native-hook-prologue-refused";
    private static Dictionary<IntPtr, List<IntPtr>>? _sharedBodies;

    /// <summary>The game method a [HarmonyPatch] class names, or null.</summary>
    internal static MethodBase? Target(Type patch)
    {
        var named = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(patch));
        return named.declaringType == null || named.methodName == null ? null
            : AccessTools.DeclaredMethod(named.declaringType, named.methodName, named.argumentTypes);
    }

    /// <summary>Puts a [HarmonyPatch] class on its method when the entry is admitted. <paramref name="sharersCheckedOffline"/>
    /// leaves out the look-up of shared bodies in the running game, which walks every method of every loaded class once (time
    /// and memory at start): for the patches a default setting installs, whose targets Tools/Forge/check-patch-bodies.py reads
    /// from their attributes and fails on a shared body unless it lists the target with its exact sharers.</summary>
    internal static bool PatchClass(Harmony patcher, Type patch, Action<string> error, bool sharersCheckedOffline = false)
    {
        var target = Target(patch);
        return Patch(target, target == null ? patch.Name : target.DeclaringType?.FullName + "." + target.Name, patcher,
            () => patcher.CreateClassProcessor(patch).Patch(), error, sharersCheckedOffline);
    }

    /// <summary>Runs <paramref name="patch"/> when the entry of <paramref name="target"/> is admitted, and takes this patcher's
    /// patches off again when the detour changed more than the checked bytes. A refused or failed target writes one line to
    /// <paramref name="error"/> and returns false. The target comes first, as in Harmony's own Patch, so the patch-target
    /// check (Tools/Forge/validate-layering.ts) reads it from a call site.</summary>
    internal static bool Patch(MethodBase? target, string name, Harmony patcher, Action patch, Action<string> error, bool sharersCheckedOffline = false)
    {
        try
        {
            var verdict = Before(target, sharersCheckedOffline, out var body, out var image);
            if (verdict == null)
            {
                patch();
                var changed = Changed(body, image);
                if (changed <= OverwriteLength) return true;
                patcher.Unpatch(target, HarmonyPatchType.All, patcher.Id);
                verdict = "detour changed " + changed + " bytes";
            }
            error("type=perf_native_hook\tstate=refused\tcode=" + RefusedCode + "\ttarget=" + name + "\treason=" + verdict);
        }
        catch (Exception failure)
        {
            error("type=perf_native_hook\tstate=failed\tcode=" + RefusedCode + "\ttarget=" + name + "\treason=" + failure.GetType().Name + ": " + failure.Message);
        }
        return false;
    }

    // Why the entry cannot carry the detour, or null with the entry and its bytes in the file.
    private static string? Before(MethodBase? method, bool sharersCheckedOffline, out IntPtr body, out byte[] image)
    {
        body = IntPtr.Zero; image = Array.Empty<byte>();
        if (method == null) return "method-not-found";
        var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
        if (field?.GetValue(null) is not IntPtr info || info == IntPtr.Zero) return "native-method-info-unavailable";
        body = Marshal.ReadIntPtr(info);
        if (body == IntPtr.Zero) return "native-body-unavailable";
        var module = NativeModuleFingerprint.Verified("GameAssembly.dll", NativeModuleFingerprint.GameSha256, out var note);
        if (module == IntPtr.Zero) return note;
        if (!sharersCheckedOffline && (_sharedBodies ??= SharedBodies()).TryGetValue(body, out var sharers)
            && !Registered(method.DeclaringType?.Name + "." + method.Name, body.ToInt64() - module.ToInt64(), sharers.ConvertAll(MethodName)))
            return "shared-native-method-body";
        if (FileImage(module, body) is not { } file) return "entry-outside-game-code";
        image = file;
        if (PrologueRejection(image) is { } prologue) return prologue;
        var changed = Changed(body, image);
        return changed == 0 || changed <= OverwriteLength && Harmony.GetPatchInfo(method) != null ? null : "entry-patched-outside-harmony";
    }

    // The length of the entry's leading bytes that differ from the file image: 0 for an untouched entry.
    private static int Changed(IntPtr body, byte[] image)
    {
        var live = new byte[image.Length];
        Marshal.Copy(body, live, 0, live.Length);
        var changed = 0;
        for (var i = 0; i < live.Length; i++) if (live[i] != image[i]) changed = i + 1;
        return changed;
    }

    // Mods/ForgeCore/Native/HookPrologue.cs, the same rule: inside the overwritten bytes only plain integer instructions the
    // backend relocates; no branch, call, SIMD register or MOV r64,imm64 crossing the end. The one branch admitted is the tail
    // jump of a thunk body (`xor edx,edx; jmp real`): a near rel32 jump that starts inside the range and ends the function,
    // proven by the 0xCC padding after it.
    internal static string? PrologueRejection(byte[] code)
    {
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(code));
        while (decoder.IP < OverwriteLength)
        {
            decoder.Decode(out var instruction);
            var at = decoder.IP - (uint)instruction.Length;
            if (instruction.IsInvalid) return "invalid-or-truncated-native-prologue";
            if (instruction.FlowControl == FlowControl.UnconditionalBranch && instruction.Length == 5
                && instruction.GetOpKind(0) == OpKind.NearBranch64)
            {
                var padding = true;
                for (var offset = (int)decoder.IP; offset < Math.Max((int)decoder.IP + 1, OverwriteLength); offset++) padding &= code[offset] == 0xcc;
                if (padding) return null;
            }
            if (instruction.FlowControl != FlowControl.Next) return "unsupported-native-prologue:" + instruction.FlowControl + "@" + at;
            if (instruction.Code == Code.Mov_r64_imm64 && decoder.IP > OverwriteLength) return "unsupported-native-prologue:mov-imm64-straddle@" + at;
            for (var operand = 0; operand < instruction.OpCount; operand++)
                if (instruction.GetOpKind(operand) == OpKind.Register && !instruction.GetOpRegister(operand).IsGPR())
                    return "unsupported-native-prologue:" + instruction.Mnemonic + "@" + at;
            if (instruction.Encoding != EncodingKind.Legacy || instruction.Mnemonic is not (
                Mnemonic.Add or Mnemonic.And or Mnemonic.Cmp or Mnemonic.Dec or Mnemonic.Inc
                or Mnemonic.Lea or Mnemonic.Mov or Mnemonic.Movsx or Mnemonic.Movsxd or Mnemonic.Movzx
                or Mnemonic.Nop or Mnemonic.Or or Mnemonic.Pop or Mnemonic.Push or Mnemonic.Sub or Mnemonic.Test
                or Mnemonic.Xchg or Mnemonic.Xor))
                return "unsupported-native-prologue:" + instruction.Mnemonic + "@" + at;
        }
        return null;
    }

    // The entry's bytes in GameAssembly.dll on disk; null when the entry is not inside a code section of the file.
    private static byte[]? FileImage(IntPtr module, IntPtr target)
    {
        var rva = target.ToInt64() - module.ToInt64();
        if (rva < 0 || rva > int.MaxValue) return null;
        var path = new StringBuilder(1024);
        if (GetModuleFileName(module, path, path.Capacity) == 0) return null;
        using var file = File.OpenRead(path.ToString());
        using var reader = new BinaryReader(file);
        file.Position = 0x3c; var pe = reader.ReadInt32();
        file.Position = pe + 6; var count = reader.ReadUInt16();
        file.Position = pe + 20; var optional = reader.ReadUInt16();
        file.Position = pe + 24 + optional;
        for (var i = 0; i < count; i++)
        {
            var section = file.Position; file.Position += 8;
            var virtualSize = reader.ReadUInt32(); var virtualAddress = reader.ReadUInt32();
            var rawSize = reader.ReadUInt32(); var rawAddress = reader.ReadUInt32();
            file.Position = section + 36; var flags = reader.ReadUInt32();
            if (rva < virtualAddress || rva + SnapshotLength > virtualAddress + Math.Min(rawSize, virtualSize)) continue;
            if ((flags & 0x20000000) == 0) return null;
            file.Position = rawAddress + rva - virtualAddress;
            var image = reader.ReadBytes(SnapshotLength);
            return image.Length == SnapshotLength ? image : null;
        }
        return null;
    }
    /// <summary>True when <paramref name="target"/> sits on the shared body at <paramref name="rva"/> that shared-native-bodies.json
    /// registers for it and <paramref name="onBody"/> (every method of the body, the target included) is exactly that registered
    /// set. Tools/Forge/check-patch-bodies.py reads the same file and checks the same address and set against the game's dump.</summary>
    internal static bool Registered(string target, long rva, IReadOnlyCollection<string> onBody)
    {
        var sorted = new List<string>(onBody); sorted.Sort(StringComparer.Ordinal);
        foreach (var entry in Registry.Value)
        {
            if (entry.Target != target || entry.Rva != rva) continue;
            var expected = new List<string>(entry.Sharers) { target }; expected.Sort(StringComparer.Ordinal);
            if (expected.Count == sorted.Count && expected.SequenceEqual(sorted)) return true;
        }
        return false;
    }

    private sealed record SharedEntry(string Target, long Rva, string[] Sharers);

    private static readonly Lazy<SharedEntry[]> Registry = new(() =>
    {
        using var stream = typeof(EntryAdmission).Assembly.GetManifestResourceStream("shared-native-bodies.json")!;
        using var document = JsonDocument.Parse(stream);
        var entries = new List<SharedEntry>();
        foreach (var e in document.RootElement.EnumerateArray())
        {
            var sharers = new List<string>();
            foreach (var s in e.GetProperty("sharers").EnumerateArray()) sharers.Add(s.GetString()!);
            entries.Add(new SharedEntry(e.GetProperty("target").GetString()!, Convert.ToInt64(e.GetProperty("rva").GetString()!.Substring(2), 16), sharers.ToArray()));
        }
        return entries.ToArray();
    });

    private static string MethodName(IntPtr info) =>
        Marshal.PtrToStringAnsi(ClassName(MethodClass(info))) + "." + Marshal.PtrToStringAnsi(MethodNameOf(info));

    // Every native body that more than one method info points at, over all loaded images, with those method infos; built once.
    private static Dictionary<IntPtr, List<IntPtr>> SharedBodies()
    {
        var owners = new Dictionary<IntPtr, IntPtr>();
        var shared = new Dictionary<IntPtr, List<IntPtr>>();
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
                while ((info = ClassMethods(klass, ref iterator)) != IntPtr.Zero)
                {
                    var body = Marshal.ReadIntPtr(info);
                    if (body == IntPtr.Zero) continue;
                    if (owners.TryGetValue(body, out var first))
                    {
                        if (first == info) continue;
                        if (!shared.TryGetValue(body, out var onBody)) shared.Add(body, onBody = new List<IntPtr> { first });
                        onBody.Add(info);
                    }
                    else owners.Add(body, info);
                }
            }
        }
        return shared;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
    private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int size);
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
