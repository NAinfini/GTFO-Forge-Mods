using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The four enemy actions this package's own native write paths carry and whose catalog rows have no
/// other owner: `forge.action.enemy.kill`, `forge.action.enemy.remove`, `forge.action.enemy.mark` and
/// `forge.action.enemy.target`.
///
/// Each row names one native submission and nothing else:
/// <list type="bullet">
/// <item>`kill` ends one enemy's life whatever its health, melee resistance or immortal flag, through the game's own
/// damage funnel and death (ForgeCore `Combat.Kill`) — which is what "clear this
/// room" means, in contrast with
/// `forge.action.combat.execute`, which asks for an execution the target's own immunity rules may refuse.</item>
/// <item>`remove` takes one enemy out of the world through the game's own replication despawn
/// (`EnemyAgent.Sync.Replicator.Despawn()`), which is the other half of the same choice: no life ends, no death
/// settlement runs and no corpse is left — the enemy is gone the way a despawned enemy always goes. `kill` and
/// `remove` share one port shape because they are one author choice with two outcomes, and the catalog keeps
/// them as two rows so a plan says which outcome it wants rather than passing a mode.</item>
/// <item>`mark` builds Forge's own NavMarker on the enemy's model object through `GuiManager.NavMarkerLayer`,
/// because the game's own tag accepts neither a colour nor a caller-set duration (ruling 86/89; the evidence is
/// `%TEMP%\nativeinv\ni-markcolor.md`). It replaces the `ToolSyncManager.WantToTagEnemy` adapter this package
/// previously reached for the same catalog row.</item>
/// <item>`target` makes each chosen enemy pursue one player through the game's own `EnemyAI.SetTarget(Agent)`.</item>
/// </list>
///
/// `mark` is `execution: presentation` (ruling 122.3): the host decides the step and when it runs, and every
/// addressed player builds the marker on its own machine, which is the only place a NavMarker is ever seen. The
/// audience is the realm's own player list, answered by `EnemyModule.PresentationAudience` — the one
/// `PresentationSessions` entry this provider registers — so a player who joins late replays the mark by loading
/// the plan and presenting the step it never saw. The row commits nothing: a marker is a local presentation
/// object, and the tier refuses a result that claims a world write.
///
/// The rows themselves are the generated block table's (<see cref="Blocks"/>); this contract carries their
/// bindings, the permission each binding needs and the handler shapes.</summary>
internal static class EnemyNodeEffectContract
{
    /// <summary>The provider every row here belongs to; the same string `ModuleDefinition.ProviderId` carries.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>`forge.action.enemy.kill`: end the target enemies' lives through the game's own instant-death
    /// entry.</summary>
    public const string KillBinding = ProviderId + ".binding.kill";
    public const string KillHandler = "gtfo.enemy.kill";

    /// <summary>`forge.action.enemy.remove`: take the target enemies out of the world through the game's own
    /// replication despawn, with no death settlement and no corpse.</summary>
    public const string RemoveBinding = ProviderId + ".binding.remove";
    public const string RemoveHandler = "gtfo.enemy.remove";

    /// <summary>`forge.action.enemy.mark`: build a Forge-owned navigation marker on each target.</summary>
    public const string MarkBinding = ProviderId + ".binding.mark";
    public const string MarkHandler = "gtfo.enemy.mark";

    /// <summary>`forge.action.enemy.target`: make each target enemy pursue one player.</summary>
    public const string TargetBinding = ProviderId + ".binding.target";
    public const string TargetHandler = "gtfo.enemy.target";

    /// <summary>Every capability id this contract binds, in the same order as <see cref="DeclareBindings"/>.</summary>
    public static readonly string[] CapabilityIds =
    {
        Blocks.ActionEnemyKill.Id, Blocks.ActionEnemyRemove.Id, Blocks.ActionEnemyMark.Id, Blocks.ActionEnemyTarget.Id
    };

    /// <summary>The permission each binding writes the world through: a life ended, an enemy removed from the
    /// world, a marker built, an enemy's own target written.</summary>
    public const string KillPermission = "gtfo.enemy.life.write";
    public const string RemovePermission = "gtfo.enemy.removal.write";
    public const string MarkPermission = "gtfo.enemy.marker.write";
    public const string TargetPermission = "gtfo.enemy.targeting.write";

    /// <summary>The kill handler's ports: `targets` is the recipient collection. The row carries no source role:
    /// neither the native instant-death entry nor this handler reads one, and a port nothing reads is an option
    /// the author would see and the game would ignore.</summary>
    public static readonly HandlerShape LifeOutcomePorts = new HandlerShape()
        .Inputs("targets").Outputs("result");
    public static readonly HandlerSlot LifeOutcomeTargetsSlot = LifeOutcomePorts.InputSlot("targets");

    /// <summary>The mark handler's ports: `targets`, then the three things only Forge can carry — the colour, its
    /// opacity and the caller-set duration — with the audience as the row's one structural parameter. The colour is
    /// a `vector3` for the same reason `forge.action.presentation.fog`'s is: the catalog has no colour port type,
    /// and three components are what a `vector3` carries without one being invented.</summary>
    public static readonly HandlerShape MarkPorts = new HandlerShape()
        .Inputs("targets", "color", "opacity", "duration").Outputs("result", "marker").Parameters("visibility_policy");
    public static readonly HandlerSlot MarkTargetsSlot = MarkPorts.InputSlot("targets");
    public static readonly HandlerSlot MarkColorSlot = MarkPorts.InputSlot("color");
    public static readonly HandlerSlot MarkOpacitySlot = MarkPorts.InputSlot("opacity");
    public static readonly HandlerSlot MarkDurationSlot = MarkPorts.InputSlot("duration");
    public static readonly HandlerSlot MarkVisibilitySlot = MarkPorts.ParameterSlot("visibility_policy");

    /// <summary>The target handler's ports: `enemies` is the recipient collection and `target` the player every
    /// recipient is pointed at.</summary>
    public static readonly HandlerShape TargetPorts = new HandlerShape()
        .Inputs("enemies", "target").Outputs("result");
    public static readonly HandlerSlot TargetEnemiesSlot = TargetPorts.InputSlot("enemies");
    public static readonly HandlerSlot TargetPlayerSlot = TargetPorts.InputSlot("target");

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(KillBinding, Blocks.ActionEnemyKill.Id, KillHandler, "execute", new[] { KillPermission }, LifeOutcomePorts),
        new ProviderBinding(RemoveBinding, Blocks.ActionEnemyRemove.Id, RemoveHandler, "execute", new[] { RemovePermission }, LifeOutcomePorts),
        new ProviderBinding(MarkBinding, Blocks.ActionEnemyMark.Id, MarkHandler, "execute", new[] { MarkPermission }, MarkPorts),
        new ProviderBinding(TargetBinding, Blocks.ActionEnemyTarget.Id, TargetHandler, "execute", new[] { TargetPermission }, TargetPorts)
    };
}
