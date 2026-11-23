using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeWeapon.Native;

/// <summary>One slot of one player's backpack as the native half reads it. The item is the native object the
/// transfer and the drop position are read from; it is null for an empty slot.</summary>
internal readonly record struct NativeSlot(string Name, object? Item, IntPtr ItemIdentity, IntPtr InstanceIdentity,
    EntityReference? Equipment);


/// <summary>The three combat reload rows, decided from one observation each:
///
/// `reload_started` opens a life when a reload is really under way — the item's own flag read true. The
/// inventory's reload entry point and the flag's own setter are the same event seen from two places, so the second
/// one confirms the life the first opened instead of opening another.
///
/// `reload_transferred` is the amount that actually entered the magazine: the difference between the magazine read
/// back now and the magazine the life recorded when it opened, published once per advance with the life's baseline
/// moved forward, so a second readback of the same difference publishes nothing. A pool that grew inside the reload
/// window is the same movement seen from the pack side and is published as a transfer too, never as a refill — the
/// refill row is the inventory family's and is not published from a readback that already carried a transfer.
///
/// `reload_completed` closes a life that moved at least one round. A life that moved none closes silently: the node
/// list's `e-w-reload` is start and completion, so an interrupted reload is not a node and carries no fact.
///
/// Nothing here reads a second machine's state: a client reads its own reload and reports it (<see cref="ClientWeaponReport"/>), which
/// reaches this observer through <see cref="ReportedBy"/>; evidence/reload-facts.json.</summary>
internal sealed class ReloadObserver
{
    /// <summary>One open reload. The baseline is the magazine at the moment the life opened and moves forward with
    /// every reported advance, so a published amount is always what this readback added and never a total that was
    /// already reported.</summary>
    private struct Life
    {
        internal Life(EntityReference equipment, EntityReference actor, int baseline)
        { Equipment = equipment; Actor = actor; Baseline = baseline; Transferred = 0; Confirmed = false; }

        internal EntityReference Equipment { get; }
        internal EntityReference Actor { get; }
        internal int Baseline { get; set; }
        internal long Transferred { get; set; }
        /// <summary>Set once this machine has seen the item's own flag read true. A life opened by the reload entry
        /// point alone, with the flag never reading true, is a reload this machine did not actually watch and is
        /// closed without a fact.</summary>
        internal bool Confirmed { get; set; }
        /// <summary>A client's reload: its amount comes from that client's report, never from a readback of the host's copy.</summary>
        internal bool Reported { get; set; }
    }

    private readonly ReloadNativeReads _native;
    private readonly Func<EntityReference, bool> _isCurrent;
    private readonly Func<long> _world, _tick;
    private readonly Func<bool> _authoritative;
    private readonly EquipmentIdentitySession _identity;
    // WHY: P5 — each row's gate, taken once at construction, read before the event value is built.
    private readonly RuntimeSubscriptionGate _startedGate, _transferredGate, _completedGate;
    private readonly WeaponLog _log;
    private readonly Dictionary<string, Life> _open = new(StringComparer.Ordinal);
    private long _epoch = -1, _sequence;

    /// <summary>`isCurrent` is the equipment namespace's own resolver, the one the runtime and every plan already
    /// ask before it acts on a reference, so a life that is no longer current is refused a transfer here for the
    /// same reason it would be refused anywhere else.</summary>
    internal ReloadObserver(ReloadNativeReads native, Func<EntityReference, bool> isCurrent,
        Func<long> worldEpoch, Func<long> tick, Func<bool> authoritative,
        EquipmentIdentitySession identity, WeaponLog log)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _world = worldEpoch ?? throw new ArgumentNullException(nameof(worldEpoch));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _authoritative = authoritative ?? throw new ArgumentNullException(nameof(authoritative));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _startedGate = identity.GateOf(ReloadInventoryContract.ReloadStartedBinding);
        _transferredGate = identity.GateOf(ReloadInventoryContract.ReloadTransferredBinding);
        _completedGate = identity.GateOf(ReloadInventoryContract.ReloadCompletedBinding);
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The reload entry point ran with this item wielded. It opens a life only when the item's own flag
    /// reads true right after, so a reload the game refused produces nothing here; the refusal is `use_failed`'s,
    /// which reads the game's own gate instead.</summary>
    internal void Requested(object item)
    {
        if (!Enter(item, out var key)) return;
        if (!_native.IsReloading(item)) return;
        Confirm(key, item);
    }

    /// <summary>The item's own reload flag changed: the rising edge opens the life, the falling edge closes it. A
    /// falling edge with no open life is a flag that was already false, which is not a reload this machine watched
    /// and publishes nothing.</summary>
    internal void FlagChanged(object item, bool reloading)
    {
        if (!Enter(item, out var key)) return;
        if (reloading) { Confirm(key, item); return; }
        // The completion callback is the one body every observed reload reaches, so the magazine is read here before
        // the life closes: a transfer whose own readback hooks did not run is still measured against the baseline.
        if (_open.TryGetValue(key.Id, out var life) && _native.Clip(item) is { } clip) Advance(life, clip - life.Baseline);
        Close(key);
    }

    /// <summary>The magazine was read back after a body that could have moved rounds into it. A gain over the open
    /// life's baseline is the transfer; a readback outside an open life publishes nothing, because a magazine that
    /// changed with no reload under way is not a reload transfer.</summary>
    internal void MagazineRead(object item)
    {
        if (!Enter(item, out var key)) return;
        if (!_open.TryGetValue(key.Id, out var life)) return;
        var clip = _native.Clip(item);
        if (clip == null) return;
        Advance(life, clip.Value - life.Baseline);
    }

    /// <summary>A client's reload, admitted by <see cref="ClientWeaponReport"/>: its start opens the life, its end carries the amount that
    /// client's magazine gained and closes it, on the same rules as a reload this machine read itself.</summary>
    internal void ReportedBy(object item, bool started, int amount)
    {
        if (!Enter(item, out var key)) return;
        if (started)
        {
            Confirm(key, item);
            if (_open.TryGetValue(key.Id, out var opened)) { opened.Reported = true; _open[key.Id] = opened; }
            return;
        }
        if (_open.TryGetValue(key.Id, out var life)) Advance(life, amount);
        Close(key);
    }

    /// <summary>One life's advance. A non-positive amount is no movement and publishes nothing, which is what
    /// makes a repeated readback of an already reported change harmless.</summary>
    private void Advance(Life life, long amount)
    {
        if (amount <= 0 || !Live(life)) return;
        life.Transferred += amount;
        life.Baseline += checked((int)amount);
        _open[life.Equipment.Id] = life;
        if (!_transferredGate.HasSubscribers) return;
        var id = Id();
        var moved = checked((int)amount);
        if (Begin(life, _transferredGate) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerCombatReloadTransferred.OutActor, life.Actor);
        fact.Values.Entity(Blocks.TriggerCombatReloadTransferred.OutEquipment, life.Equipment);
        fact.Values.Integer(Blocks.TriggerCombatReloadTransferred.OutAmount, moved);
        Raise(life, _transferredGate, fact, id);
    }

    /// <summary>Opens the life, or confirms the one already open: the flag's setter and the inventory's entry point
    /// are one event, so the second to arrive must not open a second life. A life with no owner to name is not
    /// opened at all — every reload row's `actor` port is a required entity, and a fact whose actor this machine
    /// could not resolve would be a fact the runtime refuses rather than one worth publishing.</summary>
    private void Confirm(EntityReference equipment, object item)
    {
        if (_open.TryGetValue(equipment.Id, out var open)) { open.Confirmed = true; _open[equipment.Id] = open; return; }
        var baseline = _native.Clip(item);
        var actor = _native.OwnerOf(item);
        if (baseline == null || actor == null) return;
        var life = new Life(equipment, actor, baseline.Value) { Confirmed = true };
        _open[equipment.Id] = life;
        if (!_startedGate.HasSubscribers) return;
        var id = Id();
        if (Begin(life, _startedGate) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerCombatReloadStarted.OutActor, life.Actor);
        fact.Values.Entity(Blocks.TriggerCombatReloadStarted.OutEquipment, life.Equipment);
        Raise(life, _startedGate, fact, id);
    }

    /// <summary>Closes one life. A life this machine never saw the item's own flag confirm is dropped without a
    /// fact, and so is a life that moved no ammunition: `reload_completed` is the only ending this family
    /// publishes, and it is published only for a reload that really transferred something.</summary>
    private void Close(EntityReference equipment)
    {
        if (!_open.TryGetValue(equipment.Id, out var life)) return;
        _open.Remove(equipment.Id);
        if (!life.Confirmed || life.Transferred <= 0) return;
        if (!_completedGate.HasSubscribers) return;
        var id = Id();
        if (Begin(life, _completedGate) is not { } fact) return;
        fact.Values.Entity(Blocks.TriggerCombatReloadCompleted.OutActor, life.Actor);
        fact.Values.Entity(Blocks.TriggerCombatReloadCompleted.OutEquipment, life.Equipment);
        Raise(life, _completedGate, fact, id);
    }

    /// <summary>Every observation enters through here: the world epoch and the authority gate are read once, and a
    /// world change drops every open life without a fact. A world transition is a hard boundary, so a reload that
    /// spanned it has no ending this machine can date.</summary>
    private bool Enter(object item, out EntityReference key)
    {
        key = null!;
        SyncWorld();
        if (!Ready()) return false;
        var equipment = _native.EquipmentOf(item);
        if (equipment == null || equipment.WorldEpoch != _epoch) return false;
        key = equipment;
        return true;
    }

    /// <summary>The world epoch this observer's open lives belong to, and the drop a change causes. Called at the
    /// front of every observation, so no fact can be published under an epoch the life was not opened in.</summary>
    internal void SyncWorld()
    {
        var world = _authoritative() ? _world() : -1;
        if (world == _epoch) return;
        if (_epoch != -1 && _open.Count != 0) Cleared(world == -1 ? "not-authoritative" : "world-changed");
        _open.Clear();
        _epoch = world;
    }

    /// <summary>Drops every open life for a disposal. Nothing is published: the machine is going away.</summary>
    internal void Clear()
    {
        if (_open.Count != 0) Cleared("dispose");
        _open.Clear();
        _epoch = -1;
    }

    /// <summary>The open reload lives were dropped without a fact: info, because a world boundary is not a fault.</summary>
    private void Cleared(string reason)
    {
        if (!_log.Logs(RuntimeLogLevel.Info)) return;
        _log.Write(RuntimeLogLevel.Info, WeaponLogCodes.LivesCleared, new RuntimeLogFacts
        {
            Values = new[] { RuntimeLogValue.Of("table", "reload"), RuntimeLogValue.Of("world", _epoch),
                RuntimeLogValue.Of("count", _open.Count), RuntimeLogValue.Of("reason", reason) }
        });
    }

    /// <summary>A life whose equipment is no longer current is not live. The check is the identity table's own,
    /// which is the same answer the runtime gives a plan about that reference.</summary>
    private bool Live(Life life) => life.Equipment.WorldEpoch == _epoch && _isCurrent(life.Equipment);

    private string Id() => "gtfo.weapon.reload:" + Number(_epoch) + ":" + Number(checked(++_sequence));

    /// <summary>Begins one fact, or null when the identity session refuses it. Each caller updates the reload life
    /// and reads its cached gate before constructing the event.</summary>
    private EventWriter? Begin(Life life, RuntimeSubscriptionGate gate)
    {
        try { return _identity.Begin(gate, _epoch); }
        catch (RuntimeContractException error) { Refused(life, gate, error); return null; }
    }

    /// <summary>The fact is scoped to the equipment life it names.</summary>
    private void Raise(Life life, RuntimeSubscriptionGate gate, EventWriter fact, string id)
    {
        try { fact.Raise(id, _epoch, Tick(), EntityScope.Equipment.Of(life.Equipment)); }
        catch (RuntimeContractException error) { Refused(life, gate, error); }
    }

    /// <summary>A fact the identity session refuses never reaches the kernel, so the refusal is this package's own
    /// record; a fact the kernel queues or refuses is the kernel's (`trigger.fired` / `event.rejected`).</summary>
    private void Refused(Life life, RuntimeSubscriptionGate gate, RuntimeContractException error)
        => _log.Reason(WeaponLog.RefusalLevel(error.Code), WeaponLogCodes.FactRejected, error.Code, life.Equipment.Id, gate.BindingId);

    private bool Ready() => _epoch != -1;

    private long Tick() => Math.Max(0, _tick());

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------------------------------------------------------------
