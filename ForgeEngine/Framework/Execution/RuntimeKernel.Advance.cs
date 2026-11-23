using System;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    // Empty receipts have no tick identity or mutable contents. Reusing this immutable
    // result keeps an otherwise idle advance from allocating a new receipt every frame.
    internal static readonly TickResult EmptyTickResult = new(0, 0, 0,
        Array.Empty<CommandReceipt>(), Array.Empty<EventReceipt>());

    private TickResult AdvanceCore(long simulationTick, bool isHost, bool receipts)
    {
        kernelScheduler.collectReceipts = receipts;
        Thread(); RuntimeJson.Require(!advancing && world.worldStarted, "dispatch-lifecycle", "World must be started and dispatch cannot be recursive.");
        RuntimeJson.Integer(simulationTick); RuntimeJson.Require(simulationTick >= CurrentTick, "time-reversal", "Simulation time cannot go backwards.");
        if (migrationPaused)
            return kernelScheduler.QuietTickResult();
        kernelScheduler.BeginAdvanceTick(simulationTick, isHost);
        var commands = kernelScheduler.tickCommands; var events = kernelScheduler.tickEvents; var processed = 0; var executed = 0;
        var scheduleReports = receipts ? kernelScheduler.tickSchedules : null;
        var gateRefusals = kernelScheduler.tickGateRefusals;
        advancing = true;
        try
        {
            if (!isHost)
            {
                ExpirePredictions();
                return kernelScheduler.DrainClientTick(receipts);
            }
            PrepareHostTick();
            DispatchDueEvents(simulationTick, isHost, commands, events, scheduleReports, gateRefusals, ref processed, ref executed);
            FinishOwnerTick();
        }
        finally { EndAdvanceTick(); }
        return receipts ? kernelScheduler.CompleteAdvanceTick(processed, executed, commands, events, kernelScheduler.tickSchedules, kernelScheduler.tickLeases, gateRefusals)
            : kernelScheduler.CompleteOutputsTick(processed, executed);
    }

    private void PrepareHostTick()
    {
        CleanDeferredResults();
        kernelScheduler.FlushDeferredResults();
        kernelScheduler.pulseReceipts = kernelScheduler.collectReceipts ? kernelScheduler.tickCommands : null; kernelScheduler.pulseEvents = kernelScheduler.tickEvents;
        PrepareOwnerTick();
        // The lifetime passes ask a polled kind's resolver about every held entity, and answer a reported kind
        // (RuntimeModule.EntityEndReports) from the ends its owner reported: with every held kind reported and no new
        // end, the walk below reads integers and one set and calls no provider. The walk itself stays per tick because
        // the same passes carry the time limits (lifetime-ended, ExpiresAtTick, effect periods) and the lifecycle
        // generation re-checks, which are integer comparisons.
        CleanScheduledLifetimes(kernelScheduler.collectReceipts ? kernelScheduler.tickSchedules : null); kernelVariables.CleanStateLifetimes(kernelScheduler.collectReceipts ? kernelScheduler.tickLeases : null); CleanHandleLifetimes();
        // The effect pass runs before the queue is walked and owns its own receipts: a layer's period re-runs an
        // action that is already applied, which is not a second arrival at the event that applied it.
        CleanEffectLifetimes();
        entityLives.verifyReportedLives = false;
        // The tick's own debounce releases are queued before the queue is walked, so a quiet window that passed
        // is dispatched in this advance and never waits for an event that may never come.
        QueueGateReleases(kernelScheduler.tickGateRefusals);
        FlushGroupChecks();
    }

    private void FinishOwnerTick()
    {
        kernelOwner.ownerOutputs.AddRange(kernelOwner.ownerControls); kernelOwner.ownerControls.Clear();
    }

    private void EndAdvanceTick()
    {
        currentCommand = null; Walking(null); entityLives.RestoreEntityMemo(default); advancing = false; kernelScheduler.pulseReceipts = null; kernelScheduler.pulseEvents = null;
        kernelVariables.FlushScopeReleases();
        FlushGroupChecks();
    }
}
