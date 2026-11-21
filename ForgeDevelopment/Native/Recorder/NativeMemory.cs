using System;
using System.Runtime.InteropServices;

namespace ForgeDevelopment.Native;

/// <summary>Read-only questions about this process's memory, asked through `VirtualQuery`. Nothing is read or
/// written: a region is only described, so a caller can decide whether an address range is safe to inspect.</summary>
internal static class NativeMemory
{
    /// <summary>The `MEMORY_BASIC_INFORMATION` of one region.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Region
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    private static extern UIntPtr Query(IntPtr address, out Region memory, UIntPtr size);

    /// <summary>The region <paramref name="address"/> lies in; false when the system cannot describe it.</summary>
    public static bool TryQuery(IntPtr address, out Region region)
        => Query(address, out region, (UIntPtr)Marshal.SizeOf<Region>()) != UIntPtr.Zero;

    /// <summary>Whether every byte of <paramref name="bytes"/> from <paramref name="address"/> is committed, not
    /// guarded, and readable (or, with <paramref name="executable"/>, executable).</summary>
    public static bool Readable(IntPtr address, int bytes, bool executable)
    {
        var verified = (Start: 0UL, End: 0UL);
        return Readable(address, bytes, executable, ref verified);
    }

    /// <summary><see cref="Readable(IntPtr, int, bool)"/> for a scan of many nearby addresses: the last region found
    /// readable is kept in <paramref name="verified"/> so neighbours cost no further query. It is valid only inside
    /// one pass.</summary>
    public static bool Readable(IntPtr address, int bytes, bool executable, ref (ulong Start, ulong End) verified)
    {
        if (address.ToInt64() <= 0 || bytes <= 0) return false;
        var cursor = (ulong)address.ToInt64();
        var end = checked(cursor + (uint)bytes);
        if (end > long.MaxValue) return false;
        while (cursor < end)
        {
            if (cursor >= verified.Start && cursor < verified.End) { cursor = verified.End; continue; }
            if (!TryQuery((IntPtr)(long)cursor, out var memory)
                || memory.State != 0x1000 || (memory.Protect & (0x100u | 0x01u)) != 0
                || (memory.Protect & (executable ? 0xe0u : 0xeeu)) == 0) return false;
            var regionEnd = checked((ulong)memory.BaseAddress.ToInt64() + memory.RegionSize.ToUInt64());
            if (regionEnd <= cursor) return false;
            verified = ((ulong)memory.BaseAddress.ToInt64(), regionEnd);
            cursor = regionEnd;
        }
        return true;
    }
}
