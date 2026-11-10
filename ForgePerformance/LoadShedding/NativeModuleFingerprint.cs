using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ForgePerformance.LoadShedding;

/// <summary>Read-only check that the loaded GameAssembly.dll is the supported build. ForgePerformance depends on no Forge package, so it keeps its own copy.</summary>
internal static class NativeModuleFingerprint
{
    internal const string GameSha256 = "C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55";
    internal static IntPtr Verified(string name, string expected, out string note)
    {
        note = "none";
        if (IntPtr.Size != 8) { note = "requires_x64"; return IntPtr.Zero; }
        var module = GetModuleHandle(name);
        if (module == IntPtr.Zero) { note = name + "_not_loaded"; return IntPtr.Zero; }
        var path = new StringBuilder(1024);
        if (GetModuleFileName(module, path, path.Capacity) == 0) { note = "module_path_unavailable"; return IntPtr.Zero; }
        using var file = File.OpenRead(path.ToString()); using var hash = SHA256.Create();
        var actual = Convert.ToHexString(hash.ComputeHash(file));
        if (actual != expected) { note = "unsupported_" + name + "_sha256:" + actual; return IntPtr.Zero; }
        return module;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
    private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int size);
}
