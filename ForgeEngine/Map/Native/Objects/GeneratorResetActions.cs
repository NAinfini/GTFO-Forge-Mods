using System;
using ForgeEngine.Framework;
using LevelGeneration;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>The native half of `forge.action.map.generator_reset`. A powered generator gets the interaction the game itself
/// uses when a cell leaves it: `LG_PowerGenerator_Core.AttemptInteract` with `RemoveCell`, carrying the player and the cell
/// data the generator's current state already names, so the state change (and the game's own handling of the cell) runs
/// through the generator's replicated state on the master. The reading after the call is the generator's own status; a
/// generator that still reads powered is an unknown commit, not a claimed one.</summary>
internal sealed class GeneratorResetActions
{
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;

    internal GeneratorResetActions(Func<bool> ready, Action<string> report)
    {
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    private static readonly TargetStep<GeneratorResetActions> ResetStep = Reset;
    private Action<EntityReference, Exception>? _reportException;

    internal CommandResult Handle(CommandContext context)
    {
        if (!Session.IsHost || !_ready()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var self = this;
        return context.EachTarget(ref self, ResetStep, EntityKinds.MapObject, CommandResult.MaximumFacts,
            ResultCode.NativeCommitException, report: _reportException ??= ReportException);
    }

    private void ReportException(EntityReference target, Exception error)
        => Report("map.generator-reset-exception: " + error.GetType().Name + ": " + error.Message);

    /// <summary>One generator: its cell taken out through the game's own interaction and its status read back.</summary>
    private static void Reset(ref GeneratorResetActions self, EntityReference target, int index, RowWriter rows)
    {
        rows.Integer(Blocks.ActionMapGeneratorReset.RowTargetCount, 1);
        if (GeneratorObservation.World != target.WorldEpoch) { rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity); return; }
        var generator = GeneratorObservation.LabelReceiver(target);
        if (generator == null)
        {
            rows.End(Status.Rejected, Commit.None, MapObjectGeneratorAddress.TryParse(target.Id[(EntityKinds.MapObject.Length + 1)..]) == null
                ? ResultCode.UnsupportedRecipient : ResultCode.GeneratorResetTargetMissing);
            return;
        }
        if (GeneratorObservation.StatusOf(generator) == ePowerGeneratorStatus.UnPowered)
        { rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.GeneratorAlreadyUnpowered); return; }
        if (generator.m_stateReplicator is not { } replicator)
        { rows.End(Status.Rejected, Commit.None, ResultCode.GeneratorResetTargetMissing); return; }
        var current = replicator.State;
        generator.AttemptInteract(new pPowerGeneratorInteraction
        {
            type = ePowerGeneratorInteractionType.RemoveCell,
            player = current.playerThatChangedTheState,
            itemData = current.itemDataThatChangedTheState
        });
        if (GeneratorObservation.StatusOf(generator) != ePowerGeneratorStatus.UnPowered)
        {
            self.Report("map.generator-reset-not-reached: " + target.Id);
            rows.End(Status.Failed, Commit.Unknown, ResultCode.GeneratorResetNotReached);
            return;
        }
        rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.GeneratorReset);
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { MapLog.Contained("generator-reset.report", reportError); }
    }
}
