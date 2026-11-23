using System;
using ForgeEngine.Framework;

namespace ForgePlayer;

internal static class PlayerBackpackContract
{
    internal const string Changed = Blocks.TriggerPlayerBackpackSlotChanged.Id;
    internal const string QueryHandler = "gtfo.player.backpack_slot";
    internal const string ChangedHandler = "gtfo.player.backpack_slot_changed";
    internal const string QueryBinding = PlayerStateContract.ProviderId + ".binding.query.player.backpack_slot";
    internal const string ChangedBinding = PlayerStateContract.ProviderId + ".binding.trigger.player.backpack_slot_changed";
    internal static readonly HandlerShape QueryShape = new HandlerShape().Inputs("player", "slot")
        .Outputs("occupied", "item_id", "gear_id", "status", "loaded");
    internal static ProviderBinding[] Bindings() => new[]
    {
        new ProviderBinding(QueryBinding, Blocks.QueryPlayerBackpackSlot.Id, QueryHandler, "observe", Array.Empty<string>(), QueryShape),
        new ProviderBinding(ChangedBinding, Blocks.TriggerPlayerBackpackSlotChanged.Id, ChangedHandler, "observe", Array.Empty<string>())
    };
}
