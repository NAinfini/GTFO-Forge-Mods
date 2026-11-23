using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap;

internal static class TriggerZoneActionsContract
{
    public const string TextMissing = "trigger-zone-text-missing";
    public const string StateLog = "map.trigger-zone-state";
    public const string MovementLog = "map.trigger-zone-movement";
    public const string ResizeLog = "map.trigger-zone-resize";

    public const string StateHandler = "gtfo.map.trigger_zone_state";
    public const string MovementHandler = "gtfo.map.trigger_zone_movement";
    public const string ResizeHandler = "gtfo.map.trigger_zone_resize";

    public static readonly HandlerShape StateShape = new HandlerShape().Inputs("zones").Outputs("result").Parameters("detect", "show");
    public static readonly HandlerShape MovementShape = new HandlerShape().Inputs("zones").Outputs("result").Parameters("mode");
    public static readonly HandlerShape ResizeShape = new HandlerShape().Inputs("zones", "size", "duration").Outputs("result");

    public static ProviderBinding[] Bindings() => new[]
    {
        Bind(Blocks.ActionMapTriggerZoneState.Id, StateHandler, "execute", StateShape),
        Bind(Blocks.ActionMapTriggerZoneMovement.Id, MovementHandler, "execute", MovementShape),
        Bind(Blocks.ActionMapTriggerZoneResize.Id, ResizeHandler, "execute", ResizeShape)
    };
    private static ProviderBinding Bind(string id, string handler, string mode, HandlerShape shape)
        => new(TriggerZoneContract.ProviderId + ".binding." + id["forge.".Length..], id, handler, mode, Array.Empty<string>(), shape);
}

/// <summary>Indexed zone state, built at table load. Name/group dictionaries are used only at the action/query
/// boundary; movement and rendering hold direct indices and object references.</summary>
internal sealed class TriggerZoneController
{
    private readonly IReadOnlyList<TriggerZone> _zones;
    private readonly TriggerZoneMotion[] _motion;
    public TriggerZoneController(IReadOnlyList<TriggerZone> zones, long epoch, double now, bool host)
    {
        _zones = zones; WorldEpoch = epoch; _motion = new TriggerZoneMotion[zones.Count];
        for (var i = 0; i < zones.Count; i++) _motion[i] = new TriggerZoneMotion(zones[i], now, host);
    }
    public long WorldEpoch { get; private set; }
    /// <summary>A host migration keeps the level and its zones; the controller moves to the new world number.</summary>
    public void FollowWorld(long epoch) => WorldEpoch = epoch;
    public int Count => _zones.Count;
    public TriggerZoneMotion Motion(int index) => _motion[index];
    public TriggerZone Zone(int index) => _zones[index];

    public string Change(int index, long epoch, string action, string first, string second, double now,
        IReadOnlyList<double>? size = null, double duration = 0)
    {
        if (epoch != WorldEpoch) return ResultCode.StaleEntity;
        if (index < 0 || index >= _zones.Count || !_zones[index].Placed) return ResultCode.TriggerZoneMissing;
        var zone = _zones[index]; var motion = _motion[index];
        switch (action)
        {
            case Blocks.ActionMapTriggerZoneState.Id:
                if (first is not ("on" or "off" or "unchanged") || second is not ("show" or "hide" or "unchanged"))
                    return ResultCode.TriggerZoneInput;
                var wasEnabled = zone.Enabled; var wasVisible = zone.Visible;
                if (first != "unchanged") zone.Enabled = first == "on";
                if (second != "unchanged") zone.Visible = second == "show";
                if (wasEnabled != zone.Enabled || wasVisible != zone.Visible) motion.Changed();
                return ResultCode.Completed;
            case Blocks.ActionMapTriggerZoneMovement.Id: return motion.Move(first, now);
            case Blocks.ActionMapTriggerZoneResize.Id: return size == null ? ResultCode.TriggerZoneInput : motion.Resize(size, duration, now);
            default: return ResultCode.TriggerZoneInput;
        }
    }
}
