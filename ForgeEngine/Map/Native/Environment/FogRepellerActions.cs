using System;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The native half of `forge.action.presentation.fog_repeller`: start or stop the sphere of a fog repeller the
/// level placed.
///
/// <b>What the write is.</b> `FogRepeller_Sphere.StartRepelling` and `FogRepeller_Sphere.StopRepelling` are the
/// two entries the sphere itself exposes for its own state machine, and the radius and lifetime the sphere then
/// runs with are the native configuration it already carries. Nothing here redefines either: the reviewed graph
/// declares no parameter at all, so this half invents none.
///
/// <b>How a subject is read.</b> The recipient port is an `entity many` port with no declared kind, so the row
/// names its subjects by entity and not by a resource reference: each reference is read with the `fog_repeller`
/// category's own grammar and resolved through the table the sphere's own state-change callback fills. A
/// reference of another kind, of another category of the same namespace, of another world, or one this world's
/// table does not hold is refused by its own name, per target, rather than resolved to something nearby.
///
/// <b>What it refuses.</b> A request whose `enabled` port is absent or not a boolean is refused as a whole,
/// because which side of the switch it meant would be a decision the plan did not make. A sphere that already
/// reads the state the request asks for is a confirmed success that wrote nothing — the world is in the state
/// the request names, which is the reading every other Map row makes of an already-held state — because calling
/// the native entry would otherwise move a state machine that had nothing to move.
///
/// <b>What a row reports.</b> The reading is the pair the sphere's own `Update` branches on: `RepellerEnabled`
/// and `CurrentState`. The flag is the gate the whole state machine sits behind, and the state is the phase it
/// is in — `StartRepelling` sets the flag and moves the sphere to `Grow`, `StopRepelling` moves it to `Shrink`,
/// and the flag is cleared only after the shrink has finished, so a sphere that reads enabled while it shrinks
/// is not repelling. Both readings are taken before the call and after it: a row that committed says the sphere
/// was read in the requested phase once the entry returned, and a sphere that is not in that phase is an unknown
/// commit rather than a claimed one. A throw is the other failure the call can report, and the sphere's state
/// machine may have moved before it, which is why that row carries an unknown commit too.
/// </summary>
internal sealed class FogRepellerActions
{
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;

    internal FogRepellerActions(Func<bool> ready, Action<string> report)
    {
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    private struct Request
    {
        internal FogRepellerActions Self;
        internal bool Enabled;
    }

    private static readonly TargetStep<Request> SwitchStep = SwitchOne;

    internal CommandResult Handle(CommandContext context)
    {
        // Both gates are kept for the same reason the power row keeps them: the ABI host fact says the runtime
        // dispatched this as a host step, and the game's own master flag is what the native entries need.
        if (!context.IsHost || !_ready() || !Session.IsHost) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (context.Boolean(FogRepellerContract.Enabled) is not { } enabled)
            return Outcome.Rejected(ResultCode.FogRepellerEnabledRequired);
        var request = new Request { Self = this, Enabled = enabled };
        return context.EachTarget(ref request, SwitchStep, maximum: CommandResult.MaximumFacts);
    }

    /// <summary>One repeller's row. The schema declares no count column, so the four fixed columns are all of it.</summary>
    private static void SwitchOne(ref Request request, EntityReference target, int index, RowWriter rows)
    {
        if (LevelObjectModule.FogRepellerKey(target) is not { } key)
        { rows.End(Status.Rejected, Commit.None, ResultCode.FogRepellerTargetUnsupported); return; }
        if (target.LifeEpoch != 1) { rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity); return; }
        if (!FogRepellerObservation.IsCurrent(target)
            || FogRepellerObservation.ResolveState(key) is not { } state
            || state.m_repellerSphere is not { } sphere || sphere.WasCollected)
        { rows.End(Status.Rejected, Commit.None, ResultCode.FogRepellerTargetMissing); return; }
        var answer = request.Self.One(target, state, sphere, request.Enabled);
        rows.End(answer.Status, answer.Commit, answer.Code);
    }

    /// <summary>The state read of `forge.query.map.fog_repeller`: the sphere's own `RepellerEnabled` flag, which
    /// is the gate the whole state machine sits behind and the member <see cref="One"/> reads before it writes.
    /// The row reports the flag itself and nothing derived from it: a sphere whose shrink has not finished still
    /// reads enabled, which is what the flag means, and turning that into "still repelling" would be a second
    /// state machine beside the sphere's own. A reference that is not a `fog_repeller` level object, or one this
    /// world's table does not hold, answers null and the read refuses by name.</summary>
    internal static bool? ReadEnabled(EntityReference reference)
    {
        if (!FogRepellerObservation.IsCurrent(reference)
            || LevelObjectModule.FogRepellerKey(reference) is not { } key) return null;
        // A shrinking sphere still reads enabled while it turns itself off; the read answers what it is doing.
        return FogRepellerObservation.Resolve(key) is { } sphere ? Repelling(sphere) : null;
    }

    /// <summary>
    /// Whether one sphere is repelling, or on its way there. This is the pair the sphere's own `Update` branches
    /// on and not one member of it: `RepellerEnabled` is the gate the whole state machine sits behind, and
    /// `CurrentState` is the phase it is in — `Shrink` is the sphere turning itself off, so a sphere that still
    /// reads enabled while it shrinks is not repelling. Reading only the flag would answer a request to start a
    /// shrinking sphere with "already repelling" while the sphere finished turning off.
    /// </summary>
    internal static bool Repelling(FogRepeller_Sphere sphere)
        => sphere.RepellerEnabled
           && sphere.CurrentState is eFogRepellerSphereState.Initialize or eFogRepellerSphereState.Grow
               or eFogRepellerSphereState.Life;

    /// <summary>
    /// One repeller's own decision: the state it reads now, the one native call when that is not the state the
    /// request asked for, and the state it reads after that call. A repeller that already reads the requested
    /// state is a confirmed commit that wrote nothing — the world is in the state the request names — which is
    /// the reading every other Map row makes of an already-held state and the only one the ABI accepts beside
    /// `succeeded`.
    ///
    /// The native global-state interaction is read back through the local sphere. If replication or native
    /// initialization has not moved that sphere to the requested phase when the call returns, a
    /// sphere that is not in the requested phase after the call is a request whose outcome cannot be claimed:
    /// `StartRepelling` on a sphere whose own `m_isInitialized` is not set leaves the flag set and the state
    /// `Disabled`, which the sphere's next `Update` clears. That row is an unknown commit and not a started one.
    /// </summary>
    private Outcome One(EntityReference reference, HeavyFogRepellerGlobalState state, FogRepeller_Sphere sphere, bool enabled)
    {
        try
        {
            var current = state.CurrentState;
            // A held repeller is no longer the placed device this row addresses. Preserve every other native
            // owner value, including a deactivated state: no reset semantics or replacement pose is invented.
            if (current.owner == eCarryItemWithGlobalStateOwner.Player)
                return Outcome.Rejected(ResultCode.FogRepellerTargetUnsupported);
            if (Repelling(sphere) == enabled)
                return Outcome.Succeeded(ResultCode.AlreadyInState);
            // The global-state interaction is the native replicated owner of this per-peer sphere. Calling
            // StartRepelling/StopRepelling directly would change only the host's presentation instance.
            Switch(state, enabled);
            if (Repelling(sphere) != enabled)
            {
                Report("map.fog-repeller-state-not-reached: " + reference.Id
                    + " enabled=" + (enabled ? "true" : "false")
                    + " flag=" + (sphere.RepellerEnabled ? "true" : "false") + " state=" + sphere.CurrentState);
                return Outcome.FailedUnknown(ResultCode.FogRepellerStateNotReached);
            }
        }
        catch (Exception error)
        {
            Report("map.fog-repeller-switch-failed: " + error.GetType().Name + ": " + error.Message);
            return Outcome.FailedUnknown(ResultCode.NativeCommitException);
        }
        return Outcome.Succeeded(enabled ? ResultCode.FogRepellerStarted : ResultCode.FogRepellerStopped);
    }

    /// <summary>The one native entry that switches a sphere: the global-state interaction, the replicated owner of this per-peer sphere.</summary>
    internal static void Switch(HeavyFogRepellerGlobalState state, bool enabled)
    {
        var current = state.CurrentState;
        state.AttemptInteract(new pCarryItemWithGlobalState_Interaction
        {
            type = (byte)(enabled ? eHeavyFogRepellerInteraction.Activate : eHeavyFogRepellerInteraction.Deactivate),
            owner = current.owner,
            player = current.player,
            staticPosition = current.staticPosition
        });
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { MapLog.Contained("fog-repeller.report", reportError); }
    }
}
