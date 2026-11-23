using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// Three equipment facts this package owns and publishes, each from the one native body that really performs the
/// thing it describes. The rows are the generated block table's; this file carries their bindings.
///
/// <b>Flashlight.</b> `ItemEquippable.SetAttachedFlashlightEnabled(bool mode)` is the game's own write of the
/// light attached to an item, and it carries both ports the row needs: the equipment it is called on and the
/// state it is called with. The actor is that item's own owner. Only a change is published — the state the item
/// was already in is not a toggle — so a wield that re-applies the state the light already had publishes
/// nothing.
///
/// <b>Throw released.</b> `ItemEquippable.Throw(Vector3 velocity)` is the only native entry of the throw that
/// carries a velocity, and the velocity it carries is the row's own port. `ThrowingWeapon.Throw(float)` is the
/// charge-release body named by the card's own text; it is private and carries a strength rather than a vector,
/// and whether it reaches this entry cannot be read from either truth source, so the row is published from the
/// entry that really carries the declared port. The card promises the release and not the landing, and nothing
/// here observes a landing.
///
/// <b>Melee push.</b> The push is the melee weapon's own state object, `Gear.MWS_Push`: `Enter()` is the push
/// being prepared, the private `SendAction()` is the push really being pushed out, and the per-target hit entry
/// `MeleeWeaponFirstPerson.DoAttackDamage(data, isPush: true)` is the push reaching a target — the exact branch
/// <see cref="WeaponMeleeHitContract"/>'s own row skips, so one hit is never two facts. The weapon behind a state
/// object is its base's `m_weapon` (`Gear.MWS_Base`), which is where the actor and the equipment come from.
///
/// <b>Why `phase` is a string.</b> A closed vocabulary would be an enum port with a shared set, and the shared
/// sets live in the runtime's `Wire.EnumSets` — a table this package may not extend. The
/// phase is therefore carried as a string spelled the way that set would spell it, exactly as the charge and aim
/// rows of <see cref="WeaponStateTriggerContract"/> carry theirs.
/// </summary>
internal static class EquipmentEventContract
{
    /// <summary>A player turned the flashlight attached to an item on or off.</summary>
    public const string FlashlightCapability = Blocks.TriggerEquipmentFlashlight.Id;
    /// <summary>A held throwable left the hand, with the velocity it left at.</summary>
    public const string ThrowReleasedCapability = Blocks.TriggerEquipmentThrowReleased.Id;
    /// <summary>A melee push was prepared, pushed out, or reached a target.</summary>
    public const string MeleePushCapability = Blocks.TriggerCombatMeleePush.Id;

    public const string FlashlightBinding = ModuleDefinition.ProviderId + ".binding.flashlight";
    public const string ThrowReleasedBinding = ModuleDefinition.ProviderId + ".binding.throw_released";
    public const string MeleePushBinding = ModuleDefinition.ProviderId + ".binding.melee_push";

    /// <summary>The handler names the bindings resolve to. All three rows are observe-only, so the name is the
    /// row's own id in this provider's namespace and no shape travels with it: an event payload arrives as a
    /// whole frame.</summary>
    public const string FlashlightHandler = "gtfo.equipment.flashlight";
    public const string ThrowReleasedHandler = "gtfo.equipment.throw_released";
    public const string MeleePushHandler = "gtfo.weapon.melee_push";

    /// <summary>The three push phases, as their positions in the `melee_push_phase` set: the push is being prepared,
    /// the push was pushed out, and the push reached a target. A client reports the same number.</summary>
    public const int PushPrepare = 0;
    public const int PushRelease = 1;
    public const int PushHit = 2;

    /// <summary>The flashlight and throw rows read the item a player holds and its owner, which is the wield read
    /// this package already carries.</summary>
    public const string WieldReadPermission = ModuleDefinition.WieldReadPermission;
    /// <summary>The push row reads a weapon's own attack state and what its push reached, which is the combat
    /// read this package's other swing rows carry.</summary>
    public const string CombatReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(FlashlightBinding, FlashlightCapability, FlashlightHandler, "observe", new[] { WieldReadPermission }),
        new ProviderBinding(ThrowReleasedBinding, ThrowReleasedCapability, ThrowReleasedHandler, "observe", new[] { WieldReadPermission }),
        new ProviderBinding(MeleePushBinding, MeleePushCapability, MeleePushHandler, "observe", new[] { CombatReadPermission })
    };
}
