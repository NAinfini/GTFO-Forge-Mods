using System;
using ForgeEngine.Framework;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The two entity-state reads: the kind one reference was observed as, and the zone it currently stands
/// in. Both are `state` rows in the catalog's `query` namespace, and both answer about an entity the plan named —
/// the kind comes from the provider that owns the kind, and the zone from the kernel's own zone resolution, so
/// neither row invents a second answer for a fact the world already publishes.
///
/// A zone the kernel cannot answer is refused with the kernel's own code. The row never answers "no zone": an
/// entity that stands nowhere and an entity whose placement could not be read would then be the same answer, and a
/// consumer could not tell a level without zones from an unreadable read.</summary>
internal static class ObservedEntityStateDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Row(Blocks.QueryEntityKind.Id,
            new HandlerShape().Inputs("entity").Outputs("kind"), KindHandler),
        ObservedDeclaration.Row(Blocks.QueryEntityZone.Id,
            new HandlerShape().Inputs("entity").Outputs("zone"), ZoneHandler));

    /// <summary>One reference's observed kind, read through the step's own session. The kind is what the provider
    /// that owns the entity observed — never a kind spelled from the reference's id.</summary>
    public static string Kind(RuntimeQuerySession session, EntityReference entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return ObservedSpaceNodes.Observe(session, new[] { entity })[0].Kind;
    }

    /// <summary>The zone one reference currently stands in, as the kernel's own zone entity for it. The provider
    /// that owns the entity's kind answers the placement; a kind no provider can place and an entity the level has
    /// not placed are both refusals, carried here with the kernel's own code.</summary>
    public static EntityReference ZoneOf(RuntimeQuerySession session, EntityReference entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!session.TryZone(entity, out var zone, out var code))
            throw new RuntimeContractException(code, "The entity's zone could not be read: " + code);
        return zone!;
    }

    // ---- handlers ------------------------------------------------------------------------------------------

    /// <summary>The port is an enum of the wire set `object_kind`, whose members are the observed top kinds as
    /// <see cref="EntityKinds"/> spells them, so the kind is published by name.</summary>
    private static void KindHandler(EvaluationContext context, EvaluationOutputs outputs)
        => outputs.Text(0, Kind(context.Query, ObservedEvaluation.Entity(context, "entity")));

    /// <summary>The zone the row answers is the resource the catalog's port carries: the zone's own reference,
    /// wrapped in the resource kind the port declares, so a consumer reads one shape whether the zone came from a
    /// step or from the document.</summary>
    private static void ZoneHandler(EvaluationContext context, EvaluationOutputs outputs)
        => outputs.Resource(0, new ResourceRef(RuntimeZones.ResourceKind, ZoneOf(context.Query, ObservedEvaluation.Entity(context, "entity")).Id));
}
