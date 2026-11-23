using System;
using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The enemy-domain read rows of the node list's `values` section: `v-e-health`, `v-e-alive`,
/// `v-e-type`, `v-e-sleep`, `v-e-where`, `v-e-tagged`, `v-e-group` and `v-e-phase` — the facts a plan reads out of
/// one enemy without changing anything. Only the domain-specific ones are bound here: generic health, life
/// state and position are the runtime's own observation rows.
///
/// They are `state`-kind rows at the `query` tier, which is the pair the framework's own value rows use: `state`
/// is the value row's own kind — the one an on-demand read about the world is registered under, and the one the
/// registration accepts an evaluator for (`RuntimeRegistry.WithModule`) — and every row here declares an entity
/// input, so `query` is what the tier rule requires rather than a choice. The ids
/// follow the node list's own `forge.<…>.<domain>.<snake_name>` grammar with `query` as the middle segment, the
/// spelling ruling 92 fixed for the values section; the two packages that grow value rows (enemy and player) use
/// it identically.
///
/// Every row takes one entity input named `enemy`, the node list's own port name. What each row answers, and the
/// native fact behind it:
/// <list type="bullet">
/// <item>`health`: the enemy's current and maximum health, read from its own damage receiver through the shared
/// entity snapshot (`RuntimeEntitySnapshot.Health` / `.HealthMaximum`).</item>
/// <item>`alive`: whether the enemy is alive. Read from the snapshot's life state, which is the one place the
/// provider publishes it.</item>
/// <item>`type`: the official `EnemyDataBlock.persistentID` the agent was built from, as decimal text — the same
/// spelling the `enemy-type` mount matcher uses (`Native/Observation/EnemyTypeReader.cs`).</item>
/// <item>`type_in`: whether that same type is one of a list of `enemy` resources the plan compiled.</item>
/// <item>`sleeping`: whether the enemy's behaviour state is one of the game's two hibernation states, read from
/// the snapshot's `aiState` through the same table `forge.trigger.enemy.awakened` publishes from.</item>
/// <item>`where`: the enemy's world position (the snapshot's own vector, in metres) and the zone it stands in as
/// the `forge.resource.zone` reference the framework's zone identity is written from.</item>
/// <item>`tagged`: the enemy's own BioTracker tag flag and the seconds of tag time it still has
/// (`EnemyAgent.IsTagged` / `EnemyAgent.EnemyTaggedTimer`).</item>
/// <item>`group`: the enemy group the life belongs to — the state its own behaviour machine publishes to every
/// peer, the type it was spawned as, and how frustrated its patrol has become (`EnemyAI.m_group`,
/// `EnemyGroup.Data.currentState`, `.GroupType`, `.PatrolFrustration`).</item>
/// <item>`phase`: the boss phase the life's own behaviour machine holds — the same member the write row
/// `forge.action.enemy.phase_set` writes through the same instance's setter
/// (`SquidBossBehaviour.Phase`). A life that is not a boss has no phase, so the row refuses by name instead of
/// reporting phase zero.</item>
/// </list>
///
/// A read that cannot be made is the step's own refusal with a code, never a zero, an empty string or a false:
/// a snapshot without health means this provider publishes no health for that life, and a row that answered a
/// default would be indistinguishable from an observed default. Two rows answer a port the game genuinely
/// answered with nobody: `where`'s `zone` is null when the enemy stands outside every zone, which is an answer
/// and not a missing read — a read that could not be made refuses before the port is built.</summary>
internal static class EnemyNodeValueContract
{
    /// <summary>The provider every row here belongs to.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>The entity input every row takes.</summary>
    public const string EnemyPort = "enemy";

    /// <summary>`v-e-type`: the official enemy type id.</summary>
    public const string TypeHandler = "gtfo.enemy.value.type";

    /// <summary>Whether the enemy's official type is one of the listed enemy resources.</summary>
    public const string TypeInHandler = "gtfo.enemy.value.type_in";

    /// <summary>`v-e-sleep`: whether the enemy is hibernating.</summary>
    public const string SleepingHandler = "gtfo.enemy.value.sleeping";

    /// <summary>`v-e-tagged`: the tag flag and the tag time left.</summary>
    public const string TaggedHandler = "gtfo.enemy.value.tagged";

    /// <summary>`v-e-group`: the group the enemy belongs to and what its own state machine is doing.</summary>
    public const string GroupHandler = "gtfo.enemy.value.group";

    /// <summary>`v-e-phase`: the phase the life's own boss behaviour machine is in.</summary>
    public const string PhaseHandler = "gtfo.enemy.value.phase";

    /// <summary>The binding a row is registered under: the capability's own suffix under this provider, the same
    /// rule the player value family uses, so either side can name the counterpart of a row.</summary>
    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>Every capability id this contract binds, in the same order as <see cref="DeclareBindings"/>.</summary>
    public static readonly string[] CapabilityIds =
    {
        Blocks.QueryEnemyType.Id, Blocks.QueryEnemySleeping.Id, Blocks.QueryEnemyTagged.Id, Blocks.QueryEnemyGroup.Id,
        Blocks.QueryEnemyPhase.Id, Blocks.QueryEnemyTypeIn.Id
    };

    internal static readonly HandlerShape TypeShape = new HandlerShape().Inputs(EnemyPort).Outputs("value", "spawn_origin");
    internal static readonly HandlerShape TypeInShape = new HandlerShape().Inputs(EnemyPort, "types").Outputs("value");
    internal static readonly HandlerShape SleepingShape = new HandlerShape().Inputs(EnemyPort).Outputs("value");
    internal static readonly HandlerShape TaggedShape = new HandlerShape().Inputs(EnemyPort).Outputs("value", "remaining");
    internal static readonly HandlerShape GroupShape = new HandlerShape().Inputs(EnemyPort).Outputs("state", "group_type", "patrol_frustration");
    internal static readonly HandlerShape PhaseShape = new HandlerShape().Inputs(EnemyPort).Outputs("phase");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.enemy.binding.query.enemy.type", Blocks.QueryEnemyType.Id, TypeHandler, "observe", Array.Empty<string>(), TypeShape),
        new ProviderBinding("forge.module.gtfo.enemy.binding.query.enemy.sleeping", Blocks.QueryEnemySleeping.Id, SleepingHandler, "observe", Array.Empty<string>(), SleepingShape),
        new ProviderBinding("forge.module.gtfo.enemy.binding.query.enemy.tagged", Blocks.QueryEnemyTagged.Id, TaggedHandler, "observe", Array.Empty<string>(), TaggedShape),
        new ProviderBinding("forge.module.gtfo.enemy.binding.query.enemy.group", Blocks.QueryEnemyGroup.Id, GroupHandler, "observe", Array.Empty<string>(), GroupShape),
        new ProviderBinding("forge.module.gtfo.enemy.binding.query.enemy.phase", Blocks.QueryEnemyPhase.Id, PhaseHandler, "observe", Array.Empty<string>(), PhaseShape),
        new ProviderBinding("forge.module.gtfo.enemy.binding.query.enemy.type_in", Blocks.QueryEnemyTypeIn.Id, TypeInHandler, "observe", Array.Empty<string>(), TypeInShape)
    };
}
