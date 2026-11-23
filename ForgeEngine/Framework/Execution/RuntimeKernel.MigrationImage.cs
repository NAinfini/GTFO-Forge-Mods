using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    // Only a detached decoder has a destination. Persisted handles and callbacks must belong to the live kernel.
    private RuntimeKernel? migrationDestination;

    private RuntimeKernel NewMigrationImage(long epoch, long tick)
    {
        var image = new RuntimeKernel(Identity, Limits);
        image.StartupState = StartupState;
        image.factAdmissionClosure = factAdmissionClosure;
        image.WorldEpoch = epoch;
        image.CurrentTick = tick;
        image.world.worldStarted = true;
        image.world.worldHost = world.worldHost;
        image.world.tickRate = world.tickRate;
        image.world.lifecycleGeneration = world.lifecycleGeneration + 1;
        image.eventQueue.sequence = eventQueue.sequence;
        image.kernelFlow.scheduleSequence = kernelFlow.scheduleSequence;
        image.kernelOwner.ownerControlSequence = kernelOwner.ownerControlSequence;
        image.migrationDestination = this;
        image.migrationPaused = true;
        // The image's held lives come from the old host's tables; the first sweep after the commit asks them.
        image.entityLives.verifyReportedLives = true;
        // These are load-bound declarations. No staged decoder writes their state or invokes their handlers.
        registry.CopyLoadBoundTo(image.registry);
        plans.CopyLoadBoundTo(image.plans);
        foreach (var pair in kernelOwner.ownerIdentities) image.kernelOwner.ownerIdentities.Add(pair.Key, pair.Value);
        return image;
    }

    private static HandleSlot CloneMigrationSlot(HandleSlot slot) => new()
    {
        Kind = slot.Kind,
        Lifetime = slot.Lifetime,
        ProviderId = slot.ProviderId,
        Generation = slot.Generation,
        Active = slot.Active,
        ScheduleId = slot.ScheduleId,
        EffectId = slot.EffectId,
        StatusKind = slot.StatusKind,
        Native = slot.Native,
        NativeIsLive = slot.NativeIsLive,
        NativeLife = slot.NativeLife,
        Cancel = slot.Cancel
    };

    private void RequireMigrationOwnerSettled()
    {
        RuntimeJson.Require(kernelOwner.ownerDeferrals.Count == 0 && ownerSettlements.Count == 0 && kernelOwner.ownerDeferralTaken == null,
            "migration-owner-deferral", "A pending holder callback or unsent holder settlement has no portable handover.");
    }

    private void CommitMigrationImage(RuntimeKernel image, PreparedProviderState handover,
        NativeStateRestorePlan? native, long sourceEpoch, bool isHost, IReadOnlyList<MigrationArrival> arrivals)
    {
        // Prepare scratch at the migration boundary too: a newly promoted peer may have had no local effects.
        kernelEffects.lifetimeScratch.EnsureCapacity(image.kernelEffects.effects.Count);
        kernelOwner.ownerFlightScratch.EnsureCapacity(image.kernelOwner.ownerFlights.Count);
        var usage = image.plans.TickUsage.ToArray();
        var attempted = 0;
        var switched = false;
        var phase = "provider";
        migrationPaused = true;
        try
        {
            // Mark an adopter as attempted before entering it: a callback can change its table and then throw.
            foreach (var provider in handover.Providers)
            {
                attempted++;
                ProviderHandoverRead(() => { provider.Adoption.Adopt(); return true; });
            }
            phase = "runtime";
            image.world.worldHost = isHost;
            SwapMigrationState(image);
            switched = true;
            RefreshSubscriptionGates();
            // Synchronous facts emitted by native restore belong to the target image; a failed commit swaps
            // them out with that image rather than buffering and then silently clearing them as source arrivals.
            migrationPaused = false;
            // Native sources are tied to the roots renewed by these observers. Native restore retains its own
            // rollback boundary in that recalled world; providers and managed state remain reversible until it ends.
            NotifyLifecycle(RuntimeLifecycleKind.WorldChanged, sourceEpoch, isMigration: true);
            RestoreNativeState(native);
        }
        catch (Exception error)
        {
            migrationPaused = true;
            if (switched) { SwapMigrationState(image); RefreshSubscriptionGates(); }
            var failures = new List<string>();
            for (var index = attempted - 1; index >= 0; index--)
            {
                var provider = handover.Providers[index];
                try { ProviderHandoverRead(() => { provider.Adoption.Rollback(); return true; }); }
                catch (Exception rollback) { failures.Add(provider.Id + ": " + rollback.Message); }
            }
            migrationPaused = true;
            var failed = phase == "provider" ? handover.Providers[attempted - 1].Id : "runtime/native";
            if (failures.Count != 0)
                throw new RuntimeContractException("migration-provider-rollback",
                    failed + " adoption failed (" + error.Message + "); rollback failed: " + string.Join(" | ", failures));
            if (phase == "provider")
                throw new RuntimeContractException("migration-provider-adopt", failed + ": " + error.Message);
            throw;
        }

        // Budget cells are owned by loaded plans and retain their identity. All keyed rows have already decoded
        // into the independent table, so this non-callback commit never parses or admits migration data.
        plans.TickUsage.Clear();
        foreach (var row in usage)
            plans.TickUsage.Add(row.Key, (row.Value.Events, row.Value.Commands));
        kernelFlow.controlWalk.Clear();
        kernelFlow.BeginActivation();
        children.Clear();
        BindDeferredResults();
        // Retiring local sounds is cleanup after a successful commit, never a side effect of decoding a bad image.
        image.ClearSounds(broadcast: false);
        migrationPaused = false;
        migrationArrivals.Clear();
        log.LogWorldBegan();
        foreach (var arrival in arrivals)
            log.LogTriggerFired(arrival.Provider, arrival.Event.BindingId, arrival.Event.EventId,
                arrival.Event.CauseId, arrival.Event.RootEventId ?? arrival.Event.EventId);
    }

    // The simulation thread cannot dispatch or call observers between these assignments. The same swap retains
    // the complete prior image for rollback, rather than reparsing a second snapshot after live state was lost.
    private void SwapMigrationState(RuntimeKernel image)
    {
        eventQueue.SwapWith(image.eventQueue);
        world.SwapWith(image.world);
        kernelVariables.SwapWith(image.kernelVariables);
        kernelFlow.SwapWith(image.kernelFlow);
        kernelEffects.SwapWith(image.kernelEffects);
        kernelOwner.SwapWith(image.kernelOwner);
        kernelOwner.ownerSplits.Clear(); kernelOwner.ownerSplit = null;
        kernelScheduler.SwapWith(image.kernelScheduler);
        entityLives.SwapWith(image.entityLives);
    }
}
