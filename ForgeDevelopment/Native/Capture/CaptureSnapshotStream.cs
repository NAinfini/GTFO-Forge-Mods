using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>Detach completed rows in small batches instead of promoting the entire level's diagnostic graph.
/// A batch owns its managed rows only after admission. Refused batches stay on the budgeted cursor.</summary>
internal sealed class CaptureSnapshotStream
{
    internal const int RowsPerBatch = 16;
    internal const int WaitForNextFrame = int.MinValue;
    internal const double BackpressureTimeoutMilliseconds = 5000;
    private readonly CaptureSnapshot _source;
    private readonly Func<long>? _frame;
    private readonly Func<double> _clock;
    private readonly Func<string, string, Action<Utf8JsonWriter>, long, RecFrozenContext?, bool> _write;
    private RecFrozenContext? _context;
    private int _part;
    private bool _completed, _failureRecorded;
    internal CaptureSnapshotStream(CaptureSnapshot source, Func<long>? frame = null, RecFrozenContext? context = null,
        Func<string, string, Action<Utf8JsonWriter>, long, RecFrozenContext?, bool>? write = null, Func<double>? clock = null)
    {
        _source = source; _frame = frame; _context = context;
        _write = write ?? RecSession.TryWriteFrozen;
        _clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }

    internal void SetContext(RecFrozenContext? context)
    {
        if (_part != 0 || _completed) throw new InvalidOperationException("Snapshot context is already frozen.");
        _context = context;
    }

    internal IEnumerable<int> Collect(IEnumerable<int> work) => Collect(work.GetEnumerator());

    internal IEnumerable<int> Collect(IEnumerator<int> work)
    {
        using (work)
        {
            while (work.MoveNext())
            {
                foreach (var step in Flush(false)) yield return step;
                yield return work.Current;
            }
        }
        foreach (var step in Flush(true)) yield return step;
    }

    private IEnumerable<int> Flush(bool all)
    {
        foreach (var section in _source.Sections)
        {
            while (section.Rows.Count != 0 && (all || section.Rows.Count >= RowsPerBatch))
            {
                var batch = new CaptureSnapshot(_source.Reason, _source.Scope, _source.Tick)
                {
                    WorldEpoch = _source.WorldEpoch,
                    CompletedTick = _frame?.Invoke() ?? _source.Tick,
                    StartedTick = _source.StartedTick,
                    Observation = _source.Observation
                };
                var frozen = batch.Section(section.Name, section.Source);
                var count = Math.Min(RowsPerBatch, section.Rows.Count);
                for (var index = 0; index < count; index++) frozen.Rows.Add(section.Rows[index]);
                var part = _part;
                var identity = _source.Identity;
                Action<Utf8JsonWriter> body = json => CaptureStateWriter.WriteSnapshot(json, batch, identity, part, false, 0);
                foreach (var step in Admit(body, RecFrozenBytes.Measure(batch), "capture-recorder-backpressure-timeout"))
                    yield return step;
                _source.DrainedRows += count;
                section.Rows.RemoveRange(0, count);
                _part++;
                // Each admission is one cooperative step, including a successful retry.
                yield return 0;
            }
        }
    }

    internal IEnumerable<int> Complete()
    {
        if (_completed) throw new InvalidOperationException("Snapshot already has its terminal receipt.");
        _completed = true;
        var accepted = false;
        try
        {
            if (_source.AcquisitionComplete)
                foreach (var step in Flush(true)) yield return step;
            var remaining = _source.RowCount - _source.DrainedRows;
            if (remaining != 0) _source.Skipped.Add("capture-unpersisted-rows:" + remaining);
            // The terminal receipt carries metadata and the full denominator, never mutable or unaccepted rows.
            var terminal = new CaptureSnapshot(_source.Reason, _source.Scope, _source.Tick)
            {
                WorldEpoch = _source.WorldEpoch,
                StartedTick = _source.StartedTick,
                CompletedTick = _source.CompletedTick,
                Observation = _source.Observation,
                AcquisitionComplete = _source.AcquisitionComplete,
                DrainedRows = _source.RowCount,
                TerminalStatus = _source.TerminalStatus,
                FieldCompleteness = _source.FieldCompleteness,
                CoverageCompleteness = _source.CoverageCompleteness,
                TemporalConsistency = _source.TemporalConsistency,
                MainThreadMilliseconds = _source.MainThreadMilliseconds,
                PeakSliceMilliseconds = _source.PeakSliceMilliseconds,
                AllocatedBytes = _source.AllocatedBytes,
                CaptureFrames = _source.CaptureFrames
            };
            terminal.Notes.AddRange(_source.Notes); terminal.Skipped.AddRange(_source.Skipped);
            foreach (var section in _source.Sections) terminal.Section(section.Name, section.Source);
            var part = _part;
            var identity = _source.Identity;
            Action<Utf8JsonWriter> body = json => CaptureStateWriter.WriteSnapshot(json, terminal, identity, part, true, part + 1);
            foreach (var step in Admit(body, RecFrozenBytes.Measure(terminal), "capture-recorder-terminal-receipt-timeout"))
                yield return step;
            _part++;
            accepted = true;
        }
        finally
        {
            if (!accepted && !_failureRecorded) Fail("capture-recorder-terminal-receipt-abandoned");
        }
    }

    private IEnumerable<int> Admit(Action<Utf8JsonWriter> body, long bytes, string timeout)
    {
        _context ??= RecSession.FreezeContext();
        double? refusedAt = null;
        while (true)
        {
            if (refusedAt.HasValue && _clock() - refusedAt.Value >= BackpressureTimeoutMilliseconds)
            {
                Fail(timeout);
                throw new RecObservationTerminated("timed-out", timeout);
            }
            if (_write(CaptureStateWriter.Channel, _source.Scope == "datablocks" ? "datablocks"
                : _source.Scope.StartsWith("layout/", StringComparison.Ordinal) ? "layout" : "snapshot", body, bytes, _context))
                yield break;
            refusedAt ??= _clock();
            // CaptureSnapshotPass ends this frame; no polling or serialization loop on the game thread.
            yield return WaitForNextFrame;
        }
    }

    private void Fail(string reason)
    {
        _source.AcquisitionComplete = false;
        _source.FieldCompleteness = _source.CoverageCompleteness = "incomplete";
        if (_source.TerminalStatus is null or "completed")
            _source.TerminalStatus = reason.EndsWith("-timeout", StringComparison.Ordinal) ? "timed-out" : "error";
        _source.Skipped.Add(reason);
        if (!_failureRecorded) RecSession.DropFrozen(reason, _context);
        _failureRecorded = true;
    }
}
