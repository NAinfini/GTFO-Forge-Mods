using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace ForgeDevelopment.Native;

/// <summary>
/// Bounds <c>BepInEx/ForgeReports</c> once per process. Only output of a recording run is touched: <c>rec-*</c>
/// session directories (a session renewed at its byte budget belongs to the same run as the session it continues)
/// and the <c>&lt;stamp&gt;-&lt;id&gt;(-startup|-NNN).forge</c> diagnostic files of one game process. Everything else in the
/// folder (capture, probe, pack-test and export folders, unknown names) is left alone. Each kind keeps its newest
/// <c>KeepSessions</c> runs; then the oldest runs go until the recognized total fits <c>ReportsMaxGiB</c>. The
/// current run and any run written within the last hour (another game instance) are never deleted.
/// </summary>
internal static class RecRetention
{
    private static readonly Regex RecName = new(@"^rec-(\d{8}T\d{6}Z)-.+$", RegexOptions.CultureInvariant);
    private static readonly Regex DiagName = new(@"^(\d{8}T\d{6})-[0-9a-f]{8}(-startup|-\d{3})\.forge$", RegexOptions.CultureInvariant);
    private static readonly Regex Previous = new("\"previousSession\":\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly TimeSpan LiveWindow = TimeSpan.FromHours(1);
    private static int _ran;

    private sealed class Run
    {
        internal string Label = "";
        internal DateTime Started;
        internal DateTime LastWrite;
        internal long Bytes;
        internal bool Protected;
        internal readonly List<string> Directories = new();
        internal readonly List<string> Files = new();
    }

    /// <summary>Runs the cleanup on a worker thread, once per process; the recorder start never waits for the disk.</summary>
    internal static void StartOnce(string reportsRoot, string currentRecDirectory, string currentDiagnosticsSession,
        int keepRuns, long maxBytes, Action<string> log)
    {
        if (System.Threading.Interlocked.Exchange(ref _ran, 1) != 0) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Prune(reportsRoot, currentRecDirectory, currentDiagnosticsSession, keepRuns, maxBytes, log); }
            catch (Exception error) { log("development.reports-retention-failed " + error.Message); }
        });
    }

    internal static void Prune(string reportsRoot, string currentRecDirectory, string currentDiagnosticsSession,
        int keepRuns, long maxBytes, Action<string> log)
    {
        var root = Path.GetFullPath(reportsRoot);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        var current = currentRecDirectory.Length == 0 ? "" : Path.GetFileName(Path.GetFullPath(currentRecDirectory).TrimEnd(Path.DirectorySeparatorChar));
        keepRuns = Math.Max(1, keepRuns);
        var now = DateTime.UtcNow;

        var recording = ScanRecordings(root, current, now);
        var diagnostics = ScanDiagnostics(root, currentDiagnosticsSession, now);
        var all = recording.Concat(diagnostics).ToList();
        long total = all.Sum(run => run.Bytes);

        var doomed = new List<(Run Run, string Reason)>();
        foreach (var kind in new[] { recording, diagnostics })
        {
            var newestFirst = kind.OrderByDescending(run => run.Started).ToList();
            var kept = 0;
            foreach (var run in newestFirst)
            {
                if (run.Protected) { kept++; continue; }
                if (kept < keepRuns) { kept++; continue; }
                doomed.Add((run, "older than the newest " + keepRuns));
            }
        }
        total -= doomed.Sum(entry => entry.Run.Bytes);
        var chosen = doomed.Select(entry => entry.Run).ToHashSet();
        foreach (var run in all.Where(run => !run.Protected && !chosen.Contains(run)).OrderBy(run => run.Started))
        {
            if (total <= maxBytes) break;
            doomed.Add((run, "directory over " + (maxBytes >> 20) + " MiB"));
            total -= run.Bytes;
        }

        foreach (var (run, reason) in doomed.OrderBy(entry => entry.Run.Started))
        {
            try
            {
                foreach (var directory in run.Directories) Directory.Delete(directory, recursive: true);
                foreach (var file in run.Files) File.Delete(file);
                log("development.reports-pruned run=" + run.Label + " bytes=" + run.Bytes.ToString(CultureInfo.InvariantCulture) + " reason=" + reason);
            }
            catch (Exception error)
            {
                log("development.reports-prune-failed run=" + run.Label + " " + error.Message);
            }
        }
    }

    private static List<Run> ScanRecordings(string root, string currentName, DateTime now)
    {
        var sessions = new Dictionary<string, (string Path, DateTime Started, string? Previous, long Bytes, DateTime LastWrite)>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateDirectories(root, "rec-*"))
        {
            var name = Path.GetFileName(path);
            var match = RecName.Match(name);
            if (!match.Success || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)) continue;
            long bytes = 0; var last = DateTime.MinValue; string? segment = null;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes += file.Length;
                if (file.LastWriteTimeUtc > last) last = file.LastWriteTimeUtc;
                if (file.Name.EndsWith("-000.jsonl", StringComparison.Ordinal) || file.Name.EndsWith("-000.jsonl.gz", StringComparison.Ordinal))
                    segment = file.FullName;
            }
            sessions[name] = (path, started, segment == null ? null : ReadPrevious(segment), bytes, last);
        }

        string Head(string name)
        {
            for (var guard = 0; guard < 4096 && sessions[name].Previous is { } before && sessions.ContainsKey("rec-" + before); guard++)
                name = "rec-" + before;
            return name;
        }

        var runs = new Dictionary<string, Run>(StringComparer.Ordinal);
        foreach (var (name, session) in sessions)
        {
            var head = Head(name);
            if (!runs.TryGetValue(head, out var run))
                runs[head] = run = new Run { Label = head, Started = sessions[head].Started };
            run.Directories.Add(session.Path);
            run.Bytes += session.Bytes;
            if (session.LastWrite > run.LastWrite) run.LastWrite = session.LastWrite;
            if (name == currentName) run.Protected = true;
        }
        foreach (var run in runs.Values) run.Protected |= now - run.LastWrite < LiveWindow;
        return runs.Values.ToList();
    }

    private static string? ReadPrevious(string segmentPath)
    {
        try
        {
            using var file = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream stream = segmentPath.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : file;
            var buffer = new byte[16384];
            var read = stream.Read(buffer, 0, buffer.Length);
            var match = Previous.Match(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception) { return null; } // An unreadable head only makes this session its own run.
    }

    private static List<Run> ScanDiagnostics(string root, string currentSession, DateTime now)
    {
        var runs = new Dictionary<string, Run>(StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.forge", SearchOption.TopDirectoryOnly))
        {
            var match = DiagName.Match(file.Name);
            if (!match.Success || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var session = file.Name[..(file.Name.Length - match.Groups[2].Value.Length - ".forge".Length)];
            if (!runs.TryGetValue(session, out var run))
            {
                if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)) continue;
                runs[session] = run = new Run { Label = session, Started = started, Protected = session == currentSession };
            }
            run.Files.Add(file.FullName);
            run.Bytes += file.Length;
            if (file.LastWriteTimeUtc > run.LastWrite) run.LastWrite = file.LastWriteTimeUtc;
        }
        foreach (var run in runs.Values) run.Protected |= now - run.LastWrite < LiveWindow;
        return runs.Values.ToList();
    }
}
