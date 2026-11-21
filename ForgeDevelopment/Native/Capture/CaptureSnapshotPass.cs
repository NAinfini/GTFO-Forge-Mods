using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ForgeDevelopment.Native;

internal enum CaptureEnqueueResult { Accepted, Inactive, QueueFull, Invalid }
internal sealed record CaptureCompletion(string RequestId, string Status, double TotalMilliseconds,
    double PeakMilliseconds, long AllocatedBytes, int Frames, long Steps, string Error = "")
{
    internal bool Succeeded => Status == "completed";
}

/// <summary>One bounded diagnostic queue, driven by CaptureMonitor. A hook only admits managed requests;
/// factories, native reads and streaming run inside the next capture frame's shared budget.</summary>
internal sealed class CaptureSnapshotPass
{
    private sealed class Request
    {
        internal readonly string Id, Reason;
        internal readonly long Epoch;
        internal readonly Func<IEnumerator<int>> Factory;
        internal readonly Action<CaptureCompletion> Completed;
        internal readonly Func<CaptureCompletion, IEnumerator<int>>? FinalizeCapture;
        internal readonly ObservationTicket? Observation;
        internal IEnumerator<int>? Cursor;
        internal CaptureCompletion? Completion;
        internal double Total, Peak;
        internal long Allocated, Steps;
        internal long ResumeFrame;
        internal int Frames;
        internal bool Ended;
        internal string? StopStatus;
        internal Request(string id, string reason, long epoch, Func<IEnumerator<int>> factory,
            Action<CaptureCompletion> completed, ObservationTicket? observation, Func<CaptureCompletion, IEnumerator<int>>? finalize)
        { Id = id; Reason = reason; Epoch = epoch; Factory = factory; Completed = completed; Observation = observation; FinalizeCapture = finalize; }
    }
    internal const int MaximumRequests = 64, ReservedCaptureRequests = 8, MaximumTicketBytes = 4 * 1024 * 1024;
    internal const int ObservationStepsPerFrame = 64, MaximumRestFrames = 60;
    internal const double ObservationMillisecondsPerFrame = 1;
    // A periodic snapshot is background work: a slice that overran its budget (one unsliceable scan) waits until the
    // pass has spent at most 2% of the wall clock, unless something else is queued behind it. Other reasons keep the
    // frame-count rest.
    internal const double PeriodicRestPerMillisecond = 49, MaximumPeriodicRestMilliseconds = 5000;
    private readonly Queue<Request> _requests = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Func<double> _clock;
    private Request? _current;
    private Request? _executing;
    private int _rest, _observations, _ticketBytes;
    private double _periodicRestUntil;
    private CaptureWorkBudget? _activeBudget;
    private double _sliceStarted;
    private bool _observationSlice;
    internal bool CanReadField => _activeBudget != null && _activeBudget.HasTime
        && (!_observationSlice || _clock() - _sliceStarted < ObservationMillisecondsPerFrame);
    internal bool Active { get; set; } = true;
    internal bool Pending => _current != null || _requests.Count != 0;
    internal bool IsPending(string requestId) => _ids.Contains(requestId)
        && !(_current?.Id == requestId && _current.StopStatus != null);
    internal double TotalMilliseconds { get; private set; }
    internal double PeakMilliseconds { get; private set; }
    internal long AllocatedBytes { get; private set; }
    internal int Frames { get; private set; }
    internal string LastReason { get; private set; } = "";
    internal string Session { get; private set; } = "";
    internal long Generation { get; private set; }
    internal long Frame { get; private set; }
    internal long Epoch { get; private set; }
    internal double Now => _clock();

    internal CaptureSnapshotPass(Func<double>? clock = null)
        => _clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    internal CaptureEnqueueResult Enqueue(string reason, long epoch, Func<IEnumerator<int>> cursorFactory,
        Action<CaptureCompletion> completed, ObservationTicket? observation = null,
        Func<CaptureCompletion, IEnumerator<int>>? finalize = null)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var id = observation?.RequestId ?? Guid.NewGuid().ToString("N");
        // The original admission owns this ID and its sole final receipt.
        if (_ids.Contains(id)) return CaptureEnqueueResult.Invalid;
        var result = !Active ? CaptureEnqueueResult.Inactive
            : cursorFactory == null || string.IsNullOrWhiteSpace(reason) || epoch < 0
                || observation != null && (string.IsNullOrWhiteSpace(id) || observation.SourceWorldEpoch != epoch
                    || !observation.Target.IsValid || observation.Target.WorldEpoch != epoch || observation.PayloadBytes < 0
                    || !double.IsFinite(observation.DeadlineMilliseconds)
                    || observation.DeadlineFrame <= observation.TriggerFrame
                    || observation.ManagedBytes > MaximumTicketBytes)
                ? CaptureEnqueueResult.Invalid
            : _ids.Count >= MaximumRequests || observation != null
                && (_observations >= MaximumRequests - ReservedCaptureRequests
                    || _ticketBytes + observation.ManagedBytes > MaximumTicketBytes)
                ? CaptureEnqueueResult.QueueFull : CaptureEnqueueResult.Accepted;
        var request = new Request(id, reason, epoch, cursorFactory!, completed, observation, finalize);
        if (result != CaptureEnqueueResult.Accepted)
        {
            Stop(request, result == CaptureEnqueueResult.QueueFull ? "queue-full"
                : result == CaptureEnqueueResult.Inactive ? "disabled" : "invalid");
            return result;
        }
        _ids.Add(id);
        if (observation != null) { _observations++; _ticketBytes += observation.ManagedBytes; }
        _requests.Enqueue(request);
        return result;
    }

    internal void Run(long epoch, CaptureWorkBudget budget, string session = "", long generation = 0, long frame = -1)
    {
        Epoch = epoch; Session = session; Generation = generation; Frame = frame >= 0 ? frame : Frame + 1;
        if (!Pending) return;
        var queued = _requests.Count;
        for (var i = 0; i < queued; i++)
        {
            var request = _requests.Dequeue();
            var status = Invalidated(request);
            if (status != null) Finish(request, status);
            if (!request.Ended) _requests.Enqueue(request);
        }
        if (_current != null && Invalidated(_current) is { } invalid)
        {
            var stale = _current;
            Finish(stale, invalid);
            if (stale.Ended && ReferenceEquals(_current, stale)) _current = null;
        }
        if (_current == null)
        {
            if (_requests.Count == 0) return;
            _current = _requests.Dequeue(); LastReason = _current.Reason;
            TotalMilliseconds = PeakMilliseconds = 0; AllocatedBytes = 0; Frames = 0;
        }
        if (_rest > 0) { _rest--; return; }
        if (_requests.Count == 0 && _clock() < _periodicRestUntil) return;
        var current = _current;
        if (Frame < current.ResumeFrame) return;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = _clock();
        _sliceStarted = started; _activeBudget = budget; _observationSlice = current.Observation != null && current.Completion == null;
        var steps = 0;
        string? terminal = null;
        string errorText = "";
        try
        {
            _executing = current;
            if (budget.HasTime) current.Cursor ??= current.Completion is { } completion
                ? current.FinalizeCapture!(completion) : current.Factory();
            while (current.Cursor != null && budget.HasTime
                && (current.Observation == null || steps < ObservationStepsPerFrame
                    && _clock() - started < ObservationMillisecondsPerFrame)
                && budget.TryStep())
            {
                terminal = Invalidated(current);
                if (terminal != null) break;
                steps++; current.Steps++;
                var next = current.Cursor.MoveNext();
                terminal = Invalidated(current);
                if (terminal != null) break;
                if (!next) { terminal = "completed"; break; }
                if (current.Cursor.Current == CaptureSnapshotStream.WaitForNextFrame)
                { current.ResumeFrame = Frame + 1; break; }
            }
        }
        catch (RecObservationTerminated error) { terminal = error.Status; errorText = error.Message; }
        catch (TimeoutException error) { terminal = "timed-out"; errorText = error.Message; }
        catch (Exception error) { terminal = "error"; errorText = error.GetType().Name + ": " + error.Message; }
        finally
        {
            var elapsed = Math.Max(0, _clock() - started);
            current.Total += elapsed; current.Peak = Math.Max(current.Peak, elapsed);
            current.Allocated += GC.GetAllocatedBytesForCurrentThread() - allocated; current.Frames++;
            TotalMilliseconds = current.Total; PeakMilliseconds = current.Peak;
            AllocatedBytes = current.Allocated; Frames = current.Frames;
            _rest = (int)Math.Clamp(Math.Floor(elapsed / budget.MaximumMilliseconds) - 1, 0, MaximumRestFrames);
            _periodicRestUntil = current.Reason == CaptureReason.Periodic && elapsed > budget.MaximumMilliseconds
                ? _clock() + Math.Min(elapsed * PeriodicRestPerMillisecond, MaximumPeriodicRestMilliseconds) : 0;
            _activeBudget = null;
            _executing = null;
        }
        if (terminal != null) { Finish(current, terminal, errorText); if (current.Ended) _current = null; }
    }

    private string? Invalidated(Request request)
    {
        if (request.StopStatus != null) return request.StopStatus;
        // Finalization reads detached data only and must report cancellation/world changes as well.
        if (request.Completion != null) return null;
        if (!Active) return "disabled";
        if (request.Epoch != Epoch) return "world-changed";
        var ticket = request.Observation;
        if (ticket == null) return null;
        if (ticket.Session != Session) return "session-changed";
        if (ticket.CaptureGeneration != Generation) return "generation-changed";
        if (Frame >= ticket.DeadlineFrame || _clock() >= ticket.DeadlineMilliseconds) return "timed-out";
        return null;
    }

    internal bool Cancel(string requestId)
    {
        if (_current?.Id == requestId)
        {
            var request = _current;
            if (request.StopStatus != null) return false;
            if (_executing == request) request.StopStatus = "cancelled";
            else { Stop(request, "cancelled"); _current = null; }
            return true;
        }
        var found = false;
        var count = _requests.Count;
        for (var i = 0; i < count; i++)
        {
            var request = _requests.Dequeue();
            if (request.Id == requestId) { Stop(request, "cancelled"); found = true; }
            else _requests.Enqueue(request);
        }
        return found;
    }

    private void Finish(Request request, string status, string error = "")
    {
        if (request.Ended) return;
        try { request.Cursor?.Dispose(); }
        catch (Exception failure) { status = "error"; error = "cursor-dispose: " + failure.GetType().Name; }
        request.Cursor = null;
        var result = new CaptureCompletion(request.Id, status, request.Total, request.Peak, request.Allocated,
            request.Frames, request.Steps, error);
        if (request.Completion == null && request.FinalizeCapture != null)
        { request.Completion = result; request.StopStatus = null; return; }
        request.Ended = true;
        if (_ids.Remove(request.Id) && request.Observation != null)
        { _observations--; _ticketBytes -= request.Observation.ManagedBytes; }
        request.Completed(request.Completion is { } original
            ? status == "completed" ? original : original with { Status = status, Error = error } : result);
    }

    // No frame pump remains on shutdown/admission rejection. Make one bounded terminal attempt;
    // disposing a refused receipt records explicit incompleteness instead of spinning synchronously.
    private void Stop(Request request, string status)
    {
        if (request.Completion != null && request.Cursor == null)
            request.Completion = request.Completion with { Status = status };
        else Finish(request, status);
        if (request.Ended) return;
        bool waiting;
        try
        {
            request.Cursor = request.FinalizeCapture!(request.Completion!);
            waiting = request.Cursor.MoveNext();
        }
        catch (Exception error) { Finish(request, "error", error.GetType().Name + ": " + error.Message); return; }
        Finish(request, waiting ? status : "completed");
    }

    internal void Reset()
    {
        _rest = 0;
        var status = Active ? "cancelled" : "disabled";
        if (_current != null)
        {
            var current = _current;
            if (_executing == current) current.StopStatus ??= status;
            else { _current = null; Stop(current, status); }
        }
        while (_requests.Count != 0) Stop(_requests.Dequeue(), status);
    }
}

internal sealed class RecObservationTerminated : Exception
{
    internal string Status { get; }
    internal RecObservationTerminated(string status, string message) : base(message) { Status = status; }
}
