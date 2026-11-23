using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The one whole-profile row this package owns: `forge.action.weapon.property_profile`, the write that
/// puts another weapon property profile on one instance and puts the instance's own profile back when the change
/// ends.
///
/// <b>What one profile is.</b> The row's `profile` parameter is a `profile` resource reference, and the id it
/// carries is the game's own `ArchetypeDataBlock.persistentID` as plain decimal text — the same spelling this
/// package already pins for an authored native block id (`WeaponSupplyItems` resolves an `item` reference the
/// same way). The block a profile id names is the game's own property profile: its `ShotDelay` is the rate of
/// fire, its `HipFireSpread`/`AimSpread`/`ShotgunConeSize` are the spread, and the `RecoilDataBlock` its own
/// `RecoilDataID` names carries the recoil. Nothing here invents a profile type: `evidence/weapon-property-profile.json`
/// records that this build declares none, and the ledger row's own hook note is that an implementation has to
/// take over the `m_archetypeData`/`m_recoilData` references itself.
///
/// <b>How it is written.</b> Through the same instance-level block replacement the named `fire_rate`, `spread`
/// and `recoil` rows use — one clone of the instance's own block, the profile's three groups written into it,
/// one swap of `m_archetypeData`/`m_recoilData`. The profile is therefore one write of three things and not a
/// second write path, and the values it changes are exactly the card's three groups: the profile's damage, clip
/// size, cost and the rest stay the instance's own, because the card names three properties and not a whole
/// weapon.
///
/// <b>How it ends.</b> The row mints its own effect handle (`entity_life`, the lifetime its `profile_handle`
/// output declares) through the registration the row is declared in, registers the cancellation that puts the
/// instance's own block back, and restores on the request's own `duration` tick. A change nothing can name could
/// only be ended by the world ending, which is why the handle is cast before the first write.
///
/// The body lives in <see cref="WeaponProfileWrites"/> so this declaration travels into every process that
/// registers this provider, and the native half supplies the source that really resolves a profile and writes a
/// block. A process with no native half refuses the row by code rather than declaring a write nothing performs.
/// </summary>
internal static class WeaponProfileContract
{
    /// <summary>The row's canonical id; the row itself is the generated block table's.</summary>
    public const string CapabilityId = Blocks.ActionWeaponPropertyProfile.Id;

    /// <summary>This provider's one binding of that row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.weapon_property_profile";

    /// <summary>The handler name the binding resolves to, which is also the shape key.</summary>
    public const string HandlerName = "gtfo.weapon.property_profile";

    /// <summary>The resource kind the `profile` parameter addresses. It is a member of the framework's own
    /// resource vocabulary, and this package is its owner for the same reason it owns `pool`: a weapon property
    /// profile is one of the weapon tables this package already reads.</summary>
    public const string ProfileResourceKind = "profile";

    /// <summary>The output port the row publishes its own effect handle on, and the lifetime that port declares.
    /// Both are the reviewed graph's own spelling and are read here so the handle this half casts can never name
    /// a different kind or lifetime than the port it is published into.</summary>
    public const string ProfileHandlePort = "profile_handle";
    public const string ProfileLifetime = "entity_life";

    /// <summary>The permission this row writes under: it changes one equipment instance's own block, which is the
    /// same write the named `fire_rate`, `spread` and `recoil` rows perform.</summary>
    public const string WritePermission = WeaponOverrideContract.WieldWritePermission;

    /// <summary>The ports the body reads: the instance the profile is put on, the duration it lasts, the result
    /// row and the handle the change is named by; and the parameters — `profile` for the reference itself and the
    /// three end-of-state switches the swap applies to the instance's own burst, charge and firing state.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("equipment", "duration").Outputs("result", ProfileHandlePort)
        .Parameters("profile", "end_burst", "end_charge", "end_firing");
    public static readonly HandlerSlot EquipmentSlot = Shape.InputSlot("equipment");
    public static readonly HandlerSlot DurationSlot = Shape.InputSlot("duration");
    public static readonly HandlerSlot ProfileSlot = Shape.ParameterSlot("profile");
    public static readonly HandlerSlot EndBurstSlot = Shape.ParameterSlot("end_burst");
    public static readonly HandlerSlot EndChargeSlot = Shape.ParameterSlot("end_charge");
    public static readonly HandlerSlot EndFiringSlot = Shape.ParameterSlot("end_firing");

    /// <summary>The body table, taken from the writes that own it so a shape and its handler can never be
    /// registered apart.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers() => WeaponProfileWrites.Handlers();

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, HandlerName, "execute", new[] { WritePermission }, Shape)
    };
}
