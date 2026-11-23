using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `forge.selector.target.map_object` declaration: the binding that implements it, the port shape
/// its handler is resolved against, and the evaluator that answers with the entity
/// the author's address names. It sits in the game-independent Map assembly for the same reason the zone
/// selector's does: the runtime module, the manifest and the website read it there, and the native half supplies
/// only the one read this assembly cannot make.
///
/// The row is the catalog's own: no input, one `target` entity out whose kind comes from the `address`
/// parameter's own category (a door, a terminal, a generator, a trigger zone or a shuttlebox, all `gtfo.map_object`;
/// or a level zone, `gtfo.zone`), and the plan carries that address as a compile-time constant. The entity a written
/// map-object address names is this provider's identity and nobody else's: `gtfo.map_object:<address>` with the
/// kernel's world epoch, which is the id the mount matcher and every published map-object fact already carry, so a
/// selector and the object it names are one identity rather than two lookups that could disagree. A level-zone
/// address (`zone/<d>/<l>/<z>/zone`) answers the zone kind's own identity, `gtfo.zone:<d>:<l>:<z>`, which is what the
/// zone-addressed presentation rows (fog, lighting) read their `zone` recipient as.
///
/// Resolution is the module's own: a query step is evaluated on demand, long after the plan was loaded, so the
/// address is resolved against this world every time it is asked and an address the level does not hold is a
/// refusal by name — never an empty answer a consumer would read as "the author named nothing".</summary>
internal static class MapObjectSelectorContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.map_object";
    public const string HandlerName = "gtfo.map.object";
    /// <summary>The one structural parameter, and the port it types.</summary>
    public const string AddressParameter = "address";
    public const string TargetPort = "target";
    /// <summary>The one shape of this handler: the address it is compiled with, and the entity it answers.</summary>
    public static readonly HandlerShape Shape = new HandlerShape().Parameters(AddressParameter).Outputs(TargetPort);

    /// <summary>The refusal an address this level does not hold gets. It is the authoring side's own code for the
    /// same fact (contract rule 143.10): the export checks every written address against the level's address table,
    /// and a world that no longer holds the object refuses under the same name instead of answering an entity
    /// nothing stands behind.</summary>
    public const string AddressUnavailableCode = "map-object-address-unavailable";

    /// <summary>The one read this row needs from the world: the entity an address text names right now, or null
    /// when this level holds no such object. The Map module owns that resolution — the same address grammar, the
    /// same category readers and the same native lookups the mount matcher uses — so this assembly never parses an
    /// address a second time and the native half only wires the module's own read in.</summary>
    public sealed record AddressReader(Func<string, EntityReference?> ReferenceOfAddress);

    /// <summary>The one evaluator, keyed by handler name. A missing address is the framework's own `missing-field`
    /// (the plan's own shape check refuses it first); an address this world cannot resolve is refused with the
    /// authoring side's own code, so a plan that names an object of another level is named as such.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(AddressReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [HandlerName] = (context, outputs) => Evaluate(context, outputs, reader)
        };
    }

    private static void Evaluate(EvaluationContext context, EvaluationOutputs outputs, AddressReader reader)
        => outputs.Entity(0, Answer(Address(context), reader));

    /// <summary>One written address answered as the row's frame: the one `target` entity the level holds at that
    /// address, or the refusal a level that holds none gets. The parameter read above is the only part that needs
    /// a dispatched frame, so this is the whole answer rule and the place both the handler and a reader of it
    /// agree on what an address means.</summary>
    public static EntityReference Answer(string address, AddressReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var reference = reader.ReferenceOfAddress(address)
            ?? throw new RuntimeContractException(AddressUnavailableCode,
                "This level holds no map object at " + address + ".");
        return reference;
    }

    /// <summary>The compiled address, refused by name when the frame carries none: a selector asked without the
    /// object it names has no answer, and an empty one would read exactly like an address that resolved.</summary>
    private static string Address(EvaluationContext context)
        => context.Parameters.TryGetProperty(AddressParameter, out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new RuntimeContractException("missing-field", AddressParameter);

    // ---- forge.selector.target.object_group ------------------------------------------------------------------

    /// <summary>The group selector: every placed trigger zone, placed model and interaction point of this level whose
    /// author group is the `group` parameter. The native half collects the members from the three placement halves.</summary>
    public const string GroupHandlerName = "gtfo.map.object_group";
    public static readonly HandlerShape GroupShape = new HandlerShape().Parameters("group").Outputs("targets");
    /// <summary>The refusal a group with no placed member in this level gets; the selector never answers an empty set
    /// a consumer would read as "the author named nothing".</summary>
    public const string GroupMissingCode = "map-object-group-missing";

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.SelectorTargetMapObject.Id, HandlerName, "observe", Array.Empty<string>(), Shape),
        new ProviderBinding(ModuleDefinition.ProviderId + ".binding.selector.target.object_group", Blocks.SelectorTargetObjectGroup.Id,
            GroupHandlerName, "observe", Array.Empty<string>(), GroupShape)
    };
}
