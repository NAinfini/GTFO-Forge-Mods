using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The one flight read this package owns: `forge.query.projectile.state`, the read of where one flight that
/// `forge.action.combat.bullet_projectile` launched is right now, which way it flies and whether it still exists.
///
/// The answer comes from <see cref="ProjectileStates"/>, the table the host's own flight stepping writes into; the
/// row reads and never writes. A flight that ended answers `alive = false` with no position and no direction — never
/// the last point it had. A handle this table cannot answer for (another world, a slot given back, a handle of
/// another kind) is refused by code. Flights exist on the host only, so a process whose table is not attached
/// refuses the read by name instead of answering.</summary>
internal static class ProjectileStateContract
{
    /// <summary>The row's canonical id, declared in the reviewed Behavior Operator set.</summary>
    public const string CapabilityId = Blocks.QueryProjectileState.Id;

    /// <summary>This provider's one binding of that row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.query.projectile.state";

    /// <summary>The evaluator name, which is also the shape key.</summary>
    public const string HandlerName = "gtfo.weapon.projectile_state";

    /// <summary>The same combat permission the launch row carries: the read is about that row's own flight.</summary>
    public const string ReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The handle's contract as the launch row's output and this row's input both declare it.</summary>
    public const string HandleKind = "projectile";
    public const string HandleLifetime = "encounter";

    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("projectile").Outputs("position", "direction", "alive");
    public static readonly HandlerSlot ProjectileSlot = Shape.InputSlot("projectile");

    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [HandlerName] = ProjectileStates.StateRow
    };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, CapabilityId, HandlerName, "observe", new[] { ReadPermission }, Shape)
    };
}
