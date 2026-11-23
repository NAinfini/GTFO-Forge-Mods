using System;
using ForgeEngine.Framework;

namespace ForgeEnemy;

/// <summary>The enemy-domain observation rows this provider publishes: `forge.trigger.enemy.tagged` (the
/// BioTracker tag a native tag transaction placed) and `forge.trigger.enemy.glued` (foam volume the game's own
/// glue receiver gained).
///
/// Both are `forge.trigger.*` rows, so their shapes belong to the framework's own contract module and are declared
/// by `ForgeEngine/Framework/Policy/TriggerContracts.cs`; this file keeps the ids and the binding half, and carries no
/// row text of its own. The third row of this family, `forge.trigger.enemy.spawned`, is declared by the trigger
/// contract for the same reason: this provider binds it and carries no copy of its text.
///
/// The catalog ports are the ones `TriggerContracts` declares: `tagged` carries `enemy / tagged`, `glued` carries
/// `enemy / volume / total / from_hibernate`. Neither row has a port the native observation cannot fill: the tag
/// transaction's own packet holds the tagged enemy and nothing else, and the glue receiver publishes one attached
/// volume per enemy, which is both the gain and the total, while the movement state the call starts from answers
/// whether this glue met a sleeping enemy (`EnemyLocomotion.CurrentStateEnum == ES_StateEnum.Hibernate`).</summary>
internal static class EnemyNodeTriggerContract
{
    /// <summary>The tag row: published when the game's own tag transaction lands a tag on an enemy, and again when
    /// a live tag ends.</summary>
    public const string TaggedBinding = ModuleDefinition.ProviderId + ".binding.tagged";
    public const string TaggedHandler = "gtfo.enemy.tagged";

    /// <summary>The glue row: published for the foam volume one hit really added to an enemy's own receiver.</summary>
    public const string GluedBinding = ModuleDefinition.ProviderId + ".binding.glued";
    public const string GluedHandler = "gtfo.enemy.glued";

    /// <summary>The observation permission each binding needs: both only read the enemy instance the provider
    /// already tracks — its tag flag, or the glue volume its own damage receiver publishes.</summary>
    public const string TagReadPermission = "gtfo.enemy.detection.read";
    public const string GlueReadPermission = "gtfo.enemy.glue.read";

    /// <summary>The binding id this package declares for the enemy spawn row. The capability belongs to the
    /// trigger contract's provider and that provider registers before any domain package, so this binding is
    /// legal as soon as the row exists — the fact is the watching provider's, and no other package declares a
    /// second binding for it.</summary>
    public const string SpawnedBinding = ModuleDefinition.ProviderId + ".binding.spawned";

    /// <summary>The handler name that travels with <see cref="SpawnedBinding"/>.</summary>
    public const string SpawnedHandler = "gtfo.enemy.spawned";

    /// <summary>The bindings with their own permissions and runtime shapes.</summary>
    public static ProviderBinding[] DeclareBindings() => new ProviderBinding[]
    {
        new ProviderBinding(SpawnedBinding, Blocks.TriggerEnemySpawned.Id, SpawnedHandler, "observe", Array.Empty<string>()),
        new ProviderBinding(TaggedBinding, Blocks.TriggerEnemyTagged.Id, TaggedHandler, "observe", new[] { TagReadPermission }),
        new ProviderBinding(GluedBinding, Blocks.TriggerEnemyGlued.Id, GluedHandler, "observe", new[] { GlueReadPermission })
    };
}
