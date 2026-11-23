using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The enemy action triggers this provider observes (contract §4.5 B2): the moments an enemy's own state
/// machine or ability component commits to an action — a melee swing, a scream, a sleeper starting or stopping to
/// sense a player, a projectile fired, hitting the world or expiring, a fog sphere, a mother spawning a child, a snatcher or tank grabbing a player or
/// letting one go, an attack winding up or striking, a noise heard, an alarm raised — and the zone an awake enemy
/// walks into.
///
/// Every row is a `forge.trigger.*` row, so its shape belongs to the framework's trigger contract
/// (`forge.contract.trigger`, projected from `Docs/forge-contract/primitive/primitives.json` by
/// `Tools/Forge/build-primitive-runtime.ts`); this file keeps the ids and the binding half and carries no row text.
/// The hooks that publish them are `Native/Behavior/EnemyActionHooks.cs`, the module half is
/// `Native/Behavior/EnemyModule.ActionFacts.cs`, and the members they read are recorded in
/// `evidence/enemy-behavior-hooks.json`.</summary>
internal static class EnemyActionTriggerContract
{
    /// <summary>One observed row: the capability, this provider's binding for it, the handler name the binding
    /// resolves through and the read permission its support row declares.</summary>
    public sealed record Row(string Capability, string Binding, string Handler, string Permission);

    private const string Provider = ModuleDefinition.ProviderId;
    private const string AbilityRead = "gtfo.enemy.ability.read";
    private const string BehaviorRead = "gtfo.enemy.behavior.read";

    public static readonly Row MeleeAttack = new(Blocks.TriggerEnemyMeleeAttack.Id, Provider + ".binding.melee_attack", "gtfo.enemy.melee_attack", AbilityRead);
    public static readonly Row Screamed = new(Blocks.TriggerEnemyScreamed.Id, Provider + ".binding.screamed", "gtfo.enemy.screamed", BehaviorRead);
    public static readonly Row SleeperDetecting = new(Blocks.TriggerEnemySleeperDetecting.Id, Provider + ".binding.sleeper_detecting", "gtfo.enemy.sleeper_detecting", "gtfo.enemy.detection.read");
    public static readonly Row ProjectileFired = new(Blocks.TriggerEnemyProjectileFired.Id, Provider + ".binding.projectile_fired", "gtfo.enemy.projectile_fired", AbilityRead);
    public static readonly Row ProjectileHitWorld = new(Blocks.TriggerEnemyProjectileHitWorld.Id, Provider + ".binding.projectile_hit_world", "gtfo.enemy.projectile_hit_world", AbilityRead);
    public static readonly Row ProjectileExpired = new(Blocks.TriggerEnemyProjectileExpired.Id, Provider + ".binding.projectile_expired", "gtfo.enemy.projectile_expired", AbilityRead);
    public static readonly Row FogReleased = new(Blocks.TriggerEnemyFogReleased.Id, Provider + ".binding.fog_released", "gtfo.enemy.fog_released", AbilityRead);
    public static readonly Row GaveBirth = new(Blocks.TriggerEnemyGaveBirth.Id, Provider + ".binding.gave_birth", "gtfo.enemy.gave_birth", AbilityRead);
    public static readonly Row ReleasedPlayer = new(Blocks.TriggerEnemyReleasedPlayer.Id, Provider + ".binding.released_player", "gtfo.enemy.released_player", BehaviorRead);
    public static readonly Row ZoneChanged = new(Blocks.TriggerEnemyZoneChanged.Id, Provider + ".binding.zone_changed", "gtfo.enemy.zone_changed", BehaviorRead);
    public static readonly Row AttackPerformed = new(Blocks.TriggerEnemyAttackPerformed.Id, Provider + ".binding.attack_performed", "gtfo.enemy.attack_performed", AbilityRead);
    public static readonly Row HeardNoise = new(Blocks.TriggerEnemyHeardNoise.Id, Provider + ".binding.heard_noise", "gtfo.enemy.heard_noise", "gtfo.enemy.detection.read");
    public static readonly Row AlarmRaised = new(Blocks.TriggerEnemyAlarmRaised.Id, Provider + ".binding.alarm_raised", "gtfo.enemy.alarm_raised", AbilityRead);
    public static readonly Row GrabbedPlayer = new(Blocks.TriggerEnemyGrabbedPlayer.Id, Provider + ".binding.grabbed_player", "gtfo.enemy.grabbed_player", BehaviorRead);

    /// <summary>Every row, in registration order.</summary>
    public static readonly Row[] Rows =
        {
            MeleeAttack, Screamed, SleeperDetecting, ProjectileFired, ProjectileHitWorld, ProjectileExpired, FogReleased, GaveBirth, ReleasedPlayer, ZoneChanged,
            AttackPerformed, HeardNoise, AlarmRaised, GrabbedPlayer
        };

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding("forge.module.gtfo.enemy.binding.melee_attack", Blocks.TriggerEnemyMeleeAttack.Id, "gtfo.enemy.melee_attack", "observe", new[] { AbilityRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.screamed", Blocks.TriggerEnemyScreamed.Id, "gtfo.enemy.screamed", "observe", new[] { BehaviorRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.sleeper_detecting", Blocks.TriggerEnemySleeperDetecting.Id, "gtfo.enemy.sleeper_detecting", "observe", new[] { "gtfo.enemy.detection.read" }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.projectile_fired", Blocks.TriggerEnemyProjectileFired.Id, "gtfo.enemy.projectile_fired", "observe", new[] { AbilityRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.projectile_hit_world", Blocks.TriggerEnemyProjectileHitWorld.Id, "gtfo.enemy.projectile_hit_world", "observe", new[] { AbilityRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.projectile_expired", Blocks.TriggerEnemyProjectileExpired.Id, "gtfo.enemy.projectile_expired", "observe", new[] { AbilityRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.fog_released", Blocks.TriggerEnemyFogReleased.Id, "gtfo.enemy.fog_released", "observe", new[] { AbilityRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.gave_birth", Blocks.TriggerEnemyGaveBirth.Id, "gtfo.enemy.gave_birth", "observe", new[] { AbilityRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.released_player", Blocks.TriggerEnemyReleasedPlayer.Id, "gtfo.enemy.released_player", "observe", new[] { BehaviorRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.zone_changed", Blocks.TriggerEnemyZoneChanged.Id, "gtfo.enemy.zone_changed", "observe", new[] { BehaviorRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.attack_performed", Blocks.TriggerEnemyAttackPerformed.Id, "gtfo.enemy.attack_performed", "observe", new[] { AbilityRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.heard_noise", Blocks.TriggerEnemyHeardNoise.Id, "gtfo.enemy.heard_noise", "observe", new[] { "gtfo.enemy.detection.read" }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.alarm_raised", Blocks.TriggerEnemyAlarmRaised.Id, "gtfo.enemy.alarm_raised", "observe", new[] { AbilityRead }),
        new ProviderBinding("forge.module.gtfo.enemy.binding.grabbed_player", Blocks.TriggerEnemyGrabbedPlayer.Id, "gtfo.enemy.grabbed_player", "observe", new[] { BehaviorRead })
    };
}
