using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace ForgeDevelopment.Native;

internal sealed class AsyncReportWriter : IDisposable
{
    private const int MaxPendingPaths = 8;
    private const int MaxPendingLines = 4096;
    private const int DisposeTimeoutMilliseconds = 10_000;

    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly LinkedList<Request> _pending = new();
    private readonly Dictionary<string, LinkedListNode<Request>> _byPath = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Action<Exception> _onError;
    private readonly Thread _worker;
    private bool _accepting = true;
    private bool _disposed;
    private int _pendingLines;
    private int _dropped;
    private volatile string _failure = "";
    internal string? Path { get; }
    internal int Dropped => Volatile.Read(ref _dropped);
    internal int Pending { get { lock (_gate) return _pending.Count; } }
    internal string Failure => _failure;

    internal AsyncReportWriter(Action<Exception> onError) : this(onError, null) { }

    internal AsyncReportWriter(string path) : this(static _ => { }, System.IO.Path.GetFullPath(path)) { }

    private AsyncReportWriter(Action<Exception> onError, string? path)
    {
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        Path = path;
        _worker = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "Forge Development Report Writer"
        };
        _worker.Start();
    }

    internal bool Enqueue(DiagnosticsReport report, string path, string outcome)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        lock (_gate)
        {
            if (!_accepting) return false;
        }

        var fullPath = System.IO.Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_accepting) return false;
            // Reject before capturing when no pending slot can be used. Holding the
            // queue gate makes capture/coalescing order match request order.
            if (!_byPath.ContainsKey(fullPath) && _byPath.Count >= MaxPendingPaths) return false;
            var request = new Request(report.Freeze(outcome), fullPath, null);
            if (_byPath.TryGetValue(fullPath, out var existing))
            {
                existing.Value = request;
            }
            else
            {
                _byPath.Add(fullPath, _pending.AddLast(request));
            }

            _workAvailable.Set();
            return true;
        }
    }

    internal void Write(string line)
    {
        if (Path == null) throw new InvalidOperationException("This writer has no line output path.");
        lock (_gate)
        {
            if (!_accepting || _pendingLines >= MaxPendingLines)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _pending.AddLast(new Request(null, Path, line));
            _pendingLines++;
            _workAvailable.Set();
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            if (Thread.CurrentThread == _worker)
                throw new InvalidOperationException("The report writer cannot be disposed from its error callback.");
            lock (_gate) _accepting = false;
            _workAvailable.Set();
            if (!_worker.Join(DisposeTimeoutMilliseconds))
                throw new TimeoutException("Timed out waiting for pending diagnostic reports to finish writing.");
            _workAvailable.Dispose();
            _disposed = true;
        }
    }

    private void WriteLoop()
    {
        try
        {
            using var lines = OpenLines();
            Drain(lines);
        }
        catch (Exception error)
        {
            ReportError(error);
            lock (_gate) _accepting = false;
        }
    }

    private StreamWriter? OpenLines()
    {
        if (Path == null) return null;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        return new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read,
            65536, FileOptions.SequentialScan), new UTF8Encoding(false), 65536);
    }

    private void Drain(StreamWriter? lines)
    {
        var nextFlush = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (true)
        {
            Request? request = null;
            lock (_gate)
            {
                if (_pending.First != null)
                {
                    var node = _pending.First;
                    request = node.Value;
                    _pending.RemoveFirst();
                    if (request.Line == null) _byPath.Remove(request.Path);
                    else _pendingLines--;
                }
                else if (!_accepting)
                {
                    break;
                }
            }

            if (request == null)
            {
                lines?.Flush();
                _workAvailable.WaitOne(lines == null ? Timeout.Infinite : 250);
                continue;
            }

            if (request.Line is { } line)
            {
                lines!.WriteLine(line);
                if (Stopwatch.GetTimestamp() >= nextFlush)
                {
                    lines.Flush();
                    nextFlush = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                }
                continue;
            }

            try
            {
                request.Snapshot!.Export(request.Path);
            }
            catch (Exception error)
            {
                ReportError(error);
            }
        }
    }

    private void ReportError(Exception error)
    {
        _failure = error.GetType().Name + ": " + error.Message;
        try { _onError(error); }
        catch (Exception reportingError)
        {
            _failure += "; error callback: " + reportingError.GetType().Name + ": " + reportingError.Message;
        }
    }

    private sealed record Request(DiagnosticsReport.FrozenReport? Snapshot, string Path, string? Line);
}
