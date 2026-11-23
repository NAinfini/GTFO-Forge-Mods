using System;
using System.Collections.Generic;

namespace ForgeEnemy.Native;

// Some vanilla states read a static colour inside a private native call. Until an equivalent instance call edge
// is verified, lend only that call its value. Nested vanilla calls use the baseline, not their caller's colour.
// Cleanup is independent of the plugin session and restores only fields that still contain our write.
internal sealed class EnemyColorWindow<T> where T : struct
{
    private readonly Func<T> _read;
    private readonly Action<T> _write;
    private readonly Action<T, T> _restore;
    private readonly Func<T, T, bool> _same;
    private readonly List<Entry> _calls = new();
    private long _next;

    internal EnemyColorWindow(Func<T> read, Action<T> write, Action<T, T> restore, Func<T, T, bool> same)
        => (_read, _write, _restore, _same) = (read, write, restore, same);

    internal bool Pending => _calls.Count != 0;
    internal T Baseline
    {
        get
        {
            T current = _read();
            return _calls.Count == 0 || !_same(current, _calls[^1].Written) ? current : _calls[^1].Baseline;
        }
    }

    internal Scope Enter(T? authored)
    {
        T saved = _read();
        T baseline = _calls.Count == 0 || !_same(saved, _calls[^1].Written) ? saved : _calls[^1].Baseline;
        T written = authored ?? baseline;
        long token = ++_next;
        _calls.Add(new Entry(token, saved, written, baseline));
        try { _write(written); }
        catch { Leave(token); throw; }
        return new Scope(this, token);
    }

    internal void Clear()
    {
        while (_calls.Count != 0) Leave(_calls[^1].Token);
    }

    private void Leave(long token)
    {
        int index = _calls.Count - 1;
        while (index >= 0 && _calls[index].Token != token) index--;
        if (index < 0) return; // postfix and finalizer can both close the same call.
        for (int i = _calls.Count - 1; i >= index; i--)
        {
            var call = _calls[i];
            _calls.RemoveAt(i);
            _restore(call.Written, call.Saved);
        }
    }

    private readonly record struct Entry(long Token, T Saved, T Written, T Baseline);

    internal readonly struct Scope : IDisposable
    {
        private readonly EnemyColorWindow<T>? _owner;
        private readonly long _token;
        internal Scope(EnemyColorWindow<T> owner, long token) => (_owner, _token) = (owner, token);
        public void Dispose() => _owner?.Leave(_token);
    }
}
