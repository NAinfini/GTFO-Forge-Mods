using System;
using System.Globalization;
using ForgeEngine.Framework;
using SNetwork;

namespace ForgePlayer.Native;

internal readonly record struct PlayerBackpackChange(EntityReference? Player, int Slot, PlayerBackpackSample Before);

internal sealed class PlayerBackpackFacts : IDisposable
{
    private readonly RuntimeModuleHandle _registration;
    private readonly RuntimeSubscriptionGate _gate;
    private readonly IForgeEngine _kernel;
    private readonly PlayerIdentityModule _players;
    private readonly Action<string> _report;
    private long _sequence;
    internal static PlayerBackpackFacts? Installed { get; private set; }

    internal PlayerBackpackFacts(RuntimeModuleHandle registration, IForgeEngine kernel, PlayerIdentityModule players, Action<string> report)
    {
        _registration = registration; _gate = registration.SubscriptionGate(PlayerBackpackContract.ChangedBinding);
        _kernel = kernel; _players = players; _report = report; Installed = this;
    }

    internal PlayerBackpackChange Before(SNet_Player owner, int slot)
    {
        if (!_gate.HasSubscribers || !_players.Authoritative || _players.ReferenceOf(owner) is not { } reference
            || _players.CurrentAgent(reference) is not { } agent) return default;
        if (!PlayerBackpackReads.TryRead(agent, slot, out var before, out var code))
        { _report("player.backpack-slot.before: " + code); return default; }
        return new(reference, slot, before);
    }

    internal void After(PlayerBackpackChange observation)
    {
        if (observation.Player is not { } reference || !_gate.HasSubscribers || !_players.Authoritative
            || _players.CurrentAgent(reference) is not { } agent) return;
        if (!PlayerBackpackReads.TryRead(agent, observation.Slot, out var after, out var code))
        { _report("player.backpack-slot.after: " + code); return; }
        if (after.Backpack != observation.Before.Backpack || after == observation.Before) return;
        var value = after.Value;
        var previous = observation.Before.Value;
        var e = _registration.BeginEvent(_gate);
        var values = e.Values;
        values.Entity(Blocks.TriggerPlayerBackpackSlotChanged.OutPlayer, reference);
        values.Integer(Blocks.TriggerPlayerBackpackSlotChanged.OutSlot, observation.Slot);
        if (previous.ItemId is { } previousItem) values.Integer(Blocks.TriggerPlayerBackpackSlotChanged.OutPreviousItemId, previousItem);
        else values.Null(Blocks.TriggerPlayerBackpackSlotChanged.OutPreviousItemId);
        values.Boolean(Blocks.TriggerPlayerBackpackSlotChanged.OutOccupied, value.Occupied);
        if (value.ItemId is { } item) values.Integer(Blocks.TriggerPlayerBackpackSlotChanged.OutItemId, item);
        else values.Null(Blocks.TriggerPlayerBackpackSlotChanged.OutItemId);
        if (previous.GearId is { } previousGear) values.Integer(Blocks.TriggerPlayerBackpackSlotChanged.OutPreviousGearId, previousGear);
        else values.Null(Blocks.TriggerPlayerBackpackSlotChanged.OutPreviousGearId);
        if (value.GearId is { } gear) values.Integer(Blocks.TriggerPlayerBackpackSlotChanged.OutGearId, gear);
        else values.Null(Blocks.TriggerPlayerBackpackSlotChanged.OutGearId);
        if (value.Status is { } status) values.Enum(Blocks.TriggerPlayerBackpackSlotChanged.OutStatus, status);
        else values.Null(Blocks.TriggerPlayerBackpackSlotChanged.OutStatus);
        values.Boolean(Blocks.TriggerPlayerBackpackSlotChanged.OutLoaded, value.Loaded);
        string id = "player.backpack-slot:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + (++_sequence).ToString(CultureInfo.InvariantCulture);
        e.Raise(id, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), WorldScope.Of(_kernel.WorldEpoch));
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
    }

    public void Dispose() { if (ReferenceEquals(Installed, this)) Installed = null; }
}
