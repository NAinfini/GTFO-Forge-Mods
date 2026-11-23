using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The space and ranking selectors: the rows that read the world to select from the explicit candidates a
/// plan handed them — the volume query, the distance ranking, the chain, the relation
/// filter and the partition. Every one of them takes its candidates from its own catalog port; none of them
/// enumerates a world of its own, so an absent or empty candidate set is never answered as if it were the query
/// world.
///
/// The declaration is this file's; the selection itself is <see cref="ObservedSpaceNodes"/>, which owns the
/// algorithms and the public selection entry points the handlers below call — one read, one ranking, one refusal
/// path.</summary>
internal static class ObservedSpaceDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Row(Blocks.SelectorTargetShapeOverlap.Id,
            new HandlerShape().Inputs("candidates", "center", "radius", "angle", "height", "extents", "direction").Outputs("targets")
                .Parameters("shape", "empty"), ShapeOverlap),
        ObservedDeclaration.Row(Blocks.SelectorTargetNearest.Id,
            new HandlerShape().Inputs("anchor", "candidates", "max_targets").Outputs("targets").Parameters("empty", "direction"), NearestHandler),
        ObservedDeclaration.Row(Blocks.SelectorTargetChain.Id,
            new HandlerShape().Inputs("origin", "candidates", "hops", "radius").Outputs("targets").Parameters("empty"), ChainHandler),
        ObservedDeclaration.Row(Blocks.SelectorTargetFilter.Id,
            new HandlerShape().Inputs("anchor", "candidates", "player_slot").Outputs("targets")
                .Parameters("kind", "relation", "state", "dimension", "damageable_doors", "include_self", "include_bots", "slot_overflow", "empty"), FilterHandler),
        ObservedDeclaration.Row(Blocks.SelectorTargetPartition.Id,
            new HandlerShape().Inputs("candidates", "key").Outputs("matched", "rest").Parameters("field", "empty"), PartitionHandler),
        ObservedDeclaration.Row(Blocks.SelectorTargetSort.Id,
            new HandlerShape().Inputs("candidates", "anchor", "direction").Outputs("targets").Parameters("key", "order", "empty"), SortHandler),
        ObservedDeclaration.Row(Blocks.SelectorTargetZoneMembers.Id,
            new HandlerShape().Inputs("candidates", "zone", "area").Outputs("targets").Parameters("empty"), ZoneMembersHandler));

    /// <summary>The zone filter keeps the candidates whose own zone is the one the plan named, and it reads that
    /// zone through the same session as the candidates: the plan hands a zone resource, the row asks the kernel for
    /// the zone entity that resource names, and every candidate's zone comes from the provider that owns its kind.
    /// A kind no provider can place, an entity the level has not placed, and a zone that is not this level's are all
    /// refusals — a filter that answered an unknown zone as "outside" would silently drop the target it could not
    /// read. The zone is compared by identity, which is the level's own coordinates, so two references to one zone
    /// answer the same.</summary>
    private static void ZoneMembersHandler(EvaluationContext context, EvaluationOutputs outputs)
    {
        // The row's own port is read first, so a plan naming a zone this level does not have is refused before any
        // candidate is read, and the entity the answer carries is the kernel's own reference rather than one this
        // half would have to spell the world epoch of.
        var zone = ZoneReference(context);
        var candidates = ObservedEvaluation.Candidates(context, "candidates");
        var area = ObservedEvaluation.OptionalInputInteger(context, "area");
        if (!TriggerLog.Logs(RuntimeLogLevel.Trace))
        {
            ObservedEvaluation.Targets(context, outputs, ObservedSpaceNodes.ZoneMembers(context.Query, candidates, zone, area));
            return;
        }
        // The cost the zone_members row pays on this machine — one zone read per candidate — measured only while
        // trace is open, so a player's install never reads the clock here.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var members = ObservedSpaceNodes.ZoneMembers(context.Query, candidates, zone, area);
        var elapsedUs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        TriggerLog.Write(RuntimeLogLevel.Trace, TriggerLogCodes.ZoneMembers, new RuntimeLogFacts
        {
            Values = new[]
            {
                RuntimeLogValue.Of("candidates", candidates.Count), RuntimeLogValue.Of("matched", members.Count),
                RuntimeLogValue.Of("area", area != null), RuntimeLogValue.Of("elapsedUs", elapsedUs)
            }
        });
        ObservedEvaluation.Targets(context, outputs, members);
    }

    /// <summary>The zone the row's own resource port names, as the kernel's own entity for it. The port carries
    /// what the plan compiled — a reference written in the document's `{id, revision}` form — and a value an
    /// upstream step produced, which is the frame's own two-field form; the id is the zone's coordinates either
    /// way, so both spellings are read here and the kernel's one zone read decides whether the level has it.</summary>
    internal static EntityReference ZoneReference(EvaluationContext context)
    {
        var value = context.RequiredInput("zone");
        string id;
        if (value.TryGetProperty("resourceKind", out var kind))
        {
            var resource = RuntimeJson.ResourceRefOf(value);
            if (resource.ResourceKind != RuntimeZones.ResourceKind)
                throw new RuntimeContractException(RuntimeAbiCodes.ResourceKind, resource.ResourceKind);
            id = resource.ResourceId;
        }
        else if (!value.TryGetProperty("id", out var compiled) || compiled.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("missing-field", "zone");
        else id = compiled.GetString()!;
        // The zone is read in the world this step runs in: a reference of another world is the kernel's own
        // `stale-world` refusal, not an id this half may re-stamp.
        var reference = new EntityReference(id, context.Query.WorldEpoch, 0);
        if (!context.Query.TryZone(reference, out var zone, out var code))
            throw new RuntimeContractException(code, "The named zone could not be read: " + code);
        return zone!;
    }

    // ---- handlers ---------------------------------------------------------------------------------------------

    /// <summary>The volume selects from the row's own `candidates` port — this row never enumerates a world, so a
    /// candidate port that is not there is refused instead of passing as "everything nearby". The shape is
    /// dispatched before any size is read, so each member reads only the ports it defines: a sphere its `radius`, a
    /// cone its `radius` (its length from the apex), `angle` and `direction`, a cylinder and a capsule their
    /// `radius` and `height`, and a box its own three `extents` half sizes, one per world axis. A member this build
    /// cannot measure is refused with `spatial-shape` before an unrelated port is touched.</summary>
    private static void ShapeOverlap(EvaluationContext context, EvaluationOutputs outputs)
    {
        var volume = ObservedEvaluation.ParameterText(context, "shape") switch
        {
            "sphere" => ObservedVolume.Sphere(context.RequiredInput("radius").GetDouble()),
            "cone" => ObservedVolume.Cone(context.RequiredInput("radius").GetDouble(),
                context.RequiredInput("angle").GetDouble(), Vector(context, "direction")),
            "cylinder" => ObservedVolume.Cylinder(context.RequiredInput("radius").GetDouble(),
                context.RequiredInput("height").GetDouble()),
            "capsule" => ObservedVolume.Capsule(context.RequiredInput("radius").GetDouble(),
                context.RequiredInput("height").GetDouble()),
            "box" => ObservedVolume.Box(Vector(context, "extents")),
            var other => throw new RuntimeContractException("spatial-shape", "Unsupported query shape: " + other)
        };
        ObservedEvaluation.Targets(context, outputs, ObservedSpatialNodes.Overlap(context.Query,
            ObservedEvaluation.Candidates(context, "candidates"), Vector(context, "center"), volume));
    }

    /// <summary>The anchor entity is resolved through the same session read as the candidates, so the ranking never
    /// mixes a stale position with a current one.
    /// `direction` picks the nearest or the farthest end of the same ranking; an unwritten member is the first,
    /// the nearest.</summary>
    private static void NearestHandler(EvaluationContext context, EvaluationOutputs outputs)
    {
        var candidates = ObservedEvaluation.Candidates(context, "candidates");
        var count = ObservedEvaluation.RequestedCount(context.RequiredInput("max_targets"), 1, ReferenceCollections.MaximumSelection);
        ObservedEvaluation.Targets(context, outputs, (ObservedEvaluation.OptionalParameterText(context, "direction") ?? "nearest") switch
        {
            "nearest" => ObservedSpaceNodes.Nearest(context.Query, candidates, AnchorPosition(context), count),
            "farthest" => ObservedSpaceNodes.Farthest(context.Query, candidates, AnchorPosition(context), count),
            var other => throw new RuntimeContractException("pure-operation", "Unknown distance direction: " + other)
        });
    }

    private static void ChainHandler(EvaluationContext context, EvaluationOutputs outputs)
        => ObservedEvaluation.Targets(context, outputs, ObservedSpaceNodes.Chain(context.Query,
            ObservedEvaluation.Candidates(context, "candidates"), ObservedEvaluation.Entity(context, "origin"),
            ObservedEvaluation.RequestedCount(context.RequiredInput("hops"), 1, 64),
            context.RequiredInput("radius").GetDouble()));

    /// <summary>The relation is measured against the row's explicit `anchor` input, which is what a deployable
    /// wires its `owner` into; nothing here reads a role from the event. The relation is optional: a plan that
    /// writes none asks for no relation rule and keeps every candidate the other rules accept. The row's other
    /// optional members are read from the same parameter bag — and its one optional value input from the frame's
    /// own inputs — and default to the member that filters nothing, so a plan written before they existed
    /// compiles and means what it always meant.</summary>
    private static void FilterHandler(EvaluationContext context, EvaluationOutputs outputs)
        => ObservedEvaluation.Targets(context, outputs, ObservedSpaceNodes.Filter(context.Query,
            ObservedEvaluation.Candidates(context, "candidates"), ObservedEvaluation.Entity(context, "anchor"),
            context.Relations, RecipientFilterRequest.Read(ObservedEvaluation.OptionalParameterText(context, "relation"),
                ObservedEvaluation.OptionalParameterText(context, "state"),
                ObservedEvaluation.OptionalParameterInteger(context, "dimension"),
                ObservedEvaluation.OptionalParameterFlag(context, "damageable_doors", true),
                ObservedEvaluation.OptionalParameterFlag(context, "include_self", true),
                ObservedEvaluation.OptionalInputInteger(context, "player_slot"),
                ObservedEvaluation.OptionalParameterFlag(context, "include_bots", true),
                ObservedEvaluation.OptionalParameterText(context, "slot_overflow"),
                ObservedEvaluation.OptionalParameterText(context, "kind"))));

    /// <summary>The ordering row: the candidates are reordered by the key the plan named, measured against the
    /// row's own explicit `anchor` and `direction` where the key needs one. Both are read from the row's own
    /// ports, never from an event role, and the key decides which of them the row asks for.</summary>
    private static void SortHandler(EvaluationContext context, EvaluationOutputs outputs)
        => ObservedEvaluation.Targets(context, outputs, ObservedSpaceNodes.Sort(context.Query,
            ObservedEvaluation.Candidates(context, "candidates"),
            context.Inputs.TryGetProperty("anchor", out var anchor) && anchor.ValueKind != JsonValueKind.Null
                ? RuntimeJson.Entity(anchor) : null,
            ObservedEvaluation.OptionalInputVector(context, "direction"),
            ObservedEvaluation.ParameterText(context, "key"), ObservedEvaluation.ParameterText(context, "order")));

    /// <summary>`empty` is about the selection this row makes, the `matched` side, as on every other selector:
    /// `fail` refuses only when nothing matched. The `rest` side is what was left over and may be empty without
    /// that being a failed selection (everyone matched).</summary>
    private static void PartitionHandler(EvaluationContext context, EvaluationOutputs outputs)
    {
        var partition = ObservedSpaceNodes.PartitionOn(context.Query, ObservedEvaluation.Candidates(context, "candidates"),
            ObservedEvaluation.ParameterText(context, "field"), context.RequiredInput("key").GetString()!);
        outputs.Entities(0, ReferenceCollections.ApplyEmptyPolicy(partition.Matched, ObservedEvaluation.EmptyPolicy(context.Parameters)));
        outputs.Entities(1, partition.Rest);
    }

    /// <summary>The anchor entity's current position, read through the session. An anchor the kernel could not
    /// observe is the session's own refusal, never a zero position.</summary>
    private static double[] AnchorPosition(EvaluationContext context)
    {
        if (!context.Query.TrySnapshot(ObservedEvaluation.Entity(context, "anchor"), out var snapshot, out var code))
            throw new RuntimeContractException(code, "Anchor observation failed: " + code);
        return snapshot!.Position.ToArray();
    }

    private static double[] Vector(EvaluationContext context, string port)
        => context.RequiredInput(port).EnumerateArray().Select(value => value.GetDouble()).ToArray();
}
