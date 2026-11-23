using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The row that takes a spawned item out of the world again: `forge.action.map.item_remove`. It is the counterpart of
/// `forge.action.map.item_spawn`, and its recipients are the level-object references that row (or any other item
/// spawn the game made) produced, so a reset can clear what is left of an item before spawning it afresh.
///
/// The write is the game's own despawn entry, `ItemReplicationManager.DeSpawn`; the host runs it and every
/// peer drops the instance. An item the level built itself (a pickup with
/// no dynamic replicator) has no vanilla despawn and is refused by name instead of being hidden or moved. A reference
/// whose item is already gone — picked up, destroyed, or from another world — is refused per target; it is never
/// replaced by another item.
/// </summary>
internal static class ItemRemoveContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.item_remove";
    public const string HandlerName = "gtfo.map.item_remove";

    /// <summary>The permission the row's recipient contract names, in this provider's own namespace.</summary>
    public const string RemovePermission = ModuleDefinition.ProviderId + ".item.remove";

    public static readonly HandlerShape Shape = new HandlerShape().Inputs("items").Outputs("result");
    public static readonly HandlerSlot Items = Shape.InputSlot("items");

    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionMapItemRemove.Id, HandlerName, "execute", new[] { RemovePermission }, Shape)
    };
}
