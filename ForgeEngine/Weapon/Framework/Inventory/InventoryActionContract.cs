using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The `forge.action.inventory.*` catalog rows this package is responsible for, and the exact point each
/// one stops at. `give` has native host entry points and a row on the node list, so it is declared and bound here;
/// the other four have no host entry point at all. This file says each of those by name rather than
/// declaring a row nothing answers. The catalog's `drop` row is not among them either: the node list carries no
/// drop node, so no capability and no binding may exist for it, and the body that used to answer for it is gone
/// with them rather than left behind as an unreachable implementation.
///
/// The split is decided by the native evidence (`evidence/inventory-actions.json`), never by the catalog text:
///
/// | row | native truth | verdict |
/// |---|---|---|
/// | `give`, positive quantity | `PlayerBackpackManager.MasterAddItem(pItemData_WithOwner, SNet_Player)` sends the add-item packet and applies it locally | implementable, needs the item-resource binding |
/// | `give`, negative quantity | `PlayerBackpackManager.TryMasterRemovePocketItemWithID(uint, SNet_Player)` sends the remove-item packet and applies it locally | implementable, needs the item-resource binding |
/// | `pickup` | no host entry point takes a world item into a backpack: `LG_PickupItem_Sync` is the synced state machine, and this build has no world-item entity kind to name a source with | not implemented |
/// | `equip` | `PlayerInventoryLocal.CheckAndWield` / `DoWieldItem` are the input-driven local wield path, gated on `m_allowedToWieldItem` and the wield delay; `PlayerBackpackManager.EquipLocalGear` builds a gear from a `GearIDRange` instead of switching to held equipment | not implemented |
/// | `transfer` | no atomic two-backpack write exists: a remove-then-add pair is two packets, and a failure between them leaves the amount nowhere | not implemented, depends on a transaction |
/// | `stack_set` | `PlayerBackpack.AddPocketItem` / `RemovePocketItem` are local list edits with no packet in their bodies, and there is no host entry point that sets a count | not implemented |
///
/// The `give` row is the generated block table's and this file carries its binding; both are declared only when
/// the handler is supplied (<see cref="ModuleDefinition.Create"/>), because a declared action nothing answers is a
/// promise this package does not keep.</summary>
internal static class InventoryActionContract
{
    /// <summary>The one capability this package declares. `drop` is deliberately not declared: the node list has no
    /// drop node, so nothing declares that capability and no binding row may exist for it (ruling 110.5). Taking
    /// items away is the same row with a negative quantity.</summary>
    public const string GiveCapability = Blocks.ActionInventoryGive.Id;

    public const string GiveBinding = ModuleDefinition.ProviderId + ".binding.inventory_give";

    public const string GiveHandler = "gtfo.inventory.give";

    /// <summary>The permission the write carries, in both directions: adding and taking away act on the same object
    /// class — a player's own backpack — and a plan that may put an item into a backpack may also spend one out of
    /// it.</summary>
    public const string WritePermission = "gtfo.inventory.write";

    /// <summary>The handler shape, resolved at registration against the capability's own ports. Every
    /// name here is a port the handler really reads; `viewers`, `marker`, `duration` and friends do not exist on
    /// these rows, and the give row no longer carries `capacity_policy` — no native capacity choice backs
    /// reject/clamp/drop (`PlayerBackpack.Slots` has one item per slot and the game's own add body neither tests a
    /// capacity nor clamps one), so the parameter is not declared and nothing here reads it.</summary>
    public static readonly HandlerShape GiveShape = new HandlerShape()
        .Inputs("inventories", "item", "quantity", "charges").Outputs("result");
    public static readonly HandlerSlot GiveInventoriesSlot = GiveShape.InputSlot("inventories");
    public static readonly HandlerSlot GiveItemSlot = GiveShape.InputSlot("item");
    public static readonly HandlerSlot GiveQuantitySlot = GiveShape.InputSlot("quantity");
    public static readonly HandlerSlot GiveChargesSlot = GiveShape.InputSlot("charges");

    /// <summary>The execute rows bound to the native bodies themselves. The bodies are taken as handlers rather
    /// than as the adapter that owns them, so this file stays game-independent and the native assembly decides
    /// which object answers: a registration can only declare a row whose handler it was given, and a missing one
    /// leaves the row out instead of promising a body nothing supplies.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers(CommandHandler? give)
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        if (give != null) handlers[GiveHandler] = give;
        return handlers;
    }

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings(CommandHandler? handler) => handler != null ? new ProviderBinding[]
    {
        new ProviderBinding(GiveBinding, GiveCapability, GiveHandler, "execute", new[] { WritePermission }, GiveShape)
    } : Array.Empty<ProviderBinding>();
}
