using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ChainedPuzzles;
using ForgeEngine.Framework;
using GameData;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the three scan and wave actions: one method per row, each taking the native objects
/// the row resolved and answering with the one decision it made. Nothing here is a plan, a result frame or a
/// native write of its own — the handlers below are the binding side, and the action methods are what they call
/// in.
///
/// Every write goes through a native entry the game itself runs, so the game replicates it:
/// <list type="bullet">
/// <item><description>A scan is a `ChainedPuzzleInstance`, and its own
/// `AttemptInteract(eChainedPuzzleInteraction.Activate|Deactivate)` is the entry the game's own door, terminal
/// and objective callbacks use. That entry hands the interaction to the instance's own state replicator, and the
/// replicator's callback is what runs the master side (`OnStateChange` calls `MasterActivate`/`MasterDeactivate`).
/// Writing the instance's fields or calling the private master methods directly would change one machine's state
/// without the replicator, which is exactly the write this layer must not make. An alarm is that same instance
/// kind — the puzzle's own data block says whether activating it raises one — so this activation is also how a
/// level's alarm is started; the two alarm rows the rulings deleted had no write of their own.</description></item>
/// <item><description>A wave is a Mastermind event, and `Mastermind.TriggerSurvivalWave` is the host entry that
/// creates it, answers with the event id it registered and replicates through `SurvivalWave.OnSpawn`. The stop
/// side is the same event back again — `Mastermind.TryGetEvent(eventId, out event)` followed by the event's own
/// `StopEvent()` — which is why the start row mints the handle that names it.</description></item>
/// </list>
///
/// <b>The handle.</b> A wave or the puzzle instance a scan activated is named by an effect handle the provider
/// mints through the kernel's own pool, and the native object the handle stands for is registered beside it. That
/// is the whole mechanism the node list's "start a wave and store it in 警报A, stop that wave later" template
/// needs: the start row publishes the handle, the plan's own named-object row stores it, and the stop row reads
/// the same handle back and resolves it to the event it named. A handle the kernel no longer holds live resolves
/// to nothing, so a value that outlived its wave is refused as `stale-handle` instead of stopping a stranger.
///
/// The handle can only be cast on a registration this half is attached to; <see cref="Attach"/> is the one line
/// the session calls with its own kernel and registration, and a detached half refuses the two start rows with
/// `handle-unavailable` rather than publishing a port it cannot fill.
///
/// A stop is what `StopEvent` does: it removes the event, so the pending spawns are cancelled. A wave's size, pace
/// and randomness are its data blocks' own fields, so the start row declares no port for them.</summary>
internal sealed class AlarmWaveActions
{
    /// <summary>The one attached half. A handler is a static delegate, so the session hands its kernel and
    /// registration to the half through this slot before the first plan runs; a half that was never attached
    /// refuses the two start rows with `handle-unavailable`.</summary>
    private static AlarmWaveActions? _current;

    /// <summary>The session's own kernel and registration. Handles this half mints are pool slots of this
    /// provider, and `TryNative` is what turns one back into the native object it named.</summary>
    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;

    internal AlarmWaveActions(IForgeEngine kernel, RuntimeModuleHandle registration)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
    }

    /// <summary>Attaches the one half the static handlers answer through, and answers the previous one. The
    /// session calls this once, from `Definition()`, before the returned module is registered: a handler that
    /// runs before the attach is a handler that has already refused; a handler that runs after it mints through
    /// the registration the session owns.</summary>
    internal static AlarmWaveActions Attach(IForgeEngine kernel, RuntimeModuleHandle registration)
    {
        var previous = _current;
        _current = new AlarmWaveActions(kernel, registration);
        return previous ?? _current;
    }

    /// <summary>Detaches the half when it is still the current one, so a session that is going away cannot leave
    /// its released registration in the slot a later session would mint through.</summary>
    internal static void Detach(AlarmWaveActions actions)
    {
        if (ReferenceEquals(_current, actions)) _current = null;
    }

    /// <summary>Whether this command may write at all. Two answers have to agree: the ABI's own host fact —
    /// which the kernel constructs per advance and batch E turns from a constant into the real one — and the
    /// game's own master flag, which is what the native entries below actually require. Both gates are kept
    /// because either one alone would be a claim this layer cannot back: `CommandContext.IsHost` is today still
    /// the constant `true`, and `Session.IsHost` says nothing about which peer the runtime dispatched on.</summary>
    private static bool IsHost(CommandContext context) => context.IsHost && Session.IsHost;

    // ---- result rows ---------------------------------------------------------------------------------

    /// <summary>One result row in the canonical result schema's own columns: the recipient at this row's position,
    /// then `status`, `committed`, `code`, and the command's own conclusion with the same code. A row is written for
    /// every decision, including a refusal, and the code names the one check that decided it. The wave's spent
    /// population points are deliberately not a column: the schema declares these four.
    ///
    /// The target is the recipient exactly as the request carried it — the `chained-puzzle` or `wave` resource
    /// reference for the two start rows and the wave handle for the stop row — written by the row writer from the
    /// recipient itself, so it is copied, never re-spelled. A request that carried no recipient has no row, while
    /// the command's own status and code still report the decision. The rows are written directly rather than
    /// through the action skeleton because the recipient is a resource or a handle, not an entity.</summary>
    private static CommandResult Row(CommandContext context, bool recipient, Status status, Commit commit, string code)
    {
        if (recipient) context.Rows.Row(0, status, commit, code);
        return new Outcome(status, commit, code);
    }

    /// <summary>The status and commit state one decision maps to, shared by all three row shapes: an invoked
    /// entry point committed, the state the request asked for already holding is a confirmed no-op — the state was
    /// read back, so it is a success with a confirmed commit rather than a success that wrote nothing — and a
    /// refusal wrote nothing. There is no fourth reading — an entry point that cannot say what the world will look
    /// like afterwards is why no row reports more than this.</summary>
    private static (Status, Commit) Reading(MapActionOutcome outcome) => outcome.Commit switch
    {
        MapActionCommit.Issued => (Status.Succeeded, Commit.Confirmed),
        MapActionCommit.AlreadyInState => (Status.Succeeded, Commit.Confirmed),
        _ => (Status.Rejected, Commit.None)
    };

    private static CommandResult AlarmRow(CommandContext context, MapActionOutcome outcome)
    {
        var (status, commit) = Reading(outcome);
        return AlarmRow(context, status, commit, outcome.Code);
    }

    /// <summary>A scan recipient of another kind or with an empty id is still the recipient, so its refusal row
    /// names it rather than dropping it.</summary>
    private static CommandResult AlarmRow(CommandContext context, Status status, Commit commit, string code)
        => Row(context, context.TryResource(AlarmWaveContract.Scan, out string _, out string _), status, commit, code);

    /// <summary>A wave start's row and, when the action minted one, the handle beside it. The handle is a port of
    /// the node, not a column of the result schema, and a refusal publishes no handle at all — the port is absent,
    /// which is the one honest form of a value that does not exist.</summary>
    private static CommandResult WaveRow(CommandContext context, MapActionOutcome outcome, RuntimeHandle? handle = null)
    {
        var (status, commit) = Reading(outcome);
        return WaveRow(context, status, commit, outcome.Code, handle);
    }

    private static CommandResult WaveRow(CommandContext context, Status status, Commit commit, string code, RuntimeHandle? handle = null)
    {
        bool recipient = context.TryResource(AlarmWaveContract.Wave, out string _, out string _);
        if (recipient && handle is { } minted) context.Outputs.Handle(Blocks.ActionMapWaveStart.OutWaveHandle, minted);
        return Row(context, recipient, status, commit, code);
    }

    /// <summary>Every stop branch is written through here, which is why a refusal and an issued stop cannot
    /// describe different columns. The stop rows declare no handle, so their answer is the row and nothing else.</summary>
    private static CommandResult StopRow(CommandContext context, Status status, Commit commit, string code)
        => Row(context, context.TryRuntimeHandle(AlarmWaveContract.Waves, out _), status, commit, code);

    // ---- handlers ------------------------------------------------------------------------------------

    /// <summary>The `forge.action.map.scan_state` handler. The scan is a chained puzzle the level already built,
    /// which is what the row's `chained-puzzle` resource names. The row's `operation` selects which of the
    /// instance's own three interactions is submitted: `start` activates it, `complete` solves it for the players
    /// in it, and `reset` deactivates it — the game has no separate reset member, and the deactivation puts the
    /// instance back to the status it had before it was activated. The row declares no other port: the required
    /// number of players in the scan belongs to the puzzle's own data block, so `quorum` was never a value this
    /// handler could apply and the rulings had it deleted rather than echoed.</summary>
    internal static CommandResult ExecuteScanState(CommandContext context) => ScanState(context);

    private static CommandResult ScanState(CommandContext context)
    {
        if (!IsHost(context)) return AlarmRow(context, MapActionOutcome.Refused(ResultCode.AuthorityOrPhase));
        if (Mode(context) is not { } mode)
            return AlarmRow(context, MapActionOutcome.Refused(ResultCode.ScanOperationUnknown));
        if (!context.TryResource(AlarmWaveContract.Scan, out string kind, out string id))
            return AlarmRow(context, MapActionOutcome.Refused(ResultCode.ScanResourceMissing));
        if (kind != AlarmWaveContract.ChainedPuzzleKind)
            return AlarmRow(context, MapActionOutcome.Refused(ResultCode.AlarmResourceNotChainedPuzzle));
        if (string.IsNullOrEmpty(id)) return AlarmRow(context, MapActionOutcome.Refused(ResultCode.ResourceIdEmpty));
        var manager = ChainedPuzzleManager.Current;
        if (manager == null || manager.WasCollected) return AlarmRow(context, MapActionOutcome.Refused(ResultCode.ChainedPuzzleManagerUnavailable));
        var instance = ChainedPuzzleLookup.Find(id, out var ambiguous);
        // Two instances of the level answering the one puzzle address is ambiguous: neither is started or written.
        if (instance == null) return AlarmRow(context, MapActionOutcome.Refused(ambiguous ? ResultCode.AlarmAmbiguous : ResultCode.AlarmNotFound));
        if (instance.WasCollected || instance.Data == null) return AlarmRow(context, MapActionOutcome.Refused(ResultCode.AlarmUnavailable));
        // All three operations address an instance that is already there, so none mints a handle: the scan row
        // publishes no handle port, and a stop row addresses a wave rather than a puzzle.
        if (mode != "start") return Submit(context, instance, mode == "force_complete"
            ? eChainedPuzzleInteraction.Solve : eChainedPuzzleInteraction.Deactivate, mode == "force_complete" ? ResultCode.ScanCompleted : ResultCode.ScanReset);
        if (instance.IsActive) return AlarmRow(context, MapActionOutcome.AlreadyInState(ResultCode.AlarmAlreadyActive));
        if (instance.IsSolved) return AlarmRow(context, MapActionOutcome.Refused(ResultCode.AlarmAlreadySolved));
        if (instance.NRofPuzzles() <= 0) return AlarmRow(context, MapActionOutcome.Refused(ResultCode.AlarmNoCores));
        return Submit(context, instance, eChainedPuzzleInteraction.Activate, ResultCode.ScanStarted);
    }

    private static readonly Action<string, Exception> ReportScan = static (site, error) => MapLog.Contained("alarm-wave.scan-" + site, error);

    /// <summary>The scan row's write: the same submit-and-read-back the alarm row makes, so a scan that did not
    /// land is an unknown commit and not a confirmed one.</summary>
    private static CommandResult Submit(CommandContext context, ChainedPuzzleInstance instance,
        eChainedPuzzleInteraction interaction, string issuedCode)
    {
        var (status, commit, code) = AlarmActions.Submit(instance, interaction, issuedCode, ReportScan);
        return AlarmRow(context, status, commit, code);
    }

    /// <summary>The row's `mode`, or null when the request named one this row does not carry. The parameter is
    /// structural, so it is read from the plan's own parameters rather than from a wired port.</summary>
    private static string? Mode(CommandContext context)
    {
        var mode = context.Text(AlarmWaveContract.ScanMode);
        return mode is "start" or "force_complete" or "reset" ? mode : null;
    }

    /// <summary>The `forge.action.map.wave_start` handler: one Mastermind survival wave, started through the
    /// native entry that registers the event the stop side names, with the handle that names it published.</summary>
    internal static CommandResult ExecuteStartWave(CommandContext context)
    {
        if (!IsHost(context)) return WaveRow(context, MapActionOutcome.Refused(ResultCode.AuthorityOrPhase));
        if (!context.TryResource(AlarmWaveContract.Wave, out string kind, out string id)) return WaveRow(context, MapActionOutcome.Refused(ResultCode.WaveResourceMissing));
        if (kind != AlarmWaveContract.WaveKind) return WaveRow(context, MapActionOutcome.Refused(ResultCode.WaveResourceNotWave));
        var resource = WaveResourceSession.Current?.Resolve(id);
        if (resource == null) return WaveRow(context, MapActionOutcome.Refused(ResultCode.ResourceUnavailable));
        var waveSession = WaveResourceSession.Current!;
        // A relay behavior replaying its predecessor's fact after a checkpoint restore asks for an entry the stored
        // checkpoint already had started: the start does not run twice.
        if (waveSession.Relay.RefuseRestoredStart(resource.Address, waveSession.Now()))
        {
            waveSession.Record(resource, 0, ResultCode.WaveStartAlreadyStarted);
            return WaveRow(context, MapActionOutcome.Refused(ResultCode.WaveStartAlreadyStarted));
        }
        // A wave a behavior started is one run of its list entry while any member is still the current native event:
        // starting the same entry again is refused by name instead of opening a second full run beside it, so a
        // trigger that keeps firing cannot pile runs and handles up. The handle the first start published still
        // stops that run. A run whose native state cannot be read has not been proved over, so it still counts.
        if (waveSession.Handled.TryGetValue(resource.Address, out var running) && StillRunning(running))
        {
            waveSession.Record(resource, 0, ResultCode.WaveStartAlreadyStarted);
            return WaveRow(context, MapActionOutcome.Refused(ResultCode.WaveStartAlreadyStarted));
        }
        var members = resource.NativeMembers;
        // The ids are the author's data blocks, so a wave the level does not define is refused here as an
        // unavailable resource rather than handed to the master, which would accept the call and register an
        // event for a wave that can never spawn a group. Every member is checked before the first native write.
        foreach (var member in members)
            if (GameDataBlockBase<SurvivalWaveSettingsDataBlock>.GetBlock(member.SettingsId) == null
                || GameDataBlockBase<SurvivalWavePopulationDataBlock>.GetBlock(member.PopulationId) == null)
                return WaveRow(context, MapActionOutcome.Refused(ResultCode.ResourceUnavailable));
        // The reference frame is resolved once for the whole wave, before any native write, so every member
        // starts from the same host reference and a missing reference writes nothing.
        var master = Mastermind.Current;
        if (master == null || master.WasCollected) return WaveRow(context, MapActionOutcome.Refused(ResultCode.WaveMastermindUnavailable));
        if (!TryRequest(waveSession, resource, out var request, out var requestRefusal))
        {
            waveSession.Record(resource, 0, requestRefusal!);
            return WaveRow(context, MapActionOutcome.Refused(requestRefusal!));
        }
        // Reserve the identity before any native write. A missing provider or exhausted handle pool
        // must not spawn an unaddressable wave and then report rejected/no-commit.
        if (_current is not { } self || !self._registration.IsRegistered)
            return WaveRow(context, MapActionOutcome.Refused(ResultCode.HandleUnavailable));
        RuntimeHandle handle;
        try { handle = self._registration.CastEffectHandle(AlarmWaveContract.HandleLifetime); }
        catch (RuntimeContractException) { return WaveRow(context, MapActionOutcome.Refused(ResultCode.HandleUnavailable)); }
        bool published = false, attemptedNative = false;
        NativeWaveLifetime? lifetime = null;
        try
        {
            // An explicit action pins the exact resource: one run, one handle, every member of it.
            var epoch = self._kernel.WorldEpoch;
            var run = waveSession.Registry.BeginRuntime(epoch, resource);
            lifetime = new NativeWaveLifetime(waveSession, run, epoch);
            // Every explicitly started wave uses the same owner identity. Autonomous native hosts do not enter
            // this action and acquire no Forge-owned lifetime.
            self._registration.RegisterOwnedLifetime(handle, context.ScopeId, lifetime.Cancel);
            MapActionOutcome outcome = MapActionOutcome.Issued(ResultCode.WaveStarted);
            for (int index = 0; index < members.Count; index++)
            {
                Mastermind.MastermindEvent? registered;
                waveSession.Beginning = (run, index);
                waveSession.StartingNativeWave = lifetime;
                lifetime.BeginMember(index);
                try { attemptedNative = true; outcome = StartWave(master, members[index], request, 0f, out registered); }
                finally { waveSession.Beginning = null; waveSession.StartingNativeWave = null; lifetime.EndMember(); }
                if (outcome.Commit == MapActionCommit.Refused)
                {
                    // Nothing of this wave was written when its first member was refused. A later refusal leaves
                    // earlier members started: they are stopped in the finally below and the commit is unknown.
                    if (index == 0) return WaveRow(context, outcome);
                    waveSession.Record(resource, 0, ResultCode.WaveMemberRefused, detail: outcome.Code, run: run, member: index);
                    return WaveRow(context, Status.Failed, Commit.Unknown, ResultCode.WaveMemberRefused);
                }
                // The existing successful Trigger readback is another read-only binding point if the static
                // Spawn postfix ran before Mastermind exposed the event. No EventID-only cleanup is attempted.
                if (registered != null) lifetime.ObserveCreated(registered.EventID);
                if (registered == null || registered.WasCollected || Mastermind.Current is not { } current
                    || !IsCurrentWave(current, registered) || !lifetime.Observed(index)
                    || lifetime.EventOf(index) != registered.EventID)
                    return WaveRow(context, Status.Failed, Commit.Unknown, ResultCode.WaveStartNotObserved);
            }
            if (!lifetime.IsLive()) return WaveRow(context, Status.Failed, Commit.Unknown, ResultCode.WaveStartNotObserved);
            // Natural native completion releases ownership silently. It never makes an additional StopEvent call.
            self._registration.RegisterNative(handle, lifetime, lifetime.KeepHandleWhileUncertain);
            waveSession.Own(lifetime);
            waveSession.Handled[resource.Address] = lifetime;
            published = true;
            return WaveRow(context, outcome, handle);
        }
        catch (RuntimeContractException) when (!attemptedNative)
        { return WaveRow(context, MapActionOutcome.Refused(ResultCode.HandleUnavailable)); }
        catch (Exception error)
        {
            MapLog.Contained("alarm-wave.start-commit", error);
            return WaveRow(context, Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
        }
        finally
        {
            if (!published)
            {
                if (attemptedNative) lifetime?.AbortUnpublished();
                if (self._registration.IsRegistered)
                {
                    try { self._registration.ReleaseEndedHandle(handle, WaveHandleShape, "wave-start-unpublished"); }
                    catch (RuntimeContractException error) when (error.Code is RuntimeAbiCodes.StaleHandle or "stale-world")
                    { /* A native callback already cancelled this slot or ended its world. */ }
                }
            }
        }
    }

    private static bool StillRunning(NativeWaveLifetime run)
    {
        try { return run.IsLive(); }
        catch (Exception error) { MapLog.Contained("alarm-wave.start-running-read", error); return true; }
    }

    /// <summary>Starts a list entry a restored checkpoint owed, through the same native start and the same host reference as
    /// <c>wave_start</c>, with the delay that was left. No handle is minted (no behavior asked for it); the run is owned by the
    /// session, so its host's completion still stops it. Returns the refusal code, or null when every member started.</summary>
    internal static string? StartRestored(WaveResourceSession waveSession, WaveResource resource, float delay)
    {
        var master = Mastermind.Current;
        if (master == null || master.WasCollected) return ResultCode.WaveMastermindUnavailable;
        var members = resource.NativeMembers;
        foreach (var member in members)
            if (GameDataBlockBase<SurvivalWaveSettingsDataBlock>.GetBlock(member.SettingsId) == null
                || GameDataBlockBase<SurvivalWavePopulationDataBlock>.GetBlock(member.PopulationId) == null)
                return ResultCode.ResourceUnavailable;
        if (!TryRequest(waveSession, resource, out var request, out var refusal)) return refusal;
        NativeWaveLifetime? lifetime = null;
        bool attempted = false, published = false;
        try
        {
            var epoch = waveSession.WorldEpoch;
            var run = waveSession.Registry.BeginRuntime(epoch, resource);
            lifetime = new NativeWaveLifetime(waveSession, run, epoch);
            for (int index = 0; index < members.Count; index++)
            {
                Mastermind.MastermindEvent? registered;
                waveSession.Beginning = (run, index);
                waveSession.StartingNativeWave = lifetime;
                lifetime.BeginMember(index);
                MapActionOutcome outcome;
                try { attempted = true; outcome = StartWave(master, members[index], request, delay, out registered); }
                finally { waveSession.Beginning = null; waveSession.StartingNativeWave = null; lifetime.EndMember(); }
                if (outcome.Commit == MapActionCommit.Refused) return index == 0 ? outcome.Code : ResultCode.WaveMemberRefused;
                if (registered != null) lifetime.ObserveCreated(registered.EventID);
                if (registered == null || registered.WasCollected || Mastermind.Current is not { } current
                    || !IsCurrentWave(current, registered) || !lifetime.Observed(index) || lifetime.EventOf(index) != registered.EventID)
                    return ResultCode.WaveStartNotObserved;
            }
            if (!lifetime.IsLive()) return ResultCode.WaveStartNotObserved;
            waveSession.Own(lifetime);
            // The handle the author's behavior kept for this entry before the checkpoint keeps naming the entry's run.
            if (waveSession.Handled.TryGetValue(resource.Address, out var earlier)) earlier.ReplaceWith(lifetime);
            published = true;
            return null;
        }
        catch (Exception error)
        {
            MapLog.Contained("alarm-wave.restored-start", error);
            return ResultCode.NativeCommitException;
        }
        finally
        {
            if (!published && attempted) lifetime?.AbortUnpublished();
        }
    }

    /// <summary>The `forge.action.map.wave_stop` handler: the same handle mechanism on the wave side. The native
    /// stop resolves the provider-owned handle, checks the native instance, then calls StopEvent.
    /// A reusable native event ID alone never authorizes a stop.</summary>
    internal static CommandResult ExecuteStopWave(CommandContext context)
    {
        if (!IsHost(context)) return StopRow(context, Status.Rejected, Commit.None, ResultCode.AuthorityOrPhase);
        if (!context.TryRuntimeHandle(AlarmWaveContract.Waves, out var handle))
            return StopRow(context, Status.Rejected, Commit.None, ResultCode.WaveHandleMissing);
        try
        {
            // A handle that resolves to no native object of this world (never minted here, an ended world, or a
            // wave that is gone) is one answer: there is nothing to stop.
            if (!TryLifetime(handle, out var lifetime)) return StopRow(context, Status.Rejected, Commit.None, ResultCode.StaleHandle);
            // Every member of the run stops; the stop is observed only when none of them is current any more.
            lifetime!.Cancel("explicit-cancel");
            // The native entry ran but its final state could not be established. Never call that no write.
            if (Mastermind.Current is not { WasCollected: false } || lifetime.AnyCurrent())
                return StopRow(context, Status.Failed, Commit.Unknown, ResultCode.WaveStopNotObserved);
            return StopRow(context, Status.Succeeded, Commit.Confirmed, ResultCode.WaveStopped);
        }
        catch (Exception error)
        {
            MapLog.Contained("alarm-wave.stop-commit", error);
            return StopRow(context, Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
        }
    }

    // ---- the handle ----------------------------------------------------------------------------------

    private static readonly HandlePort WaveHandleShape = new("effect", AlarmWaveContract.HandleLifetime);


    /// <summary>The register uses a reusable numeric ID; the handle names a native instance.
    /// Done is a cycle state: native MastermindUpdate moves Done back to waiting.
    /// StopEvent removes the instance. See evidence/wave-state-identity.json.</summary>
    internal static bool IsCurrentWave(Mastermind master, Mastermind.MastermindEvent expected)
        => master != null && !master.WasCollected && expected != null && !expected.WasCollected
            && expected.EventType == eMastermindEventType.SurvivalWave
            && master.TryGetEvent(expected.EventID, out var live) && live != null && !live.WasCollected
            && live.Pointer != IntPtr.Zero && live.Pointer == expected.Pointer;

    /// <summary>The native object one live handle of this provider names, or false when the handle is not live:
    /// a handle this provider never cast, one whose world has ended, and one whose native object is gone are the
    /// same answer to the only question the stop row asks — there is nothing to stop — and no branch guesses
    /// which of them it was.</summary>
    private static bool TryNative(RuntimeHandle handle, out object? native)
    {
        native = null;
        if (_current is not { } self) return false;
        if (!self._registration.TryNative(handle, out native)) return false;
        return native != null;
    }

    /// <summary>The same question asked of a wave handle: the run the handle names, or false when it names no live
    /// run of this provider. A wave handle only ever holds a <see cref="NativeWaveLifetime"/>.</summary>
    private static bool TryLifetime(RuntimeHandle handle, out NativeWaveLifetime? lifetime)
    {
        lifetime = null;
        if (!TryNative(handle, out var native) || native is not NativeWaveLifetime binding || !binding.IsLive()) return false;
        while (binding.Successor != null) binding = binding.Successor;
        lifetime = binding;
        return true;
    }

    // ---- native actions ------------------------------------------------------------------------------

    /// <summary>Starts one native member of a survival wave on the host. The master's own entry answers whether it accepted the
    /// wave and hands back the event id it registered, which is the identity the wave's stop side and the wave
    /// facts both use. The population points the call spent are not read back here: the capability's result
    /// schema declares no column for them, so a reading this layer could not report would be a value nothing
    /// observes.
    ///
    /// Every member of one wave uses the same request: <see cref="TryRequest"/> resolves it once.</summary>
    private static MapActionOutcome StartWave(Mastermind master, WaveNativeMember member, WaveRequest request, float delay,
        out Mastermind.MastermindEvent? registered)
    {
        registered = null;
        if (master.WasCollected) return MapActionOutcome.Refused(ResultCode.WaveMastermindUnavailable);
        if (request.Node.WasCollected) return MapActionOutcome.Refused(ResultCode.WaveCourseNodeUnavailable);
        bool started = master.TriggerSurvivalWave(request.Node, member.SettingsId, member.PopulationId, out ushort eventId,
            request.SpawnType, delay, request.AreaDistance, request.Position,
            true, request.CustomDirection, request.Direction, request.WorldEventFilter);
        if (!started) return MapActionOutcome.Refused(ResultCode.WaveRejectedByMastermind);
        // The event the wave registered is what the handle names. Reading it back here is what makes the stop
        // side a lookup of the same event rather than a second search for it; a registration the master accepts
        // but does not hold is not a wave this layer can name, and the handle is left unpublished for it.
        master.TryGetEvent(eventId, out registered);
        return MapActionOutcome.Issued(ResultCode.WaveStarted);
    }

    /// <summary>The native request frame of one Forge-started wave.</summary>
    private readonly record struct WaveRequest(AIGraph.AIG_CourseNode Node, SurvivalWaveSpawnType SpawnType,
        float AreaDistance, UnityEngine.Vector3 Position, bool CustomDirection, UnityEngine.Vector3 Direction,
        string WorldEventFilter);

    /// <summary>Reads the complete native request of a Forge-started wave from its host:
    /// <list type="bullet">
    /// <item><description>a per-component scan uses the exact live bioscan core and the owning ChainedPuzzle block's
    /// area distance;</description></item>
    /// <item><description>a whole alarm (`chained-puzzle`) uses the first core and the same block distance;</description></item>
    /// <item><description>every other host reuses the frame its first native wave of this world was requested with
    /// (node, spawn type, distance, position, direction, filter), and is refused when none was captured;</description></item>
    /// <item><description>a wave with no host keeps the action contract's level-default frame.</description></item>
    /// </list>
    /// A puzzle host that is solved with `DisableSurvivalWaveOnComplete` refuses, so a relay never outlives it.</summary>
    private static bool TryRequest(WaveResourceSession session, WaveResource resource, out WaveRequest request,
        out string? refusal)
    {
        request = default;
        refusal = null;
        if (resource.Host == null)
        {
            if (CourseNode() is not { } fallback)
            { refusal = ResultCode.WaveCourseNodeUnavailable; return false; }
            request = new(fallback, SurvivalWaveSpawnType.InRelationToClosestAlivePlayer, 2, UnityEngine.Vector3.zero,
                false, UnityEngine.Vector3.zero, "");
            return true;
        }
        var nativeHost = resource.Host.Host;
        if (nativeHost.ValueKind != JsonValueKind.Object || !nativeHost.TryGetProperty("kind", out var kindValue))
        { refusal = ResultCode.WaveRequestUnavailable; return false; }
        var kind = kindValue.GetString();
        if (kind is not ("scan" or "chained-puzzle"))
        {
            if (session.HostFrame(resource.Host) is not { } frame)
            { refusal = ResultCode.WaveHostReferenceUnavailable; return false; }
            if (frame.Node is not { WasCollected: false } node)
            { refusal = ResultCode.WaveCourseNodeUnavailable; return false; }
            request = new(node, frame.SpawnType, frame.AreaDistance, frame.Position, frame.CustomDirection,
                frame.Direction, frame.WorldEventFilter);
            return true;
        }
        try
        {
            var id = nativeHost.GetProperty("id").GetUInt32();
            var puzzle = ChainedPuzzleLookup.Find(id.ToString(CultureInfo.InvariantCulture), out var ambiguous);
            if (puzzle == null)
            { refusal = ambiguous ? ResultCode.WaveHostAmbiguous : ResultCode.WaveCourseNodeUnavailable; return false; }
            var data = GameDataBlockBase<ChainedPuzzleDataBlock>.GetBlock(id);
            if (data == null)
            { refusal = ResultCode.WaveRequestUnavailable; return false; }
            if (puzzle.IsSolved && data.DisableSurvivalWaveOnComplete)
            { refusal = ResultCode.WaveHostCompleted; return false; }
            var components = ScanInstanceResolver.Components(puzzle);
            var index = kind == "scan" ? nativeHost.GetProperty("scanIndex").GetInt32() : 0;
            if (index < 0 || index >= components.Count
                || components[index].TryCast<CP_Bioscan_Core>()?.m_courseNode is not { WasCollected: false } source)
            { refusal = ResultCode.WaveCourseNodeUnavailable; return false; }
            request = new(source, SurvivalWaveSpawnType.InRelationToClosestAlivePlayer, data.SurvivalWaveAreaDistance,
                UnityEngine.Vector3.zero, false, UnityEngine.Vector3.zero, "");
            return true;
        }
        catch (Exception error)
        {
            MapLog.Contained("alarm-wave.request-read", error);
            refusal = ResultCode.WaveRequestUnavailable;
            return false;
        }
    }

    /// <summary>The level's first course node, or null when the node graph has none. The node list is the game's
    /// own (`AIG_CourseNode.s_allNodes`), which is what makes this a reading of the level rather than a guessed
    /// coordinate.</summary>
    private static AIGraph.AIG_CourseNode? CourseNode()
    {
        var nodes = AIGraph.AIG_CourseNode.s_allNodes;
        if (nodes == null) return null;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node != null && !node.WasCollected) return node;
        }
        return null;
    }

    // ---- resource providers --------------------------------------------------------------------------

    /// <summary>Installed wave declarations, including levels not yet selected when plans load.</summary>
    internal static IReadOnlyList<ResourceRef> EnumerateWaves()
    {
        var references = new List<ResourceRef>();
        var session = WaveResourceSession.Current;
        if (session != null) foreach (var row in session.Registry.Resources)
            references.Add(new ResourceRef(AlarmWaveContract.WaveKind, row.Address));
        return references.AsReadOnly();
    }

    /// <summary>Plan resolution reads the installed declaration. ExecuteStartWave checks the active level
    /// and native blocks before any spawn; those live objects do not exist during startup plan loading.</summary>
    internal static ResourceRef? ResolveWave(string resourceId)
        => WaveResourceSession.Current?.Registry.Resolve(resourceId) != null
            ? new ResourceRef(AlarmWaveContract.WaveKind, resourceId) : null;
}

