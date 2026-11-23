using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;
using static ForgeEngine.Framework.RuntimeKernel;

namespace ForgeEngine.Framework;

/// <summary>The events waiting to be dispatched and the ledger that refuses a replayed one: the priority queue, the sequence every admitted event is stamped with, the idempotency rows and their sweep marks. Data and its own bookkeeping only; who admits, dispatches and sweeps is above it.</summary>
internal sealed class EventQueue
{
    private readonly KernelWorld world;

    internal EventQueue(KernelWorld world)
    {
        this.world = world;
    }

    internal PriorityQueue<Pending, (long Tick, long Sequence)> queue = new();

    internal Dictionary<LedgerKey, LedgerRow> history = new();

    internal long sequence;

    /// <summary>How many queued dispatches still name this plan. The queue is the authority — an item carries the
    /// plans it claimed — so the answer is read from it rather than from a second counter that would have to be kept
    /// in step with every path that drops an item.</summary>
    internal int QueuedFor(LoadedPlan plan)
    {
        var count = 0;
        foreach (var entry in queue.UnorderedItems) if (NamesPlan(entry.Element, plan)) count++;
        return count;
    }

    internal long nextLedgerSweepTick, ledgerFullSweepTick = -1;

    /// <summary>The oldest event tick the ledger still answers for because of its capacity, not its horizon: every
    /// event row below it was evicted, so an event published for an earlier tick is refused as `event-stale` and a
    /// forgotten identity can never be admitted twice. <see cref="long.MinValue"/> until the first eviction.</summary>
    internal long ledgerFloorTick = long.MinValue;

    // The capacity-eviction histogram is persistent kernel scratch. A full ledger must not allocate this array
    // inside Advance on the first saturated tick.
    internal readonly int[] ledgerTickCounts = new int[EventHistoryHorizonTicks + 1];

    /// <summary>The oldest tick an event may still be published for: the horizon, raised by any eviction.</summary>
    internal long ReplayFloor => Math.Max(world.Tick - EventHistoryHorizonTicks, ledgerFloorTick);

    internal void ResetLedger(long floor = long.MinValue)
    {
        history.Clear(); nextLedgerSweepTick = 0; ledgerFullSweepTick = -1; ledgerFloorTick = floor;
    }

    /// <summary>A checkpoint restore keeps the world and rewinds it: every event identity the world admitted so far
    /// belongs to the timeline the restore replaced, so it is forgotten, and the floor moves to the current tick so an
    /// event published for that timeline is `event-stale` rather than a second admission. Whether a source restores
    /// its sequence numbers, starts them over or keeps counting, the next event it publishes is a new one. Leases
    /// and schedules keep their rows: their identities are bounded by their own tables.</summary>
    internal void ForgetEvents()
    {
        foreach (var entry in history) if (entry.Key.Space == LedgerSpace.Event) history.Remove(entry.Key);
        ledgerFloorTick = Math.Max(ledgerFloorTick, world.Tick);
    }

    /// <summary>
    /// The capacity bound. A world that admits more identities inside one horizon than the ledger holds forgets
    /// the oldest event ticks — about <see cref="EventHistoryEvictionRows"/> rows — and raises
    /// <see cref="ledgerFloorTick"/> past them, so an event published for one of those ticks is `event-stale`
    /// rather than a second admission. The floor never passes the current tick or the oldest queued work: the
    /// events of this tick, and events still waiting to run, keep their rows. Leases and schedules are never
    /// evicted; their identities are bounded by their own tables.
    /// </summary>
    internal void EvictOldestEvents()
    {
        var cutoff = world.Tick - EventHistoryHorizonTicks;
        var limit = world.Tick;
        foreach (var item in queue.UnorderedItems) if (item.Priority.Tick < limit) limit = item.Priority.Tick;
        if (limit <= cutoff) return;
        var counts = ledgerTickCounts;
        var span = (int)(limit - cutoff);
        Array.Clear(counts, 0, span);
        foreach (var entry in history)
            if (entry.Key.Space == LedgerSpace.Event && entry.Value.Tick < limit)
                counts[(int)(Math.Max(entry.Value.Tick, cutoff) - cutoff)]++;
        var floor = cutoff; var evicted = 0;
        while (floor < limit && evicted < EventHistoryEvictionRows) evicted += counts[(int)(floor++ - cutoff)];
        if (evicted == 0) return;
        foreach (var entry in history)
            if (entry.Key.Space == LedgerSpace.Event && entry.Value.Tick < floor) history.Remove(entry.Key);
        ledgerFloorTick = Math.Max(ledgerFloorTick, floor);
    }

    internal void Restamp(LedgerKey key, long tick)
    {
        if (history.TryGetValue(key, out var row) && row.Tick < tick) history[key] = row with { Tick = tick };
    }

    /// <summary>Whether the queue holds an entry something has since made stale: a schedule that ended, or a
    /// `debounce` release a later arrival moved past. Either is the only thing that can leave such an entry behind,
    /// so either is also the only thing that asks for the compaction.</summary>
    internal bool queueDirty;

    internal void QueuePulse(ScheduledJob job)
    {
        job.Handle.NextTick = job.NextTick;
        if (job.ModulePending != null)
        {
            queue.Enqueue(job.ModulePending, (job.NextTick, ++sequence));
            return;
        }
        var value = job.Template with { EventId = "scheduled:" + job.Identity + ":" + job.Index, SimulationTick = job.NextTick, RootEventId = job.Template.RootEventId ?? job.Template.EventId };
        queue.Enqueue(new Pending(job.Handle.ProviderId, job.Generation, value, job.Work, job)
        { EndSubject = job.EndSubject }, (job.NextTick, ++sequence));
    }

    /// <summary>This component's parts of a host-migration image: the event sequence, the idempotency rows and the
    /// floor an eviction raised. The queued events themselves are the flow's part: what survives is decided there.</summary>
    internal void CaptureMigration(Dictionary<string, object?> image)
    {
        image["sequence"] = sequence;
        image["history"] = history.Select(entry => (Key: entry.Key.Text, Row: entry.Value))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new { key = entry.Key, fingerprint = entry.Row.Fingerprint.Text, tick = entry.Row.Tick }).ToArray();
        // The capacity floor travels with the rows it stands for: an event the predecessor evicted stays stale
        // on the replacement host instead of reading as new there.
        image["historyFloor"] = ledgerFloorTick == long.MinValue ? null : ledgerFloorTick;
    }

    /// <summary>Reads the ledger of an image into this staged component. The sequence only moves forward: the staged
    /// component starts from the adopting machine's own.</summary>
    internal void RestoreMigration(JsonElement snapshot)
    {
        var floor = snapshot.GetProperty("historyFloor");
        ResetLedger(floor.ValueKind == JsonValueKind.Null ? long.MinValue
            : RuntimeJson.Integer(floor, 0, RuntimeJson.MaxSafeInteger));
        foreach (var entry in RuntimeJson.Rows(snapshot, "history", MaximumEventHistory))
        {
            RuntimeJson.Shape(entry, "key fingerprint tick");
            var key = entry.GetProperty("key").GetString();
            RuntimeJson.Require(!string.IsNullOrEmpty(key) && key.Length <= 1024, "migration-history", "Invalid event ledger key.");
            history.Add(LedgerKey.Parse(key!), new LedgerRow(LedgerHash.Parse(RuntimeJson.Text(entry, "fingerprint")),
                RuntimeJson.Integer(entry.GetProperty("tick"), 0, RuntimeJson.MaxSafeInteger)));
        }
        sequence = Math.Max(sequence, RuntimeJson.Integer(snapshot.GetProperty("sequence"), 0, RuntimeJson.MaxSafeInteger));
    }

    /// <summary>Exchanges the state a host migration swaps with the staged image's. Nothing runs between the
    /// assignments; the image keeps the prior state for rollback.</summary>
    internal void SwapWith(EventQueue other)
    {
        (queue, other.queue) = (other.queue, queue);
        (history, other.history) = (other.history, history);
        (sequence, other.sequence) = (other.sequence, sequence);
        (queueDirty, other.queueDirty) = (other.queueDirty, queueDirty);
        (nextLedgerSweepTick, other.nextLedgerSweepTick) = (other.nextLedgerSweepTick, nextLedgerSweepTick);
        (ledgerFullSweepTick, other.ledgerFullSweepTick) = (other.ledgerFullSweepTick, ledgerFullSweepTick);
        (ledgerFloorTick, other.ledgerFloorTick) = (other.ledgerFloorTick, ledgerFloorTick);
    }
}
