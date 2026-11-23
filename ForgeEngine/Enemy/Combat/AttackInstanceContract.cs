using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The damage-transaction row this package observes: the kill a committed damage transaction settles
/// (`forge.trigger.combat.killed`). It is a `forge.trigger.*` row, so its shape belongs to the framework contract
/// module and is declared by `ForgeEngine/Framework/Policy/TriggerContracts.cs`; this file keeps the id and the binding
/// half, and carries no row text of its own. The body part a hit went into is the optional `limb` port of
/// `forge.trigger.combat.damage_applied`, published by the health window of the same call.
///
/// The catalog ports include `equipment?`: only the exact native attack's equipment life, or null when unavailable.
/// The attacker in `source` is never substituted for it. `killed` is
/// not the enemy's death: a death flow that never settles as a kill is `forge.trigger.enemy.death_started`, and the
/// two rows are published from different native moments precisely so a plan can tell them apart.</summary>
internal static class AttackInstanceContract
{
    /// <summary>The provider binding. A binding id lives in the provider's own namespace and may never repeat its
    /// capability id, which the kernel reads as a duplicate row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.killed";

    /// <summary>The native handler name the binding resolves through. An `observe` trigger binding needs no
    /// handler shape: an event payload arrives as a whole frame.</summary>
    public const string HandlerName = "gtfo.enemy.killed";

    /// <summary>The binding only reads the receiver the damage already landed on, so the permission is the
    /// health read every other observed combat fact in this package declares.</summary>
    public const string ReadPermission = "gtfo.enemy.health.read";

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.TriggerCombatKilled.Id, HandlerName, "observe", new[] { ReadPermission })
    };
}
