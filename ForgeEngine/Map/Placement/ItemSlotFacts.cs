using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>
/// The publisher behind `forge.trigger.interaction.item_placed` and `forge.trigger.interaction.item_removed`: a
/// carried item was put into the host that accepts it, or an inserted item was taken back out of it. It owns no
/// registration of its own — the rows are declared on the one Map provider, because the runtime accepts exactly one
/// provider of one identity namespace — and it knows no game type: the native half reads the three subjects off the
/// game's own committed entry and hands their keys here.
///
/// Every port of both frozen rows is required, so an event whose host, item or actor did not read publishes nothing
/// and is reported once by the name of the row and the port that was missing, rather than being published with a
/// port left out. A peer that is not the host reports once per row and publishes nothing. Each insertion and each
/// removal is its own fact: two insertions into one host are two events, told apart by this world's own sequence.
/// </summary>
internal sealed class ItemSlotFacts : IDisposable
{
    private readonly IForgeEngine _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly Row _placed, _removed;
    private readonly Func<bool> _authority;
    private readonly OnceReporter _once;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private RuntimeLifecycleSubscription? _lifecycle;
    private long _sequence;
    private bool _disposed;

    /// <param name="registration">The one Map registration this half publishes through.</param>
    /// <param name="authority">Whether this peer publishes. The rows are host-executed, so a client reports one
    /// diagnostic per row and publishes nothing.</param>
    public ItemSlotFacts(IForgeEngine kernel, RuntimeModuleHandle registration, Func<bool> authority, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _once = new OnceReporter(report ?? throw new ArgumentNullException(nameof(report)));
        var gates = registration.SubscriptionGates();
        _placed = new Row(ItemSlotContract.PlacedFact, ItemSlotContract.PlacedBinding, gates);
        _removed = new Row(ItemSlotContract.RemovedFact, ItemSlotContract.RemovedBinding, gates);
        // The sequence belongs to one world: a new world numbers its events from one again, and the world epoch in
        // the event id keeps the two worlds' ids apart.
        _lifecycle = registration.ObserveLifecycle(change =>
        {
            if (change.Kind == RuntimeLifecycleKind.WorldChanged) _sequence = 0;
        });
    }

    /// <summary>One of the two rows: its fact and its subscription gate, resolved once at attach. The kernel
    /// refreshes the gate in place, so a hook reads one field per call. A registration that does not declare the
    /// binding has no gate, and nothing is published for it.</summary>
    private sealed class Row
    {
        internal Row(string fact, string binding, IReadOnlyDictionary<string, RuntimeSubscriptionGate> gates)
        {
            Fact = fact;
            Gate = gates.TryGetValue(binding, out var gate) ? gate : null;
        }

        internal readonly string Fact;
        internal readonly RuntimeSubscriptionGate? Gate;
        internal bool Observed => Gate is { HasSubscribers: true };
    }

    /// <summary>One insertion the game committed. `hostKey` is the insertion target's map-object key
    /// (`host/dim/layer/zone/serial`), `itemKey` the level item's key (`item/dim/layer/zone/serial`) and `actor` the
    /// inserting player as the player domain names it. Returns whether a fact reached the kernel.</summary>
    public bool ItemPlaced(string? hostKey, string? itemKey, EntityReference? actor, EntityReference? item = null)
        => Offer(_placed, hostKey, itemKey, actor, item);

    /// <summary>One removal the game committed: the same three subjects, for an item taken back out of its
    /// host.</summary>
    public bool ItemRemoved(string? hostKey, string? itemKey, EntityReference? actor, EntityReference? item = null)
        => Offer(_removed, hostKey, itemKey, actor, item);

    /// <summary>`item` is the captured level-item instance the pickup facts publish (contract M16: one item identity
    /// for pickup, insertion and removal, so a rule host and a fixed-item address recognise the same entity). A caller
    /// that read only a key gets the key-derived reference of the first life.</summary>
    private bool Offer(Row row, string? hostKey, string? itemKey, EntityReference? actor, EntityReference? item)
    {
        CheckThread();
        if (_disposed || !_registration.IsRegistered) return false;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this call
        // would build, so neither the event nor a diagnostic about its ports is worth anything. The sequence only
        // numbers events that were offered, so skipping it keeps every id unique.
        if (!row.Observed) return false;
        string fact = row.Fact;
        if (!_authority())
        {
            ReportOnce("client:" + fact, fact + " observed on a non-authoritative peer: the host publishes it, a client "
                + "does not.");
            return false;
        }
        if (string.IsNullOrEmpty(hostKey)) return Missing(fact, "host", "the host has no zone or no identity");
        if (string.IsNullOrEmpty(itemKey))
            return Missing(fact, "item", "the item names no level item with a zone and an identity");
        if (actor == null || actor.WorldEpoch != _kernel.WorldEpoch)
            return Missing(fact, "actor", "the player is not a player of this world");
        long sequence = ++_sequence;
        long epoch = _kernel.WorldEpoch;
        // The placed and removed rows declare the same three outputs at the same positions.
        var raised = _registration.BeginEvent(row.Gate!);
        var values = raised.Values;
        values.Entity(Blocks.TriggerInteractionItemPlaced.OutHost, new EntityReference(EntityKinds.MapObject + ":" + hostKey, epoch, 1));
        values.Entity(Blocks.TriggerInteractionItemPlaced.OutItem, item ?? new EntityReference(EntityKinds.LevelObject + ":" + itemKey, epoch, 1));
        values.Entity(Blocks.TriggerInteractionItemPlaced.OutActor, actor);
        var result = raised.Raise(ItemSlotContract.EventId(fact, epoch, hostKey, sequence), epoch, Math.Max(0, _kernel.CurrentTick),
            "gtfo.world:" + epoch.ToString(CultureInfo.InvariantCulture));
        if (result.Status == "queued") return true;
        // Runtime owns publication refusals and their shared repeated-diagnostic summary.
        return false;
    }

    private bool Missing(string fact, string port, string reason)
    {
        ReportOnce("port:" + fact + ":" + port, fact + " not published: required port `" + port + "` did not read ("
            + reason + ").");
        return false;
    }

    private void ReportOnce(string key, string message)
    {
        _once.Report(key, message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Item slots require the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _lifecycle = null;
    }
}
