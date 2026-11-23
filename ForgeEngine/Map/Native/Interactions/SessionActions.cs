using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using SNetwork;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Native bodies for session-wide Map actions. Checkpoint save is the game's own host save: the store
/// through CheckpointManager's replicated interaction channel (the static StoreCheckpoint is that same call), then
/// the Checkpoint buffer captured and sent.</summary>
internal sealed partial class SessionActions
{
    private const string PlayerKindPrefix = "gtfo.player:";
    private const float AnchorTolerance = 0.01f;

    private readonly Func<bool> _ready;
    private readonly Action<string> _report;
    private readonly Func<EntityReference, RuntimePlayerLife?> _life;

    /// <param name="life">The Player package's life record of one player, or null when it cannot be read.</param>
    internal SessionActions(Func<bool> ready, Action<string> report, Func<EntityReference, RuntimePlayerLife?> life)
    {
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _life = life ?? throw new ArgumentNullException(nameof(life));
    }

    internal CommandResult CheckpointSave(CommandContext context)
    {
        if (!context.IsHost || !_ready()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        int count = context.Count(SessionActionContract.Participants);
        if (count < 0) return Outcome.Rejected(ResultCode.MissingField);
        if (count > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        bool anyAccepted = false;
        for (int index = 0; index < count; index++)
        {
            if (!context.TryEntity(SessionActionContract.Participants, index, out var participant))
                return Outcome.Rejected(ResultCode.UnsupportedRecipient);
            anyAccepted |= IsLivePlayer(participant, context.WorldEpoch);
        }
        if (context.Vector(SessionActionContract.Anchor) is not { } value
            || !double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z)
            || value.X < float.MinValue || value.X > float.MaxValue
            || value.Y < float.MinValue || value.Y > float.MaxValue
            || value.Z < float.MinValue || value.Z > float.MaxValue)
            return Outcome.Rejected(ResultCode.CheckpointAnchorInvalid);
        var anchor = new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        var manager = CheckpointManager.Current;
        var replicator = manager == null || manager.WasCollected ? null : manager.m_stateReplicator;
        var capture = SNet.Capture;
        if (replicator == null || capture == null) return Outcome.Rejected(ResultCode.CheckpointUnavailable);
        if (ForgeCore.Checkpoint.Recalling) return Outcome.Rejected(ResultCode.CheckpointReloading);

        // Rows are judged one by one: a stale or non-player participant is refused on its own row and the store
        // still runs for the rest, because the store itself is session-wide and not per participant.
        if (!anyAccepted) return Outcome.Rejected(ResultCode.CheckpointParticipantsRequired);

        // The game's save event (EventAPI.OnCheckpointReached, where Forge and every package take their own snapshot)
        // is a moved door lock position. A store at the position the game already holds would capture the game's
        // buffer again without that event and leave those snapshots behind it, so it is refused before any write.
        // The comparison is the game's own Vector3 equality, the one the event is decided by.
        if (replicator.State.doorLockPosition == anchor) return Outcome.Rejected(ResultCode.CheckpointUnavailable);

        // The three host steps of the game's own save, in its order (the checkpoint door's scan-done callback):
        // the replicated store, the capture of the game state into the Checkpoint buffer, and that buffer sent to
        // every player. The store alone leaves no buffer for a reload to recall.
        bool stored = false;
        try
        {
            manager!.AttemptInteract(new pCheckpointInteraction(eCheckpointInteractionType.StoreCheckpoint, anchor));
            var state = replicator.State;
            stored = state.lastInteraction == eCheckpointInteractionType.StoreCheckpoint
                && Vector3.Distance(state.doorLockPosition, anchor) <= AnchorTolerance;
            if (stored)
            {
                capture.CaptureGameState(eBufferType.Checkpoint);
                capture.SendBuffer(eBufferType.Checkpoint, null, false);
            }
        }
        catch (Exception error)
        {
            _report("map.checkpoint-save-commit-exception: " + error.GetType().Name + (stored ? " after the store" : ""));
            return Conclude(context, Outcome.FailedUnknown(ResultCode.NativeCommitException));
        }

        // It counts as saved only when the manager's replicated state says the last interaction was a store at this
        // anchor and the capture holds a valid Checkpoint buffer (the capture clears it before filling it again).
        if (!stored || !capture.GotBuffer(eBufferType.Checkpoint))
        {
            _report("map.checkpoint-save-not-observed: " + (stored ? "buffer" : "state"));
            return Conclude(context, Outcome.FailedUnknown(ResultCode.CheckpointStoreNotObserved));
        }

        return Conclude(context, Outcome.Succeeded(ResultCode.CheckpointStoreIssued));
    }

    private static bool IsPlayer(EntityReference target) => target.Id.StartsWith(PlayerKindPrefix, StringComparison.Ordinal);

    /// <summary>A participant counts when it is a player of this world who is alive and not downed.</summary>
    private bool IsLivePlayer(EntityReference target, long worldEpoch)
        => target.WorldEpoch == worldEpoch && IsPlayer(target) && _life(target) is { Alive: true, Downed: false };

    /// <summary>The one store's outcome copied to every accepted participant; the skeleton refuses a participant
    /// that is not a player (`unsupported-recipient`) or is a player of an earlier world (`stale-entity`) on its own
    /// row, so a store that ran beside a refusal is a partial.</summary>
    private static Outcome Conclude(CommandContext context, Outcome stored)
        => context.EachTarget(ref stored, StoredStep, EntityKinds.Player,
            countColumn: Blocks.ActionSessionCheckpointSave.RowTargetCount);

    private static readonly TargetStep<Outcome> StoredStep = static (ref Outcome stored, EntityReference target, int index, RowWriter rows)
        => rows.End(stored.Status, stored.Commit, stored.Code);
}
