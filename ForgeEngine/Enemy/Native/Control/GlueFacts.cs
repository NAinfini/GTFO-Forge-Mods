using System;
using System.Collections.Generic;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>The native facts one enemy life resolves to, as the foam ledger needs them: the agent itself, the
/// instance it was when the foam was applied, and its glue receiver. The provider hands these in from its own
/// entity table, so this family never keeps a second reference registry of its own.</summary>
/// <param name="Enemy">The native agent this life currently resolves to.</param>
/// <param name="EnemyPointer">The native instance pointer of that agent.</param>
/// <param name="Damage">The agent's own glue receiver, or null when the agent holds none or it is not set up.</param>
internal readonly record struct GlueTarget(EnemyAgent Enemy, IntPtr EnemyPointer, Dam_EnemyDamageBase? Damage);

/// <summary>One foam application this provider made, as the ledger remembers it. Applications never merge: the
/// effect handle, expiry and exact receiver belong to the one native increment that this record observed.</summary>
/// <param name="Target">The enemy life the foam was applied to.</param>
/// <param name="EnemyPointer">The native instance that life resolved to when the foam was applied.</param>
/// <param name="ReceiverPointer">The native receiver that accepted the increment.</param>
/// <param name="Volume">The volume this application added, which is exactly the volume a removal takes back.</param>
/// <param name="Handle">The `foam` handle this application is cleared through.</param>
/// <param name="ExpiresAtTick">The tick the foam is taken back at, or null when it lasts until the encounter ends.</param>
internal sealed class GlueRecord
{
    internal GlueRecord(EntityReference target, IntPtr enemyPointer, IntPtr receiverPointer, double volume,
        RuntimeHandle? handle, long? expiresAtTick)
    {
        Target = target;
        EnemyPointer = enemyPointer;
        ReceiverPointer = receiverPointer;
        Volume = volume;
        Handle = handle;
        ExpiresAtTick = expiresAtTick;
    }

    internal EntityReference Target { get; }
    internal IntPtr EnemyPointer { get; }
    internal IntPtr ReceiverPointer { get; }
    internal double Volume { get; }
    internal RuntimeHandle? Handle { get; }
    internal long? ExpiresAtTick { get; }
    internal bool CleanupRequested { get; set; }
    internal float? UnknownWriteBefore { get; set; }
    internal long LastAttemptTick { get; set; } = long.MinValue;
}

/// <summary>A foam spawn the entry point accepted whose volume had not moved when the command read it back. The game
/// applies the spawn on a later frame (or when its broadcast arrives), so the ledger reads the receiver again on the
/// following ticks and, if the volume rose, files the increment as an ordinary record; the command's own row stays
/// the unknown commit it reported at the time.</summary>
internal sealed class PendingGlue
{
    internal PendingGlue(EntityReference target, IntPtr enemyPointer, IntPtr receiverPointer, float before,
        RuntimeHandle? handle, long? expiresAtTick, long startTick)
    {
        Target = target; EnemyPointer = enemyPointer; ReceiverPointer = receiverPointer; Before = before;
        Handle = handle; ExpiresAtTick = expiresAtTick; StartTick = startTick;
    }
    internal EntityReference Target { get; }
    internal IntPtr EnemyPointer { get; }
    internal IntPtr ReceiverPointer { get; }
    /// <summary>The receiver's volume this spawn is measured from. Another spawn on the same receiver that landed first
    /// moves it up by what that one claimed, so one increment is never claimed twice.</summary>
    internal float Before { get; set; }
    internal RuntimeHandle? Handle { get; }
    internal long? ExpiresAtTick { get; }
    internal long StartTick { get; }
    /// <summary>The handle this spawn was filed under was cancelled before the volume appeared: the increment is
    /// taken back off as soon as it lands.</summary>
    internal bool Cancelled { get; set; }
}

/// <summary>
/// The foam family's own state: the foam this provider applied, and the one place a Forge-side duration is
/// honoured.
///
/// The game's glue has no timer of its own — it is added by volume and taken back by volume — so an application
/// with a duration is remembered here and taken back when the duration runs out. Every record names the exact enemy
/// life and receiver that accepted it. Explicit replacement, despawn and world-end paths retire that record; a
/// transient resolution failure retains it for retry rather than following a pointer into another life.
///
/// The tick comes from the kernel's own lifecycle observation, which is the framework's existing "the simulation
/// advanced" notification. Expiry publishes no fact: the rows this action answers are the command's own result, and
/// an internal cleanup with no plan-facing meaning does not get a binding invented for it.
///
/// Resolution is handed in rather than owned: what an `gtfo.enemy` reference currently names is the enemy
/// provider's own answer, and this ledger only ever asks it again — never a stale pointer it kept.
/// </summary>
internal sealed class GlueLedger : IDisposable
{
    /// <summary>The most foam applications this provider keeps at once. A plan that asks for one more is refused by
    /// name rather than silently dropping the oldest.</summary>
    internal const int MaximumRecords = 4096;

    private readonly IForgeEngine _kernel;
    private static readonly InvalidOperationException ReceiverUnavailable = new("Glue receiver is unavailable.");
    private static readonly InvalidOperationException ReadbackUnavailable = new("Glue readback is unavailable.");
    private static readonly InvalidOperationException RemovalUnconfirmed = new("Glue removal was not confirmed.");
    private static readonly InvalidOperationException RemovalInterference = new("Glue changed after an unknown removal.");

    private readonly Func<EntityReference, GlueTarget?> _resolveCleanup;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly List<GlueRecord> _records = new();
    private readonly List<PendingGlue> _pending = new();
    private Exception? _lastFailure;
    private bool _disposed;

    internal GlueLedger(IForgeEngine kernel, RuntimeModuleHandle owner,
        Func<EntityReference, GlueTarget?> resolveCleanup)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _resolveCleanup = resolveCleanup ?? throw new ArgumentNullException(nameof(resolveCleanup));
        ArgumentNullException.ThrowIfNull(owner);
        _lifecycle = owner.ObserveLifecycle(OnLifecycle);
    }

    internal int Count => _records.Count;

    /// <summary>Whether this enemy life already carries foam this provider applied.</summary>
    internal bool IsFoamed(EntityReference target)
    {
        for (int index = 0; index < _records.Count; index++)
            if (_records[index].Target == target) return true;
        return false;
    }

    /// <summary>Whether this provider may add another application right now.</summary>
    internal bool HasRoom => _records.Count < MaximumRecords;

    internal void Add(GlueRecord record) => _records.Add(record);

    /// <summary>How many ticks after the spawn the receiver is still read for the volume to appear.</summary>
    internal const int PendingWindowTicks = 120;

    /// <summary>Remembers a spawn whose volume was not yet visible; see <see cref="PendingGlue"/>.</summary>
    internal void AddPending(PendingGlue pending)
    {
        if (_pending.Count < MaximumRecords) _pending.Add(pending);
    }

    private void ResolvePending()
    {
        long tick = _kernel.CurrentTick;
        // Oldest first: the spawn that was asked first claims the increment first.
        for (int index = 0; index < _pending.Count;)
        {
            var pending = _pending[index];
            if (pending.Target.WorldEpoch != _kernel.WorldEpoch || tick - pending.StartTick > PendingWindowTicks)
            { _pending.RemoveAt(index); continue; }
            if (tick == pending.StartTick) { index++; continue; }
            GlueTarget? target;
            try { target = _resolveCleanup(pending.Target); }
            catch (Exception error) { EnemyLog.Contained(error); index++; continue; }
            if (target == null) { index++; continue; }
            var damage = target.Value.Damage;
            if (target.Value.EnemyPointer != pending.EnemyPointer || damage == null || damage.Pointer != pending.ReceiverPointer)
            { _pending.RemoveAt(index); continue; }
            float? now = GlueActions.ReadAttachedGlueVolume(damage);
            if (!now.HasValue || now.Value <= pending.Before) { index++; continue; }
            _pending.RemoveAt(index);
            float claimed = now.Value - pending.Before;
            // The same receiver's other waiting spawns measure from above what this one took.
            foreach (var other in _pending)
                if (other.ReceiverPointer == pending.ReceiverPointer && other.EnemyPointer == pending.EnemyPointer) other.Before += claimed;
            if (HasRoom)
                _records.Add(new GlueRecord(pending.Target, pending.EnemyPointer, pending.ReceiverPointer,
                    claimed, pending.Handle, pending.ExpiresAtTick)
                { CleanupRequested = pending.Cancelled });
        }
    }

    /// <summary>Marks a handle's applications once, then tries their cleanup. Repeated public cancellation only
    /// observes the pending records; failed native work is retried by the lifecycle tick.</summary>
    internal bool RequestByHandle(RuntimeHandle handle)
    {
        bool requested = false;
        // A spawn still waiting to land under this handle is cancelled with it: it is taken back off the moment it appears.
        bool waiting = false;
        foreach (var pending in _pending)
            if (pending.Handle == handle) { pending.Cancelled = true; waiting = true; }
        for (int index = 0; index < _records.Count; index++)
        {
            if (_records[index].Handle != handle || _records[index].CleanupRequested) continue;
            _records[index].CleanupRequested = true;
            requested = true;
        }
        if (!requested) return !waiting;
        PumpCleanup();
        for (int index = 0; index < _records.Count; index++)
            if (_records[index].Handle == handle && _records[index].CleanupRequested) return false;
        return !waiting;
    }

    /// <summary>An explicit despawn or replacement proves the old life has ended. Its native receiver must not be
    /// called again, and the record cannot follow the pointer into the replacement life.</summary>
    internal void ForgetLife(EntityReference target)
    {
        for (int index = _records.Count - 1; index >= 0; index--)
            if (_records[index].Target == target) _records.RemoveAt(index);
        _pending.RemoveAll(pending => pending.Target == target);
    }

    /// <summary>A host migration moved the world under the foam this ledger applied. The foam is on the enemies the game
    /// recalled, so each application follows its enemy to the life the module tracked it under again and its handle to
    /// the new world's spelling; one whose enemy was not recalled has nothing left to take back and ends.</summary>
    internal void Follow(Func<IntPtr, EntityReference?> lifeOf, Func<RuntimeHandle, RuntimeHandle> handleOf)
    {
        for (int index = _records.Count - 1; index >= 0; index--)
        {
            var old = _records[index];
            if (lifeOf(old.EnemyPointer) is not { } life) { _records.RemoveAt(index); continue; }
            _records[index] = new GlueRecord(life, old.EnemyPointer, old.ReceiverPointer, old.Volume, old.Handle is { } held ? handleOf(held) : null, old.ExpiresAtTick)
            { CleanupRequested = old.CleanupRequested, UnknownWriteBefore = old.UnknownWriteBefore, LastAttemptTick = old.LastAttemptTick };
        }
        for (int index = _pending.Count - 1; index >= 0; index--)
        {
            var old = _pending[index];
            if (lifeOf(old.EnemyPointer) is not { } life) { _pending.RemoveAt(index); continue; }
            _pending[index] = new PendingGlue(life, old.EnemyPointer, old.ReceiverPointer, old.Before, old.Handle is { } held ? handleOf(held) : null,
                old.ExpiresAtTick, old.StartTick)
            { Cancelled = old.Cancelled };
        }
    }

    /// <summary>
    /// Ends every application whose duration has run out, through the receiver's own removal entry point and by the
    /// same volume it added. A cleanup that cannot observe its receiver stays pending and is reported through the
    /// native-fault channel; only an explicit life or world end releases it without another write.
    /// </summary>
    internal void Expire()
    {
        if (_disposed || _records.Count == 0) return;
        long tick = _kernel.CurrentTick;
        for (int index = 0; index < _records.Count; index++)
        {
            var record = _records[index];
            if (record.ExpiresAtTick is not { } expires || expires > tick) continue;
            record.CleanupRequested = true;
        }
        PumpCleanup();
    }

    /// <summary>Retries only records already due for cleanup. A write whose return or readback was unknown is first
    /// observed on a later tick: an observed completed decrement closes the record, an unchanged value permits one
    /// retry, and any other change stays unknown so another source is never decremented blindly.</summary>
    private void PumpCleanup(bool retrySameTick = false)
    {
        if (_records.Count == 0) return;
        long tick = _kernel.CurrentTick;
        for (int index = _records.Count - 1; index >= 0; index--)
        {
            var record = _records[index];
            if (!record.CleanupRequested || (!retrySameTick && record.LastAttemptTick == tick)) continue;
            record.LastAttemptTick = tick;
            if (TryRemove(record)) _records.RemoveAt(index);
        }
    }

    private bool TryRemove(GlueRecord record)
    {
        if (record.Target.WorldEpoch != _kernel.WorldEpoch
            || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return true;
        GlueTarget? target;
        try { target = _resolveCleanup(record.Target); }
        catch (Exception error) { return Failed(error); }
        // A different world proves the old life ended. A null lookup alone does not: a native identity getter can
        // fail transiently, so that record stays pending until an explicit life/world end or an exact receiver.
        if (target == null) return Failed(ReceiverUnavailable);
        var damage = target.Value.Damage;
        try
        {
            if (target.Value.EnemyPointer != record.EnemyPointer || !target.Value.Enemy.Alive) return true;
            if (damage == null || !damage.IsSetup || damage.Pointer == IntPtr.Zero
                || damage.Pointer != record.ReceiverPointer || damage.Owner == null
                || damage.Owner.Pointer != record.EnemyPointer) return Failed(ReceiverUnavailable);
        }
        catch (Exception error) { return Failed(error); }

        if (record.UnknownWriteBefore is { } unknownBefore)
        {
            float? observed = GlueActions.ReadAttachedGlueVolume(damage);
            if (!observed.HasValue) return Failed(ReadbackUnavailable);
            float expected = GlueActions.ExpectedAfterRemoval(unknownBefore, record.Volume);
            if (observed.Value <= expected) return true;
            if (observed.Value != unknownBefore) return Failed(RemovalInterference);
            record.UnknownWriteBefore = null;
        }

        float? before = GlueActions.ReadAttachedGlueVolume(damage);
        if (!before.HasValue) return Failed(ReadbackUnavailable);
        record.UnknownWriteBefore = before.Value;
        try
        {
            if (GlueActions.Remove(damage, record.Volume, before.Value)) return true;
        }
        catch (Exception error) { return Failed(error); }
        return Failed(RemovalUnconfirmed);
    }

    private bool Failed(Exception error)
    {
        _lastFailure = error;
        NativeFault.Note("enemy.glue.remove", error);
        return false;
    }

    private void RequestAll(bool retrySameTick)
    {
        for (int index = 0; index < _records.Count; index++) _records[index].CleanupRequested = true;
        PumpCleanup(retrySameTick);
    }

    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.EnemyGlueLifecycle);
        if (_disposed) return;
        // A migration keeps the foam: the module's own follow moves it to the new world (<see cref="Follow"/>).
        if (value.Kind == RuntimeLifecycleKind.WorldChanged && !value.IsMigration
            || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
        { _records.Clear(); _pending.Clear(); return; }
        if (value.Kind != RuntimeLifecycleKind.TickAdvanced) return;
        if (_pending.Count != 0) ResolvePending();
        Expire();
    }

    public void Dispose() => Dispose(false);

    internal void Dispose(bool retrySameTick)
    {
        if (_disposed) return;
        _lastFailure = null;
        // A spawn that landed since the last tick is filed first, so the removal below covers it.
        if (_pending.Count != 0) ResolvePending();
        _pending.Clear();
        RequestAll(retrySameTick);
        if (_records.Count != 0)
            throw new InvalidOperationException("Enemy glue cleanup is incomplete: "
                + (_lastFailure?.Message ?? "native removal unconfirmed"));
        _disposed = true;
        _lifecycle.Dispose();
    }
}
