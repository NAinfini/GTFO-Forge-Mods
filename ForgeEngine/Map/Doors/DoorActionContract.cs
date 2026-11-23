using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The door action bindings this provider implements for the one `gtfo.map_object` namespace. Opening
/// and closing use this provider's `forge.action.map.door_open` row. Damage reuses the shared
/// `forge.action.combat.damage` row: authored interior weak doors have an implemented native receiver, while a
/// security door is refused as non-damageable because its override is a folded empty body on build 20403457.
/// There is no alarm row: a door's alarm is the chained puzzle instance its lock component holds and
/// `forge.action.map.scan_state` already writes that instance kind; a plan reads the instance from
/// `forge.query.map.door_state`'s `puzzle` output. Shapes and bindings live here so the binding and the native
/// handler cannot drift.</summary>
internal static class DoorActionContract
{
    /// <summary>The permission the catalog's own recipient contract names. It is the whole permission a plan
    /// pinning the binding declares, because the row depends on no other binding.</summary>
    public const string OpenPermission = "door.control";

    public const string OpenHandlerName = "gtfo.map_object.door_open";
    public const string DamageHandlerName = "gtfo.map_object.damage";

    /// <summary>Internal binding ids, by capability: this provider's own namespace plus the capability's own
    /// suffix, so the counterpart of a row is readable from either side.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>The row's own ports: the recipient collection, the `operation` that picks opening or closing,
    /// and `bypass_policy`, which only opening reads — the door's own interaction entry is the one close entry the
    /// game has, and it carries neither an occupancy decision nor a force form.</summary>
    public static readonly HandlerShape OpenShape = new HandlerShape()
        .Inputs("doors").Outputs("result").Parameters("operation", "bypass_policy");
    public static readonly HandlerSlot Doors = OpenShape.InputSlot("doors");
    public static readonly HandlerSlot Operation = OpenShape.ParameterSlot("operation");
    public static readonly HandlerSlot BypassPolicy = OpenShape.ParameterSlot("bypass_policy");

    /// <summary>The shared combat-damage ABI, copied exactly from its canonical row. This provider does not
    /// declare that capability; it only supplies the receiver binding for addressed doors.</summary>
    public static readonly HandlerShape DamageShape = new HandlerShape()
        .Inputs("targets", "source", "instigator", "amount", "damage_kind", "limb", "precision_multi", "stagger_multi", "hit_origin")
        .Outputs("result");
    public static readonly HandlerSlot DamageTargets = DamageShape.InputSlot("targets");
    public static readonly HandlerSlot DamageSource = DamageShape.InputSlot("source");
    public static readonly HandlerSlot DamageInstigator = DamageShape.InputSlot("instigator");
    public static readonly HandlerSlot DamageAmount = DamageShape.InputSlot("amount");
    public static readonly HandlerSlot DamageKind = DamageShape.InputSlot("damage_kind");
    public static readonly HandlerSlot DamageLimb = DamageShape.InputSlot("limb");
    public static readonly HandlerSlot DamagePrecision = DamageShape.InputSlot("precision_multi");
    public static readonly HandlerSlot DamageStagger = DamageShape.InputSlot("stagger_multi");
    public static readonly HandlerSlot DamageHitOrigin = DamageShape.InputSlot("hit_origin");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.map.binding.action.map.door_open", Blocks.ActionMapDoorOpen.Id, OpenHandlerName, "execute", new[] { OpenPermission }, OpenShape),
        new ProviderBinding(Binding(Blocks.ActionCombatDamage.Id), Blocks.ActionCombatDamage.Id, DamageHandlerName, "execute",
            new[] { OpenPermission }, DamageShape)
    };
}
