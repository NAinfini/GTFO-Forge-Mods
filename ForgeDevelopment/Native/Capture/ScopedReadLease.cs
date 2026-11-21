using System;
using System.Collections.Generic;

namespace ForgeDevelopment.Native;

internal sealed record RecProjectionOperation(string Kind, string Path, string Parent, int Index, int Depth,
    string Token = "", bool ValidateOnly = false);
internal sealed record RecProjectionStep(CaptureRow? Row, IReadOnlyList<RecProjectionOperation> Next, string? Failure = null);

// Only reviewed readers implement this interface. A returned row contains frozen managed values.
internal interface IRecProjectionAdapter : IDisposable
{
    object? Resolve(TargetBinding binding);
    bool IsCurrent(TargetBinding binding, object root, RecProjectionOperation operation);
    RecProjectionStep Read(object root, RecProjectionOperation operation);
}

internal sealed record RecReadContext(int ThreadId, string Session, long WorldEpoch, long Generation,
    long Frame, double Milliseconds, bool Active, string InactiveStatus = "cancelled");

internal sealed class ScopedReadLease
{
    private readonly ObservationTicket _ticket;
    private readonly Func<RecReadContext> _context;
    private readonly Func<string, string, bool> _installation;
    private readonly Func<bool> _canRead;

    internal ScopedReadLease(ObservationTicket ticket, Func<RecReadContext> context,
        Func<string, string, bool> installation, Func<bool>? canRead = null)
    { _ticket = ticket; _context = context; _installation = installation; _canRead = canRead ?? (() => true); }

    private void Check()
    {
        if (RecObservation.InHook) throw new RecObservationTerminated("invalid", "hook-read-refused");
        var context = _context();
        string? reason = !_ticket.Target.IsValid || _ticket.Target.WorldEpoch != _ticket.SourceWorldEpoch ? "invalid"
            : !context.Active ? context.InactiveStatus
            : Environment.CurrentManagedThreadId != context.ThreadId ? "error"
            : context.Session != _ticket.Session ? "session-changed"
            : context.WorldEpoch != _ticket.SourceWorldEpoch ? "world-changed"
            : context.Generation != _ticket.CaptureGeneration ? "generation-changed"
            : context.Frame >= _ticket.DeadlineFrame || context.Milliseconds >= _ticket.DeadlineMilliseconds ? "timed-out"
            : _ticket.EntryId.Length != 0 && !_installation(_ticket.EntryId, _ticket.InstallationId) ? "installation-changed" : null;
        if (reason != null) throw new RecObservationTerminated(reason, "read lease refused: " + reason);
    }

    private void CheckRead()
    {
        Check();
        // An overrun cannot keep an unvalidated native value for the next frame. Discard it and close the attempt.
        if (!_canRead()) throw new RecObservationTerminated("truncated", "microstep-time-budget");
    }

    internal RecProjectionStep Read(IRecProjectionAdapter adapter, RecProjectionOperation operation)
    {
        Check();
        if (!_canRead()) throw new RecObservationDeferred();
        using var scope = RecObservation.EnterReadLease(CheckRead);
        var root = adapter.Resolve(_ticket.Target);
        Check();
        if (!_canRead()) throw new RecObservationDeferred();
        if (root == null) throw new RecObservationTerminated("target-unresolved", "owner did not resolve the original life");
        if (!adapter.IsCurrent(_ticket.Target, root, operation))
            throw new RecObservationTerminated("target-replaced", "owner/path no longer names the original reference");
        Check();
        if (!_canRead()) throw new RecObservationDeferred();
        var step = adapter.Read(root, operation);
        CheckRead();
        if (!adapter.IsCurrent(_ticket.Target, root, operation))
            throw new RecObservationTerminated("target-replaced", "life or collection changed during the read");
        CheckRead();
        if (step.Failure != null) throw new RecObservationTerminated(step.Failure, "projection refused: " + operation.Path);
        return step;
    }
}

internal sealed class RecObservationDeferred : Exception { }
