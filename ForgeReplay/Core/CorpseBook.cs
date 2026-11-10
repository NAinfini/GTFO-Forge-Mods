using System;
using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>The bodies of enemies that died in playback: the game plays the death and its decay, then the body is hidden after <see cref="KeepSeconds"/>;
/// a jump takes every body at once. An enemy is in the recording's world until its end event, a body never is, so these are all that is left outside it.</summary>
internal sealed class CorpseBook<T> where T : class
{
    internal const float KeepSeconds = 15f;
    private readonly List<(T Body, float Since)> _bodies = new();

    internal int Count => _bodies.Count;

    internal void Add(T body, float now) => _bodies.Add((body, now));

    /// <summary>Hands the bodies whose time is up to <paramref name="hide"/>.</summary>
    internal void Due(float now, Action<T> hide)
    {
        for (var i = _bodies.Count - 1; i >= 0; i--)
        {
            if (now - _bodies[i].Since < KeepSeconds) continue;
            hide(_bodies[i].Body);
            _bodies.RemoveAt(i);
        }
    }

    /// <summary>Hands every body to <paramref name="hide"/>.</summary>
    internal void All(Action<T> hide)
    {
        foreach (var (body, _) in _bodies) hide(body);
        _bodies.Clear();
    }
}
