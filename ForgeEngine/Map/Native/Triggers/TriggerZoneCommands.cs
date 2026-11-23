using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

internal sealed partial class MapPluginSession
{
    private RuntimeModule WithTriggerZoneCommands(RuntimeModule module)
    {
        var handlers = new Dictionary<string, CommandHandler>(module.Handlers, StringComparer.Ordinal)
        {
            [TriggerZoneActionsContract.StateHandler] = context => ZoneCommand(context, Blocks.ActionMapTriggerZoneState.Id),
            [TriggerZoneActionsContract.MovementHandler] = context => ZoneCommand(context, Blocks.ActionMapTriggerZoneMovement.Id),
            [TriggerZoneActionsContract.ResizeHandler] = context => ZoneCommand(context, Blocks.ActionMapTriggerZoneResize.Id)
        };
        return module with { Handlers = handlers };
    }
    private CommandResult ZoneCommand(CommandContext context, string action)
        => TriggerZones?.Command(context, action) ?? Outcome.Rejected(ResultCode.TriggerZoneMissing);
}

internal sealed partial class TriggerZoneSession
{
    internal TriggerZone? LabelReceiver(EntityReference reference)
    {
        const string prefix = EntityKinds.MapObject + ":";
        if (_disposed || reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var address = TriggerZoneAddress.TryParse(reference.Id[prefix.Length..]);
        return address != null && _source.ById(address.Key) is { Placed: true } zone && zone.Level == _placedLevel ? zone : null;
    }
    private static readonly HandlerSlot Detect = TriggerZoneActionsContract.StateShape.ParameterSlot("detect"),
        Show = TriggerZoneActionsContract.StateShape.ParameterSlot("show"),
        MovementMode = TriggerZoneActionsContract.MovementShape.ParameterSlot("mode"),
        Size = TriggerZoneActionsContract.ResizeShape.InputSlot("size"),
        Duration = TriggerZoneActionsContract.ResizeShape.InputSlot("duration");
    private TriggerZoneController? _controller;
    internal Action? WorkScheduled { get; set; }

    /// <summary>The placed zones of this level in <paramref name="group"/>, added to <paramref name="members"/>.</summary>
    internal void GroupMembers(string group, List<EntityReference> members)
    {
        if (_disposed) return;
        foreach (var zone in _source.Zones)
            if (zone.Placed && zone.Level == _placedLevel && zone.Group == group
                && _mapObjects.AddressReference(TriggerZoneAddress.Of(zone.Id)!.ToString()) is { } reference)
                members.Add(reference);
    }

    private void Index()
    {
        for (var i = 0; i < _source.Zones.Count; i++) _source.Zones[i].RuntimeIndex = i;
    }

    /// <summary>One zone request, read once before the first zone.</summary>
    private struct ZoneRequest
    {
        internal TriggerZoneSession Self;
        internal string Action, First, Second;
        internal double[]? Sizes;
        internal double Seconds;
        internal long World;
        internal bool Authority;
    }

    private static readonly TargetStep<ZoneRequest> ZoneStep = ZoneOne;

    internal CommandResult Command(CommandContext context, string action)
    {
        if (action is not (Blocks.ActionMapTriggerZoneState.Id or Blocks.ActionMapTriggerZoneMovement.Id or Blocks.ActionMapTriggerZoneResize.Id))
            throw new InvalidOperationException("Unknown trigger-zone action.");
        var first = action == Blocks.ActionMapTriggerZoneState.Id ? context.Text(Detect) : action == Blocks.ActionMapTriggerZoneMovement.Id ? context.Text(MovementMode) : "";
        var second = action == Blocks.ActionMapTriggerZoneState.Id ? context.Text(Show) : "";
        var size = action == Blocks.ActionMapTriggerZoneResize.Id ? context.Vector(Size) : null;
        // The resize duration port is ticks (contract §3.2 时间单位); the zone's own animation runs in seconds.
        var seconds = action == Blocks.ActionMapTriggerZoneResize.Id ? context.Number(Duration) / context.TicksPerSecond : 0;
        var request = new ZoneRequest
        {
            Self = this,
            Action = action,
            First = first ?? "",
            Second = second ?? "",
            Sizes = size is { } v ? new[] { v.X, v.Y, v.Z } : null,
            Seconds = seconds ?? double.NaN,
            World = context.WorldEpoch,
            Authority = _authority()
        };
        // A zone is an authored record, not an entity of a native kind, so the skeleton checks no kind: a reference
        // the zone table does not hold is `trigger-zone-missing`, the code this row declares for it.
        // A zone whose write throws ends `failed + unknown` with `native-commit-exception` and the throw is logged.
        var outcome = context.EachTarget(ref request, ZoneStep, maximum: CommandResult.MaximumFacts, report: ZoneFailed);
        Advance(); WorkScheduled?.Invoke();
        return outcome;
    }

    private static readonly Action<EntityReference, Exception> ZoneFailed =
        static (_, error) => MapLog.Contained("trigger-zone.command", error);

    private static void ZoneOne(ref ZoneRequest request, EntityReference reference, int i, RowWriter rows)
    {
        var self = request.Self;
        var code = request.World != self._kernel.WorldEpoch || reference.WorldEpoch != self._kernel.WorldEpoch
            ? ResultCode.StaleEntity : !request.Authority ? ResultCode.AuthorityOrPhase : ResultCode.Completed;
        var index = -1;
        var revision = -1L;
        if (code == ResultCode.Completed)
        {
            // The map-object kind registers no native-instance lookup (a zone is an authored record, not a native object),
            // so the address is resolved through the zone table itself, the same read the label and readback paths use.
            var zone = self.LabelReceiver(reference);
            index = zone?.RuntimeIndex ?? -1;
            var controller = self._controller;
            if (zone == null || controller == null || index < 0 || index >= controller.Count
                || !ReferenceEquals(controller.Zone(index), zone) || zone.Level != self._placedLevel)
                code = ResultCode.TriggerZoneMissing;
            else
            {
                revision = controller.Motion(index).Revision;
                code = controller.Change(index, request.World, request.Action, request.First, request.Second, self.Now,
                    request.Sizes, request.Seconds);
                if (code == ResultCode.Completed) self.StateChanged(index);
            }
        }
        var done = code == ResultCode.Completed;
        if (done && self._controller!.Motion(index).Revision != revision) self.Publish(index);
        if (done) rows.End(Status.Succeeded, Commit.Confirmed, code);
        else rows.End(Status.Rejected, Commit.None, code);
        Record(request.Action, reference.Id, request.First, code);
    }

    private static void Record(string action, string address, string mode, string result)
    {
        // Each domain code has one fixed level. The kernel's step.finished retains the refusal/error status.
        var level = RuntimeLogLevel.Info;
        if (!MapLog.Logs(level)) return;
        var code = action switch
        {
            Blocks.ActionMapTriggerZoneState.Id => TriggerZoneActionsContract.StateLog,
            Blocks.ActionMapTriggerZoneResize.Id => TriggerZoneActionsContract.ResizeLog,
            _ => TriggerZoneActionsContract.MovementLog
        };
        var facts = new RuntimeLogFacts
        {
            Entity = address,
            Values = new[] {
            RuntimeLogValue.Of("zone", address), RuntimeLogValue.Of("operation", mode), RuntimeLogValue.Of("result", result) }
        };
        MapLog.Write(level, code, in facts);
    }
}
