using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The one availability-gate row this package owns: `forge.query.weapon.gate`, the read that answers one
/// declared boolean state of one equipment life. Every member it reads is a property the game itself declares on
/// `ItemEquippable` and overrides on the weapon families below it (`catalog/native/native-runtime.json`, types
/// `ItemEquippable`, `Weapon`, `BulletWeapon`, `MeleeWeaponFirstPerson`), so the read is a virtual call on the
/// item the life currently holds and the answer is the one the game itself would act on.
///
/// The row reads and never writes: the write side of these states is `forge.action.player.action_lock`, which is
/// not this row and not this package's.
///
/// Every member names a boolean property. `ItemEquippable.IsActive()` is not one: this build declares it as
/// `public virtual new String IsActive()` (the ledger row types it `text`), so the row offers no member for it.</summary>
internal static class WeaponGateContract
{
    /// <summary>The row's canonical id; the row itself is the generated block table's.</summary>
    public const string CapabilityId = Blocks.QueryWeaponGate.Id;

    /// <summary>This provider's one binding of that row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.weapon_gate";

    /// <summary>The evaluator name, which is also the shape key: a registration cannot supply a body whose ports
    /// were never declared.</summary>
    public const string HandlerName = "gtfo.weapon.gate";

    /// <summary>The permission the read carries: the same equipment the wield and magazine reads already carry,
    /// so the name is not split per row.</summary>
    public const string ReadPermission = ModuleDefinition.WieldReadPermission;

    /// <summary>The declared `gate` members, in the catalog's own order: the parameter arrives as the index of the
    /// member the author chose, so the order here is the contract and not a convenience.</summary>
    public const int SwitchGate = 0;
    public const int ReloadGate = 1;
    public const int RunGate = 2;
    public const int InteractGate = 3;
    public const int AimGate = 4;
    public static readonly IReadOnlyList<string> Gates = Array.AsReadOnly(new[]
    {
        "switch", "reload", "run", "interact", "aim"
    });

    /// <summary>The ports the evaluator really reads: the equipment life the question is about, the member asked
    /// for, and the one boolean the row answers.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("equipment").Outputs("value").Parameters("gate");

    /// <summary>The evaluator table, taken from the reads that own the body so a shape and its evaluator can never
    /// be registered apart.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => WeaponGateReads.Evaluators();

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, HandlerName, "observe", new[] { ReadPermission }, Shape)
    };
}
