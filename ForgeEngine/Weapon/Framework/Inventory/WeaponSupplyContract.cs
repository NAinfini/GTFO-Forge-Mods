using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The ammunition row the node list's `a-p-ammo` lands on: `forge.action.weapon.ammo_add`, whose signed
/// `amount` adds rounds when positive and takes them away when negative. The row is the generated block table's
/// and is declared with its handler or not at all.
///
/// <b>What the native side really has.</b> The game's own ammunition gift is
/// `PlayerBackpackManager.GiveAmmoToPlayer(SNet_Player, float standardRel, float specialRel, float classRel)`,
/// which is the body the resource pack reaches (`ResourcePackFirstPerson.ApplyPack` → `PlayerAgent.GiveAmmoRel` →
/// this call) and which submits a `pAmmoGive` through the manager's own `SNet_AuthorativeAction&lt;pAmmoGive&gt;`
/// (field +0x20) — the host-authoritative object whose receive half is `ReceiveAmmoGive`, applying the three
/// relative amounts through `PlayerAmmoStorage.PickupAmmo` and, on the pool's owner, bringing its storage sync forward.
/// The machine that owns the pool calls that receive half itself, so the write lands in the same call on host and
/// client alike; only a pool this machine merely mirrors still goes through the host's action and is reported
/// as an unobserved write (failed, commit unknown). Its amounts are relative to a pool's capacity, so an absolute round count is converted against the
/// resource-pack base read before the call and the landing is read back after it. Evidence: `evidence/weapon-supply.json`.
///
/// <b>Where it runs.</b> The row is an `owner` row: the kernel sends it to the machine of the player it names, and
/// the handler there writes that machine's own pool and reads the same pool back, so a client's ammunition is never
/// written through the host's mirrored copy. The gift body clamps only at the pool's cap
/// (`InventorySlotAmmo.AddAmmo` is `Min(AmmoInPack + amount, AmmoMaxCap)`), so a negative amount could drive a
/// pool below zero; the body that does clamp both ends is `PlayerAmmoStorage.UpdateBulletsInPack(AmmoType, int)`,
/// which is what the resource pack itself calls on a backpack this machine owns.
/// Evidence: `evidence/weapon-supply.json`.</summary>
internal static class WeaponSupplyContract
{
    public const string AmmoAddCapability = Blocks.ActionWeaponAmmoAdd.Id;

    public const string AmmoAddBinding = ModuleDefinition.ProviderId + ".binding.action.weapon.ammo_add";

    /// <summary>The handler the native half supplies for the row. The row is declared with its handler or not at
    /// all, so a registration that carries no body never promises the row.</summary>
    public const string AmmoAddHandler = "gtfo.weapon.ammo_add";

    /// <summary>The permission the row writes under: it changes a player's own ammunition pool, which is the one
    /// write this provider performs on a player's state that is not equipment.</summary>
    public const string AmmoWritePermission = "gtfo.weapon.ammo.write";

    /// <summary>The native `Player.AmmoType` members in their declared order. The order is what lets an
    /// `ammo_type` name travel as the native value without a second mapping table. `none` stays in this table
    /// because it is the native spelling of "no pool"; no row offers it, since adding to or reading back from no
    /// pool is refused by name.</summary>
    public static readonly IReadOnlyList<string> AmmoTypes = Array.AsReadOnly(new[]
    {
        "standard", "special", "class", "resource_pack_rel", "none", "current_consumable"
    });

    /// <summary>The two overflow policies the native path can actually honour: clamp through the game's own
    /// pool bounds, or reject before the write when the requested amount would go past the cap or below zero.</summary>
    public static readonly IReadOnlyList<string> OverflowPolicies = Array.AsReadOnly(new[]
    {
        "clamp", "reject"
    });

    /// <summary>The two units an add amount may be written in: `rounds` is a count of bullets, the unit every
    /// pool readback in this package uses, and `raw` is the pool's own ammunition value, which the slot's
    /// `CostOfBullet` converts into rounds. An absent unit is `rounds`, the spelling this row always had.</summary>
    public static readonly IReadOnlyList<string> Units = Array.AsReadOnly(new[]
    {
        "rounds", "raw"
    });

    /// <summary>The handler shape, resolved at registration against the row's own ports: the single recipient the
    /// row names, the amount it asks for, and the result it answers with.</summary>
    public static readonly HandlerShape AddShape = new HandlerShape()
        .Inputs("player", "amount").Outputs("result").Parameters("ammo_type", "overflow_policy", "unit");
    public static readonly HandlerSlot AddPlayerSlot = AddShape.InputSlot("player");
    public static readonly HandlerSlot AddAmountSlot = AddShape.InputSlot("amount");
    public static readonly HandlerSlot AddAmmoTypeSlot = AddShape.ParameterSlot("ammo_type");
    public static readonly HandlerSlot AddOverflowPolicySlot = AddShape.ParameterSlot("overflow_policy");
    public static readonly HandlerSlot AddUnitSlot = AddShape.ParameterSlot("unit");

    /// <summary>The body that was supplied, keyed by the handler name the row names.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers(CommandHandler? add)
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        if (add != null) handlers[AmmoAddHandler] = add;
        return handlers;
    }

    /// <summary>The owner-tier resolver for the row: the session of the player the step names, whose machine owns that
    /// player's ammunition pool. A registration without a resolver carries no answer, and the kernel refuses the step
    /// with `owner-holder`.</summary>
    public static IReadOnlyDictionary<string, Func<EntityReference, string?>> OwnerSessions(Func<EntityReference, string?>? recipients)
    {
        var sessions = new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal);
        if (recipients != null) sessions[AmmoAddCapability] = recipients;
        return sessions;
    }
    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings(CommandHandler? handler) => handler != null ? new ProviderBinding[]
    {
        new ProviderBinding(AmmoAddBinding, AmmoAddCapability, AmmoAddHandler, "execute", new[] { AmmoWritePermission }, AddShape)
    } : Array.Empty<ProviderBinding>();
}
