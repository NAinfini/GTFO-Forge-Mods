using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>The per-frame callbacks mods registered through <c>Frame</c>, run in registration order. A callback that throws is
/// reported once per mod and callback kind (<see cref="ModLog.ErrorOnce"/>) and never stops the others or later frames.</summary>
internal sealed class FrameBook
{
    private readonly struct Entry
    {
        internal Entry(string modId, Action callback) { ModId = modId; Callback = callback; }
        internal readonly string ModId;
        internal readonly Action Callback;
    }

    private readonly string _kind;
    private readonly List<Entry> _entries = new();

    internal FrameBook(string kind) => _kind = kind;

    internal int Count => _entries.Count;

    internal void Add(string modId, Action callback)
    {
        if (string.IsNullOrWhiteSpace(modId)) throw new ArgumentException("Name the mod (its plugin GUID).", nameof(modId));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        _entries.Add(new Entry(modId, callback));
    }

    /// <summary>Runs every callback once. A callback registered while this runs waits for the next call.</summary>
    internal void Run()
    {
        var count = _entries.Count;
        for (var i = 0; i < count; i++)
        {
            var entry = _entries[i];
            try { entry.Callback(); }
            catch (Exception error) { Log.For(entry.ModId).ErrorOnce(entry.ModId + " " + _kind + " callback failed: " + error); }
        }
    }
}
