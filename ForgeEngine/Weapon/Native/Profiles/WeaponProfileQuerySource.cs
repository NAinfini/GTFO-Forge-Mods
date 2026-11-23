using System;
using ForgeEngine.Framework;
using BulletWeapon = global::Gear.BulletWeapon;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of `forge.query.weapon.profile`: the seven numbers one equipment instance's own block carries
/// right now, read from the very pointers the instance-override write swaps. `BulletWeaponArchetype.m_archetypeData`
/// is the block the fire path reads every shot, so a value read here is the one the game acts on — including the
/// private clone <see cref="WeaponOverrideApplier"/> installed, which is the whole reason the read asks the same
/// instance lookup the write uses instead of the game's shared block table.
///
/// Nothing is written and nothing is cached: every call re-reads the weapon, its archetype and the two blocks. An
/// instance this machine cannot name, a missing block pointer and a recoil half whose `horizontalScale`/
/// `verticalScale`/`power` range is absent are all refusals with their own codes, because a profile answered as
/// zeros reads exactly like a weapon whose spread really is zero.
///
/// The weapon lookup is injected rather than reached here for the same reason the applier's is: it is the
/// equipment domain's own current-instance answer, and this file must not keep a second table of which life holds
/// which item.
/// </summary>
internal sealed class WeaponProfileQuerySource : IWeaponProfileReadSource
{
    /// <summary>The equipment reference is not one this machine answers for right now: another world, an ended
    /// life, or a slot whose item was replaced.</summary>
    internal const string StaleEquipmentCode = "stale-equipment";
    /// <summary>The instance's archetype, its block, or the recoil block its `RecoilDataID` names is not there,
    /// so there is no number to read rather than a block full of zeros.</summary>
    internal const string BlockMissingCode = "weapon-profile-block-missing";
    /// <summary>A native member read threw, so the profile is unknown rather than reported.</summary>
    internal const string ReadFailedCode = "weapon-profile-read-failed";

    private readonly Func<EntityReference, BulletWeapon?> _weaponOf;

    internal WeaponProfileQuerySource(Func<EntityReference, BulletWeapon?> weaponOf)
        => _weaponOf = weaponOf ?? throw new ArgumentNullException(nameof(weaponOf));

    /// <summary>One instance's own spread and recoil numbers, or false with the code the read refused by. Every
    /// member is read from the live objects; none is derived from another and none is defaulted.</summary>
    public bool TryProfile(EntityReference equipment, out WeaponProfileSample sample, out string code)
    {
        sample = default;
        BulletWeapon? weapon;
        try { weapon = _weaponOf(equipment); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponProfileQuerySource.TryProfile", failure); code = StaleEquipmentCode; return false; }
        if (weapon == null || weapon.Pointer == IntPtr.Zero) { code = StaleEquipmentCode; return false; }
        var archetype = weapon.m_archeType;
        if (archetype == null || archetype.Pointer == IntPtr.Zero) { code = BlockMissingCode; return false; }
        var block = archetype.m_archetypeData;
        if (block == null || block.Pointer == IntPtr.Zero) { code = BlockMissingCode; return false; }
        var recoil = archetype.m_recoilData;
        if (recoil == null || recoil.Pointer == IntPtr.Zero) { code = BlockMissingCode; return false; }
        // The three recoil values the firing path samples are range objects of their own; a block whose own
        // `RecoilDataID` resolved to no such range has no destination for that value, so its profile is refused
        // as a whole rather than answered with the spread half alone.
        if (recoil.horizontalScale is not { } horizontal || recoil.verticalScale is not { } vertical
            || recoil.power is not { } kick)
        { code = BlockMissingCode; return false; }
        try
        {
            sample = new WeaponProfileSample(block.ShotgunConeSize, block.HipFireSpread, block.AimSpread,
                horizontal.Min, vertical.Min, recoil.spring, kick.Min);
        }
        catch (Exception failure) { NativeCallFailure.Note("WeaponProfileQuerySource.TryProfile#2", failure); code = ReadFailedCode; return false; }
        code = "";
        return true;
    }
}
