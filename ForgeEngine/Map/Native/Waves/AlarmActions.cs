using System;
using System.Collections.Generic;
using ChainedPuzzles;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The native half of `forge.action.map.alarm`: raise or clear the alarm on the doors, zones or the whole level a
/// request names. An alarm is not a second instance kind — it is the chained puzzle the game's own data block
/// marks with `TriggerAlarmOnActivate`, the same instance `forge.action.map.scan_state` drives — so the write is
/// that instance's own `AttemptInteract(Activate|Deactivate)` and the game's state replicator carries it to every
/// peer. What this row adds to the scan row is the *addressing*: a plan names doors and zones, and the puzzle
/// behind them is resolved here rather than by the author.
///
/// The three scopes are the three subjects the catalog declares and the three the game itself has:
/// <list type="bullet">
/// <item><description>`local` — every target is a map object of this provider, and the alarm is the puzzle its own
/// lock component holds (`LG_SecurityDoor_Locks.ChainedPuzzleToSolve`). A door whose lock holds no puzzle is
/// refused by name instead of silently doing nothing.</description></item>
/// <item><description>`zone` — every target is a `gtfo.zone`, and the alarm is the entrance gate of that zone:
/// the door the level generated as the zone's own gate, resolved through the same address grammar and the same
/// door table every door fact and door action goes through, so an alarm and a door fact can never disagree about
/// which door a zone has.</description></item>
/// <item><description>`level` — the whole level, which the game has exactly one entry for: `WardenObjectiveManager.
/// StopAlarms()` silences the alarms of every layer at once. There is no level-wide *start*, and a request for one
/// is refused by name rather than served as a loop over the level's instances the plan did not name.</description></item>
/// </list>
///
/// **The handle is the identity of the instance that was really driven.** Starting an alarm publishes one effect
/// handle per target, bound to the puzzle that target's own door or gate resolved to — the handle this row already
/// cast for that puzzle while its slot is live, otherwise a new one — so a batch of doors
/// publishes one handle per door, in the request's own order, instead of one handle standing for the first
/// instance of the batch or for the batch as a whole. `forge.query.map.alarm_state` reads `active` back through
/// exactly that value, which is what makes the row's write side and its read side one identity. A target whose
/// puzzle was not resolved publishes the null its port declares; the array is left out altogether when no target
/// resolved a puzzle, and the `level` scope mints nothing because it has no instance to name.
///
/// The binding is the kernel's own native slot, not a table of this half's. `RegisterNative` carries the puzzle
/// and the provider's own live answer (`!WasCollected`), so the kernel holds the instance for the handle's
/// declared lifetime, releases it when the puzzle the handle named is gone, and hands the same object back through
/// `TryNative`. A second table keyed by the handle's text would be a second source of truth for the same binding,
/// and the read row would have to keep the two in step.
///
/// The row declares no resource and no source: the alarm it acts on is the one the level already built, and the native
/// interaction payload (pChainedPuzzleInteraction) carries the interaction alone, with no player, agent or equipment field.
///

/// The write is the same one `forge.action.map.scan_state` makes, and that is deliberate: the game has one alarm
/// instance kind, and a second activation path would be a second source of truth for it. This row differs by what
/// it addresses, not by what it writes.</summary>
internal sealed class AlarmActions
{
    /// <summary>The two members the row's `mode` parameter declares, and the puzzle interaction each one submits.
    /// `start` is the game's own activation — the instance's data block decides whether that raises an alarm —
    /// and `stop` is the deactivation that clears it.</summary>
    internal const string StartMode = "start";
    internal const string StopMode = "stop";

    /// <summary>The three members the row's `scope` parameter declares.</summary>
    internal const string LocalScope = "local";
    internal const string ZoneScope = "zone";
    internal const string LevelScope = "level";

    /// <summary>The one attached half the static entry point answers through. A half that was never attached
    /// refuses by name instead of writing an alarm nothing could read back.</summary>
    private static AlarmActions? _current;

    private readonly RuntimeModuleHandle _registration;
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;

    private AlarmActions(RuntimeModuleHandle registration, Func<bool> ready, Action<string> report)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Attaches the one half the static handler answers through. It is created after `RegisterModule`
    /// returned, because the handles it mints need a registration that exists. The puzzle bindings live in the
    /// kernel's own handle slots, so a handle of an ended world can never resolve to the next world's instance.</summary>
    internal static AlarmActions Attach(RuntimeModuleHandle registration, Func<bool> ready, Action<string> report)
    {
        var previous = _current;
        _current = new AlarmActions(registration, ready, report);
        return previous ?? _current;
    }

    /// <summary>Detaches the half when it is still the current one, so a session that is going away cannot leave
    /// its released registration in the slot a later session would mint through. Nothing else is dropped: the
    /// bindings go with the registration the kernel is already discarding.</summary>
    internal static void Detach(AlarmActions actions)
    {
        if (ReferenceEquals(_current, actions)) _current = null;
    }

    /// <summary>What a handle of this row names now, in the three states the world can be in. `Unknown` is a value
    /// that is not a live handle of this provider's world — one this half never cast, one the kernel has released,
    /// or one from an ended world — and `Gone` is this half's own handle whose puzzle is no longer there. The read
    /// row reports the two as two codes rather than as one "not active", because a plan repairing the first has a
    /// wiring problem and a plan seeing the second has a world that moved on.</summary>
    internal enum Binding { Unknown, Gone, Live }

    /// <summary>The puzzle a handle of this row names, read from the kernel's own native slot: a handle that is not
    /// this provider's or not this world's answers nothing before the object is looked at, and the object is the
    /// instance the command really wrote to. A live handle whose puzzle was collected before the kernel's next
    /// sweep reads as `Gone` rather than as an unwired handle.</summary>
    internal static Binding TryPuzzle(RuntimeHandle handle, out ChainedPuzzleInstance? puzzle)
    {
        puzzle = null;
        if (_current is not { } self) return Binding.Unknown;
        if (!self._registration.TryNative(handle, out var native) || native is not ChainedPuzzleInstance bound)
            return Binding.Unknown;
        if (bound.WasCollected) return Binding.Gone;
        puzzle = bound;
        return Binding.Live;
    }

    /// <summary>One recipient the command really read: the door or gate it resolved, the puzzle that door's own
    /// lock holds, and the refusal code when either step answered nothing. The resolution is a read, so the whole
    /// batch is decided before the first write — which is what lets the handles be reserved for it first.</summary>
    private sealed record Addressed(EntityReference Target, ChainedPuzzleInstance? Puzzle, string? Refusal);

    internal static CommandResult Handle(CommandContext context)
        => _current is { } self ? self.Execute(context) : Outcome.Rejected(ResultCode.HandleUnavailable);

    /// <summary>The rows are written one per recipient in the request's order, each with its own target, the
    /// catalog's four columns and `target_count`. They are written directly rather than through the action skeleton
    /// because the whole batch is addressed and its handles reserved before the first write, and the level scope
    /// answers every row from one native call.</summary>
    private CommandResult Execute(CommandContext context)
    {
        if (!context.IsHost || !_ready()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        string? mode = context.Text(AlarmWaveContract.AlarmMode);
        if (mode is not (StartMode or StopMode)) return Outcome.Rejected(ResultCode.AlarmModeUnknown);
        string? scope = context.Text(AlarmWaveContract.AlarmScope);
        if (scope is not (LocalScope or ZoneScope or LevelScope)) return Outcome.Rejected(ResultCode.AlarmScopeUnknown);
        // The level scope is one native entry for the whole level and names no object: starting is not a thing the
        // game has, and stopping is `StopAlarms`, which is the same call the objective machine and the terminal's
        // DEACTIVATE_ALARMS command make. Neither reaches an instance this row could hand a handle back for.
        if (scope == LevelScope && mode == StartMode) return Outcome.Rejected(ResultCode.AlarmLevelStartUnsupported);
        // The targets port is optional because the level stop names no object: left unwired, the stop runs and
        // writes no row. The object and zone scopes address their alarms through it, so there it must be wired.
        if (!context.Present(AlarmWaveContract.AlarmTargets))
            return scope == LevelScope ? StopLevel(context.Rows, 0) : Outcome.Rejected(ResultCode.MissingField);
        var targets = context.Entities(AlarmWaveContract.AlarmTargets);
        // The kernel answers a resolved empty set itself, so no target here means a member that is not an entity.
        if (targets.Length == 0) return Outcome.Rejected(ResultCode.MissingField);
        if (targets.Length > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);
        if (scope == LevelScope) return StopLevel(context.Rows, targets.Length);

        // The whole batch is addressed before anything is written: every target's door and that door's puzzle are
        // resolved here, so a command that cannot carry the handle its own row promises never raises the alarm.
        var addressed = new Addressed[targets.Length];
        for (int index = 0; index < targets.Length; index++) addressed[index] = Resolve(scope, targets[index], context.WorldEpoch);

        if (mode == StartMode)
        {
            if (!TryMint(addressed, out var handles)) return Outcome.Rejected(ResultCode.HandleUnavailable);
            // The handles beside the rows, published only when the batch really reserved one: the handle at each
            // position is the one minted for the recipient at that position, and a recipient whose puzzle was not
            // resolved carries the null its port declares rather than a neighbour's handle.
            if (handles is { } minted) context.Outputs.Handles(Blocks.ActionMapAlarm.OutAlarmHandle, minted);
        }

        var rows = context.Rows;
        for (int index = 0; index < addressed.Length; index++) Write(rows, index, mode, addressed[index], addressed.Length);
        // One row per target, concluded by the shared rule. A target whose alarm was already in the state the
        // request asked for is a confirmed no-op — the state was read back, so the row is `succeeded + confirmed`
        // and its own code still says no write was issued.
        return rows.Conclude();
    }

    /// <summary>Reserves one effect handle per addressed target whose puzzle was resolved, in the request's own
    /// order, and binds each to that puzzle. The whole batch is reserved before the first write, so a command that
    /// cannot name what it is about to start is refused instead of leaving an alarm nothing can read back. The
    /// element for an unresolved target is the null its port declares; a batch in which no target resolved a
    /// puzzle publishes no handle port at all.</summary>
    private bool TryMint(Addressed[] addressed, out RuntimeHandle?[]? handles)
    {
        handles = null;
        if (!_registration.IsRegistered) return false;
        var minted = new RuntimeHandle?[addressed.Length];
        // Only the handles this batch cast itself are given back on a refusal; a reused one still names its puzzle.
        var cast = new RuntimeHandle?[addressed.Length];
        bool any = false;
        for (int index = 0; index < addressed.Length; index++)
        {
            if (addressed[index].Puzzle is not { } puzzle) continue;
            // A puzzle that already has a live handle of this row keeps it: starting an alarm that is already
            // ringing, or starting it again later in the level, names the same instance with the same handle instead
            // of spending one more encounter-long slot each time.
            if (HeldHandle(puzzle) is { } held) { minted[index] = held; any = true; continue; }
            try
            {
                var handle = _registration.CastEffectHandle(AlarmWaveContract.HandleLifetime);
                // The kernel holds the puzzle for the handle's declared lifetime and asks this half's own answer
                // before it releases a slot, so a collected instance is the one fact that ends the binding.
                _registration.RegisterNative(handle, puzzle, ChainedPuzzleLookup.Liveness(puzzle));
                minted[index] = cast[index] = handle;
                _held[puzzle.Pointer] = handle;
                any = true;
            }
            catch (RuntimeContractException error)
            {
                // The handles already reserved for this batch name instances nothing will write to; they are
                // given back rather than left holding puzzles the command never touched.
                Unbind(cast);
                _report("map.alarm-handle-refused: " + error.Code);
                return false;
            }
        }
        if (any) handles = minted;
        return true;
    }

    /// <summary>The handle this row already cast for one puzzle, while the kernel's own slot still binds it to that
    /// very instance. The table only remembers which handle to ask about; the kernel's slot stays the one answer to
    /// what the handle names, so a released slot, an ended world or a reused native address reads as no handle and
    /// a new one is cast.</summary>
    private RuntimeHandle? HeldHandle(ChainedPuzzleInstance puzzle)
    {
        if (!_held.TryGetValue(puzzle.Pointer, out var held)) return null;
        if (_registration.TryNative(held, out var native) && native is ChainedPuzzleInstance bound
            && !bound.WasCollected && bound.Pointer == puzzle.Pointer) return held;
        _held.Remove(puzzle.Pointer);
        return null;
    }

    private readonly Dictionary<IntPtr, RuntimeHandle> _held = new();

    /// <summary>Gives back the handles a refused reservation had already minted, so the slot is not spent on a
    /// command that wrote nothing. The kernel drops the native object with the slot, so the handle names no puzzle
    /// any more.</summary>
    private void Unbind(RuntimeHandle?[] minted)
    {
        foreach (var value in minted)
        {
            if (value is not { } handle) continue;
            try { _registration.ReleaseEndedHandle(handle, AlarmHandleShape, "alarm-handle-unreserved"); }
            catch (RuntimeContractException error) { MapLog.Contained("alarm.unreserve-release", error); }
        }
    }

    /// <summary>The port descriptor a handle of this row is released through. It is the same kind and lifetime the
    /// catalog declares for `alarm_handle`, so the release is checked against the row's own declaration.</summary>
    private static readonly HandlePort AlarmHandleShape = new("effect", AlarmWaveContract.HandleLifetime);

    /// <summary>One recipient's own alarm decision. The door the target names is resolved first and the puzzle its
    /// lock holds second, so a target that names no door or zone of this scope is refused as unaddressable and a
    /// door whose lock holds no puzzle as `alarm-not-found` — two different answers, because they are two
    /// different problems for whoever wrote the plan.</summary>
    private static Addressed Resolve(string scope, EntityReference target, long worldEpoch)
    {
        // A recipient of another world names a door of that world, never the same address in this one.
        if (target.WorldEpoch != worldEpoch) return new Addressed(target, null, ResultCode.StaleEntity);
        if (!TryDoor(scope, target, out var door, out var address))
            return new Addressed(target, null, ResultCode.AlarmTargetNotAddressable);
        // A lock whose puzzle has no core holds no alarm to raise, the same answer `scan_state` gives it.
        if (PuzzleOf(door!, address!) is not { } puzzle || puzzle.NRofPuzzles() <= 0)
            return new Addressed(target, null, ResultCode.AlarmNotFound);
        return new Addressed(target, puzzle, null);
    }

    /// <summary>One addressed target's write and the row that reports it. A target whose puzzle was not resolved
    /// is the refusal the resolution already decided; a target whose alarm already holds the state the request
    /// asked for start is a confirmed no-op, read back rather than written.</summary>
    private void Write(RowWriter rows, int index, string mode, Addressed addressed, int count)
    {
        rows.Begin(index, addressed.Target).Integer(Blocks.ActionMapAlarm.RowTargetCount, count);
        if (addressed.Puzzle is not { } puzzle) { rows.End(Status.Rejected, Commit.None, addressed.Refusal!); return; }
        if (mode == StartMode)
        {
            // The desired state was already in force and was read back: a confirmed no-op, not a no-commit success.
            if (puzzle.IsActive) { rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.AlarmAlreadyActive); return; }
            if (puzzle.IsSolved) { rows.End(Status.Rejected, Commit.None, ResultCode.AlarmAlreadySolved); return; }
        }
        var (status, commit, code) = Submit(puzzle,
            mode == StartMode ? eChainedPuzzleInteraction.Activate : eChainedPuzzleInteraction.Deactivate,
            mode == StartMode ? ResultCode.AlarmStarted : ResultCode.AlarmStopped,
            _reportSubmit ??= (site, error) => _report("map.alarm-" + site + "-exception: " + error.GetType().Name));
        rows.End(status, commit, code);
    }

    private Action<string, Exception>? _reportSubmit;

    /// <summary>The one write every alarm and scan row makes: submit the interaction, then read the instance's own
    /// state back. The interaction entry returns nothing, so the call alone proves no commit; only a state that
    /// matches the request is a confirmed write, and an exception on either side is an unknown commit.</summary>
    internal static (Status, Commit, string) Submit(ChainedPuzzleInstance puzzle,
        eChainedPuzzleInteraction interaction, string issuedCode, Action<string, Exception> report)
    {
        try { puzzle.AttemptInteract(interaction); }
        catch (Exception error)
        {
            report("commit", error);
            return (Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
        }
        bool landed;
        try
        {
            landed = interaction switch
            {
                eChainedPuzzleInteraction.Activate => puzzle.IsActive,
                eChainedPuzzleInteraction.Solve => puzzle.IsSolved,
                _ => !puzzle.IsActive
            };
        }
        catch (Exception error)
        {
            report("readback", error);
            return (Status.Failed, Commit.Unknown, ResultCode.ReadbackUnconfirmed);
        }
        // The interaction was submitted but the instance's own state does not show the requested state: unknown,
        // not committed.
        return landed ? (Status.Succeeded, Commit.Confirmed, issuedCode)
            : (Status.Failed, Commit.Unknown, ResultCode.ReadbackUnconfirmed);
    }

    /// <summary>The whole level's own alarm stop. The native entry is one call for the whole level, but the result
    /// still owes one row per recipient the plan named, in the order it named them: each row carries that
    /// recipient's own reference and the one outcome the call had.</summary>
    private CommandResult StopLevel(RowWriter rows, int count)
    {
        var (status, commit, code) = (Status.Succeeded, Commit.Confirmed, ResultCode.AlarmStopped);
        try
        {
            WardenObjectiveManager.StopAlarms();
        }
        catch (Exception error)
        {
            _report("map.alarm-stop-exception: " + error.GetType().Name);
            (status, commit, code) = (Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
        }
        // An unwired targets port names no recipient, so the call's own outcome is the command's.
        if (count == 0) return new Outcome(status, commit, code);
        for (int index = 0; index < count; index++)
            rows.Begin(index).Integer(Blocks.ActionMapAlarm.RowTargetCount, count).End(status, commit, code);
        return rows.Conclude();
    }

    /// <summary>The door one target's alarm is on. Under `local` the target is the door's own address; under
    /// `zone` it is a `gtfo.zone` and the door is that zone's entrance gate, built from the zone's own coordinates
    /// with the one address factory the door category publishes. Either way the door is resolved through the one
    /// door table every other door reader uses, so an alarm and a door fact can never disagree about which door a
    /// target names.</summary>
    private static bool TryDoor(string scope, EntityReference target, out LG_SecurityDoor? door, out MapObjectReference? address)
    {
        door = null;
        address = null;
        if (scope == LocalScope)
        {
            if (MapObjectDoorAddress.TryParse(Id(target)) is not { } named) return false;
            // A weak door is the same category and has no entrance alarm: its lock holds no `ChainedPuzzleToSolve`,
            // so it is unaddressable for this row rather than a door whose alarm is missing.
            if (MapObjectDoorAddress.ParseWeakSerial(named.Key) != null) return false;
            if (DoorObservation.ByAddress(named) is not { } local || !DoorObservation.IsCurrentAddress(local, named)) return false;
            door = local;
            address = named;
            return true;
        }
        // The zone grammar has one reader, the environment rows' own; a coordinate outside the level's indices names
        // no zone, so the target is refused as unaddressable exactly like a zone that does not exist.
        if (EnvironmentZone.Parse(target) is not { } zone) return false;
        if (MapObjectDoorAddress.Create(zone.Dimension, zone.Layer, zone.Zone) is not { } gate) return false;
        if (DoorObservation.ByAddress(gate) is not { } entrance || !DoorObservation.IsCurrentAddress(entrance, gate)) return false;
        door = entrance;
        address = gate;
        return true;
    }

    /// <summary>The chained puzzle a resolved door's alarm is: the lock component's own `ChainedPuzzleToSolve`, the
    /// property the game sets in `SetupForChainedPuzzle` and reads back when the door is used. A door whose lock
    /// holds no puzzle has no alarm this row could raise or clear.</summary>
    internal static ChainedPuzzleInstance? PuzzleOf(LG_SecurityDoor door, MapObjectReference address)
    {
        if (!DoorObservation.IsCurrentAddress(door, address)) return null;
        var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        if (locks == null || locks.WasCollected) return null;
        var puzzle = locks.ChainedPuzzleToSolve;
        return puzzle == null || puzzle.WasCollected ? null : puzzle;
    }

    /// <summary>The entity id one reference carries, with its kind prefix removed. Every reference this runtime
    /// hands a handler is `<kind>:<id>`, so a reference without a separator is not one this provider can address
    /// and answers nothing rather than being read as an id that happens to look like an address.</summary>
    private static string? Id(EntityReference reference)
    {
        int separator = reference.Id.IndexOf(':');
        return separator < 0 ? null : reference.Id[(separator + 1)..];
    }
}
