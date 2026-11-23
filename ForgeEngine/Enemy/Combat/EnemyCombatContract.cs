using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The game-independent half of the `C-enemy-combat` slice: the binding rows and handler shapes this
/// provider's execute handlers are registered with. The integration step picks them up from here, so the registry
/// spelling lives in one place instead of being repeated inside `EnemyModule`; the capability rows are the
/// generated block table's (<see cref="Blocks"/>).
///
/// Two rows were surveyed against build 20403457 and both are bound here and registered by the integration
/// patch:
///   * `forge.action.combat.stagger` writes through the game's one hitreact entry,
///     `ES_HitreactBase.CanHitreact` then `ActivateState`, and carries the `reaction` strength as a structural
///     parameter because that entry point cannot omit it.
///   * `forge.action.combat.attack_interrupt` names the enemies whose in-flight attack is to be interrupted —
///     `enemies:entity` of kind `gtfo.enemy`, `many` — and proves the attack through
///     `ES_EnemyAttackBase.IsPerformingAttack`/`IsChargingAttack` before writing the same hitreact state machine.
///     It consumes no handle: no provider in this tree casts an attack-instance transaction handle, so a handle
///     input would be a row no plan could ever reach. An enemy that is not mid-attack is reported with the
///     controlled code `enemy-not-attacking` and a `none` commit state.
///
/// The native evidence behind every claim here is `ForgeEnemy/evidence/enemy-combat-actions.json`.</summary>
internal static class EnemyCombatContract
{
    /// <summary>The provider every row here belongs to; the same string `EnemyModule.ProviderId` carries.</summary>
    public const string ProviderId = "forge.module.gtfo.enemy";

    /// <summary>`forge.action.combat.stagger`: put a target that has a hitreact entry into a hitreact.</summary>
    public const string StaggerBinding = ProviderId + ".binding.stagger";
    public const string StaggerHandler = "gtfo.enemy.stagger";

    /// <summary>`forge.action.combat.attack_interrupt`: interrupt the attack the named enemies are performing or
    /// charging.</summary>
    public const string AttackInterruptBinding = ProviderId + ".binding.attack_interrupt";
    public const string AttackInterruptHandler = "gtfo.enemy.attack_interrupt";

    /// <summary>The two execute handlers this contract registers.</summary>
    public static readonly string[] Handlers = { StaggerHandler, AttackInterruptHandler };

    /// <summary>The two bindings this contract registers. Every id here is a binding id; a capability id is a
    /// separate namespace in the kernel registry and may never be reused as one.</summary>
    public static readonly string[] Bindings = { StaggerBinding, AttackInterruptBinding };

    /// <summary>The stagger handler's own ports, resolved once at registration against the block's own row.
    /// `targets` is the recipient collection, whose whole declared width separates `source` from the rest; both
    /// inputs are read only to prove the roles the kernel validated are the ones the row carries.</summary>
    public static readonly HandlerShape StaggerPorts = new HandlerShape()
        .Inputs("targets", "source").Outputs("result").Parameters("reaction", "immunity_policy");
    public static readonly HandlerSlot StaggerTargetsSlot = StaggerPorts.InputSlot("targets");
    public static readonly HandlerSlot StaggerSourceSlot = StaggerPorts.InputSlot("source");
    public static readonly HandlerSlot StaggerReactionSlot = StaggerPorts.ParameterSlot("reaction");
    public static readonly HandlerSlot StaggerImmunitySlot = StaggerPorts.ParameterSlot("immunity_policy");

    /// <summary>The attack-interrupt handler's own ports: `enemies` is the recipient collection and `source` the
    /// only other role. The row declares no structural parameter, so the shape declares none either — a handler
    /// shape and its capability graph are the two halves of one contract and neither may name a port the other
    /// does not carry.</summary>
    public static readonly HandlerShape AttackInterruptPorts = new HandlerShape()
        .Inputs("enemies", "source").Outputs("result");
    public static readonly HandlerSlot AttackInterruptEnemiesSlot = AttackInterruptPorts.InputSlot("enemies");
    public static readonly HandlerSlot AttackInterruptSourceSlot = AttackInterruptPorts.InputSlot("source");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(StaggerBinding, Blocks.ActionCombatStagger.Id, StaggerHandler, "execute", new[] { "gtfo.enemy.motion.write" }, StaggerPorts),
        new ProviderBinding(AttackInterruptBinding, Blocks.ActionCombatAttackInterrupt.Id, AttackInterruptHandler, "execute", new[] { "gtfo.enemy.motion.write" }, AttackInterruptPorts)
    };
}
