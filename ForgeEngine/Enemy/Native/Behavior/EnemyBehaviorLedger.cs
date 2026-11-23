using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>One in-flight ability, as the ledger holds it: the enemy life, the ability kind and the native
/// component the trigger was submitted to.</summary>
internal sealed record RunningBehavior(string EnemyKey, byte Ability, int Index, IntPtr Component, long WorldEpoch);

/// <summary>The provider-side table of in-flight enemy behaviours: what an enemy's own ability machine is running,
/// keyed by the enemy life it belongs to. One row per enemy at most, because the game itself keeps one
/// `EnemyAbilities.m_activeAbility` per agent — a second entry for the same life would be a second answer to a
/// question the native machine answers once.
///
/// The table exists because an in-flight ability is the only enemy behaviour this provider can both observe and
/// end: this provider submitted the trigger itself, so it knows which ability is running, and the row is what
/// tells a later end apart from a normal one. No native member says how an ability ended — the game's own
/// `EnemyAbility.AbilityIsDone()` is its whole answer, and the hitreact state machine that interrupts an ability
/// (`ES_HitreactBase.ActivateState`, the entry `forge.action.combat.stagger` and the combat family's
/// `attack_interrupt` submit) leaves the same answer behind as one that completed. A row still held while the
/// component reports itself unfinished was therefore ended by something other than the ability's own lifetime,
/// which is the interruption `forge.trigger.enemy.ability_interrupted` carries; the module publishes it, and a
/// row the machine reports as finished publishes nothing.
///
/// An entry is never timed. Nothing here counts ticks or expires entries, because no native member gives an
/// ability a duration: a row ends when a later trigger replaces it, when the enemy despawns, or when the world
/// ends. An expiry the provider invented would be a claim the native machine does not make, so the table has none
/// and this slice does not use the kernel's per-tick module hook.
///
/// The world epoch is part of the key rather than a field to compare: a world change clears the table, and a key
/// that names an older epoch cannot name a row here at all.</summary>
internal sealed class EnemyBehaviorLedger
{
    private readonly Dictionary<(long WorldEpoch, string Key), RunningBehavior> _entries = new();

    /// <summary>The ability one enemy life is currently running, or null.</summary>
    internal RunningBehavior? Find(string enemyKey, long worldEpoch)
        => _entries.TryGetValue(Key(enemyKey, worldEpoch), out var entry) ? entry : null;

    /// <summary>Replaces the row for one enemy life. The native machine holds one active ability per agent, so
    /// the newest submitted ability is the one that is running.</summary>
    internal void Start(RunningBehavior entry) => _entries[Key(entry.EnemyKey, entry.WorldEpoch)] = entry;

    /// <summary>Drops one enemy life's row and answers whether one was there. What the end was is the caller's to
    /// report: the decision half tells the module through <see cref="EnemyModule.NativeBehaviorPorts.AbilityDropped"/>, and the
    /// despawn path reads its own row back before it drops it.</summary>
    internal bool End(string enemyKey, long worldEpoch) => _entries.Remove(Key(enemyKey, worldEpoch));

    internal void Clear() => _entries.Clear();

    internal int Count => _entries.Count;

    private static (long WorldEpoch, string Key) Key(string enemyKey, long worldEpoch)
    {
        if (enemyKey == null) throw new ArgumentException("An enemy key is required.", nameof(enemyKey));
        return (worldEpoch, enemyKey);
    }
}

/// <summary>The `ability` resource kind this provider owns: an author names one of the game's own
/// `Agents.AgentAbility` members and its zero-based position within that kind's enabled component list.
/// The shared definition is consumed by the website too; a bare historical kind cannot select a component.
///
/// `AgentAbility.None` (0) is not a resource: it is the machine's own "nothing is active" value, not an ability
/// an author can ask for, and the table below never answers it.</summary>
internal static class EnemyAbilityResources
{
    internal const string Kind = "ability";

    /// <summary>A component-position reference. Its syntax is resolved at plan load; its presence is checked
    /// against each exact target before submission, because different enemy types have different lists.</summary>
    internal static ResourceRef? Resolve(string id)
    {
        return EnemyAbilityIdentity.TryParse(id, out _, out _) ? new ResourceRef(Kind, id) : null;
    }

    /// <summary>The native member behind one resolved reference. A reference of another kind, or an id this
    /// provider does not own, is refused by the caller instead of being guessed at.</summary>
    internal static bool TryAbility(ResourceRef reference, out byte ability, out int index)
    {
        ability = 0; index = -1;
        if (reference == null || reference.ResourceKind != Kind) return false;
        return EnemyAbilityIdentity.TryParse(reference.ResourceId, out ability, out index);
    }

    /// <summary>The other direction, for a fact that carries the ability it is about: the reference one native
    /// member belongs to. A byte outside the table is not a resource this provider owns, and the caller publishes
    /// nothing rather than inventing an id for it.</summary>
    internal static bool TryReference(byte ability, int index, out ResourceRef reference)
    {
        if (EnemyAbilityIdentity.TryId(ability, index, out var id))
        { reference = new ResourceRef(Kind, id); return true; }
        reference = null!;
        return false;
    }
}
