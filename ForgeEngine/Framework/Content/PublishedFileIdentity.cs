using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForgeEngine.Framework;

/// <summary>Identity of the actual handles used to admit published bytes, never a cached path verdict.</summary>
internal static class PublishedFileIdentity
{
    internal static SafeFileHandle OpenRoot(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidDataException("Opened package identity is unsupported on this platform.");
        // No delete sharing: the install root cannot be renamed or replaced during this read/scan.
        var path = Path.GetFullPath(root);
        var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var handle = CreateFileW(nativePath, 0x80000000, FileShare.Read | FileShare.Write,
            IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw new IOException("Cannot open the package root identity.",
                new Win32Exception(Marshal.GetLastWin32Error()));
            VerifyRoot(handle, root);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void VerifyRoot(SafeFileHandle handle, string expected)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(FinalPath(handle)),
            Path.TrimEndingDirectorySeparator(Unprefixed(Path.GetFullPath(expected))), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The opened package root resolves outside its declared identity.");
    }

    internal static void VerifyDocument(SafeFileHandle handle, string expected)
    {
        if (!string.Equals(FinalPath(handle), Unprefixed(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The opened package document resolves outside its declared root path.");
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.Capacity = checked((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("Cannot resolve the opened package path.", new Win32Exception(Marshal.GetLastWin32Error()));
        return Unprefixed(buffer.ToString());
    }

    private static string Unprefixed(string path)
        => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
