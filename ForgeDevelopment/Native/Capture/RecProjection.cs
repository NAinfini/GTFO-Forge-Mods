using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeDevelopment.Native;

internal sealed class RecProjectionCursor : IEnumerator<int>
{
    private readonly Stack<RecProjectionOperation> _pending = new();
    private readonly IRecProjectionAdapter _adapter;
    private readonly ScopedReadLease _lease;
    private readonly CaptureSnapshot _snapshot;
    private readonly ArrayBufferWriter<byte> _rowBytes = new();
    private readonly int _maximumRows, _maximumDepth, _maximumSteps, _maximumBytes;
    private int _steps, _rows, _bytes;
    private bool _disposed;

    internal RecProjectionCursor(IRecProjectionAdapter adapter, ScopedReadLease lease, CaptureSnapshot snapshot,
        RecProjectionOperation root, int maximumRows, int maximumDepth,
        int maximumSteps, int maximumBytes)
    {
        _adapter = adapter; _lease = lease; _snapshot = snapshot;
        _maximumRows = maximumRows; _maximumDepth = maximumDepth; _maximumSteps = maximumSteps; _maximumBytes = maximumBytes;
        _pending.Push(root);
    }

    public int Current => 0;
    object IEnumerator.Current => Current;
    public void Reset() => throw new NotSupportedException();

    public bool MoveNext()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RecProjectionCursor));
        if (_pending.Count == 0) return false;
        var operation = _pending.Pop();
        if (++_steps > _maximumSteps || operation.Depth > _maximumDepth)
            Truncate(operation, operation.Depth > _maximumDepth ? "depth-limit" : "step-limit");
        RecProjectionStep step;
        try { step = _lease.Read(_adapter, operation); }
        catch (RecObservationDeferred) { _steps--; _pending.Push(operation); return true; }
        catch (RecObservationTerminated error)
        {
            _snapshot.Skipped.Add(operation.Kind + ":" + operation.Path + ":" + operation.Index + ":" + error.Status);
            throw;
        }
        if (step.Row is { } row)
        {
            _rowBytes.Clear();
            using (var json = new Utf8JsonWriter(_rowBytes))
            { CaptureStateWriter.WriteRow(json, row); json.Flush(); }
            var size = _rowBytes.WrittenCount;
            if (row.TypedFields != null) foreach (var field in row.TypedFields)
            {
                if (field.Value.Status is not ("value" or "null"))
                {
                    _snapshot.FieldCompleteness = "incomplete";
                    if (field.Value.Type == "collection") _snapshot.CoverageCompleteness = "incomplete";
                }
            }
            if (_rows >= _maximumRows || size > _maximumBytes - _bytes)
                Truncate(operation, _rows >= _maximumRows ? "row-limit" : "output-limit");
            _bytes += size; _rows++;
            _snapshot.Section("projection", "reviewed-native-reader").Rows.Add(row);
        }
        // Adapters must schedule a bounded continuation, not materialize a whole native collection.
        if (step.Next.Count > 8 || _pending.Count + step.Next.Count > _maximumSteps)
            Truncate(operation, "continuation-limit");
        for (var index = step.Next.Count - 1; index >= 0; index--) _pending.Push(step.Next[index]);
        return true;
    }

    private void Truncate(RecProjectionOperation operation, string reason)
    {
        _snapshot.CoverageCompleteness = _snapshot.FieldCompleteness = "incomplete";
        _snapshot.Skipped.Add(operation.Kind + ":" + operation.Path + ":" + operation.Index + ":" + reason);
        _snapshot.Section("projection", "reviewed-native-reader").Rows.Add(new CaptureRow("gap", operation.Path, "")
            .Value("unread", RecProjectionValue.Truncated("collection", reason)));
        throw new RecObservationTerminated("truncated", reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _pending.Clear(); _adapter.Dispose();
    }
}
