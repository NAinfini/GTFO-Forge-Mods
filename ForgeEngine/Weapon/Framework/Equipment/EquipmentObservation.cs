using ForgeEngine.Framework;

namespace ForgeWeapon;

internal enum EquipmentLocation { Inventory, World, Deployed }

/// <summary>Trusted adapter input, not a wire schema or proof of native execution.
/// Entity/world/life and owner references must come from verified lifecycle observations.
/// Resource identity, inventory slot and active instance identity are deliberately separate.</summary>
internal sealed record EquipmentObservation(EntityReference Entity, string ResourceId,
    string ResourceRevision, EntityReference? Owner, string? Slot, EquipmentLocation Location,
    bool IsReady, bool IsWielded);

/// <summary>Process-local precondition ticket. Never serialize it as ownership authority,
/// a resource reservation, or a checkpoint/network identity.</summary>
internal sealed class EquipmentUseTicket
{
    internal EquipmentUseTicket(object session, long revision, EquipmentObservation observation,
        bool requireWielded)
    { Session = session; Revision = revision; Observation = observation; RequireWielded = requireWielded; }
    internal object Session { get; }
    internal long Revision { get; }
    internal bool RequireWielded { get; }
    public EquipmentObservation Observation { get; }
}
