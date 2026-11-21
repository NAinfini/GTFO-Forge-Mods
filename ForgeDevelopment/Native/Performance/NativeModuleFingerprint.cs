using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ForgeDevelopment.Native;

internal static class NativeModuleFingerprint
{
    // A loaded module's file does not change while the game runs: each is hashed once per process, however many checks ask.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Hashes = new(StringComparer.OrdinalIgnoreCase);
    internal const string GameSha256 = "C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55";
    internal static IntPtr Verified(string name, string expected, out string note)
    {
        note = "none";
        if (IntPtr.Size != 8) { note = "requires_x64"; return IntPtr.Zero; }
        var module = GetModuleHandle(name);
        if (module == IntPtr.Zero) { note = name + "_not_loaded"; return IntPtr.Zero; }
        var path = new StringBuilder(1024);
        if (GetModuleFileName(module, path, path.Capacity) == 0) { note = "module_path_unavailable"; return IntPtr.Zero; }
        var actual = Hashes.GetOrAdd(path.ToString(), static file => { using var stream = File.OpenRead(file); using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)); });
        if (actual != expected) { note = "unsupported_" + name + "_sha256:" + actual; return IntPtr.Zero; }
        return module;
    }
    // Reject an in-memory patch even when the on-disk fingerprint matches.
    internal static bool OriginalCode(IntPtr module, IntPtr target)
    {
        var rva = target.ToInt64() - module.ToInt64();
        if (rva < 0 || rva > int.MaxValue) return false;
        var path = new StringBuilder(1024);
        if (GetModuleFileName(module, path, path.Capacity) == 0) return false;
        using var file = File.OpenRead(path.ToString()); using var reader = new BinaryReader(file);
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
            if (rva < virtualAddress || rva + 16 > virtualAddress + Math.Min(rawSize, virtualSize)) continue;
            if ((flags & 0x20000000) == 0) return false;
            file.Position = rawAddress + rva - virtualAddress;
            for (var j = 0; j < 16; j++) if (reader.ReadByte() != Marshal.ReadByte(target, j)) return false;
            return true;
        }
        return false;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
    private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int size);
}

