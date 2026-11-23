using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `forge.selector.target.zone` declaration: the binding that implements it, the port shape its
/// handler is resolved against, and the evaluator that answers through the readers the game-bound registration
/// supplies. It sits in the game-independent Map assembly because the runtime module, the
/// manifest and the website read it there; the native half reads the level's zone table and none of that crosses
/// this boundary.
///
/// The row is the catalog's own: an optional `anchor` entity and the optional `dimension_index` / `local_index`
/// coordinates with the `layer_type` parameter, one nullable `zone` resource out. It is the half of `q-zone` that
/// names a place: a zone is the level's own, identified by the three coordinates its doors and terminals are
/// addressed by, so the text this row answers with is the text every other reader of that zone already
/// spells. The two spellings of that place are alternatives and not a merge — an anchor *and* a coordinate set is
/// refused, because the row cannot know which of the two the author meant.</summary>
internal static class ZoneSelectorContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.zone";
    public const string HandlerName = "gtfo.map.zone";
    /// <summary>The entity kind a zone is read as, and the namespace its resource ids are written in. It is the
    /// framework's own spelling, because the selector that filters by zone compares the reference this row answers
    /// with the one a provider answers for an entity of its own kind — two packages, one text.</summary>
    public const string EntityKind = EntityKinds.Zone;
    /// <summary>The `layer_type` members, in the order `LG_LayerType` declares them: the same three layers the
    /// objective rows index, so a coordinate set written here and a layer an objective write names are one
    /// vocabulary.</summary>
    public static readonly string[] Layers = { "main", "secondary", "third" };
    /// <summary>The one shape of this handler: the anchor it may be asked about, the coordinate ports it may be
    /// asked with, and the zone resource it answers.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("anchor", "dimension_index", "local_index").Outputs("zone").Parameters("layer_type");

    /// <summary>The refusal an anchor of a kind this row does not read gets. This row reads the zone of a player,
    /// whose placement the Player provider answers through the kernel's own zone read.</summary>
    public const string AnchorKindCode = "zone-anchor-kind";

    /// <summary>The refusal a read that could not be made gets because the world holds no level zone table: the
    /// anchor may well stand in a zone, and this process cannot name a single one of them.</summary>
    public const string AnchorUnavailableCode = "zone-anchor-unavailable";

    /// <summary>The refusal a tracked player whose own placement names no zone of this level gets, reported by
    /// name rather than answered as a zone the level does not have. It is a refusal and not a null answer: this
    /// row is asked for the zone of a player it holds, and a player standing in no zone of the level is not an
    /// answer the `zone` resource kind can carry.</summary>
    public const string AnchorUnplacedCode = "zone-anchor-unplaced";

    /// <summary>The refusal a coordinate set that names no layer this vocabulary has gets. A member outside the
    /// list is refused rather than mapped to the nearest layer.</summary>
    public const string LayerCode = "zone-layer-type";

    /// <summary>The refusal a request that names both an anchor and a coordinate set gets: the two are two ways of
    /// naming one place and the row does not merge them, so the author has to pick one.</summary>
    public const string ConflictCode = "zone-coordinates-conflict";

    /// <summary>The refusal a request that names neither an anchor nor a whole coordinate set gets: the coordinates
    /// are one group, so a request missing any of the three has named no place at all.</summary>
    public const string CoordinatesMissingCode = "zone-coordinates-missing";

    /// <summary>The refusal a coordinate set the level has no zone for gets, reported by name rather than answered
    /// as a zone the level does not have.</summary>
    public const string CoordinatesUnmatchedCode = "zone-coordinates-unmatched";

    /// <summary>The one read this row needs from the world: which zone an anchor stands in, and which zone the
    /// level's own coordinates name. Null is the answer that the anchor's own placement names no zone of this
    /// level, or that the level has no zone at those coordinates; a read that could not be made is refused with its
    /// own code — this contract's, or the anchor provider's zone refusal — and is never collapsed into that answer. This
    /// assembly holds no game type, so the game-bound registration is the only one that can supply the reads;
    /// there is no static slot here, because two registrations in one process would otherwise answer each other's
    /// reads.</summary>
    public sealed record ZoneReaders(Func<EntityReference, EntityReference?> ZoneOfAnchor,
        Func<int, string, int, EntityReference?> ZoneOfCoordinates);

    /// <summary>The one evaluator, keyed by handler name: what a registration adds to its evaluator table. Every refusal is raised with the code this contract names, so a caller can tell an
    /// anchor of another kind from one the level has not placed.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(ZoneReaders readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [HandlerName] = (context, outputs) => Evaluate(context, outputs, readers)
        };
    }

    private static void Evaluate(EvaluationContext context, EvaluationOutputs outputs, ZoneReaders readers)
    {
        // The two ways of naming a place are read first, and exactly one of them has to be the author's: the row
        // refuses a request that names both and one that names neither, before any world read.
        var anchored = context.Inputs.TryGetProperty("anchor", out var anchor) && anchor.ValueKind != JsonValueKind.Null;
        int? dimension = OptionalInteger(context, "dimension_index");
        int? local = OptionalInteger(context, "local_index");
        string? layer = context.Parameters.TryGetProperty("layer_type", out var layerValue)
            && layerValue.ValueKind == JsonValueKind.String ? layerValue.GetString() : null;
        var coordinates = dimension != null || local != null || layer != null;
        if (anchored && coordinates)
            throw new RuntimeContractException(ConflictCode, "A zone request names either an anchor or a coordinate set, never both.");
        if (!anchored && (dimension == null || local == null || layer == null))
            throw new RuntimeContractException(CoordinatesMissingCode,
                "A zone request needs an anchor or all three coordinates: dimension, layer and local index.");
        EntityReference? zone;
        if (anchored)
        {
            var reference = RuntimeJson.Entity(anchor);
            if (!EntityKinds.Is(reference, EntityKinds.Player))
                throw new RuntimeContractException(AnchorKindCode, "This row reads the zone of a player, not of " + reference.Id);
            zone = readers.ZoneOfAnchor(reference);
        }
        else
        {
            if (Array.IndexOf(Layers, layer) < 0)
                throw new RuntimeContractException(LayerCode, "Unknown layer member: " + layer);
            zone = readers.ZoneOfCoordinates(dimension!.Value, layer!, local!.Value);
            if (zone == null)
                throw new RuntimeContractException(CoordinatesUnmatchedCode,
                    "This level has no zone at dimension " + dimension + ", layer " + layer + ", local index " + local + ".");
        }
        if (zone == null || !EntityKinds.Is(zone, EntityKind))
            throw new RuntimeContractException(AnchorUnplacedCode, "The anchor stands in no zone of this level.");
        var resource = new ResourceRef(RuntimeZones.ResourceKind, zone.Id);
        outputs.Resource(0, resource);
    }

    /// <summary>One optional value input of the resolved frame, or null when the plan left it unwired: an unwired
    /// coordinate is the author's own statement that this half of the group is not in play.</summary>
    private static int? OptionalInteger(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble() : null;

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.SelectorTargetZone.Id, HandlerName, "observe", Array.Empty<string>(), Shape)
    };
}
