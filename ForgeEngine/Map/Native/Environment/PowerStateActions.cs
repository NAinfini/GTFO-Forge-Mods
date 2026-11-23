using System;
using ForgeEngine.Framework;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The native half of `forge.action.map.power_state`: cut or restore the power of the zones a request names.
///
/// <b>What the write is.</b> This build has no blackout object, no power grid and no member of either truth source
/// spelling one — the search of `catalog/native/native-runtime.json` finds `Blackout` only as a tone-mapping field and a
/// level-layout name. What the game does have is the expedition light mode, a per-zone replicated state the
/// environment state manager owns: `EnvironmentStateManager.AttemptSetExpeditionLightModeInZone(GlobalZoneIndex,
/// bool, pLightSequenceData)` writes it and `GetLightMode(GlobalZoneIndex)` reads it back. That state is what a
/// vanilla level's own blackout is built from, so it is this row's write and the row claims nothing beyond it:
/// what follows the mode is whatever the game attached to the zone's own light collection, and this layer neither
/// re-implements nor reports that.
///
/// The sequence argument is left at its zero value, which is the instant flip: a sequenced blackout is the
/// presentation lighting row's business (`forge.action.presentation.lighting` carries `position`, `count` and a
/// transition), and this row's reviewed graph declares no sequence member at all, so inventing one here would be
/// a choice no author made.
///
/// <b>What it refuses.</b> A reference that is not a `gtfo.zone` address of this world, a well-formed address this
/// level has no zone at, and a request whose `enabled` port is absent or not a boolean are each refused by their
/// own name. A zone already in the state the request asks for is a success that wrote nothing — the same reading
/// every other Map row makes of an already-held state — because the native entry would otherwise replicate a
/// state change that did not change anything.
/// </summary>
internal sealed class PowerStateActions
{
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;

    internal PowerStateActions(Func<bool> ready, Action<string> report)
    {
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    private struct Request
    {
        internal PowerStateActions Self;
        internal bool Enabled;
    }

    private static readonly TargetStep<Request> SwitchStep = SwitchOne;

    internal CommandResult Handle(CommandContext context)
    {
        // Both gates are kept for the same reason the scan and wave rows keep them: the ABI host fact says the
        // runtime dispatched this as a host step, and the game's own master flag is what the native entry needs.
        if (!context.IsHost || !_ready() || !Session.IsHost) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (context.Boolean(PowerStateContract.Enabled) is not { } enabled)
            return Outcome.Rejected(ResultCode.PowerEnabledRequired);
        // A member that is not a zone address refuses the whole request, before any zone is written; a zone of
        // another world answers its own `stale-entity` row while the other zones go on.
        var rows = context.Rows;
        for (int index = 0; index < rows.Count; index++)
            if (!rows.TryTarget(index, out var target) || EnvironmentZone.Parse(target) == null)
                return Outcome.Rejected(ResultCode.PowerTargetUnsupported);
        if (EnvironmentStateManager.Current == null) return Outcome.Rejected(ResultCode.PowerEnvironmentUnavailable);
        var request = new Request { Self = this, Enabled = enabled };
        return context.EachTarget(ref request, SwitchStep, maximum: CommandResult.MaximumFacts,
            countColumn: Blocks.ActionMapPowerState.RowTargetCount);
    }

    /// <summary>One zone's own decision: the state it is in now, and the one native write when that is not the
    /// state the request asked for.</summary>
    private static void SwitchOne(ref Request request, EntityReference target, int index, RowWriter rows)
    {
        var answer = request.Self.One(EnvironmentZone.Parse(target)!.Value, request.Enabled);
        rows.End(answer.Status, answer.Commit, answer.Code);
    }

    private Outcome One(EnvironmentZone address, bool enabled)
    {
        // The level's own zone table is asked first: a well-formed address this level has no zone at would
        // otherwise be written as a light mode nobody can see and reported as a success.
        if (EnvironmentZoneTable.At(address) == null) return Outcome.Rejected(ResultCode.PowerZoneMissing);
        try
        {
            // The zone was already in the requested state and that state was read back: a confirmed no-op.
            if (EnvironmentStateManager.GetLightMode(address.Global) == enabled) return Outcome.Succeeded(ResultCode.AlreadyInState);
            EnvironmentStateManager.AttemptSetExpeditionLightModeInZone(address.Global, enabled, default);
        }
        catch (Exception error)
        {
            // The entry replicates through the manager's own state replicator and answers nothing. A throw may
            // happen after part of the change landed, so it is an unknown commit.
            Report("map.power-state-exception: " + error.GetType().Name + ": " + error.Message);
            return Outcome.FailedUnknown(ResultCode.NativeCommitException);
        }
        try
        {
            if (EnvironmentStateManager.Current != null
                && EnvironmentStateManager.GetLightMode(address.Global) == enabled)
                return Outcome.Succeeded(enabled ? ResultCode.PowerRestored : ResultCode.PowerCut);
        }
        catch (Exception error)
        {
            Report("map.power-state-readback-exception: " + error.GetType().Name + ": " + error.Message);
        }
        return Outcome.FailedUnknown(ResultCode.ReadbackUnconfirmed);
    }
    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { MapLog.Contained("power-state.report", reportError); }
    }
}
