using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>One game method patched once by ForgeCore, handed to every package that wants to see it. The patch calls
/// <see cref="RunBefore"/> from a prefix at <c>Priority.First</c>, <see cref="RunAfter"/> from a postfix at <c>Priority.Last</c> and
/// <see cref="RunFaulted"/> from a finalizer at <c>Priority.Last</c>, so a subscriber reads the call around every other patch of the
/// method. A subscriber's state travels from its before to its after through ForgeCore (a value, never boxed); a subscriber that only
/// listens at one end gives nothing for the other. A handler that throws is one <c>ErrorOnce</c> line and the others still run. The first
/// subscription puts the module's patches on (<see cref="Patches.Ensure"/>). Main thread only.</summary>
public sealed class Bracket<TCall>
{
    private interface ITap
    {
        void Before(TCall call);
        void After(TCall call, bool ran);
        void Faulted(TCall call, Exception error);
    }

    private sealed class Tap<TState> : ITap
    {
        private readonly Bracket<TCall> _owner;
        private readonly Func<TCall, TState>? _before;
        private readonly Action<TCall, TState, bool>? _after;
        private readonly Action<TCall, TState, Exception>? _faulted;
        private readonly Stack<TState> _states = new();

        internal Tap(Bracket<TCall> owner, Func<TCall, TState>? before, Action<TCall, TState, bool>? after, Action<TCall, TState, Exception>? faulted)
        {
            _owner = owner;
            _before = before;
            _after = after;
            _faulted = faulted;
        }

        public void Before(TCall call)
        {
            var state = default(TState)!;
            try { if (_before != null) state = _before(call); }
            catch (Exception error) { _owner.Failed("before", error); }
            _states.Push(state);
        }

        public void After(TCall call, bool ran)
        {
            if (!_states.TryPop(out var state)) return;
            try { _after?.Invoke(call, state, ran); }
            catch (Exception error) { _owner.Failed("after", error); }
        }

        public void Faulted(TCall call, Exception fault)
        {
            if (!_states.TryPop(out var state)) return;
            try { _faulted?.Invoke(call, state, fault); }
            catch (Exception error) { _owner.Failed("faulted", error); }
        }
    }

    private readonly string _module;
    private readonly Type _patches;
    private ITap[] _taps = [];
    private Func<TCall, bool>[] _skips = [];

    internal Bracket(string module, Type patches)
    {
        _module = module;
        _patches = patches;
    }

    /// <summary>True once something subscribed; the patch skips building the call otherwise.</summary>
    internal bool Active => _taps.Length != 0 || _skips.Length != 0;

    /// <summary>Sees the call before the game's body (and before any other patch of it).</summary>
    public void Before(Action<TCall> before) => Add(new Tap<bool>(this, call => { before(call); return true; }, null, null));

    /// <summary>Sees the call after the game's body returned and every other postfix ran; <c>ran</c> is false when a prefix skipped the body.</summary>
    public void After(Action<TCall, bool> after) => Add(new Tap<bool>(this, null, (call, _, ran) => after(call, ran), null));

    /// <summary>Sees the call at both ends and keeps its own state between them: <paramref name="before"/> answers the state,
    /// <paramref name="after"/> gets it back when the body returned and <paramref name="faulted"/> when the body threw (the exception is
    /// still the game's to throw).</summary>
    public void Around<TState>(Func<TCall, TState> before, Action<TCall, TState, bool>? after, Action<TCall, TState, Exception>? faulted = null)
        => Add(new Tap<TState>(this, before ?? throw new ArgumentNullException(nameof(before)), after, faulted));

    /// <summary>Answers true to keep the game's body from running for this call (a method patched with <see cref="Skipped"/> in its prefix;
    /// only those methods honour it). Every other subscriber still sees the call, and its <c>after</c> gets <c>ran</c> false. Asked before the other patches, so the first one that answers true wins.</summary>
    public void Skip(Func<TCall, bool> skip)
    {
        Patches.Ensure(_module, _patches);
        _skips = [.. _skips, skip ?? throw new ArgumentNullException(nameof(skip))];
    }

    private void Add(ITap tap)
    {
        Patches.Ensure(_module, _patches);
        _taps = [.. _taps, tap];
    }

    internal void RunBefore(TCall call)
    {
        foreach (var tap in _taps) tap.Before(call);
    }

    /// <summary>True when a <see cref="Skip"/> subscriber wants the body not to run; a subscriber that throws does not skip it.</summary>
    internal bool Skipped(TCall call)
    {
        foreach (var skip in _skips)
        {
            try { if (skip(call)) return true; }
            catch (Exception error) { Failed("skip", error); }
        }
        return false;
    }

    internal void RunAfter(TCall call, bool ran)
    {
        var taps = _taps;
        for (var i = taps.Length - 1; i >= 0; i--) taps[i].After(call, ran);
    }

    internal void RunFaulted(TCall call, Exception error)
    {
        var taps = _taps;
        for (var i = taps.Length - 1; i >= 0; i--) taps[i].Faulted(call, error);
    }

    private void Failed(string end, Exception error)
        => Plugin.Logger.ErrorOnce("A " + _module + " handler failed (" + end + "): " + error);
}
