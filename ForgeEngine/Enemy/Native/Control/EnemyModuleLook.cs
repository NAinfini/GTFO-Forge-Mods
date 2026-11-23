using System;
using Enemies;
using ForgeEnemy.Profile;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>
/// The per-life half of the look (<see cref="EnemyLookProfile"/>): the glow pulses, the propagated wake-up colour
/// and the tag marker (text, scales, blinks, marking while hibernating). Each is
/// started from the spawn path (<see cref="ApplyProfiles"/>) on every peer from the same documents, lives on the
/// tracked life's entry and goes with it on despawn or a world change.
///
/// The pulses and the marker blinks run on the kernel's own clock: the module's lifecycle observer already sees
/// every <c>TickAdvanced</c>, and the step reads the kernel tick and its rate, so no Unity update is patched. It
/// walks only when some loaded profile has a pulse or a blink, iterates the tracked lives without allocating and
/// writes only through the game's own entries: <c>EnemyAppearance.InterpolateGlow(Color, float)</c> (dump.cs
/// 535814) for a pulse and <c>CoroutineManager.BlinkIn(GameObject, float)</c> (579864) for a blink.
///
/// Every native member used here is named with its dump.cs line of build 20403457 where it is called.
/// </summary>
internal sealed partial class EnemyModule
{
    private bool _tickedLooks;
    private bool _propagateLooks;
    private double _pulseTicksPerSecond;
    private long _pulseRateWorld = -1;
    /// <summary>The game's propagated wake-up colour while one authored wake-up has it swapped out, restored when
    /// that wake-up returns.</summary>
    internal static readonly EnemyColorWindow<Vector4> WakeupWindow = new(
        static () => ES_HibernateWakeUp.m_propagatedWakeupColor,
        static color => ES_HibernateWakeUp.m_propagatedWakeupColor = color,
        static (written, saved) =>
        {
            if (ES_HibernateWakeUp.m_propagatedWakeupColor.Equals(written)) ES_HibernateWakeUp.m_propagatedWakeupColor = saved;
        }, static (left, right) => left.Equals(right));

    /// <summary>Starts the look's per-life state for one tracked life. Called from the spawn path after the
    /// spawn-time glow write, so a pulse lerps from the colour that write left.</summary>
    private void StartLook(EnemyAgent enemy, uint typeId, string? source, EnemyLookProfile look,
        EnemyAppearanceProfile? appearance)
    {
        if (look.AllowMarkingOnHibernate) AllowMarkingOnHibernate(enemy, typeId, source);
        if (!look.HasLifeState) return;
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry) || entry.EnemyPointer != enemy.Pointer) return;
        var life = new EnemyLookLife(look, typeId, source);
        if (look.PropagateWakeupColor is { } propagate)
        {
            var color = ToColor(propagate);
            life.PropagateWakeup = new Vector4(color.r, color.g, color.b, color.a); // Vector4(float x4), dump.cs 266920
        }
        if (look.Pulses.Count != 0)
        {
            // EEC lerps from `CharacterMaterialHandler.m_defaultGlowColor` (dump.cs 533340, a Vector4 whose x..w are
            // 266896-266899); a profile that authored the default glow colour set that colour at spawn instead.
            Color baseColor;
            if (appearance?.GlowColor is { } authored) baseColor = ToColor(authored);
            else if (enemy.MaterialHandler is { } handler) // EnemyAgent.MaterialHandler, dump.cs 662341
            {
                var fallback = handler.m_defaultGlowColor;
                baseColor = new Color(fallback.x, fallback.y, fallback.z, fallback.w); // Color(float x4), 265581
            }
            else
            {
                LookFailed(life, "pulse-default-glow-missing", "MaterialHandler");
                entry.Look = life;
                return;
            }
            var runs = new EnemyPulseRun[look.Pulses.Count];
            for (int index = 0; index < runs.Length; index++)
            {
                var pulse = look.Pulses[index];
                var colors = new Color[pulse.Steps.Length];
                var target = ToColor(pulse.Color);
                for (int step = 0; step < colors.Length; step++)
                    colors[step] = Lerp(baseColor, target, Math.Clamp(pulse.Steps[step].Progression, 0f, 1f));
                double delay = pulse.RandomizeTime ? ForgeCore.DeterministicRandom.Unit(enemy.GlobalID, (ulong)index) * pulse.Duration : 0d;
                runs[index] = new EnemyPulseRun(pulse, colors, delay);
            }
            life.Pulses = runs;
        }
        entry.Look = life;
    }

    /// <summary>`AllowMarkingOnHibernate`: EEC's spawn write, `ScannerData.m_soundIndex = 0`, which the game's
    /// tagging reads to let a hibernating enemy be marked. The object is the agent's own (EnemyAgent.m_scannerData, with its
    /// per-enemy matrix, property block and update id, dump.cs 616388), not a block shared by type, so no undo entry is kept.</summary>
    private void AllowMarkingOnHibernate(EnemyAgent enemy, uint typeId, string? source)
    {
        var scanner = enemy.ScannerData; // EnemyAgent.get_ScannerData, dump.cs 662462
        if (scanner == null)
        {
            TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, typeId, source, "scanner-data-missing", "ScannerData");
            return;
        }
        scanner.m_soundIndex = 0; // Gear.EnemyScannerDataObject.m_soundIndex, 616394
        TypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.MarkerOnHibernate, typeId, source);
    }

    /// <summary>Called from the lifecycle observer on every kernel tick; a no-op unless a loaded profile has a
    /// pulse or a marker blink. The kernel's rate is read once per world (it throws `runtime-not-ready` until the
    /// host reports it).</summary>
    private void AdvanceLooks(RuntimeLifecycleSnapshot current)
    {
        if (!_tickedLooks || _entities.Count == 0) return;
        long tick = current.SimulationTick;
        if (tick < 0) return;
        if (_pulseRateWorld != current.WorldEpoch)
        {
            _pulseRateWorld = current.WorldEpoch;
            try { _pulseTicksPerSecond = _kernel.TicksPerSecond; }
            catch (RuntimeContractException error)
            {
                // The host reports the rate when it begins the world, before the first tick; a world without one
                // cannot time a pulse, and says so once instead of every tick.
                _pulseTicksPerSecond = 0d;
                if (Log.Logs(RuntimeLogLevel.Error))
                    Log.Write(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, new RuntimeLogFacts
                    {
                        Values = new[] { RuntimeLogValue.Of("reason", "pulse-clock-unknown"), RuntimeLogValue.Of("code", error.Code) }
                    });
            }
        }
        if (!(_pulseTicksPerSecond > 0d)) return;
        double now = tick / _pulseTicksPerSecond;
        foreach (var entry in _entities.Values)
        {
            if (entry.Look is not { } life) continue;
            if (life.Pulses.Length != 0)
            {
                try { StepPulses(entry.Enemy, life, now); }
                catch (Exception error)
                {
                    EnemyLog.Contained(error);
                    // A native read that throws stops this life's pulses instead of repeating the failure each tick.
                    life.Pulses = Array.Empty<EnemyPulseRun>();
                    LookFailed(life, "pulse-native-error", error.GetType().Name);
                }
            }
            if (life.BlinkTarget != null) StepBlink(entry.Enemy, life, now);
        }
    }

    /// <summary>EEC's `Blink` schedule on the kernel clock: the first blink 0.4 s plus one delay after the marker
    /// was placed, each next one a further delay later, none after the window (`min(Duration, TagTime)`). Each blink
    /// is the game's own `CoroutineManager.BlinkIn(GameObject, 0)`, called when it is due; blinks that fall due in the
    /// same tick are one call. The schedule starts on the first tick after the marker was placed.</summary>
    private void StepBlink(EnemyAgent enemy, EnemyLookLife life, double now)
    {
        var blink = life.Profile.Marker!.Blink!;
        if (double.IsNaN(life.BlinkStart))
        {
            life.BlinkStart = now;
            life.BlinkNext = now + EnemyMarkerBlink.FirstOffset + blink.Delay(enemy.GlobalID, life.Blinks++);
        }
        if (now < life.BlinkNext) return;
        if (life.BlinkNext - life.BlinkStart > life.BlinkWindow) { life.BlinkTarget = null; return; }
        try
        {
            CoroutineManager.BlinkIn(life.BlinkTarget!, 0f); // CoroutineManager.BlinkIn(GameObject, float), dump.cs 579864
        }
        catch (Exception error)
        {
            EnemyLog.Contained(error);
            // A marker object the game destroyed stops this life's blinks instead of failing every tick.
            life.BlinkTarget = null;
            LookFailed(life, "marker-blink-native-error", error.GetType().Name);
            return;
        }
        double delay = blink.Delay(enemy.GlobalID, life.Blinks++);
        double next = life.BlinkNext + delay;
        if (next <= now) next = now + delay;
        life.BlinkNext = next;
        if (next - life.BlinkStart > life.BlinkWindow) life.BlinkTarget = null;
    }

    /// <summary>EEC's `PulseRoutine`, step for step: after the start delay, every `Duration / steps` seconds the
    /// pulse tries one step. A step is skipped — the index does not advance — when the mode is not a target, when the
    /// life is dead without `KeepOnDead`, or (unless `AlwaysPulse`) when the game is already driving the glow: a
    /// hibernating enemy mid-detection, an aggressive one attacking or in a shooter/striker/tank-multi attack or scout
    /// scream state, a scout whose antenna wants its tendrils out.</summary>
    private static void StepPulses(EnemyAgent enemy, EnemyLookLife life, double now)
    {
        foreach (var run in life.Pulses)
        {
            if (double.IsNaN(run.NextAt)) run.NextAt = now + run.StartDelay;
            if (now < run.NextAt) continue;
            var pulse = run.Profile;
            run.NextAt += pulse.StepSeconds;
            if (run.NextAt <= now) run.NextAt = now + pulse.StepSeconds;
            var ai = enemy.AI;
            int mode = (int)ai.Mode; // AgentAI.get_Mode, dump.cs 680884
            if ((pulse.Target & EnemyModeTargets.OfGameMode(mode)) == 0) continue;
            if (!enemy.Alive && !pulse.KeepOnDead) continue; // EnemyAgent.Alive, 662454
            if (!pulse.AlwaysPulse && GlowBusy(enemy, mode)) continue;
            if (run.Index >= pulse.Steps.Length) run.Index = 0;
            int index = run.Index++;
            var step = pulse.Steps[index];
            if (step.IsDelay) continue;
            enemy.Appearance.InterpolateGlow(run.Colors[index], step.Length * pulse.StepSeconds); // 535814
        }
    }

    private static bool GlowBusy(EnemyAgent enemy, int mode)
    {
        switch (mode)
        {
            case 4: // Hibernate
                return enemy.IsHibernationDetecting; // 662390
            case 1: // Agressive
                var locomotion = enemy.Locomotion;
                if (locomotion.IsAttacking()) return true; // EnemyLocomotion.IsAttacking, 663202
                return locomotion.CurrentStateEnum is ES_StateEnum.ShooterAttack or ES_StateEnum.StrikerAttack // 663114
                    or ES_StateEnum.TankMultiTargetAttack or ES_StateEnum.ScoutScream;
            case 3: // Scout
                // EnemyLocomotion.ScoutDetection 663044 -> ES_ScoutDetection.m_antennaDetection 537884 ->
                // ScoutAntennaDetection.m_wantsToHaveTendrils 659537.
                var antenna = enemy.Locomotion.ScoutDetection?.m_antennaDetection;
                return antenna != null && antenna.m_wantsToHaveTendrils;
            default:
                return false;
        }
    }

    /// <summary>`EnemyAgent.SyncPlaceNavMarkerTag` (dump.cs 662718) placed the tag marker. The icon is swapped first
    /// (its sprite is the game's own asset of that shape), then the marker's colour and the icon part's size are written,
    /// then `BlinkIn` and the start of the `Blink` schedule. A marker placed again restarts the schedule.</summary>
    internal void MarkerPlaced(EnemyAgent? enemy)
    {
        if (!TryLook(enemy, out var life) || life.Profile.Marker is not { } profile) return;
        var marker = enemy!.m_tagMarker; // EnemyAgent.m_tagMarker, dump.cs 662421
        if (marker == null)
        {
            LookFailed(life, "marker-missing", "m_tagMarker");
            return;
        }
        bool iconApplied = profile.Icon == EnemyMarkerIcon.Vanilla || SwapMarkerIcon(life, marker.m_enemySubObj, profile.Icon); // NavMarker.m_enemySubObj, 552446
        if (profile.Color is { } color) marker.SetColor(ToColor(color)); // NavMarker.SetColor(Color), 552626
        if (profile.SpriteScale is float spriteScale) ScaleSubObject(marker.m_enemySubObj, spriteScale, life, "m_enemySubObj");
        life.MarkerPlaced = true;
        if (profile.BlinkIn || profile.Blink != null)
        {
            var sprite = marker.m_enemySubObj;
            if (sprite == null)
            {
                LookFailed(life, "marker-missing", "m_enemySubObj");
                return;
            }
            var target = sprite.gameObject; // Component.get_gameObject, 267895
            if (profile.BlinkIn)
            {
                CoroutineManager.BlinkIn(target, 0f); // CoroutineManager.BlinkIn(GameObject, float), 579864
                CoroutineManager.BlinkIn(target, 0.2f);
            }
            if (profile.Blink is { } blink)
            {
                // EnemyAgent.EnemyBalancingData 662365 -> EnemyBalancingDataBlock.get_TagTime 622572: the marker's own
                // lifetime caps the blink window, as EEC's `Math.Min(BlinkDuration, TagTime)` does.
                var balancing = enemy.EnemyBalancingData;
                if (balancing == null)
                    LookFailed(life, "marker-blink-window-unknown", "EnemyBalancingData");
                else
                {
                    life.BlinkWindow = Math.Min(blink.Duration, balancing.TagTime);
                    life.BlinkStart = double.NaN;
                    life.BlinkTarget = target;
                }
            }
        }
        // An unauthored scale is written as -1: the marker kept the game's.
        if (TypeRecordDue(RuntimeLogLevel.Info, EnemyLogCodes.MarkerStyled, life.TypeId))
            WriteTypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.MarkerStyled, life.TypeId, life.Source, null, null, new[]
            {
                RuntimeLogValue.Of("icon", EnemyMarkerIcons.Name(profile.Icon)), RuntimeLogValue.Of("iconApplied", iconApplied),
                RuntimeLogValue.Of("blinkIn", profile.BlinkIn), RuntimeLogValue.Of("blink", profile.Blink != null),
                RuntimeLogValue.Of("color", profile.Color != null), RuntimeLogValue.Of("spriteScale", (double)(profile.SpriteScale ?? -1f))
            });
    }

    /// <summary>One refused look write for a life's type, once per type, reason and detail.</summary>
    private void LookFailed(EnemyLookLife life, string reason, string detail)
        => TypeRecord(RuntimeLogLevel.Error, EnemyLogCodes.AppearanceFailed, life.TypeId, life.Source, reason, detail);

    /// <summary>`SpriteScale` is a multiple of the icon part's own scale, which the game sets per prefab (about 54): the
    /// scale the part had when this marker was first placed is kept, so a marker placed again is not scaled twice.</summary>
    private void ScaleSubObject(NavMarkerComponent? part, float scale, EnemyLookLife life, string member)
    {
        if (part == null)
        {
            LookFailed(life, "marker-missing", member);
            return;
        }
        var transform = part.transform; // Component.get_transform 267894
        int id = part.GetInstanceID();
        if (life.SpriteBaseId != id)
        {
            life.SpriteBase = transform.localScale; // Transform.get_localScale
            life.SpriteBaseId = id;
        }
        var own = life.SpriteBase;
        transform.localScale = new Vector3(own.x * scale, own.y * scale, own.z * scale); // Transform.set_localScale 271040, Vector3(float x3) 266109
    }
    /// <summary>`ES_HibernateWakeUp.DoWakeup` (dump.cs 664518) is starting. For a propagated wake-up of a life
    /// whose type authored `PropagateWakeupColor`, the game's one static colour (`m_propagatedWakeupColor`, 664488)
    /// — which `DoWakeup` passes to the glow on that path — is swapped for the authored one until the call returns,
    /// so a nested vanilla wake-up keeps the baseline. The call's finalizer closes its own scope on exceptions.</summary>
    internal EnemyColorWindow<Vector4>.Scope WakeupStarting(ES_HibernateWakeUp? state, bool isPropagatedWakeup)
    {
        if (!_propagateLooks || !isPropagatedWakeup || state == null) return WakeupWindow.Enter(null);
        var owner = state.m_enemyAgent; // ES_Base.m_enemyAgent, dump.cs 663281
        if (!TryLook(owner, out var life) || life.PropagateWakeup is not { } color) return WakeupWindow.Enter(null);
        TypeRecord(RuntimeLogLevel.Info, EnemyLogCodes.PropagateWakeupColor, life.TypeId, life.Source);
        return WakeupWindow.Enter(color);
    }

    /// <summary>World/owner teardown: close remaining native call windows and release only owned instance writes.</summary>
    internal void ClearLooks(bool releaseInstanceAppearance)
    {
        WakeupWindow.Clear();
        ScreamWindow.Clear();
        if (releaseInstanceAppearance) ClearInstanceAppearance();
    }

    private bool TryLook(EnemyAgent? enemy, out EnemyLookLife life)
    {
        life = null!;
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry) || entry.EnemyPointer != enemy.Pointer
            || entry.Look is not { } found) return false;
        life = found;
        return true;
    }

    private static Color ToColor(float[] color) => new(color[0], color[1], color[2], color.Length > 3 ? color[3] : 1f);

    private static Color Lerp(Color from, Color to, float t)
        => new(from.r + (to.r - from.r) * t, from.g + (to.g - from.g) * t, from.b + (to.b - from.b) * t, from.a + (to.a - from.a) * t);
}

/// <summary>One tracked life's look state.</summary>
internal sealed class EnemyLookLife
{
    internal EnemyLookLife(EnemyLookProfile profile, uint typeId, string? source)
    {
        Profile = profile; TypeId = typeId; Source = source;
    }

    internal EnemyLookProfile Profile { get; }
    /// <summary>The life's enemy type and the profile file it came from, which every look record names.</summary>
    internal uint TypeId { get; }
    internal string? Source { get; }
    internal EnemyPulseRun[] Pulses { get; set; } = Array.Empty<EnemyPulseRun>();
    /// <summary>The authored propagated wake-up colour as the vector the game's static holds.</summary>
    internal Vector4? PropagateWakeup { get; set; }
    internal bool MarkerPlaced { get; set; }
    /// <summary>The icon part's own scale when this life's marker was first placed, and the part it belongs to.</summary>
    internal Vector3 SpriteBase { get; set; }
    internal int SpriteBaseId { get; set; }
    /// <summary>The marker sprite object a running `Blink` schedule blinks; null when no schedule runs.</summary>
    internal GameObject? BlinkTarget { get; set; }
    /// <summary>The blink window in seconds: `min(BlinkDuration, TagTime)`.</summary>
    internal double BlinkWindow { get; set; }
    /// <summary>Kernel seconds of the schedule's start (NaN until the first tick after the marker was placed).</summary>
    internal double BlinkStart { get; set; } = double.NaN;
    internal double BlinkNext { get; set; }
    /// <summary>How many blink delays this life has drawn: the counter of the deterministic draw.</summary>
    internal int Blinks { get; set; }
}

/// <summary>One pulse's running state: the per-step colours computed once at spawn, the step index and the time
/// of the next attempt in kernel seconds.</summary>
internal sealed class EnemyPulseRun
{
    internal EnemyPulseRun(EnemyPulseProfile profile, Color[] colors, double startDelay)
    { Profile = profile; Colors = colors; StartDelay = startDelay; }

    internal EnemyPulseProfile Profile { get; }
    internal Color[] Colors { get; }
    internal double StartDelay { get; }
    internal int Index { get; set; }
    internal double NextAt { get; set; } = double.NaN;
}
