using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The bindings and handler shapes of the two enemy behaviour rows this provider implements,
/// `forge.action.enemy.ability` and `forge.action.enemy.noise_emit`. The rows themselves are the generated block
/// table's (<see cref="Blocks"/>); registration refuses a shape that names a port its row does not carry.
///
/// The two rows carry the ports the implemented actions really answer. `forge.action.enemy.ability` keeps the
/// catalog's `enemies`/`ability` inputs, its `target_policy` (whose `nearest` member is refused by the handler
/// rather than served as `current`); `forge.action.enemy.noise_emit` keeps
/// `source`/`position`/`radius` after ruling 87 deleted `propagation_mask` and `heard_by`.
///
/// Three rows of the same family are deleted by ruling 88 and are not declared: `forge.action.enemy.patrol`
/// (no waypoint-path entry point exists), `flee` (the game's behaviour machine has no flee state) and
/// `threat_change` (the game keeps no threat table). `forge.action.enemy.investigate` is reshaped by ruling 88
/// into a state request, which is `forge.action.enemy.state_request`'s row in the enemy control family and is not
/// declared here under a second id; see `ForgeEnemy/evidence/enemy-behavior-ledger.json` for the evidence behind
/// each of those decisions.
///
/// EnemyRegistration.Declaration includes these bindings together with their permissions and handler shapes.</summary>
internal static class EnemyBehaviorContract
{
    /// <summary>The provider id every binding row below names, spelled once. It is the same id the enemy module
    /// registers under, and the contract repeats it rather than borrowing the module's constant so that the
    /// declaration half can be compiled without the native half.</summary>
    internal const string ProviderId = "forge.module.gtfo.enemy";
    internal const string AbilityBinding = ProviderId + ".binding.ability";
    internal const string NoiseEmitBinding = ProviderId + ".binding.noise_emit";
    internal const string AbilityHandler = "gtfo.enemy.ability";
    internal const string NoiseEmitHandler = "gtfo.enemy.noise_emit";

    /// <summary>The handlers' own port sets; registration refuses a shape that disagrees with its block's row.</summary>
    internal static readonly HandlerShape AbilityPorts = new HandlerShape()
        .Inputs("enemies", "ability").Outputs("result").Parameters("target_policy");
    internal static readonly HandlerShape NoiseEmitPorts = new HandlerShape()
        .Inputs("source", "position", "radius", "radius_min").Outputs("result").Parameters("cross_door", "origin", "noise_type");

    internal static readonly HandlerSlot AbilityResourceSlot = AbilityPorts.InputSlot("ability");
    internal static readonly HandlerSlot AbilityTargetPolicySlot = AbilityPorts.ParameterSlot("target_policy");
    internal static readonly HandlerSlot NoiseSourceSlot = NoiseEmitPorts.InputSlot("source");
    internal static readonly HandlerSlot NoisePositionSlot = NoiseEmitPorts.InputSlot("position");
    internal static readonly HandlerSlot NoiseRadiusSlot = NoiseEmitPorts.InputSlot("radius");
    internal static readonly HandlerSlot NoiseRadiusMinSlot = NoiseEmitPorts.InputSlot("radius_min");
    internal static readonly HandlerSlot NoiseCrossDoorSlot = NoiseEmitPorts.ParameterSlot("cross_door");
    internal static readonly HandlerSlot NoiseOriginSlot = NoiseEmitPorts.ParameterSlot("origin");
    /// <summary>`NM_NoiseData.type`, one of the game's `NM_NoiseType` members; absent keeps `Detectable`.</summary>
    internal static readonly HandlerSlot NoiseTypeSlot = NoiseEmitPorts.ParameterSlot("noise_type");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(AbilityBinding, Blocks.ActionEnemyAbility.Id, AbilityHandler, "execute", new[] { "gtfo.enemy.behavior.write" }, AbilityPorts),
        new ProviderBinding(NoiseEmitBinding, Blocks.ActionEnemyNoiseEmit.Id, NoiseEmitHandler, "execute", new[] { "gtfo.enemy.noise.write" }, NoiseEmitPorts)
    };
}
