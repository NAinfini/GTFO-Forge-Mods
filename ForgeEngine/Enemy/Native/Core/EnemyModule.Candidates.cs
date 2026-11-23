using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The `gtfo.enemy` candidate source: every living enemy life this module currently tracks and can still
/// resolve, and nothing else (a corpse awaiting despawn is not listed). The kernel never discovers entities, so this table is the one answer to "which
/// enemies exist" — it is registered on the same module that already owns the `gtfo.enemy` resolver, which is
/// the only ownership the kernel accepts for a candidate source, and the selector binding reads it through the
/// step's query session rather than through a second way to the module.
///
/// Only membership ordering is cached. Every read still applies the same `Resolve` checks, in the entity
/// table's original order; native instance validity and world identity are never cached. Spawn, replacement,
/// despawn and world reset invalidate the immutable roster without changing a previously returned list.
/// The order is `id` ordinal, exactly as the Map
/// provider's player source orders its lives, so two reads of an unchanged world answer identically.
///
/// The source never truncates: it names every life it holds, and a set larger than the kernel's per-query
/// ceiling is refused by the kernel with `entity-query-budget` — the evaluator rethrows that code, so an
/// overflowing set reaches the step as a refusal instead of a short list that looks like a small world.</summary>
internal sealed partial class EnemyModule
{
    private IReadOnlyList<EntityReference>? _candidateRoster;
    private Entry[] _candidateEntries = Array.Empty<Entry>();

    internal IReadOnlyList<EntityReference> CurrentCandidates()
    {
        CheckThread();
        // A released module owns no table: answering an empty list here would claim the world holds no enemy,
        // which is exactly the answer the kernel must never be given by a source that is simply gone.
        if (!IsRegistered)
            throw new RuntimeContractException("enemy-module-unavailable", "No enemy module is registered.");
        // The roster holds only the living, so the common read checks its members and returns it as it is: no
        // temporary list and no sort. A member that died or went stale rebuilds it once.
        if (_candidateRoster != null)
        {
            bool intact = true;
            foreach (var entry in _candidateEntries)
                if (!IsAliveLive(entry)) { intact = false; break; }
            if (intact) return _candidateRoster;
        }
        var living = new List<Entry>(_entities.Count);
        foreach (var entry in _entities.Values)
            if (IsAliveLive(entry)) living.Add(entry);
        living.Sort(static (left, right) => string.CompareOrdinal(left.Reference.Id, right.Reference.Id));
        _candidateEntries = living.ToArray();
        return _candidateRoster = Array.AsReadOnly(living.Select(entry => entry.Reference).ToArray());
    }
    /// <summary>A tracked corpse stays in the table until it despawns, but it is not a candidate: every action on the
    /// enemy selector would only refuse it, so the roster names the living.</summary>
    private bool IsAliveLive(Entry entry) => IsLive(entry) && entry.Enemy.Alive;
}
