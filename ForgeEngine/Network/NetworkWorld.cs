namespace ForgeEngine.Network;

/// <summary>
/// What ended a world, in the vocabulary the host's lifecycle speaks. Every transition here is one the runtime
/// itself causes: a new generation, a level cleanup, or the host taking itself out of the world. A checkpoint
/// reload is deliberately not one of them — the runtime keeps running across it, so it ends no world.
/// </summary>
internal enum WorldTransition : byte
{
    NewGeneration = 0,
    LevelCleanup = 1,
    HostSuspend = 2
}

/// <summary>
/// A world transition the host could not announce yet, because the kernel was still dispatching when it arrived. The
/// kernel moves to its new world at the end of that dispatch, and the announcement follows the epoch it really
/// reached, so several transitions inside one dispatch collapse into that single advance.
/// </summary>
internal struct PendingWorldChange
{
    private WorldTransition _transition;
    private string _detail;
    private bool _pending;

    internal bool Pending => _pending;

    /// <summary>Remembers a transition. The last one names why the world the kernel finally leaves ended.</summary>
    internal void Remember(WorldTransition transition, string detail)
    {
        _pending = true;
        _transition = transition;
        _detail = detail;
    }

    /// <summary>Takes the remembered transition, once: the advance it belongs to happens exactly at this call, and a
    /// second call reports nothing left to announce.</summary>
    internal bool Take(out WorldTransition transition, out string detail)
    {
        transition = _transition;
        detail = _detail ?? string.Empty;
        if (!_pending) return false;
        _pending = false;
        _transition = default;
        _detail = string.Empty;
        return true;
    }
}
