using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The module-side half of the ability-used observation: the fact `forge.trigger.enemy.ability_used`
/// publishes, read from the game's own attack-start datum. The Harmony class that calls into it is in
/// `EnemyNodeHooks.cs`; nothing here writes the world.
///
/// The datum is `Enemies.pES_EnemyAttackData`, the body `ES_EnemyAttackBase` hands to every peer when an enemy
/// begins an attack ability. It carries the ability kind and index, the agent the attack was aimed at and the
/// length the state was told to run for, which is exactly the row's own four payload ports; the enemy the fact is
/// about comes from the state that sent it, because the datum itself names only the target.
///
/// Three readings are refused rather than reported: an enemy this module does not track (a stale pointer or a
/// life that is not the one the reference names), an ability kind outside the table this provider owns as
/// resources, and a target handle whose agent is already gone. In each case nothing is published — a fact that
/// names an entity the kernel cannot resolve would be a different fact, and the capability's own payload check
/// would refuse it anyway.</summary>
internal sealed partial class EnemyModule
{
    private readonly HashSet<byte> _unmappedObservedAbilities = new();
    private long _abilityCoverageEpoch = -1;
    private bool _untrackedAbilityReported;
    /// <summary>The binding this observation publishes through; `EnemyAbilityUsedContract` declares it and spells
    /// the ports below.</summary>
    internal const string AbilityUsedBinding = EnemyAbilityUsedContract.BindingId;

    /// <summary>Native callback entry (the `ES_EnemyAttackBase.RecieveAttackStart` postfix): one attack the game
    /// itself has already decided is happening. The enemy is handed in by the hook rather than read here, because
    /// the state's own agent field is not part of the interop surface this package compiles against.</summary>
    /// <paramref name="attackKind"/> is the `enemy_attack_kind` member of the state that received the datum
    /// (`AttackKindOf`), or -1 for a state that is none of the five, which leaves the optional port absent.
    internal void AfterAbilityUsed(EnemyAgent? enemy, pES_EnemyAttackData attack, int attackKind = -1)
    {
        CheckThread();
        if (enemy == null || !CanPublishNodeFacts(_abilityUsedGate)) return;
        if (_abilityCoverageEpoch != _kernel.WorldEpoch)
        {
            _abilityCoverageEpoch = _kernel.WorldEpoch;
            _unmappedObservedAbilities.Clear();
            _untrackedAbilityReported = false;
        }
        // The reference must be the one this module minted for this life: a pointer that changed under the state,
        // or a reference that no longer resolves, is not a fact about a live enemy.
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer || Resolve(entry.Reference) == null)
        {
            if (!_untrackedAbilityReported)
            {
                _untrackedAbilityReported = true;
                Log.Write(RuntimeLogLevel.Info, "enemy.ability-observation-unresolved", new RuntimeLogFacts
                { Binding = AbilityUsedBinding, Detail = "The native attack's enemy life is not current; no ability fact was published." });
            }
            return;
        }
        // The kind is a resource this provider owns, or the fact is not one this provider can name.
        if (!EnemyAbilityResources.TryReference((byte)attack.AbilityType, attack.AbilityIndex, out var ability))
        {
            if (_unmappedObservedAbilities.Add((byte)attack.AbilityType))
                Log.Write(RuntimeLogLevel.Info, "enemy.ability-observation-unmapped", new RuntimeLogFacts
                {
                    Binding = AbilityUsedBinding,
                    Values = new[] { RuntimeLogValue.Of("abilityType", (long)(byte)attack.AbilityType) },
                    Detail = "The observed native ability has no reviewed Forge resource; no substitute ability fact was published."
                });
            return;
        }
        // An attack aimed at a position instead of an agent, and a target handle whose agent was torn down, both
        // leave the port absent rather than naming something that is not there — the port is optional, so the
        // framework reads its absence as "no target" instead of refusing the fact.
        EntityReference? resolved = null;
        try
        {
            if (attack.TargetAgent.TryGet(out Agent? target) && target != null) resolved = ResolveAgent(target);
        }
        catch (Exception error) { NativeFault.Note("enemy.ability-used.target-read", error); }
        if (!TryBeginNodeFact(_abilityUsedGate, entry.Reference, out var fact)) return;
        var values = fact.Values;
        values.Entity(Blocks.TriggerEnemyAbilityUsed.OutEnemy, entry.Reference);
        values.Resource(Blocks.TriggerEnemyAbilityUsed.OutAbility, ability);
        if (resolved != null) values.Entity(Blocks.TriggerEnemyAbilityUsed.OutTarget, resolved);
        // The length is published in the clock's own unit, converted from the datum's seconds; the port is
        // optional, so a datum whose own length is not a finite number publishes none at all rather than a zero
        // no plan could tell from a real length.
        if (float.IsFinite(attack.Duration)) values.Integer(Blocks.TriggerEnemyAbilityUsed.OutDuration, Ticks(attack.Duration));
        // The datum's own two points (contract §4.5 B3): where the attack is aimed and where it starts. A point that
        // does not read as finite is an absent port.
        var aimed = attack.TargetPosition;
        if (IsFinitePoint(aimed)) values.Vector(Blocks.TriggerEnemyAbilityUsed.OutTargetPosition, aimed.x, aimed.y, aimed.z);
        var origin = attack.Position;
        if (IsFinitePoint(origin)) values.Vector(Blocks.TriggerEnemyAbilityUsed.OutPosition, origin.x, origin.y, origin.z);
        if (attackKind >= 0) values.Enum(Blocks.TriggerEnemyAbilityUsed.OutAttackKind, attackKind);
        RaiseNodeFact(fact);
    }
}
