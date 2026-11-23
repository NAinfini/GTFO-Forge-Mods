using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The game-independent half of `forge.action.combat.foaming` as this provider carries it: the binding
/// row, the handler's port shape and the support row the provider's execute handler is registered with. The
/// integration step picks them up from here, so the registry spelling lives in one place instead of being
/// repeated inside `EnemyModule`.
///
/// The capability row itself is **not** declared here. Its recipients are enemies and doors, so the enemy package
/// and the map package each bind it for the kind they own; a capability has exactly one owner, and that owner is
/// the shared combat contract (<see cref="CombatContracts"/>, provider `forge.contract.combat`), which the host
/// registers before any package. This file therefore carries the binding and its shape and reads the ports it must
/// answer from the registered row, so the shape a plan is resolved against is the row the host publishes.</summary>
internal static class GlueContract
{
    /// <summary>The provider every row here belongs to; the same string `EnemyModule.ProviderId` carries.</summary>
    internal const string ProviderId = "forge.module.gtfo.enemy";

    /// <summary>`forge.action.combat.foaming`: put the game's own foam on an enemy.</summary>
    internal const string FoamingBinding = ProviderId + ".binding.foaming";
    internal const string FoamingHandler = "gtfo.enemy.foaming";

    /// <summary>The handler's own port set, resolved once at registration against the host's capability row.
    /// `targets` is the recipient collection, whose whole declared width separates `source` from the rest.</summary>
    internal static readonly HandlerShape FoamingPorts = new HandlerShape()
        .Inputs("targets", "source", "volume", "strength", "duration", "bubble_amount").Outputs("result", "foam").Parameters();
    internal static readonly HandlerSlot TargetsSlot = FoamingPorts.InputSlot("targets");
    internal static readonly HandlerSlot SourceSlot = FoamingPorts.InputSlot("source");
    internal static readonly HandlerSlot VolumeSlot = FoamingPorts.InputSlot("volume");
    internal static readonly HandlerSlot StrengthSlot = FoamingPorts.InputSlot("strength");
    internal static readonly HandlerSlot DurationSlot = FoamingPorts.InputSlot("duration");
    internal static readonly HandlerSlot BubbleAmountSlot = FoamingPorts.InputSlot("bubble_amount");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(FoamingBinding, Blocks.ActionCombatFoaming.Id, FoamingHandler, "execute", new[] { "gtfo.enemy.glue.write" }, FoamingPorts)
    };
}
