using System;
using System.Linq;
using ForgeEngine.GameBindings;

namespace ForgeMap.Native;

internal sealed partial class TriggerZoneSession
{
    internal MapTriggerZoneInspection? InspectState(string id, long world)
    {
        if (!TryCurrentZone(id, world, out var zone, out _)) return null;
        // ById is the very same source TriggerZoneModule.Tick reads. Copy its effective values so a consumer
        // cannot change production membership or confuse authored geometry with a native collision body.
        return new(zone!.Id, zone.Level.ToString(), world, "production-trigger-zone-table",
            zone.Shape.ToString().ToLowerInvariant(), zone.Who.ToString().ToLowerInvariant(), zone.Enabled, zone.BlocksPlayers,
            Array.AsReadOnly(zone.Position.ToArray()), Array.AsReadOnly(zone.Rotation.ToArray()), Array.AsReadOnly(zone.Size.ToArray()));
    }

    private bool TryCurrentZone(string id, long world, out TriggerZone? zone, out string reason)
    {
        zone = null;
        reason = "trigger-zone-session-disposed";
        if (_disposed) return false;
        reason = "trigger-zone-world-mismatch";
        if (_placedEpoch != world || _kernel.WorldEpoch != world) return false;
        reason = "trigger-zone-level-mismatch";
        if (_level() is not { } level || _placedLevel != level) return false;
        reason = "trigger-zone-id-invalid";
        if (TriggerZoneAddress.Of(id) is not { } address) return false;
        reason = "trigger-zone-not-placed";
        zone = _source.ById(address.Key);
        if (zone == null || !zone.Placed || zone.Level != level) return false;
        reason = ""; return true;
    }
}
