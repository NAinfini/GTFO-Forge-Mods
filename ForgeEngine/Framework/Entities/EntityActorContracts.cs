using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>Explicit actor roles supplied by the caller. No role is inferred from another, and a role the dispatch
/// leaves unreadable is refused by name rather than answered from a neighbouring one.</summary>
public sealed class RuntimeActorContext
{
    private readonly IReadOnlyDictionary<string, EntityReference> actors;
    private readonly bool ambiguousSelf;
    public RuntimeActorContext(IReadOnlyDictionary<string, EntityReference> values) : this(values, false) { }
    /// <summary>The same context for a dispatch whose own mount accepted more than one entity: `self` has no single
    /// answer there, so <see cref="Get"/> refuses it — a step that never reads the role still runs.</summary>
    internal RuntimeActorContext(IReadOnlyDictionary<string, EntityReference> values, bool ambiguousSelf)
    {
        ArgumentNullException.ThrowIfNull(values);
        RuntimeJson.Require(values.Count <= 5, "actor-budget", "Only the five explicit roles are supported.");
        var copy = new Dictionary<string, EntityReference>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ValidateRole(pair.Key); copy.Add(pair.Key, RuntimeEntityReferences.Validate(pair.Value));
        }
        actors = new ReadOnlyDictionary<string, EntityReference>(copy);
        this.ambiguousSelf = ambiguousSelf;
    }
    internal static void ValidateRole(string role)
        => RuntimeJson.Require(RuntimeActorRoles.IsRole(role), "actor-role", "Unknown actor role.");
    /// <summary>The role's actor, or null when the dispatch does not carry it. `self` is the one role that can be
    /// unreadable rather than absent — a mount that accepted several entities has no single subject — and it is
    /// refused by name here instead of being reported as a missing role.</summary>
    public EntityReference? Get(string role)
    {
        ValidateRole(role);
        if (ambiguousSelf && role == "self")
            throw new RuntimeContractException("actor-ambiguous", "The plan's mount accepted more than one entity for this event.");
        return actors.TryGetValue(role, out var value) ? value : null;
    }
    /// <summary>The role's actor, or a refusal when the event that reached this handler did not carry it. A role a
    /// contract does not allow to be absent reads itself through here, so the refusal names the role and never looks
    /// like a missing world entity.</summary>
    public EntityReference Require(string role)
        => Get(role) ?? throw new RuntimeContractException("actor-missing", "The event carries no " + role + " actor.");
    public IReadOnlyDictionary<string, EntityReference> Actors => actors;
    public static RuntimeActorContext FromJson(JsonElement input)
    {
        var value = RuntimeJson.Parse(input.GetRawText());
        RuntimeJson.Shape(value, "", "self source owner instigator event-target");
        return new(value.EnumerateObject().ToDictionary(p => p.Name,
            p => RuntimeJson.Entity(p.Value), StringComparer.Ordinal));
    }
}

public sealed record RuntimeFactionRelation(string From, string To, string Relation);

/// <summary>Bounded directed snapshot of explicit relations, not a global faction service.</summary>
public sealed class RuntimeFactionRelations
{
    public const int MaximumRelations = 4096;
    private readonly Dictionary<(string From, string To), string> relations = new();
    public RuntimeFactionRelations(IReadOnlyList<RuntimeFactionRelation> rules)
    {
        RuntimeJson.Require(rules != null && rules.Count <= MaximumRelations,
            "relation-budget", "Faction relation budget exceeded.");
        for (int i = 0; i < rules!.Count; i++)
        {
            var rule = rules[i]; ArgumentNullException.ThrowIfNull(rule);
            var from = RuntimeJson.Text(rule.From); var to = RuntimeJson.Text(rule.To);
            RuntimeJson.Require(rule.Relation is "ally" or "hostile" or "neutral",
                "relation-value", "Only explicit ally, hostile or neutral rules are accepted.");
            RuntimeJson.Require(relations.TryAdd((from, to), rule.Relation),
                "relation-conflict", "Duplicate directed faction rule.");
        }
    }
    /// <summary>The one table every game world starts with: players are allies of each other, players and enemies are
    /// hostile in both directions, and enemies are allies of each other. Map and level objects carry no rule, so
    /// they resolve to `unknown` rather than to a guessed relationship.</summary>
    internal static RuntimeFactionRelations Gameplay() => new(new RuntimeFactionRelation[]
    {
        new("player", "player", "ally"), new("player", "enemy", "hostile"),
        new("enemy", "player", "hostile"), new("enemy", "enemy", "ally")
    });
    public string Resolve(RuntimeEntitySnapshot anchor, RuntimeEntitySnapshot recipient)
    {
        ArgumentNullException.ThrowIfNull(anchor); ArgumentNullException.ThrowIfNull(recipient);
        RuntimeJson.Require(anchor.Ref.WorldEpoch == recipient.Ref.WorldEpoch,
            "stale-world", "Relationship snapshots belong to different worlds.");
        if (anchor.Ref == recipient.Ref) return "self";
        if (anchor.Faction == null || recipient.Faction == null) return "unknown";
        return relations.TryGetValue((anchor.Faction, recipient.Faction), out var relation) ? relation : "unknown";
    }
    public static RuntimeFactionRelations FromJson(JsonElement input)
    {
        var value = RuntimeJson.Parse(input.GetRawText());
        RuntimeJson.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= MaximumRelations,
            "relation-budget", "Expected bounded faction rule array.");
        var rules = new List<RuntimeFactionRelation>();
        foreach (var rule in value.EnumerateArray())
        {
            RuntimeJson.Shape(rule, "from to relation");
            rules.Add(new(RuntimeJson.Text(rule, "from"), RuntimeJson.Text(rule, "to"), RuntimeJson.Text(rule, "relation")));
        }
        return new(rules);
    }
}
