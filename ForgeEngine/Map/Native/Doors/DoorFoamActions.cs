using System;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The door half of `forge.action.combat.foaming`: one native foam application per door recipient, through
/// `ProjectileManager.WantToSpawnGlueOnDoor` — the manager's own spawn entry, which replicates through the same
/// broadcast the glue launcher's door hit uses. A door is an address this provider owns, so this half resolves
/// its recipients through the door action layer's own resolution and reuses its codes; the enemy half of the same
/// capability stays `ForgeEnemy`'s, unchanged.
///
/// <b>What the ports map onto, and what they cannot.</b> `volume` is the descriptor's `volume` and
/// `bubble_amount` its `expandVolume`, the two numbers the game's own descriptor carries for an author.
/// `duration` is refused by name: the row's enemy half honours it with a Forge-side timer that removes the added
/// volume through the receiver, and this half has no such removal — the addressed door exposes no per-application
/// removal, and clearing its whole local glue would take back foam a player's own glue gun applied. `strength`
/// is refused by name for the same kind of reason: the enemy entry point takes an effect multiplier and the door
/// entry takes none, so only the neutral multiplier is carriable.
///
/// The spawn entry point returns void: the door's own `GetAttachedGlueVolume` readback is the only proof an
/// application landed, so a spawn that did not move it stays an unknown commit rather than a success this layer
/// cannot back. A door with no sync component has no replicator provider to spawn through and is refused per
/// recipient with `missing-glue-receiver`; a door whose own sync interface is present still has to read back.
///
/// Only the authoritative host may submit: the entry point is a static "want to" call whose replication is the
/// manager's own broadcast, and `execution: host` is what the row declares.
/// </summary>
internal sealed class DoorFoamActions
{
    /// <summary>The one strength the door entry point can carry: the neutral multiplier the game's own launcher
    /// passes when nothing scales the effect.</summary>
    internal const double NeutralStrength = 1d;

    /// <summary>The smallest volume the game's own unit can carry and still be a request, and the ceiling the
    /// package's other numeric action ports use — the same bounds the enemy half applies.</summary>
    internal const float MinimumVolume = 0.000001f;
    internal const float MaximumVolume = 1000000f;
    internal const float MinimumStrength = 0.000001f;
    internal const float MaximumStrength = 1000000f;

    private readonly DoorActionCommands _doors;
    private readonly Func<bool> _canExecute;

    internal DoorFoamActions(DoorActionCommands doors, Func<bool> canExecute)
    {
        _doors = doors ?? throw new ArgumentNullException(nameof(doors));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
    }

    /// <summary>Whether a volume the plan handed over is one this entry point can carry. A non-finite, zero or
    /// negative volume is not a small amount of foam; the game's own descriptor has no such value.</summary>
    internal static bool IsUsableVolume(double value)
        => double.IsFinite(value) && value >= MinimumVolume && value <= MaximumVolume;

    /// <summary>Whether an effect multiplier the plan handed over is one the door entry point can carry: the
    /// enemy half's own bounds and the one neutral value, because the door entry takes no multiplier at all.</summary>
    internal static bool IsUsableStrength(double value)
        => double.IsFinite(value) && value >= MinimumStrength && value <= MaximumStrength && value == NeutralStrength;

    /// <summary>
    /// One door's foam application through the manager's own spawn entry: a fresh sync id from the manager's own
    /// sequence, the replicator provider the door's own sync component publishes, the entry point's own "no
    /// particular part" sub index, the door's local origin, and the volume descriptor.
    ///
    /// `default(GlueVolumeDesc)` is filled field by field rather than constructed: the descriptor is the value the
    /// entry point takes, and its `currentScale` belongs to a projectile in flight, so it stays at zero exactly
    /// as the enemy half leaves it.
    /// </summary>
    internal static void Apply(object door, double volume, double expandVolume)
    {
        uint syncID = ProjectileManager.GetNextSyncID();
        var provider = door switch
        {
            LG_SecurityDoor security => security.GetSyncStruct(),
            LG_WeakDoor weak => weak.GetSyncStruct(),
            _ => throw new InvalidOperationException("Unsupported native door core.")
        };
        var descriptor = default(GlueVolumeDesc);
        descriptor.volume = (float)volume;
        descriptor.expandVolume = (float)expandVolume;
        ProjectileManager.WantToSpawnGlueOnDoor(syncID, provider, -1, UnityEngine.Vector3.zero, descriptor);
    }

    /// <summary>The door's own glue reading. A read that throws or publishes a negative or non-finite volume is
    /// not a zero reading, so it answers null and the caller refuses by name.</summary>
    internal static float? ReadAttachedGlueVolume(object door)
    {
        try
        {
            float volume = door switch
            {
                LG_SecurityDoor security => security.GetAttachedGlueVolume(),
                LG_WeakDoor weak => weak.GetAttachedGlueVolume(),
                _ => float.NaN
            };
            return float.IsFinite(volume) && volume >= 0f ? volume : null;
        }
        catch (Exception containedError) { MapLog.Contained("door-foam-actions.read-attached-glue-volume", containedError); return null; }
    }

    /// <summary>The foam request, read once before the first door.</summary>
    private struct FoamRequest
    {
        internal DoorActionCommands Doors;
        internal double Volume, Bubble;
    }

    private static readonly TargetStep<FoamRequest> FoamStep = FoamOne;

    /// <summary>
    /// `forge.action.combat.foaming` over the doors a plan named. One row is written per recipient in the plan's
    /// own order; a door the door layer refuses carries that layer's code unchanged, a door that is no longer the
    /// one the reference names once the spawn has been entered is `receiver-changed-during-commit`, and a spawn
    /// whose door volume cannot be read back afterwards is an unknown commit rather than a success.
    /// </summary>
    internal CommandResult Foaming(CommandContext context)
    {
        if (!_canExecute()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        // The source is a required, kernel-validated entity reference the entry point does not take: the door
        // spawn carries the volume, not a source agent, so no attacker is invented for it.
        if (!context.TryEntity(FoamingContract.Source, out _)) return Outcome.Rejected(ResultCode.SourceMissing);
        if (context.Number(FoamingContract.Volume) is not { } volume)
            return Outcome.Rejected(ResultCode.VolumeOutOfRange);
        if (!IsUsableVolume(volume)) return Outcome.Rejected(ResultCode.VolumeOutOfRange);
        double bubbleAmount = volume;
        if (context.Present(FoamingContract.BubbleAmount))
        {
            if (context.Number(FoamingContract.BubbleAmount) is not { } bubbleValue || !IsUsableVolume(bubbleValue))
                return Outcome.Rejected(ResultCode.BubbleAmountOutOfRange);
            bubbleAmount = bubbleValue;
        }
        if (context.Number(FoamingContract.Strength) is not { } strength)
            return Outcome.Rejected(ResultCode.StrengthOutOfRange);
        // The door entry point takes no effect multiplier, so only the neutral value is carriable.
        if (!IsUsableStrength(strength))
            return Outcome.Rejected(double.IsFinite(strength) && strength > 0d && strength <= MaximumStrength
                ? ResultCode.StrengthUnsupported : ResultCode.StrengthOutOfRange);
        if (context.Integer(FoamingContract.Duration) is not { } duration)
            return Outcome.Rejected(ResultCode.DurationOutOfRange);
        if (duration < 0) return Outcome.Rejected(ResultCode.DurationOutOfRange);
        // This half has no native removal entry, so a Forge-side duration cannot be honoured on a door.
        if (duration > 0) return Outcome.Rejected(ResultCode.DurationUnsupported);
        var request = new FoamRequest { Doors = _doors, Volume = volume, Bubble = bubbleAmount };
        return context.EachTarget(ref request, FoamStep, EntityKinds.MapObject, CommandResult.MaximumFacts,
            countColumn: Blocks.ActionCombatFoaming.RowTargetCount);
    }

    private static void FoamOne(ref FoamRequest request, EntityReference target, int index, RowWriter rows)
    {
        rows.Number(Blocks.ActionCombatFoaming.RowAmount, request.Volume);
        var doors = request.Doors;
        if (doors.ResolveCore(target, out var refusal) is not { } door)
        { rows.End(Status.Rejected, Commit.None, refusal); return; }
        // The door's own sync component is the replicator provider source; a door without one has nothing to
        // spawn through, which is a refusal before any entry point is reached.
        bool receiver = door switch
        {
            LG_SecurityDoor security => security.m_sync != null,
            LG_WeakDoor weak => weak.m_sync != null && weak.LastStatus != eDoorStatus.Destroyed,
            _ => false
        };
        if (!receiver)
        { rows.End(Status.Rejected, Commit.None, ResultCode.MissingGlueReceiver); return; }
        float? before = ReadAttachedGlueVolume(door);
        if (!before.HasValue)
        { rows.End(Status.Rejected, Commit.None, ResultCode.MissingGlueReceiver); return; }

        float? after;
        try { Apply(door, request.Volume, request.Bubble); }
        catch (Exception containedError) { MapLog.Contained("door-foam-actions.glue-commit", containedError); rows.End(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException); return; }
        try
        {
            if (doors.ResolveCore(target, out _) is not { } current || !SameCore(current, door))
            { rows.End(Status.Failed, Commit.Unknown, ResultCode.ReceiverChangedDuringCommit); return; }
            after = ReadAttachedGlueVolume(current);
        }
        catch (Exception containedError) { MapLog.Contained("door-foam-actions.glue-readback", containedError); rows.End(Status.Failed, Commit.Unknown, ResultCode.ReadbackException); return; }
        // The receiver published less foam than it held before the spawn, or its volume no longer reads.
        if (!after.HasValue || after.Value < before.Value)
        { rows.End(Status.Failed, Commit.Unknown, ResultCode.UnexpectedGlueReadback); return; }
        // The entry point was entered and the published volume did not move: a dropped spawn and a door
        // already at its cap are indistinguishable from here.
        if (after.Value == before.Value)
        { rows.End(Status.Failed, Commit.Unknown, ResultCode.GlueUnseen); return; }
        rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
    }

    private static bool SameCore(object left, object right) => (left, right) switch
    {
        (LG_SecurityDoor a, LG_SecurityDoor b) => a.Pointer == b.Pointer,
        (LG_WeakDoor a, LG_WeakDoor b) => a.Pointer == b.Pointer,
        _ => false
    };
}
