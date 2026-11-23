using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeEngine.Presentation;
using Player;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>The bodies the generic-player batch adds to the Player provider, on build 20403457:
///
/// <list type="bullet">
/// <item>`forge.action.combat.impulse` — one directional push at a player, through the player's own external-push
/// channel (`PlayerLocomotion.AddExternalPushForce`, dump.cs 656528, with `m_externalPushForce` at 656239 and the
/// readback at 656531). The row pushes and never damages: no damage entry is entered, so no health, limb or
/// hitreact state is touched.</item>
/// <item>`forge.action.player.stamina_change` — one change to a player's `0..1` stamina (dump.cs 541458, ceiling the
/// private constant `MaxStamina = 1` at 541437), on the machine that simulates the player: the game's own
/// `UseStamina` / `ResetStamina` where they say the change, one write of `Stamina` where nothing does.</item>
/// <item>`forge.action.presentation.camera_shake` — `FPSCamera.Shake(duration, amplitude, frequency,
/// worldDirection)` (dump.cs 530638) on each viewer whose camera this machine holds.</item>
/// <item>`forge.action.presentation.screen_liquid` — `ScreenLiquidManager.DirectApply(setting, screenPosition, direction)`
/// (dump.cs 588846), the native entry that queues one liquid job for the local viewport.</item>
/// <item>`forge.query.player.movement_state` — the native `PlayerLocomotion.m_currentStateEnum` (the eighteen
/// members are dumped at 656698) and the machine's own state-entry time.</item>
/// <item>`forge.query.player.aim` — the local player's `FPSCamera.CameraRayPos` / `CameraRayDir` (dump.cs
/// 530240-530242), every other player's `Agent.EyePosition` and synced `Agent.TargetLookDir` (dump.cs 680553).</item>
/// </list>
///
/// The impulse row's recipient set is one set, as the ruling requires, and the handler dispatches on the reference
/// kind. The player arm is the one implemented here; the enemy arm is refused by name because this provider cannot
/// turn a `gtfo.enemy` reference into the native `EnemyAgent`: the enemy package owns that reference table, the
/// runtime publishes references and snapshots and no native instance, and a second enemy identity table built in
/// this half is the duplicate path the framework forbids. The report carries the missing interface.</summary>
internal static class GenericPlayerActions
{
    /// <summary>The epsilon a readback is compared with: the native members are floats, so the observed vector
    /// delta is compared with the submitted force rather than bit for bit.</summary>
    internal const float ReadbackEpsilon = 0.001f;

    /// <summary>The reference kind the impulse row dispatches on.</summary>
    internal const string PlayerKind = EntityKinds.Player;


    /// <summary>The observe body this half supplies: the movement-state row, built from the one native read that
    /// can answer it.</summary>
    internal static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(Func<PlayerIdentityModule?> players)
        => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [PlayerMovementStateContract.HandlerName] =
            PlayerMovementStateContract.Evaluator(new PlayerMovementStateContract.MovementReaders(reference => ReadMovement(players(), reference))),
            [PlayerStaminaContract.ValueHandlerName] =
            PlayerStaminaContract.ValueEvaluator(new PlayerStaminaContract.StaminaReaders(reference => ReadStamina(players(), reference))),
            [PlayerAimContract.HandlerName] = PlayerAimContract.Evaluator(new PlayerAimContract.AimReaders(reference => ReadAim(players(), reference)))
        };

    // ------------------------------------------------------------------ impulse

    /// <summary>`forge.action.combat.impulse` for player recipients: applies one pure push vector (metres per second)
    /// to each. This is an `owner` row: the kernel sends it to every machine that simulates one of the recipients,
    /// each answers rows for its own players, and a recipient this machine does not simulate is refused with
    /// <see cref="ResultCode.OwnerNotAddressed"/> in the row the kernel then drops for the machine that does hold it.
    /// The force is the row's shared shaping (<see cref="CombatImpulseContract.TryForce"/>).</summary>
    internal static CommandResult Impulse(PlayerIdentityModule? players, CommandContext context)
    {
        if (!CombatImpulseContract.TryForce(context, out double x, out double y, out double z, out string? code))
            return Outcome.Rejected(code!);
        var force = new Vector3((float)x, (float)y, (float)z);
        if (!float.IsFinite(force.x) || !float.IsFinite(force.y) || !float.IsFinite(force.z))
            return Outcome.Rejected(ResultCode.ImpulseDirectionInvalid);

        var command = new ImpulseCommand { Players = players, Force = force };
        return context.EachTarget(ref command, ImpulseStep, PlayerKind, CommandResult.MaximumFacts, ResultCode.NativeCommitException,
            countColumn: Blocks.ActionCombatImpulse.RowTargetCount);
    }

    private struct ImpulseCommand
    {
        internal PlayerIdentityModule? Players;
        internal Vector3 Force;
    }

    private static readonly TargetStep<ImpulseCommand> ImpulseStep = ImpulseTarget;

    private static void ImpulseTarget(ref ImpulseCommand command, EntityReference target, int index, RowWriter rows)
    {
        var row = ImpulseOne(command.Players, target, command.Force);
        rows.End(row.Status, row.Commit, row.Code);
    }

    /// <summary>One recipient's push, confirmed by the full vector delta the channel reads back.</summary>
    private static Outcome ImpulseOne(PlayerIdentityModule? players, EntityReference target, Vector3 force)
    {
        // The skeleton has already answered a recipient of another kind.
        if (players == null || !players.CanCommitOwned) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var agent = players.CurrentAgent(target);
        if (agent == null) return Outcome.Rejected(ResultCode.StaleEntity);
        PlayerLocomotion? locomotion;
        try
        {
            // The push channel belongs to the machine that simulates the body; a replica would take the write and
            // never read it, so any other machine refuses by name instead of reporting a push.
            if (!agent.IsLocallyOwned) return Outcome.Rejected(ResultCode.OwnerNotAddressed);
            locomotion = agent.Locomotion;
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return Outcome.Rejected(ResultCode.StaleEntity); }
        if (locomotion == null || locomotion.Pointer == IntPtr.Zero)
            return Outcome.Rejected(ResultCode.StaleEntity);
        var agentPointer = agent.Pointer;
        var locomotionPointer = locomotion.Pointer;
        Vector3 before;
        try
        {
            before = locomotion.GetExternalPushForce();
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return Outcome.Rejected(ResultCode.StaleEntity); }
        try
        {
            var current = players.CurrentAgent(target);
            var currentLocomotion = current?.Locomotion;
            if (current == null || current.Pointer != agentPointer || currentLocomotion == null
                || currentLocomotion.Pointer != locomotionPointer)
                return Outcome.Rejected(ResultCode.ReceiverChangedDuringCommit);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return Outcome.Rejected(ResultCode.StaleEntity); }
        Vector3 readback;
        try
        {
            locomotion.AddExternalPushForce(force);
            var current = players.CurrentAgent(target);
            var currentLocomotion = current?.Locomotion;
            if (current == null || current.Pointer != agentPointer || currentLocomotion == null
                || currentLocomotion.Pointer != locomotionPointer)
                return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
            readback = currentLocomotion.GetExternalPushForce();
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return Outcome.FailedUnknown(ResultCode.NativeCommitException); }
        // The channel accumulates. Only its full vector delta proves this request: a pre-existing force may
        // cancel the request or be large enough to hide a write that never reached the body.
        bool held = Vector3.Distance(readback - before, force) <= ReadbackEpsilon;
        return held ? Outcome.Succeeded(ResultCode.ImpulseApplied) : Outcome.FailedUnknown(ResultCode.NativeCommitException);
    }

    // ------------------------------------------------------------------ stamina

    /// <summary>`forge.action.player.stamina_change`: one player's stamina, as an absolute write or a signed amount.
    /// This is an `owner` row, so it runs on the machine that simulates the player and refuses on any other
    /// (<see cref="ResultCode.OwnerNotAddressed"/>) instead of writing a replica that nothing reads.
    ///
    /// The change goes through the game's own request where its semantics match (see
    /// <see cref="StaminaPath"/>) and through one write of the member those requests end in where they do not. The
    /// value is read before the request and read back after it, and the row reports the change the receiver's own
    /// readback showed, never the one that was asked for.</summary>
    internal static CommandResult StaminaChange(PlayerIdentityModule? players, CommandContext context)
    {
        string? operation = context.Text(PlayerStaminaContract.OperationSlot);
        if (operation == null || Array.IndexOf(PlayerStaminaContract.Operations, operation) < 0) return Outcome.Rejected(ResultCode.OperationUnsupported);
        double amount = context.Number(PlayerStaminaContract.AmountSlot) ?? double.NaN;
        if (!double.IsFinite(amount)) return Outcome.Rejected(ResultCode.AmountOutOfRange);
        if (Math.Abs(amount) > 1) return Outcome.Rejected(ResultCode.AmountOutOfRange);
        // The optional cap lowers the ceiling the result is clamped to, exactly as heal's and infection's cap do:
        // the reserve is a 0..1 ratio, so a cap at or above 1 changes nothing and one at or below 0 names no
        // ceiling at all.
        double ceiling = 1;
        if (context.Present(PlayerStaminaContract.CapSlot))
        {
            double capValue = context.Number(PlayerStaminaContract.CapSlot) ?? double.NaN;
            if (!double.IsFinite(capValue) || capValue <= 0)
                return Outcome.Rejected(ResultCode.InvalidCap);
            ceiling = Math.Min(1, capValue);
        }
        // The game's own spend carries a second, in-combat amount and two resting-timer flags. They only mean
        // something to a plain uncapped subtraction, which is the one change that is a spend; any other
        // combination is refused whole rather than silently dropping half of what was asked for.
        bool combatHalf = context.Present(PlayerStaminaContract.AmountInCombatSlot);
        bool resetRecovery = context.Boolean(PlayerStaminaContract.ResetRecoverySlot) == true;
        bool resetRecoveryInCombat = context.Boolean(PlayerStaminaContract.ResetRecoveryInCombatSlot) == true;
        double amountInCombat = amount;
        if (combatHalf || resetRecovery || resetRecoveryInCombat)
        {
            if (operation != "subtract" || context.Present(PlayerStaminaContract.CapSlot))
                return Outcome.Rejected(ResultCode.StaminaCombatCostRequiresSpend);
            if (combatHalf)
            {
                amountInCombat = context.Number(PlayerStaminaContract.AmountInCombatSlot) ?? double.NaN;
                if (!double.IsFinite(amountInCombat) || amountInCombat < 0 || amountInCombat > 1)
                    return Outcome.Rejected(ResultCode.AmountOutOfRange);
            }
            if (amount < 0) return Outcome.Rejected(ResultCode.AmountOutOfRange);
        }
        bool spend = combatHalf || resetRecovery || resetRecoveryInCombat;
        if (!context.TryEntity(PlayerStaminaContract.TargetSlot, out var target))
            return Outcome.Rejected(ResultCode.MissingField);

        // The one recipient's row is the command's conclusion as it is, code included. `value` is the reserve read
        // back after the write, or the reserve found (0 before it was read) on a row that wrote nothing.
        Outcome Done(Status status, Commit commit, string code, double delta = 0, double value = 0)
        {
            context.Rows.Begin(0, target).Number(Blocks.ActionPlayerStaminaChange.RowAmount, delta)
                .Number(Blocks.ActionPlayerStaminaChange.RowValue, value).End(status, commit, code);
            return new Outcome(status, commit, code);
        }
        Outcome Refuse(string code) => Done(Status.Rejected, Commit.None, code);

        if (players == null || !players.CanCommitOwned) return Refuse(ResultCode.AuthorityOrPhase);
        if (!EntityKinds.Is(target, PlayerKind)) return Refuse(ResultCode.UnsupportedRecipient);
        var agent = players.CurrentAgent(target);
        if (agent == null) return Refuse(ResultCode.StaleEntity);
        PlayerStamina? stamina;
        float current;
        try
        {
            if (!agent.IsLocallyOwned) return Refuse(ResultCode.OwnerNotAddressed);
            stamina = agent.Stamina;
            if (stamina == null || stamina.Pointer == IntPtr.Zero) return Refuse(ResultCode.StaleEntity);
            current = stamina.Stamina;
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return Refuse(ResultCode.StaleEntity); }
        if (!float.IsFinite(current) || current < 0f || current > 1f) return Refuse(ResultCode.StaleEntity);

        double wanted = operation switch
        {
            "set" => amount,
            "add" => current + amount,
            _ => current - amount
        };
        var next = (float)Math.Clamp(wanted, 0d, ceiling);
        // An add never lowers a value already above the ceiling, as heal's and infection's cap do.
        if (operation == "add" && amount > 0 && next < current) next = current;
        // With the in-combat half the result depends on the combat state the game reads inside the spend, so the
        // lowest legal result is the larger of the two halves taken away.
        var lowest = (float)Math.Clamp(current - Math.Max(amount, amountInCombat), 0d, ceiling);
        var path = spend ? StaminaPath.Consume : StaminaPathOf(operation, amount, current, next, ceiling);
        float readback;
        bool held;
        try
        {
            switch (path)
            {
                case StaminaPath.Reset:
                    PlayerStaminaNative.Reset(stamina);
                    readback = stamina.Stamina;
                    held = readback + ReadbackEpsilon >= 1f;
                    break;
                case StaminaPath.Consume:
                    PlayerStaminaNative.Consume(stamina, (float)amount, (float)amountInCombat, resetRecovery, resetRecoveryInCombat);
                    readback = stamina.Stamina;
                    // The game spends the cost and applies its own floor, so the readback may sit above the
                    // requested result; it is confirmed when it is a legal reserve that did not rise.
                    held = float.IsFinite(readback) && readback >= lowest - ReadbackEpsilon && readback <= current + ReadbackEpsilon;
                    break;
                default:
                    PlayerStaminaNative.Write(stamina, next);
                    readback = stamina.Stamina;
                    held = Math.Abs(readback - next) <= ReadbackEpsilon;
                    break;
            }
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return Done(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException, 0, current); }
        return held
            ? Done(Status.Succeeded, Commit.Confirmed, ResultCode.StaminaChanged, readback - current, readback)
            : Done(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException, readback - current, readback);
    }

    /// <summary>How one stamina change reaches the game. `Reset` is `PlayerStamina.ResetStamina`, which fills the
    /// reserve and nothing else; `Consume` is `PlayerStamina.UseStamina`, which spends a cost, never raises the
    /// reserve and applies the game's own floor; `Write` is the minimal adapter for what neither request can
    /// say: an arbitrary set, a partial refill, a result held under a cap.</summary>
    internal enum StaminaPath { Write, Reset, Consume }

    /// <summary>The request that carries the change, chosen from what the change is. Anything that ends at full
    /// stamina is a reset. A plain subtraction is a spend, but only without a cap: the game's request has no
    /// ceiling to clamp the result to, so a capped change is written.</summary>
    internal static StaminaPath StaminaPathOf(string operation, double amount, float current, float next, double ceiling)
    {
        if (ceiling < 1) return StaminaPath.Write;
        if (next + ReadbackEpsilon >= 1f && current < 1f) return StaminaPath.Reset;
        if (operation == "subtract" && amount > 0 && next < current) return StaminaPath.Consume;
        return StaminaPath.Write;
    }

    // ------------------------------------------------------------------ camera shake

    /// <summary>`forge.action.presentation.camera_shake`: one shake per viewer whose camera this machine holds, through
    /// ForgeVisual's camera shake (the native entry takes one amplitude and no radius, dump.cs 530638, so ForgeVisual
    /// applies the authored radii, the player's own shake strength and the cap on shakes running together).
    /// `ticksPerSecond` is the rate the running world reported, and the native duration converts through it, so the
    /// row declares one time base (ticks, contract §3.2 时间单位) and writes another (seconds) only at a declared rate.
    /// Amplitude, frequency and duration have no ceiling: the native entry takes any float, so only a value the
    /// float cannot carry (the conversion below) or a negative one is refused.</summary>
    internal static CommandResult CameraShake(PlayerIdentityModule? players, CommandContext context)
    {
        if (!PresentationAudience.TryCheck(context, PresentationActionContract.CameraViewersSlot, out var refusal)) return refusal;
        double ticksPerSecond = context.TicksPerSecond;
        var ticksValue = context.Number(PresentationActionContract.CameraDurationSlot);
        if (context.Present(PresentationActionContract.CameraDurationSlot) && ticksValue == null)
            return Outcome.Rejected(ResultCode.DurationOutOfRange);
        double ticks = ticksValue ?? 0;
        double duration = ticks / ticksPerSecond;
        if (ticks < 0 || !float.IsFinite((float)duration))
            return Outcome.Rejected(ResultCode.DurationOutOfRange);
        if (ticks == 0) return Outcome.Rejected(ResultCode.CameraShakeDurationRequired);
        var amplitudeValue = context.Number(PresentationActionContract.CameraAmplitudeSlot);
        if (context.Present(PresentationActionContract.CameraAmplitudeSlot) && amplitudeValue == null)
            return Outcome.Rejected(ResultCode.CameraShakeAmplitudeOutOfRange);
        double amplitude = amplitudeValue ?? 0;
        if (amplitude < 0 || !float.IsFinite((float)amplitude))
            return Outcome.Rejected(ResultCode.CameraShakeAmplitudeOutOfRange);
        if (amplitude == 0) return Outcome.Rejected(ResultCode.CameraShakeAmplitudeRequired);
        var frequencyValue = context.Number(PresentationActionContract.CameraFrequencySlot);
        if (context.Present(PresentationActionContract.CameraFrequencySlot) && frequencyValue == null)
            return Outcome.Rejected(ResultCode.CameraShakeFrequencyOutOfRange);
        double frequency = frequencyValue ?? 0;
        if (frequency < 0 || !float.IsFinite((float)frequency))
            return Outcome.Rejected(ResultCode.CameraShakeFrequencyOutOfRange);
        var innerValue = context.Number(PresentationActionContract.CameraInnerRadiusSlot);
        var radiusValue = context.Number(PresentationActionContract.CameraRadiusSlot);
        if (context.Present(PresentationActionContract.CameraInnerRadiusSlot) && innerValue == null
            || context.Present(PresentationActionContract.CameraRadiusSlot) && radiusValue == null)
            return Outcome.Rejected(ResultCode.CameraShakeRadiusInvalid);
        double inner = innerValue ?? 0, radius = radiusValue ?? 0;
        if (inner < 0 || radius < 0) return Outcome.Rejected(ResultCode.CameraShakeRadiusInvalid);
        if (radius > 0 && inner > radius) return Outcome.Rejected(ResultCode.CameraShakeRadiusInvalid);

        var direction = Vector3.zero;
        if (context.Present(PresentationActionContract.CameraDirectionSlot))
        {
            if (GameVectors.Unity(context.Vector(PresentationActionContract.CameraDirectionSlot)) is not { } read)
                return Outcome.Rejected(ResultCode.CameraShakeDirectionInvalid);
            direction = read;
        }
        bool hasCenter = context.Present(PresentationActionContract.CameraCenterSlot);
        var center = Vector3.zero;
        if (hasCenter)
        {
            if (GameVectors.Unity(context.Vector(PresentationActionContract.CameraCenterSlot)) is not { } read)
                return Outcome.Rejected(ResultCode.CameraShakeRadiusInvalid);
            center = read;
        }

        var shake = new ShakeCommand
        {
            Players = players,
            Direction = direction,
            HasCenter = hasCenter,
            Center = center,
            Radius = radius,
            Spec = new ForgeVisual.ShakeSpec((float)amplitude, (float)radius, (float)duration, (float)frequency, (float)inner)
        };
        return context.EachTargetPresented(ref shake, ShakeStep, countColumn: Blocks.ActionPresentationCameraShake.RowTargetCount);
    }

    private struct ShakeCommand
    {
        internal PlayerIdentityModule? Players;
        internal ForgeVisual.ShakeSpec Spec;
        internal Vector3 Direction, Center;
        internal bool HasCenter;
        internal double Radius;
    }

    private static readonly TargetStep<ShakeCommand> ShakeStep = ShakeTarget;

    private static void ShakeTarget(ref ShakeCommand shake, EntityReference viewer, int index, RowWriter rows)
    {
        var (status, code, reached, elsewhere) = ShakeOne(ref shake, viewer);
        rows.Number(Blocks.ActionPresentationCameraShake.RowAmplitude, reached);
        if (elsewhere) rows.EndElsewhere(status, Commit.None, code);
        else rows.End(status, Commit.None, code);
    }

    /// <summary>One viewer's shake; <c>elsewhere</c> is a viewer another machine draws, whose own machine answers it.</summary>
    private static (Status, string, double, bool) ShakeOne(ref ShakeCommand shake, EntityReference viewer)
    {
        var agent = shake.Players?.CurrentAgent(viewer);
        if (agent == null) return (Status.Rejected, ResultCode.StaleEntity, 0, false);
        float reached;
        try
        {
            // The shake goes through ForgeVisual's one camera shake, so it obeys the player's own shake strength and the
            // cap on shakes running together; ForgeVisual shakes the local player's camera only.
            if (!agent.IsLocallyOwned) return (Status.Rejected, ResultCode.CameraUnavailable, 0, true);
            if (agent.FPSCamera is not { } camera || camera.Pointer == IntPtr.Zero)
                return (Status.Rejected, ResultCode.CameraUnavailable, 0, false);
            if (shake.Radius > 0)
            {
                var at = shake.HasCenter ? shake.Center : agent.EyePosition;
                if (!ForgeVisual.CameraShake.InRange(at, shake.Spec)) return (Status.Rejected, ResultCode.CameraShakeOutOfRange, 0, false);
                reached = ForgeVisual.CameraShake.Shake(at, shake.Spec, shake.Direction);
            }
            else reached = ForgeVisual.CameraShake.ShakeHere(shake.Spec, shake.Direction);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return (Status.Failed, ResultCode.NativeCommitException, 0, false); }
        // The shake was issued on this viewer's own camera and the tier commits no world state: the row is
        // the presentation tier's own `partial + none`, never `succeeded + none`; the amplitude is what reached the camera.
        return (Status.Partial, ResultCode.CameraShakeIssued, reached, false);
    }

    // ------------------------------------------------------------------ screen liquid

    /// <summary>`forge.action.presentation.screen_liquid`: one native liquid job for each viewer whose viewport this
    /// machine draws. A viewer of another machine is refused by name: the native entry queues the job against the
    /// one local liquid system (dump.cs 588824), so applying it for somebody else's camera would splash this
    /// machine's screen.</summary>
    internal static CommandResult ScreenLiquid(PlayerIdentityModule? players, CommandContext context)
    {
        if (!PresentationAudience.TryCheck(context, PresentationActionContract.LiquidViewersSlot, out var refusal)) return refusal;
        string? preset = context.Text(PresentationActionContract.LiquidPresetSlot);
        if (preset == null) return Outcome.Rejected(ResultCode.ScreenLiquidPresetRequired);
        int index = Array.IndexOf(PresentationActionContract.LiquidPresets, preset);
        if (index < 0) return Outcome.Rejected(ResultCode.ScreenLiquidPresetUnknown);

        var liquid = new LiquidCommand { Players = players, Preset = preset, Setting = (ScreenLiquidSettingName)index };
        return context.EachTargetPresented(ref liquid, LiquidStep, countColumn: Blocks.ActionPresentationScreenLiquid.RowTargetCount);
    }

    private struct LiquidCommand
    {
        internal PlayerIdentityModule? Players;
        internal string Preset;
        internal ScreenLiquidSettingName Setting;
    }

    private static readonly TargetStep<LiquidCommand> LiquidStep = LiquidTarget;

    private static void LiquidTarget(ref LiquidCommand liquid, EntityReference viewer, int index, RowWriter rows)
    {
        var (status, code) = LiquidOne(liquid.Players, viewer, liquid.Setting);
        rows.Text(Blocks.ActionPresentationScreenLiquid.RowPreset, liquid.Preset);
        // A viewer another machine draws is splashed and answered there.
        if (code == ResultCode.ScreenLiquidViewerUnsupported) rows.EndElsewhere(status, Commit.None, code);
        else rows.End(status, Commit.None, code);
    }

    private static (Status, string) LiquidOne(PlayerIdentityModule? players, EntityReference viewer, ScreenLiquidSettingName setting)
    {
        var agent = players?.CurrentAgent(viewer);
        if (agent == null) return (Status.Rejected, ResultCode.StaleEntity);
        bool applied;
        try
        {
            // The presentation belongs to the machine that draws the viewport, so a viewer of another machine
            // is not this half's to splash.
            if (!agent.IsLocallyOwned) return (Status.Rejected, ResultCode.ScreenLiquidViewerUnsupported);
            // The world-space entry projects a source point onto the screen; a source at the eye itself has no
            // screen point, so the job was queued and never drawn. The splash is the viewer's own screen, so it
            // goes in through the screen-space entry, at the centre and running down as the game's own drops do.
            applied = ScreenLiquidManager.DirectApply(setting, new Vector2(0.5f, 0.5f), Vector2.up);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return (Status.Failed, ResultCode.NativeCommitException); }
        // The splash was queued on this viewer's own viewport and the tier commits no world state: the row is
        // `partial + none` when it landed and a refusal when the game's own manager did not take it.
        return applied ? (Status.Partial, ResultCode.ScreenLiquidApplied) : (Status.Rejected, ResultCode.ScreenLiquidNotApplied);
    }

    // ------------------------------------------------------------------ movement-state read

    /// <summary>The one native read behind `forge.query.player.movement_state`: the agent's own locomotion machine,
    /// its current member and the time it entered that state. A player this process cannot resolve, or an agent
    /// whose locomotion half is gone, is answered as null — the row's refusal, never a substituted state.</summary>
    internal static PlayerMovementStateContract.MovementSample? ReadMovement(PlayerIdentityModule? players, EntityReference reference)
    {
        var agent = players?.CurrentAgent(reference);
        if (agent == null) return null;
        PlayerLocomotion? locomotion;
        try { locomotion = agent.Locomotion; }
        catch (Exception error) { PlayerCaughtFailures.Capture("movement-locomotion-read", error); return null; }
        if (locomotion == null || locomotion.Pointer == IntPtr.Zero) return null;
        int index;
        float entered;
        try
        {
            index = (int)locomotion.m_currentStateEnum;
            entered = locomotion.m_changeStateTime;
        }
        catch (Exception error) { PlayerCaughtFailures.Capture("movement-state-read", error); return null; }
        // A native value outside the declared members is handed on as it is: the contract refuses it by name rather
        // than mapping it onto the nearest state.
        if (index < 0 || index >= PlayerMovementStateContract.States.Length)
            return new PlayerMovementStateContract.MovementSample(string.Empty, 0, index);
        double since = Time.time - entered;
        if (!double.IsFinite(since) || since < 0) since = 0;
        return new PlayerMovementStateContract.MovementSample(PlayerMovementStateContract.States[index], since, index);
    }

    // ------------------------------------------------------------------ aim read

    /// <summary>The one native read behind `forge.query.player.aim`. The player this machine drives is read from
    /// its own camera ray, `FPSCamera.CameraRayPos` and `CameraRayDir` — the ray the game's own weapon casts start
    /// from. Every other player has no camera on this machine, so they are read from `Agent.EyePosition` and
    /// `Agent.TargetLookDir`, which `PlayerLocomotion.UpdateTargetLookDirFromSync` keeps in step with the look
    /// direction the owner's machine sends in `pPlayerLocomotion.LookDir`. A player this process cannot resolve, or
    /// a local player whose camera is gone, is answered as null — the row's refusal, never a substituted ray.</summary>
    internal static PlayerAimContract.AimSample? ReadAim(PlayerIdentityModule? players, EntityReference reference)
    {
        var agent = players?.CurrentAgent(reference);
        if (agent == null) return null;
        Vector3 origin, direction;
        try
        {
            if (agent.IsLocallyOwned)
            {
                var camera = agent.FPSCamera;
                if (camera == null || camera.Pointer == IntPtr.Zero) return null;
                origin = camera.CameraRayPos;
                direction = camera.CameraRayDir;
            }
            else
            {
                origin = agent.EyePosition;
                direction = agent.TargetLookDir;
            }
        }
        catch (Exception error) { PlayerCaughtFailures.Capture("aim-read", error); return null; }
        return new PlayerAimContract.AimSample(origin.x, origin.y, origin.z, direction.x, direction.y, direction.z);
    }

    // ------------------------------------------------------------------ stamina read

    /// <summary>The one native read behind `forge.query.player.stamina`: the agent's own stamina half, its current
    /// value, its ceiling and the ratio the game itself keeps. All three are members of `PlayerStamina` — the ceiling
    /// is that type's own static, which is why it is read off the type and not off the instance — and none is
    /// computed from the others, so a build whose ceiling is not the number the game divides by still answers the
    /// ratio the game acts on. A player this process cannot resolve, and an agent whose stamina half is gone, are
    /// answered as null — the row's refusal, never a reserve of zero.</summary>
    internal static PlayerStaminaContract.StaminaSample? ReadStamina(PlayerIdentityModule? players, EntityReference reference)
    {
        var agent = players?.CurrentAgent(reference);
        if (agent == null) return null;
        PlayerStamina? stamina;
        // Stamina is not replicated: a replica holds a value nothing updates, so only the machine that simulates the
        // player can answer. The host answers a teammate from that machine's own low-rate report, and a player with
        // no fresh report is answered as null (the row's refusal) instead of a stale reserve.
        try
        {
            if (!agent.IsLocallyOwned)
                return PlayerInputReport.Current is { } report && report.TryStamina(agent, out var reported, out var relative)
                    ? new PlayerStaminaContract.StaminaSample(reported, PlayerStamina.MaxStamina, relative) : null;
            stamina = agent.Stamina;
        }
        catch (Exception error) { PlayerCaughtFailures.Capture("stamina-agent-read", error); return null; }
        if (stamina == null || stamina.Pointer == IntPtr.Zero) return null;
        try { return new PlayerStaminaContract.StaminaSample(stamina.Stamina, PlayerStamina.MaxStamina, stamina.StaminaRelative); }
        catch (Exception error) { PlayerCaughtFailures.Capture("stamina-read", error); return null; }
    }
}
