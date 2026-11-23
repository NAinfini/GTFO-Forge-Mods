using System;
using System.Collections.Generic;

namespace ForgeMap.Native;

internal sealed partial class MapPluginSession
{
    internal InteractionPointSession? InteractionPoints { get; private set; }

    private void StartInteractionPoints(IReadOnlyList<InteractionPoint>? prepared = null)
    {
        if (MapObjects == null) throw new InvalidOperationException("Map objects are not registered.");
        InteractionPoints = InteractionPointSession.Start(_kernel, MapObjects, LevelIdentity.Read,
            room => RoomResolver.Lookup(_kernel.WorldEpoch, room), _report, prepared);
    }

    private void AdmitInteractionPoints(IReadOnlyList<InteractionPoint> rows)
        => InteractionPoints?.AdmitRows(rows);

    private void RefreshInteractionPoints() => InteractionPoints?.Refresh();

    private bool InteractionPointsPending() => InteractionPoints?.Pending ?? false;

    private void TickInteractionPoints() => InteractionPoints?.Tick();

    private void AttachInteractionPointClock()
    {
        if (InteractionPoints != null) InteractionPoints.WorkScheduled = WakeInteractionPointClock;
    }

    private void WakeInteractionPointClock() => _clock?.Wake();

    private void CleanupInteractionPoints(List<Exception> errors)
    {
        var points = InteractionPoints;
        InteractionPoints = null;
        if (points != null) ForgeEngine.Framework.DomainSession.Cleanup(points.Dispose, errors);
    }
}
