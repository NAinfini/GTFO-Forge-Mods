using System;
using System.IO;
using System.Threading;

namespace ForgeDevelopment.Native;

internal delegate T? CapturePlanReader<T>(out string? text, out string? error) where T : class;

// Only the main thread parses. Watcher callbacks invalidate a revision; they never touch a plan or native hooks.
// A watcher failure revokes the cached plan explicitly rather than serving an enabled plan of unknown freshness.
internal sealed class CachedCapturePlan<T> : IDisposable where T : class
{
    private readonly string _path;
    private readonly CapturePlanReader<T> _read;
    private FileSystemWatcher _watch;
    private bool _ancestor;
    private int _dirty = 1;
    private T? _plan;
    private string? _text, _error;
    private volatile string? _watchError;
    private bool _disposed;

    internal CachedCapturePlan(string path, CapturePlanReader<T> read)
    {
        _path = Path.GetFullPath(path);
        _read = read;
        _watch = Watch();
    }

    // The plan file's own directory is watched without recursion: only changes to its direct entries can matter, and a
    // recursive watch there also receives every file the recorder and the experiment transport write below it. Only while
    // that directory does not exist yet is the nearest existing ancestor watched recursively, to see it being created.
    private FileSystemWatcher Watch()
    {
        var directory = Path.GetDirectoryName(_path)!;
        _ancestor = !Directory.Exists(directory);
        while (!Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException(_path);
        var watch = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = _ancestor,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watch.Changed += Changed; watch.Created += Changed; watch.Deleted += Changed;
        watch.Renamed += Renamed; watch.Error += Failed;
        watch.EnableRaisingEvents = true;
        return watch;
    }

    private bool Relevant(string path) => string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)
        || _path.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void Changed(object sender, FileSystemEventArgs args)
    { if (Relevant(args.FullPath)) Interlocked.Exchange(ref _dirty, 1); }
    private void Renamed(object sender, RenamedEventArgs args)
    { if (Relevant(args.FullPath) || Relevant(args.OldFullPath)) Interlocked.Exchange(ref _dirty, 1); }
    private void Failed(object sender, ErrorEventArgs args)
    { _watchError = "capture-plan-watch-failed: " + args.GetException().Message; }

    internal T? Read(out string? text, out string? error)
    {
        if (_disposed) { text = null; error = "capture-plan-watch-stopped"; return null; }
        if (_watchError is { } failure) { text = null; error = failure; return null; }
        // Exchange before reading: an edit racing with the read remains dirty for the next main-thread slice.
        if (Interlocked.Exchange(ref _dirty, 0) != 0)
        {
            if (_ancestor && Directory.Exists(Path.GetDirectoryName(_path)!))
            {
                // The plan directory now exists: narrow the watch to it. The new watcher is armed before the read.
                var old = _watch; _watch = Watch(); old.Dispose();
            }
            _plan = _read(out _text, out _error);
        }
        text = _text; error = _error; return _plan;
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _watch.Dispose(); _plan = null; }
}
