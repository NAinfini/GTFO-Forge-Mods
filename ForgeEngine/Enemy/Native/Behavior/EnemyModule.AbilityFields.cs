using System;
using System.Collections.Generic;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The per-ability blocks (<see cref="EnemyAbilityProfile"/>): each value lands on the vanilla member that carries
/// it, at the moment the game creates or starts that member's owner.
///
/// - Birthing: the life's own `EAB_Birthing` (dump.cs 533583) at spawn.
/// - Snatcher: a per-type copy of the shared `PouncerDataContainer` (661507), made once and handed to each life's
///   `PouncerBehaviour.m_data` (661259) at spawn, so no other type's snatchers change.
/// - Scout feeler: `ScoutAntennaDetection.Setup` (659577) lays the tendril directions out and `ScoutAntenna.Init`
///   (659398) builds one tendril; their postfixes rewrite what those set.
/// - Scout scream: the two scream colours are statics of `ES_ScoutScream` (664668/664669), swapped for this type's
///   values around its own `CommonUpdate` (664702) and put back; `ImmortalDuringScream: false` clears the
///   `Dam_EnemyDamageBase.IsImortal` (544199) the state sets in `ActivateState` / `RecieveScream`.
/// - Fog sphere: the `FogSphereHandler` (543711) `EAB_FogSphere.DoTrigger` (533795) has just released.
/// - Scanner: `EnemyAgent.set_ScannerColor` (662678) at each state entry the colour belongs to.
///
/// Every write is local and deterministic on every peer; nothing goes over the network. Nothing here runs per frame
/// except the scream swap, which only a screaming scout of a type that authored its colours pays.
/// </summary>
internal sealed partial class EnemyModule
{
    /// <summary>Which ability hooks some loaded type needs; every hook returns before its guard when its flag is
    /// clear.</summary>
    internal EnemyAbilityHooks AbilityHooks { get; private set; }

    /// <summary>One `PouncerDataContainer` copy per snatcher type that authored its data.</summary>
    private readonly Dictionary<uint, PouncerDataContainer> _pouncerData = new();

    internal readonly record struct ScreamColors(Color Chargeup, Color Pop);
    internal static readonly EnemyColorWindow<ScreamColors> ScreamWindow = new(
        static () => new(ES_ScoutScream.s_screamChargeupColor, ES_ScoutScream.s_screamPopColor),
        static colors => { ES_ScoutScream.s_screamChargeupColor = colors.Chargeup; ES_ScoutScream.s_screamPopColor = colors.Pop; },
        static (written, saved) =>
        {
            if (ES_ScoutScream.s_screamChargeupColor.Equals(written.Chargeup)) ES_ScoutScream.s_screamChargeupColor = saved.Chargeup;
            if (ES_ScoutScream.s_screamPopColor.Equals(written.Pop)) ES_ScoutScream.s_screamPopColor = saved.Pop;
        }, static (left, right) => left.Equals(right));

    /// <summary>Starts one life's ability sections: the spawn writes now, and the life state the hooks read.</summary>
    private void StartAbilityFields(EnemyAgent enemy, uint typeId, string? source, EnemyAbilityProfile abilities)
    {
        if (abilities.Birthing is { } birthing) ApplyBirthing(enemy, typeId, source, birthing);
        if (abilities.Pouncer is { } pouncer) ApplyPouncer(enemy, typeId, source, pouncer);
        if (abilities.Spawning.Length != 0) PrepareChildSpawning(enemy, typeId, source, abilities.Spawning);
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry) || entry.EnemyPointer != enemy.Pointer) return;
        entry.Abilities = new EnemyAbilityLife(abilities, typeId, source);
        // The life may already be in the state its first colour belongs to (a sleeper enters hibernation before
        // the spawn hook), so the current state's colour is written once here.
        if (abilities.Scanner != null) ScannerEntered(enemy, CurrentScannerState(enemy));
    }

    // ---- spawn writes -------------------------------------------------------------------------------------------

    /// <summary>`forge.eec.Ability.Birthing` on the life's own birthing component (found by narrowing its ability
    /// table, as the tentacle glow is). Per-birth count and interval come from the spawning attack config only.</summary>
    private void ApplyBirthing(EnemyAgent enemy, uint typeId, string? source, EnemyBirthingValues values)
    {
        var comps = enemy.Abilities?.AllComps;
        int written = 0;
        if (comps != null)
            for (int index = 0; index < comps.Length; index++)
            {
                var birthing = comps[index]?.TryCast<EAB_Birthing>();
                if (birthing == null) continue;
                if (values.ChildrenPerBirthMin is int perBirthMin) birthing.m_childrenPerBirthMin = perBirthMin;
                if (values.ChildrenMax is int max) birthing.m_childrenMax = max;
                if (values.ChildrenCost is float cost) birthing.m_childrenCost = cost;
                written++;
            }
        if (written == 0) { AbilityMissing(typeId, source, "birthing", "EAB_Birthing"); return; }
        AbilityApplied(typeId, source, "birthing", values.Count);
    }

    /// <summary>`forge.eec.Ability.Pouncer`: the life's behaviour is pointed at this type's own copy of its data.</summary>
    private void ApplyPouncer(EnemyAgent enemy, uint typeId, string? source, EnemyPouncerValues values)
    {
        var behaviour = enemy.AI?.m_behaviour?.TryCast<PouncerBehaviour>(); // EnemyAI.m_behaviour
        if (behaviour == null) { AbilityMissing(typeId, source, "pouncer", "PouncerBehaviour"); return; }
        if (!_pouncerData.TryGetValue(typeId, out var copy) || copy == null)
        {
            var original = behaviour.m_data; // PouncerBehaviour.m_data, +0xB8
            if (original == null) { AbilityMissing(typeId, source, "pouncer", "PouncerDataContainer"); return; }
            copy = UnityEngine.Object.Instantiate(original);
            copy.hideFlags = HideFlags.HideAndDontSave;
            WritePouncer(copy, values);
            _pouncerData[typeId] = copy;
        }
        behaviour.m_data = copy;
        AbilityApplied(typeId, source, "pouncer", values.Count);
    }

    /// <summary>The snatcher's data members, each a copy of the struct read, changed and written back where the
    /// member is a struct.</summary>
    private static void WritePouncer(PouncerDataContainer data, EnemyPouncerValues values)
    {
        if (values.DashCooldownMin != null || values.DashCooldownMax != null)
        {
            var cooldown = data.m_DashCooldown; // +0x18, x = min, y = max
            if (values.DashCooldownMin is float min) cooldown.x = min;
            if (values.DashCooldownMax is float max) cooldown.y = max;
            data.m_DashCooldown = cooldown;
        }
        if (values.DashMaxTime is float dashMaxTime) data.DashMaxTime = dashMaxTime;
        if (values.ChargeDuration is float chargeDuration) data.ChargeDuration = chargeDuration;
        if (values.ConsumeDuration is float consumeDuration) data.ConsumeDuration = consumeDuration;
        if (values.DashEndPhaseDistance is float endPhase) data.DashEndPhaseDistance = endPhase;
        if (values.ChargeStaggerDamageThreshold is float chargeStagger) data.ChargeStaggerDamageThreshold = chargeStagger;
        if (values.DashStaggerDamageThreshold is float dashStagger) data.DashStaggerDamageThreshold = dashStagger;
        if (values.StaggerDuration is float staggerDuration) data.StaggerDuration = staggerDuration;
        if (values.FaceToTargetDuringProwlingDistance is float face) data.FaceToTargetDuringProwlingDistance = face;
        if (values.StaggerLeadsToAfterHeld is bool afterHeld) data.StaggerLeadsToAfterHeld = afterHeld;
        if (values.EnableOffMeshLinkDash is bool offMesh) data.EnableOffMeshLinkDash = offMesh;
        if (values.LOSRequiredForDash is bool sight) data.LOSRequiredForDash = sight;
        if (values.DashStaggerUsesHeavyHitreaction is bool heavy) data.DashStaggerUsesHeavyHitreaction = heavy;
        if (values.DashMovementModifier is { } modifier)
        {
            var movement = data.DashMovementModifier; // +0x48
            movement.SpeedModifier = modifier[0];
            movement.AccelerationModifier = modifier[1];
            data.DashMovementModifier = movement;
        }
        if (values.MaxHeldDuration != null || values.DamageToPlayerPerSecond != null || values.DamageOnStartHolding != null
            || values.AfterHeldRunAwayDuration != null || values.SpitOutStateDuration != null)
        {
            var held = data.HeldStateData; // +0x50
            if (values.MaxHeldDuration is float maxHeld) held.MaxHeldDuration = maxHeld;
            if (values.DamageToPlayerPerSecond is float perSecond) held.DamageToPlayerPerSecond = perSecond;
            if (values.DamageOnStartHolding is float onStart) held.DamageOnStartHolding = onStart;
            if (values.AfterHeldRunAwayDuration is float runAway) held.AfterHeldRunAwayDuration = runAway;
            if (values.SpitOutStateDuration is float spitOut) held.SpitOutStateDuration = spitOut;
            data.HeldStateData = held;
        }
        if (values.DamageShape is { } shape)
        {
            var damage = data.DamageShapeData; // +0x80
            damage.Radius = shape[0];
            damage.Angle = shape[1];
            data.DamageShapeData = damage;
        }
    }

    // ---- scout feeler -------------------------------------------------------------------------------------------

    /// <summary>`ScoutAntennaDetection.Setup` returned. It placed tendril i at `i * m_dirAngStep + m_dirAngOffset`
    /// (with a random height) and shuffled the list; each entry's slot is recovered from its own angle and re-placed
    /// at the authored step and offset, which keeps the shuffle and the heights. `m_timerWaitOut` is a serialized
    /// member the game only reads, so the authored wait replaces it for this antenna.</summary>
    internal void FeelerLaidOut(ScoutAntennaDetection? detection)
    {
        if (detection == null || !TryAbilities(detection.m_owner, out var life) || life.Profile.Feeler is not { Layout: true } feeler) return;
        if (feeler.AngleOffset != null || feeler.StepAngle != null)
        {
            float oldStep = detection.m_dirAngStep, oldOffset = detection.m_dirAngOffset; // +0x74, +0xD0
            float step = feeler.StepAngle ?? oldStep, offset = feeler.AngleOffset ?? oldOffset;
            var angles = detection.m_randomAngleIndex; // Vector2[], +0x60
            if (angles != null && oldStep != 0f)
                for (int index = 0; index < angles.Length; index++)
                {
                    var angle = angles[index];
                    angle.x = MathF.Round((angle.x - oldOffset) / oldStep) * step + offset;
                    angles[index] = angle;
                }
            detection.m_dirAngStep = step;
            detection.m_dirAngOffset = offset;
        }
        if (feeler.OutTimer is float wait) detection.m_timerWaitOut = wait; // +0x80
        AbilityApplied(life.TypeId, life.Source, "feeler-layout",
            Counted.Of(feeler.AngleOffset) + Counted.Of(feeler.StepAngle) + Counted.Of(feeler.OutTimer));
    }

    /// <summary>`ScoutAntenna.Init` returned: the tendril's reach, its step and its two colours. `Init` already
    /// handed the default colour to the renderer's property block, so the block is set again with the authored one.</summary>
    internal void FeelerTendrilBuilt(ScoutAntenna? antenna, ScoutAntennaDetection? detection)
    {
        if (antenna == null || detection == null || !TryAbilities(detection.m_owner, out var life)
            || life.Profile.Feeler is not { Tendril: true } feeler) return;
        if (feeler.Distance is float distance) antenna.m_maxDistance = distance; // +0x64
        if (feeler.StepDistance is float stepDistance) antenna.m_stepDistance = stepDistance; // +0x68
        if (feeler.DetectColor is { } detect) antenna.m_colorDetection = ToColor(detect); // +0xB8
        if (feeler.NormalColor is { } normal)
        {
            var color = ToColor(normal);
            antenna.m_colorDefault = color; // +0xA8
            var block = antenna.m_propertyBlock; // +0x30
            var renderer = antenna.m_renderer; // +0x28
            if (block != null && renderer != null)
            {
                block.SetColor(ScoutAntenna.s_ColorID, color); // MaterialPropertyBlock.SetColor(int, Color)
                renderer.SetPropertyBlock(block); // Renderer.SetPropertyBlock
            }
        }
        AbilityApplied(life.TypeId, life.Source, "feeler-tendril",
            Counted.Of(feeler.Distance) + Counted.Of(feeler.StepDistance) + Counted.Of(feeler.NormalColor) + Counted.Of(feeler.DetectColor));
    }

    // ---- scout scream -------------------------------------------------------------------------------------------

    /// <summary>`ES_ScoutScream.CommonUpdate` is starting: for a screamer whose type authored the colours, the two
    /// static colours are lent to this call only. Nested calls have their own scopes; both normal and exceptional
    /// returns close the scope independently of the plugin session.</summary>
    internal EnemyColorWindow<ScreamColors>.Scope ScreamUpdating(ES_ScoutScream? state)
    {
        if (state == null || !TryAbilities(state.m_enemyAgent, out var life) || life.Profile.Scream is not { Colors: true } scream)
            return ScreamWindow.Enter(null);
        AbilityApplied(life.TypeId, life.Source, "scout-scream", Counted.Of(scream.ChargeupColor) + Counted.Of(scream.ScreamColor));
        var baseline = ScreamWindow.Baseline;
        return ScreamWindow.Enter(new ScreamColors(
            scream.ChargeupColor is { } chargeup ? ToColor(chargeup) : baseline.Chargeup,
            scream.ScreamColor is { } pop ? ToColor(pop) : baseline.Pop));
    }

    /// <summary>The scream state started (`ActivateState` on the host, `RecieveScream` on a client) and made the
    /// screamer immortal; a type with `ImmortalDuringScream: false` is made mortal again. The game clears the flag
    /// itself in `CommonExit`.</summary>
    internal void ScreamStarted(ES_ScoutScream? state)
    {
        // A puppet's scream is shown, not played: its immortality belongs to whoever holds it (ForgeCore Puppets).
        if (state == null || ForgeCore.Puppets.IsPuppet(state.m_enemyAgent) || !TryAbilities(state.m_enemyAgent, out var life)
            || life.Profile.Scream is not { ClearsImmortal: true }) return;
        var damage = state.m_enemyAgent!.Damage; // EnemyAgent.Damage
        if (damage == null) { AbilityMissing(life.TypeId, life.Source, "scream-mortal", "Dam_EnemyDamageBase"); return; }
        damage.IsImortal = false; // +0x119
        AbilityApplied(life.TypeId, life.Source, "scream-mortal", 1);
    }

    // ---- fog sphere ---------------------------------------------------------------------------------------------

    /// <summary>`EAB_FogSphere.DoTrigger` returned. It instantiated the sphere, started its handler and appended the
    /// handler to `m_activeFogSpheres`, so the last entry is the sphere just released; the handler evaluates these
    /// members every frame from its start.</summary>
    internal void FogSphereReleased(EAB_FogSphere? ability)
    {
        if (ability == null || !TryAbilities(ability.m_owner, out var life) || life.Profile.FogSphere is not { } fog) return;
        var spheres = ability.m_activeFogSpheres; // +0x60
        var handler = spheres != null && spheres.Count > 0 ? spheres[spheres.Count - 1] : null;
        if (handler == null) { AbilityMissing(life.TypeId, life.Source, "fog-sphere", "FogSphereHandler"); return; }
        if (fog.ColorMin is { } colorMin) handler.m_colorMin = ToColor(colorMin); // +0x50
        if (fog.ColorMax is { } colorMax) handler.m_colorMax = ToColor(colorMax); // +0x60
        if (fog.IntensityMin is float intensityMin) handler.m_intensityMin = intensityMin; // +0x78
        if (fog.IntensityMax is float intensityMax) handler.m_intensityMax = intensityMax; // +0x7C
        if (fog.RangeMin is float rangeMin) handler.m_rangeMin = rangeMin; // +0x98
        if (fog.RangeMax is float rangeMax) handler.m_rangeMax = rangeMax; // +0x9C
        if (fog.DensityMin is float densityMin) handler.m_densityMin = densityMin; // +0xA8
        if (fog.DensityMax is float densityMax) handler.m_densityMax = densityMax; // +0xAC
        if (fog.DensityAmountMin is float amountMin) handler.m_densityAmountMin = amountMin; // +0xB8
        if (fog.DensityAmountMax is float amountMax) handler.m_densityAmountMax = amountMax; // +0xBC
        if (fog.Duration is float duration) handler.m_totalLength = duration; // +0x44
        AbilityApplied(life.TypeId, life.Source, "fog-sphere", fog.Count);
    }

    // ---- scanner ------------------------------------------------------------------------------------------------

    /// <summary>A life entered the state one scanner colour belongs to; an authored colour (alpha = the dot size) is
    /// handed to the game's own setter, which marks it dirty for the tracker. An unauthored state keeps whatever the
    /// game shows.</summary>
    internal void ScannerEntered(EnemyAgent? enemy, EnemyScannerState state)
    {
        if (!TryAbilities(enemy, out var life) || life.Profile.Scanner is not { } scanner || scanner[(int)state] is not { } color) return;
        enemy!.ScannerColor = new Color(color[0], color[1], color[2], color[3]); // EnemyAgent.set_ScannerColor
        int count = 0;
        foreach (var authored in scanner) if (authored != null) count++;
        AbilityApplied(life.TypeId, life.Source, "scanner", count);
    }

    /// <summary>`ScoutAntennaDetection.SetState`: the tendrils are out in every state but `Idle`.</summary>
    internal void ScannerAntennaState(ScoutAntennaDetection? detection, eDetectionState state)
        => ScannerEntered(detection?.m_owner, state == eDetectionState.Idle ? EnemyScannerState.ScoutPatrol : EnemyScannerState.ScoutFeeler);

    private static EnemyScannerState CurrentScannerState(EnemyAgent enemy)
    {
        var locomotion = enemy.Locomotion;
        if (locomotion == null) return EnemyScannerState.Default;
        switch (locomotion.CurrentStateEnum)
        {
            case ES_StateEnum.Hibernate: return enemy.IsHibernationDetecting ? EnemyScannerState.Detection : EnemyScannerState.Heartbeat;
            case ES_StateEnum.HibernateWakeUp: return EnemyScannerState.Wakeup;
            case ES_StateEnum.ScoutDetection: return EnemyScannerState.ScoutPatrol;
            default: return EnemyScannerState.Default;
        }
    }

    // ---- shared -------------------------------------------------------------------------------------------------

    private bool TryAbilities(EnemyAgent? enemy, out EnemyAbilityLife life)
    {
        life = null!;
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry) || entry.EnemyPointer != enemy.Pointer
            || entry.Abilities is not { } found) return false;
        life = found;
        return true;
    }

    /// <summary>The first real write of one group for one type.</summary>
    private void AbilityApplied(uint typeId, string? source, string group, int count)
    {
        if (TypeRecordDue(RuntimeLogLevel.Info, EnemyLogCodes.AbilityFieldsApplied, typeId, group))
            WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.AbilityFieldsApplied, typeId, source, group, null,
                new[] { RuntimeLogValue.Of("count", (long)count) });
    }

    /// <summary>A group whose vanilla carrier the life does not have, once per type and group.</summary>
    private void AbilityMissing(uint typeId, string? source, string group, string component)
        => TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AbilityFieldMissing, typeId, source, group, component);
}

/// <summary>One tracked life's ability sections, with the type and file every record names.</summary>
internal sealed class EnemyAbilityLife
{
    internal EnemyAbilityLife(EnemyAbilityProfile profile, uint typeId, string? source)
    {
        Profile = profile; TypeId = typeId; Source = source;
    }

    internal EnemyAbilityProfile Profile { get; }
    internal uint TypeId { get; }
    internal string? Source { get; }
}
