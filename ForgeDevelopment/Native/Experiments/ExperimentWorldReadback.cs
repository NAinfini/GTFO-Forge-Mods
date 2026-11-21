using System;

namespace ForgeDevelopment.Native;

// A native lifecycle call declares at most one world replacement. This is a read lease policy,
// not a scheduler: the experiment engine polls its independent readback like any other pending call.
// The engine's world number only rises, but not by one: in a session it is ForgeCore's world number times the network world
// span (256 per world), outside one it steps by one. So the replacement is "one move to a higher number", never "number + 1".
internal sealed class ExperimentWorldReadback
{
    internal ExperimentWorldReadback(long world) { World = world; }
    internal long World { get; private set; }
    private long _origin;
    private bool _returning, _leaving, _cleanup;
    internal bool Pending { get; private set; }

    internal void Begin(bool returning) => Begin(returning, false);

    /// <summary>Leaving the session hub from a cleaned lobby ends in NoLobby. The lobby's world was already cleaned, so the leave
    /// may replace it once or not at all; a level cleanup the leave runs belongs to it.</summary>
    internal void BeginLeave() => Begin(false, true);

    private void Begin(bool returning, bool leaving)
    {
        if (Pending) throw new InvalidOperationException("experiment.world-readback-already-pending");
        _origin = World;
        _returning = returning;
        _leaving = leaving;
        _cleanup = false;
        Pending = true;
    }

    internal void Validate(long world)
    {
        if (world == World) return;
        if (!Pending || World != _origin || world <= _origin)
            throw new InvalidOperationException("experiment.read-world-changed");
        World = world;
    }

    internal bool LevelCleanup()
    {
        if (!Pending || !_returning && !_leaving) return false;
        _cleanup = true;
        return true;
    }

    internal bool Readback(string state, bool floorPresent)
    {
        if (!Pending) throw new InvalidOperationException("experiment.world-readback-not-pending");
        if (World == _origin && !_leaving) return false;
        if (_leaving ? state != "NoLobby" || floorPresent
            : _returning ? !_cleanup || state != "Lobby" || floorPresent : state is "Lobby" or "NoLobby") return false;
        Pending = false;
        return true;
    }

    internal void Cancel() => Pending = false;
}
