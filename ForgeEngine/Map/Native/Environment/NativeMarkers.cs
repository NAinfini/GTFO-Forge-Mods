using ForgeEngine.Framework;
using ForgeEngine.Presentation.Native;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>One presentation receiver on each selected peer, using the vanilla GUI marker APIs. Forge's existing
/// presentation delivery carries the entity; this class never sends a second native SyncTrigger broadcast.</summary>
internal static class NativeMarkers
{
    internal static Outcome Handle(CommandContext context, bool ready)
    {
        if (!ready) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!context.TryEntity(EnvironmentContract.NavMarkerZone, out var target))
            return Outcome.Rejected(ResultCode.MarkerTargetRequired);
        if (target.WorldEpoch != context.WorldEpoch
            || EnvironmentZone.Parse(target) != null && target.LifeEpoch != 0)
            return Row(context, target, false, ResultCode.StaleEntity);
        if (context.Boolean(EnvironmentContract.NavMarkerEnabled) is not { } enabled)
            return Row(context, target, false, ResultCode.MarkerEnabledRequired);
        string filter = context.Text(EnvironmentContract.NavMarkerFilter) ?? "";
        bool custom = context.Text(EnvironmentContract.NavMarkerShape.InputSlot("style")) != null
            || context.Text(EnvironmentContract.NavMarkerShape.InputSlot("text")) != null
            || context.Vector(EnvironmentContract.NavMarkerShape.InputSlot("position")) != null
            || context.Vector(EnvironmentContract.NavMarkerShape.InputSlot("offset")) != null
            || context.Count(EnvironmentContract.NavMarkerViewers) > 0 || context.Present(EnvironmentContract.NavMarkerDuration);
        if (custom || EnvironmentZone.Parse(target) == null || !enabled && filter.Length == 0)
        {
            if (filter.Length != 0) return Row(context, target, false, ResultCode.MarkerInstanceFilterUnsupported);
            return MapLabels.HudMarker(context);
        }
        if (EnvironmentZone.Parse(target) is { } address)
            return Zone(context, target, address, filter, enabled);
        return Row(context, target, false, ResultCode.MarkerTargetUnavailable);
    }

    private static Outcome Zone(CommandContext context, EntityReference target, EnvironmentZone address,
        string filter, bool enabled)
    {
        if (filter.Length == 0) return Row(context, target, false, ResultCode.WorldEventObjectFilterRequired);
        if (filter.Length > EnvironmentActions.MaximumFilterLength) return Row(context, target, false, ResultCode.WorldEventObjectFilterTooLong);
        var zone = ZoneIndex.Current(context.WorldEpoch).ZoneAt(new ZoneCoordinates(address.Dimension, address.Layer, address.Zone));
        if (zone == null || WorldEventManager.Current == null) return Row(context, target, false, ResultCode.MarkerTargetUnavailable);
        var results = WorldEventManager.WorldEventObjectResults;
        int count = WorldEventManager.GetWorldEventObjectsNonAlloc(filter, results);
        // A full native query buffer may omit matches. Refuse before drawing any of the returned prefix.
        if (count >= results.Length) return Row(context, target, false, ResultCode.MarkerQueryBudget);
        int matched = 0;
        for (int i = 0; i < count; i++)
        {
            var obj = results[i];
            if (obj == null || !WorldEventManager.CheckWorldEventObjectPlaceFilter(obj, null, null, zone, null)) continue;
            var components = obj.WorldEventComponents;
            if (components == null) continue;
            for (int j = 0; j < components.Length; j++)
            {
                var marker = components[j]?.TryCast<LG_WorldEventNavMarker>();
                if (marker?.m_placeNavMarkerOnGo is not { } place) continue;
                if (enabled) place.PlaceMarker();
                place.SetMarkerVisible(enabled);
                matched++;
            }
        }
        return matched == 0 ? Row(context, target, false, ResultCode.MarkerObjectUnavailable) : Row(context, target, true, enabled ? ResultCode.MarkerShown : ResultCode.MarkerHidden);
    }

    private static Outcome Row(CommandContext context, EntityReference target, bool succeeded, string code)
    {
        if (!succeeded) MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.InstanceMarkerRefused, "Object marker refused.",
            RuntimeLogValue.Of("entity", target.Id), RuntimeLogValue.Of("reason", code));
        return PresentationRows.Answer(context, succeeded ? Status.Partial : Status.Rejected, code);
    }
}
