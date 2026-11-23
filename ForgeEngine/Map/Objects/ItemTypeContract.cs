using System;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The `forge.query.map.item_type` row: which pickup class a level item is and its game item number
/// (`ItemDataBlock.persistentID`). The item is the native instance the level-object kind publishes for the reference;
/// the class is read off that instance and never off the address. Read-only: no handle, no state, no fact.</summary>
internal static class ItemTypeContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.query.map.item_type";
    public const string HandlerName = "gtfo.map.item_type";

    /// <summary>A reference of another entity kind.</summary>
    public const string KindCode = "item-type-kind-unsupported";
    /// <summary>A reference of another world.</summary>
    public const string StaleCode = "item-type-input-stale";
    /// <summary>No live item answers for the reference, or its data block did not read.</summary>
    public const string UnreadableCode = "item-type-unreadable";

    public static readonly HandlerShape Shape = new HandlerShape().Inputs("item").Outputs("kind", "item_id");

    /// <summary>One reading: the member index in `item_kind` and the item number, or null when nothing reads.</summary>
    public readonly record struct ItemType(int Kind, uint ItemId);

    public sealed record ItemTypeReader(Func<object, ItemType?> Read);

    public static EvaluatorHandler Evaluator(ItemTypeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (context, outputs) =>
        {
            if (!context.Inputs.TryGetProperty("item", out var value) || value.ValueKind == JsonValueKind.Null)
                throw new RuntimeContractException("missing-field", "item");
            var reference = RuntimeJson.Entity(value);
            if (!EntityKinds.Is(reference, EntityKinds.LevelObject))
                throw new RuntimeContractException(KindCode, "This provider reads the type of a level item, not of " + reference.Id);
            if (reference.WorldEpoch != context.Query.WorldEpoch)
                throw new RuntimeContractException(StaleCode, "The item belongs to another world: " + reference.Id);
            var type = context.EntityInstance(reference) is { } instance ? reader.Read(instance) : null;
            if (type is not { } read)
                throw new RuntimeContractException(UnreadableCode, "No live item with a readable type answers for: " + reference.Id);
            outputs.Enum(0, read.Kind);
            outputs.Integer(1, read.ItemId);
        };
    }

    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.QueryMapItemType.Id, HandlerName, "evaluate", Array.Empty<string>(), Shape)
    };
}
