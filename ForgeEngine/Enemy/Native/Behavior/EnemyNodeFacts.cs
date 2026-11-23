using System;
using System.Collections.Generic;
using System.Globalization;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The module-side half of the node-list observations this package adds: the generic spawn fact of an
/// enemy, the game's own tag transaction, the moment foam lands on an enemy, and the tag's falling edge. Each
/// one publishes exactly one row and writes nothing; the fields and the publication rule live here, next to the
/// one registration that declares the binding, and the Harmony classes that call into them are in
/// `EnemyNodeHooks.cs`.
///
/// The tag is the one fact with two edges and one native entry: `ToolSyncManager.DoTagEnemy` runs on the host
/// when a scanner client asked for a tag, so the rising edge is the transaction's own; nothing in the build
/// reports the tag ending, so the falling edge is a comparison of `EnemyAgent.IsTagged` against the last state
/// this module published. Only the lives that hold a tag are compared, once per kernel tick
/// (<see cref="ObserveHoldersInFrame"/>), and only a transition is published.
///
/// The marker ledger is here for the same reason: a Forge mark is a local presentation object with a lifetime
/// the plan asked for, and the frame pump that already runs for the tag is what expires it.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The glue volume one native accumulation call is about to change, captured before the call so a
    /// postfix can report what it really added. The token is spent by the first postfix that closes it, and a
    /// postfix of another receiver never closes it.</summary>
    internal sealed class GlueObservation
    {
        private readonly EnemyModule _owner;
        private bool _consumed;
        internal EntityReference Target { get; }
        internal IntPtr ReceiverPointer { get; }
        internal float VolumeBefore { get; }
        /// <summary>Whether the enemy was asleep when this glue landed: its movement machine stood in the native
        /// `ES_StateEnum.Hibernate` state as the call began, which is the state the game's own stuck state is
        /// entered from when it records a hibernate origin.</summary>
        internal bool WasHibernating { get; }
        internal GlueObservation(EnemyModule owner, EntityReference target, IntPtr receiver, float volumeBefore,
            bool wasHibernating)
        {
            _owner = owner; Target = target; ReceiverPointer = receiver; VolumeBefore = volumeBefore;
            WasHibernating = wasHibernating;
        }
        internal bool TryConsume(EnemyModule owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true;
            return true;
        }
    }

    /// <summary>One Forge-built navigation marker, and the enemy life it tracks: the row's identity, the instance
    /// the mark was placed on, and the tick the marker ends at. The marker itself is a native object held by the
    /// batch the kernel keeps in the `marker` handle's native slot, so this record is what cancelling and expiring
    /// it need and nothing more.</summary>
    internal sealed record MarkerRecord(EntityReference Target, IntPtr EnemyPointer, ForgeUI.NavMarkerHandle Marker,
        long ExpiresAtTick);

    /// <summary>Every marker one dispatch placed, which is what that dispatch's `marker` handle stands for. The
    /// kernel holds the batch in the handle's native slot for the handle's declared lifetime and asks
    /// <see cref="AnyLive"/> before it releases the slot, so a handle whose markers are all gone stops naming
    /// anything without this half keeping a second handle-to-object table.</summary>
    internal sealed class MarkerBatch
    {
        internal readonly List<MarkerRecord> Records = new();

        /// <summary>Whether this batch still stands for a mark: its records are the markers the dispatch placed
        /// and none has been removed — by expiry, by cancel, or by the enemy life ending. A batch that lost its last
        /// record names nothing.</summary>
        internal bool AnyLive() => Records.Count != 0;
    }

    /// <summary>Every marker batch this provider has placed and not yet removed, which is the per-frame expiry
    /// pump's own ledger: the handle bindings themselves live in the kernel's slots.</summary>
    internal readonly List<MarkerBatch> _batches = new();

    /// <summary>The native object one live `marker` handle of this provider names, or null when the handle is not
    /// live of this provider or names no batch.</summary>
    private MarkerBatch? BatchOf(RuntimeHandle handle)
        => _registration.TryNative(handle, out var native) && native is MarkerBatch batch ? batch : null;

    /// <summary>The enemy-spawn fact, published from the enemy's own spawn hook. The
    /// reference is the one this module already tracks — the spawn registration runs first and this postfix is
    /// last — and the position is the instance's own, read once and read back. An instance this module cannot
    /// resolve publishes nothing rather than minting an identity of its own.
    ///
    /// The spawn mode the game handed the hook is recorded on the life before anything is published: it is how the
    /// enemy entered the level, the game does not publish it anywhere else, and the `spawn_origin` port of
    /// `forge.query.enemy.type` reads it back from the same entry.</summary>
    internal void AfterSpawn(EnemyAgent enemy, Agents.AgentMode mode)
    {
        CheckThread();
        if (enemy == null) return;
        // The identity is registered whatever the publication gate says: a plan that never subscribes to the
        // spawn fact still dispatches at the reference this path minted.
        EntityReference reference;
        try { reference = TrackSpawn(enemy); }
        catch (Exception error) { NativeFault.Note("enemy.node.spawn-track", error); return; }
        if (_entities.TryGetValue(enemy.GlobalID, out var entry) && entry.EnemyPointer == enemy.Pointer)
            RecordSpawnMode(entry.Reference, mode);
        if (!CanPublishNodeFacts(_spawnedGate) || Resolve(reference) == null) return;
        // Every port but the identity is optional and a reading that fails is an absent port, never a made-up
        // value: a position that does not read is not a zero vector, and a type block that does not read is not
        // type 0.
        var position = enemy.Position;
        var type = Resolve(reference)?.Type;
        if (!TryBeginNodeFact(_spawnedGate, reference, out var fact)) return;
        var values = fact.Values;
        values.Entity(Blocks.TriggerEnemySpawned.OutEnemy, reference);
        if (IsFinitePoint(position)) values.Vector(Blocks.TriggerEnemySpawned.OutPosition, position.x, position.y, position.z);
        // `spawn_mode` is the `agent_mode` member, which is `Agents.AgentMode`'s own order (off, aggressive,
        // patrolling, scout, hibernate); a mode outside it is an absent port.
        if ((int)mode is >= 0 and <= 4) values.Enum(Blocks.TriggerEnemySpawned.OutSpawnMode, (int)mode);
        // `enemy_type` is the official type the life was built from (`EnemyAgent.EnemyData.persistentID`), in the
        // decimal spelling `forge.query.enemy.type` answers and the `enemy-type` mount matches.
        if (type is uint typeId) values.Text(Blocks.TriggerEnemySpawned.OutEnemyType, typeId.ToString(CultureInfo.InvariantCulture));
        RaiseNodeFact(fact);
    }

    /// <summary>The tag transaction's rising edge, read from the packet the host's own action carried. The
    /// packet holds one enemy handle and nothing else, so the only reading it can produce is "this enemy is
    /// tagged"; a packet whose agent was torn down between the action and this postfix resolves to nothing and
    /// publishes nothing.</summary>
    internal void AfterTagged(ToolSyncManager.pTagEnemy data)
    {
        CheckThread();
        if (!CanPublishNodeFacts(_taggedGate)) return;
        // The packet holds a `pEnemyAgent` handle, not an agent: it is asked for the instance it names, and a
        // handle whose agent was torn down between the action and this postfix answers false and publishes
        // nothing.
        EnemyAgent? enemy;
        try { if (!data.enemy.TryGet(out enemy) || enemy == null) return; }
        catch (Exception error) { NativeFault.Note("enemy.node.tag-target", error); return; }
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer || Resolve(entry.Reference) == null) return;
        entry.Tagged = true;
        SetHolder(entry);
        PublishTagged(entry, true);
    }

    private void PublishTagged(Entry entry, bool tagged)
    {
        if (!TryBeginNodeFact(_taggedGate, entry.Reference, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyTagged.OutEnemy, entry.Reference);
        fact.Values.Boolean(Blocks.TriggerEnemyTagged.OutTagged, tagged);
        RaiseNodeFact(fact);
    }

    /// <summary>The tag's falling edge: the tag's native timer runs out on the game's side and nothing reports it, so
    /// the lives holding a tag are looked at once per kernel tick (<see cref="ObserveHoldersInFrame"/>).
    /// The state is compared against what this module last published, so one tag produces one rising and one
    /// falling fact — a re-tag after a lapse is a new rising edge, which is what the game's own timer means.</summary>
    private void ObserveTagState(EnemyAgent enemy, Entry entry)
    {
        CheckThread();
        if (!CanPublishNodeFacts(_taggedGate)) return;
        if (entry.EnemyPointer != enemy.Pointer || !IsLive(entry)) return;
        bool tagged;
        try { tagged = enemy.IsTagged; }
        catch (Exception error) { NativeFault.Note("enemy.node.tag-state", error); return; }
        if (tagged == entry.Tagged) return;
        entry.Tagged = tagged;
        PublishTagged(entry, tagged);
    }

    /// <summary>The marker ledger's clock: every marker whose lifetime has run out is removed from the layer. A batch
    /// that lost its last record is dropped with its handle's own answer to "is anything still there" turning false on
    /// the next sweep. The markers of an enemy life that ended are dropped when the life ends
    /// (<see cref="ForgetMarksOf"/>), not found by looking at every marker every tick. This runs once per kernel tick
    /// from the module's lifecycle observer and does nothing when no marker is live; the lifetime is a deadline in kernel
    /// ticks, so it is the one part of the ledger that needs the clock.</summary>
    internal void ExpireMarks()
    {
        CheckThread();
        if (_batches.Count == 0) return;
        for (int batchIndex = _batches.Count - 1; batchIndex >= 0; batchIndex--)
        {
            var batch = _batches[batchIndex];
            for (int index = batch.Records.Count - 1; index >= 0; index--)
            {
                var record = batch.Records[index];
                if (_kernel.CurrentTick < record.ExpiresAtTick) continue;
                batch.Records.RemoveAt(index);
                RemoveMarker(record.Marker);
            }
            if (batch.Records.Count == 0) _batches.RemoveAt(batchIndex);
        }
    }

    /// <summary>An enemy life ended: every marker placed on it goes, and a batch that lost its last record with it.</summary>
    private void ForgetMarksOf(EntityReference target)
    {
        if (_batches.Count == 0) return;
        for (int batchIndex = _batches.Count - 1; batchIndex >= 0; batchIndex--)
        {
            var batch = _batches[batchIndex];
            for (int index = batch.Records.Count - 1; index >= 0; index--)
            {
                if (batch.Records[index].Target != target) continue;
                var record = batch.Records[index];
                batch.Records.RemoveAt(index);
                RemoveMarker(record.Marker);
            }
            if (batch.Records.Count == 0) _batches.RemoveAt(batchIndex);
        }
    }
    /// <summary>Reads the glue volume a call is about to change, so the postfix can report what it added. The
    /// reading is the enemy's own receiver, resolved through this module's table: a receiver this module cannot
    /// name is not a fact about an enemy it tracks. With no consumer the window never opens; an observed call
    /// still captures its original volume and movement state before the native body changes either.</summary>
    internal GlueObservation? BeforeGlue(Dam_EnemyDamageBase? damage)
    {
        CheckThread();
        if (!_gluedGate.HasSubscribers) return null;
        if (damage == null || damage.Owner == null) return null;
        if (!_entities.TryGetValue(damage.Owner.GlobalID, out var entry)
            || entry.EnemyPointer != damage.Owner.Pointer || Resolve(entry.Reference) == null
            || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer) return null;
        float volume;
        bool hibernating;
        try
        {
            volume = damage.AttachedGlueVolume;
            // Read before the call, like the volume: the stuck state's own packet outlives the gluing that set it,
            // so only the movement state this call starts from says whether this glue is the one that met a
            // sleeping enemy. A life whose movement machine cannot be read is not observed at all.
            var locomotion = entry.Enemy.AI?.m_locomotion;
            if (locomotion == null) return null;
            hibernating = (int)locomotion.CurrentStateEnum == NativeHibernateState;
        }
        catch (Exception error) { NativeFault.Note("enemy.node.glue-before", error); return null; }
        return float.IsFinite(volume) && volume >= 0f
            ? new GlueObservation(this, entry.Reference, damage.Pointer, volume, hibernating) : null;
    }

    /// <summary>Native `ES_StateEnum.Hibernate` (14): the movement state a sleeping enemy stands in.</summary>
    private const int NativeHibernateState = 14;

    /// <summary>The glue the call really added: the difference between the volume the postfix reads and the one
    /// the prefix captured. A call that changed nothing publishes nothing — the game's own accumulation entry can
    /// be entered with a volume that a receiver's own rules reduce to zero, and that is not glue landing on an
    /// enemy. `total` is the volume the receiver now carries, so the two ports describe one reading.</summary>
    internal void AfterGlue(Dam_EnemyDamageBase? damage, GlueObservation? before)
    {
        CheckThread();
        // The receiver is judged before the token is spent: a postfix that belongs to another call must not
        // close this observation, because the call it was opened for has not returned yet.
        if (before == null || damage == null || damage.Pointer != before.ReceiverPointer) return;
        if (!before.TryConsume(this) || !CanPublishNodeFacts(_gluedGate)) return;
        var entry = Resolve(before.Target);
        if (entry == null || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer) return;
        float total;
        try { total = damage.AttachedGlueVolume; }
        catch (Exception error) { NativeFault.Note("enemy.node.glue-after", error); return; }
        if (!float.IsFinite(total) || total < 0f) return;
        double added = total - before.VolumeBefore;
        if (added <= 0) return;
        if (!TryBeginNodeFact(_gluedGate, before.Target, out var fact)) return;
        fact.Values.Entity(Blocks.TriggerEnemyGlued.OutEnemy, before.Target);
        fact.Values.Number(Blocks.TriggerEnemyGlued.OutVolume, added);
        fact.Values.Number(Blocks.TriggerEnemyGlued.OutTotal, total);
        fact.Values.Boolean(Blocks.TriggerEnemyGlued.OutFromHibernate, before.WasHibernating);
        RaiseNodeFact(fact);
    }

    /// <summary>Removes every marker one dispatch placed, which is the `marker` handle's own cancel. The native
    /// removal is called once per live marker, and the batch goes whether or not the call answered. A batch that
    /// was already removed is left alone, so cancelling a spent handle removes nothing a second time.</summary>
    internal void RemoveMarked(MarkerBatch batch)
    {
        if (!_batches.Remove(batch)) return;
        foreach (var record in batch.Records) RemoveMarker(record.Marker);
        batch.Records.Clear();
    }

    /// <summary>The same removal addressed by the handle a caller holds: the batch the kernel's native slot names
    /// is resolved first, and a handle that is not this provider's or holds no batch removes nothing. This is the
    /// entry a cancel outside a plan takes, and the one the focused suite drives directly.</summary>
    internal void RemoveMarked(RuntimeHandle handle)
    {
        if (BatchOf(handle) is { } batch) RemoveMarked(batch);
    }

    /// <summary>Drops every marker this provider placed, which is what a package unloading mid-level needs: no
    /// marker is left behind that nothing can name. The batches go whole, so the handles that named them answer
    /// nothing on the next sweep.</summary>
    private void RemoveEveryMarker()
    {
        foreach (var batch in _batches)
        {
            foreach (var record in batch.Records) RemoveMarker(record.Marker);
            batch.Records.Clear();
        }
        _batches.Clear();
    }

    /// <summary>The one native removal. A layer that is gone, or a marker the game already collected, is not an
    /// error worth escaping into a native callback; the ledger entry is dropped either way.</summary>
    private static void RemoveMarker(ForgeUI.NavMarkerHandle? marker)
    {
        if (marker == null) return;
        try { marker.Remove(); }
        catch (Exception error) { NativeFault.Note("enemy.node.marker-remove", error); }
    }

    /// <summary>PERF(P5): the kernel gates of the four node facts this file and `EnemyAbilityUsedFacts` publish,
    /// resolved once at attach and refreshed by the kernel whenever it rebuilds subscriptions.</summary>
    private EnemyGate _spawnedGate = null!, _taggedGate = null!, _gluedGate = null!, _abilityUsedGate = null!;

    /// <summary>Resolved once, right after the registration exists; `SubscriptionGate` refuses a binding this
    /// provider does not own.</summary>
    private void CreateNodeGates()
    {
        _spawnedGate = Gate(EnemyNodeTriggerContract.SpawnedBinding);
        _taggedGate = Gate(NodeTaggedBinding);
        _gluedGate = Gate(NodeGluedBinding);
        _abilityUsedGate = Gate(AbilityUsedBinding);
        _deathStartedGate = Gate(DeathStartedBinding);
        _limbBrokenGate = Gate(LimbBrokenBinding);
        Log.Write(RuntimeLogLevel.Info, "enemy.health-observation-coverage-incomplete", new RuntimeLogFacts
        {
            Binding = HealthChangedBinding,
            Detail = "Health facts cover observed native damage windows; direct health writes and healing bypasses produce no fact; the next window that finds a different health logs enemy.health-bypass-observed once per life."
        });
        Log.Write(RuntimeLogLevel.Info, "enemy.ability-observation-coverage-incomplete", new RuntimeLogFacts
        {
            Binding = AbilityUsedBinding,
            Detail = "A native ability without a reviewed Forge resource cannot produce a typed ability fact; Forge action tracking does not prove complete vanilla coverage."
        });
    }

    /// <summary>Whether a plan consumes the tag's rising or falling fact that the behaviour pump samples.</summary>
    internal bool TagStateObserved => _taggedGate.HasSubscribers;
    /// <summary>Whether a plan consumes `enemy.ability_used`, read by its attack-start hook before it builds anything.</summary>
    internal bool AbilityUsedObserved => _abilityUsedGate.HasSubscribers;
    internal bool GlueObserved => _gluedGate.HasSubscribers;

    /// <summary>Whether a plan subscribes to one node fact, on this module's own authority gate. The gate is the
    /// one every other host-owned fact in this package passes, so a peer that may not publish the damage facts
    /// may not publish these either. The subscription is read first: it is a field, the authority is not.</summary>
    private bool CanPublishNodeFacts(EnemyGate gate)
        => gate.HasSubscribers && _kernel.StartupState == RuntimeStartupState.Ready && CanExecute;

    /// <summary>Begins one node fact written by output position, when its subject still resolves. The caller has
    /// asked <see cref="CanPublishNodeFacts"/> first, so the gate has subscribers; writing everything the fact
    /// carries and then <see cref="RaiseNodeFact"/> is the only thing left to do with the writer.</summary>
    private bool TryBeginNodeFact(EnemyGate gate, EntityReference subject, out EventWriter fact)
    {
        fact = default;
        if (Resolve(subject) == null) return false;
        fact = gate.Begin(_registration);
        return true;
    }

    /// <summary>Publishes one node fact. A refusal is the kernel's to record (`event.rejected` under this binding),
    /// and it never reopens this native observation. A fact about a projectile, which has no Forge entity identity,
    /// begins its writer from the gate directly: the projectile rows declare no trigger subject (the shooter is only
    /// a related port), so each event is its own play.</summary>
    private void RaiseNodeFact(EventWriter fact)
        => fact.Raise("gtfo.enemy.node:" + _kernel.WorldEpoch + ":" + checked(++_eventSequence),
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch);

    private static bool IsFinitePoint(UnityEngine.Vector3 point)
        => float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
}
