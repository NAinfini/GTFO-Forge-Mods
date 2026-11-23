using System;
using System.Collections.Generic;
using System.Numerics;

namespace ForgeEnemy.Profile;

/// <summary>Which vanilla limb damage type a `Limbs.LimbType` names. `WeakspotCustom` and `ArmorCustom` are
/// EEC's own two extra values: the vanilla enum has no member for them, so they make the limb a native Weakspot /
/// Armor limb and carry the author's own multiplier on that type's multiplier field.</summary>
internal enum EnemyFieldLimbType
{
    Normal,
    Weakspot,
    WeakspotCustom,
    Armor,
    ArmorCustom
}

/// <summary>One `Limbs` entry of the `forge.eec.Model.Limb` block, already normalised: the limb name is the
/// resolution key, the type decides which multiplier field the custom multiplier reaches.</summary>
internal sealed class EnemyFieldLimb
{
    internal EnemyFieldLimb(string name) { Name = name; }

    internal string Name { get; }

    /// <summary>The position of this entry in the document's `Limbs` list; the pack test addresses a limb by it.</summary>
    internal int Entry { get; init; }
    internal EnemyFieldLimbType? Type { get; set; }
    internal float? CustomMultiplier { get; set; }
    internal float? Health { get; set; }

    /// <summary>The most damage one hit may deal to this limb. Zero means "no cap the document asks for" and is
    /// the field the hit-time rule reads as a finite cap; a document that says nothing leaves it null.</summary>
    internal float? MaxDamage { get; set; }

    /// <summary>`false` blocks the host's destruction of this part; `true` asks for a destruction the limb must
    /// already have. Null keeps the vanilla answer.</summary>
    internal bool? Breakable { get; set; }

    /// <summary>The value true removes the part from the live enemy: no model, no collision, no damage. Null and false leave it.</summary>
    internal bool? Removed { get; set; }

    /// <summary>The damage multiplier a destroyed limb takes.</summary>
    internal float? DestroyedMultiplier { get; set; }
}

/// <summary>One `Bones` entry of the `forge.eec.Model.Bone` block: a skeleton node named by the author and the
/// model-local transform it takes. The name is the resolution key, exactly as a limb's name is; the applier finds
/// the node under the type's main model by that name, so the group needs no native bone member of its own.</summary>
internal sealed class EnemyFieldBone
{
    internal EnemyFieldBone(string name) { Name = name; }

    internal string Name { get; }
    internal float[]? Offset { get; set; }
    internal float[]? RotationOffset { get; set; }
    internal float[]? Scale { get; set; }

    /// <summary>`true` multiplies the authored scale into the node's own, `false` replaces it; null means the
    /// document asked for no scaling of the node and the member is left alone.</summary>
    internal bool? UsingRelativeScale { get; set; }
}

/// <summary>One region entry of the region assembly: a `ModelRefs` entry whose `Type` names a body region (the
/// part swap, model from `CreateFromPath`) or a `MaterialSets` entry whose `From` names one (the reskin, model
/// from `To`). The resolver only routes the entry here; the reader checks it. `Extra` names the first EEC
/// attach-point member the entry also set (`CopyFrom`, `Offset`, `RotateOffset`), which a region swap has no
/// meaning for and the reader refuses.</summary>
internal sealed class EnemyAppearanceRegionValue
{
    internal string? Region { get; set; }
    internal string? Model { get; set; }
    internal string? Extra { get; set; }
}

/// <summary>One `Bones` entry whose `Bone` names a placement slot (a body region or `tumor1`..`tumor8`): the
/// slot and its model-local transform as written — `Offset`, `RotationOffset` as Unity Euler degrees, `Scale` and
/// `UsingRelativeScale`. The reader parses the slot, turns the angles into a rotation and checks the scale.</summary>
internal sealed class EnemyPlacementValue
{
    internal string? Target { get; set; }
    internal float[]? Position { get; set; }
    internal float[]? Rotation { get; set; }
    internal float[]? Scale { get; set; }
    internal bool? RelativeScale { get; set; }
}

/// <summary>One controller's `forge.eec.Model.AnimHandle` values. Null members keep that controller's original
/// value; omitted controllers receive no override.</summary>
internal sealed class EnemyAnimHandleValues
{
    internal float? WalkSpeed { get; set; }
    internal float? RunSpeed { get; set; }
    internal float? StrafeWalkSpeed { get; set; }
    internal float? StrafeRunSpeed { get; set; }
    internal float? ClimbSpeed { get; set; }
    internal float? InPlayerRoomSpeed { get; set; }
    internal float? JumpMoveSpeed { get; set; }
    internal float? TentacleAttackWindUpLen { get; set; }
    internal float? MeleeAttackFwdDuration { get; set; }
    internal float? MeleeAttackFwdDamageStart { get; set; }
    internal float? MeleeAttackFwdDamageEnd { get; set; }

    internal bool? AllowMelee180 { get; set; }
    internal bool? NeedsArmsToMove { get; set; }

    /// <summary>The backward timing entry, in the website's own field names; every member optional.</summary>
    internal float? MeleeAttackBwdDuration { get; set; }
    internal float? MeleeAttackBwdDamageStart { get; set; }
    internal float? MeleeAttackBwdDamageEnd { get; set; }
    internal int? MeleeAttackBwdSfxId { get; set; }

}

/// <summary>Sparse overrides indexed by the native AnimatorControllerHandleName bit identity. Native shared
/// controllers remain unchanged; these values apply to the instance's currently selected controller.</summary>
internal sealed class EnemyAnimHandleOverrides
{
    private readonly EnemyAnimHandleValues?[] _controllers = new EnemyAnimHandleValues?[12];

    internal static int ControllerIndex(int controller)
        => controller > 0 && controller <= 2048 && (controller & (controller - 1)) == 0
            ? BitOperations.TrailingZeroCount((uint)controller) : -1;

    internal EnemyAnimHandleValues? ForController(int controller)
    {
        int index = ControllerIndex(controller);
        return index < 0 ? null : _controllers[index];
    }

    internal EnemyAnimHandleValues GetOrCreate(int controller)
    {
        int index = ControllerIndex(controller);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(controller));
        return _controllers[index] ??= new EnemyAnimHandleValues();
    }
}

/// <summary>One `PulseEffects` entry as the document wrote it.</summary>
internal sealed class EnemyPulseValue
{
    internal string? Pattern { get; set; }
    internal float? Duration { get; set; }
    internal EnemyFieldValue? Target { get; set; }
    internal float[]? Color { get; set; }
    internal bool? AlwaysPulse { get; set; }
    internal bool? KeepOnDead { get; set; }
    internal bool? RandomizeTime { get; set; }
}

/// <summary>One `MaterialSets` entry as written. `Region` marks a `From` that names a body region: its `To` is the
/// model the region is reskinned from, and `full` with properties writes them on every material that has them.
/// On any other entry `NamesTo` marks a swap to another named material, which this build does not perform; its
/// properties are then not read.</summary>
internal sealed class EnemyMaterialSetValue
{
    internal string? From { get; set; }
    internal bool Region { get; set; }
    internal string? To { get; set; }
    internal bool NamesTo { get; set; }
    internal List<(string? Name, float? Value)> Floats { get; } = new();
}

/// <summary>One `Looks` entry as the document wrote it: the target name, the colour and glow as numbers or colour
/// text, the brightness, and whether each of the three optional members was present even when it could not be read.</summary>
internal sealed class EnemyLookValue
{
    internal string? Target { get; set; }
    internal float[]? Color { get; set; }
    internal float[]? Glow { get; set; }
    internal float? GlowBrightness { get; set; }
    internal bool? Invisible { get; set; }
    internal bool ColorDeclared { get; set; }
    internal bool GlowDeclared { get; set; }
    internal bool BrightnessDeclared { get; set; }
    internal bool InvisibleDeclared { get; set; }
}

/// <summary>What one enemy's block document came to before any native write: the typed values the sink is given,
/// the ids the sink actually wrote, and the accepted paths this build has no writer for.</summary>
internal sealed class EnemyFieldResolution
{
    internal EnemyFieldResolution(string source) { Source = source; }

    internal List<EnemySpawnAttackValues> SpawnAttacks { get; } = new();

    internal string Source { get; }

    internal bool? RequireTagForDetection { get; set; }

    /// <summary>`forge.enemyWake`: the distance inside which a sneaking player wakes this type, and whether player
    /// gunfire and damage wake it. Read by the host's hooks (`EnemyModule.Wake`), never written at spawn.</summary>
    internal float? SneakWakeDistance { get; set; }
    internal bool? GunfireWakes { get; set; }
    internal bool? DamageWakes { get; set; }
    internal float? BackDamageMultiplier { get; set; }

    /// <summary>`forge.enemySpawn.CostTowardsCap`: how much of the on-screen cap one aggressive life of this type
    /// takes, in place of the per-type cost the game would allocate (`EnemyCostManager.GetEnemyCost`). Written by the
    /// host when the game allocates the life's share, never at spawn (`EnemyModule.AfterModeChange`).</summary>
    internal float? CostTowardsCap { get; set; }

    internal float[]? GlowDefaultColor { get; set; }
    internal float[]? GlowDetection { get; set; }
    internal float[]? GlowHeartbeat { get; set; }
    internal float[]? GlowShooterFire { get; set; }

    /// <summary>`forge.eec.Model.Glow.SelfWakeupColor`: the colour the wake-up state fades a self-awakened enemy
    /// towards. `ES_HibernateWakeUp.m_selfWakeupColor` carries it per instance.</summary>
    internal float[]? GlowSelfWakeup { get; set; }

    /// <summary>`forge.eec.Model.Glow.PropagateWakeupColor`: the colour a wake-up propagated from a neighbour fades
    /// this type towards. The game reads it from one static (`ES_HibernateWakeUp.m_propagatedWakeupColor`), so the
    /// look swaps that static for this type's value only around this enemy's own propagated wake-up.</summary>
    internal float[]? GlowPropagateWakeup { get; set; }

    /// <summary>`forge.eec.Model.Glow.TentacleAttackColor`: the colour the multi-tentacle attack state animates the
    /// glow to (`EAB_MovingEnemyTentacleMultiple.m_attackGlowColor`). A type whose ability table has no such
    /// component simply has nowhere to write it, which the spawn path reports rather than inventing one.</summary>
    internal float[]? GlowTentacleAttack { get; set; }

    /// <summary>`forge.eec.Model.Glow.Transition`: the seconds the default glow colour fades in over, applied with
    /// `GlowDefaultColor` through the receiver's own `InterpolateGlow(colour, seconds)`.</summary>
    internal float? GlowTransition { get; set; }

    internal List<EnemyFieldLimb> Limbs { get; } = new();
    internal List<EnemyFieldBone> Bones { get; } = new();
    internal List<EnemyAppearanceAttachment> Attachments { get; } = new();
    internal List<EnemyAppearanceRegionValue> Parts { get; } = new();
    internal List<EnemyAppearanceRegionValue> Materials { get; } = new();
    internal List<EnemyPlacementValue> Placement { get; } = new();
    internal EnemyAnimHandleOverrides? AnimHandle { get; set; }

    /// <summary>`forge.eec.Model.Glow.PulseEffects`, one value per entry as written; the reader checks them.</summary>
    internal List<EnemyPulseValue> Pulses { get; } = new();

    /// <summary>The members of `forge.eec.Model.Marker` this build writes, as written: the icon (and whether the
    /// member was present at all), the hibernation marking switch and the icon blinks.</summary>
    internal string? MarkerIcon { get; set; }
    internal bool MarkerIconDeclared { get; set; }
    internal bool? MarkerAllowOnHibernate { get; set; }
    internal bool? MarkerBlinkIn { get; set; }
    internal bool? MarkerBlink { get; set; }
    internal float? MarkerBlinkDuration { get; set; }
    internal float? MarkerBlinkMinDelay { get; set; }
    internal float? MarkerBlinkMaxDelay { get; set; }
    /// <summary>`MarkerColor` (the marker's own colour, `NavMarker.SetColor`) and `SpriteScale` (the icon part's
    /// scale), as written.</summary>
    internal float[]? MarkerColor { get; set; }
    internal float? MarkerSpriteScale { get; set; }

    /// <summary>The per-ability blocks as written (<see cref="EnemyFeelerValues"/> and its siblings); null when the
    /// document sets none of a block's written paths.</summary>
    internal EnemyFeelerValues? Feeler { get; set; }
    internal EnemyScreamValues? Scream { get; set; }
    internal EnemyBirthingValues? Birthing { get; set; }
    internal EnemyPouncerValues? Pouncer { get; set; }
    internal EnemyFogSphereValues? FogSphere { get; set; }
    internal EnemyScannerValues? Scanner { get; set; }

    /// <summary>`forge.enemyAppearance.Looks`, one value per entry as written; the reader checks them.</summary>
    internal List<EnemyLookValue> Looks { get; } = new();

    /// <summary>`forge.eec.Model.Material.MaterialSets`, every entry as written (with or without `To`), so the
    /// reader can refuse a duplicate `From` across all of them.</summary>
    internal List<EnemyMaterialSetValue> MaterialSets { get; } = new();

    /// <summary>Field ids a native write landed on.</summary>
    internal List<string> Applied { get; } = new();

    /// <summary>Accepted field ids this build does not write, with the block that owns each, so the package
    /// reports a named line once instead of silently applying part of a document.</summary>
    internal List<(string Block, string FieldId)> Unapplied { get; } = new();

    /// <summary>Entries the document wrote that set nothing (an empty block, a limb with no value, a transition with no
    /// colour, a spawn attack that changes nothing). They are skipped and reported by name; the rest of the document
    /// still applies, because an entry that writes nothing is no rule the game could break on.</summary>
    internal List<(string Block, string Entry)> Skipped { get; } = new();

    internal bool IsEmpty => Applied.Count == 0 && Unapplied.Count == 0;
}
