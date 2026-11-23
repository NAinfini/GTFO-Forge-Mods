using System;
using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The one canonical combat row this provider implements for `gtfo.player`: `forge.action.combat.heal`
/// is declared and owned by `forge.contract.combat`, and this package binds it to its own handler exactly as the
/// enemy package binds the same capability to its own. The capability is the authority for the ports, the result
/// schema and the codes; nothing here restates them, and no second capability is invented for a player.
///
/// The row lives in the game-independent assembly because the runtime module, the manifest and the website read
/// it there; the handler and the native write path are the native assembly's `PlayerHealthAction` and
/// `PlayerHealthReceiver`, which answer through this same shape. A registration only declares what it answers,
/// which is why this file is a declaration and not part of the identity table.</summary>
internal static class PlayerHealthContract
{
    public const string CapabilityId = Blocks.ActionCombatHeal.Id;
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.heal";
    public const string HandlerName = "gtfo.player.heal";

    /// <summary>The one shape of this handler, resolved at registration against the canonical capability: the
    /// recipient collection, the required source reference, the requested amount and the optional ceiling, and
    /// the one structural overheal policy. It is declared here rather than by the registration site, so the
    /// native half cannot quietly describe a different port layout.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "source", "amount", "cap").Outputs("result").Parameters("overheal_policy", "cancel_regen");

    public static readonly HandlerSlot TargetsSlot = Shape.InputSlot("targets");
    public static readonly HandlerSlot SourceSlot = Shape.InputSlot("source");
    public static readonly HandlerSlot AmountSlot = Shape.InputSlot("amount");
    public static readonly HandlerSlot CapSlot = Shape.InputSlot("cap");
    public static readonly HandlerSlot OverhealPolicySlot = Shape.ParameterSlot("overheal_policy");
    public static readonly HandlerSlot CancelRegenSlot = Shape.ParameterSlot("cancel_regen");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(BindingId, Blocks.ActionCombatHeal.Id, HandlerName, "execute", Array.Empty<string>(), Shape)
    };
}
