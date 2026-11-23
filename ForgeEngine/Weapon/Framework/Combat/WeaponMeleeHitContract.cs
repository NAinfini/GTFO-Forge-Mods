using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The one row the node list's `e-w-melee` names and this build did not have: a melee swing that <b>landed on a
/// target</b>. The catalog's melee vocabulary asks for the swing itself as an action, and the `hit_candidate` fact
/// this package already declares for a swing's target does not say "this swing hit this thing": it is published
/// before any damage is worked out, so it carries no limb and no damage. The node list's example — a hammer that
/// heals its wielder when it connects — needs the connected hit itself, once per target, with the damage the hit
/// really dealt.
///
/// <b>Where the row is read.</b> Every port here is a read of the swing's own hit entry on the machine that
/// performs the swing, which is the only place all five exist:
/// <list type="bullet">
/// <item>`MeleeWeaponFirstPerson.DoAttackDamage(MeleeWeaponDamageData, bool)` is the first-person per-target hit
/// entry: `MWS_AttackHit.Update` calls it once per entry of `HitsForDamage`, and its `MeleeWeaponDamageData` names
/// the target's damageable and the hit point. The wielder is the weapon's own `Owner`, the damage is the weapon's
/// own `m_damageToDeal` (fixed by `SetNextDamageToDeal` for this hit), and the charge is the weapon's own state.
/// The one other caller in this build is `MWS_Push.Update`, which enters the same body for the shove with `isPush`
/// set; that entry is a push and not a swing hit, so the observation skips it.</item>
/// <item>`MeleeWeaponThirdPerson.DoAttackDamage(GameObject, Vector3, Vector3, float, bool)` is the same hit on a
/// machine that is not the wielder's: the bot melee action calls it per target with the damage as its own
/// argument, and the weapon's `Owner` is the attacker.</item>
/// </list>
///
/// <b>Where a remote player's swing is read.</b> A human client's swing runs the first-person entry on that
/// client's machine only; the swing's broadcast action (`MeleeWeaponFirstPerson.m_hitPacket`) reaches
/// `MeleeWeaponThirdPerson.IncomingMeleeHit`, whose body only plays feedback and spawns decals, and the
/// third-person damage body's only literal callers are the bot melee action's strike state. What the host does
/// receive is the replicated damage itself — `Dam_EnemyDamageBase.ReceiveMeleeDamage(pFullDamageData)`, which
/// carries the attacker, the limb and the gear category but not the charge, and whose damage field is a packed
/// `UFloat16` whose decode scale belongs to the receiver's own body. That path is the row's third source: the
/// damage published from it is the health the receive body really took, read around it, and `charged` stays
/// absent. The two sources publish through one ledger, so one landed hit is published once: a hit whose attacker
/// is this machine's own player or a bot is published by the hit entry that performed it, and the receive half
/// publishes only for a remote player's swing.
///
/// <b>The row is the runtime's trigger contract's own.</b> It is a reviewed Primitive row of the catalog, so
/// `forge.contract.trigger` declares its label, sentence and graph; this file owns the id, the observe binding,
/// the handler name and the permission, and <see cref="Row"/> reads the shipped row back from that contract.
/// `source` is the attacker and `target` the thing that was hit. `equipment` is the exact hit-entry weapon's
/// equipment life; it is null (unavailable) for replicated packets, which carry no equipment-instance identity.
/// The names the combat family already uses are preserved, and
/// `limb` and `charged` are nullable there because both are genuinely absent in cases the game produces: a hit
/// on a damageable that is not a body part has no limb index, and a swing the first-person weapon did not label
/// light or heavy (a shove, or a state this build does not classify) has no charge.
/// </summary>
internal static class WeaponMeleeHitContract
{
    /// <summary>A melee swing connected with a target. One fact per target of one swing.</summary>
    public const string MeleeHitCapability = Blocks.TriggerCombatMeleeHit.Id;

    public const string MeleeHitBinding = ModuleDefinition.ProviderId + ".binding.melee_hit";

    /// <summary>The handler name the binding resolves to. The row is observe-only, so the name is the row's own
    /// id in this provider's namespace, exactly like the other fact rows this package declares.</summary>
    public const string MeleeHitHandler = "gtfo.weapon.melee_hit";

    /// <summary>The permission the row is read under: the same combat read the shot and hit-candidate rows carry,
    /// because the hit is read from the weapon's own body and from the target's damageable, and nothing here
    /// writes.</summary>
    public const string CombatReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The one declared row.</summary>
    public static readonly string CapabilityId = MeleeHitCapability;

    /// <summary>The shipped row, as the runtime's trigger contract carries it: the catalog's own label and
    /// sentence with the reviewed Primitive's own graph. This family's cases hold the ports the two native hit
    /// paths really fill to that shape — the shape the kernel checks a published fact with.</summary>
    public static JsonElement Row() => TriggerContracts.Rows(new[] { MeleeHitCapability })[0];

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(MeleeHitBinding, MeleeHitCapability, MeleeHitHandler, "observe", new[] { CombatReadPermission })
        { EquipmentAttachment = new EquipmentAttachmentPorts("source", "equipment") }
    };
}
