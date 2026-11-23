using System;

namespace ForgeEnemy.Profile;

/// <summary>
/// The typed values of the per-ability blocks: the scout feeler, the scout scream, the birthing ability, the
/// snatcher's data, the fog sphere and the bio-tracker colours. <see cref="EnemyFieldResolver"/> fills them from the
/// document, <see cref="EnemyBlockReader"/> checks them, and the native side writes each member onto the vanilla
/// member the research named (`EnemyModule.AbilityFields.cs`). A null member keeps the game's own value.
///
/// Every class counts the members it carries, which is the `count` the first real write of a group reports.
/// </summary>
internal sealed class EnemyFeelerValues
{
    /// <summary>`TendrilAngleOffset` and `TendrilStepAngle`, degrees: the offset and the step of the tendril
    /// directions `ScoutAntennaDetection.Setup` lays out (`m_dirAngOffset`, `m_dirAngStep`).</summary>
    internal float? AngleOffset { get; set; }
    internal float? StepAngle { get; set; }
    /// <summary>`TendrilOutTimer`, seconds: how long the tendrils wait out (`m_timerWaitOut`).</summary>
    internal float? OutTimer { get; set; }
    /// <summary>`Distance` and `StepDistance`, metres: one tendril's reach and its step (`ScoutAntenna.m_maxDistance`,
    /// `m_stepDistance`).</summary>
    internal float? Distance { get; set; }
    internal float? StepDistance { get; set; }
    internal float[]? NormalColor { get; set; }
    internal float[]? DetectColor { get; set; }

    internal bool Layout => AngleOffset != null || StepAngle != null || OutTimer != null;
    internal bool Tendril => Distance != null || StepDistance != null || NormalColor != null || DetectColor != null;

    internal int Count => Counted.Of(AngleOffset) + Counted.Of(StepAngle) + Counted.Of(OutTimer) + Counted.Of(Distance)
        + Counted.Of(StepDistance) + Counted.Of(NormalColor) + Counted.Of(DetectColor);
}

/// <summary>`forge.eec.Ability.ScoutScream` colours and `forge.eec.Detection.Screaming.ImmortalDuringScream`.</summary>
internal sealed class EnemyScreamValues
{
    internal float[]? ChargeupColor { get; set; }
    internal float[]? ScreamColor { get; set; }
    /// <summary>`false` lets the scream be interrupted by damage: the game makes the screamer immortal when the
    /// state starts, and the write clears that again. `true` is the game's own behaviour and writes nothing.</summary>
    internal bool? ImmortalDuringScream { get; set; }

    internal bool Colors => ChargeupColor != null || ScreamColor != null;
    internal bool ClearsImmortal => ImmortalDuringScream == false;

    internal int Count => Counted.Of(ChargeupColor) + Counted.Of(ScreamColor) + (ImmortalDuringScream != null ? 1 : 0);
}

/// <summary>`forge.eec.Ability.Birthing`, written on the life's own `EAB_Birthing` at spawn.</summary>
internal sealed class EnemyBirthingValues
{
    internal int? ChildrenPerBirthMin { get; set; }
    internal int? ChildrenMax { get; set; }
    internal float? ChildrenCost { get; set; }

    internal int Count => (ChildrenPerBirthMin != null ? 1 : 0) + (ChildrenMax != null ? 1 : 0) + Counted.Of(ChildrenCost);
}

/// <summary>`forge.eec.Ability.Pouncer`, written on a per-type copy of the snatcher's `PouncerDataContainer`.
/// The two list paths carry two numbers each: `DashMovementModifier` is speed then acceleration, `DamageShape` is
/// radius then angle.</summary>
internal sealed class EnemyPouncerValues
{
    internal float? DashCooldownMin { get; set; }
    internal float? DashCooldownMax { get; set; }
    internal float? DashMaxTime { get; set; }
    internal float? ChargeDuration { get; set; }
    internal float? ConsumeDuration { get; set; }
    internal float? DashEndPhaseDistance { get; set; }
    internal float? ChargeStaggerDamageThreshold { get; set; }
    internal float? DashStaggerDamageThreshold { get; set; }
    internal float? StaggerDuration { get; set; }
    internal float? FaceToTargetDuringProwlingDistance { get; set; }
    internal bool? StaggerLeadsToAfterHeld { get; set; }
    internal bool? EnableOffMeshLinkDash { get; set; }
    internal bool? LOSRequiredForDash { get; set; }
    internal bool? DashStaggerUsesHeavyHitreaction { get; set; }
    internal float[]? DashMovementModifier { get; set; }
    internal float? MaxHeldDuration { get; set; }
    internal float? DamageToPlayerPerSecond { get; set; }
    internal float? DamageOnStartHolding { get; set; }
    internal float? AfterHeldRunAwayDuration { get; set; }
    internal float? SpitOutStateDuration { get; set; }
    internal float[]? DamageShape { get; set; }

    internal int Count => Counted.Of(DashCooldownMin) + Counted.Of(DashCooldownMax) + Counted.Of(DashMaxTime)
        + Counted.Of(ChargeDuration) + Counted.Of(ConsumeDuration) + Counted.Of(DashEndPhaseDistance)
        + Counted.Of(ChargeStaggerDamageThreshold) + Counted.Of(DashStaggerDamageThreshold) + Counted.Of(StaggerDuration)
        + Counted.Of(FaceToTargetDuringProwlingDistance) + Counted.Of(StaggerLeadsToAfterHeld) + Counted.Of(EnableOffMeshLinkDash)
        + Counted.Of(LOSRequiredForDash) + Counted.Of(DashStaggerUsesHeavyHitreaction) + Counted.Of(DashMovementModifier)
        + Counted.Of(MaxHeldDuration) + Counted.Of(DamageToPlayerPerSecond) + Counted.Of(DamageOnStartHolding)
        + Counted.Of(AfterHeldRunAwayDuration) + Counted.Of(SpitOutStateDuration) + Counted.Of(DamageShape);
}

/// <summary>`forge.eec.Ability.FogSphere`, written on the `FogSphereHandler` the ability has just released.</summary>
internal sealed class EnemyFogSphereValues
{
    internal float[]? ColorMin { get; set; }
    internal float[]? ColorMax { get; set; }
    internal float? IntensityMin { get; set; }
    internal float? IntensityMax { get; set; }
    internal float? RangeMin { get; set; }
    internal float? RangeMax { get; set; }
    internal float? DensityMin { get; set; }
    internal float? DensityMax { get; set; }
    internal float? DensityAmountMin { get; set; }
    internal float? DensityAmountMax { get; set; }
    internal float? Duration { get; set; }

    internal int Count => Counted.Of(ColorMin) + Counted.Of(ColorMax) + Counted.Of(IntensityMin) + Counted.Of(IntensityMax)
        + Counted.Of(RangeMin) + Counted.Of(RangeMax) + Counted.Of(DensityMin) + Counted.Of(DensityMax)
        + Counted.Of(DensityAmountMin) + Counted.Of(DensityAmountMax) + Counted.Of(Duration);
}

/// <summary>The six bio-tracker states `forge.eec.Model.Scanner` names, in the order of
/// <see cref="EnemyScannerValues.Names"/>.</summary>
internal enum EnemyScannerState
{
    Default,
    Wakeup,
    Detection,
    Heartbeat,
    ScoutPatrol,
    ScoutFeeler
}

/// <summary>`forge.eec.Model.Scanner`: one colour and one dot size per state. The game draws the tracker dot with
/// `EnemyAgent.m_scannerColor`, whose alpha is the dot size, so a state's written value is its colour with the size
/// as alpha. A colour without a size takes the field table's default size for that state.</summary>
internal sealed class EnemyScannerValues
{
    internal static readonly string[] Names = { "Default", "Wakeup", "Detection", "Heartbeat", "ScoutPatrol", "ScoutFeeler" };
    /// <summary>The field table's default size of each state (`catalog/authoring/object-fields.json`), which the website shows
    /// for an empty size.</summary>
    internal static readonly float[] DefaultSizes = { 1f, 1f, 1f, 1f, 0.5f, 1f };

    internal float[]?[] Colors { get; } = new float[]?[Names.Length];
    internal float?[] Sizes { get; } = new float?[Names.Length];

    /// <summary>The written value of one state, RGB plus the size as alpha; null when the state is not authored.</summary>
    internal float[]? Written(EnemyScannerState state)
    {
        int index = (int)state;
        if (Colors[index] is not { } color) return null;
        return new[] { color[0], color[1], color[2], Sizes[index] ?? DefaultSizes[index] };
    }

    internal int Count
    {
        get
        {
            int count = 0;
            for (int index = 0; index < Names.Length; index++) count += Counted.Of(Colors[index]) + Counted.Of(Sizes[index]);
            return count;
        }
    }
}

/// <summary>The per-ability sections one enemy type authored, each checked by the reader. A null section writes
/// nothing and installs nothing: the hooks return at once when no loaded type has the section.</summary>
internal sealed class EnemyAbilityProfile
{
    internal EnemySpawnAttackValues[] Spawning { get; set; } = Array.Empty<EnemySpawnAttackValues>();
    internal EnemyFeelerValues? Feeler { get; set; }
    internal EnemyScreamValues? Scream { get; set; }
    internal EnemyBirthingValues? Birthing { get; set; }
    internal EnemyPouncerValues? Pouncer { get; set; }
    internal EnemyFogSphereValues? FogSphere { get; set; }
    /// <summary>The scanner values with every authored state already turned into its written colour, indexed by
    /// <see cref="EnemyScannerState"/>.</summary>
    internal float[]?[]? Scanner { get; set; }

    internal bool IsEmpty => Spawning.Length == 0 && Feeler == null && Scream == null && Birthing == null && Pouncer == null && FogSphere == null && Scanner == null;
}

/// <summary>The ability hooks some loaded type needs; each hook returns at once when its flag is clear.</summary>
[Flags]
internal enum EnemyAbilityHooks
{
    None = 0,
    FeelerLayout = 1,
    FeelerTendril = 2,
    ScreamColors = 4,
    ScreamMortal = 8,
    FogSphere = 16,
    Scanner = 32,
    ChildSpawning = 64
}

internal static class Counted
{
    internal static int Of(float? value) => value != null ? 1 : 0;
    internal static int Of(bool? value) => value != null ? 1 : 0;
    internal static int Of(float[]? value) => value != null ? 1 : 0;
}
