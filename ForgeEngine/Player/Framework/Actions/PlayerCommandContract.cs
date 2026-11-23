using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The player-domain actions this provider executes: damage, revive and down. Three rows, one native
/// write entry each, all on the host, because a player's life is the host's to change and every one of these
/// entries is a replicated one:
/// <list type="bullet">
/// <item>`forge.action.combat.damage` for `gtfo.player` submits the receiver's own damage entry, which is the
/// packet-sending half of the game's damage path, so the amount that lands is the receiver's answer and not what
/// was asked for. The capability and its result row belong to the combat contract; this file declares only the
/// binding, the shape and the permission.</item>
/// <item>`forge.action.combat.revive` submits the game's host-authoritative replicated revive action, with an
/// explicit rescuer and recipient. Native rules own the restored health; this action creates no respawn or warp.</item>
/// <item>`forge.action.player.down` is this provider's own row: it submits the receiver's `SendSetDead`, whose one
/// argument is whether the downed life may still be revived. That argument is the row's one structural parameter,
/// so the two behaviours are two authored choices rather than one implicit one.</item>
/// </list>
///
/// Every handler here refuses before it writes: authority, recipient kind, staleness, amount bounds and the
/// structural parameters are all checked once, and a request whose native entry cannot express what it asked for is
/// refused by name instead of being approximated. Each recipient gets one result row, written through the action
/// skeleton (<c>CommandContext.EachTarget</c>), whose conclusion is the one aggregation rule's.</summary>
internal static class PlayerCommandContract
{
    public const string ProviderId = "forge.module.gtfo.player";


    public const string DamageHandlerName = "gtfo.player.damage";
    public const string ReviveHandlerName = "gtfo.player.revive";
    public const string DownHandlerName = "gtfo.player.down";

    /// <summary>The permission a write to a player's life carries, mirroring the enemy provider's own namespaced
    /// health permission. It is the binding's own requirement; the catalog's recipient contract names the
    /// authoring-side permission separately and neither is derived from the other.</summary>
    public const string HealthWritePermission = "gtfo.player.health.write";

    /// <summary>This provider's binding id for a capability: the capability's own suffix under the Player provider.</summary>
    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>The one shape of each handler, resolved at registration against the capability it implements: the
    /// damage and revive rows are the combat contract's own port lists, and the down row's list is this
    /// declaration's. The native half reads these shapes rather than describing a second layout.</summary>
    internal static readonly HandlerShape DamageShape = new HandlerShape()
        .Inputs("targets", "source", "instigator", "amount", "damage_kind", "limb", "precision_multi", "stagger_multi", "hit_origin")
        .Outputs("result");
    internal static readonly HandlerShape ReviveShape = new HandlerShape()
        .Inputs("targets", "source").Outputs("result");
    internal static readonly HandlerShape DownShape = new HandlerShape()
        .Inputs("targets").Outputs("result").Parameters("revive_policy");

    public static readonly HandlerSlot DamageTargetsSlot = DamageShape.InputSlot("targets");
    public static readonly HandlerSlot DamageSourceSlot = DamageShape.InputSlot("source");
    public static readonly HandlerSlot DamageInstigatorSlot = DamageShape.InputSlot("instigator");
    public static readonly HandlerSlot DamageAmountSlot = DamageShape.InputSlot("amount");
    public static readonly HandlerSlot DamageKindSlot = DamageShape.InputSlot("damage_kind");
    public static readonly HandlerSlot DamageLimbSlot = DamageShape.InputSlot("limb");
    public static readonly HandlerSlot DamagePrecisionSlot = DamageShape.InputSlot("precision_multi");
    public static readonly HandlerSlot DamageStaggerSlot = DamageShape.InputSlot("stagger_multi");
    public static readonly HandlerSlot DamageHitOriginSlot = DamageShape.InputSlot("hit_origin");
    public static readonly HandlerSlot ReviveTargetsSlot = ReviveShape.InputSlot("targets");
    public static readonly HandlerSlot ReviveSourceSlot = ReviveShape.InputSlot("source");
    public static readonly HandlerSlot DownTargetsSlot = DownShape.InputSlot("targets");
    public static readonly HandlerSlot DownRevivePolicySlot = DownShape.ParameterSlot("revive_policy");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.player.binding.action.combat.damage", Blocks.ActionCombatDamage.Id, DamageHandlerName, "execute", new[] { HealthWritePermission }, DamageShape),
        new ProviderBinding("forge.module.gtfo.player.binding.action.combat.revive", Blocks.ActionCombatRevive.Id, ReviveHandlerName, "execute", new[] { HealthWritePermission }, ReviveShape),
        new ProviderBinding("forge.module.gtfo.player.binding.action.player.down", Blocks.ActionPlayerDown.Id, DownHandlerName, "execute", new[] { HealthWritePermission }, DownShape)
    };
}
