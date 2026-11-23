using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using Player;
using ForgeCore;

namespace ForgePlayer.Native;

/// <summary>
/// The native half of `forge.action.player.action_lock`.
///
/// Native synchronized control path: `RequestToggleControlsEnabled(bool)` (dump.cs:650974, RVA 0x1305C40),
/// `SendToggleControlsEnabled(bool)` (:657676, RVA 0x18DBDF0), `IncomingSyncControlsToggle(pPlayerControlsToggle)`
/// (:657694, RVA 0x18D94C0), and the packet's sole bool Value (:657481). The same members are in
/// catalog/native/native-runtime.json. The complete Send/Incoming bodies submit the packet and conditionally call
/// `PlayerLocomotion.ChangeState`: false maps to `StandStill` (17), true to `Stand` (0). That readable locomotion
/// state is shared with vanilla callers (including the pouncer consume behavior) and carries no writer, command,
/// receipt or prior-state identity. No action mask can be passed on this path; local InputMapper readers and
/// inventory gates do not constitute a synchronized per-action command. This binding supports all only.
///
/// <b>What a row reports.</b> The host can read its proxy's locomotion state, but that is not a receipt that the
/// addressed peer applied the synchronized request and it cannot identify this command among vanilla or overlapping
/// writers. A committed row therefore carries `action-lock-requested` — the native
/// synchronized toggle entry was called and returned — and not a claim that a durable owned state was read back.
/// No row of this half ever reports a lock it did not ask the game for.
///
/// <b>Restoring.</b> Accepted locks retain the handle and exact player references they were written for. A
/// refused handle cast attempts compensation and reports an unknown commit if native restoration throws.
/// Provisional claims are reserved before native calls, including calls that can mutate and then throw. For
/// an accepted lock, duration expiry, cancellation and disposal release each claim only after restoration or
/// proof that its player life ended; overlapping claims keep the player locked. Expired handles return to the
/// kernel only after all their claims end. Failed restoration remains pending rather than being reported as
/// completed. The kernel's separate provider-cancellation contract still spends a cancelled public handle even
/// when its hook fails; this half reports that failure and retries only the unresolved native claims.
/// </summary>
internal sealed class PlayerActionLockWrites : IDisposable
{
    /// <summary>The one attached half the static handler answers through.</summary>
    private static PlayerActionLockWrites? _current;

    private readonly RuntimeModuleHandle _registration;
    /// <summary>The identity half the session this lock belongs to built. It is read when a command arrives, because
    /// the half is built after the registration this lock is attached to.</summary>
    private readonly Func<PlayerIdentityModule?> _players;
    private readonly Action<string> _report;
    private readonly Func<long> _monotonicMilliseconds;
    private const long FailureWindowMilliseconds = 5000;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    /// <summary>The locks this half wrote and has not lifted, keyed by the handle that names them. It is this
    /// provider's own ledger, not a claim about the game's input state: every lift proves the agent is still the
    /// one the reference named before calling anything.</summary>
    private readonly Dictionary<RuntimeHandle, Held> _locks = new();
    // A single unpublished command may need compensation without a kernel handle. Its key is the default value,
    // which no cast handle has. Pending cleanup blocks new writes, so spent handles cannot grow a second budget.
    private static readonly RuntimeHandle ProvisionalKey = default;
    private readonly Dictionary<EntityReference, int> _targetLocks = new();
    private readonly List<RuntimeHandle> _due = new();
    /// <summary>The rows of the command being applied, by recipient position: the handler runs on one thread and
    /// rewrites a row as it learns what happened, then writes them all once at the end.</summary>
    private readonly Outcome[] _rows = new Outcome[CommandResult.MaximumFacts];
    private RuntimeLifecycleSubscription? _lifecycle;
    private bool _disposed;
    private bool _disposing;

    /// <summary>One command's whole lock: the players it was written on and the tick it comes due.</summary>
    private sealed class Held
    {
        internal long ExpiryTick = -1;
        internal RuntimeHandle Handle;
        internal string? EndReason;
        internal bool Releasing;
        internal bool Cancelled;
        internal readonly List<EntityReference> Targets = new();
        internal readonly FailureReports ToggleFailures;
        internal readonly FailureReports ReleaseFailures;

        internal Held(Held? diagnostics = null)
        {
            ToggleFailures = diagnostics?.ToggleFailures ?? new();
            ReleaseFailures = diagnostics?.ReleaseFailures ?? new();
        }
    }

    // Exactly two diagnostic slots per held command. Retries remain per tick; diagnostics are first detail,
    // then counted summaries, without retaining an error object or a per-target/event history.
    private sealed class FailureReports
    {
        internal string? Detail;
        internal long TotalCount;
        internal long RepeatCount;
        internal long WindowStarted;
    }

    private PlayerActionLockWrites(RuntimeModuleHandle registration, Func<PlayerIdentityModule?> players,
        Action<string> report, Func<long>? monotonicMilliseconds)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _monotonicMilliseconds = monotonicMilliseconds ?? (static () => Environment.TickCount64);
        // This is refusal evidence for the whole provider, not native state recovery. Register before taking
        // the lifecycle subscription so a closed or duplicate registration cannot leak that subscription.
        _registration.RegisterProviderHandover(HandoverSchema, CaptureHandover, PrepareHandover);
        _lifecycle = _registration.ObserveLifecycle(OnLifecycle);
    }

    private const string HandoverSchema = "player-native-refusal";

    // Capture runs during ordinary native migration-buffer saves. Pending work is a bounded refusal marker,
    // never an exception or an attempt to release/reapply controls. StandStill cannot identify this writer, and
    // a spent RegisterCancel handle can still leave private cleanup here, so neither native state nor public
    // handle liveness can stand in for this ledger.
    private JsonElement CaptureHandover(JsonElement obligations)
        => RuntimeJson.From(new
        {
            blocked = _locks.Count != 0 || obligations.GetProperty("handles").GetArrayLength() != 0
                || obligations.GetProperty("costs").GetArrayLength() != 0
        });

    private static ProviderHandoverAdoption PrepareHandover(JsonElement context)
    {
        bool sourceBlocked = HandoverBlocked(context.GetProperty("source"));
        bool retainedBlocked = HandoverBlocked(context.GetProperty("retained"));
        // Registering makes every Player-native obligation eligible for generic handover preflight. Keep the
        // refusal provider-wide, including other handle kinds, callbacks, and held resource reservations.
        if (sourceBlocked || retainedBlocked || context.GetProperty("handles").GetArrayLength() != 0
            || context.GetProperty("owner").GetProperty("flights").EnumerateArray().Any(flight =>
                flight.GetProperty("costs").EnumerateArray().Any(cost =>
                    cost.GetProperty("provider").GetString() == ModuleDefinition.ProviderId)))
            throw new RuntimeContractException("migration-native-handle",
                "Player native obligations and unresolved cleanup have no proven handover.");
        return new ProviderHandoverAdoption(Array.Empty<ProviderHandleAdoption>(), static () => { }, static () => { });
    }

    private static bool HandoverBlocked(JsonElement envelope)
    {
        // A missing/old provider envelope is not proof that its private cleanup was empty. Both worlds must
        // explicitly describe the current refusal schema; no compatibility path reconstructs native identity.
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String
            || schema.GetString() != HandoverSchema
            || !envelope.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
            || payload.EnumerateObject().Count() != 1 || !payload.TryGetProperty("blocked", out var blocked)
            || blocked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new RuntimeContractException("migration-provider-data",
                "Player handover requires an explicit boolean refusal marker in the current schema.");
        return blocked.GetBoolean();
    }

    /// <summary>Attaches the one half the static handler answers through. It is created after `RegisterModule`
    /// returned, because both the handle it mints and the lifecycle subscription it takes need a registration that
    /// exists.</summary>
    internal static PlayerActionLockWrites Attach(RuntimeModuleHandle registration, Func<PlayerIdentityModule?> players,
        Action<string> report, Func<long>? monotonicMilliseconds = null)
    {
        var previous = _current;
        _current = new PlayerActionLockWrites(registration, players, report, monotonicMilliseconds);
        return previous ?? _current;
    }

    /// <summary>Detaches the half when it is still the current one, so a session that is going away cannot leave
    /// its released registration in the slot a later session would mint through.</summary>
    internal static void Detach(PlayerActionLockWrites writes)
    {
        if (ReferenceEquals(_current, writes)) _current = null;
        writes?.Dispose();
    }

    internal static CommandResult Execute(CommandContext context)
        => _current is { } self
            ? self.Apply(context)
            : Outcome.Rejected(ResultCode.ActionLockUnavailable);


    private CommandResult Apply(CommandContext context)
    {
        CheckThread();
        if (_disposed) return Refuse(ResultCode.ActionLockUnavailable);
        if (!Members(context.Text(PlayerActionLockContract.ActionsSlot), out string? refusal))
            return Refuse(refusal!);
        if (!context.IsHost || !Session.IsHost) return Refuse(ResultCode.AuthorityOrPhase);
        if (!Duration(context, out long ticks)) return Refuse(ResultCode.ActionLockDurationInvalid);

        int targetCount = context.Count(PlayerActionLockContract.TargetsSlot);
        var targets = new List<EntityReference>(targetCount);
        for (int index = 0; index < targetCount; index++)
            if (context.TryEntity(PlayerActionLockContract.TargetsSlot, index, out var target)) targets.Add(target);
        // The kernel answers an empty recipient collection itself, so a recipient that is not an entity means the
        // port carried none: every row is about the recipient at its position, so no row is written for the rest.
        if (targets.Count == 0 || targets.Count != targetCount) return Refuse(ResultCode.MissingField);
        // The over-budget refusal is the one refusal that may not write a row per supplied reference: the set it
        // is refusing is already past the row bound, so the command answers in the established no-row shape.
        if (targets.Count > CommandResult.MaximumFacts)
            return Refuse(ResultCode.TooManyTargets);

        // The whole request is judged before the first player is touched: every reference is resolved to the agent
        // it names, so a command that cannot be carried out as asked never locks half the players it named.
        var players = _players();
        var rows = _rows;
        var subjects = new List<(EntityReference Reference, PlayerAgent Agent, int Row)>();
        for (int index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (players == null || !players.CanCommit)
            { rows[index] = Outcome.Rejected(ResultCode.AuthorityOrPhase); continue; }
            if (!EntityKinds.Is(target, EntityKinds.Player))
            { rows[index] = Outcome.Rejected(ResultCode.UnsupportedRecipient); continue; }
            var agent = players.CurrentAgent(target);
            if (agent == null || agent.Pointer == IntPtr.Zero)
            { rows[index] = Outcome.Rejected(ResultCode.StaleEntity); continue; }
            subjects.Add((target, agent, index));
            rows[index] = Outcome.Succeeded(ResultCode.ActionLockRequested);
        }
        // Nothing to lock means nothing to name: no handle is minted and no port is published, which is the one
        // honest form of a handle that stands for no lock.
        if (subjects.Count == 0) return Write(context, targets, null);

        // Native calls can mutate before throwing. Reserve the one provisional owner before calling anything
        // or invoking a reporter. Also stop admitting writes while spent public handles still owe cleanup: their
        // freed kernel slots are not additional capacity for this provider's private obligations.
        if (_locks.ContainsKey(ProvisionalKey) || _locks.Values.Any(value => value.Cancelled))
        {
            foreach (var (_, _, rowIndex) in subjects)
                rows[rowIndex] = Outcome.Rejected(ResultCode.HandleBudget);
            return Write(context, targets, null);
        }
        var held = new Held { ExpiryTick = ticks > 0 ? context.SimulationTick + ticks : -1, Releasing = true };
        _locks.Add(ProvisionalKey, held);
        var applied = new List<EntityReference>();
        var uncertain = new List<EntityReference>();
        foreach (var (reference, agent, rowIndex) in subjects)
        {
            // A diagnostic callback can dispose the session or advance its world. Never write another subject
            // or resurrect an old-world provisional owner after that callback has retired it.
            if (_disposed || !players!.CanCommit || !OwnsProvisional(held))
            {
                rows[rowIndex] = Outcome.Rejected(ResultCode.ActionLockUnavailable);
                continue;
            }
            var current = players.CurrentAgent(reference);
            if (current == null || current.Pointer == IntPtr.Zero || current.Pointer != agent.Pointer)
            {
                rows[rowIndex] = Outcome.Rejected(ResultCode.ReceiverChangedDuringCommit);
                continue;
            }
            held.Targets.Add(reference);
            _targetLocks[reference] = _targetLocks.GetValueOrDefault(reference) + 1;
            if (!Toggle(agent, false, held))
            {
                uncertain.Add(reference);
                rows[rowIndex] = Outcome.FailedUnknown(ResultCode.NativeCommitException);
            }
            else applied.Add(reference);
        }
        held.Releasing = false;
        if (!OwnsProvisional(held))
        {
            // A world boundary conclusively ended these old-world claims while a diagnostic was running.
            foreach (var reference in applied)
                Rewrite(rows, targets, reference, Outcome.Rejected(ResultCode.StaleEntity));
            return Write(context, targets, null);
        }
        if (applied.Count == 0 || _disposed || !players!.CanCommit)
        {
            Release(ProvisionalKey, "apply-failed");
            RewriteCompensated(rows, targets, applied, held, ResultCode.ActionLockUnavailable);
            return Write(context, targets, null);
        }

        // The reservation owns all calls until both minting and cancellation registration finish. A refused
        // cast therefore needs no unbounded orphan ledger, and a registration failure also retains its minted
        // handle until the last native claim is safe to retire.
        RuntimeHandle key;
        try
        {
            key = _registration.CastEffectHandle(PlayerActionLockContract.HandleLifetime);
            held.Handle = key;
            _registration.RegisterCancel(key, () =>
            {
                // Provider cancellation still spends its public handle at most once. Only private native
                // cleanup is retried after a failed hook.
                if (_locks.TryGetValue(key, out var cancelling)) cancelling.Cancelled = true;
                if (!Release(key, "handle-cancelled"))
                    throw new InvalidOperationException("The player action lock could not be fully restored.");
            });
        }
        catch (RuntimeContractException)
        {
            Release(ProvisionalKey, "handle-refused");
            RewriteCompensated(rows, targets, applied, held, ResultCode.HandleBudget);
            return Write(context, targets, null);
        }

        var accepted = held;
        if (uncertain.Count == 0)
        {
            _locks[key] = held;
            _locks.Remove(ProvisionalKey);
        }
        else
        {
            // Successful calls keep their ordinary lifetime. Unknown calls are compensated now and, if that
            // fails, remain in the single provisional slot. Moving a claim does not change its overlap count.
            accepted = new Held(held) { ExpiryTick = held.ExpiryTick, Handle = key };
            accepted.Targets.AddRange(applied);
            _locks[key] = accepted;
            held.Handle = default;
            held.Targets.Clear();
            held.Targets.AddRange(uncertain);
            Release(ProvisionalKey, "apply-failed");
        }
        // Compensation diagnostics may have disposed or retired the accepted owner too. Do not publish a
        // spent handle, or describe those already-released claims as a newly accepted lock.
        if (_disposed || !Owns(key, accepted))
        {
            RewriteCompensated(rows, targets, applied, accepted, ResultCode.ActionLockUnavailable);
            return Write(context, targets, null);
        }
        return Write(context, targets, key);
    }

    private bool OwnsProvisional(Held held) => Owns(ProvisionalKey, held);

    private bool Owns(RuntimeHandle key, Held held)
        => _locks.TryGetValue(key, out var current) && ReferenceEquals(current, held);

    private static void RewriteCompensated(Outcome[] rows, List<EntityReference> targets, List<EntityReference> applied,
        Held held, string code)
    {
        foreach (var reference in applied)
        {
            bool restored = !held.Targets.Contains(reference);
            Rewrite(rows, targets, reference, restored ? Outcome.Rejected(code) : Outcome.FailedUnknown(code));
        }
    }

    /// <summary>The one native call this row makes, on the one member of this game that turns a player's own input
    /// off and on again. It answers whether the call returned, never whether the input really changed: there is no
    /// readable state to prove that with.</summary>
    private bool Toggle(PlayerAgent agent, bool enabled, Held? restoring = null)
    {
        try
        {
            agent.RequestToggleControlsEnabled(enabled);
            return true;
        }
        catch (Exception error)
        {
            string detail = "player.action-lock-toggle-failed: " + error.GetType().Name + ": " + error.Message;
            if (restoring == null) Report(detail);
            else ReportFailure(restoring.ToggleFailures, detail);
            return false;
        }
    }

    /// <summary>Releases only claims this command has actually ended. A failed native restoration stays in the
    /// ledger, so later ticks retry that target without re-enabling players already restored by this command.
    /// A missing life is conclusive only while the identity module can authoritatively resolve its lives.</summary>
    private bool Release(RuntimeHandle key, string reason, bool endedLivesOnly = false)
    {
        if (!_locks.TryGetValue(key, out var held)) return true;
        // Reporting a native failure can call back into disposal. The in-flight release alone owns these claims.
        if (held.Releasing) return false;
        held.Releasing = true;
        try
        {
            if (!endedLivesOnly) held.EndReason ??= reason;
            var players = _players();
            if (held.Targets.Count != 0 && (players == null || !players.CanCommit))
            {
                if (!endedLivesOnly)
                    ReportFailure(held.ReleaseFailures, "player.action-lock-release-failed reason=" + reason + " players=" + held.Targets.Count);
                return false;
            }
            int failures = 0;
            for (int index = 0; index < held.Targets.Count;)
            {
                // A prior failure's reporter can revoke authority mid-batch. An unreadable next life is not
                // evidence that it ended, and it must keep its claim without another native call.
                if (!players!.CanCommit) { failures += held.Targets.Count - index; break; }
                var reference = held.Targets[index];
                var agent = players!.CurrentAgent(reference);
                bool ended = agent == null || agent.Pointer == IntPtr.Zero;
                if (endedLivesOnly && !ended) { index++; continue; }
                int remaining = _targetLocks[reference] - 1;
                // Another handle still owns a claim, so relinquishing ours must not restore its input. The last
                // claim is removed only when the native request returns or its exact player life has ended.
                if (remaining == 0 && !ended)
                {
                    bool restored = Toggle(agent!, true, held);
                    if (!Owns(key, held)) return true;
                    if (!restored) { failures++; index++; continue; }
                }
                if (remaining > 0) _targetLocks[reference] = remaining;
                else _targetLocks.Remove(reference);
                held.Targets.RemoveAt(index);
            }
            if (failures != 0)
                ReportFailure(held.ReleaseFailures, "player.action-lock-release-failed reason=" + reason + " players=" + failures);
            if (!Owns(key, held)) return true;
            if (held.Targets.Count != 0) return false;

            try
            {
                if (held.Handle.IsCast)
                    _registration.ReleaseEndedHandle(held.Handle, HandleShape, reason);
            }
            catch (RuntimeContractException error)
            {
                // Cancellation may already have spent the public handle. A world or registration boundary also
                // proves it gone; ownership/shape or any unknown refusal does not and remains pending cleanup.
                if (error.Code is not ("stale-handle" or "stale-world" or "module-unregistered"))
                {
                    ReportFailure(held.ReleaseFailures, "player.action-lock-release-failed reason=" + reason + " players=0 code=" + error.Code);
                    held.EndReason ??= reason;
                    return false;
                }
            }
            _locks.Remove(key);
            FlushFailures(held);
            return true;
        }
        finally { held.Releasing = false; }
    }

    private static readonly HandlePort HandleShape = new("effect", PlayerActionLockContract.HandleLifetime);

    /// <summary>The `actions` parameter, read once for the whole command. Every token has to be a member this game
    /// can carry; the first that is not refuses the whole request, because a partly-applied lock is not the lock
    /// the plan asked for.</summary>
    private static bool Members(string? text, out string? refusal)
    {
        refusal = null;
        if (text == null) { refusal = ResultCode.ActionLockActionsRequired; return false; }
        var tokens = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) { refusal = ResultCode.ActionLockActionsRequired; return false; }
        foreach (var token in tokens)
        {
            string? member = PlayerActionLockContract.Member(token);
            if (member == null) { refusal = ResultCode.ActionLockActionsUnknown; return false; }
            if (member != PlayerActionLockContract.AllMember)
            { refusal = ResultCode.ActionLockSlotUnavailable; return false; }
        }
        return true;
    }

    private static bool Duration(CommandContext context, out long ticks)
    {
        ticks = context.Integer(PlayerActionLockContract.DurationSlot) ?? -1;
        return ticks >= 0 && context.SimulationTick <= long.MaxValue - ticks;
    }


    /// <summary>A refusal of the whole command that publishes no result rows: it came before any recipient was
    /// judged, or the recipient set itself is what is refused (over the row bound, or not all entities), so no
    /// target is invented or truncated.</summary>
    private static CommandResult Refuse(string code)
        => code == ResultCode.ActionLockSlotUnavailable ? Outcome.Rejected(code).WithDetail(OnlyAllControls)
            : code == ResultCode.ActionLockActionsUnknown ? Outcome.Rejected(code).WithDetail(UnknownAction)
            : Outcome.Rejected(code);

    private const string OnlyAllControls = "This action can only lock all controls; weapon switching, sprinting, firing and reloading cannot be locked separately.";
    private const string UnknownAction = "The requested action is not supported. Choose all controls.";

    /// <summary>Writes the command's rows, one per recipient in order, with the handle beside them when it locked
    /// something, and concludes by the one rule every multi-target action follows.</summary>
    private CommandResult Write(CommandContext context, List<EntityReference> targets, RuntimeHandle? handle)
    {
        var writer = context.Rows;
        for (int index = 0; index < targets.Count; index++)
        {
            var row = _rows[index];
            writer.Begin(index, targets[index]).Integer(Blocks.ActionPlayerActionLock.RowTargetCount, targets.Count)
                .End(row.Status, row.Commit, row.Code);
        }
        if (handle is { } minted) context.Outputs.Handle(Blocks.ActionPlayerActionLock.OutHandle, minted);
        return writer.Conclude();
    }

    /// <summary>Replaces the optimistic row one recipient already has with what really happened to it. The rows
    /// are in recipient order and one reference is one row, so the first row still carrying the requested code is
    /// that recipient's own.</summary>
    private static void Rewrite(Outcome[] rows, List<EntityReference> targets, EntityReference reference, Outcome outcome)
    {
        for (int slot = 0; slot < targets.Count; slot++)
            if (targets[slot] == reference && rows[slot].Code == ResultCode.ActionLockRequested)
            { rows[slot] = outcome; return; }
    }

    // ---- lifetime ------------------------------------------------------------------------------------

    /// <summary>The per-world half. A checkpoint restore keeps the epoch and the exact in-memory claims: it neither
    /// reapplies controls nor reconstructs a lock from the shared locomotion state. The next frame resolves the
    /// complete life reference, so a replaced agent cannot inherit a claim by its reusable player id.
    /// A world change drops this world's ledger without calling, because the agents it
    /// named went with the level. It is local bookkeeping only: lifting what came due on a tick calls the native
    /// control toggle, whose locomotion state change reaches the Player event hook, and that hook publishes, which
    /// a read-only lifecycle notification may not do - so <see cref="Frame"/> does it from the host's own frame.</summary>
    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.PlayerActionLifecycle);
        if (value.Kind != RuntimeLifecycleKind.WorldChanged) return;
        try
        {
            var ended = _locks.Values.ToArray();
            _locks.Clear(); _targetLocks.Clear();
            foreach (var held in ended) FlushFailures(held);
        }
        catch (Exception error)
        {
            try { _report("player.action-lock-sweep-failed: " + RuntimeDiagnostics.Describe(error)); }
            catch (Exception) { /* the sweep failure is what this report carries; a reporter that throws has no further channel */ }
        }
    }

    /// <summary>The per-tick half, called from the session's frame driver once per kernel tick: lifts what came
    /// due. The caller has established that the runtime is ready; a stopped or faulted identity cannot prove a
    /// target's life ended and cannot restore native input, so unresolved claims stay for disposal and reporting.</summary>
    internal void Frame(long tick)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.PlayerActionLockFrame);
        CheckThread();
        // A half that was disposed no longer sweeps on its own; only an explicit disposal retries what is left.
        // An empty ledger holds nothing a tick could expire, so the sweep's own clear-and-enumerate is
        // skipped entirely rather than run over nothing.
        if (_disposed || _locks.Count == 0) return;
        try { Sweep(tick); }
        catch (Exception error)
        {
            try { _report("player.action-lock-sweep-failed: " + RuntimeDiagnostics.Describe(error)); }
            catch (Exception) { /* the sweep failure is what this report carries; a reporter that throws has no further channel */ }
        }
    }

    private void Sweep(long tick)
    {
        _due.Clear();
        _due.AddRange(_locks.Keys);
        foreach (var key in _due)
        {
            if (!_locks.TryGetValue(key, out var held)) continue;
            if (held.EndReason is { } reason) Release(key, reason);
            else if (held.ExpiryTick >= 0 && held.ExpiryTick <= tick) Release(key, "expired");
            else Release(key, "entity-ended", endedLivesOnly: true);
        }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("The Player action-lock half is used from one thread only.");
    }

    private void ReportFailure(FailureReports failure, string detail)
    {
        long now = _monotonicMilliseconds();
        failure.Detail = detail;
        failure.TotalCount++;
        if (failure.TotalCount == 1)
        {
            // Commit diagnostic state before calling a reporter that can reenter Dispose or throw.
            failure.WindowStarted = now;
            Report(detail);
            return;
        }
        failure.RepeatCount++;
        if (now - failure.WindowStarted >= FailureWindowMilliseconds) FlushFailure(failure, now);
    }

    private void FlushFailures(Held held)
    {
        long now = _monotonicMilliseconds();
        FlushFailure(held.ToggleFailures, now);
        FlushFailure(held.ReleaseFailures, now);
    }

    private void FlushFailure(FailureReports failure, long now)
    {
        if (failure.RepeatCount == 0) return;
        string summary = failure.Detail + " repeatCount=" + failure.RepeatCount + " totalCount=" + failure.TotalCount
            + " windowMs=" + Math.Max(0, now - failure.WindowStarted);
        // Drain before reporting: reentrant cleanup can flush other slots, but never emit this count twice.
        failure.RepeatCount = 0;
        failure.WindowStarted = now;
        Report(summary);
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception) { /* a reporter that throws must not replace the outcome it reports */ }
    }

    /// <summary>Attempts every outstanding restoration and drops the subscription. Failed targets stay recorded
    /// for an explicit disposal retry; a failure is reported rather than claimed as restored.</summary>
    public void Dispose()
    {
        CheckThread();
        if (_disposing || _disposed && _locks.Count == 0) return;
        _disposed = true;
        _disposing = true;
        try
        {
            foreach (var key in _locks.Keys.ToArray()) Release(key, "session-released");
            foreach (var held in _locks.Values.ToArray()) FlushFailures(held);
            var subscription = _lifecycle;
            _lifecycle = null;
            subscription?.Dispose();
        }
        // A reporter may call Dispose while its pending-count flush is running. That nested call must not
        // manufacture another native attempt and another summary; a later explicit call still retries cleanup.
        finally { _disposing = false; }
    }
}
