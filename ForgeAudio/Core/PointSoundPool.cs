using System;
using System.Collections.Generic;
using System.Numerics;

namespace ForgeAudio;

/// <summary>The native half one kept sound emitter is driven through: make one at a point, post an event on it at a point (which also
/// moves it there) or globally, and recycle it. A post answers the sound engine's playing id, 0 when nothing was posted. The production
/// backend is the game's <c>CellSoundPlayer</c>; a focused case records the calls.</summary>
internal interface IPointSoundBackend<TPlayer> where TPlayer : class
{
    TPlayer Create(Vector3 position);
    uint Post(TPlayer player, uint sound, Vector3 position);
    uint PostGlobal(TPlayer player, uint sound);
    void Recycle(TPlayer player);
}

/// <summary>
/// The emitters a game sound event played at a world point goes through, and the stop that has to reach them.
///
/// A Wwise stop event acts on the sound object it is posted to, so a loop posted at a point is not stopped by the same stop event posted
/// globally or on a level object: every positioned play goes through an emitter this pool keeps, and <see cref="Stop"/> posts the stop event
/// on every one of them. An emitter is reused for a new sound within <see cref="ReuseRadius"/> of where it stands, so one shooter's repeated
/// sounds and the loop it started share one emitter instead of pushing that loop out of the pool; when the pool is full the least recently
/// used emitter is recycled. Game-independent over its backend.
/// </summary>
internal sealed class PointSoundPool<TPlayer> where TPlayer : class
{
    /// <summary>Emitters kept at once. A positioned loop's emitter is recycled only after this many other points were used since it was last touched.</summary>
    internal const int Capacity = 32;

    /// <summary>Metres within which a new sound reuses a kept emitter instead of taking another.</summary>
    internal const float ReuseRadius = 1f;

    private sealed class Entry
    {
        internal TPlayer Player = null!;
        internal Vector3 Position;
        internal long Used;
    }

    private readonly IPointSoundBackend<TPlayer> _backend;
    private readonly List<Entry> _entries = new();
    private TPlayer? _global;
    private long _clock;

    internal PointSoundPool(IPointSoundBackend<TPlayer> backend) => _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    internal int Count => _entries.Count;

    /// <summary>Posts <paramref name="sound"/> at <paramref name="position"/>; the playing id, 0 when the engine posted nothing.</summary>
    internal uint Play(uint sound, Vector3 position)
    {
        var entry = Nearest(position);
        if (entry != null) entry.Position = position;
        else
        {
            if (_entries.Count >= Capacity)
            {
                var oldest = _entries[0];
                foreach (var candidate in _entries) if (candidate.Used < oldest.Used) oldest = candidate;
                _backend.Recycle(oldest.Player);
                _entries.Remove(oldest);
            }
            entry = new Entry { Player = _backend.Create(position), Position = position };
            _entries.Add(entry);
        }
        entry.Used = ++_clock;
        return _backend.Post(entry.Player, sound, position);
    }

    /// <summary>Posts <paramref name="sound"/> on the one emitter that has no place (the sound plays as a level-wide one).</summary>
    internal uint PlayGlobal(uint sound) => _backend.PostGlobal(_global ??= _backend.Create(default), sound);

    /// <summary>Posts the stop event <paramref name="sound"/> on every kept emitter; how many it reached.</summary>
    internal int Stop(uint sound)
    {
        foreach (var entry in _entries) _backend.Post(entry.Player, sound, entry.Position);
        if (_global != null) _backend.PostGlobal(_global, sound);
        return _entries.Count + (_global != null ? 1 : 0);
    }

    /// <summary>Recycles every emitter (the world ended); every failure is reported together after all were tried.</summary>
    internal void Clear()
    {
        List<Exception>? failures = null;
        foreach (var entry in _entries.ToArray())
        {
            try { _backend.Recycle(entry.Player); _entries.Remove(entry); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (_global != null)
        {
            try { _backend.Recycle(_global); _global = null; }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures != null) throw new AggregateException("point-sound-cleanup-failed", failures);
    }

    private Entry? Nearest(Vector3 position)
    {
        Entry? best = null;
        var bestDistance = ReuseRadius * ReuseRadius;
        foreach (var entry in _entries)
        {
            var distance = Vector3.DistanceSquared(entry.Position, position);
            if (distance > bestDistance) continue;
            best = entry;
            bestDistance = distance;
        }
        return best;
    }
}
