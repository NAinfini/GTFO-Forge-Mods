using System;

namespace ForgeDevelopment.Native;

/// <summary>
/// The session side of the change writer. Snapshots use the budgeted CaptureSnapshotStream cursor;
/// change records have one bounded immutable result.
/// </summary>
internal static partial class CaptureStateWriter
{
    /// <summary>Writes the differential snapshot a change pass produces. It is the same channel with a different kind
    /// so a reader can take the periodic snapshots and the per-frame diff separately.</summary>
    internal static void WriteChanges(CaptureChangeResult result, long tick, string scope)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!RecSession.WriteFrozen(Channel, ChangesKind, json => WriteChangeBody(json, result, tick, scope), RecFrozenBytes.Measure(result) + RecFrozenBytes.Measure(scope)))
            throw new InvalidOperationException("capture-change-recorder-backpressure");
    }

}
