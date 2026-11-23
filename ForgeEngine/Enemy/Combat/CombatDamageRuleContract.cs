using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The damage-interception row this provider owns: `forge.action.combat.damage_rule`, which changes the
/// damage a named enemy is about to be committed — refusing it outright, holding it above a health floor, or
/// scaling it down — and reports the health it kept off.
///
/// <b>Why it is a standing rule and not a one-shot write.</b> The card is about damage that has not happened yet.
/// A plan step cannot sit inside the native damage call: a Forge fact is queued and answered on the kernel's own
/// turn, so by the time a plan could react to a hit, that hit has already been committed. The one place a hit can
/// still be changed is the native receive body itself, `Dam_EnemyDamageBase.ProcessReceivedDamage`, which this
/// package already hooks for the damage window. The action therefore writes a rule onto the enemies it names and
/// that hook applies it to every hit those enemies receive from then on, which is exactly what "the damage about
/// to be committed" means from a plan's point of view.
///
/// A rule stands until the plan's own `effect` block ends it — the kernel owns that clock and calls this provider
/// back, the way the effect-volume row's does — until a later application replaces it, or until the world ends.
/// The card declares no `duration` port for that reason: a duration the kernel hands out is not a port a handler
/// keeps a clock for.
///
/// <b>The three policies.</b> `immune` refuses the whole hit. `floor` keeps the receiver's health from falling
/// below the `floor` input, so a hit is trimmed to what is left above it and a receiver already at or below the
/// floor takes nothing. `scale` multiplies the hit by `factor`, which the card's own meaning ("按比例减免") bounds
/// to `[0, 1]`: a factor outside that range would raise damage rather than reduce it, and is refused by name
/// rather than silently clamped.
///
/// <b>What `rejected` reports.</b> The port is the health the rule kept off its target, in hp. At the moment a
/// rule is written nothing has been blocked yet, so what a row reports is what the rule this application replaced
/// on that same enemy had blocked while it stood; a target that carried no rule reports zero. The scalar
/// `rejected` output is the sum of the rows. Nothing is invented here: the number is the total of the differences
/// the hook really applied.
///
/// <b>Recipients.</b> The card's `targets` port names players and enemies. Enemies are ruled here; a player is
/// handed to the Player domain's incoming-damage rules. Any other kind is refused by name with
/// `unsupported-recipient` and an enemy life this provider no longer tracks with `stale-entity`, rather than
/// accepted and never applied.</summary>
internal static class CombatDamageRuleContract
{
    public const string ProviderId = ModuleDefinition.ProviderId;
    public const string BindingId = ProviderId + ".binding.damage_rule";
    public const string HandlerName = "gtfo.enemy.damage_rule";

    /// <summary>The permission the write carries. The rule decides how much health a hit takes, so it is a health
    /// write, and it reads the receiver's own health to apply a floor — the same pair the damage action declares.
    /// </summary>
    public static readonly string[] Permissions = { "gtfo.enemy.health.read", "gtfo.enemy.health.write" };
    public static readonly IReadOnlyList<string> Policies = Array.AsReadOnly(new[] { "immune", "floor", "scale", "clear" });

    /// <summary>The ports the handler reads: the recipient collection and the two numbers the policies need, the
    /// row collection and the scalar it answers with, and the one structural parameter that picks the policy.
    /// </summary>
    public static readonly HandlerShape Ports = IncomingDamagePolicy.Shape;
    public static readonly HandlerSlot TargetsSlot = Ports.InputSlot("targets");

    public static HandlerShape Shape() => Ports;

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionCombatDamageRule.Id, HandlerName, "execute", new[] { "gtfo.enemy.health.read", "gtfo.enemy.health.write" }, Ports)
    };
}
