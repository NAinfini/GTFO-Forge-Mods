using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The staggered row this package observes: `forge.trigger.combat.staggered`, published from the
/// closing half of the one damage window every hit passes through, and only when the receiver's own answer says
/// the hit landed *and* the enemy's own hitreact state machine still stands on the stagger-class reaction the
/// hit asked for. A receiver that applied the damage but left no such reaction behind — an immune or throttled
/// reaction — is not a stagger, and neither is a hit the receiver refused.
///
/// Like <see cref="AttackInstanceContract"/> this row is declared by the framework contract module:
/// `TriggerContracts` carries `forge.trigger.combat.staggered`, so this provider binds that id and registers no
/// shape of its own. The catalog ports are
/// `equipment? / next / target / source? / hit_type`: `target` is the enemy whose own reaction state shows the stagger, and `source` is
/// the inflictor when the receiver still names one this package can resolve — an explosion packet carries no
/// source, and that is the optional member's whole reason. `equipment` is present only when the current damage
/// call attributes an equipment life to that source; it is never guessed from the player's current slot.
///
/// The reaction classes are the three `forge.action.combat.stagger` may submit (`Micro`, `Light`, `Heavy`).
/// `None` and `Unspecified` are no reaction at all, and `ToDeath` and `InstantRagdollDeath` are kills, which
/// belong to `forge.trigger.combat.killed` instead.</summary>
internal static class EnemyStaggerContract
{
    /// <summary>The one provider binding of this row. A binding id lives in the provider's own namespace and
    /// may never repeat its capability id, which the kernel reads as a duplicate row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.staggered";

    /// <summary>The native handler name the binding resolves through. An `observe` trigger binding needs no
    /// handler shape: an event payload arrives as a whole frame.</summary>
    public const string HandlerName = "gtfo.enemy.staggered";

    /// <summary>The read the published fact needs: the receiver the damage landed on and the reaction state of
    /// the enemy that owns it, both read off an enemy this provider already tracks.</summary>
    public const string ReadPermission = "gtfo.enemy.health.read";

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.TriggerCombatStaggered.Id, HandlerName, "observe", new[] { ReadPermission })
    };
}
