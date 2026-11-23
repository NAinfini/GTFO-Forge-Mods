using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The instance ledger of the random spawn rules (contract M16). The vanilla spawn points report each instance a
/// rule produced, in the order the level generates them; this table answers the one question a `map-object` mount
/// on a rule asks: is the entity the event names an instance of this rule that carries the rule's behaviours.
///
/// Which instances carry behaviours is decided here, not by the author per instance: a rule with `Expected` N and
/// `Attached` K picks K of the N generation positions from the level seed and the rule id (never from a Unity
/// instance id or pointer), so every machine that runs the same level reaches the same positions and the host's
/// order is the authority. An instance beyond position N, or any instance of a rule that declared none, is not
/// attached, and a rule that produced fewer than N instances attaches fewer than K; both are counted so the
/// shortfall is reported rather than hidden. The table is per world: the first registration of a new world drops
/// every row of the previous one, and an instance is answered only with the world and life it was registered with.
/// </summary>
internal sealed class SpawnRuleRegistry
{
    private sealed class Row
    {
        public SpawnRule Rule = null!;
        public bool[] Chosen = Array.Empty<bool>();
        public int Registered;
        public int AttachedCount;
        public int Overflow;
    }

    private readonly Dictionary<string, Row> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityReference, (Row Row, bool Attached)> _instances = new();
    private long _epoch = long.MinValue;
    private uint _seed;
    private IReadOnlyList<SpawnRule> _definitions = Array.Empty<SpawnRule>();

    /// <summary>Installs this level's rules. Called once per level before its instances are generated.</summary>
    public void Load(IReadOnlyList<SpawnRule> rules, uint seed)
    {
        _definitions = rules;
        _seed = seed;
        lock (_rules) { Reset(long.MinValue); }
    }

    public bool HasRules => _definitions.Count != 0;

    /// <summary>The level seed every choice of this level is made from.</summary>
    public uint Seed => _seed;

    /// <summary>Run before a match is answered, so instances the game spawned but whose identity was not readable yet
    /// are recorded first, in generation order.</summary>
    public Action? BeforeMatch { get; set; }

    /// <summary>The rule an item spawn belongs to, by the zone and item id the vanilla distribution reported.</summary>
    public SpawnRule? ItemRule(int dimension, int layer, int zone, uint itemId)
        => _definitions.FirstOrDefault(rule => rule.Kind == SpawnRuleKind.Item && rule.ItemId == itemId
            && rule.Zone is { } z && z.Dimension == dimension && z.Layer == layer && z.Zone == zone);

    /// <summary>The rule one entry of a zone's random enemy list belongs to, by the group type and difficulty of the data
    /// block the spawned group carries.</summary>
    public SpawnRule? EnemyRule(int dimension, int layer, int zone, int groupType, int difficulty)
        => _definitions.FirstOrDefault(rule => rule.Kind == SpawnRuleKind.Enemy && rule.GroupType == groupType && rule.Difficulty == difficulty
            && rule.Zone is { } z && z.Dimension == dimension && z.Layer == layer && z.Zone == zone);

    public SpawnRule? Rule(string id) => _definitions.FirstOrDefault(rule => rule.Id == id);

    /// <summary>Records the next instance of a rule, in generation order. Answers whether it carries the rule's
    /// behaviours. The same reference registered twice is one instance; an instance already claimed by another rule
    /// keeps its first rule, and the call answers false.</summary>
    public bool Register(SpawnRule rule, EntityReference instance)
    {
        lock (_rules)
        {
            if (instance.WorldEpoch != _epoch) Reset(instance.WorldEpoch);
            if (_instances.TryGetValue(instance, out var known)) return known.Row.Rule.Id == rule.Id && known.Attached;
            var row = RowOf(rule);
            int position = row.Registered++;
            bool attached = position < row.Chosen.Length && row.Chosen[position];
            if (position >= row.Chosen.Length) row.Overflow++;
            if (attached) row.AttachedCount++;
            _instances[instance] = (row, attached);
            return attached;
        }
    }

    /// <summary>Whether <paramref name="instance"/> is an attached instance of the rule: the world and life must be
    /// exactly the ones it was registered with, so a replaced or destroyed entity is never re-pointed to.</summary>
    public bool Matches(string ruleId, EntityReference instance)
    {
        BeforeMatch?.Invoke();
        lock (_rules)
            return instance.WorldEpoch == _epoch && _instances.TryGetValue(instance, out var known)
                && known.Attached && known.Row.Rule.Id == ruleId;
    }

    /// <summary>Whether the rule recorded an entity of this id in this world under another life: the instance it
    /// recorded is gone and this one replaced it, which a caller reports instead of treating as an unchosen one.</summary>
    public bool RecordedUnderAnotherLife(string ruleId, EntityReference instance)
    {
        lock (_rules)
            return instance.WorldEpoch == _epoch && _instances.Any(row => row.Key.Id == instance.Id
                && row.Key.LifeEpoch != instance.LifeEpoch && row.Value.Row.Rule.Id == ruleId);
    }

    /// <summary>The registered, attached and overflow counts of a rule in the current world.</summary>
    public (int Registered, int Attached, int Overflow) Counts(string ruleId)
    {
        lock (_rules) return _rules.TryGetValue(ruleId, out var row) ? (row.Registered, row.AttachedCount, row.Overflow) : (0, 0, 0);
    }

    public void Clear()
    {
        lock (_rules) Reset(long.MinValue);
    }

    private void Reset(long epoch)
    {
        _rules.Clear(); _instances.Clear(); _epoch = epoch;
    }

    private Row RowOf(SpawnRule rule)
    {
        if (_rules.TryGetValue(rule.Id, out var row)) return row;
        row = new Row { Rule = rule, Chosen = Choose(_seed, rule.Id, rule.Expected, rule.Attached) };
        _rules[rule.Id] = row;
        return row;
    }

    /// <summary>The K positions of N a seed and a rule id pick: every position gets a hash of (seed, id, position),
    /// and the K smallest are chosen, ties going to the earlier position. The hash is FNV-1a over fixed bytes, so the
    /// result is the same on every machine and every run.</summary>
    public static bool[] Choose(uint seed, string ruleId, int expected, int attached)
    {
        var chosen = new bool[expected];
        if (attached <= 0) return chosen;
        if (attached >= expected) { Array.Fill(chosen, true); return chosen; }
        var order = new (uint Key, int Position)[expected];
        for (int i = 0; i < expected; i++) order[i] = (Hash(seed, ruleId, i), i);
        Array.Sort(order, (a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Position.CompareTo(b.Position));
        for (int i = 0; i < attached; i++) chosen[order[i].Position] = true;
        return chosen;
    }

    private static uint Hash(uint seed, string id, int position)
        => ForgeCore.Fnv.Mix32(ForgeCore.Fnv.MixUtf8(ForgeCore.Fnv.Mix32(ForgeCore.Fnv.Offset32, seed), id), position);
}
