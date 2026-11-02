using System.Text.Json;
using ForgeOrdnance;
using GameData;
using Gear;
using Player;

namespace ForgeOrdnanceArsenal;

internal sealed record HeavyParameters(HeavyWeaponSpec Spec, uint GearId);

/// <summary>Payload type <c>arsenal.HeavyWeapon</c>: the weapon arrives racked in ForgeOrdnance's drop pod (reused through
/// <c>OrdnanceDelivery</c>), standing at true scale in a POD bay (a direct arrival lays it on the ground with no pod). A player takes it with the use key; the host grants the
/// first claim. The taker's special weapon slot then holds the lent game gear, shown with this package's model, until its
/// ammunition is spent, the drop key is pressed or the player goes down; then the player's own special weapon comes back.</summary>
internal sealed class HeavyWeaponPayload : IOrdnancePayload
{
    public object Parse(JsonElement payload)
    {
        var spec = HeavyWeaponSpec.Parse(payload);
        // Load time is where a bad model is reported, not the first shot: the model must exist and carry the gun sockets.
        if (ArsenalArt.CheckWeaponModel(spec.Model) is { } missing) throw new PayloadParameterException("Model", missing);
        var block = spec.GearId.HasValue ? GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(spec.GearId.Value) : GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(spec.GearName!);
        if (block == null || block.WasCollected) throw new PayloadParameterException("Gear", "no offline gear block " + (spec.GearId.HasValue ? spec.GearId.Value.ToString() : "'" + spec.GearName + "'") + " in this install.");
        if (string.IsNullOrEmpty(block.GearJSON)) throw new PayloadParameterException("Gear", "the gear block has no GearJSON.");
        var baseItem = new GearIDRange(block.GearJSON).GetCompID(eGearComponent.BaseItem);
        var item = GameDataBlockBase<ItemDataBlock>.GetBlock(baseItem);
        if (item == null || item.inventorySlot != InventorySlot.GearSpecial)
            throw new PayloadParameterException("Gear", "gear block " + block.persistentID + " is not a special slot weapon (base item " + baseItem + ").");
        // Last, so a refused definition holds no hash: the model's id must not share a hash with another accepted model (the hash is
        // what travels and what marks the lent gear).
        if (HeavyModels.Register(spec.Model) is { } clash) throw new PayloadParameterException("Model", clash);
        return new HeavyParameters(spec, block.persistentID);
    }

    public bool HasOwnArrival => true;

    /// <summary>A direct arrival lays the weapon on the ground at the landing point, ready to take at once.</summary>
    public bool SupportsDirect => true;

    public void Execute(OrdnanceExecution execution) => HeavyWeapons.HostArrived(execution);

    public void OnArrived(OrdnancePresentation presentation) => HeavyWeapons.ShowRack(presentation);

    public void OnData(OrdnancePayloadData data) => HeavyWeapons.OnData(data);
}
