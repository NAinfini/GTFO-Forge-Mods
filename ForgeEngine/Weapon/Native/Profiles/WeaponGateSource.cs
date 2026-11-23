using System;
using ForgeEngine.Framework;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of <see cref="WeaponGateContract"/>: one declared availability gate of one equipment life,
/// read off the item that life's own slot currently holds. The equipment is reached through the adapter's own
/// current-check (<see cref="EquipmentNativeAdapter.EquippableOf"/>), which is the same check the magazine read
/// makes, so a life this machine no longer answers for refuses instead of being read from whatever the slot holds
/// now. There is no authority gate: the row reads and never writes, so a client answers about its own equipment.
///
/// The five members are boolean properties `ItemEquippable` declares and the weapon families below it
/// override (`catalog/native/native-runtime.json`, type `ItemEquippable`: `WeaponSwitchAllowed`, `CanReload`,
/// `RunAllowed`, `AllowPlayerInteraction`, `AimingAllowed`). They are read through the base wrapper, so the
/// answer is the override the item's own family supplies rather than the base's.
/// </summary>
internal sealed class WeaponGateSource : IWeaponGateSource
{
    private readonly EquipmentNativeAdapter _adapter;

    internal WeaponGateSource(EquipmentNativeAdapter adapter)
        => _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    public bool TryGate(EntityReference equipment, int gateIndex, out bool value, out string code)
    {
        value = false;
        if (_adapter.EquippableOf(equipment) is not { } found)
        {
            code = WeaponGateReads.StaleEquipmentCode;
            return false;
        }
        var item = found.Item;
        try
        {
            switch (gateIndex)
            {
                case WeaponGateContract.SwitchGate: value = item.WeaponSwitchAllowed; break;
                case WeaponGateContract.ReloadGate: value = item.CanReload; break;
                case WeaponGateContract.RunGate: value = item.RunAllowed; break;
                case WeaponGateContract.InteractGate: value = item.AllowPlayerInteraction; break;
                case WeaponGateContract.AimGate: value = item.AimingAllowed; break;
                default: code = WeaponGateReads.GateCode; return false;
            }
        }
        catch (Exception failure)
        {
            NativeCallFailure.Note("WeaponGateSource.TryGate", failure);
            code = WeaponGateReads.ReadFailedCode;
            return false;
        }
        code = "";
        return true;
    }
}
