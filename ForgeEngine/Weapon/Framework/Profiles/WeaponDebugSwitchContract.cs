using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The debug-switch row this package owns: `forge.action.weapon.debug_switch`, the write that turns one of
/// the game's three weapon debug switches on or off.
///
/// The three switches are `Weapon.SuperWeapons`, `Weapon.InfiniteAmmo` and `Weapon.RecoilOn`, which this build
/// declares as public static boolean properties on the one `Weapon` class (`catalog/native/native-runtime.json`, type
/// `Weapon`). They are static, so one write is for the whole process and not for a weapon: the row's `subject` is
/// the session the write was asked for and never a filter, which is why one command performs exactly one native
/// write and reports the same outcome under each named subject. Nothing here reads or writes a weapon instance.
///
/// The body lives in <see cref="WeaponDebugSwitchWrites"/> so this declaration travels into every process that
/// registers this provider, and the native half is what supplies the source that really writes the statics. A
/// process with no native half refuses the row by code rather than declaring a write nothing performs.</summary>
internal static class WeaponDebugSwitchContract
{
    /// <summary>The row's canonical id; the row itself is the generated block table's.</summary>
    public const string CapabilityId = Blocks.ActionWeaponDebugSwitch.Id;

    /// <summary>This provider's one binding of that row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.debug_switch";

    /// <summary>The handler name the binding resolves to, which is also the shape key.</summary>
    public const string HandlerName = "gtfo.weapon.debug_switch";

    /// <summary>The write this row performs: the game's own process-wide weapon debug statics. It is not one of
    /// the equipment read permissions this package already carries, because nothing here reads an instance.</summary>
    public const string WritePermission = "gtfo.weapon.debug.write";

    /// <summary>The declared `switch` members, in the catalog's own order: the parameter arrives as the index of
    /// the member the author chose, so the order here is the contract and not a convenience.</summary>
    public const int SuperWeapons = 0;
    public const int InfiniteAmmo = 1;
    public const int Recoil = 2;
    public static readonly IReadOnlyList<string> Switches = Array.AsReadOnly(new[]
    {
        "super_weapons", "infinite_ammo", "recoil"
    });

    /// <summary>The ports the body reads: the subjects the write is recorded for, the state asked for, the one
    /// structural member, and the result rows it answers with.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("subject", "enabled").Outputs("result").Parameters("switch");
    public static readonly HandlerSlot SubjectSlot = Shape.InputSlot("subject");
    public static readonly HandlerSlot EnabledSlot = Shape.InputSlot("enabled");
    public static readonly HandlerSlot SwitchSlot = Shape.ParameterSlot("switch");

    /// <summary>The body table, taken from the writes that own it so a shape and its handler can never be
    /// registered apart.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers() => WeaponDebugSwitchWrites.Handlers();

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, HandlerName, "execute", new[] { WritePermission }, Shape)
    };
}
