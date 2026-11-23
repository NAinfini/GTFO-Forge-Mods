using ForgeEngine.GameBindings;
using System;
using ForgeEngine.Framework;
using Player;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>The two player-domain actions this package submits, on build 20403457. Each one ends in a game entry
/// point that is itself the replication path, and each one is refused by name when the request asks for something
/// that entry point cannot carry:
///
/// - teleport submits the game's own warp request, `PlayerAgent.RequestWarpToSync`. Its session half
///   (`PlayerSync.SendSyncWarp`) sends the move and applies it only on the machine that simulates the player, and
///   the receive half (`IncomingSyncWarp`) applies it only there too, so the row is an `owner` row: the kernel sends
///   it to the machine of each player it names, which warps its own player and reads the landing back in the same
///   call, on host and client alike. The landing point is not taken on trust: the game's own
///   `PlayerAgent.SampleWarpPosition` solves it for the destination, and the agent's own locomotion state must be
///   one of its `m_warpableStates`, which is the same gate the warp applies. The destination is asked for in the
///   authored room's dimension when a named map area is supplied, otherwise in the reality dimension.
///   The write is confirmed by the agent's own position read
///   back, never by the call having been made: a warp the game declined leaves the position where it was, which
///   is an unknown commit rather than a success.
/// - infection change submits the player damage base's own `ModifyInfection` entry with `sync` set, which is the
///   player receiver's replicated write: the same call sends `m_receiveModifyInfectionPacket` to the other
///   machines. The requested operation maps to the native mode where the game has one (`Set`, `Add`) and is
///   computed as an absolute value where it does not (`subtract`), and the row reports the infection the
///   receiver's own readback showed. The native path owns player infection resistance through
///   AgentModifier.InfectionResistance; the action carries no per-request resistance override.
///
/// A teleport that carries an `area_field` is the same warp with the field as its range. A named map area keeps
/// its authored room placement; a reusable geometry definition is centred on the requested destination.
/// The landing the game's sampler solves must lie inside that volume. Definitions are read from the same published
/// runtime resources the field provider owns the kind through, with the shared reader
/// (`ForgeEngine.Framework.AreaFieldTable`), so the accepted grammar and the duplicate-id rule exist once; a field
/// this install does not ship is refused by name rather than treated as the bare coordinate, because a destination
/// constrained to a range is a different request from an unconstrained one.
///
/// What neither action does is invent state: no field is written directly, no client-side value is guessed, and
/// every refusal carries the code that names why.</summary>
internal static class PlayerActions
{
    /// <summary>The dimension a landing is read in when the request carries a field area that names none (a
    /// field is a coordinate range). A request with no area at all lands each recipient in the dimension that
    /// recipient is standing in; a named map area reads its room's dimension.</summary>
    private static readonly eDimensionIndex FieldAreaDimension = eDimensionIndex.Reality;


    /// <summary>The delta below which an infection readback is the value that was asked for. The native field is a
    /// float the receiver owns, so the write is confirmed by the value it stored, not by the call returning.</summary>
    internal const float InfectionTolerance = 0.001f;

    /// <summary>The bound on one infection request, in the receiver's own unit. It exists so a plan cannot ask for
    /// a value no infection model could hold; it is not a claim about the game's own ceiling.</summary>
    internal const double MaximumAmount = 1000;

    /// <summary>The field one request's `area` resource port names. A plan compiles an author's own block reference
    /// and a value an upstream step produced arrives as the frame's two-field form; the id is the instance either
    /// way, and the provider's own table decides whether this world holds it.</summary>
    // ---- forge.action.player.teleport --------------------------------------------------------------------

    /// <summary>The `forge.action.player.teleport` command handler. The request is checked once, before the first
    /// recipient: a field this install does not ship and a destination (or a
    /// landing) it cannot solve refuse the whole command, because half a teleport is not a state the plan asked
    /// for.</summary>
    internal static CommandResult Teleport(PlayerIdentityModule? players, CommandContext context)
    {
        // An owner row: it runs on the machine of every player it names, which is any peer.
        if (players == null || !players.CanCommitOwned)
            return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        // The area port names a field definition this install ships. A definition this install does not have is a
        // different request from the bare coordinate, so it is refused rather than silently treated as the bare one.
        AreaFieldDefinition? area = null;
        if (context.Present(PlayerActionContract.TeleportAreaSlot))
        {
            if (!context.TryResource(PlayerActionContract.TeleportAreaSlot, out var areaReference)
                || areaReference.ResourceKind != AreaFieldDocument.ResourceKind)
                return Outcome.Rejected(ResultCode.AreaReferenceInvalid);
            // The engine's one area-field table for the current world, the same one the trigger rows measure against.
            area = ForgeEngine.Trigger.Native.TriggerHost.Fields?.Current.Definition(areaReference.ResourceId);
            if (area == null) return Outcome.Rejected(ResultCode.AreaFieldUnloaded);
        }
        // A named map area may carry an authored landing (world point and forward vector). A wired destination or
        // rotation overrides it; without a destination and without a landing the request is refused by name.
        var authored = area != null && area.Id.StartsWith("teleport-area:", StringComparison.Ordinal)
            ? ForgeMap.Native.MapHost.Session?.AreaLanding(area.Id, context.WorldEpoch) : null;
        // The dimension the destination is in. Without one (and without an area) it is whichever dimension each
        // recipient stands in.
        eDimensionIndex? named = null;
        if (context.Present(PlayerActionContract.TeleportDimensionSlot))
        {
            if (context.Integer(PlayerActionContract.TeleportDimensionSlot) is not { } index
                || index < 0 || index >= (int)eDimensionIndex.MAX_COUNT)
                return Outcome.Rejected(ResultCode.DimensionOutOfRange);
            named = (eDimensionIndex)index;
        }
        // A named dimension with no destination and no area lands each recipient on that dimension's own landing
        // point, the one the game's dimension warp picks for that player (`LG_Dimension.GetSpawnPoint`).
        bool ownLanding = named != null && area == null && !context.Present(PlayerActionContract.TeleportDestinationSlot);
        Vector3 destination = default, look = default;
        if (context.Present(PlayerActionContract.TeleportDestinationSlot))
        {
            if (GameVectors.Unity(context.Vector(PlayerActionContract.TeleportDestinationSlot)) is not { } wired)
                return Outcome.Rejected(ResultCode.DestinationInvalid);
            destination = wired;
        }
        else if (authored != null) destination = new Vector3((float)authored[0], (float)authored[1], (float)authored[2]);
        else if (!ownLanding) return Outcome.Rejected(area != null ? ResultCode.AreaLandingUnavailable : ResultCode.DestinationInvalid);
        bool lookWired = context.Present(PlayerActionContract.TeleportRotationSlot);
        if (lookWired)
        {
            if (GameVectors.Unity(context.Vector(PlayerActionContract.TeleportRotationSlot)) is not { } rotation)
                return Outcome.Rejected(ResultCode.RotationMissing);
            look = LookDirection(rotation.x, rotation.y, rotation.z);
        }
        else if (authored != null) look = new Vector3((float)authored[3], (float)authored[4], (float)authored[5]);
        // Otherwise `look` stays zero: each recipient keeps the facing it had (see WarpOne).

        // No area: the landing is solved and warped per recipient, on that recipient's own machine, in the named
        // dimension or else in the dimension the recipient stands in.
        if (area == null)
        {
            var own = new WarpCommand
            {
                Players = players,
                PerRecipient = true,
                Named = named,
                OwnLanding = ownLanding,
                LookWired = lookWired,
                Point = destination,
                Look = look,
                HolderOnly = true
            };
            return context.EachTarget(ref own, WarpStep, EntityKinds.Player, CommandResult.MaximumFacts, ResultCode.NativeCommitException,
                countColumn: Blocks.ActionPlayerTeleport.RowTargetCount);
        }

        var warpDimension = FieldAreaDimension;
        if (area != null && area.Id.StartsWith("teleport-area:", StringComparison.Ordinal))
        {
            var dimension = ForgeMap.Native.MapHost.Session?.AreaDimension(area.Id, context.WorldEpoch);
            if (dimension == null) return Outcome.Rejected(ResultCode.AreaFieldUnloaded);
            if (!Enum.IsDefined(typeof(eDimensionIndex), dimension.Value)) return Outcome.Rejected(ResultCode.DimensionOutOfRange);
            warpDimension = (eDimensionIndex)dimension.Value;
        }
        // The area already says which dimension it is in; a different one is a contradiction, not a choice.
        if (named != null && named.Value != warpDimension) return Outcome.Rejected(ResultCode.DimensionOutOfRange);

        // The landing point is solved once per command by the game's own sampler; a destination it cannot answer
        // for is refused before any recipient is touched.
        Vector3 landing;
        try
        {
            if (!PlayerAgent.SampleWarpPosition(warpDimension, destination, out landing))
                return Outcome.Rejected(ResultCode.DestinationUnavailable);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.DestinationUnavailable, error); return Outcome.Rejected(ResultCode.DestinationUnavailable); }
        if (!float.IsFinite(landing.x) || !float.IsFinite(landing.y) || !float.IsFinite(landing.z))
            return Outcome.Rejected(ResultCode.DestinationUnavailable);
        // The field is the range the landing is allowed to fall in. The sampler resolves colliders on its way in,
        // so a landing it pushed out of the field is refused by name instead of
        // warping a player to a place the plan's own range excludes.
        if (area != null)
        {
            var point = new[] { (double)landing.x, (double)landing.y, (double)landing.z };
            if (area.Id.StartsWith("teleport-area:", StringComparison.Ordinal))
            {
                // A named map range is fixed in its authored room, never recentered on the requested destination.
                var inside = ForgeMap.Native.MapHost.Session?.AreaContains(area.Id, context.WorldEpoch, point);
                if (inside == null) return Outcome.Rejected(ResultCode.AreaFieldUnloaded);
                if (!inside.Value) return Outcome.Rejected(ResultCode.DestinationOutsideArea);
            }
            else if (!area.Contains(point, new[] { (double)destination.x, (double)destination.y, (double)destination.z }))
                return Outcome.Rejected(ResultCode.DestinationOutsideArea);
        }

        var fixedLanding = new WarpCommand { Players = players, Dimension = warpDimension, Point = landing, Look = look, HolderOnly = true };
        return context.EachTarget(ref fixedLanding, WarpStep, EntityKinds.Player, CommandResult.MaximumFacts, ResultCode.NativeCommitException,
            countColumn: Blocks.ActionPlayerTeleport.RowTargetCount);
    }

    /// <summary>What every recipient of one teleport shares: either one landing already solved in one dimension, or
    /// (<see cref="PerRecipient"/>) a landing solved for each recipient: in <see cref="Named"/> or else the dimension
    /// that recipient stands in, at <see cref="Point"/> or else (<see cref="OwnLanding"/>) at the dimension's own
    /// landing point for that player, whose facing is used unless one was wired (<see cref="LookWired"/>).</summary>
    private struct WarpCommand
    {
        internal PlayerIdentityModule Players;
        internal bool PerRecipient, OwnLanding, LookWired;
        internal eDimensionIndex? Named;
        /// <summary>The row runs on each recipient's own machine, so a recipient this machine does not simulate is
        /// another holder's to answer and is never sent a second request from here.</summary>
        internal bool HolderOnly;
        internal eDimensionIndex Dimension;
        internal Vector3 Point, Look;
    }

    private static readonly TargetStep<WarpCommand> WarpStep = WarpTarget;

    private static void WarpTarget(ref WarpCommand command, EntityReference target, int index, RowWriter rows)
    {
        var row = command.PerRecipient ? WarpPerRecipient(ref command, target)
            : WarpOne(command.Players, target, command.Dimension, command.Point, command.Look, command.HolderOnly);
        rows.End(row.Status, row.Commit, row.Code);
    }

    /// <summary>Whether this machine simulates the agent, or null when its owner cannot be read.</summary>
    private static bool? Simulated(PlayerAgent agent)
    {
        try { return ForgeCore.Players.SimulatedHere(agent.Owner); }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return null; }
    }

    /// <summary>One recipient's warp whose landing is solved for that recipient, on its own machine: in the named
    /// dimension or else the one the recipient stands in, at the wired destination or else at the named dimension's
    /// own landing point for that player (`LG_Dimension.GetSpawnPoint`, the point the game's dimension warp uses and
    /// the one an authored dimension landing answers through). The game's own sampler solves the landing and the
    /// shared warp body commits and reads it back.</summary>
    private static Outcome WarpPerRecipient(ref WarpCommand command, EntityReference target)
    {
        if (!EntityKinds.Is(target, EntityKinds.Player)) return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        var agent = command.Players.CurrentAgent(target);
        if (agent == null) return Outcome.Rejected(ResultCode.StaleEntity);
        // The dimension a recipient stands in, and its landing point, are read where the recipient is simulated.
        if (Simulated(agent) is not { } here) return Outcome.Rejected(ResultCode.StaleEntity);
        if (!here) return Outcome.Rejected(ResultCode.OwnerNotAddressed);
        eDimensionIndex dimension;
        Vector3 requested = command.Point, look = command.Look, landing;
        try
        {
            dimension = command.Named ?? agent.DimensionIndex;
            if (command.Named != null)
            {
                // Qualified: `AIGraph` declares its own `Dimension` in some builds.
                if (!LevelGeneration.Dimension.GetDimension(dimension, out var native) || native == null)
                    return Outcome.Rejected(ResultCode.DimensionOutOfRange);
                if (command.OwnLanding)
                {
                    var level = native.DimensionLevel;
                    var point = level == null || level.WasCollected ? null : level.GetSpawnPoint(agent);
                    if (point == null) return Outcome.Rejected(ResultCode.DestinationUnavailable);
                    requested = point.position;
                    if (!command.LookWired) look = point.forward;
                }
            }
            if (!PlayerAgent.SampleWarpPosition(dimension, requested, out landing)) return Outcome.Rejected(ResultCode.DestinationUnavailable);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.DestinationUnavailable, error); return Outcome.Rejected(ResultCode.DestinationUnavailable); }
        if (!float.IsFinite(landing.x) || !float.IsFinite(landing.y) || !float.IsFinite(landing.z))
            return Outcome.Rejected(ResultCode.DestinationUnavailable);
        return WarpOne(command.Players, target, dimension, landing, look, holderOnly: true);
    }

    /// <summary>The Map-facing body of "move this one player to an already solved Reality landing", called on the
    /// host: the same gate and warp the teleport row uses, answered as one plain outcome. A player the host
    /// simulates is read back; one another machine simulates is sent the request and answers `warp-not-observed`
    /// with its commit unknown.</summary>
    internal static (string Status, string CommitState, string Code) WarpToReality(PlayerIdentityModule? players,
        EntityReference target, Vector3 landing, Vector3 look)
    {
        if (players == null) return (CommandStatuses.Rejected, CommitStates.None, PlayerDirectoryService.MissingCode);
        if (!players.CanCommit) return (CommandStatuses.Rejected, CommitStates.None, ResultCode.AuthorityOrPhase);
        var outcome = WarpOne(players, target, eDimensionIndex.Reality, landing, look, holderOnly: false);
        return (Outcomes.StatusNames[(int)outcome.Status], Outcomes.CommitNames[(int)outcome.Commit], outcome.Code);
    }

    /// <summary>One recipient's warp. Every step before the native call is a read the caller could re-check, and
    /// the agent is resolved again after the call: a life replaced in between leaves the effect of the write
    /// unknown rather than attributed to whoever holds the reference now. The dimension is the caller's, because
    /// the authored teleport and the dimension warp differ in exactly that: where the landing is read back.
    /// <paramref name="holderOnly"/> is the teleport row's rule: it runs on every recipient's own machine, so a
    /// recipient simulated elsewhere is refused here (`owner-not-addressed`) and that machine's row is the answer.
    /// Without it the host also sends the request for a player it does not simulate; nothing on the host moves
    /// then, so that write is unobserved (`warp-not-observed`, commit unknown).</summary>
    private static Outcome WarpOne(PlayerIdentityModule players, EntityReference target,
        eDimensionIndex dimension, Vector3 landing, Vector3 look, bool holderOnly)
    {
        if (!EntityKinds.Is(target, EntityKinds.Player)) return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        if (!players.CanCommitOwned) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var agent = players.CurrentAgent(target);
        if (agent == null) return Outcome.Rejected(ResultCode.StaleEntity);
        var pointer = agent.Pointer;
        if (players.CurrentAgent(target)?.Pointer != pointer) return Outcome.Rejected(ResultCode.StaleEntity);
        if (Simulated(agent) is not { } here) return Outcome.Rejected(ResultCode.StaleEntity);
        if (holderOnly && !here) return Outcome.Rejected(ResultCode.OwnerNotAddressed);
        // A request that names no facing keeps the one the player had before the warp.
        if (look == Vector3.zero)
        {
            try { look = agent.TargetLookDir; }
            catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.StaleEntity, error); return Outcome.Rejected(ResultCode.StaleEntity); }
        }

        // ForgeCore's warp: the game's own gate (life and the states the agent accepts a warp in) refuses by name instead of
        // submitting and being ignored, the machine that simulates the agent applies the move in the call, and the position
        // is read back there.
        var warp = ForgeCore.Players.Warp(agent, dimension, landing, look, ForgeCore.WarpMode.Synced);
        switch (warp.Refusal)
        {
            case ForgeCore.WarpRefusal.None: break;
            case ForgeCore.WarpRefusal.NotAlive: return Outcome.Rejected(ResultCode.NotAlive);
            case ForgeCore.WarpRefusal.State: return Outcome.Rejected(ResultCode.WarpStateRefused);
            // A client asked to move a player it does not simulate.
            case ForgeCore.WarpRefusal.NotHost: return Outcome.Rejected(ResultCode.OwnerNotAddressed);
            case ForgeCore.WarpRefusal.Faulted: return Outcome.FailedUnknown(ResultCode.NativeCommitException);
            default: return Outcome.Rejected(ResultCode.StaleEntity);
        }
        // Sent from the host to the machine that simulates the player: the landing happens there, later.
        if (!here) return Outcome.FailedUnknown(ResultCode.WarpNotObserved);

        var after = players.CurrentAgent(target);
        // The life the request was made for is no longer the life this reference names; whether the warp
        // landed on it is not observable from here.
        if (after == null || after.Pointer != pointer) return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
        if (float.IsNaN(warp.Distance)) return Outcome.FailedUnknown(ResultCode.ReadbackException);
        return warp.Landed ? Outcome.Succeeded() : Outcome.FailedUnknown(ResultCode.WarpNotObserved);
    }

    // ---- forge.action.player.infection_change ------------------------------------------------------------

    /// <summary>The `forge.action.player.infection_change` command handler. The operation and the amount are
    /// checked once for the command; the ceiling is applied per recipient, because it bounds the value that
    /// recipient's receiver ends up holding.</summary>
    internal static CommandResult Infection(PlayerIdentityModule? players, CommandContext context)
    {
        if (players == null || !players.CanCommit)
            return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var operation = context.Text(PlayerActionContract.InfectionOperationSlot);
        if (operation == null || Array.IndexOf(ForgePlayer.PlayerStaminaContract.Operations, operation) < 0)
            return Outcome.Rejected(ResultCode.OperationUnsupported);
        // `enable_fx` selects the native effect member the write carries: the receiver's own disinfection FX when
        // written true, and the neutral member when it is unwritten or false. It is a structural parameter, so the
        // whole command is read once and every recipient's write carries the same choice.
        bool enableFx = context.Boolean(PlayerActionContract.InfectionEnableFxSlot) == true;
        double amount = context.Number(PlayerActionContract.InfectionAmountSlot) ?? double.NaN;
        if (!double.IsFinite(amount) || amount < 0 || amount > MaximumAmount)
            return Outcome.Rejected(ResultCode.AmountOutOfRange);
        double? cap = null;
        if (context.Present(PlayerActionContract.InfectionCapSlot))
        {
            double capValue = context.Number(PlayerActionContract.InfectionCapSlot) ?? double.NaN;
            if (!double.IsFinite(capValue) || capValue < 0)
                return Outcome.Rejected(ResultCode.CapOutOfRange);
            cap = capValue;
        }
        var command = new InfectionCommand { Players = players, Operation = operation, Amount = amount, Cap = cap, EnableFx = enableFx };
        return context.EachTarget(ref command, InfectionStep, EntityKinds.Player, CommandResult.MaximumFacts, ResultCode.NativeCommitException,
            countColumn: Blocks.ActionPlayerInfectionChange.RowTargetCount);
    }

    private struct InfectionCommand
    {
        internal PlayerIdentityModule Players;
        internal string Operation;
        internal double Amount;
        internal double? Cap;
        internal bool EnableFx;
    }

    private static readonly TargetStep<InfectionCommand> InfectionStep = InfectionTarget;

    private static void InfectionTarget(ref InfectionCommand command, EntityReference target, int index, RowWriter rows)
    {
        var row = InfectOne(command.Players, target, command.Operation, command.Amount, command.Cap, command.EnableFx);
        rows.End(row.Status, row.Commit, row.Code);
    }

    /// <summary>One recipient's infection change. The receiver is read before the write, the value the operation
    /// asks for is computed from that read, the same receiver is proved again before anything is submitted, and
    /// the row reports what the receiver holds afterwards — never what was requested.</summary>
    private static Outcome InfectOne(PlayerIdentityModule players, EntityReference target, string operation, double amount, double? cap, bool enableFx)
    {
        if (!EntityKinds.Is(target, EntityKinds.Player)) return Outcome.Rejected(ResultCode.UnsupportedRecipient);
        if (!players.CanCommit) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var agent = players.CurrentAgent(target);
        if (agent == null) return Outcome.Rejected(ResultCode.StaleEntity);
        var pointer = agent.Pointer;
        bool alive;
        Dam_PlayerDamageBase? damage;
        float current;
        try
        {
            alive = agent.Alive;
            damage = agent.Damage;
            if (damage == null || damage.Pointer == IntPtr.Zero || !damage.IsSetup) return Outcome.Rejected(ResultCode.MissingInfectionReceiver);
            if (damage.Owner == null || damage.Owner.Pointer != pointer) return Outcome.Rejected(ResultCode.InfectionReceiverOwnerMismatch);
            current = damage.Infection;
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.MissingInfectionReceiver, error); return Outcome.Rejected(ResultCode.MissingInfectionReceiver); }
        if (!alive) return Outcome.Rejected(ResultCode.NotAlive);
        if (!float.IsFinite(current) || current < 0) return Outcome.Rejected(ResultCode.MissingInfectionReceiver);

        // `add` is the one operation the native mode itself carries, so it is submitted as the delta it is; the
        // ceiling then bounds the delta by what is left under it, and an `add` never lowers a value already above
        // the ceiling. Every other operation submits an absolute request before native resistance: `set` names
        // it, and `subtract` computes it because `pInfectionMode` has `Set` and `Add` only.
        double submitted;
        if (operation == "add")
        {
            submitted = cap.HasValue ? Math.Max(0, Math.Min(amount, cap.Value - current)) : amount;
        }
        else
        {
            submitted = operation == "set" ? amount : Math.Max(0, current - amount);
            if (cap.HasValue) submitted = Math.Min(submitted, cap.Value);
        }
        var mode = operation == "add" ? pInfectionMode.Add : pInfectionMode.Set;
        // Native resistance multiplies every positive submitted amount, `Set` included, so an absolute value is
        // written divided by the resistance it will meet: the value that lands is the one the author wrote.
        double written = submitted;
        if (mode == pInfectionMode.Set && submitted > 0)
        {
            float resistance;
            try { resistance = AgentModifierManager.ApplyModifier(agent, AgentModifier.InfectionResistance, 1f); }
            catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.MissingInfectionReceiver, error); return Outcome.Rejected(ResultCode.MissingInfectionReceiver); }
            if (!float.IsFinite(resistance) || resistance <= 0f) return Outcome.Rejected(ResultCode.AmountOutOfRange);
            written = submitted / resistance;
        }
        var value = new pInfection
        {
            amount = (float)written,
            mode = mode,
            effect = enableFx ? pInfectionEffect.DisinfectionPack : pInfectionEffect.None
        };

        // Build 20403457: ModifyInfection(sync:true) applies native resistance to every positive
        // submitted amount (Set as well as Add), then clamps the stored value to 0..1. Predict with
        // that same native API only for readback; submit the pre-resistance amount so resistance runs once.
        float nativeAmount = (float)written;
        try
        {
            if (nativeAmount > 0)
                nativeAmount = AgentModifierManager.ApplyModifier(agent, AgentModifier.InfectionResistance, nativeAmount);
        }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.MissingInfectionReceiver, error); return Outcome.Rejected(ResultCode.MissingInfectionReceiver); }
        if (!float.IsFinite(nativeAmount)) return Outcome.Rejected(ResultCode.MissingInfectionReceiver);
        double desired = Math.Clamp(operation == "add" ? current + nativeAmount : nativeAmount, 0f, 1f);

        // A native read can re-enter other mods, so the state the value was computed from is proved again before
        // the write; a change is a refusal, never a value applied to a stale read.
        if (Changed(players, target, pointer, current, damage.Pointer) is { } changed) return Outcome.Rejected(changed);

        try { ForgeCore.PlayerHits.WriteAuthoredInfection(damage, value, true, true); }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.NativeCommitException, error); return Outcome.FailedUnknown(ResultCode.NativeCommitException); }

        var after = players.CurrentAgent(target)?.Damage;
        if (after == null || after.Pointer != damage.Pointer || after.Owner == null || after.Owner.Pointer != pointer)
            return Outcome.FailedUnknown(ResultCode.ReceiverChangedDuringCommit);
        float stored;
        try { stored = after.Infection; }
        catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.ReadbackException, error); return Outcome.FailedUnknown(ResultCode.ReadbackException); }
        if (!float.IsFinite(stored)) return Outcome.FailedUnknown(ResultCode.ReadbackException);
        if (Math.Abs(stored - desired) > InfectionTolerance)
        {
            // The row says only that the receiver holds something else; which number disagreed is in the log.
            bool local;
            try { local = agent.IsLocallyOwned; }
            catch (Exception error) { PlayerCaughtFailures.Capture(ResultCode.UnexpectedInfectionReadback, error); local = false; }
            PlayerCaughtFailures.Note(ResultCode.UnexpectedInfectionReadback, FormattableString.Invariant(
                $"target={target.Id} local={local} op={operation} effect={value.effect} before={current} submitted={submitted} resisted={nativeAmount} expected={desired} stored={stored}"));
            return Outcome.FailedUnknown(ResultCode.UnexpectedInfectionReadback);
        }
        return Outcome.Succeeded();
    }

    /// <summary>Proves the receiver the value was computed from is still the one the reference names, and answers
    /// with the refusal when it is not: another agent or damage base is a changed receiver, the same receiver
    /// holding another infection value is a changed state.</summary>
    private static string? Changed(PlayerIdentityModule players, EntityReference target, IntPtr pointer, float current, IntPtr receiver)
    {
        var agent = players.CurrentAgent(target);
        var damage = agent?.Damage;
        if (agent == null || agent.Pointer != pointer || damage == null || damage.Pointer != receiver
            || damage.Owner == null || damage.Owner.Pointer != pointer) return ResultCode.ReceiverChangedDuringCommit;
        return damage.Infection == current ? null : ResultCode.StateChangedBeforeCommit;
    }

    /// <summary>The look direction a warp takes, from the catalog's `rotation` port. The port carries euler angles
    /// in degrees and the native entry takes a direction, so the forward vector is built with Unity's own euler
    /// order (Z, then X, then Y): roll cannot tilt a forward vector, pitch lifts it and yaw turns it.</summary>
    internal static Vector3 LookDirection(float x, float y, float z)
    {
        float pitch = DegreesToRadians(x), yaw = DegreesToRadians(y);
        float cosPitch = MathF.Cos(pitch);
        return new Vector3(MathF.Sin(yaw) * cosPitch, -MathF.Sin(pitch), MathF.Cos(yaw) * cosPitch);
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
