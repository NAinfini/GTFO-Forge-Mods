using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The one row that puts a native item into the world: `forge.action.map.item_spawn`. Its write is the game's own
/// spawn entry, `ItemReplicationManager.SpawnItem(persistentID, ItemMode, position, rotation, doSetup, pItemData,
/// parent)`, and the row's `mode` parameter is that entry's own `ItemMode` argument — the four members the
/// reviewed graph declares are the four the enum has, so nothing is mapped, narrowed or invented.
///
/// The item itself is a resource of the `item` kind, which this provider answers from the game's own item data
/// blocks: a plan naming an item this install does not carry resolves to nothing rather than to the nearest block.
/// The kind is enumerated and resolved by the native half, because only it can read the loaded blocks.
/// </summary>
internal static class ItemSpawnContract
{
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.item_spawn";
    public const string HandlerName = "gtfo.map.item_spawn";

    /// <summary>The permission the row's own recipient contract names.</summary>
    public const string SpawnPermission = "spawn.item";

    /// <summary>The resource kind the row's recipient port declares. It is the shared vocabulary's own `item`,
    /// and its instances are this install's item data blocks, keyed by the block's own persistent id.</summary>
    public const string ItemResourceKind = "item";

    /// <summary>The four members the row's `mode` parameter declares, in the catalog's order. Each one is the
    /// `ItemMode` member of the same name in the game's own enum.</summary>
    public static readonly string[] Modes = { "first_person", "third_person", "pickup", "instance" };

    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("item", "position", "rotation").Outputs("result").Parameters("mode");
    public static readonly HandlerSlot Item = Shape.InputSlot("item");
    public static readonly HandlerSlot Position = Shape.InputSlot("position");
    public static readonly HandlerSlot Rotation = Shape.InputSlot("rotation");
    public static readonly HandlerSlot Mode = Shape.ParameterSlot("mode");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionMapItemSpawn.Id, HandlerName, "execute", new[] { SpawnPermission }, Shape)
    };
}
