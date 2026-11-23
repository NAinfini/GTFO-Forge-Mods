using System;
using ForgeEngine.Framework;

namespace ForgeEngine.Presentation.Native;

/// <summary>
/// What one `effect_play` step of one loaded plan keeps (<see cref="CommandContext.StepState"/>), made on the step's first
/// command and dropped with the plan. A sustained member keeps one live copy per play, and a play is the same play from one
/// command to the next when the host names the same play (<see cref="CommandContext.Play"/>): the play that follows one
/// target of a `for_each` stays that target's copy whatever the order of the list and whoever else leaves it. Two
/// commands of one play on one tick (the same target listed twice, one scope playing twice with no loop) are told apart
/// by their order on that tick, the n-th taking a row of its own. The keys are taken from the effect player once per row
/// and reused; nothing is looked up by text or allocated per play.
/// <para>The table is fixed: at most <see cref="Capacity"/> plays in <see cref="Slots"/> slots, open addressing on the
/// play's number. At most half the slots are ever in use, so a probe ends at an empty slot after a short run, whether the
/// play is found or new. When a new play finds <see cref="Capacity"/> plays held, the rows with no play for
/// <see cref="IdleTicks"/> ticks are dropped in one pass that rebuilds the table into its spare copy; the copies those rows
/// held are long over, and the play that comes back later takes a fresh key. A pass runs at most once per tick: it
/// remembers the first tick on which any row it kept can go idle, and until then a new play with no room is refused at
/// once with `effect-held-budget`, never folded into another play's copy. Nothing is allocated after
/// the step's first command. A new world keeps only the rows still held on the old world's last tick and restarts their
/// clocks on it (see <see cref="Take"/>).</para>
/// <para>Ticks only move forward inside one world. A command whose tick is older than its play's row (delivered after a
/// later one of the same play) is the play's own copy, ordinal 0: it is still that play, the order of commands on a tick
/// is only meaningful on the tick the row is counting, and the row's clock never moves backward.</para>
/// </summary>
internal sealed class EffectStep
{
    /// <summary>The held plays one step keeps at once. Most triggers run in the world scope and a step's concurrent plays
    /// are the targets of the loops it runs in, which the kernel bounds at 1024 rounds; 512 is the old table's total
    /// (16 scopes of 32) and well past the plays any shipped behavior holds at once.</summary>
    internal const int Capacity = 512;
    /// <summary>Twice <see cref="Capacity"/>: the table is never more than half full.</summary>
    internal const int Slots = Capacity * 2;
    /// <summary>Ten seconds at the 30 Hz tick. A copy ForgeVisual is not held again stops emitting after its member's hold
    /// window (at most half a second for the sustained `effect_play` members) and is freed once its particles end, within
    /// the effect's linger (eight seconds by default), so a row dropped here carries no live copy.</summary>
    internal const long IdleTicks = 300;
    /// <summary>Half a second at the 30 Hz tick: the longest hold window of the sustained `effect_play` members. A row
    /// last taken more than this before a world's last tick no longer holds a live copy.</summary>
    internal const long HoldTicks = 15;
    private const int Mask = Slots - 1;

    private struct Slot
    {
        internal bool Taken;
        internal long Play;
        internal long Tick;
        internal int Uses;
        internal int Key;
    }

    private Slot[] _slots = new Slot[Slots];
    private Slot[] _spare = new Slot[Slots];
    private int _count;
    /// <summary>The earliest tick on which a held row can be idle (a row refreshed since only moves it later); no pass runs
    /// before it.</summary>
    private long _nextPass = long.MaxValue;
    private long _world = long.MinValue;
    /// <summary>The latest tick a command of this world named.</summary>
    private long _lastTick = long.MinValue;
    /// <summary>The member last played by this step and what the player resolved it to, so a step that keeps playing the
    /// same member resolves it once.</summary>
    internal EffectMember? Member { get; set; }
    internal object? Resolved { get; set; }

    /// <summary>The held-play key of the row <see cref="Take"/> answered; 0 until the player takes one. A row number is
    /// good until the next <see cref="Take"/>.</summary>
    internal ref int KeyAt(int row) => ref _slots[row].Key;

    /// <summary>The row of <paramref name="play"/> on <paramref name="tick"/>; -1 when the table has no room for it.</summary>
    internal int Take(long world, long play, long tick)
    {
        if (world != _world)
        {
            // A host migration starts a new world for the same plays (their identities survive it): a row still held on
            // the old world's last tick keeps its play and key, so its copy goes on being held rather than drawn a second
            // time beside the one still fading, and restarts on the new world's clock. Every other row — its copy already
            // stopped, or the whole old level's table on a level change or checkpoint — is dropped now, not left to go
            // idle on the new world's clock while it holds the budget.
            if (_count > 0) Retain(_lastTick - HoldTicks, tick, restart: true);
            _world = world;
            _lastTick = tick;
        }
        else if (tick > _lastTick) _lastTick = tick;
        var row = Row(play, tick);
        if (row < 0) return -1;
        ref var slot = ref _slots[row];
        if (tick < slot.Tick) return row;
        if (tick > slot.Tick) { slot.Tick = tick; slot.Uses = 0; }
        var ordinal = slot.Uses++;
        if (ordinal == 0) return row;
        // The same play again on this tick: its own row, named by its order on the tick.
        var again = Row(unchecked((play ^ ordinal) * 1099511628211L), tick);
        if (again >= 0 && tick > _slots[again].Tick) _slots[again].Tick = tick;
        return again;
    }

    /// <summary>The row holding <paramref name="play"/>, or a new row claimed for it; -1 when <see cref="Capacity"/> plays
    /// are live.</summary>
    private int Row(long play, long tick)
    {
        var at = Find(_slots, play);
        if (_slots[at].Taken) return at;
        if (_count == Capacity)
        {
            if (tick < _nextPass || !Retain(tick - IdleTicks, tick, restart: false)) return -1;
            at = Find(_slots, play);
        }
        _slots[at] = new Slot { Taken = true, Play = play, Tick = tick };
        _count++;
        _nextPass = Math.Min(_nextPass, tick + IdleTicks + 1);
        return at;
    }

    /// <summary>The slot holding <paramref name="play"/>, or the empty slot that ends its probe run.</summary>
    private static int Find(Slot[] slots, long play)
    {
        var at = (int)(play ^ (play >> 32)) & Mask;
        while (slots[at].Taken && slots[at].Play != play) at = (at + 1) & Mask;
        return at;
    }

    /// <summary>Rebuilds the table into its spare copy in one pass, keeping only the rows last taken on or after
    /// <paramref name="since"/>; with <paramref name="restart"/> the kept rows start over on <paramref name="tick"/>. True
    /// when a row was dropped.</summary>
    private bool Retain(long since, long tick, bool restart)
    {
        Array.Clear(_spare, 0, Slots);
        var kept = 0;
        var nextPass = long.MaxValue;
        for (var i = 0; i < Slots; i++)
        {
            ref var slot = ref _slots[i];
            if (!slot.Taken || slot.Tick < since) continue;
            if (restart) { slot.Tick = tick; slot.Uses = 0; }
            _spare[Find(_spare, slot.Play)] = slot;
            kept++;
            nextPass = Math.Min(nextPass, slot.Tick + IdleTicks + 1);
        }
        (_slots, _spare) = (_spare, _slots);
        var dropped = kept < _count;
        _count = kept;
        _nextPass = nextPass;
        return dropped;
    }
}
