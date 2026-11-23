using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The ability-used row this package observes: `forge.trigger.enemy.ability_used`, published from the
/// game's own attack-start datum (`Enemies.pES_EnemyAttackData`) the moment an enemy begins an attack ability.
///
/// That stanza is the only native body in this build that carries "which ability, at whom, for how long" as one
/// value: `AbilityType` and `AbilityIndex` name the ability the enemy's own component table holds, `TargetAgent`
/// is the handle of the agent it was aimed at, and `Duration` is the length the attack state was told to run for.
/// It is the body `ES_EnemyAttackBase` sends to every peer, so the fact is read at the one point where the game
/// itself has decided the attack is happening, and the watching half (`EnemyNodeHooks`) reads it back on the
/// machine whose module owns the enemy rather than at the send site.
///
/// Only the ability kinds this provider already owns as resources are published: `ability` is the same
/// `forge.resource.ability` reference `forge.action.enemy.ability` resolves against, minted from the native enum
/// by `EnemyAbilityResources`, so a plan that triggers an ability and a plan that watches for one name the same
/// id. A kind outside that table is not a resource this provider owns and publishes nothing.
///
/// The row carries no parameters. Filtering by ability kind is the graph's own job: the payload names the
/// ability, and a plan restricts itself with a condition on that port, which is how every other observe row in
/// this package narrows what it reacts to. A row-level filter parameter would be inert here — an `observe`
/// binding has no handler shape and the publisher's only subscription query is "does any plan want this binding".
///
/// The catalog ports are `next / enemy / ability / target / duration / target_position / position / attack_kind`: `enemy` is the enemy whose ability
/// started, `ability` the `forge.resource.ability` reference of the kind that started, `target` the agent it was
/// aimed at — optional, because an attack aimed at a position names no agent — and `duration` the attack's own
/// length in plan ticks. The datum carries seconds and the port carries the clock's own unit, so the length is
/// converted once, at the one place it is read; a datum whose own length is not a finite number publishes no
/// `duration` at all, which is why that port is optional too: a zero would be a length no plan could tell from
/// a real one. `target_position` and `position` are the datum's own aim point and origin, each absent when it
/// does not read as finite. `attack_kind` is the `enemy_attack_kind` member of the attack state that received the
/// datum (shooter, flying shooter, striker, tank, multi-target tank), absent for any other state.</summary>
internal static class EnemyAbilityUsedContract
{
    /// <summary>The one provider binding of this row, in the provider's own namespace.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.ability_used";

    /// <summary>The native handler name the binding resolves through. An `observe` trigger binding needs no
    /// handler shape: an event payload arrives as a whole frame.</summary>
    public const string HandlerName = "gtfo.enemy.ability_used";

    /// <summary>The read the published fact needs: the enemy instance this provider already tracks, the ability
    /// component table it carries, and the attack state's own datum.</summary>
    public const string ReadPermission = "gtfo.enemy.ability.read";

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.TriggerEnemyAbilityUsed.Id, HandlerName, "observe", new[] { ReadPermission })
    };
}
