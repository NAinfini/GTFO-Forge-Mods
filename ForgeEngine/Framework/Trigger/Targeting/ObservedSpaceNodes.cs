using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>One partition row's answer: the candidates whose field equalled the plan's key, and the ones whose did
/// not. The two sides are disjoint and together are exactly the input, in the identity order every other collection
/// row answers with.</summary>
internal sealed class ObservedPartition
{
    internal ObservedPartition(EntityReference[] matched, EntityReference[] rest)
    {
        Matched = Array.AsReadOnly(matched); Rest = Array.AsReadOnly(rest);
    }
    public IReadOnlyList<EntityReference> Matched { get; }
    public IReadOnlyList<EntityReference> Rest { get; }
}

/// <summary>The space and ranking selection over the references a step already holds, and the registration tables
/// of the rows that publish it. The declaration is <see cref="ObservedSpaceDeclarations"/>'s; this class owns the
/// selection itself — the volume query, the two distance rankings, the chain, the relation
/// filter and the partition — so a handler, a consumer and a test all reach one implementation.
///
/// Every read goes through the caller's own <see cref="RuntimeQuerySession"/>: one budgeted call for the whole
/// explicit candidate set, and a set the kernel could not observe completely is refused rather than ranked, so a
/// selector can never answer with the best of the half it happened to see. A candidate that is provably gone is
/// not part of that set any more (<see cref="ObserveCurrent"/>).</summary>
internal static class ObservedSpaceNodes
{
    // ---- the family's registration tables ---------------------------------------------------------------------
    // The batch that declares these rows owns them; the module reads them here because this is the family's public
    // entry point, exactly as it reads the two tables of the query module.

    public static IReadOnlyList<ObservedNode> Nodes => ObservedSpaceDeclarations.Family.Nodes;

    /// <summary>An explicit candidate set's current positions, in the order the kernel answered them. One budgeted
    /// call, never a batch of single reads around the per-query or per-tick budget, and an incomplete answer — a
    /// spent budget, a stale identity, an observer that refused — is refused with the kernel's own code rather than
    /// answered as a shorter set.</summary>
    public static IReadOnlyList<RuntimeEntitySnapshot> Observe(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        // The read's own bound is decided before the read: one past it is refused rather than attempted and answered
        // with the kernel's normalized budget code, so a caller can tell "too many to ask about" from "the read ran
        // out of budget".
        if (candidates.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Explicit candidate query exceeds the Runtime limit.");
        if (!session.TrySnapshots(candidates, out var result, out var code))
            throw new RuntimeContractException(code, "Entity observation failed: " + code);
        // A partial answer is the framework's own refusal, thrown with the framework's own code: a selector that
        // ranked the half it happened to observe would silently drop the targets it never saw.
        return result.RequireComplete();
    }

    /// <summary>
    /// <see cref="Observe"/> for a selector's candidate set: a candidate the kernel proves is gone — its world
    /// ended, or the provider that owns its kind no longer knows that life — is no longer anything a selector could
    /// pick, so it leaves the set, exactly as `forge.control.flow.present` reads it as absent. A list held across a
    /// wait, in a variable or from an older event therefore still selects among the candidates that are left
    /// instead of refusing the whole step for one enemy that died meanwhile. Everything else is unchanged: a spent
    /// budget, an observer that refused or failed, and a candidate that merely could not be read still refuse the
    /// step, because those are targets that exist and were not seen. <paramref name="required"/> is the row's own
    /// anchor read with the set: it is not a candidate, so a gone anchor refuses with its own code.
    /// </summary>
    public static IReadOnlyList<RuntimeEntitySnapshot> ObserveCurrent(RuntimeQuerySession session,
        IReadOnlyList<EntityReference> candidates, EntityReference? required = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Explicit candidate query exceeds the Runtime limit.");
        if (!session.TrySnapshots(candidates, out var result, out var code))
            throw new RuntimeContractException(code, "Entity observation failed: " + code);
        if (result.IsComplete) return result.RequireComplete();
        var current = new List<RuntimeEntitySnapshot>(result.Items.Count);
        foreach (var item in result.Items)
        {
            if (item.Snapshot != null) { current.Add(item.Snapshot); continue; }
            var gone = item.Code is "stale-world" or "stale-entity";
            if (gone && item.Reference != required) continue;
            throw new RuntimeContractException(gone ? item.Code : result.Code, "Entity observation failed: " + item.Reference.Id);
        }
        return current.AsReadOnly();
    }

    /// <summary>The catalog's identity order over a set the kernel already observed.</summary>
    public static IReadOnlyList<EntityReference> IdentityOrder(IEnumerable<EntityReference> candidates)
        => Array.AsReadOnly(candidates.OrderBy(ReferenceCollections.OrderKey, StringComparer.Ordinal).ToArray());

    /// <summary>Minima and maxima of a distance ranking, in the order the plan asks for.</summary>
    public static IReadOnlyList<EntityReference> Nearest(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> anchor, int count)
        => ObservedSpatialNodes.Nearest(session, candidates, anchor, count).Selected;
    public static IReadOnlyList<EntityReference> Farthest(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> anchor, int count)
        => ObservedSpatialNodes.Farthest(session, candidates, anchor, count).Selected;

    /// <summary>One hop chain from the origin. The hop budget and radius are the existing chain's own bounds.</summary>
    public static IReadOnlyList<EntityReference> Chain(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference start, int hops, double radius)
        => ObservedSpatialNodes.Chain(session, candidates, start, hops, radius).Selected;

    /// <summary>The zone filter: the candidates the provider that owns each of their kinds places in the same zone
    /// the plan named. Zone membership is read from the same session as every other observation, one read per
    /// candidate, because a zone is a fact about an entity rather than a value any snapshot carries. The answer is
    /// in identity order, exactly like every other collection row of this family.
    ///
    /// An entity no provider can place is refused, never treated as "outside": a filter that dropped it would
    /// answer a shorter set than its own input, which is the one thing a collection row may not do silently.</summary>
    public static IReadOnlyList<EntityReference> ZoneMembers(RuntimeQuerySession session,
        IReadOnlyList<EntityReference> candidates, EntityReference zone, int? area = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        RuntimeEntityReferences.Validate(zone);
        if (area is { } index && index < 0)
            throw new RuntimeContractException("zone-area-index", "An area index is a position in the zone's own list: " + index);
        if (candidates.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Explicit candidate query exceeds the Runtime limit.");
        // The whole set is one read, charged once as a snapshot set is: one query per candidate spent the tick's
        // queries on a single filter over a level's enemies.
        if (candidates.Count == 0) return Array.Empty<EntityReference>();
        var zones = new List<EntityZoneResolution>(candidates.Count);
        // A candidate that is provably gone stands in no zone (see ObserveCurrent); any other unplaced one refuses.
        if (!session.TryZones(candidates, zones, out var code, skipGone: true))
            throw new RuntimeContractException(code, "The zones of the candidates could not be read: " + code);
        var matched = new List<EntityReference>();
        for (var i = 0; i < candidates.Count; i++)
            if (zones[i].Zone is { } placed && placed == zone) matched.Add(candidates[i]);
        // The area is the second half of the same placement and is read from the same owner, for the candidates in
        // the zone only. A candidate the provider cannot place in an area is refused rather than answered as area zero.
        if (area is { } wanted && matched.Count > 0)
        {
            var areas = new List<EntityAreaResolution>(matched.Count);
            if (!session.TryZoneAreas(matched, areas, out var areaCode))
                throw new RuntimeContractException(areaCode, "The zone areas of the candidates could not be read: " + areaCode);
            matched = matched.Where((_, i) => areas[i].Area == wanted).ToList();
        }
        return IdentityOrder(matched);
    }

    /// <summary>The relation filter measured against the row's explicit anchor, never against a role looked up
    /// again: the anchor is one more reference read in the same budgeted call as the candidates.</summary>
    public static IReadOnlyList<EntityReference> Filter(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference anchor, RuntimeFactionRelations relations, RecipientFilterRequest request)
        => ObservedRecipientFilter.Select(session, candidates, anchor, relations, request).Selected;

    /// <summary>One ordering of an observed candidate set by one named key, with ties always broken by stable
    /// identity so the same world and the same candidates answer the same order. `distance` and `angle` are
    /// measured against the row's own explicit `anchor` — `angle` additionally against its `direction` — and
    /// `health` reads the candidate's own snapshot. The anchor is one more reference read in the same budgeted
    /// call as the candidates, so the ranking never mixes a stale position with a current one, and a measurement
    /// the plan did not supply or a health no provider published is refused by name rather than answered with a
    /// made-up order.</summary>
    public static IReadOnlyList<EntityReference> Sort(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference? anchor, IReadOnlyList<double>? direction, string key, string order)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (order is not ("ascending" or "descending"))
            throw new RuntimeContractException(SortOrderCode, "Unknown order member: " + order);
        var measured = key is "distance" or "angle";
        EntityReference? from = null;
        if (measured)
        {
            if (anchor is not { } reference)
                throw new RuntimeContractException(SortAnchorCode, "The " + key + " key needs the anchor it is measured from.");
            from = reference;
        }
        if (key == "angle" && direction == null)
            throw new RuntimeContractException(SortDirectionCode, "The angle key needs the direction it is measured against.");
        var requested = from != null
            ? (candidates.Contains(from) ? candidates : candidates.Append(from).ToArray()) : candidates;
        var observed = ObserveCurrent(session, requested, from);
        double[]? origin = null;
        if (from != null)
        {
            var found = observed.FirstOrDefault(row => row.Ref == from);
            if (found == null)
                throw new RuntimeContractException(SortAnchorCode, "The anchor could not be observed: " + from.Id);
            origin = found.Position.ToArray();
        }
        double[]? axis = key == "angle" ? Unit(direction!) : null;
        var candidatesSet = new HashSet<EntityReference>(candidates);
        var ranked = observed.Where(row => candidatesSet.Contains(row.Ref))
            .Select(row => (Row: row, Key: Measure(row, key, origin, axis))).ToArray();
        var ordered = order == "descending"
            ? ranked.OrderByDescending(row => row.Key).ThenBy(row => ReferenceCollections.OrderKey(row.Row.Ref), StringComparer.Ordinal)
            : ranked.OrderBy(row => row.Key).ThenBy(row => ReferenceCollections.OrderKey(row.Row.Ref), StringComparer.Ordinal);
        return Array.AsReadOnly(ordered.Select(row => row.Row.Ref).ToArray());
    }

    /// <summary>One candidate's own key, over the one observation the row already made. A key the row does not
    /// declare is refused rather than answered as a distance, and a candidate whose own health no provider
    /// published is refused rather than sorted as zero: an unreadable value would put it at one end of the order
    /// as if the game had said so.</summary>
    private static double Measure(RuntimeEntitySnapshot row, string key, double[]? origin, double[]? axis)
    {
        switch (key)
        {
            case "distance":
                return ObservedSpatialNodes.Distance(row.Position, origin!);
            case "angle":
                return Angle(origin!, row.Position, axis!);
            case "health":
                if (row.Health is not { } health)
                    throw new RuntimeContractException(SortHealthCode, "The health of " + row.Ref.Id + " could not be read.");
                return health;
            default:
                throw new RuntimeContractException(SortKeyCode, "Unknown sort key: " + key);
        }
    }

    /// <summary>The angle in degrees between the row's own direction and the direction from the anchor to one
    /// candidate: 0 is straight ahead and 180 straight behind, so an ascending order is the one an aim assist
    /// wants. A candidate standing exactly on the anchor is 0 degrees rather than a NaN, because a port of unit
    /// `deg` is a number a plan compares.</summary>
    private static double Angle(IReadOnlyList<double> origin, IReadOnlyList<double> position, IReadOnlyList<double> axis)
    {
        var x = position[0] - origin[0]; var y = position[1] - origin[1]; var z = position[2] - origin[2];
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(length)) throw new RuntimeContractException("spatial-overflow", "Position difference overflowed.");
        if (length <= double.Epsilon) return 0d;
        var cos = (x * axis[0] + y * axis[1] + z * axis[2]) / length;
        return Math.Acos(Math.Clamp(cos, -1d, 1d)) * 180d / Math.PI;
    }

    /// <summary>The row's own direction as a unit vector. A direction of zero length is not a direction, so it is
    /// refused rather than read as an axis every candidate would measure the same against.</summary>
    private static double[] Unit(IReadOnlyList<double> direction)
    {
        if (direction.Count != 3 || direction.Any(value => !double.IsFinite(value)))
            throw new RuntimeContractException(SortDirectionCode, "The direction requires three finite coordinates.");
        var length = Math.Sqrt(direction[0] * direction[0] + direction[1] * direction[1] + direction[2] * direction[2]);
        if (!(length > double.Epsilon))
            throw new RuntimeContractException(SortDirectionCode, "The direction must not be the zero vector.");
        return new[] { direction[0] / length, direction[1] / length, direction[2] / length };
    }

    /// <summary>The row's `key` member names no key this build measures.</summary>
    public const string SortKeyCode = "sort-key";
    /// <summary>The row's `order` member names no order this build applies.</summary>
    public const string SortOrderCode = "sort-order";
    /// <summary>The row's key needs the anchor it is measured from, and the plan left it out.</summary>
    public const string SortAnchorCode = "sort-anchor";
    /// <summary>The row's key needs a direction, and the plan left it out or supplied one that is not a vector.</summary>
    public const string SortDirectionCode = "sort-direction";
    /// <summary>A candidate whose own health this provider does not publish.</summary>
    public const string SortHealthCode = "sort-health";

    /// <summary>Partition splits an observed candidate set on one named field into the rows whose field carries the
    /// plan's `key` and the rows whose field does not. The two sides are disjoint and together are exactly the
    /// input, so nothing is dropped; a field the catalog does not declare is refused instead of answering every
    /// target as "rest". A field every candidate has (`kind`, `life_state`) and a key that is never found therefore
    /// answer every candidate as `rest` — a real split, not an error.</summary>
    public static ObservedPartition PartitionOn(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        string fieldName, string key)
    {
        var values = PartitionField(fieldName);
        var matched = new List<EntityReference>(); var rest = new List<EntityReference>();
        foreach (var snapshot in ObserveCurrent(session, candidates))
            (values(snapshot).Contains(key, StringComparer.Ordinal) ? matched : rest).Add(snapshot.Ref);
        return new ObservedPartition(IdentityOrder(matched).ToArray(), IdentityOrder(rest).ToArray());
    }

    private static Func<RuntimeEntitySnapshot, IReadOnlyList<string>> PartitionField(string name) => name switch
    {
        "kind" => snapshot => new[] { snapshot.Kind },
        "faction" => snapshot => snapshot.Faction == null ? Array.Empty<string>() : new[] { snapshot.Faction },
        "life_state" => snapshot => new[] { snapshot.LifeState },
        "tag" => snapshot => snapshot.Tags,
        _ => throw new RuntimeContractException("partition-key", "Unknown partition field: " + name)
    };

    /// <summary>The catalog's structural empty policy, read from one row's resolved parameter frame: `fail` refuses
    /// an empty answer, `emit-empty` passes it on.</summary>
    public static EmptySelectionPolicy EmptyPolicy(JsonElement parameters) => ObservedEvaluation.EmptyPolicy(parameters);
}
