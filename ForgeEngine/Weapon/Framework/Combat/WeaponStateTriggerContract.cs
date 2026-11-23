using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The two weapon-state rows this package owns: a charge that changes phase, and the entry/exit of aiming down
/// sights. Both are read on the machine that holds the weapon — the only machine whose copy of the weapon really
/// changes state — and both publish a phase port instead of filtering, because a trigger row's own parameters are
/// authoring metadata the kernel never compares against a fact (the runtime matches a fact to an entry point by
/// binding, mount targets and its own `gate` block only). The phase is therefore carried as a value the plan
/// filters with a condition, and never as a parameter that would look like a filter and be inert. The phase ports
/// are enums of the shared `charge_phase` and `aim_phase` sets in `Wire.EnumSets`; the values
/// below spell those members exactly.
/// </summary>
internal static class WeaponStateTriggerContract
{
    /// <summary>A weapon's charge changed phase: it started charging, it is charging, it stopped charging, or the
    /// charged attack was really swung.</summary>
    public const string ChargeStateCapability = Blocks.TriggerWeaponChargeState.Id;
    /// <summary>A wielder entered or left aiming down sights.</summary>
    public const string AimStateCapability = Blocks.TriggerCombatAimState.Id;

    public const string ChargeStateBinding = ModuleDefinition.ProviderId + ".binding.charge_state";
    public const string AimStateBinding = ModuleDefinition.ProviderId + ".binding.aim_state";

    /// <summary>The handler names the bindings resolve to. Both rows are observe-only, so the name is the row's own
    /// id in this provider's namespace, exactly like every other fact row this package declares.</summary>
    public const string ChargeStateHandler = "gtfo.weapon.charge_state";
    public const string AimStateHandler = "gtfo.weapon.aim_state";

    /// <summary>The four charge phases by name, so a body that publishes one and a plan that compares against it
    /// spell the value once.</summary>
    public const string ChargeStart = "start";
    public const string ChargeProgress = "progress";
    public const string ChargeEnd = "end";
    public const string ChargeSwing = "swing";

    /// <summary>The two aim phases by name.</summary>
    public const string AimEnter = "enter";
    public const string AimExit = "exit";

    /// <summary>The permission both rows are read under: they read the weapon's own state and its owner, and
    /// nothing here writes.</summary>
    public const string ReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(ChargeStateBinding, ChargeStateCapability, ChargeStateHandler, "observe", new[] { ReadPermission }),
        new ProviderBinding(AimStateBinding, AimStateCapability, AimStateHandler, "observe", new[] { ReadPermission })
    };
}
