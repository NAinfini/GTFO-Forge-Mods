using System;
using System.Collections.Generic;

namespace ForgeDevelopment.Native;

// One generation run owns one report/scan pair. No Unity objects or private clock live here.
internal sealed class ProjectInspectionSession
{
    internal DiagnosticsReport Report { get; }
    internal ProjectObjectReferenceScan? Scan { get; }
    internal long WorldEpoch { get; }
    internal bool IsClosed { get; private set; }
    internal bool IsPartial { get; private set; }
    private bool _started, _cancelled;
    internal string Outcome => !IsClosed ? "inspection_pending"
        : Scan?.Snapshot().ScanStatus == ProjectScanStatus.Rejected ? "inspection_rejected"
        : _cancelled ? "inspection_cancelled"
        : IsPartial ? "inspection_partial" : "inspection_complete";

    internal ProjectInspectionSession(DiagnosticsReport report, ProjectObjectReferenceScan? scan, long worldEpoch)
    {
        ArgumentNullException.ThrowIfNull(report);
        ProjectObjectReferences.ValidateEpoch(worldEpoch, null);
        if (scan != null && scan.Snapshot().WorldEpoch != worldEpoch)
            throw new ArgumentException("The scan must belong to this world.", nameof(scan));
        Report = report; Scan = scan; WorldEpoch = worldEpoch;
        if (scan != null) report.AttachObjectReferences(scan);
    }

    internal bool AcceptWorld(long currentEpoch, long? currentTick)
    {
        if (IsClosed) return false;
        if (WorldEpoch > 0 && currentEpoch == WorldEpoch) return true;
        // The new world's tick must never be written into the old world's receipt.
        Cancel(null, WorldEpoch == 0 ? "world_epoch_unavailable" : "world_epoch_changed");
        return false;
    }

    internal void Start(IReadOnlyList<ProjectLayoutKey> layouts, long? tick)
    {
        if (IsClosed || _started) return;
        _started = true;
        Scan?.Start(layouts, tick);
        if (IsPartial) Scan?.MarkPartial();
    }

    internal void MarkPartial()
    {
        if (IsClosed) return;
        IsPartial = true;
        Scan?.MarkPartial();
    }

    internal void Complete(long? tick)
    {
        if (IsClosed) return;
        if (!_started) MarkPartial();
        Scan?.Complete(tick);
        var status = Scan?.Snapshot().ScanStatus;
        IsPartial |= status.HasValue && status != ProjectScanStatus.Complete;
        IsClosed = true;
    }

    internal void Cancel(long? tick, string reason)
    {
        if (IsClosed) return;
        Scan?.Cancel(tick);
        _cancelled = true;
        IsClosed = true;
        Report.Check("inspection", "world", "cancelled", reason);
    }
}
