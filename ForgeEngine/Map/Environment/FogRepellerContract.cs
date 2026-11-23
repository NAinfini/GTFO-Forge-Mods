using System;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The one row this package owns about a fog repeller the level placed: `forge.action.presentation.fog_repeller`,
/// the switch that starts or stops a placed `FogRepeller_Sphere`.
///
/// <b>Which subject.</b> The reviewed row's recipient port is `repellers`, an `entity many` port with no declared
/// kind, so this row names its subjects by entity and not by a resource reference: the entities are the level
/// objects of the `fog_repeller` category <see cref="LevelObjectModule"/> owns, and there is no resource kind to
/// register for it. The player-held fog repeller tool is deliberately not this row's subject: a carried item is a
/// player's inventory object, and giving it a level-object identity here would be an identity the level never
/// assigned.
///
/// <b>What the write is.</b> The placed `HeavyFogRepellerGlobalState.AttemptInteract` activation/deactivation
/// requests own the per-peer spheres; the host preserves the state's owner and pose. The radius and lifetime the sphere then
/// runs with are the native configuration the sphere already carries, which the row does not redefine. The
/// readable state is the pair the sphere's own `Update` branches on — `FogRepeller_Sphere.RepellerEnabled` and
/// `FogRepeller_Sphere.CurrentState` — so a request that asks for the state the sphere is already in is answered
/// as such and writes nothing, and a call whose sphere does not read the requested state afterwards is an
/// unknown commit rather than a claimed one.
///
/// <b>What it is not.</b> The sphere's radius, lifetime and overlap are not parameters here: the reviewed graph
/// declares no parameter at all, and the catalog's own note says the radius and lifetime stay the game's. The
/// row is `host` execution — it changes world state — and it carries the same `presentation.fog` permission the
/// two fog rows beside it carry, because it is the same fog surface.
/// </summary>
internal static class FogRepellerContract
{
    /// <summary>This provider's one binding of the row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.presentation.fog_repeller";

    /// <summary>The handler name, which is also the shape key.</summary>
    public const string HandlerName = "gtfo.map.fog_repeller";

    // ---- the state read (forge.query.map.fog_repeller) -----------------------------------------------

    /// <summary>The read of the sphere's own enable flag, which is the state the switch above reads before it
    /// writes. It is declared beside the write because one native member family is spelled once: both halves
    /// resolve a placed sphere through the same `fog_repeller` category key, and splitting them would let two
    /// files disagree about which sphere a reference names.</summary>
    public const string QueryHandlerName = "gtfo.map.fog_repeller_query";

    public static readonly string QueryBindingId = ModuleDefinition.ProviderId + ".binding.query.map.fog_repeller";

    /// <summary>The read's ports: the repeller and the one boolean its own flag answers.</summary>
    public static readonly HandlerShape QueryShape = new HandlerShape().Inputs("repeller").Outputs("enabled");

    /// <summary>The refusal an input that is not a `fog_repeller` level object gets: another kind, or another
    /// category of the same level-object namespace.</summary>
    public const string QueryInputKindCode = "fog-repeller-query-input-kind";
    /// <summary>The refusal a repeller this world does not hold gets — the world epoch moved on, or the sphere is
    /// gone. It is the same word the switch above refuses a target it cannot resolve with.</summary>
    public const string QueryMissingCode = "fog-repeller-query-missing";

    /// <summary>The one native read this row needs: the enable flag of the sphere a reference names, or null when
    /// this world does not hold that repeller. A stopped sphere and a sphere that could not be read are different
    /// facts, so only a real reading is answered.</summary>
    public sealed record FogRepellerReader(Func<EntityReference, bool?> Read);

    /// <summary>The evaluator, keyed by handler name: the reference is refused unless it carries the
    /// `fog_repeller` category's own key, and the answer is the sphere's own flag.</summary>
    public static EvaluatorHandler QueryEvaluator(FogRepellerReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (context, outputs) =>
        {
            if (!context.Inputs.TryGetProperty("repeller", out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null)
                throw new RuntimeContractException("missing-field", "repeller");
            var reference = RuntimeJson.Entity(value);
            if (LevelObjectModule.FogRepellerKey(reference) == null)
                throw new RuntimeContractException(QueryInputKindCode,
                    "This provider reads the state of a placed fog repeller, not of " + reference.Id);
            bool enabled = reader.Read(reference)
                ?? throw new RuntimeContractException(QueryMissingCode, "This world holds no such repeller: " + reference.Id);
            outputs.Boolean(0, enabled);
        };
    }

    /// <summary>The permission the row's own recipient contract names (`recipients.requires`), which is the fog
    /// surface's own permission and not a second one invented for this row.</summary>
    public const string FogPermission = EnvironmentContract.FogPermission;

    /// <summary>The ports the handler really reads and writes. The two execution ports are not part of a shape,
    /// the same way no other action row of this provider declares them, and the reviewed graph declares no
    /// parameter, so none is declared here either.</summary>
    public static readonly HandlerShape Shape = new HandlerShape().Inputs("repellers", "enabled").Outputs("result");
    public static readonly HandlerSlot Repellers = Shape.InputSlot("repellers");
    public static readonly HandlerSlot Enabled = Shape.InputSlot("enabled");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionPresentationFogRepeller.Id, HandlerName, "execute", new[] { FogPermission }, Shape),
        new ProviderBinding("forge.module.gtfo.map.binding.query.map.fog_repeller", Blocks.QueryMapFogRepeller.Id, QueryHandlerName, "evaluate", new[] { "gtfo.map_object.read" }, QueryShape)
    };
}
