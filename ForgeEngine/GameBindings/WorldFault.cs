namespace ForgeEngine.GameBindings;

/// <summary>A domain session's fault that lasts for the world it happened in. A native callback that threw stops the
/// session's work for that world; the next world (a new level, a restart, a rejoin) starts clean, so one failure never
/// disables the domain for the rest of the game process. A fault that disposed a half for good stays the session's own
/// permanent flag, not this one.</summary>
internal sealed class WorldFault
{
    private long _epoch;
    private bool _set;

    /// <summary>The session faulted in world <paramref name="worldEpoch"/>.</summary>
    public void Mark(long worldEpoch) { _set = true; _epoch = worldEpoch; }

    /// <summary>True while the fault stands: it was marked in world <paramref name="worldEpoch"/>. A later world clears it.</summary>
    public bool Holds(long worldEpoch)
    {
        if (!_set) return false;
        if (worldEpoch == _epoch) return true;
        _set = false;
        return false;
    }
}
