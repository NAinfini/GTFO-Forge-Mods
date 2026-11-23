using System;
using ChainedPuzzles;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The execute half of the door lock row, `forge.action.map.door_lock`, whose `operation` member picks
/// locking or unlocking. Both act on the doors a plan named in the one `gtfo.map_object` namespace,
/// through the native entry points the readback half already reads — no second door table, no Forge-side lock
/// ledger, no handle.
///
/// The one lock this row writes is the no-key lock: the door's own `LockDoorWithNoKey` request, the one the
/// level's LockSecurityDoor event uses, which goes through the door sync interaction and so replicates as the
/// game's own lock. The generator case is a group of doors locked and released later by unlocking. The row
/// offers no key choice: a key lock needs a native `GateKeyItem` instance no resource provider resolves, and the
/// unlock interaction spends no key.
///
/// A door the level did not make a zone's entrance — a weak door, a node door, a decorative door — has no
///   address in this provider's grammar, so it cannot be named by a plan and never reaches this half.</summary>
internal sealed class DoorTerminalActions
{
    private readonly DoorActionCommands _doors;
    private readonly Action<string> _report;

    /// <summary>The door resolution, the recipient walk and the result rows are the door open/close row's own:
    /// <paramref name="doors"/> is that half, so the two rows cannot read a door or conclude a run differently.</summary>
    internal DoorTerminalActions(DoorActionCommands doors, Action<string> report)
    {
        _doors = doors ?? throw new ArgumentNullException(nameof(doors));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Puts one door back under the native no-key lock. The game has no "any key" lock: a key door opens
    /// with its one GateKeyItem, and the door's own `SetupSimpleLock` is an empty method (GameAssembly build
    /// 20403457, RVA 0x353190 is a bare `ret`).</summary>
    private MapActionOutcome Lock(LG_SecurityDoor door, MapObjectReference address)
    {
        if (door == null || door.WasCollected) return MapActionOutcome.Refused(ResultCode.StaleEntity);
        if (!DoorObservation.IsCurrentAddress(door, address)) return MapActionOutcome.Refused(ResultCode.StaleEntity);
        if (door.m_locks?.TryCast<LG_SecurityDoor_Locks>() is not { } locks || locks.WasCollected)
            return MapActionOutcome.Refused(ResultCode.DoorNoLockComponent);
        // The lock is read from the door's own status before anything is written: a door already locked with no
        // key is left alone, which also keeps a repeated request from re-running a setup.
        if (door.LastStatus == eDoorStatus.Closed_LockedWithNoKey) return MapActionOutcome.AlreadyInState(ResultCode.DoorAlreadyLocked);
        if (door.m_sync?.TryCast<LG_Door_Sync>() is not { } sync || sync.WasCollected)
            return MapActionOutcome.Refused(ResultCode.DoorNoSyncComponent);
        try
        {
            // The original LockSecurityDoor event uses this request (LockDoorWithNoKey, RVA 0x159f040, calls the
            // door sync AttemptDoorInteraction(SetLockedNoKey)); it reaches the synchronized interaction, unlike the
            // generation-only SetupAsLockedNoKey return value.
            door.LockDoorWithNoKey(null);
        }
        catch (Exception error)
        {
            // The call entered the door's own lock setup; whether it had already written the lock component is
            // not observable from here, so the commit stays unknown.
            _report("map.door-lock-exception: " + error.GetType().Name);
            return MapActionOutcome.Refused(ResultCode.NativeCommitException);
        }
        // The entry is the door's synchronized lock request: on a client LastStatus takes the new value only when the
        // host's replicated interaction arrives, so a same-frame read of it reports a failed write for a lock that was
        // issued. This row reports the entry it invoked, as the unlock and open rows do; the door's real state is
        // published by the observation path, never claimed here.
        return MapActionOutcome.Issued(ResultCode.DoorLockIssued);
    }

    /// <summary>Releases one door's lock through the door's own synchronized interaction entry, which takes no
    /// key.</summary>
    private MapActionOutcome Unlock(LG_SecurityDoor door, MapObjectReference address)
    {
        if (door == null || door.WasCollected) return MapActionOutcome.Refused(ResultCode.StaleEntity);
        if (!DoorObservation.IsCurrentAddress(door, address)) return MapActionOutcome.Refused(ResultCode.StaleEntity);
        int status = (int)door.LastStatus;
        if (status == (int)eDoorStatus.Unlocked || status == (int)eDoorStatus.Open || status == (int)eDoorStatus.Opening)
            return MapActionOutcome.AlreadyInState(ResultCode.DoorNotLocked);
        // The door's own sync slot is an interface in the interop assembly, so the cast is how the concrete
        // component is reached, exactly as the lock slot is reached.
        if (door.m_sync?.TryCast<LG_Door_Sync>() is not { } sync || sync.WasCollected)
            return MapActionOutcome.Refused(ResultCode.DoorNoSyncComponent);
        try
        {
            if (DoorLocksNative.Current?.Unlock(door, address) is { } gate) return gate;
            // The same entry as vanilla event 2; an unsolved scan remains in the original opening chain.
            door.UseChainedPuzzleOrUnlock(null);
        }
        catch (Exception error)
        {
            _report("map.door-unlock-exception: " + error.GetType().Name);
            return MapActionOutcome.Refused(ResultCode.NativeCommitException);
        }
        return MapActionOutcome.Issued(ResultCode.DoorUnlockIssued);
    }

    /// <summary>The row's handler body. The whole request is checked before the first door, because a request
    /// that cannot be carried out as asked must not half-apply. An unwritten operation is locking.</summary>
    internal CommandResult HandleLock(CommandContext context)
    {
        if (!_doors.Authorized()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var operation = context.Text(DoorTerminalActionContract.Operation) ?? "lock";
        if (operation is not ("lock" or "unlock")) return Outcome.Rejected(ResultCode.DoorUnknownLockOperation);
        return _doors.Each(context, new LockRequest(this, operation == "unlock"), LockStep, Blocks.ActionMapDoorLock.RowTargetCount);
    }

    /// <summary>`forge.action.map.door_alarm`. The scan is resolved once before the first door: replacing needs a
    /// scene scan this level built on every machine, putting back needs none (each door remembers its own alarm).</summary>
    internal CommandResult HandleAlarm(CommandContext context, DoorAlarmSwaps? swaps)
    {
        if (!_doors.Authorized() || swaps == null) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var operation = context.Text(DoorTerminalActionContract.AlarmOperation) ?? "replace";
        if (operation is not ("replace" or "restore")) return Outcome.Rejected(ResultCode.DoorAlarmUnknownOperation);
        ChainedPuzzleInstance? scan = null; string? scanId = null;
        if (operation == "replace")
        {
            if (!context.TryResource(DoorTerminalActionContract.AlarmScan, out string kind, out string id))
                return Outcome.Rejected(ResultCode.ScanResourceMissing);
            if (kind != AlarmWaveContract.ChainedPuzzleKind) return Outcome.Rejected(ResultCode.AlarmResourceNotChainedPuzzle);
            if (string.IsNullOrEmpty(id)) return Outcome.Rejected(ResultCode.ResourceIdEmpty);
            scan = ChainedPuzzleLookup.Find(id, out var ambiguous);
            if (scan == null) return Outcome.Rejected(ambiguous ? ResultCode.AlarmAmbiguous : ResultCode.AlarmNotFound);
            if (scan.WasCollected || scan.Data == null || scan.IsActive || scan.IsSolved) return Outcome.Rejected(ResultCode.AlarmUnavailable);
            scanId = id;
        }
        return _doors.Each(context, new AlarmRequest(swaps, scan, scanId), AlarmStep, Blocks.ActionMapDoorAlarm.RowTargetCount);
    }

    private readonly record struct AlarmRequest(DoorAlarmSwaps Swaps, ChainedPuzzleInstance? Scan, string? ScanId);

    private static readonly DoorActionCommands.DoorEntry<AlarmRequest> AlarmStep = AlarmOne;

    private static MapActionOutcome AlarmOne(ref AlarmRequest request, object door, MapObjectReference address) => door switch
    {
        LG_SecurityDoor security => request.Scan != null ? request.Swaps.Replace(security, address, request.Scan, request.ScanId!)
            : request.Swaps.Restore(security, address),
        _ => MapActionOutcome.Refused(ResultCode.UnsupportedRecipient)
    };

    /// <summary>The lock request, read once before the first door.</summary>
    private readonly record struct LockRequest(DoorTerminalActions Self, bool Unlock);

    private static readonly DoorActionCommands.DoorEntry<LockRequest> LockStep = LockOne;

    private static MapActionOutcome LockOne(ref LockRequest request, object door, MapObjectReference address) => door switch
    {
        LG_SecurityDoor security => request.Unlock ? request.Self.Unlock(security, address)
            : request.Self.Lock(security, address),
        _ => MapActionOutcome.Refused(ResultCode.UnsupportedRecipient)
    };
}
