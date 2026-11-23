using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon.Native;

/// <summary>
/// The four numerical instance-override rows as this package executes them: `fire_rate`, `spread`, `recoil` and
/// the generic `property`. The decisions are made in <see cref="WeaponActionRuntime"/> and the writes in
/// <see cref="WeaponOverrideApplier"/>; this file is the seam between them and one dispatched command — it reads
/// the request frame, hands the decision a ledger to record against, and answers with the row's own result
/// schema.
///
/// The host decides the action and the owner channel invokes it on the mapped holder. The native applier checks
/// that the resolved weapon is local to that holder before replacing its blocks. A request about an instance whose life
/// has ended is refused with `stale-entity` before the applier is asked for anything — a reference from a
/// previous life names a weapon that no longer exists, and an instance override is meaningless without one.
/// </summary>
internal sealed class WeaponActionAdapter
{
    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _canExecute;
    private readonly WeaponOverrideApplier _applier;
    private readonly Action<string> _report;
    private long _applications;
    private sealed record Modifier(EntityReference Equipment, RuntimeHandle Handle)
    {
        internal bool RestoreFailureReported { get; set; }
    }
    private readonly Dictionary<WeaponOverrideSource, Modifier> _modifiers = new();
    private readonly List<WeaponOverrideSource> _replaced = new();
    private sealed record Application(EntityReference Equipment, string PlanId, string NodeId, string Id);
    private readonly Dictionary<Application, WeaponOverrideSource> _effectWriters = new();
    internal Func<RuntimeEffectContext, bool>? RestoreOwner { get; set; }
    internal RuntimeModuleHandle? Registration { get; set; }

    internal WeaponActionAdapter(IForgeEngine kernel, Func<bool> canExecute, WeaponOverrideApplier applier,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>`forge.action.weapon.fire_rate`. Result fields: the four fixed columns, then `rate`.</summary>
    internal CommandResult FireRate(CommandContext context)
        => Run(context, WeaponActionRuntime.FireRate, Columns.Rate,
            Math.Max(0L, context.Integer(WeaponOverrideContract.FireRateDurationSlot) ?? 0L), Blocks.ActionWeaponFireRate.OutModifier);

    /// <summary>`forge.action.weapon.spread`. Result fields: the four fixed columns, then `cone`.</summary>
    internal CommandResult Spread(CommandContext context)
        => Run(context, WeaponActionRuntime.Spread, Columns.Cone, 0);

    /// <summary>`forge.action.weapon.recoil`. Result fields: the four fixed columns, then `horizontal`.</summary>
    internal CommandResult Recoil(CommandContext context)
        => Run(context, WeaponActionRuntime.Recoil, Columns.Horizontal, 0);

    /// <summary>`forge.action.weapon.property`. Result fields: the four fixed columns, then `field` and `value`.</summary>
    internal CommandResult Property(CommandContext context)
        => Run(context, WeaponActionRuntime.Property, Columns.FieldValue, 0, Blocks.ActionWeaponProperty.OutProperty);

    /// <summary>
    /// The four override rows' restore callback, which the kernel calls when the effect its card asked for ends —
    /// by its duration, by a cancellation, by a released plan or by the world. What ends is what this card's writer
    /// set on the instance the application was about; a request the kernel kept after the module had already undone
    /// it is the no-op it should be.
    ///
    /// The subject is the row's own `equipment` recipient. The explicit application identities name the native
    /// writers, including the same identity on every periodic re-execution. A node alone cannot identify a layer.
    /// </summary>
    internal void RestoreProperty(RuntimeEffectContext context)
    {
        if (context.Subject.Count == 0) return;
        if (RestoreOwner?.Invoke(context) == true) return;
        string? failure = null;
        foreach (var id in context.ApplicationIds)
        {
            var application = new Application(context.Subject[0], context.PlanId, context.NodeId, id);
            if (!_effectWriters.TryGetValue(application, out var source)) continue;
            if (_applier.Ledger.RevertWriter(application.Equipment, source, out var code))
                _effectWriters.Remove(application);
            else failure ??= code;
        }
        if (failure != null)
            throw new RuntimeContractException(failure, "Weapon property restore was not confirmed.");
    }

    /// <summary>
    /// The shared body of the four block-replacing rows: decide, refuse as decided, otherwise record the request
    /// in the ledger — which is what writes it — and answer with the row's own result fields.
    ///
    /// A native write whose swaps could not be read back is answered `failed`+`unknown` with the applier's own
    /// code, not as a rejection: the row cannot claim the instance was left alone, and it must not claim the
    /// values are on it either. Nothing is recorded in the ledger for that answer and nothing is retried — the
    /// applier has already written the instance's own block back, or kept its entry reachable for the equipment
    /// life, world and session boundaries when even that could not be read back.
    /// </summary>
    private CommandResult Run(CommandContext context, Func<CommandContext, WeaponActionOutcome> decide,
        Columns columns, long duration, int handleOutput = -1)
    {
        if (!_canExecute() || _kernel.StartupState != RuntimeStartupState.Ready) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var outcome = decide(context);
        if (outcome.Code.Length != 0) return Outcome.Rejected(outcome.Code);
        // These result schemas require the named input (rate, cone, horizontal or property value).
        // Refuse a malformed direct call before writing, not after the ABI discovers a missing column.
        if (!HasColumns(columns, outcome.Fields)) return Outcome.Rejected(ResultCode.InputUnsupported);
        var equipment = outcome.Equipment!;
        if (!_kernel.IsEntityCurrent(equipment))
            return Outcome.Rejected(ResultCode.StaleEntity);
        if (!_applier.HasCurrentEquipment(equipment)) return Outcome.Rejected(RuntimeKernel.OwnerUnsupportedCode);

        // A kernel-managed effect already has its one handle and clock. A duration-port request without an
        // effect block mints through the same pool before writing, so a handle-budget refusal changes nothing.
        RuntimeHandle? minted = null;
        if (handleOutput >= 0 && context.EffectHandleId == null && context.EffectApplicationId == null)
        {
            if (Registration == null) return Outcome.Rejected(ResultCode.ModuleUnregistered);
            try { minted = Registration.CastEffectHandle("entity_life"); }
            catch (RuntimeContractException error) { return Outcome.Rejected(error.Code); }
        }
        var application = context.EffectApplicationId is { } applicationId
            ? new Application(equipment, context.PlanId, context.NodeId, applicationId) : null;
        var source = application != null && _effectWriters.TryGetValue(application, out var existingSource)
            ? existingSource : new WeaponOverrideSource(context.PlanId, context.NodeId, ++_applications);
        var request = new WeaponOverrideRequest(equipment, outcome.Fields, source,
            context.EffectHandleId == null && application == null ? duration : 0);
        WeaponOverrideCommit commit;
        string code;
        try { commit = _applier.Ledger.Begin(request, context.SimulationTick, out code); }
        catch (Exception error)
        {
            // The applier classifies its own native failures, so an exception reaching here is a defect in the
            // write path rather than an answer. Its commit is not observable, and the row must not escape as a
            // generic handler exception: it reports the defect and answers `failed`+`unknown` rather than claiming
            // anything about the instance. A recovery entry the applier kept, if any, stays reachable at the
            // equipment-life, world and session boundaries.
            _report("weapon.override-write-failed equipment=" + equipment.Id + ": " + error.GetType().Name + ": " + error.Message);
            code = ResultCode.OverrideWriteUnconfirmed;
            if (minted is { } failedHandle) Registration!.ReleaseEndedHandle(failedHandle, ModifierPort, "override-write-failed");
            WriteRow(context.Rows, columns, outcome.Fields, Status.Failed, Commit.Unknown, code);
            return Outcome.FailedUnknown(code);
        }
        if (commit != WeaponOverrideCommit.Applied)
        {
            if (minted is { } refusedHandle) Registration!.ReleaseEndedHandle(refusedHandle, ModifierPort, "override-not-applied");
            _report("weapon.override-not-applied equipment=" + equipment.Id + " commit=" + commit + " code=" + code);
            return NotApplied(context.Rows, columns, outcome.Fields, commit, code);
        }
        if (application != null) _effectWriters[application] = source;
        WriteRow(context.Rows, columns, outcome.Fields, Status.Succeeded, Commit.Confirmed, ResultCode.OverrideApplied);
        if (minted is { } handle)
        {
            _modifiers[request.Source] = new Modifier(equipment, handle);
            Registration!.RegisterNative(handle, request, _ => _kernel.IsEntityCurrent(equipment)
                && _applier.Ledger.HasWriter(equipment, request.Source));
            Registration.RegisterCancel(handle, () =>
            {
                if (!_applier.Ledger.RevertWriter(equipment, request.Source, out var restoreCode))
                    throw new RuntimeContractException(restoreCode, "Weapon override restore was not confirmed.");
                _modifiers.Remove(request.Source);
            });
            context.Outputs.Handle(handleOutput, handle);
        }
        return Outcome.Succeeded();
    }
    /// <summary>A duration-port expiry ends the exact returned handle, never every application of its node.</summary>
    internal void Expired(EntityReference equipment, WeaponOverrideSource source, bool restored, string code)
    {
        EndModifier(source, "expired", restored, code);
    }

    private void EndModifier(WeaponOverrideSource source, string reason, bool restored, string code)
    {
        if (!_modifiers.TryGetValue(source, out var modifier)) return;
        var handle = modifier.Handle;
        if (!restored)
        {
            if (!modifier.RestoreFailureReported)
            {
                Registration!.ReportOwnerEnd(handle, reason, Outcome.FailedUnknown(code));
                modifier.RestoreFailureReported = true;
            }
            return;
        }
        _modifiers.Remove(source);
        // A prior unknown owner ending is an immutable receipt. Later local recovery releases native state;
        // it never rewrites that receipt or claims a duplicate transport reply confirmed another operation.
        if (!modifier.RestoreFailureReported)
            Registration!.ReportOwnerEnd(handle, reason, Outcome.Succeeded());
        try { Registration!.ReleaseEndedHandle(handle, ModifierPort, "override-ended"); }
        catch (RuntimeContractException error)
        {
            if (!WeaponProfileActions.AlreadyGone(error.Code))
                _report("weapon.override-handle-release-refused: " + error.Code);
        }
    }

    /// <summary>Clients do not run the host handle sweep. Reclaim replaced writers on the same lifecycle tick.</summary>
    internal void PruneModifiers()
    {
        if (Registration == null || _modifiers.Count == 0) return;
        _replaced.Clear();
        foreach (var pair in _modifiers)
        {
            // IsHandleLive is a command-thread API, not a lifecycle-observer read. The ledger is this
            // provider's own truth; ReleaseEndedHandle tolerates a host sweep that already spent the slot.
            if (!_applier.Ledger.HasWriter(pair.Value.Equipment, pair.Key))
                _replaced.Add(pair.Key);
        }
        foreach (var source in _replaced) EndModifier(source, "replaced", true, "");
    }

    internal void EndFor(EntityReference equipment, bool restored, string code)
    {
        if (restored)
            foreach (var application in new List<Application>(_effectWriters.Keys))
                if (application.Equipment == equipment) _effectWriters.Remove(application);
        foreach (var pair in new List<KeyValuePair<WeaponOverrideSource, Modifier>>(_modifiers))
            if (pair.Value.Equipment == equipment) EndModifier(pair.Key, "equipment-life-ended", restored, code);
    }

    internal void ForgetModifiers()
    {
        foreach (var modifier in _modifiers.Values)
            try { Registration!.ReleaseEndedHandle(modifier.Handle, ModifierPort, "override-boundary"); }
            catch (RuntimeContractException error)
            {
                if (!WeaponProfileActions.AlreadyGone(error.Code)) _report("weapon.override-handle-release-refused: " + error.Code);
            }
        _modifiers.Clear();
        _effectWriters.Clear();
    }

    /// <summary>
    /// The answer one commit that did not apply gives. A refusal is `rejected` with its own code and no row: it
    /// promises the instance was left exactly as it was found. A failure that happened before any native write is
    /// `failed` with no commit and the applier's own code: the instance was provably not touched, but the request
    /// was acceptable and the write is what failed, so calling it a refusal would blame the plan. A write that may
    /// have landed and could not be read back is `failed` with an unknown commit: the row may not claim the instance
    /// was left alone, and it may not claim the values are on it either.
    /// </summary>
    private static CommandResult NotApplied(RowWriter rows, Columns columns, IReadOnlyList<WeaponOverrideField> fields,
        WeaponOverrideCommit commit, string code)
    {
        if (commit == WeaponOverrideCommit.Refused) return Outcome.Rejected(code);
        var unknown = commit == WeaponOverrideCommit.Unconfirmed;
        WriteRow(rows, columns, fields, Status.Failed, unknown ? Commit.Unknown : Commit.None, code);
        return unknown ? Outcome.FailedUnknown(code) : Outcome.Failed(code);
    }

    private static readonly HandlePort ModifierPort = new("effect", "entity_life");

    /// <summary>Which of the four rows' own result columns one request answers with.</summary>
    private enum Columns { Rate, Cone, Horizontal, FieldValue }

    /// <summary>Whether the request set the field its row's required column reports. Run refuses a request without
    /// it before applying the override; it never invents zero. The spread row's `cone` is the one nullable column, so
    /// any of the spread fields answers it.</summary>
    private static bool HasColumns(Columns columns, IReadOnlyList<WeaponOverrideField> fields)
    {
        if (columns == Columns.FieldValue) return fields.Count > 0;
        foreach (var field in fields)
            switch (columns)
            {
                case Columns.Rate when field.Name == "fire_rate":
                case Columns.Horizontal when field.Name == "recoil_horizontal":
                case Columns.Cone when field.Name is "spread_cone" or "spread_movement_scale" or "spread_aim_scale":
                    return true;
            }
        return false;
    }

    /// <summary>
    /// The one row every request answers with: the four fixed columns, then this row's own column. `target` is the
    /// instance the override landed on, and the commit state distinguishes a write that happened from a request that
    /// was refused. `rate` is the rate the plan asked for, which is the unit the ledger stores — the native
    /// seconds-between-shots value is produced at the write and never travels back. `cone` is the absolute cone, or
    /// null for a request that set the whole-spread multiplier and no absolute cone, rather than the multiplier under
    /// a cone's name. `property`'s `field` and `value` are the name that was accepted and the value resolved against
    /// it — the request's own, because the block's value is the native member, a different unit for `fire_rate`.
    /// </summary>
    private static void WriteRow(RowWriter rows, Columns columns, IReadOnlyList<WeaponOverrideField> fields,
        Status status, Commit commit, string code)
    {
        rows.Begin(0);
        switch (columns)
        {
            case Columns.FieldValue:
                rows.Text(Blocks.ActionWeaponProperty.RowField, fields[0].Name).Number(Blocks.ActionWeaponProperty.RowValue, fields[0].Value);
                break;
            case Columns.Cone:
                if (Field(fields, "spread_cone") is { } cone) rows.Number(Blocks.ActionWeaponSpread.RowCone, cone);
                else rows.Null(Blocks.ActionWeaponSpread.RowCone);
                break;
            case Columns.Rate:
                rows.Number(Blocks.ActionWeaponFireRate.RowRate, Field(fields, "fire_rate")!.Value);
                break;
            case Columns.Horizontal:
                rows.Number(Blocks.ActionWeaponRecoil.RowHorizontal, Field(fields, "recoil_horizontal")!.Value);
                break;
        }
        rows.End(status, commit, code);
    }

    /// <summary>The value of the last field of one name, as the row reported it.</summary>
    private static double? Field(IReadOnlyList<WeaponOverrideField> fields, string name)
    {
        double? value = null;
        foreach (var field in fields) if (field.Name == name) value = field.Value;
        return value;
    }
}
