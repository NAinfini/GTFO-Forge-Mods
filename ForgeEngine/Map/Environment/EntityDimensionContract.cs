using System;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `forge.query.entity.dimension` row: the dimension an entity is in, read off the entity's own
/// navigation node and never inferred from a position.
///
/// <b>Which entity kinds.</b> Both kinds the reviewed row's subject list carries are answered here. A
/// `gtfo.player` reference resolves through this package's own player identity — the same table the player facts
/// are recorded in — and an `gtfo.enemy` reference resolves through the runtime's published instance door
/// (<see cref="EvaluationContext.EntityInstance"/>): the enemy package owns that kind's instances and registers
/// the lookup, and this package asks the owner instead of reading another package's table. The native
/// `EnemyAgent` a resolved instance is read as is the game's own type, not a type of the enemy package, so no
/// package reference crosses the boundary. A kind outside the reviewed two is refused by name, and an enemy
/// reference the door resolves nothing for is refused by its own name rather than answered from a player's node.
///
/// <b>Why not the address or the position.</b> A map object's address carries the dimension of the zone it was
/// placed in and a position carries coordinates; neither is the node the entity stands on. Deriving the answer
/// from either would answer a dimension the entity is not in after a warp, which is exactly the fact this row
/// exists to observe.
///
/// The row is read-only: it declares no handle, writes no state and publishes no fact.</summary>
internal static class EntityDimensionContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.query.entity.dimension";
    public const string HandlerName = "gtfo.map.entity_dimension";

    /// <summary>The refusal a reference of a kind outside the reviewed subject list gets.</summary>
    public const string KindCode = "dimension-kind-unsupported";
    /// <summary>The refusal a reference of another world gets.</summary>
    public const string StaleCode = "dimension-input-stale";
    /// <summary>The refusal an enemy reference the instance door resolves nothing for gets: the kind's owner
    /// holds no such instance right now, or no owner publishes an instance lookup for it at all. It is a
    /// different fact from a node that did not read, and it is named separately so a caller can tell them
    /// apart.</summary>
    public const string InstanceCode = "dimension-instance-unresolved";
    /// <summary>The refusal an entity whose own navigation node or dimension did not read gets. A dimension of
    /// zero would read exactly like the main dimension, so an unreadable node is refused rather than answered.</summary>
    public const string UnreadableCode = "dimension-unreadable";

    /// <summary>The read's ports: the entity and its dimension, as the integer the game's own `DimensionIndex`
    /// is.</summary>
    public static readonly HandlerShape Shape = new HandlerShape().Inputs("entity").Outputs("dimension");

    /// <summary>The one native read this row needs: the dimension of the navigation node the entity a reference
    /// names stands on, or null when this world cannot read one. The native instance the runtime's instance door
    /// answered with is handed over with the reference — null for a kind whose owner this package reads itself —
    /// so the game-bound half reads the object the owner published and never looks one up of its own. The world
    /// epoch the query session reads travels with it so a reference of another world is refused by name here.</summary>
    public sealed record DimensionReader(Func<EntityReference, object?, long, int?> Read);

    /// <summary>The evaluator, keyed by handler name: the input is required, a kind outside the reviewed list is
    /// refused by name, an enemy whose instance the door does not resolve is refused by its own name, and the
    /// answer is the node's own dimension.</summary>
    public static EvaluatorHandler Evaluator(DimensionReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (context, outputs) =>
        {
            if (!context.Inputs.TryGetProperty("entity", out var value) || value.ValueKind == JsonValueKind.Null)
                throw new RuntimeContractException("missing-field", "entity");
            var reference = RuntimeJson.Entity(value);
            bool enemy = EntityKinds.Is(reference, EntityKinds.Enemy);
            if (!enemy && !EntityKinds.Is(reference, EntityKinds.Player))
                throw new RuntimeContractException(KindCode,
                    "This provider reads the dimension of a player or an enemy, not of " + reference.Id);
            // The world is judged before anything is resolved, so a reference of a level that is gone is refused
            // as stale instead of being reported as an entity no instance answers for.
            if (reference.WorldEpoch != context.Query.WorldEpoch)
                throw new RuntimeContractException(StaleCode, "The entity belongs to another world: " + reference.Id);
            object? instance = enemy
                ? context.EntityInstance(reference)
                    ?? throw new RuntimeContractException(InstanceCode,
                        "No native instance answers for the enemy: " + reference.Id)
                : null;
            int dimension = reader.Read(reference, instance, context.Query.WorldEpoch)
                ?? throw new RuntimeContractException(UnreadableCode,
                    "The entity has no readable navigation node: " + reference.Id);
            outputs.Integer(0, dimension);
        };
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.QueryEntityDimension.Id, HandlerName, "evaluate", Array.Empty<string>(), Shape)
    };
}
