using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
// The game's blocks are named through fully qualified aliases for the same reason the override applier names
// them that way: this file's own namespace is `ForgeWeapon.Native`, so an unqualified name would be looked for
// inside the package first.
using BulletWeapon = global::Gear.BulletWeapon;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of `forge.action.weapon.property_profile`: the one place a profile reference becomes values,
/// and the one place the change is named so it can be given back.
///
/// <b>Where a profile's values come from.</b> A named weapon-project resource from
/// `forge/resources/*.resource.forge`. The resource keeps its exact id and revision and declares only real
/// Archetype and Recoil numeric fields; no native block-id or display-name substitute is accepted.
///
/// <b>How the write happens.</b> Through <see cref="WeaponOverrideApplier"/> and its ledger, which is the same
/// instance-level block replacement the named `fire_rate`, `spread` and `recoil` rows perform: one clone of the
/// instance's own block, the profile's three groups written into it, one swap. The profile is therefore one write
/// of three things and not a second write path, and only the card's three groups change — the profile's damage,
/// clip size, cost and the rest are never copied, because the card names three properties and not a whole weapon.
///
/// <b>How the change ends.</b> The row casts its own effect handle through the registration it is declared in,
/// remembers which writer it was for, and puts the instance's own block back when the request's own `duration`
/// expires, when a `cancel` step cancels the handle, or when the equipment life it was written on ends. The
/// ledger entry carries no expiry of its own: the handle is the one clock, and a second one would be a second
/// name for the same ending. The writer it remembers is the ledger's whole merge key `(plan, node, sequence)`,
/// and its sequence is this half's own application counter — not the dispatch tick, which two applications of one
/// node inside one tick would share. Every ending gives the handle back through the kernel's own release for a
/// handle whose change the provider has ended, which is the one handle entry a lifecycle observer may call: a
/// duration that expires inside the kernel's tick notification therefore returns its slot at once, and a change
/// that is over never keeps holding one of the pool's live names.
/// </summary>
internal sealed class WeaponProfileActions : IWeaponProfileSource, IDisposable
{
    /// <summary>The one attached half the row's body answers through.</summary>
    private static WeaponProfileActions? _current;

    private readonly RuntimeModuleHandle _registration;
    private readonly WeaponOverrideApplier _applier;
    private readonly Func<bool> _authoritative;
    private readonly Action<string> _report;
    private readonly WeaponLog _log;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    /// <summary>The changes this half wrote and has not put back, keyed by the handle that names them. It is this
    /// provider's own ledger and not a claim about the weapon: every restore checks the instance is still there
    /// before writing.</summary>
    private readonly Dictionary<RuntimeHandle, Modification> _modifications = new();
    /// <summary>This half's own application counter, which is the sequence of the writer identity every write is
    /// recorded under. The ledger's source is `(plan, node, sequence)` and its sequence is a counter for a
    /// behaviour that fires more than once — not a clock — because one node may write twice inside one dispatch
    /// tick and each write has to end by itself.</summary>
    private long _applications;
    private readonly List<RuntimeHandle> _due = new();
    // A cancelled handle is spent even if its callback fails. Until its provider-owned cleanup finishes,
    // do not admit another profile: recycled kernel slots must not grow an unbounded private retry table.
    private int _pendingRestores;
    private bool _disposed;

    private const long FailureWindowMilliseconds = 5000;
    private sealed class FailureReports
    {
        internal string? Detail;
        internal long TotalCount, RepeatCount, WindowStarted;
    }

    /// <summary>One command's whole change: the instance it was written on, the writer identity the ledger knows
    /// it by, the handle that names it, and the tick its duration ends at (`-1` for a request that asked for no
    /// expiry). The writer identity is the ledger's whole merge key, so the ending of one write of a node cannot
    /// drop another write of the same node.</summary>
    private sealed record Modification(EntityReference Equipment, WeaponOverrideSource Source,
        RuntimeHandle Handle, long ExpiryTick)
    {
        internal bool RestorePending;
        internal bool EndReported;
        internal bool KernelOwnsRelease;
        internal bool Restoring;
        internal string? EndReason;
        // Fixed event-family slots, never keyed by exception text, target names or repeated attempts.
        internal readonly FailureReports?[] Failures = new FailureReports?[9];
    }

    private WeaponProfileActions(RuntimeModuleHandle registration, WeaponOverrideApplier applier,
        Func<bool> authoritative, Action<string> report, WeaponLog log)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _authoritative = authoritative ?? throw new ArgumentNullException(nameof(authoritative));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Attaches the one half the row's body answers through, and answers the half it installed. It is
    /// created after `RegisterModule` returned, because the handle it casts needs a registration that exists.
    /// The caller must hold exactly this instance: it is the one the body writes through and the one
    /// <see cref="Detach"/> can give up, so a caller handed an earlier half would write through a released
    /// registration and leave this one attached for the rest of the process.</summary>
    internal static WeaponProfileActions Attach(RuntimeModuleHandle registration, WeaponOverrideApplier applier,
        Func<bool> authoritative, Action<string> report, WeaponLog log)
    {
        _current = new WeaponProfileActions(registration, applier, authoritative, report, log);
        return _current;
    }

    /// <summary>Detaches the half when it is still the current one, so a session that is going away cannot leave
    /// its released registration in the slot a later session would cast through.</summary>
    internal static void Detach(WeaponProfileActions actions)
    {
        if (ReferenceEquals(_current, actions)) _current = null;
        actions?.Dispose();
    }

    /// <summary>
    /// One profile onto one instance. The whole request is judged before the first write — the profile block, its
    /// recoil block and the duration's own arithmetic — and the handle is cast before the write, for the same
    /// reason the Map family's scan-speed row casts its own first: a change nothing can name could only be ended
    /// by the world ending.
    ///
    /// The handle is the one resource this half owns from the moment it is cast, so every path that does not end
    /// in a recorded change gives it back: a refusal, a write that failed before touching the instance, a write
    /// whose outcome nobody could read, and an exception escaping the ledger's write path — which is a defect
    /// rather than an expected answer, and is reported as one. A name left behind for a change that does not exist
    /// would hold one of the pool's slots and could be cancelled into a revert nothing owns. `Failed` and
    /// `Unconfirmed` both travel to the row as `failed`, with no commit and with an unknown commit respectively.
    /// </summary>
    public WeaponProfileOutcome TryApply(WeaponProfileRequest request, out RuntimeHandle handle, out string code)
    {
        handle = default;
        if (_disposed || Environment.CurrentManagedThreadId != _threadId) { code = ResultCode.ProfileWriteRefused; return WeaponProfileOutcome.Refused; }
        if (_pendingRestores != 0) { code = ResultCode.ProfileWriteRefused; return WeaponProfileOutcome.Refused; }
        if (!_authoritative()) { code = ResultCode.AuthorityOrPhase; return WeaponProfileOutcome.Refused; }
        if (_applier.CurrentWeapon(request.Equipment) == null) { code = RuntimeKernel.OwnerUnsupportedCode; return WeaponProfileOutcome.Refused; }
        if (!TryProfile(request.ProfileId, out var reading, out code)) return WeaponProfileOutcome.Refused;
        long expiry = -1;
        if (request.DurationTicks > 0)
        {
            // A duration that cannot be turned into an absolute tick is refused here rather than wrapped into a
            // tick that has already passed.
            if (request.DurationTicks > long.MaxValue - request.Tick) { code = ResultCode.ProfileDurationInvalid; return WeaponProfileOutcome.Refused; }
            expiry = request.Tick + request.DurationTicks;
        }
        // The one destination a profile contributes only where the instance has it: a cone value is part of every
        // archetype block, but a weapon whose fire path never samples one is not asked to hold it.
        var fields = WeaponActionRuntime.Profile(reading, _applier.ReadsCone(request.Equipment));

        RuntimeHandle minted;
        try { minted = _registration.CastEffectHandle(WeaponProfileContract.ProfileLifetime); }
        catch (RuntimeContractException) { code = ResultCode.HandleBudget; return WeaponProfileOutcome.Refused; }
        // The ledger's own duration is left at zero: the handle is this change's one clock, and the writer
        // identity is what lets the ledger put exactly this card's fields back while another writer's stay. That
        // identity is the ledger's whole merge key, and its sequence is this half's own application counter: the
        // dispatch tick is not unique per application — one node may write twice inside one tick — and a source
        // two applications share would let the older handle's ending drop the newer write.
        var source = new WeaponOverrideSource(request.PlanId, request.NodeId, ++_applications);
        var ledgerRequest = new WeaponOverrideRequest(request.Equipment, fields, source, 0);
        WeaponOverrideCommit commit;
        try { commit = _applier.Ledger.Begin(ledgerRequest, request.Tick, out code); }
        catch (Exception error)
        {
            // The applier classifies its own native failures, so an exception reaching here is a defect in the
            // write path rather than an answer. It must not escape as a generic handler exception with a minted
            // name still live, and its commit is not observable: the handle is spent, the defect is reported, and
            // the row answers `failed`+`unknown` rather than claiming anything about the instance.
            Spend(minted);
            Report("weapon.profile-write-failed equipment=" + (request.Equipment.Id ?? "") + ": "
                + error.GetType().Name + ": " + error.Message);
            code = ResultCode.OverrideWriteUnconfirmed;
            return WeaponProfileOutcome.Unconfirmed;
        }
        if (commit != WeaponOverrideCommit.Applied)
        {
            Spend(minted);
            return commit switch
            {
                WeaponOverrideCommit.Unconfirmed => WeaponProfileOutcome.Unconfirmed,
                WeaponOverrideCommit.Failed => WeaponProfileOutcome.Failed,
                _ => WeaponProfileOutcome.Refused
            };
        }
        var key = minted;
        _modifications[key] = new Modification(request.Equipment, source, minted, expiry);
        // The three end-of-state switches are applied after the swap that put the new profile on the instance:
        // they end what the old profile had running, and a request that set none of them leaves the instance's
        // burst, charge-up and firing exactly as it found them. They are one-shot transitions, not a second
        // overridable value, so there is nothing to record or give back for them.
        if (request.EndBurst || request.EndCharge || request.EndFiring)
            EndRunningState(request, _applier.CurrentWeapon(request.Equipment));
        // The cancel hook does not give the handle back: the kernel's own cancel path releases the slot itself
        // right after this hook returns, and one ending has exactly one release owner.
        _registration.RegisterCancel(minted, () => Revert(key, "handle-cancelled", release: false));
        handle = minted;
        code = "";
        if (_log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ProfileApplied, new RuntimeLogFacts
            { Entity = Named(request.Equipment), Values = new[] { RuntimeLogValue.Of("fields", (long)fields.Count) } });
        return WeaponProfileOutcome.Applied;
    }

    /// <summary>
    /// The instance's own running state, ended the way the switch asked for when a new profile takes over. The
    /// three transitions are the game's own end-of-state members and not this package's re-derivation of them:
    /// `BulletWeaponArchetype.OnStopChargeup`/`OnStopFiring` are the two bodies the archetype's own input path
    /// reaches when a charge-up or a firing run ends, and a burst is ended by clearing the counters the burst
    /// archetypes advance — `BulletWeapon.m_burstCurrentCount` and the archetype's own copy, or
    /// `BWA_SemiBurst.ResetBurstCount`, which is the semi-burst class's own reset. Nothing here writes the block
    /// the profile swap just installed.
    /// </summary>
    private static void EndRunningState(WeaponProfileRequest request, BulletWeapon? weapon)
    {
        if (weapon == null || weapon.Pointer == IntPtr.Zero) return;
        var archetype = weapon.m_archeType;
        if (archetype == null || archetype.Pointer == IntPtr.Zero) return;
        if (request.EndBurst)
        {
            weapon.m_burstCurrentCount = 0;
            if (archetype.TryCast<global::Gear.BWA_Burst>() is { } burst) burst.m_burstCurrentCount = 0;
            else if (archetype.TryCast<global::Gear.BWA_SemiBurst>() is { } semiBurst) semiBurst.ResetBurstCount();
        }
        if (request.EndCharge) archetype.OnStopChargeup();
        if (request.EndFiring) archetype.OnStopFiring();
    }

    /// <summary>
    /// Puts back every change whose duration has passed. Called from the session's one lifecycle subscription on
    /// a tick advance, so this half owns no clock of its own. Failed endings remain provider-owned and retry
    /// independently of duration: an explicit cancellation of a duration-free profile also needs recovery.
    /// </summary>
    internal void Sweep(long tick)
    {
        if (_disposed || _modifications.Count == 0) return;
        _due.Clear();
        foreach (var pair in _modifications)
            if (pair.Value.RestorePending || pair.Value.ExpiryTick >= 0 && pair.Value.ExpiryTick <= tick) _due.Add(pair.Key);
        foreach (var key in _due) Revert(key, "expired");
    }

    /// <summary>
    /// Gives up every change this half is holding without writing anything back. It is the boundary the ledger's
    /// own revert already covers: a world change took the instances with it, and a checkpoint reload or a failed
    /// runtime is a boundary where the ledger has just put every block back. An entry kept through one of those
    /// would restore a block a second time, which is why the table is dropped rather than reverted. The handles
    /// those entries were named by are given back with them, through the release a lifecycle observer may call: a
    /// world boundary already cleared the pool, which that release tolerates, and a checkpoint reload leaves the
    /// names behind unless this gives them back.
    /// </summary>
    internal void Forget()
    {
        if (_modifications.Count == 0) return;
        var forgotten = new List<Modification>(_modifications.Values);
        _modifications.Clear();
        _pendingRestores = 0;
        foreach (var modification in forgotten)
        {
            FlushFailures(modification);
            if (!modification.KernelOwnsRelease) Release(modification.Handle);
        }
    }

    /// <summary>
    /// Ends every change this half holds on one instance, for a boundary that is not the world: the equipment life
    /// those changes were written on ended, and the instance's own block is put back through the ledger before the
    /// identity that named it is dropped. This is the same revert the request's own duration performs; what makes
    /// it a separate entry point is only that nothing else in this package knows the life has ended.
    /// </summary>
    internal void EndFor(EntityReference equipment)
    {
        if (_disposed || equipment == null) return;
        _due.Clear();
        foreach (var pair in _modifications)
            if (pair.Value.Equipment == equipment) _due.Add(pair.Key);
        foreach (var key in _due) Revert(key, "equipment-life-ended");
    }

    /// <summary>
    /// Puts one change back through the ledger's own per-writer revert, so the fields this card set are the fields
    /// that go back and another writer's fields on the same instance stay true.
    ///
    /// The handle is given back by the ending that owns it, and only by that one. Expiry, an equipment life ending,
    /// a session release and a world boundary are this provider's own endings, so they return the slot through the
    /// release a lifecycle observer may call. An explicit `cancel` is not: the kernel's cancel path clears this
    /// hook, runs it, and releases the slot itself immediately afterwards, so the hook reverts the change and
    /// leaves the release to the kernel — a second release from here would be two owners performing one lifecycle
    /// transition.
    /// </summary>
    /// <summary>The equipment a record is about, or none: a record's entity is never an empty text.</summary>
    private static string? Named(EntityReference equipment) => string.IsNullOrEmpty(equipment.Id) ? null : equipment.Id;

    private void Revert(RuntimeHandle key, string reason, bool release = true)
    {
        if (!_modifications.TryGetValue(key, out var modification)) return;
        if (!release) modification.KernelOwnsRelease = true;
        if (modification.Restoring)
        {
            if (!release) throw new RuntimeContractException(ResultCode.OverrideRestoreUnconfirmed,
                "Weapon profile restore is still in progress.");
            return;
        }
        modification.EndReason ??= reason;
        bool restored;
        string code;
        string? failureDetail = null;
        modification.Restoring = true;
        try
        {
            restored = _applier.RevertProfileWriter(modification.Equipment, modification.Source,
            message => ReportFailure(modification, message), out code);
        }
        catch (Exception error)
        {
            restored = false;
            code = ResultCode.OverrideRestoreUnconfirmed;
            failureDetail = error.GetType().Name + ": " + error.Message;
        }
        finally { modification.Restoring = false; }
        // A diagnostic observer may have retired this provider during the native attempt. Its boundary
        // already removed the record; do not recreate counters or release a newly reused handle afterward.
        if (!_modifications.TryGetValue(key, out var current) || !ReferenceEquals(current, modification))
        {
            if (!restored && !release) throw new RuntimeContractException(code, "Weapon profile restore was not confirmed.");
            return;
        }
        if (!restored && !modification.RestorePending)
        {
            modification.RestorePending = true;
            _pendingRestores++;
        }
        // The first owner-end receipt is immutable. Recovery after an unknown result only cleans up this
        // provider's native state; it must not rewrite the remote result into a later confirmation.
        if (!modification.EndReported)
        {
            modification.EndReported = true;
            _registration.ReportOwnerEnd(modification.Handle, modification.EndReason,
                restored ? Outcome.Succeeded() : Outcome.FailedUnknown(code));
        }
        if (!restored)
        {
            ReportFailure(modification, "weapon.profile-restore-refused equipment=" + (modification.Equipment.Id ?? "")
                + " reason=" + modification.EndReason + " code=" + code
                + (failureDetail == null ? "" : ": " + failureDetail));
            // The kernel detaches the callback and spends its handle in finally. Throw so public cancel cannot
            // report success; this record, not that spent callback, owns all later attempts.
            if (!release) throw new RuntimeContractException(code, "Weapon profile restore was not confirmed.");
            return;
        }
        _modifications.Remove(key);
        if (modification.RestorePending) _pendingRestores--;
        FlushFailures(modification);
        if (_log.Logs(RuntimeLogLevel.Info))
            _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.ProfileRestored, new RuntimeLogFacts
            { Entity = Named(modification.Equipment), Values = new[] { RuntimeLogValue.Of("reason", modification.EndReason) } });
        if (!modification.KernelOwnsRelease) Release(modification.Handle);
    }

    /// <summary>Spends a handle this half cast for a change that was then refused, so a refused request cannot
    /// leave a live name for a modification that does not exist.</summary>
    private void Spend(RuntimeHandle handle)
    {
        try { _registration.CancelHandle(handle, HandlePort, "profile-refused"); }
        catch (RuntimeContractException error) { Report("weapon.profile-handle-release-refused: " + error.Code); }
    }

    /// <summary>
    /// Gives back the handle one ended change was named by, through the kernel's own release for a handle whose
    /// change the provider has ended. That entry is the one handle call a lifecycle observer may make, which is
    /// what lets a duration that expires inside the kernel's tick notification return its slot at once instead of
    /// waiting for an unrelated later request.
    ///
    /// Only the refusals that prove the name is already gone are tolerated — the world took the pool with it, the
    /// registration is gone, or the slot no longer answers for that generation. `handle-owner` is deliberately
    /// not among them: a handle this registration minted cannot become another provider's while it is live (the
    /// generation check makes a recycled slot a `stale-handle`, never a valid value for somebody else), so that
    /// refusal means the intended slot was not released and the ownership invariant is broken. It is reported
    /// like any other refusal rather than swallowed.
    /// </summary>
    private void Release(RuntimeHandle handle)
    {
        try { _registration.ReleaseEndedHandle(handle, HandlePort, "profile-ended"); }
        catch (RuntimeContractException error)
        {
            if (!AlreadyGone(error.Code)) Report("weapon.profile-handle-release-refused: " + error.Code);
        }
    }

    /// <summary>Whether one handle refusal proves the name is already gone, which is the outcome a release wanted
    /// and not a failure. `handle-owner` is not one of these, and neither is any code this half does not know:
    /// an unrecognised refusal leaves the question open, and the release reports it.</summary>
    internal static bool AlreadyGone(string? code)
        => code is "stale-handle" or "stale-world" or "module-unregistered";

    /// <summary>The port description a handle cast for this row's own output is checked against: the kind and the
    /// lifetime the reviewed graph declares. It is written from the contract's constants so a handle can never be
    /// released against a port that describes something else.</summary>
    private static readonly HandlePort HandlePort = new("effect", WeaponProfileContract.ProfileLifetime);

    /// <summary>
    /// A profile reference reads the exact named resource this package loaded, with no block-id fallback.
    /// </summary>
    private static WeaponPropertyProfileTable _profiles = new();
    internal static void UseProfiles(WeaponPropertyProfileTable profiles)
        => _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));

    internal static bool TryProfile(string resourceId, out WeaponProfileReading reading, out string code)
    {
        reading = _profiles.Reading(resourceId)!;
        code = reading == null ? ResultCode.ProfileUnknown : "";
        return reading != null;
    }


    private void ReportFailure(Modification modification, string detail)
    {
        var end = detail.IndexOf(' ');
        var slot = (end < 0 ? detail : detail[..end]) switch
        {
            "weapon.override-write-failed" => 0,
            "weapon.override-write-threw" => 1,
            "weapon.override-rollback-unconfirmed" => 2,
            "weapon.override-rolled-back" => 3,
            "weapon.override-rollback-failed" => 4,
            "weapon.override-restore-unconfirmed" => 5,
            "weapon.override-restore-failed" => 6,
            "weapon.profile-restore-refused" => 7,
            _ => 8
        };
        var failure = modification.Failures[slot] ??= new FailureReports();
        var now = Environment.TickCount64;
        failure.Detail = detail;
        failure.TotalCount++;
        if (failure.TotalCount == 1)
        {
            failure.WindowStarted = now;
            Report(detail);
            return;
        }
        failure.RepeatCount++;
        if (now - failure.WindowStarted >= FailureWindowMilliseconds) FlushFailure(failure, now);
    }

    private void FlushFailures(Modification modification)
    {
        var now = Environment.TickCount64;
        foreach (var failure in modification.Failures)
            if (failure != null) FlushFailure(failure, now);
    }

    private void FlushFailure(FailureReports failure, long now)
    {
        if (failure.RepeatCount == 0) return;
        var summary = failure.Detail + " repeatCount=" + failure.RepeatCount + " totalCount=" + failure.TotalCount
            + " windowMs=" + Math.Max(0, now - failure.WindowStarted);
        failure.RepeatCount = 0;
        failure.WindowStarted = now;
        Report(summary);
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception failure) { NativeCallFailure.Note("WeaponProfileActions.Report", failure); /* a reporter that throws must not replace the outcome it reports */ }
    }

    /// <summary>Puts every change this half still holds back and drops it. A session that is going away leaves no
    /// profile on a weapon that nothing can name any more.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in new List<RuntimeHandle>(_modifications.Keys)) Revert(key, "session-released");
        var remaining = new List<Modification>(_modifications.Values);
        _modifications.Clear();
        _pendingRestores = 0;
        foreach (var modification in remaining) FlushFailures(modification);
    }
}
