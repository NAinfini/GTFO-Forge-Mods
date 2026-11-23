using System;
using System.Text.Json;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The two combat actions this provider implements for `gtfo.player`: a sourced modifier applied to a
/// player's own attribute, and canonical effect cancellation for handles this provider owns. Both are canonical
/// `forge.action.combat.*` capabilities owned by, and declared by, the combat contract module; this package
/// declares its own binding rows and nothing else, and reads the two rows back from that module rather than
/// restating their text.
///
/// The ruled shape follows what the native entry can express: each `attribute` is an `agent_modifier` member
/// instead of a free string, `operation` keeps only `set`/`add`/`subtract` (the native entry takes a value and a
/// decay rate and has no multiplication, minimum or maximum form), and there is no `priority` (the native
/// modification table has no ordering). The `(attribute, amount)` pair is the row's `modifiers` port group, so one
/// command writes one or more members under one effect handle — a movement preset is speed plus acceleration —
/// and the step's `effect` duration restores every member it wrote. `agent_modifier.none` is a no-op member
/// rather than an applicable attribute; the handler refuses it by name instead of writing a modification that
/// contributes nothing.
///
/// Only players are recipients. The native modification table is keyed by `Agent`, and this package is the owner
/// of `gtfo.player`; an enemy's attribute is its own profile's business, so a target of any other kind is refused
/// with `unsupported-recipient` rather than resolved through some other provider's table.</summary>
internal static class AgentModifierContract
{
    public const string ApplyCapabilityId = CombatContracts.AttributeApplyCapabilityId;
    public const string CancelCapabilityId = CombatContracts.EffectCancelCapabilityId;
    public const string ApplyHandlerName = "gtfo.player.attribute_apply";
    public const string CancelHandlerName = "gtfo.player.effect_cancel";
    /// <summary>The permission both rows' catalog recipient contracts name.</summary>
    public const string Permission = "attribute.modify";

    /// <summary>The catalog's own domain list for the apply row, in its own order: map and room behaviors apply
    /// movement presets as much as equipment does.</summary>
    public static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player" };

    /// <summary>The apply row's port group and its count parameter: one `(attribute, amount)` tuple per member,
    /// expanded per plan into `attribute_i`/`amount_i`.</summary>
    public const string ModifierGroup = "modifiers";
    public const string ModifierCountParameter = "modifier_count";

    /// <summary>The kernel's effect cancellation capability.</summary>
    public static readonly JsonElement CancelCapability = CombatContracts.EffectCancelCapability;

    /// <summary>The fixed inputs and parameters keep resolved tokens while the `modifiers` group records its
    /// resolved base and stride. Duration belongs to the kernel effect lifecycle, not this handler input.</summary>
    public static readonly HandlerShape ApplyShape = new HandlerShape()
        .Inputs("targets", "source", "cap").Parameters("operation", ModifierCountParameter);

    public static readonly HandlerSlot ApplyTargetsSlot = ApplyShape.InputSlot("targets");
    public static readonly HandlerSlot ApplySourceSlot = ApplyShape.InputSlot("source");
    public static readonly HandlerSlot ApplyCapSlot = ApplyShape.InputSlot("cap");
    public static readonly HandlerSlot ApplyOperationSlot = ApplyShape.ParameterSlot("operation");
    public static readonly HandlerSlot ApplyAttributeSlot = ApplyShape.InputGroupSlot(ModifierGroup, "attribute");
    public static readonly HandlerSlot ApplyAmountSlot = ApplyShape.InputGroupSlot(ModifierGroup, "amount");

    /// <summary>The canonical effect-cancel handler consumes effect handles and reports both result rows and the
    /// number of native modifier entries actually cancelled.</summary>
    public static readonly HandlerShape CancelShape = new HandlerShape()
        .Inputs("handles").Outputs("result", "cancelled");

    public static readonly HandlerSlot CancelHandlesSlot = CancelShape.InputSlot("handles");

    /// <summary>This provider's binding id for a row: the capability's own suffix under the Player provider, so the
    /// counterpart of a binding is readable from either side.</summary>
    public static string Binding(string capabilityId) => capabilityId switch
    {
        ApplyCapabilityId => ModuleDefinition.ProviderId + ".binding.attribute_apply",
        CancelCapabilityId => ModuleDefinition.ProviderId + ".binding.effect_cancel",
        _ => throw new ArgumentOutOfRangeException(nameof(capabilityId), capabilityId, "Unknown modifier capability")
    };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.player.binding.attribute_apply", ApplyCapabilityId, ApplyHandlerName, "execute", new[] { Permission }, ApplyShape),
        new ProviderBinding("forge.module.gtfo.player.binding.effect_cancel", CancelCapabilityId, CancelHandlerName, "execute", Array.Empty<string>(), CancelShape)
    };
}
