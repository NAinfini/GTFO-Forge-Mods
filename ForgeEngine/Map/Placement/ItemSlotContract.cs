using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The two item-slot rows this provider implements: `forge.trigger.interaction.item_placed`, a carried
/// item put into the host that accepts it, and `forge.trigger.interaction.item_removed`, an inserted item taken back
/// out of its host. Their output ports — `next`, `host`, `item` and `actor`, all required — are the reviewed
/// primitives' own.
///
/// Both are published by the host from the game's own committed entries: the insertion and the removal the game
/// performs on the master (`CarryItemInteractionUtils.TryInsertItem` / `TryPickupRemovedItem`). The host is the
/// insertion target itself, addressed in the one `gtfo.map_object` namespace under its own `host` category; the item
/// is the level item, addressed in the level-object namespace exactly as the pickup facts address it, so a plan that
/// saw an item picked up names the same entity when it is placed or taken out.</summary>
internal static class ItemSlotContract
{
    /// <summary>The map-object category of the host an item is put into or taken out of: the socket, crate or
    /// power-cell receptacle the game's own insertion target is.</summary>
    public const string HostCategory = "host";

    /// <summary>The fact kinds the two rows publish, which are also their observe bindings' handler suffixes.</summary>
    public const string PlacedFact = "item_placed";
    public const string RemovedFact = "item_removed";

    public static string PlacedBinding => BindingOf(Blocks.TriggerInteractionItemPlaced.Id);
    public static string RemovedBinding => BindingOf(Blocks.TriggerInteractionItemRemoved.Id);

    /// <summary>The event id of one placement or removal: the map-object namespace, the fact kind, the world epoch,
    /// the host address and this world's own item-slot sequence. Two insertions into one host are two facts, so the
    /// sequence and not the host state is what makes the id unique.</summary>
    public static string EventId(string fact, long worldEpoch, string hostAddress, long sequence)
        => EntityKinds.MapObject + "." + fact + ":" + Number(worldEpoch) + ":" + hostAddress + ":"
            + Number(sequence);

    private static string BindingOf(string capability)
        => ModuleDefinition.ProviderId + ".binding." + capability["forge.trigger.".Length..];

    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.item_placed", Blocks.TriggerInteractionItemPlaced.Id, "gtfo.map_object.item_placed", "observe", new[] { "gtfo.map_object.read" }),
        new ProviderBinding("forge.module.gtfo.map.binding.interaction.item_removed", Blocks.TriggerInteractionItemRemoved.Id, "gtfo.map_object.item_removed", "observe", new[] { "gtfo.map_object.read" })
    };
}
