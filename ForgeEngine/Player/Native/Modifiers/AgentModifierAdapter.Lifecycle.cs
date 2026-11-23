using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeEngine.Framework;

namespace ForgePlayer.Native;

internal sealed partial class AgentModifierAdapter
{
    /// <summary>The world-change half. A world change clears everything the previous world's ledger still held and
    /// a failed or stopped runtime retries cleanup; what belongs to a life that ended is released when the identity
    /// announces it (<see cref="OnEntityLost"/>). Timed endings are not here: they are the kernel's effect lifecycle, which
    /// calls <see cref="RestoreApply"/>. The kernel forbids a lifecycle observer from
    /// mutating it, so nothing here calls a kernel write path; the native clear and the provider's own ledger are
    /// not kernel state.</summary>
    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind == RuntimeLifecycleKind.TickAdvanced)
        {
            if (_pending.Count != 0) DrainPending(value.Current.SimulationTick);
            return;
        }
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.PlayerModifierLifecycle);
        try
        {
            if (value.Kind == RuntimeLifecycleKind.WorldChanged) { BeginWorld(); return; }
            if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            { EndWorld(); return; }
        }
        catch (Exception error)
        {
            // A throwing observer is removed by the kernel and latched as its own fault, after which no later world
            // would retry the cleanup. The failure is reported and swallowed here instead: the unconfirmed ids stay
            // in the ledger and the next world change or end retries them.
            try { _report("player.attribute-modifier-sweep-failed: " + error.GetType().Name); }
            catch (Exception reporting)
            { throw new AggregateException("Player modifier observation and diagnostic reporting failed.", error, reporting); }
        }
    }

    /// <summary>Releases every modification written on one life, which is what a despawn and a death that ended
    /// the life reduce to. The identity announces each ended life once; no tick looks for lost lives. An id whose
    /// clear was not confirmed stays in the ledger for the world-end pass and is reported once.</summary>
    internal void OnEntityLost(EntityReference entity)
    {
        CheckThread();
        if (_entries.Count == 0) return;
        _due.Clear();
        foreach (var entry in _entries.Values) if (entry.Target == entity) _due.Add(entry.Id);
        if (_due.Count != 0) Clear(_due, "entity-lost");
    }

    /// <summary>Adopts a new world: release the previous world's ids, retaining any unconfirmed cleanup. The world
    /// epoch is not kept here — every group's key already carries the world it was minted in, and the kernel's
    /// own handle table dies with that world, so a handle from the previous epoch resolves to nothing either
    /// way.</summary>
    internal void BeginWorld()
    {
        CheckThread();
        _pending.Clear();
        ClearAll("world-changed");
        _writeTick = -1;
        _writesThisTick = 0;
    }

    /// <summary>The runtime failed or stopped: release held ids and retain failures for another cleanup attempt.</summary>
    internal void EndWorld()
    {
        CheckThread();
        _pending.Clear();
        ClearAll("world-ended");
        _writeTick = -1;
        _writesThisTick = 0;
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _lifecycle = null;
        try { ClearAll("provider-disposed"); }
        finally { if (ReferenceEquals(Current, this)) Current = null; }
    }

    /// <summary>The end of one effect: every modification filed under the handle is released through the same
    /// native clear every other release uses — every member of every recipient the command wrote — and nothing
    /// else is. A handle no group answers for is the no-op of an effect the module already undid.</summary>
    private void ReleaseGroup(RuntimeHandle key, string reason)
    {
        if (!_groups.TryGetValue(key, out var group)) return;
        _due.Clear();
        _due.AddRange(group.Ids);
        Clear(_due, reason);
    }

    /// <summary>Whether this adapter holds modifications under one handle — the question a caller asks to tell a
    /// command whose effect is still live from one it never filed.</summary>
    internal bool HoldsGroup(RuntimeHandle handle) => _groups.ContainsKey(handle);

    /// <summary>Releases the named ids through the native synced clear and forgets the ones that were released.
    /// An id whose removal was not confirmed stays in the ledger — with one diagnostic, not one per tick — so the world-end
    /// pass still tries it: dropping it here would leave a native modification nothing can ever name again.</summary>
    private void Clear(List<uint> ids, string reason)
    {
        foreach (var id in ids)
        {
            if (!_entries.TryGetValue(id, out var entry)) continue;
            try
            {
                ForgeCore.AgentModifiers.Clear(id);
                // The synchronized entry only sends a clear. Verify removal from the exact native instance
                // captured at application, even if this player's current instance has since been replaced.
                var instance = entry.NativeInstance;
                if (instance == null || instance.Holds(id))
                    throw new InvalidOperationException("Native modifier removal not confirmed.");
            }
            catch (Exception error)
            {
                _clearFailures++;
                if (!entry.Reported)
                {
                    entry.Reported = true;
                    _report("player.attribute-modifier-clear-failed " + reason + ": " + error.GetType().Name);
                }
                continue;
            }
            Forget(entry);
        }
    }

    private void ClearAll(string reason)
    {
        long before = _clearFailures;
        Clear(_entries.Keys.ToList(), reason);
        if (_clearFailures != before)
        {
            _report("player.attribute-modifier-cleanup-incomplete " + reason
                + ": failures=" + (_clearFailures - before).ToString(CultureInfo.InvariantCulture));
            throw new RuntimeContractException(ResultCode.NativeClearException, "Native modifier cleanup was not confirmed: " + reason);
        }
    }

    private void Forget(Applied entry)
    {
        _entries.Remove(entry.Id);
        Track(entry.Target, entry.Modifier, -entry.Submitted);
        if (!_groups.TryGetValue(entry.Handle, out var group)) return;
        group.Ids.Remove(entry.Id);
        if (group.Ids.Count == 0) _groups.Remove(entry.Handle);
    }

    /// <summary>The contribution this provider currently holds on one recipient and member, which is what the
    /// optional cap bounds. It is the ledger's own arithmetic and not a read of the native table: the table's
    /// total also holds the game's own contributions, which a Forge cap may not claim to clamp.</summary>
    internal double Total(EntityReference target, AgentModifier modifier)
        => _totals.TryGetValue((target, modifier), out double total) ? total : 0;

    /// <summary>Releases exactly these ids through the native clear every other release uses; false when any removal was not
    /// confirmed.</summary>
    internal bool Release(IEnumerable<uint> ids, string reason)
    {
        CheckThread();
        long before = _clearFailures;
        _due.Clear();
        _due.AddRange(ids);
        Clear(_due, reason);
        return _clearFailures == before;
    }

    /// <summary>Adds one live contribution to the provider's own total for a recipient and member, dropping the
    /// entry when it returns to nothing so the table stays the size of what is really held. Fractional deltas
    /// added and removed in a different order leave rounding residue, so "nothing" is a tolerance, not exact zero.</summary>
    private void Track(EntityReference target, AgentModifier modifier, double delta)
    {
        var key = (target, modifier);
        _totals.TryGetValue(key, out double total);
        total += delta;
        if (Math.Abs(total) < 1e-9) _totals.Remove(key);
        else _totals[key] = total;
    }

    /// <summary>Whether this tick may still write one recipient's whole member set. The budget is a provider
    /// counter, reset by each new tick; a recipient whose set does not fit is refused by name and spends nothing,
    /// instead of taking part of its set.</summary>
    private bool TryReserveWrites(long tick, int count, bool force)
    {
        if (_writeTick != tick) { _writeTick = tick; _writesThisTick = 0; }
        if (!force && _writesThisTick + count > MaximumModifierWritesPerTick) return false;
        _writesThisTick += count;
        return true;
    }

    /// <summary>Writes the recipients the budget has not reached, oldest first, as far as this tick's budget goes.
    /// The first one of a tick is always written. A recipient that fails here has no command left to answer, so
    /// its refusal is a log line, and the recipients behind it are still written.</summary>
    private void DrainPending(long tick)
    {
        CheckThread();
        try
        {
            if (_players() is not { CanCommit: true })
            { _pending.Clear(); _report("player.attribute-queued-dropped: " + ResultCode.AuthorityOrPhase); return; }
            while (_pending.TryPeek(out var request))
            {
                while (request.Waiting.Count != 0)
                {
                    bool first = _writeTick != tick || _writesThisTick == 0;
                    _written.Clear();
                    var outcome = WriteRecipient(request, request.Waiting.Peek(), tick, first, _written, out var row, out _);
                    if (outcome == Written.Deferred) return;
                    request.Waiting.Dequeue();
                    FileWritten(request, _written);
                    if (outcome == Written.Committed) continue;
                    _report("player.attribute-queued-write: " + outcome + " " + row.Code);
                }
                _pending.Dequeue();
            }
        }
        catch (Exception error)
        {
            _pending.Clear();
            _report("player.attribute-queued-failed: " + error.GetType().Name);
        }
    }

    /// <summary>The effect ended: its recipients that still wait are never written.</summary>
    private void DropPending(RuntimeHandle key)
    {
        foreach (var request in _pending)
            if (request.Named && request.Key == key) request.Waiting.Clear();
    }

}
