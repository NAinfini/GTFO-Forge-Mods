using System;
using ForgeEngine.Framework;
using Player;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of `forge.action.inventory.detonate`: one deployed device's own trigger, requested through the
/// item action the mine's own replicated trigger path uses.
///
/// <b>The entry point.</b> `MineDeployerInstance.WantItemAction(PlayerAgent, SyncedItemAction_New.Trigger)` is the
/// complete request the game makes when a deployed mine is triggered: it replicates the action and ends in the
/// device's own `iMineDeployerInstance_Detonation.TriggerDetonate`. `MineDeployerInstance_Detonate_Explosive` and
/// `MineDeployerInstance_Detonate_Glue` are the two implementations behind it, and the device itself decides which
/// one it is — the card takes no payload parameter for exactly that reason. `TriggerDetonate` is deliberately not
/// called directly: it would skip the replicated action, and `DoExplode` is the damage half the trigger runs.
///
/// <b>The agent.</b> The request takes the player the action is attributed to. This card declares no actor port,
/// so that is the device's placer, read from the placed item's own `Owner`, and it is used as the request's
/// source/owner only; it is not an instigator of anything the row reports. A device whose placer cannot be read is
/// refused before anything is called.
///
/// <b>What is read back.</b> The explosive payload declares `m_inDetonationProcess`, the game's own "this mine is
/// going off" state: a device that already shows it is refused rather than triggered twice, and a device that
/// shows it after the call is a commit. The glue payload declares no state at all — its trigger only starts an
/// `ExplosionSequence` coroutine — so this build has no postcondition to read for it and the row reports the
/// outcome as `unknown` rather than claiming a commit from the component still being attached, which a no-op
/// trigger would leave identical. A call whose outcome no reading can show is never claimed as a success and is
/// never retried.
///
/// <b>The refusals.</b> `detonate-write-refused`: this half is disposed or was reached off its own thread.
/// `unsupported-recipient`: the reference is not an equipment reference. `stale-entity`: it is not one of this
/// provider's current placements. `detonate-unsupported-device`: the placement is not a mine, or the mine declares
/// no detonation component. `already-detonating`: the explosive payload already shows its own detonation process.
/// `detonate-placer-unknown`: the placed item has no readable placer to attribute the item action to.
/// </summary>
internal sealed class DetonateActions : IDetonateSource, IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly Func<EntityReference, Item?> _placedItemOf;
    private readonly Func<bool> _authoritative;
    private readonly Action<string> _report;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    /// <summary>`placedItemOf` is this package's own placement table read back as the native item it holds, and
    /// the kernel's currency check is asked beside it, so a device whose life ended between the reference being
    /// made and this call is refused rather than triggered through a stale pointer.</summary>
    internal DetonateActions(IForgeEngine kernel, Func<EntityReference, Item?> placedItemOf, Func<bool> authoritative,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _placedItemOf = placedItemOf ?? throw new ArgumentNullException(nameof(placedItemOf));
        _authoritative = authoritative ?? throw new ArgumentNullException(nameof(authoritative));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>One device's trigger. Every refusal is decided before the call, so a rejected device is one
    /// nothing was called on at all.</summary>
    public DetonateOutcome Detonate(EntityReference device, out string code)
    {
        code = "";
        if (_disposed || Environment.CurrentManagedThreadId != _threadId) { code = ResultCode.DetonateWriteRefused; return DetonateOutcome.Rejected; }
        if (!_authoritative()) { code = ResultCode.AuthorityOrPhase; return DetonateOutcome.Rejected; }
        if (device == null || !EntityKinds.Is(device, EntityKinds.Equipment))
        { code = ResultCode.UnsupportedRecipient; return DetonateOutcome.Rejected; }
        if (!_kernel.IsEntityCurrent(device)) { code = ResultCode.StaleEntity; return DetonateOutcome.Rejected; }
        Item? item;
        try { item = _placedItemOf(device); }
        catch (Exception failure) { NativeCallFailure.Note("DetonateActions.Detonate", failure); code = ResultCode.StaleEntity; return DetonateOutcome.Rejected; }
        // Unity equality: an instance the game has destroyed reads as null here, which is the same answer as a
        // placement that is no longer the item it was recorded for.
        var mine = item?.TryCast<MineDeployerInstance>();
        if (mine == null) { code = ResultCode.DetonateUnsupportedDevice; return DetonateOutcome.Rejected; }
        iMineDeployerInstance_Detonation? detonation;
        try { detonation = mine.m_detonation; }
        catch (Exception failure) { NativeCallFailure.Note("DetonateActions.Detonate#2", failure); code = ResultCode.DetonateUnsupportedDevice; return DetonateOutcome.Rejected; }
        if (detonation == null || detonation.Pointer == IntPtr.Zero) { code = ResultCode.DetonateUnsupportedDevice; return DetonateOutcome.Rejected; }

        // The one in-progress state this build declares, and it is declared by the explosive payload alone. The
        // glue payload's own sequence is a coroutine with no readable flag, so for it this refusal cannot be
        // made — the row does not pretend otherwise, and the report records the gap.
        MineDeployerInstance_Detonate_Explosive? explosive;
        try { explosive = detonation.TryCast<MineDeployerInstance_Detonate_Explosive>(); }
        catch (Exception failure) { NativeCallFailure.Note("DetonateActions.Detonate#3", failure); explosive = null; }
        if (explosive != null && explosive.m_inDetonationProcess)
        {
            code = ResultCode.AlreadyDetonating;
            return DetonateOutcome.Rejected;
        }

        PlayerAgent? placer;
        try { placer = mine.Owner; }
        catch (Exception failure) { NativeCallFailure.Note("DetonateActions.Detonate#4", failure); placer = null; }
        if (placer == null) { code = ResultCode.DetonatePlacerUnknown; return DetonateOutcome.Rejected; }

        var pointer = detonation.Pointer;
        try { mine.WantItemAction(placer, SyncedItemAction_New.Trigger); }
        catch (Exception error)
        {
            Report("weapon.detonate-exception: " + error.GetType().Name + ": " + error.Message);
            code = ResultCode.NativeCommitException;
            return DetonateOutcome.Unknown;
        }
        try
        {
            // The payload's own in-progress state, where this build declares one: a mine that shows it after the
            // call is a mine that is going off, and one that does not show it is left `unknown` rather than
            // claimed.
            if (explosive != null && explosive.Pointer == pointer)
                return explosive.m_inDetonationProcess ? DetonateOutcome.Committed : Unobserved(out code);
            // The payload that declares no such state — the glue variant — has no postcondition this build can
            // read. Its `TriggerDetonate` starts an `ExplosionSequence` coroutine and writes nothing onto the
            // component, so the component still being the one the call went through says only that the call did
            // not destroy it: a trigger that does nothing at all leaves exactly the same reading. Component
            // identity is therefore not a commit and is not reported as one — the row says the outcome could not
            // be observed.
            return Unobserved(out code);
        }
        catch (Exception error)
        {
            Report("weapon.detonate-readback-exception: " + error.GetType().Name + ": " + error.Message);
            code = ResultCode.NativeCommitException;
            return DetonateOutcome.Unknown;
        }
    }

    private static DetonateOutcome Unobserved(out string code)
    {
        code = ResultCode.DetonationNotObserved;
        return DetonateOutcome.Unknown;
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception failure) { NativeCallFailure.Note("DetonateActions.Report", failure); /* a reporter that throws must not replace the outcome it reports */ }
    }

    /// <summary>A detonation is one call and leaves nothing this half holds, so disposal only stops later calls
    /// from being made through a session that is going away.</summary>
    public void Dispose() => _disposed = true;
}
