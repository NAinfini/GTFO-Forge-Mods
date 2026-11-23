using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>Consumers of public R3 observations. No discovery, clock, registry or authority grant.</summary>
internal static class ObservedEntityNodes
{
    public static RuntimeEntitySnapshot Actor(IForgeEngine runtime, RuntimeActorContext actors, string role)
        => One(runtime.InspectActor(actors, role));

    public static RuntimeEntitySnapshot Entity(IForgeEngine runtime, EntityReference target)
        => One(runtime.InspectEntities(new[] { target }));

    /// <summary>False only for a proven stale world/life or absent entity; unknown observation failures still reject.</summary>
    public static bool Exists(IForgeEngine runtime, EntityReference target)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        RuntimeEntityReferences.Validate(target);
        var query = runtime.InspectEntities(new[] { target });
        if (query.IsComplete) return true;
        if (query.Status == "partial" && query.Items.Count == 1
            && query.Items[0].Code is "stale-world" or "stale-entity") return false;
        throw new RuntimeContractException(query.Code, "Entity existence is unknown: "
            + string.Join(", ", query.Items.Select(item => item.Code)));
    }

    public static bool IsKind(IForgeEngine runtime, EntityReference target, string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return string.Equals(Entity(runtime, target).Kind, kind, StringComparison.Ordinal);
    }

    public static bool HasTag(IForgeEngine runtime, EntityReference target, string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return Entity(runtime, target).Tags.Contains(tag, StringComparer.Ordinal);
    }
    public static string Relation(IForgeEngine runtime, RuntimeActorContext actors, string role,
        EntityReference recipient, RuntimeFactionRelations relations)
    {
        ArgumentNullException.ThrowIfNull(actors); ArgumentNullException.ThrowIfNull(relations);
        var anchor = actors.Get(role);
        if (anchor is null) throw new RuntimeContractException("actor-missing", "The requested relation anchor is absent.");
        var snapshots = runtime.InspectEntities(new[] { anchor, recipient }).RequireComplete();
        return relations.Resolve(snapshots.Single(row => row.Ref == anchor), snapshots.Single(row => row.Ref == recipient));
    }

    public static IReadOnlyList<RuntimeEntitySnapshot> RequireReceivers(IForgeEngine runtime,
        IReadOnlyList<EntityReference> recipients, string capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var snapshots = runtime.InspectEntities(recipients).RequireComplete();
        foreach (var snapshot in snapshots)
            if (!snapshot.Receives.Contains(capability, StringComparer.Ordinal))
                throw new RuntimeContractException("receiver-unsupported", snapshot.Ref.Id + " cannot receive " + capability);
        return snapshots;
    }

    private static RuntimeEntitySnapshot One(RuntimeEntityQueryResult query)
    {
        var snapshots = query.RequireComplete();
        if (snapshots.Count != 1)
            throw new RuntimeContractException("entity-observation-cardinality", "Exactly one current entity was required.");
        return snapshots[0];
    }
}
