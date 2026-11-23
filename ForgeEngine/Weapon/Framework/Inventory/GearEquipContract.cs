using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>
/// The `forge.action.inventory.equip_gear` row: put one named gear into the slot its category belongs to, on the
/// machine of the player who receives it.
///
/// The row is the `owner` tier. Equipping goes through the game's own `PlayerBackpackManager.EquipLocalGear`, which
/// is a local-player entry — it builds the gear in the backpack of whoever calls it — so the host decides when and
/// for whom (the `recipient` port) and the recipient's own client performs the call. The kernel resolves that client
/// from <see cref="OwnerSessions"/>, and a handler that finds itself on any other machine refuses with
/// <see cref="ResultCode.OwnerNotAddressed"/> instead of writing a different player's backpack.
///
/// What the row asks for is exactly one of three sources: an authored or vanilla weapon (the `weapon` port), a tool
/// (the `tool` port), or the gear this slot held when the level started (the `departure_slot` parameter). The slot a
/// weapon or tool goes into is the one its own base item declares; only `departure_slot` names a slot, because a
/// departure gear has no resource to read one from.
///
/// The assets the game's assembly reads (the muzzle flash prefab) are loaded first for any gear that lacks them, and the
/// request is sent only when they are there; the same wait defers the row.
///
/// The row answers when the slot shows the gear, not when the request returns: the game builds the gear after
/// <c>EquipLocalGear</c> returns, so the handler defers its result (<see cref="CommandContext.DeferOwnerResult"/>) and
/// the host's graph continues through `next` only once the row is settled — confirmed, or unconfirmed after
/// <see cref="ConfirmSeconds"/>. One request per player and slot is open at a time.
///
/// The names of every port, parameter and result column are the reviewed Primitive's own
/// (`Docs/forge-contract/primitive/primitives.json`); the row is the generated block table's and is declared only
/// when its handler is supplied (<see cref="ModuleDefinition.Create"/>).
/// </summary>
internal static class GearEquipContract
{
    public const string Capability = Blocks.ActionInventoryEquipGear.Id;
    public const string Binding = ModuleDefinition.ProviderId + ".binding.inventory_equip_gear";
    public const string Handler = "gtfo.inventory.equip_gear";
    /// <summary>Equipping changes the same object class a give does — a player's own backpack — so it carries the
    /// same permission.</summary>
    public const string Permission = InventoryActionContract.WritePermission;

    /// <summary>The resource kinds the two gear ports carry. Both address `PlayerOfflineGear` records; the kind only
    /// says which workshop the author picked the work from.</summary>
    public const string WeaponKind = "weapon";
    public const string ToolKind = "tool";

    public const string StandardSlot = "standard";
    public const string SpecialSlot = "special";
    public const string MeleeSlot = "melee";
    public const string ToolSlot = "tool";

    /// <summary>How long the holder waits for the slot to show the requested gear before it settles the row as
    /// unconfirmed. The game assembles gear after the request returns, so the row is answered when the slot shows
    /// the gear or when this window ends, never in the call itself.</summary>
    public const double ConfirmSeconds = 2.5;

    /// <summary>How long the holder waits for the game's shard loader to bring in the assets the gear's assembly reads
    /// (its muzzle flash prefab) before it ends the row as <see cref="ResultCode.EquipGearAssetsLoadTimeout"/>. The wait comes before the
    /// request is sent, so nothing has been written when it runs out.</summary>
    public const double AssetSeconds = 4;

    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("recipient", "weapon", "tool").Outputs("result").Parameters("departure_slot");
    public static readonly HandlerSlot RecipientSlot = Shape.InputSlot("recipient");
    public static readonly HandlerSlot WeaponSlot = Shape.InputSlot("weapon");
    public static readonly HandlerSlot ToolInputSlot = Shape.InputSlot("tool");
    public static readonly HandlerSlot DepartureSlot = Shape.ParameterSlot("departure_slot");

    public static IReadOnlyDictionary<string, CommandHandler> Handlers(CommandHandler? handler)
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        if (handler != null) handlers[Handler] = handler;
        return handlers;
    }

    public static ProviderBinding[] DeclareBindings(CommandHandler? handler) => handler != null ? new[]
    {
        new ProviderBinding(Binding, Capability, Handler, "execute", new[] { Permission }, Shape)
    } : Array.Empty<ProviderBinding>();

    /// <summary>The owner-tier resolver table for this row: the session of the player the step names. A registration
    /// without a resolver carries no answer, and the kernel refuses the step with `owner-holder`.</summary>
    public static IReadOnlyDictionary<string, Func<EntityReference, string?>> OwnerSessions(Func<EntityReference, string?>? recipients)
    {
        var sessions = new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal);
        if (recipients != null) sessions[Capability] = recipients;
        return sessions;
    }

    /// <summary>The slot a `departure_slot` member names, or null for a member this row does not carry.</summary>
    public static GearSlot? SlotOf(string? member) => member switch
    {
        StandardSlot => GearSlot.Standard,
        SpecialSlot => GearSlot.Special,
        MeleeSlot => GearSlot.Melee,
        ToolSlot => GearSlot.Tool,
        _ => null
    };

    public static string NameOf(GearSlot slot) => slot switch
    {
        GearSlot.Standard => StandardSlot,
        GearSlot.Special => SpecialSlot,
        GearSlot.Melee => MeleeSlot,
        _ => ToolSlot
    };
}
