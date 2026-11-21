using System;
using System.IO;
using System.Linq;

namespace ForgeDevelopment.Native;

/// <summary>
/// Bounds the experimental transport folders: archived requests and results, swapped-fixture receipts and drive-session
/// command receipts. A folder keeps its newest <c>keep</c> entries and nothing older than <see cref="MaximumAge"/>; anything
/// written inside <see cref="LiveWindow"/> is left alone, because another session may still be using it. Reads and writes
/// never fail on a prune: an unreadable or vanished entry is skipped and the transport continues.
/// </summary>
internal static class TransportRetention
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan LiveWindow = TimeSpan.FromHours(1);

    /// <summary>Prunes the files of one transport kind; <paramref name="pattern"/> is a search pattern such as <c>*.forge</c>.</summary>
    internal static void PruneFiles(string directory, string pattern, int keep)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
        FileInfo[] files;
        try { files = info.GetFiles(pattern, SearchOption.TopDirectoryOnly); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        Prune(files.OrderByDescending(file => file.LastWriteTimeUtc).ToArray(), keep,
            file => (file.Attributes & FileAttributes.ReparsePoint) == 0, file => file.LastWriteTimeUtc, file => file.Delete());
    }

    /// <summary>Prunes the receipt directories of one swap input folder.</summary>
    internal static void PruneDirectories(string directory, int keep)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
        DirectoryInfo[] directories;
        try { directories = info.GetDirectories("*", SearchOption.TopDirectoryOnly); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        Prune(directories.OrderByDescending(entry => entry.LastWriteTimeUtc).ToArray(), keep,
            entry => (entry.Attributes & FileAttributes.ReparsePoint) == 0, entry => entry.LastWriteTimeUtc,
            entry => entry.Delete(recursive: true));
    }

    private static void Prune<T>(T[] entries, int keep, Func<T, bool> physical, Func<T, DateTime> lastWrite, Action<T> delete)
    {
        var now = DateTime.UtcNow;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            DateTime written;
            bool isPhysical;
            try { written = lastWrite(entry); isPhysical = physical(entry); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            // A session that is still running may hold the file or write another one next to it; that is the one live window.
            if (now - written < LiveWindow) continue;
            if (!isPhysical) continue;
            if (index < keep && now - written <= MaximumAge) continue;
            try { delete(entry); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
