using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>What one weapon action decided, before the native half has written anything. The four numeric rows
/// all have the same life: read the request's own ports, turn them into a field set, and let the applier write
/// that set. An outcome that carries no fields is a refusal, and the code names which of the row's ports or
/// which native destination was the reason — never a silent success. The native adapter is the one place that
/// turns this into a result.</summary>
internal sealed record WeaponActionOutcome(
    EntityReference? Equipment,
    string Code,
    IReadOnlyList<WeaponOverrideField> Fields)
{
    /// <summary>A request this action can carry: the field set the applier is asked to write.</summary>
    public static WeaponActionOutcome Ready(EntityReference equipment, IReadOnlyList<WeaponOverrideField> fields)
        => new(equipment, "", fields ?? Array.Empty<WeaponOverrideField>());

    /// <summary>A request this action will not carry, with the code that says why. The code is the detail a plan
    /// reads; a refusal without one would be indistinguishable from a success with no fields.</summary>
    public static WeaponActionOutcome Refuse(EntityReference? equipment, string code)
        => new(equipment, code, Array.Empty<WeaponOverrideField>());
}

/// <summary>
/// What one weapon property profile reads as: the values the game's own archetype block and the recoil block it
/// names hold, in the units those members store them in. It is a reading and not a request — the profile row asks
/// the native half for it and turns it into the ledger's own field vocabulary — so it carries exactly the eight
/// members the card's three groups are made of and nothing else.
/// </summary>
internal sealed record WeaponProfileReading(double ShotDelay, double HipFireSpread, double AimSpread,
    double ShotgunConeSize, double RecoilHorizontal, double RecoilVertical, double RecoilSpring, double RecoilPower);

/// <summary>
/// The three numerical `forge.action.weapon.*` rows this slice owns, as pure functions from a dispatched command
/// to the instance values it asks for: `fire_rate`, `spread` and `recoil`. The port names below are the catalog's
/// own, port for port; a port this build cannot serve is refused by name rather than dropped, so a plan author is
/// told which input the game has no destination for.
///
/// What every one of these rows has in common is ruling 84's answer to "how does a runtime weapon value change
/// at all": this instance gets its own copy of the block it points at, the copy's fields are changed, and the
/// instance is pointed at the copy. Nothing here writes the shared block and nothing here builds a Forge-only
/// override layer that the player's own trigger would not honour.
/// </summary>
internal static class WeaponActionRuntime
{
    /// <summary>
    /// The `cap` port's own rule, shared by every modifier row this package declares. The cap bounds the value the
    /// row contributes — the rate under its operation, a spread multiplier, a reload speed multiplier, or a recoil
    /// value — in the unit that operation works in. The operation's unit element decides which side the cap bounds:
    /// a cap above it is an upper bound, a cap at or below it a lower bound, which is the `TriggerMod.Cap` rule the
    /// evidence records (`Cap > 1` takes the smaller multiplier, otherwise the larger). `add` works in an additive
    /// unit, whose element is 0; `multiply` works in a multiplicative unit, whose element is 1; `set`, `minimum` and
    /// `maximum` contribute a value of the quantity itself, so their element is that quantity's own zero.
    /// </summary>
    public static double ClampToCap(double value, WeaponOverrideOperation operation, double cap)
        => cap > Unit(operation) ? Math.Min(value, cap) : Math.Max(value, cap);

    private static double Unit(WeaponOverrideOperation operation)
        => operation == WeaponOverrideOperation.Multiply ? 1d : 0d;

    /// <summary>The value one row contributes after its own `cap` port is read: a row that set no cap contributes
    /// exactly what it asked for.</summary>
    public static double WithCap(double? cap, WeaponOverrideOperation operation, double value)
        => cap is { } limit ? ClampToCap(value, operation, limit) : value;

    /// <summary>The `operation` parameter of a modifier row as the ledger's own member. An absent parameter is
    /// `set`, the operation every modifier row had before the port existed; an unknown name is a refusal, never a
    /// fallback, so a plan that asked for an operation this build does not have is told by name.</summary>
    public static bool TryOperation(string? name, out WeaponOverrideOperation operation)
    {
        if (name == null) { operation = WeaponOverrideOperation.Set; return true; }
        return WeaponOverrideLedger.TryParseOperation(name, out operation);
    }

    /// <summary>The weapon families that decide which of the field set's destinations exist on one instance.
    /// `shotgun` owns the cone and the per-pellet spread reads; every other bullet family reads the two spread
    /// values but no cone. `burst` owns the burst length field — the other archetype classes have no such field
    /// at all, so a burst length is not something they can hold.</summary>
    public enum WeaponFamily
    {
        Bullet,
        Shotgun,
        Burst
    }

    /// <summary>
    /// Whether one field has a destination on this instance, and which of the two destination groups it belongs
    /// to. This is the check that makes a refusal leave the weapon untouched: the applier asks it for every field
    /// of a request before the first write, so a request that names one value this instance cannot hold changes
    /// nothing at all.
    ///
    /// The three families are the shipped code's own: the cone and the per-pellet spread are read only by the
    /// shotgun fire path, the burst length field exists only on `BWA_Burst`/`BWA_SemiBurst`, and everything else
    /// is a value every bullet archetype reads every shot.
    /// </summary>
    public static bool HasDestination(string field, WeaponFamily family, out bool needsRecoil)
    {
        needsRecoil = false;
        switch (field)
        {
            case "fire_rate":
            case "reload_time":
            case "spread_movement_scale":
            case "spread_aim_scale":
                return true;
            case "spread_cone":
                // A weapon whose fire path never reads the cone would take the write and show nothing.
                return family == WeaponFamily.Shotgun;
            case "burst_count":
                return family == WeaponFamily.Burst;
            case "recoil_horizontal":
            case "recoil_vertical":
            case "recoil_recovery":
            case "recoil_camera_kick":
                needsRecoil = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// `forge.action.weapon.fire_rate`. Ports: `equipment`, `rate`, `duration`, `cap`; structural `operation`.
    ///
    /// `operation` is how this request stacks against the instance's own value: `set` writes the rate, `add` and
    /// `subtract` move it, `multiply` scales it, and `minimum`/`maximum` keep the smaller or the larger. The
    /// value is in shots per second in every case, so an `add` of two means two more shots per second and a
    /// `multiply` of two doubles the rate. `cap` bounds the value this request contributes under that operation.
    /// </summary>
    public static WeaponActionOutcome FireRate(CommandContext context)
    {
        var equipment = Equipment(context, WeaponOverrideContract.FireRateEquipmentSlot);
        if (equipment == null) return WeaponActionOutcome.Refuse(null, ResultCode.StaleEntity);
        if (!TryOperation(context.Text(WeaponOverrideContract.FireRateOperationSlot), out var operation))
            return WeaponActionOutcome.Refuse(equipment, ResultCode.WeaponModeUnsupported);
        if (context.Number(WeaponOverrideContract.FireRateValueSlot) is not { } rate || !double.IsFinite(rate))
            return WeaponActionOutcome.Refuse(equipment, ResultCode.InputUnsupported);
        rate = WithCap(context.Number(WeaponOverrideContract.FireRateCapSlot), operation, rate);
        return WeaponActionOutcome.Ready(equipment, new[]
        {
            new WeaponOverrideField("fire_rate", rate, operation, WeaponOverrideUnit.ShotsPerSecond)
        });
    }

    /// <summary>
    /// `forge.action.weapon.property`: the generic form of the three rows above, and the one every "change a
    /// weapon number for a while" card lands on. Ports: `equipment`, `value`; structural parameters `field` (a
    /// name from <see cref="WeaponOverrideLedger.Fields"/>) and `operation` (`set`, `add`, `subtract`).
    ///
    /// The value is in the unit the field's own name states — the same unit the specific row for that field uses
    /// — and the native half is what converts it to the member the game reads. A field this build has no write
    /// point for is refused with the ledger's own `override-field-unknown`, so an author is told which name is
    /// not served rather than getting a silently ignored write. The effect handle the row declares is the
    /// kernel's: this row answers with the target and the accepted value, the plan's own `effect` block times the
    /// change, and the module's restore callback is what undoes the write when that effect ends.
    /// </summary>
    public static WeaponActionOutcome Property(CommandContext context)
    {
        var equipment = Equipment(context, WeaponOverrideContract.PropertyEquipmentSlot);
        if (equipment == null) return WeaponActionOutcome.Refuse(null, ResultCode.StaleEntity);
        var name = context.Text(WeaponOverrideContract.PropertyFieldSlot);
        if (name == null) return WeaponActionOutcome.Refuse(equipment, ResultCode.WeaponModeUnsupported);
        if (!WeaponOverrideLedger.IsKnownField(name))
            return WeaponActionOutcome.Refuse(equipment, ResultCode.OverrideFieldUnknown);
        // The generic row's own graph offers three operations, so a name outside them is refused here as well as
        // by the plan's own parameter check: the named modifier rows carry the other three, and this row's
        // semantics are the three it declares.
        var operationName = context.Text(WeaponOverrideContract.PropertyOperationSlot);
        if (operationName == null || !WeaponOverrideLedger.Operations.Contains(operationName)
            || !WeaponOverrideLedger.TryParseOperation(operationName, out var operation))
            return WeaponActionOutcome.Refuse(equipment, ResultCode.WeaponModeUnsupported);
        if (context.Number(WeaponOverrideContract.PropertyValueSlot) is not { } value)
            return WeaponActionOutcome.Refuse(equipment, ResultCode.InputUnsupported);
        // Fire-rate properties use the same frequency unit as the named action; their native representation
        // is checked by the applier after resolving the operation, without a sign or balance restriction.
        if (name == "fire_rate" && !double.IsFinite(value))
            return WeaponActionOutcome.Refuse(equipment, ResultCode.InputUnsupported);
        if (name is not ("fire_rate" or "reload_time" or "melee_charge_time") && operation == WeaponOverrideOperation.Set && value < 0)
            return WeaponActionOutcome.Refuse(equipment, ResultCode.InputUnsupported);
        return WeaponActionOutcome.Ready(equipment, new[] { new WeaponOverrideField(name, value, operation,
            name == "fire_rate" ? WeaponOverrideUnit.ShotsPerSecond : WeaponOverrideUnit.Native) });
    }

    /// <summary>
    /// `forge.action.weapon.spread`. Ports: `equipment`, `cone`, `movement_scale`, `aim_scale`,
    /// `spread_multiplier`, `cap`.
    ///
    /// The cone is the shot-spread cone the shotgun path reads per pellet; `movement_scale` and `aim_scale` are
    /// the two spread values a bullet weapon reads per shot, scaled by the inputs. `spread_multiplier` is the
    /// whole-spread multiplier the modifier rows of this family carry: it scales the two values the instance's
    /// own block already holds, which is the one operation that has no absolute value behind it, and it is the
    /// value `cap` bounds. A request that names both the multiplier and an absolute value of the same field
    /// writes the absolute value, because the two spellings of one field cannot both land and the explicit value
    /// is the one the author typed.
    ///
    /// The row has no seed and no pattern: the native cone has no variant and no per-instance randomness to seed.
    /// Every value port is optional, so a request names only what it changes; a `cone` on a weapon that has no
    /// cone is refused by name when it is wired, never dropped.
    /// </summary>
    public static WeaponActionOutcome Spread(CommandContext context)
    {
        var equipment = Equipment(context, WeaponOverrideContract.SpreadEquipmentSlot);
        if (equipment == null) return WeaponActionOutcome.Refuse(null, ResultCode.StaleEntity);

        // One field per name: the multiplier's fields are laid down first, so an absolute value of the same
        // field replaces the multiplier's rather than being merged with it.
        var fields = new List<WeaponOverrideField>(4);
        if (context.Number(WeaponOverrideContract.SpreadMultiplierSlot) is { } multiplier)
        {
            multiplier = WithCap(context.Number(WeaponOverrideContract.SpreadCapSlot),
                WeaponOverrideOperation.Multiply, multiplier);
            if (multiplier < 0) return WeaponActionOutcome.Refuse(equipment, ResultCode.InputUnsupported);
            // A multiplier a wire computed to exactly 1 is a legal "unchanged": it is written like any other, so the
            // result, the effect handle and the restore behave the same at the neutral value.
            fields.Add(new WeaponOverrideField("spread_movement_scale", multiplier, WeaponOverrideOperation.Multiply));
            fields.Add(new WeaponOverrideField("spread_aim_scale", multiplier, WeaponOverrideOperation.Multiply));
        }
        if (context.Number(WeaponOverrideContract.SpreadConeSlot) is { } cone)
        {
            if (cone < 0) return WeaponActionOutcome.Refuse(equipment, ResultCode.InputUnsupported);
            fields.Add(new WeaponOverrideField("spread_cone",
                WithCap(context.Number(WeaponOverrideContract.SpreadCapSlot), WeaponOverrideOperation.Set, cone)));
        }
        if (context.Number(WeaponOverrideContract.SpreadMovementScaleSlot) is { } movement)
            fields.Add(new WeaponOverrideField("spread_movement_scale",
                WithCap(context.Number(WeaponOverrideContract.SpreadCapSlot), WeaponOverrideOperation.Set, movement)));
        if (context.Number(WeaponOverrideContract.SpreadAimScaleSlot) is { } aim)
            fields.Add(new WeaponOverrideField("spread_aim_scale",
                WithCap(context.Number(WeaponOverrideContract.SpreadCapSlot), WeaponOverrideOperation.Set, aim)));
        var distinct = OneFieldPerName(fields);
        return distinct.Count == 0
            ? WeaponActionOutcome.Refuse(equipment, ResultCode.InputUnsupported)
            : WeaponActionOutcome.Ready(equipment, distinct);
    }

    /// <summary>
    /// `forge.action.weapon.recoil`. Ports: `equipment`, `horizontal`, `vertical`, `recovery`, `camera_kick`,
    /// `cap`. The four are the four values the recoil block's own application path reads: the two scale ranges,
    /// the spring stiffness and the impulse power. The row carries no `profile` resource and no construction
    /// parameters — ruling 84 removed both — so a recoil request is always a change to this instance's block, and
    /// the `cap` port bounds each value the request contributes.
    /// </summary>
    public static WeaponActionOutcome Recoil(CommandContext context)
    {
        var equipment = Equipment(context, WeaponOverrideContract.RecoilEquipmentSlot);
        if (equipment == null) return WeaponActionOutcome.Refuse(null, ResultCode.StaleEntity);
        var fields = new List<WeaponOverrideField>(4);
        if (context.Number(WeaponOverrideContract.RecoilHorizontalSlot) is { } horizontal)
            fields.Add(new WeaponOverrideField("recoil_horizontal",
                WithCap(context.Number(WeaponOverrideContract.RecoilCapSlot), WeaponOverrideOperation.Set, horizontal)));
        if (context.Number(WeaponOverrideContract.RecoilVerticalSlot) is { } vertical)
            fields.Add(new WeaponOverrideField("recoil_vertical",
                WithCap(context.Number(WeaponOverrideContract.RecoilCapSlot), WeaponOverrideOperation.Set, vertical)));
        if (context.Number(WeaponOverrideContract.RecoilRecoverySlot) is { } recovery)
            fields.Add(new WeaponOverrideField("recoil_recovery",
                WithCap(context.Number(WeaponOverrideContract.RecoilCapSlot), WeaponOverrideOperation.Set, recovery)));
        if (context.Number(WeaponOverrideContract.RecoilCameraKickSlot) is { } kick)
            fields.Add(new WeaponOverrideField("recoil_camera_kick",
                WithCap(context.Number(WeaponOverrideContract.RecoilCapSlot), WeaponOverrideOperation.Set, kick)));
        return fields.Count == 0
            ? WeaponActionOutcome.Refuse(equipment, ResultCode.InputUnsupported)
            : WeaponActionOutcome.Ready(equipment, fields);
    }

    /// <summary>
    /// The same field named twice collapses to its last spelling, in first-set order. A request that names one
    /// field both as a multiplier and as an absolute value is one write of that field, and the ledger would
    /// otherwise record two entries for one name and let the earlier one win the merge it was already in.
    /// </summary>
    private static IReadOnlyList<WeaponOverrideField> OneFieldPerName(IReadOnlyList<WeaponOverrideField> fields)
    {
        var distinct = new List<WeaponOverrideField>(fields.Count);
        foreach (var field in fields)
        {
            var index = -1;
            for (var i = 0; i < distinct.Count; i++)
                if (string.Equals(distinct[i].Name, field.Name, StringComparison.Ordinal)) { index = i; break; }
            if (index >= 0) distinct[index] = field;
            else distinct.Add(field);
        }
        return distinct;
    }

    /// <summary>
    /// `forge.action.weapon.property_profile`'s own mapping: the field set one profile asks for, in the ledger's
    /// own vocabulary. Ports: `equipment`, `duration`; the structural `profile` and the three optional members
    /// beside it.
    ///
    /// A profile is not a set of operations on the instance's own values: it is another profile's values, read
    /// from the game's own data block and written over this instance's block in one go. The mapping from that
    /// reading to the ledger's field vocabulary lives here, next to the three named rows it shares a destination
    /// with, so a profile can never write a value under a name those rows would not.
    ///
    /// <paramref name="readsCone"/> is the instance's own answer to <see cref="HasDestination"/> for
    /// `spread_cone`: every archetype block carries a cone value, but only a shotgun's fire path samples one, so
    /// a profile put on a weapon that reads no cone contributes no cone field rather than a write nothing reads.
    /// Every other value of the three groups has a destination on every bullet weapon.
    /// </summary>
    public static IReadOnlyList<WeaponOverrideField> Profile(WeaponProfileReading profile, bool readsCone)
    {
        var fields = new List<WeaponOverrideField>(8)
        {
            // The shared name identifies ShotDelay; the profile keeps its native interval and exact sign.
            new("fire_rate", profile.ShotDelay),
            new("spread_movement_scale", profile.HipFireSpread),
            new("spread_aim_scale", profile.AimSpread),
            new("recoil_horizontal", profile.RecoilHorizontal),
            new("recoil_vertical", profile.RecoilVertical),
            new("recoil_recovery", profile.RecoilSpring),
            new("recoil_camera_kick", profile.RecoilPower)
        };
        if (readsCone) fields.Add(new WeaponOverrideField("spread_cone", profile.ShotgunConeSize));
        return fields;
    }

    /// <summary>The `equipment` input every one of these rows addresses. A row whose equipment input is absent is
    /// a plan the loader already rejected; a row whose reference is present but not current is refused here,
    /// because an override on a dead instance would be a write to a block nothing reads.
    ///
    /// The reference is never invented from the holder: the `source` port of `fire_rate` names the writer, not
    /// the weapon, and the weapon is the port this action is attached to.</summary>
    private static EntityReference? Equipment(CommandContext context, HandlerSlot slot)
    {
        return context.TryEntity(slot, out var reference) ? reference : null;
    }
}
