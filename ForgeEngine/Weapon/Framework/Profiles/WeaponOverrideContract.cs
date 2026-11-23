using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The `forge.action.weapon.*` instance-override rows this slice registers; the rows themselves are the generated
/// block table's. Three of them are the named values
/// that describe how one instance shoots — its rate, its spread and its recoil — which is the whole of the node
/// list's `a-w-stats`; the fourth is the generic form of the same write, one named field of the same vocabulary
/// with the operation that name is changed by, and it is where every "change a weapon number for a while" card
/// lands whose number has no named row of its own.
///
/// Each row carries a body, because each of them is an instance-level block replacement this package can
/// actually perform: <see cref="WeaponActionRuntime"/> decides the values and
/// <c>ForgeWeapon.Native.WeaponOverrideApplier</c> is the only code that writes them. The state family those
/// rows were once paired with is gone: `reload` is the one row of it the node list keeps, and it is declared by
/// the framework's own action vocabulary.
///
/// A row and its binding travel together — a declared action nothing answers would be a promise this package
/// does not keep — so <see cref="ModuleDefinition.Create"/> declares the four rows (<see cref="All"/>) and binds
/// <see cref="DeclareBindings"/> exactly when the handler table is supplied.
/// </summary>
internal static class WeaponOverrideContract
{
    public const string FireRateCapability = Blocks.ActionWeaponFireRate.Id;
    public const string SpreadCapability = Blocks.ActionWeaponSpread.Id;
    public const string RecoilCapability = Blocks.ActionWeaponRecoil.Id;
    public const string PropertyCapability = Blocks.ActionWeaponProperty.Id;

    public const string FireRateBinding = ModuleDefinition.ProviderId + ".binding.weapon_fire_rate";
    public const string SpreadBinding = ModuleDefinition.ProviderId + ".binding.weapon_spread";
    public const string RecoilBinding = ModuleDefinition.ProviderId + ".binding.weapon_recoil";
    public const string PropertyBinding = ModuleDefinition.ProviderId + ".binding.weapon_property";

    public const string FireRateHandler = "gtfo.weapon.fire_rate";
    public const string SpreadHandler = "gtfo.weapon.spread";
    public const string RecoilHandler = "gtfo.weapon.recoil";
    public const string PropertyHandler = "gtfo.weapon.property";

    /// <summary>The permission every row here writes under: each one changes an equipment instance's own block,
    /// which is a write to state the wield bindings already read under their read permission.</summary>
    public const string WieldWritePermission = "gtfo.equipment.wield.write";

    /// <summary>Every row of this family, in catalog order.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        FireRateCapability, SpreadCapability, RecoilCapability, PropertyCapability
    };

    /// <summary>The handler port sets, resolved at registration against the rows. Each one declares exactly
    /// the ports its row has on the request side, so a port this package reads but the row does not declare — or
    /// the reverse — fails at registration instead of at a plan load.</summary>
    public static readonly HandlerShape FireRateShape = new HandlerShape()
        .Inputs("equipment", "rate", "duration", "cap")
        .Outputs("next", "result", "modifier").Parameters("operation");
    public static readonly HandlerSlot FireRateEquipmentSlot = FireRateShape.InputSlot("equipment");
    public static readonly HandlerSlot FireRateValueSlot = FireRateShape.InputSlot("rate");
    public static readonly HandlerSlot FireRateDurationSlot = FireRateShape.InputSlot("duration");
    public static readonly HandlerSlot FireRateCapSlot = FireRateShape.InputSlot("cap");
    public static readonly HandlerSlot FireRateOperationSlot = FireRateShape.ParameterSlot("operation");

    public static readonly HandlerShape SpreadShape = new HandlerShape()
        .Inputs("equipment", "cone", "movement_scale", "aim_scale", "spread_multiplier", "cap")
        .Outputs("next", "result");
    public static readonly HandlerSlot SpreadEquipmentSlot = SpreadShape.InputSlot("equipment");
    public static readonly HandlerSlot SpreadConeSlot = SpreadShape.InputSlot("cone");
    public static readonly HandlerSlot SpreadMovementScaleSlot = SpreadShape.InputSlot("movement_scale");
    public static readonly HandlerSlot SpreadAimScaleSlot = SpreadShape.InputSlot("aim_scale");
    public static readonly HandlerSlot SpreadMultiplierSlot = SpreadShape.InputSlot("spread_multiplier");
    public static readonly HandlerSlot SpreadCapSlot = SpreadShape.InputSlot("cap");

    public static readonly HandlerShape RecoilShape = new HandlerShape()
        .Inputs("equipment", "horizontal", "vertical", "recovery", "camera_kick", "cap")
        .Outputs("next", "result");
    public static readonly HandlerSlot RecoilEquipmentSlot = RecoilShape.InputSlot("equipment");
    public static readonly HandlerSlot RecoilHorizontalSlot = RecoilShape.InputSlot("horizontal");
    public static readonly HandlerSlot RecoilVerticalSlot = RecoilShape.InputSlot("vertical");
    public static readonly HandlerSlot RecoilRecoverySlot = RecoilShape.InputSlot("recovery");
    public static readonly HandlerSlot RecoilCameraKickSlot = RecoilShape.InputSlot("camera_kick");
    public static readonly HandlerSlot RecoilCapSlot = RecoilShape.InputSlot("cap");

    public static readonly HandlerShape PropertyShape = new HandlerShape().Inputs("equipment", "value")
        .Outputs("next", "result", "property").Parameters("field", "operation");
    public static readonly HandlerSlot PropertyEquipmentSlot = PropertyShape.InputSlot("equipment");
    public static readonly HandlerSlot PropertyValueSlot = PropertyShape.InputSlot("value");
    public static readonly HandlerSlot PropertyFieldSlot = PropertyShape.ParameterSlot("field");
    public static readonly HandlerSlot PropertyOperationSlot = PropertyShape.ParameterSlot("operation");

    /// <summary>The handler table as the session supplies it: one entry per row, keyed by the handler name its
    /// binding names. A session that cannot supply one of these registers the rest and leaves that row
    /// undeclared, which is what a declaration with no body is.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers(CommandHandler fireRate,
        CommandHandler spread, CommandHandler recoil, CommandHandler property)
        => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [FireRateHandler] = fireRate ?? throw new ArgumentNullException(nameof(fireRate)),
            [SpreadHandler] = spread ?? throw new ArgumentNullException(nameof(spread)),
            [RecoilHandler] = recoil ?? throw new ArgumentNullException(nameof(recoil)),
            [PropertyHandler] = property ?? throw new ArgumentNullException(nameof(property))
        };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(FireRateBinding, FireRateCapability, FireRateHandler, "execute", new[] { WieldWritePermission }, FireRateShape),
        new ProviderBinding(SpreadBinding, SpreadCapability, SpreadHandler, "execute", new[] { WieldWritePermission }, SpreadShape),
        new ProviderBinding(RecoilBinding, RecoilCapability, RecoilHandler, "execute", new[] { WieldWritePermission }, RecoilShape),
        new ProviderBinding(PropertyBinding, PropertyCapability, PropertyHandler, "execute", new[] { WieldWritePermission }, PropertyShape)
    };
}
