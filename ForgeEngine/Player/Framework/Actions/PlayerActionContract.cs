using ForgeEngine.Framework;

namespace ForgePlayer;

/// <summary>The two player-domain actions this provider implements, one row each: what the catalog declares and
/// what the native half answers through. Both are host actions that end in a game entry point which replicates
/// by itself — a warp request that the session sends to every machine, and an infection change submitted with the
/// sync flag — so this package never writes a field a client would have to guess at.
///
/// The capability rows, their ports, parameters and result schemas come from the block table; this file holds
/// the bindings, the handler shapes and the permissions.
///
/// The remaining three player rows of the catalog are deliberately not declared here, because no binding of this
/// build could answer them end to end:
/// - `forge.action.player.respawn` has no native request path at all (`PlayerAgent.Alive` is a flag, not a life
///   allocation, and nothing spawns or replicates a replacement `PlayerAgent`);
/// - `forge.action.player.input_restrict` declares an `effect` handle and a per-action gate, while the only
///   native entry (`PlayerAgent.RequestToggleControlsEnabled`) toggles every control at once and mints nothing;
/// - `forge.action.player.checkpoint_inventory` consumes a `transaction` snapshot handle, which is the
///   transaction/reservation store the runtime does not have yet.
/// `ForgePlayer/evidence/player-actions.json` carries the native basis for each conclusion. A movement preset is
/// not a player row: it is `forge.action.combat.attribute_apply` writing MovementSpeed and MovementAcceleration
/// in one command (<see cref="AgentModifierContract"/>).</summary>
internal static class PlayerActionContract
{
    public const string TeleportHandlerName = "gtfo.player.teleport";
    /// <summary>The permission the catalog's own recipient contract names for the teleport row.</summary>
    public const string TeleportPermission = "player.teleport";

    public const string InfectionHandlerName = "gtfo.player.infection";
    /// <summary>The permission the catalog's own recipient contract names for the infection row.</summary>
    public const string InfectionPermission = "infection.modify";

    /// <summary>This provider's binding id for a row: the capability's own suffix under the Player provider, so the
    /// counterpart of a binding is readable from either side.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.action.player.".Length..];

    /// <summary>The one shape of the teleport handler, resolved once at registration against the capability: the
    /// recipient collection, the destination and its facing, the optional area the author constrained it to and the
    /// optional dimension the destination is in. The native half reads this shape rather than describing a second one.</summary>
    public static readonly HandlerShape TeleportShape = new HandlerShape()
        .Inputs("players", "destination", "rotation", "area", "dimension").Outputs("result");

    public static readonly HandlerSlot TeleportPlayersSlot = TeleportShape.InputSlot("players");
    public static readonly HandlerSlot TeleportDestinationSlot = TeleportShape.InputSlot("destination");
    public static readonly HandlerSlot TeleportRotationSlot = TeleportShape.InputSlot("rotation");
    public static readonly HandlerSlot TeleportAreaSlot = TeleportShape.InputSlot("area");
    public static readonly HandlerSlot TeleportDimensionSlot = TeleportShape.InputSlot("dimension");

    /// <summary>The Primitive's infection shape: explicit recipients, amount in 0–1 units, an optional
    /// pre-native-write ceiling, the set/add/subtract operation and the optional effect switch. There is no source:
    /// the native `pInfection` value carries none. Add bounds its submitted delta by max(0, cap-current) without
    /// lowering an already higher current value; set/subtract bound the submitted absolute request. Omitting cap adds
    /// no ceiling. Native ModifyInfection applies the player's current InfectionResistance to every positive amount
    /// and Clamp01; an add meets it, a set/subtract is written divided by it so the absolute value lands as asked.
    /// Persistent resistance is authored by the existing player attribute action.</summary>
    public static readonly HandlerShape InfectionShape = new HandlerShape()
        .Inputs("targets", "amount", "cap").Outputs("result").Parameters("operation", "enable_fx");

    public static readonly HandlerSlot InfectionTargetsSlot = InfectionShape.InputSlot("targets");
    public static readonly HandlerSlot InfectionAmountSlot = InfectionShape.InputSlot("amount");
    public static readonly HandlerSlot InfectionCapSlot = InfectionShape.InputSlot("cap");
    public static readonly HandlerSlot InfectionOperationSlot = InfectionShape.ParameterSlot("operation");
    public static readonly HandlerSlot InfectionEnableFxSlot = InfectionShape.ParameterSlot("enable_fx");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.player.binding.teleport", Blocks.ActionPlayerTeleport.Id, TeleportHandlerName, "execute", new[] { TeleportPermission }, TeleportShape),
        new ProviderBinding("forge.module.gtfo.player.binding.infection_change", Blocks.ActionPlayerInfectionChange.Id, InfectionHandlerName, "execute", new[] { InfectionPermission }, InfectionShape)
    };
}
