using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;

namespace ForgeReplay;

internal enum MarkerKind : byte { Kill, Down, Revive, Door, Alarm, Wave, Scan, Terminal, Checkpoint, Chat, Reactor, WorldEvent, Ping, Note }

/// <summary>A point on the timeline bar: when, what, the player it concerns (0 to 3, or -1 for none) and, for an event at a place (a door,
/// an alarm, a scan, a terminal, the reactor), the player nearest to it at that moment (-1 when nobody was within <see cref="Analysis.NearMetres"/>).</summary>
internal readonly record struct Marker(uint TimeMs, MarkerKind Kind, int Slot, int Near = -1);

/// <summary>A revive: when, who was revived, who revived them (-1 when nobody stood close) and the share of full health the reviver had.</summary>
internal readonly record struct RescueLine(uint TimeMs, int Revived, int Reviver, float Health);

internal readonly record struct ChatLine(uint TimeMs, int Slot, string Text);

/// <summary>A note a mod wrote into the recording (<c>Recording.Note</c>, event 101); <see cref="Mark"/> notes are also timeline markers, in the same order.</summary>
internal readonly record struct NoteLine(uint TimeMs, string ModId, string Kind, string Payload, bool Mark);

/// <summary>A line of the game's own log (a player was downed, revived, an item was found), with the game's colour tags taken out.</summary>
internal readonly record struct GameLine(uint TimeMs, string Text);

/// <summary>A line on a terminal's screen, or what is typed at it so far (<see cref="Typed"/>).</summary>
internal readonly record struct TerminalText(uint TimeMs, ulong Terminal, string Text, bool Typed);

/// <summary>A player (0 to 3) started using a terminal (the TerminalState event naming them).</summary>
internal readonly record struct TerminalUse(uint TimeMs, int Slot, ulong Terminal);

/// <summary>A place a player pinged.</summary>
internal readonly record struct ObjectiveLine(uint TimeMs, int Layer, ulong Status);

internal readonly record struct PingMark(uint TimeMs, int Slot, float X, float Y, float Z);

/// <summary>What one player did, counted from the recorded events. Damage is the health the hits took off enemies, in game units. A kill goes
/// to the player whose hit took the last health: settled on the host (<see cref="Analysis.Exact"/>); on a client the host's synced health
/// says how much each loss was, and the hits matched to it say whose (<see cref="HitMatch"/>).</summary>
internal sealed class PlayerStats
{
    // RayHits: the shooter's own ray ended on an enemy limb (a wall is not a hit). BulletHits: the bullet hits the host settled for this player.
    internal int Rays, RayHits, BulletHits, Kills, Downs, Revives, Packs;
    // Landed: the hits by bullet or swing the game settled (or the recorder read of its own); Weak: those on a weak spot; Backstabs: those in the back.
    internal int Landed, Weak, Backstabs;
    internal float BulletDamage, MeleeDamage, ExplosionDamage, DamageTaken;
    // Never in the footer: the share of full health lost (worked out from the health bar), the players this one revived, the packs others
    // used on this one, the enemies this one woke, the mines laid, the tongues dodged.
    internal float TakenShare;
    internal int Rescues, Supplied, Woken, Mines, Dodges;
    /// <summary>One bit per <see cref="StatKind"/>: part of that number so far was worked out, not read from what the game said.</summary>
    internal uint EstimatedKinds;
    internal bool Estimated(StatKind kind) => (EstimatedKinds & (1u << (int)kind)) != 0;
}

internal enum StatKind : byte { Ray, RayHit, Bullet, Melee, Explosion, Taken, Kill, Down, Revive, Pack, TakenShare, Rescue, Supplied, Woken, Mine, Dodge, BulletHit, Landed, Weak, Backstab }

/// <summary>A player joined or left (Presence). <see cref="Name"/> and <see cref="Loadout"/> are null when the event carries an empty name
/// or loadout: the file header's then stand.</summary>
internal readonly record struct PresenceLine(uint TimeMs, int Slot, bool Joined, string? Name, string? Loadout);

internal readonly record struct StatDelta(uint TimeMs, int Slot, StatKind Kind, float Amount, bool Estimated = false);

/// <summary>Health taken off enemies and put to a player: the one settlement the damage rows of the statistics (<see cref="StatDelta"/>) and the weapon
/// page (<see cref="RunReport"/>) are both made from, so the two never count a hit differently. <see cref="Kind"/> is the game's damage kind (0 bullet,
/// 1 melee, 2 explosion, <see cref="HitMatch.SentryKind"/>), <see cref="Wielded"/> the weapon row it belongs to; <see cref="WeaponKnown"/>: the row is
/// decided (the damage estimate names sentries and mines itself) and is not matched against the sentries' shots.</summary>
internal readonly record struct DamageLine(uint TimeMs, int Slot, int Kind, int Wielded, float Amount, bool Estimated, bool WeaponKnown = false);

/// <summary>The statistics up to a moment of the recording: the deltas are added as the clock passes them and counted again from the start
/// when it goes back.</summary>
internal sealed class StatsClock
{
    private readonly Analysis _analysis;
    private int _next;
    private uint _last;

    internal StatsClock(Analysis analysis) { _analysis = analysis; }

    internal PlayerStats[] Players { get; private set; } = { new(), new(), new(), new() };

    internal static void Add(PlayerStats p, StatKind kind, float amount, bool estimated = false)
    {
        if (estimated) p.EstimatedKinds |= 1u << (int)kind;
        switch (kind)
        {
            case StatKind.Ray: p.Rays += (int)amount; break;
            case StatKind.RayHit: p.RayHits += (int)amount; break;
            case StatKind.BulletHit: p.BulletHits += (int)amount; break;
            case StatKind.Bullet: p.BulletDamage += amount; break;
            case StatKind.Melee: p.MeleeDamage += amount; break;
            case StatKind.Explosion: p.ExplosionDamage += amount; break;
            case StatKind.Taken: p.DamageTaken += amount; break;
            case StatKind.Kill: p.Kills += (int)amount; break;
            case StatKind.Down: p.Downs += (int)amount; break;
            case StatKind.Revive: p.Revives += (int)amount; break;
            case StatKind.Pack: p.Packs += (int)amount; break;
            case StatKind.TakenShare: p.TakenShare += amount; break;
            case StatKind.Rescue: p.Rescues += (int)amount; break;
            case StatKind.Supplied: p.Supplied += (int)amount; break;
            case StatKind.Woken: p.Woken += (int)amount; break;
            case StatKind.Mine: p.Mines += (int)amount; break;
            case StatKind.Dodge: p.Dodges += (int)amount; break;
            case StatKind.Landed: p.Landed += (int)amount; break;
            case StatKind.Weak: p.Weak += (int)amount; break;
            case StatKind.Backstab: p.Backstabs += (int)amount; break;
        }
    }

    /// <summary>Moves to <paramref name="nowMs"/>; true when any number changed.</summary>
    internal bool Advance(uint nowMs)
    {
        var changed = false;
        if (nowMs < _last) { Players = new PlayerStats[] { new(), new(), new(), new() }; _next = 0; changed = true; }
        _last = nowMs;
        var deltas = _analysis.Deltas;
        while (_next < deltas.Count && deltas[_next].TimeMs <= nowMs)
        {
            var d = deltas[_next++];
            Add(Players[d.Slot], d.Kind, d.Amount, d.Estimated);
            changed = true;
        }
        return changed;
    }
}

/// <summary>One pass over a recording: the markers for the timeline bar, the chat, and the per-player statistics.</summary>
internal sealed class Analysis
{
    internal List<Marker> Markers { get; } = new();
    internal List<ChatLine> Chat { get; } = new();
    internal List<GameLine> GameLines { get; } = new();
    /// <summary>Every note any mod wrote (<see cref="NoteLine"/>), in time order; the <see cref="MarkerKind.Note"/> markers are the ones with <see cref="NoteLine.Mark"/>, in the same order.</summary>
    internal List<NoteLine> Notes { get; } = new();
    /// <summary>The level events the game scheduled that carry something to read (intel, a sub objective, a subtitle), with the moment each was scheduled:
    /// the <see cref="MarkerKind.WorldEvent"/> markers are these, in the same order. The event happens <see cref="WorldEventLine.DelayS"/> later.</summary>
    internal List<(uint TimeMs, WorldEventLine Line)> WorldEvents { get; } = new();
    internal List<PingMark> Pings { get; } = new();
    internal List<TerminalText> TerminalTexts { get; } = new();
    /// <summary>Which terminal each player turned to, in time order (a repeat of the same player at the same terminal is left out).</summary>
    internal List<TerminalUse> TerminalUses { get; } = new();

    /// <summary>The terminal <paramref name="slot"/> last turned to at or before <paramref name="nowMs"/>; false when none.</summary>
    internal bool TerminalOf(int slot, uint nowMs, out ulong terminal)
    {
        for (var i = TerminalUses.Count - 1; i >= 0; i--)
        {
            var use = TerminalUses[i];
            if (use.TimeMs > nowMs || use.Slot != slot) continue;
            terminal = use.Terminal;
            return true;
        }
        terminal = 0;
        return false;
    }
    /// <summary>Every line a terminal printed, as recorded (the game's tags kept), in time order: the screens of the replay level's terminals.</summary>
    internal List<(uint TimeMs, TerminalPrint Line)> TerminalPrints { get; } = new();
    /// <summary>Where each recorded terminal stands (its state and sent-line events say).</summary>
    internal Dictionary<ulong, (float X, float Y, float Z)> TerminalPlaces { get; } = new();

    /// <summary>The screen of <paramref name="terminal"/> at <paramref name="nowMs"/>: its last <paramref name="most"/> printed lines, oldest first,
    /// into <paramref name="into"/>, and what is typed at it (<paramref name="typed"/>, empty after a printed line). Returns how many lines it has
    /// printed by then, which with <paramref name="typed"/> says whether the screen changed.</summary>
    internal int TerminalScreenAt(ulong terminal, uint nowMs, int most, List<string>? into, out string typed)
    {
        typed = "";
        for (var i = TerminalTexts.Count - 1; i >= 0; i--)
        {
            var text = TerminalTexts[i];
            if (text.TimeMs > nowMs || text.Terminal != terminal) continue;
            if (text.Typed) typed = text.Text;
            break;
        }
        var count = 0; var first = -1;
        for (var i = TerminalPrints.Count - 1; i >= 0; i--)
        {
            var (time, line) = TerminalPrints[i];
            if (time > nowMs || line.Key != terminal) continue;
            if (count++ < most) first = i;
        }
        if (into != null && first >= 0)
            for (var i = first; i < TerminalPrints.Count && TerminalPrints[i].TimeMs <= nowMs; i++)
                if (TerminalPrints[i].Line.Key == terminal) into.Add(TerminalPrints[i].Line.Text);
        return count;
    }

    /// <summary>Each objective layer's recorded status changes (0 main, 1 secondary, 2 overload; status 40 and up is done), for the time bar's bands.</summary>
    internal List<ObjectiveLine> Objectives { get; } = new();
    internal PlayerStats[] Players { get; } = { new(), new(), new(), new() };

    /// <summary>True while the recorder is the host, so damage and kills are the game's own settlement and not an estimate: the header's
    /// value, switched at a HostChanged event (host migration).</summary>
    internal bool Exact { get; private set; }

    internal const float NearMetres = 30f;
    private WorldState? _state;

    private Timeline? _timeline;
    private Func<ulong, bool>? _notable;

    /// <summary>The timeline <see cref="Run"/> read the recording through (null for an analysis fed live): when the run is done it has read every block.</summary>
    internal Timeline? Source => _timeline;

    /// <summary>Every change of a statistic with its moment, in time order: what <see cref="StatsClock"/> adds up to any moment.</summary>
    internal List<StatDelta> Deltas { get; } = new();

    /// <summary>Every damage settlement in time order (empty for the recorder's own count): what the weapon page adds up.</summary>
    internal List<DamageLine> Damages { get; } = new();

    internal Analysis(bool exact) { Exact = exact; }

    /// <summary>True for the recorder's own count while it writes: only the totals are kept (the footer), not the deltas and the lists a whole
    /// run would grow in memory for nobody.</summary>
    internal bool TotalsOnly { get; init; }

    /// <summary>The recorder's player slot, -1 when it is not known; its own numbers are exact in any recording.</summary>
    internal int RecorderSlot { get; init; } = -1;

    /// <summary>Every kill of the recording in time order, for the kill feed.</summary>
    internal List<KillLine> Kills { get; } = new();

    /// <summary>Every wake-up of the recording in time order, for the blame feed: the host's Wake events with the cause the game names; on
    /// a client (or where no Wake came) the game's own wake-up animation (WakeUp) or the locomotion state turning to waking, cause unknown.
    /// A wake-up the game passed on from a known enemy is counted on that enemy's line.</summary>
    internal List<WakeLine> Wakes { get; } = new();
    private readonly Dictionary<int, ulong> _dataOf = new();
    private readonly Dictionary<int, uint> _wokeAt = new();
    private readonly Dictionary<int, int> _wakeLine = new();
    private readonly Dictionary<int, int> _locomotion = new();
    // The blame lines. On the host every wake-up passes ES_HibernateWakeUp.ActivateState, whose Wake event names the cause; a client has no Wake
    // events, so its line comes from the WakeUp or the change into the wake-up state, whichever is first within a few seconds, with no known waker.
    private const uint WakeSameMs = 3000;
    private const int WakingState = 15; // ES_StateEnum.HibernateWakeUp

    private HitMatch? _match;
    private readonly int[] _wielded = { -1, -1, -1, -1 };

    // ---- The host's own records in a client's recording (PLAN 5.7, kinds HostHit, HostWake, HostLink) ----
    private bool _hostLive, _fromHost, _hostGap;
    private readonly HostTotals[] _hosted = new HostTotals[4], _guessed = new HostTotals[4];
    private readonly HashSet<int> _hostKilled = new();
    private readonly Dictionary<int, (uint TimeMs, ulong Data, uint SpawnMs)> _pendingKills = new();
    private readonly List<int> _expired = new();
    /// <summary>How long after an enemy's death a hit of the host that killed it may still come (a packet leaves the host up to a tenth of a second after the hit and takes its trip).</summary>
    private const uint KillWaitMs = 2000;

    /// <summary>True once the host's records were in force at any moment of the recording (a client that had them).</summary>
    internal bool HostSeen { get; private set; }

    /// <summary>True when the host's records began and never ended before the recording did: every hit and wake-up is the host's own settlement, like a host recording's.</summary>
    internal bool HostWhole => HostSeen && !_hostGap;

    /// <summary>The settlement is the game's own: the recorder is the host, or the host's records are in force for the whole recording after they began.</summary>
    internal bool Settled => Exact || HostWhole;

    // What each counter of the host's records covers is kept per player: the part the host's hits and wake-ups added, and the part worked out by estimate while the records were not in force.
    private static void Tally(HostTotals[] into, int slot, StatKind kind, float amount)
    {
        ref var t = ref into[slot & 3];
        var tenths = (long)MathF.Round(amount * 10f);
        switch (kind)
        {
            case StatKind.Bullet: t.BulletTenths = (uint)Math.Max(0, t.BulletTenths + tenths); break;
            case StatKind.Melee: t.MeleeTenths = (uint)Math.Max(0, t.MeleeTenths + tenths); break;
            case StatKind.Explosion: t.ExplosionTenths = (uint)Math.Max(0, t.ExplosionTenths + tenths); break;
            case StatKind.Kill: t.Kills = (uint)Math.Max(0, t.Kills + (long)amount); break;
            case StatKind.Landed: t.Landed = (uint)Math.Max(0, t.Landed + (long)amount); break;
            case StatKind.Weak: t.Weak = (uint)Math.Max(0, t.Weak + (long)amount); break;
            case StatKind.Backstab: t.Backstabs = (uint)Math.Max(0, t.Backstabs + (long)amount); break;
            case StatKind.Woken: t.Woken = (uint)Math.Max(0, t.Woken + (long)amount); break;
            case StatKind.Pack: t.Packs = (uint)Math.Max(0, t.Packs + (long)amount); break;
            case StatKind.Supplied: t.Supplied = (uint)Math.Max(0, t.Supplied + (long)amount); break;
        }
    }

    /// <summary>What the host has settled so far for each player, from the host's own counts: the numbers a snapshot is made of.</summary>
    internal void HostTotalsOf(Span<HostTotals> into)
    {
        for (var slot = 0; slot < 4; slot++)
        {
            var p = Players[slot];
            into[slot] = new HostTotals
            {
                BulletTenths = (uint)MathF.Round(p.BulletDamage * 10f),
                MeleeTenths = (uint)MathF.Round(p.MeleeDamage * 10f),
                ExplosionTenths = (uint)MathF.Round(p.ExplosionDamage * 10f),
                Kills = (uint)p.Kills,
                Landed = (uint)p.Landed,
                Weak = (uint)p.Weak,
                Backstabs = (uint)p.Backstabs,
                Woken = (uint)p.Woken,
                Packs = (uint)p.Packs,
                Supplied = (uint)p.Supplied,
            };
        }
    }

    // The host's snapshot: every counter becomes what the host says, by adding the difference from what the records and the estimates since added.
    // Packs and supplies are the extended snapshot's; in a mode 1 snapshot of an older recording they are left as the recording counted them.
    private void TakeSnapshot(uint timeMs, int mask, HostTotals[] totals, bool extended)
    {
        _hostLive = true; HostSeen = true;
        for (var slot = 0; slot < 4; slot++)
        {
            if ((mask >> slot & 1) == 0) continue;
            var s = totals[slot]; var h = _hosted[slot]; var g = _guessed[slot];
            void Add(StatKind kind, long now, long was, long guess, float scale)
            {
                var delta = now - was - guess;
                if (delta != 0) Count(timeMs, slot, kind, delta / scale);
            }
            Add(StatKind.Bullet, s.BulletTenths, h.BulletTenths, g.BulletTenths, 10f);
            Add(StatKind.Melee, s.MeleeTenths, h.MeleeTenths, g.MeleeTenths, 10f);
            Add(StatKind.Explosion, s.ExplosionTenths, h.ExplosionTenths, g.ExplosionTenths, 10f);
            Add(StatKind.Kill, s.Kills, h.Kills, g.Kills, 1f);
            Add(StatKind.Landed, s.Landed, h.Landed, g.Landed, 1f);
            Add(StatKind.Weak, s.Weak, h.Weak, g.Weak, 1f);
            Add(StatKind.Backstab, s.Backstabs, h.Backstabs, g.Backstabs, 1f);
            Add(StatKind.Woken, s.Woken, h.Woken, g.Woken, 1f);
            if (extended)
            {
                Add(StatKind.Pack, s.Packs, h.Packs, g.Packs, 1f);
                Add(StatKind.Supplied, s.Supplied, h.Supplied, g.Supplied, 1f);
            }
            _hosted[slot] = s; _guessed[slot] = default;
        }
    }

    private void FlushKills(uint timeMs)
    {
        foreach (var pair in _pendingKills) if (timeMs - pair.Value.TimeMs > KillWaitMs) _expired.Add(pair.Key);
        foreach (var entity in _expired) _pendingKills.Remove(entity);
        _expired.Clear();
    }

    // A kill by the player the killing hit named: counted, marked and put in the feed.
    private void CountKill(uint timeMs, int entity, int killer, bool exact, int kind, ulong data, uint spawnMs)
    {
        Count(timeMs, killer, StatKind.Kill, 1f, !exact);
        // Only notable kills (a special, a mini boss, a boss) are marked on the bar; a crowd of ordinary ones would hide the rest.
        if (_notable?.Invoke(data) == true) Markers.Add(new Marker(timeMs, MarkerKind.Kill, killer));
        if (!TotalsOnly) Kills.Add(new KillLine(timeMs, killer, kind, data, _wielded[killer], exact, entity, spawnMs));
    }

    // One hit on an enemy: the counts every hit makes, and for one the host settled the health it took off and, when it took the last, the kill.
    private void Hurt(uint timeMs, int entity, int kind, int slot, bool weak, bool behind, bool settled, float taken, bool lethal)
    {
        _lastKind[entity] = kind;
        if (kind == 0 && _shot[slot] && timeMs - _lastShotMs[slot] <= ShotToHitMs) Count(timeMs, slot, StatKind.BulletHit, 1f);
        if (kind <= 1)
        {
            Count(timeMs, slot, StatKind.Landed, 1f);
            if (weak) Count(timeMs, slot, StatKind.Weak, 1f);
            if (behind) Count(timeMs, slot, StatKind.Backstab, 1f);
        }
        if (!settled) return;
        // The host's settlement: the health the hit took off, and whether it took the last.
        Damaged(timeMs, slot, kind, WeaponOf(slot, kind), taken, false);
        if (!lethal) return;
        if (_fromHost && _pendingKills.Remove(entity, out var gone))
        {
            // The enemy was seen dying before the host's word on who killed it came.
            CountKill(gone.TimeMs, entity, slot, true, kind, gone.Data, gone.SpawnMs);
            _lastKind.Remove(entity);
            return;
        }
        _killer[entity] = (slot, true);
        if (_fromHost) _hostKilled.Add(entity);
    }

    /// <summary>The most ammunition each player's pack shows in the whole recording: slot, then main, special, tool.</summary>
    internal readonly ulong[,] AmmoPeak = new ulong[4, 3];
    private readonly Dictionary<int, int> _lastKind = new();

    // The damage of other players worked out from their rays when the host syncs no enemy health (a client's recording; null without the game data).
    private DamageEstimate? _estimate;
    private Action<uint, uint, int, int, float, int>? _landed;
    private const int HibernateState = 14; // ES_StateEnum.Hibernate: the game's sleeper multiplier
    private readonly Dictionary<ulong, int> _sentryOwners = new();
    private readonly Dictionary<ulong, (int Owner, float X, float Y, float Z)> _mineOwners = new();
    private const float MineSameMetres = 1f;
    private readonly string[] _loadouts = { "", "", "", "" };
    private uint _otherKillerMs; private bool _otherKiller; // the last mine or sentry that went off: who owned what it hit cannot be told

    /// <summary>The hits <see cref="DamageEstimate"/> worked out for other players, in time order (the weapon table's estimated damage reads them).</summary>
    internal IReadOnlyList<EstimatedHit> EstimatedHits => _estimate?.Hits ?? (IReadOnlyList<EstimatedHit>)Array.Empty<EstimatedHit>();

    /// <summary>True when the recording holds the host's synced health of enemies (a client's, format 5): every loss of health is then the
    /// game's number, and only its owner can be an estimate (several players hit the enemy at once).</summary>
    internal bool Synced { get; private set; }

    private readonly Dictionary<int, (int Slot, bool Exact)> _killer = new();

    /// <summary>The health the host synced for each enemy (the screen's exact remaining health of a watched enemy).</summary>
    internal EnemyHealthTrack HealthTrack { get; } = new();
    // A client's own readings of its hits on an enemy the host never synced a health of (no health sync came): settled when it is gone, at the
    // moment of each hit, no more than its full health.
    private readonly Dictionary<int, List<(uint TimeMs, int Slot, int Kind, int Wielded, float Damage)>> _readings = new();
    private readonly HashSet<int> _syncedEnemies = new();
    private readonly Dictionary<int, float> _fullHealth = new();
    private readonly int[] _matchCounts = new int[4];
    private readonly bool[] _healthSynced = new bool[4];
    private readonly bool[] _wasDown = new bool[4];
    private readonly Dictionary<(ushort, ulong), ulong> _seen = new();

    /// <summary>One flag per second of the recording: something happened in it (a shot, a hit, a hurt player, a swing, an interaction, a
    /// player walking). The seconds without are what <see cref="Pacing"/> plays fast while the automatic camera is on.</summary>
    internal List<bool> Busy { get; } = new();

    /// <summary>Every revive with who did it (the nearest other player, within <see cref="RescueMetres"/>) and the share of full health they had.</summary>
    internal List<RescueLine> Rescues { get; } = new();

    /// <summary>The moments every player in the level was down at once.</summary>
    internal List<uint> Wipes { get; } = new();

    internal const float RescueMetres = 3f, MoveMetres = 1.5f;
    private readonly Dictionary<int, uint> _spawnMs = new();
    private readonly (float X, float Y, float Z, bool Set)[] _anchor = new (float, float, float, bool)[4];
    private bool _wiped;

    private void Busied(uint timeMs)
    {
        if (TotalsOnly) return;
        var second = (int)(timeMs / 1000);
        while (Busy.Count <= second) Busy.Add(false);
        Busy[second] = true;
    }

    // A player sample: a walk of MoveMetres from where they last stood counts as activity; turning on the spot does not.
    private void Moved(uint timeMs, int slot, float x, float y, float z)
    {
        ref var a = ref _anchor[slot];
        var dx = x - a.X; var dy = y - a.Y; var dz = z - a.Z;
        if (a.Set && dx * dx + dy * dy + dz * dz < MoveMetres * MoveMetres) return;
        if (a.Set) Busied(timeMs);
        a = (x, y, z, true);
    }

    private void Count(uint timeMs, int slot, StatKind kind, float amount, bool estimated = false)
    {
        StatsClock.Add(Players[slot & 3], kind, amount, estimated);
        if (!TotalsOnly) Deltas.Add(new StatDelta(timeMs, slot & 3, kind, amount, estimated));
        // What the host's records added, and what was worked out while they were not in force: the snapshot settles the two against the host's numbers.
        // A Supply event this machine recorded (the recorder's own gift) is not in the host's total: only the giver's machine records a gift, so it is not the host's to level.
        if (_fromHost) Tally(_hosted, slot, kind, amount);
        else if (!_hostLive && !Exact && kind != StatKind.Supplied) Tally(_guessed, slot, kind, amount);
    }

    private static StatKind DamageKind(int kind) => kind is 0 or HitMatch.SentryKind ? StatKind.Bullet : kind == 1 ? StatKind.Melee : StatKind.Explosion;

    // The weapon row a hit of this kind by this player belongs to: mines and a sentry's bullets have their own, the rest the weapon in hand.
    private int WeaponOf(int slot, int kind) => kind == 2 ? WeaponKey.Explosion : kind == HitMatch.SentryKind ? WeaponKey.Sentry : _wielded[slot & 3];

    // The one place damage is settled: the statistics row and the weapon page's line.
    private void Damaged(uint timeMs, int slot, int kind, int wielded, float amount, bool estimated, bool weaponKnown = false)
    {
        Count(timeMs, slot, DamageKind(kind), amount, estimated);
        if (!TotalsOnly) Damages.Add(new DamageLine(timeMs, slot & 3, kind, wielded, amount, estimated, weaponKnown));
    }

    /// <summary><paramref name="notable"/> tells from an enemy's data block id whether its death is marked on the bar (null: none);
    /// <paramref name="hearing"/> how far its type hears a player (null: only recorded noises explain a wake-up). Touches nothing but the
    /// timeline and these two, so it may run off the game thread over a timeline of its own when both read plain data.</summary>
    internal static Analysis Run(Timeline timeline, bool exact, int recorderSlot = -1, Func<ulong, bool>? notable = null, Func<ulong, Hearing?>? hearing = null, DamageData? damage = null, IReadOnlyList<PlayerEntry>? players = null, System.Threading.CancellationToken cancel = default)
    {
        var analysis = new Analysis(exact) { _state = timeline.State, _timeline = timeline, _notable = notable, RecorderSlot = recorderSlot };
        if (!exact) analysis._match = new HitMatch(recorderSlot);
        if (damage != null) analysis._estimate = new DamageEstimate(damage);
        if (players != null) foreach (var p in players) if (p.Slot is >= 0 and <= 3) analysis._loadouts[p.Slot] = p.Loadout;
        var inference = new ClientInference(timeline.State, hearing);
        var report = new RunReport(timeline.State);
        timeline.Seek(0);
        timeline.Advance(timeline.DurationMs, r =>
        {
            cancel.ThrowIfCancellationRequested(); // a read nobody waits for any more stops here
            // An event cut short (the payload writer drops what does not fit) is skipped and counted: one bad event never leaves the recording unopened.
            try { analysis.Take(r.TimeMs, r.EventType, new PayloadReader(r.Payload!), r.Payload); }
            catch (FormatException) { analysis.SkippedEvents++; }
            try { inference.Take(r.TimeMs, r.EventType, r.Payload!); }
            catch (FormatException) { analysis.SkippedEvents++; }
            report.Take(r.TimeMs, r.EventType, r.Payload!);
        }, r =>
        {
            inference.Sample(r.TimeMs, r.Entity, r.State);
            report.Sample(r.TimeMs, r.Entity, r.State);
            if (r.Entity < 4) analysis.Moved(r.TimeMs, r.Entity, r.X, r.Y, r.Z);
        });
        analysis.Settle(timeline.DurationMs);
        inference.Finish();
        analysis.Infer(inference);
        // The report is extra: a file it cannot read through leaves the statistics without it, never the replay unopened.
        try { report.Finish(analysis, inference, timeline.DurationMs); analysis.Report = report; }
        catch (Exception error) when (error is FormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { analysis.ReportFailure = error.Message; }
        return analysis;
    }

    /// <summary>How many events of the recording were cut short and left out of the counts.</summary>
    internal int SkippedEvents { get; private set; }

    /// <summary>What the statistics pages, the list summary and the export read beyond the counts (null for an analysis fed live).</summary>
    internal RunReport? Report { get; private set; }

    /// <summary>Why <see cref="Report"/> is null for an analysis of a whole recording: what the report's read stopped on.</summary>
    internal string? ReportFailure { get; private set; }

    /// <summary>The inferences of PLAN 5.6 over the whole recording (null for an analysis fed live by the recorder).</summary>
    internal ClientInference? Inferred { get; private set; }

    /// <summary>Who joined and left, in time order, with the name and loadout the event carries.</summary>
    internal List<PresenceLine> Presence { get; } = new();

    /// <summary>The time of every foam blob fired (a Projectile of kind 1), in order: the presenter counts a burst from it, since the game plays one sound for a whole burst.</summary>
    internal List<uint> FoamLaunches { get; } = new();

    private readonly bool[] _exactPacks = new bool[4];

    /// <summary>True when the packs in this row are the recorder's own uses (exact); the rest are matched by <see cref="ClientInference"/>.</summary>
    internal bool PacksExact(int slot) => _hasKit || (slot & 3) == RecorderSlot || _exactPacks[slot & 3];

    // The recording says what every player carries (event 55): every pack used is seen in the count going down, on any machine.
    private bool _hasKit, _hasReviveBy;
    private readonly PlayerKit[] _kits = new PlayerKit[4];
    private readonly uint[] _lastUseMs = new uint[4], _lastShotMs = new uint[4];
    private readonly bool[] _gave = new bool[4], _shot = new bool[4], _hurtSeen = new bool[4];
    private readonly HashSet<ulong> _minesCounted = new();
    private readonly Dictionary<int, int> _wokenBy = new();
    private readonly List<(int Entity, uint TimeMs)> _wakesPlaced = new();

    // A settled bullet counts as a hit of the player's shot only near one of their shots: a sentry's bullets name its owner too.
    private const uint ShotToHitMs = 1000, PackUseWindowMs = 3000;


    /// <summary>True when the game's own damage on this player is in the file: the recorder and, on the host, the bots. A remote player's
    /// damage is settled on that player's machine.</summary>
    // On a client the other players' damage is settled on their own machines: what this file has of it is partial (63.5 health taken
    // against 3 downs in the 11-05 client recording), so their row is read off the health bar instead.
    // The host syncs every player's health (PlayerHealth), so a client has the others' losses exactly where that sync came.
    internal bool HurtExact(int slot) => (slot & 3) == RecorderSlot || Exact && _hurtSeen[slot & 3] || _healthSynced[slot & 3];

    /// <summary>True when the damage of <paramref name="slot"/> is in the recording or can be worked out: the host settled it, the host's health
    /// sync says it, it is the recorder's own, or the game data is at hand to estimate it from the rays (<see cref="DamageEstimate"/>).</summary>
    internal bool DamageKnown(int slot) => Settled || Synced || (slot & 3) == RecorderSlot || _estimate != null;

    /// <summary>True when the kills of <paramref name="slot"/> can be put to it: everything <see cref="DamageKnown"/> covers, and on a client
    /// the hits matched to each death (<see cref="HitMatch"/>).</summary>
    internal bool KillsKnown(int slot) => Settled || Synced || (slot & 3) == RecorderSlot || _match != null;

    /// <summary>True when the hits of <paramref name="slot"/> on weak spots and in the back are in the recording: what the host settled,
    /// and the recorder's own. A client does not see where another player's bullet struck.</summary>
    internal bool HitsKnown(int slot) => Settled || (slot & 3) == RecorderSlot;

    /// <summary>True when the inferred row <paramref name="kind"/> can be worked out for <paramref name="slot"/>: a player whose health is in
    /// the recording for the damage taken; for revives, wake-ups and mines, unless the recording has some and not one could be put to a player.</summary>
    internal bool Estimable(StatKind kind, int slot)
    {
        var inferred = Inferred;
        if (inferred == null) return false;
        switch (kind)
        {
            case StatKind.TakenShare: return inferred.HealthSeen(slot);
            case StatKind.Rescue: return _hasReviveBy || inferred.Revives.Count == 0 || inferred.Revives.Exists(r => r.Reviver >= 0);
            case StatKind.Woken: return _wakesPlaced.Count > 0 || inferred.Wakes.Count == 0 || inferred.Wakes.Exists(w => w.NoiseSlot >= 0);
            case StatKind.Mine: return _minesCounted.Count > 0 || inferred.Mines.Count == 0 || inferred.Mines.Exists(m => m.Owner >= 0);
            default: return true;
        }
    }

    // The inferred rows as deltas, merged into the exact ones in time order so the statistics follow the clock.
    private void Infer(ClientInference inferred)
    {
        Inferred = inferred;
        var more = new List<StatDelta>();
        foreach (var h in inferred.Hurts) more.Add(new StatDelta(h.TimeMs, h.Slot, StatKind.TakenShare, h.Fraction, true));
        if (!_hasReviveBy)
            foreach (var r in inferred.Revives) if (r.Reviver >= 0) more.Add(new StatDelta(r.TimeMs, r.Reviver, StatKind.Rescue, 1f, true));
        foreach (var p in inferred.Packs)
        {
            if (!PacksExact(p.User)) more.Add(new StatDelta(p.TimeMs, p.User, StatKind.Pack, 1f, true));
            // What the recorder and the host's bots give is an exact Supply event; only a giver on another machine is left to work out.
            if (p.User != p.Receiver && (!_hasKit || p.User != RecorderSlot && !_gave[p.User])) more.Add(new StatDelta(p.TimeMs, p.Receiver, StatKind.Supplied, 1f, true));
        }
        // A wake-up line nothing named gets the player whose noise explains it (the same inference the Woken count uses).
        foreach (var w in inferred.Wakes)
        {
            if (w.NoiseSlot < 0) continue;
            for (var i = 0; i < Wakes.Count; i++)
                if (Wakes[i].Entity == w.Entity && Wakes[i].Waker < 0 && Wakes[i].Cause == WakeCause.Unknown && Math.Abs((long)Wakes[i].TimeMs - w.TimeMs) <= ClientInference.WakeBeforeMs)
                { Wakes[i] = Wakes[i] with { Waker = w.NoiseSlot, Cause = WakeCause.Noise }; break; }
        }
        foreach (var w in inferred.Wakes)
            if (w.NoiseSlot >= 0 && !_wakesPlaced.Exists(e => e.Entity == w.Entity && Math.Abs((long)e.TimeMs - w.TimeMs) <= ClientInference.WakeBeforeMs)) more.Add(new StatDelta(w.TimeMs, w.NoiseSlot, StatKind.Woken, 1f, true));
        foreach (var m in inferred.Mines) if (m.Owner >= 0 && !_minesCounted.Contains(m.Key)) more.Add(new StatDelta(m.TimeMs, m.Owner, StatKind.Mine, 1f, true));
        foreach (var d in inferred.Dodges) more.Add(new StatDelta(d.TimeMs, d.Slot, StatKind.Dodge, 1f, !d.TargetRecorded || !(Exact || d.Slot == RecorderSlot)));
        foreach (var h in EstimatedHits)
        {
            more.Add(new StatDelta(h.TimeMs, h.Slot, DamageKind(h.Kind), h.Damage, true));
            Damages.Add(new DamageLine(h.TimeMs, h.Slot, h.Kind, h.Wielded, h.Damage, true, true));
        }
        foreach (var d in more) StatsClock.Add(Players[d.Slot & 3], d.Kind, d.Amount, d.Estimated);
        // The own readings are settled at their hit's moment when the enemy is gone, so both lists are put in time order here.
        var all = Deltas.Concat(more).OrderBy(d => d.TimeMs).ToList();
        Deltas.Clear();
        Deltas.AddRange(all);
        var lines = Damages.OrderBy(d => d.TimeMs).ToList();
        Damages.Clear();
        Damages.AddRange(lines);
    }

    /// <summary>Counts one event, in time order. The recorder feeds it the events it writes and a replay feeds it the events it reads, so the
    /// summary at the end of a file and the analysis of the whole file agree by construction.</summary>
    internal void Take(uint timeMs, ushort type, PayloadReader read, byte[]? payload = null)
    {
        if (_match != null && _state != null && payload != null)
        {
            _match.Watch(timeMs, _state, _landed ??= EstimateSwing);
            if (type == EventKind.Shot) EstimateShot(timeMs, _match.Shot(timeMs, new PayloadReader(payload), _state), payload);
            else if (type is EventKind.Melee or EventKind.MeleeCharge)
            {
                var swing = new PayloadReader(payload);
                var slot = (int)swing.U() & 3;
                if (type == EventKind.MeleeCharge) _match.Charge(timeMs, slot, swing.U() == 1);
                else _match.Swung(timeMs, slot, (int)swing.U(), _estimate?.MeleeOf(_loadouts[slot]));
            }
            else if (type == EventKind.Boosters) _estimate?.Boosters(new PayloadReader(payload));
            else if (type == EventKind.MeleeHit) EstimateStruck(timeMs, new PayloadReader(payload));
            else if (type is EventKind.Sentry or EventKind.Mine) Placed(type, new PayloadReader(payload));
            else if (type == EventKind.SentryShot) EstimateSentry(timeMs, new PayloadReader(payload));
            else if (type == EventKind.Explosion) EstimateBlast(timeMs, new PayloadReader(payload));
        }
        if (_pendingKills.Count > 0) FlushKills(timeMs);
        var wasDown = _wasDown; var seen = _seen;
        switch (type)
        {
            case EventKind.Shot:
                {
                    var slot = (int)read.U() & 3; read.U(); read.Pos(); read.Pos();
                    Count(timeMs, slot, StatKind.Ray, 1f); Busied(timeMs);
                    _shot[slot] = true; _lastShotMs[slot] = timeMs;
                    // The ray ends on anything, a wall too: only one that ended on an enemy limb hit (kind 2).
                    var landed = read.U() == 1;
                    var endKind = read.U();
                    if (landed && endKind == 2) Count(timeMs, slot, StatKind.RayHit, 1f);
                    break;
                }
            case EventKind.EnemyHurt:
                {
                    var entity = (int)read.U(); var kind = (int)read.U(); var damage = read.U() / 10f; var attacker = read.U(); read.U();
                    var settled = read.U() == 1; var taken = read.U() / 10f; var lethal = read.U() == 1; read.U();
                    var limb = (int)read.U(); var weak = limb == 1; read.U(); var behind = read.U() == 1;
                    Busied(timeMs);
                    if (attacker > 3) break;
                    var slot = (int)attacker;
                    // While the host's records are in force they are every hit's settlement: this machine's own reading of a hit counts nothing.
                    if (!settled && _hostLive) break;
                    Hurt(timeMs, entity, kind, slot, weak, behind, settled, taken, lethal);
                    if (!settled)
                    {
                        // A client's reading of its own hit: the host's synced health says what it took off; kept only for an enemy no sync comes for.
                        _match?.Hit(timeMs, entity, slot, kind);
                        _estimate?.Own(kind, limb);
                        if (_syncedEnemies.Contains(entity)) break;
                        if (!_readings.TryGetValue(entity, out var list)) _readings[entity] = list = new();
                        list.Add((timeMs, slot, kind, WeaponOf(slot, kind), damage));
                    }
                    break;
                }
            case EventKind.EnemyHealth:
                {
                    var entity = (int)read.U(); var before = read.U() / 10f; var after = read.U() / 10f;
                    HealthTrack.Add(entity, timeMs, after);
                    if (Exact || _hostLive) break; // the host's own hits are settled in EnemyHurt, and a host that sends its records says whose every hit was
                    Synced = true;
                    _syncedEnemies.Add(entity); _readings.Remove(entity); _estimate?.Forget(entity);
                    var lost = before - after;
                    if (lost <= 0f || _match == null) break;
                    var players = _match.Match(timeMs, entity, _matchCounts, out var last, out var lastKind);
                    if (players == 0) break; // a mine, a sentry, fire: nobody's hit is in the window
                    var total = 0; foreach (var c in _matchCounts) total += c;
                    for (var slot = 0; slot < 4; slot++)
                        if (_matchCounts[slot] > 0) Damaged(timeMs, slot, _match.KindOf[slot], WeaponOf(slot, _match.KindOf[slot]), lost * _matchCounts[slot] / total, players > 1);
                    _lastKind[entity] = lastKind;
                    if (after <= 0f && before > 0f) _killer[entity] = (last, players == 1);
                    break;
                }
            case EventKind.EnemySpawn:
                {
                    var entity = (int)read.U(); _dataOf[entity] = read.U(); read.U(); read.Pos();
                    _spawnMs[entity] = timeMs;
                    HealthTrack.Spawned(entity, timeMs);
                    _fullHealth[entity] = read.U() / 10f;
                    break;
                }
            case EventKind.EnemyGone:
                {
                    var entity = (int)read.U();
                    var killed = read.U() == 1;
                    var data = _timeline?.GoneData ?? 0UL;
                    OwnReadings(timeMs, entity, killed);
                    var known = _killer.Remove(entity, out var settledKiller);
                    var spawnedAt = _spawnMs.GetValueOrDefault(entity);
                    // The host's word on who killed it may come after the enemy is seen dying (its packet travels): the kill waits for it a moment.
                    if (killed && !known && _hostLive) { _pendingKills[entity] = (timeMs, data, spawnedAt); known = false; }
                    else if (known && _hostKilled.Remove(entity)) _fromHost = true;
                    // No settlement and no synced health: the death goes to the players whose hits on it lie in the window before it.
                    if (killed && !known && !_hostLive && _match != null && _match.Match(timeMs, entity, _matchCounts, out var hitter, out var hitKind) is var hitters and > 0)
                    {
                        settledKiller = (hitter, hitters == 1 && hitKind <= 1); known = true; _lastKind[entity] = hitKind;
                    }
                    // No settlement and no hit in the window: nobody's kill (PLAN 5.5), whoever hit it longer ago.
                    if (killed && known)
                    {
                        CountKill(timeMs, entity, settledKiller.Slot, settledKiller.Exact, _lastKind.GetValueOrDefault(entity), data, spawnedAt);
                    }
                    _fromHost = false;
                    _match?.Forget(entity);
                    _lastKind.Remove(entity); _wokenBy.Remove(entity); _syncedEnemies.Remove(entity); _fullHealth.Remove(entity);
                    _dataOf.Remove(entity); _spawnMs.Remove(entity); _wokeAt.Remove(entity); _wakeLine.Remove(entity); _locomotion.Remove(entity);
                    break;
                }
            case EventKind.PlayerHurt:
                {
                    var slot = (int)read.U() & 3; read.U();
                    _hurtSeen[slot] = true; Busied(timeMs);
                    // On a client another player's hits are settled on their machine and only some reach this file: theirs come from PlayerHealth.
                    if (Exact || slot == RecorderSlot) Count(timeMs, slot, StatKind.Taken, read.U() / 10f);
                    break;
                }
            case EventKind.PlayerHealth:
                {
                    var slot = (int)read.U() & 3; var before = read.U() / 10f; var after = read.U() / 10f;
                    if (slot == RecorderSlot || Exact && _hurtSeen[slot]) break;
                    _healthSynced[slot] = true;
                    if (before > after) Count(timeMs, slot, StatKind.Taken, before - after);
                    break;
                }
            case EventKind.PlayerStatus:
                {
                    var slot = (int)read.U() & 3;
                    read.U();
                    _wielded[slot] = (int)read.U();
                    read.U();
                    for (var i = 0; i < 3; i++) AmmoPeak[slot, i] = Math.Max(AmmoPeak[slot, i], read.U());
                    var down = read.U() == 1;
                    if (down && !_wasDown[slot]) { Count(timeMs, slot, StatKind.Down, 1f); Markers.Add(new Marker(timeMs, MarkerKind.Down, slot)); }
                    wasDown[slot] = down;
                    Wiped(timeMs);
                    break;
                }
            case EventKind.Revive:
                {
                    var slot = (int)read.U() & 3;
                    Count(timeMs, slot, StatKind.Revive, 1f);
                    Markers.Add(new Marker(timeMs, MarkerKind.Revive, slot));
                    Rescued(timeMs, slot);
                    break;
                }
            case EventKind.ReviveBy:
                {
                    var target = (int)read.U(); var source = (int)read.U();
                    _hasReviveBy = true;
                    if (source <= 3 && source != target) Count(timeMs, source, StatKind.Rescue, 1f);
                    break;
                }
            case EventKind.Pack:
                {
                    var slot = (int)read.U() & 3;
                    _exactPacks[slot] = true;
                    _lastUseMs[slot] = timeMs;
                    // In a recording with PlayerKit events the count going down says it, for the host's bots and other machines too.
                    if (!_hasKit) Count(timeMs, slot, StatKind.Pack, 1f);
                    break;
                }
            case EventKind.PlayerKit:
                {
                    var slot = (int)read.U() & 3;
                    var kit = PlayerKit.Read(read);
                    var was = _kits[slot];
                    _hasKit = true;
                    _kits[slot] = kit;
                    if (!was.Known || was.PackItem == 0 || was.PackCount <= 0) break;
                    // Fewer left of the same pack is a use. The pack gone altogether is a use only just after a use motion: dropping it empties the slot too.
                    if (kit.PackItem == was.PackItem ? kit.PackCount < was.PackCount : kit.PackItem == 0 && _lastUseMs[slot] != 0 && timeMs - _lastUseMs[slot] <= PackUseWindowMs)
                        Count(timeMs, slot, StatKind.Pack, 1f);
                    break;
                }
            case EventKind.Interact:
                {
                    var slot = (int)read.U() & 3; Busied(timeMs);
                    if (read.U() is 5 or 6) _lastUseMs[slot] = timeMs; // pGenericInteractAnimation: give a resource, use a resource
                    break;
                }
            case EventKind.Supply:
                {
                    var giver = (int)read.U(); var receiver = (int)read.U();
                    if (giver > 3 || receiver > 3) break;
                    _gave[giver] = true; _lastUseMs[giver] = timeMs;
                    if (giver != receiver) Count(timeMs, receiver, StatKind.Supplied, 1f);
                    break;
                }
            case EventKind.Mine:
                {
                    var key = read.U(); var state = read.U(); var owner = read.U();
                    if (state == 0 && owner is >= 1 and <= 4 && _minesCounted.Add(key)) Count(timeMs, (int)owner - 1, StatKind.Mine, 1f);
                    break;
                }
            case EventKind.Wake:
                {
                    var entity = (int)read.U(); var propagated = read.U() == 1; read.U(); read.U();
                    var propagator = (int)read.U() - 1;
                    var detection = (int)read.U(); var heard = read.U() == 1;
                    var player = (int)read.U() - 1; var action = (int)read.U();
                    Waked(timeMs, entity, propagated, propagator, detection, heard, player, action, Exact);
                    break;
                }
            case EventKind.HostWake:
                {
                    // U entity, U propagated, U detection (7 none), U heard, U player + 1, U action, U propagator + 1, U age.
                    var entity = (int)read.U(); var propagated = read.U() == 1; var detection = (int)read.U(); var heard = read.U() == 1;
                    var player = (int)read.U() - 1; var action = (int)read.U(); var propagator = (int)read.U() - 1;
                    var age = Math.Min(read.U(), timeMs);
                    if (!_hostLive) break;
                    _fromHost = true;
                    Waked(timeMs - (uint)age, entity, propagated, propagator, detection == 7 ? 255 : detection, heard, player, action, true);
                    _fromHost = false;
                    break;
                }
            case EventKind.HostHit:
                {
                    var entity = (int)read.U(); var kind = (int)read.U(); var slot = (int)read.U() & 3; var taken = read.U() / 10f; var limb = (int)read.U(); var flags = read.U();
                    read.U(); read.U();
                    var age = Math.Min(read.U(), timeMs);
                    if (!_hostLive) break;
                    Busied(timeMs);
                    _fromHost = true;
                    Hurt(timeMs - (uint)age, entity, kind & 3, slot, limb == 1, (flags & 2) != 0, true, taken, (flags & 1) != 0);
                    _fromHost = false;
                    break;
                }
            case EventKind.HostLink:
                {
                    var totals = HostLink.ReadLink(read, out var live, out var mask, out var extended);
                    if (live) { _hostGap |= HostSeen && !_hostLive; TakeSnapshot(timeMs, mask, totals, extended); break; }
                    if (_hostLive) { _hostLive = false; _hostGap = true; _pendingKills.Clear(); }
                    break;
                }
            case EventKind.HostHurt:
                {
                    // U slot, U kind, U damage x10, U attacking player + 1, U attacking enemy entity + 1, U age. The amount of a player's damage is
                    // already in PlayerHealth (or read off its health); this is the source the host settled, for the down causes and friendly fire.
                    break;
                }
            case EventKind.HostSupply:
                {
                    // U giver, U receiver, U kind, U age. Only the giver's machine records a Supply (the host's own player and the bots), so these
                    // are the gifts a client recording does not have; it counts the receiver's supplies and remembers the giver for the inference.
                    var giver = (int)read.U(); var receiver = (int)read.U(); var kind = (int)read.U();
                    var age = Math.Min(read.U(), timeMs);
                    if (!_hostLive || giver > 3 || receiver > 3 || kind > 2) break;
                    var at = timeMs - (uint)age;
                    _gave[giver] = true; _lastUseMs[giver] = at;
                    if (giver == receiver) break;
                    _fromHost = true;
                    Count(at, receiver, StatKind.Supplied, 1f);
                    _fromHost = false;
                    break;
                }
            case EventKind.HostPack:
                {
                    // U slot, U age. A pack applied on the host's machine: the count in PlayerKit going down says it everywhere, so it counts nothing
                    // there; without PlayerKit events it is the pack use itself.
                    var slot = (int)read.U() & 3;
                    var age = Math.Min(read.U(), timeMs);
                    if (!_hostLive) break;
                    var at = timeMs - (uint)age;
                    _exactPacks[slot] = true; _lastUseMs[slot] = at;
                    if (_hasKit) break;
                    _fromHost = true;
                    Count(at, slot, StatKind.Pack, 1f);
                    _fromHost = false;
                    break;
                }
            case EventKind.HostRay:
                {
                    // U slot, U 1 the ray ended on an enemy limb, U age. A shot fired on the host's machine (its own player and the bots); it also
                    // marks the player's shot for the host's settled bullets that follow, which are counted as that player's hits.
                    var slot = (int)read.U() & 3;
                    var hit = read.U() == 1;
                    var age = Math.Min(read.U(), timeMs);
                    if (!_hostLive) break;
                    var at = timeMs - (uint)age;
                    _shot[slot] = true; _lastShotMs[slot] = at;
                    _fromHost = true;
                    Count(at, slot, StatKind.Ray, 1f);
                    if (hit) Count(at, slot, StatKind.RayHit, 1f);
                    _fromHost = false;
                    break;
                }
            case EventKind.WakeUp:
                {
                    var entity = (int)read.U(); read.U(); read.U(); read.U();
                    var propagated = read.U() == 1;
                    if (!Exact && !_hostLive && !WokeLately(entity, timeMs)) Woke(timeMs, entity, -1, propagated ? WakeCause.Passed : WakeCause.Unknown, 0, -1);
                    break;
                }
            case EventKind.EnemyState:
                {
                    var entity = (int)read.U(); var now = (int)read.U();
                    var was = _locomotion.TryGetValue(entity, out var before) ? before : 0;
                    _locomotion[entity] = now;
                    if (!Exact && !_hostLive && now == WakingState && was != WakingState && !WokeLately(entity, timeMs)) Woke(timeMs, entity, -1, WakeCause.Unknown, 0, -1);
                    break;
                }
            case EventKind.SentryShot:
            case EventKind.Explosion:
                _otherKiller = true; _otherKillerMs = timeMs;
                if (type == EventKind.SentryShot) { read.U(); var (sx, sy, sz) = read.Pos(); WakeSource(timeMs, WakeCause.Sentry, sx, sy, sz); }
                else { read.U(); var (ex, ey, ez) = read.Pos(); WakeSource(timeMs, WakeCause.Explosion, ex, ey, ez); }
                break;
            case EventKind.Projectile:
                if (read.U() == 1) FoamLaunches.Add(timeMs);
                break;
            case EventKind.Presence:
                {
                    var slot = (int)read.U();
                    if (slot > 3) break;
                    var joined = read.U() == 1;
                    var name = read.Text();
                    var loadout = read.Text();
                    Presence.Add(new PresenceLine(timeMs, slot, joined, string.IsNullOrEmpty(name) ? null : name, string.IsNullOrWhiteSpace(loadout) ? null : loadout));
                    if (joined && !string.IsNullOrEmpty(loadout)) _loadouts[slot] = loadout;
                    break;
                }
            case EventKind.Door:
                {
                    // A door that opens or is broken: the enemies of its zone may be woken by it.
                    if (payload != null)
                    {
                        var door = new PayloadReader(payload); var doorKey = door.U();
                        var status = door.U(); door.U(); var (dx, dy, dz) = door.Pos();
                        var changed = !_doorStatus.TryGetValue(doorKey, out var was) || was != status;
                        _doorStatus[doorKey] = status;
                        if (changed && status is 10 or 11) WakeSource(timeMs, WakeCause.Door, dx, dy, dz);
                    }
                    Changed(timeMs, type, read, MarkerKind.Door, _seen);
                    break;
                }
            case EventKind.Puzzle: Changed(timeMs, type, read, MarkerKind.Alarm, _seen); break;
            case EventKind.Scan: Changed(timeMs, type, read, MarkerKind.Scan, _seen); break;
            case EventKind.Reactor: Changed(timeMs, type, read, MarkerKind.Reactor, _seen); break;
            case EventKind.Wave: if (read.U() == 1) Markers.Add(new Marker(timeMs, MarkerKind.Wave, -1)); break;
            case EventKind.Terminal:
                {
                    var terminal = read.U();
                    var (x, y, z) = read.Pos();
                    Markers.Add(new Marker(timeMs, MarkerKind.Terminal, -1, NearestPlayer(x, y, z)));
                    TerminalPlaces[terminal] = (x, y, z);
                    break;
                }
            case EventKind.Checkpoint: Markers.Add(new Marker(timeMs, MarkerKind.Checkpoint, -1)); break;
            case EventKind.HostChanged:
                {
                    // From a migration on, the recorder counts by the rules of what it now is.
                    read.U();
                    if (TotalsOnly) break;
                    Exact = read.U() == 1;
                    if (!Exact) _match ??= new HitMatch(RecorderSlot);
                    break;
                }
            case EventKind.Objective:
                {
                    var layer = read.U();
                    Objectives.Add(new ObjectiveLine(timeMs, layer > 2 ? -1 : (int)layer, read.U()));
                    break;
                }
            case EventKind.Intel:
                {
                    var (_, _, intel) = ReplayExtras.Intel(payload!);
                    intel = Untagged(intel);
                    if (intel.Length > 0) AddGameLine(new GameLine(timeMs, intel));
                    break;
                }
            case EventKind.WorldEvent:
                {
                    if (TotalsOnly) break;
                    var line = ReplayExtras.WorldEvent(payload!);
                    line = line with { Intel = Untagged(line.Intel), SubHeader = Untagged(line.SubHeader), SubText = Untagged(line.SubText), Subtitle = Untagged(line.Subtitle) };
                    if (!line.Accepted || line.Intel.Length + line.SubHeader.Length + line.SubText.Length + line.Subtitle.Length == 0) break;
                    WorldEvents.Add((timeMs, line));
                    Markers.Add(new Marker(timeMs, MarkerKind.WorldEvent, -1));
                    // The game's intel and sub objective lines have their own events (Intel); a subtitle is shown nowhere else, when the event happens.
                    if (line.Subtitle.Length > 0) AddGameLine(new GameLine(timeMs + (uint)MathF.Round(line.DelayS * 1000f), line.Subtitle));
                    break;
                }
            case EventKind.TerminalLine:
                {
                    var printed = ReplayExtras.Printed(payload!);
                    TerminalPrints.Add((timeMs, printed));
                    foreach (var line in TerminalColumns(printed.Text).Split('\n')) TerminalTexts.Add(new TerminalText(timeMs, printed.Key, line.TrimEnd(), false));
                    break;
                }
            case EventKind.TerminalInput:
                {
                    var (terminal, typed) = ReplayExtras.Typed(payload!);
                    TerminalTexts.Add(new TerminalText(timeMs, terminal, Untagged(typed), true));
                    break;
                }
            case EventKind.TerminalState:
                {
                    // U key, U state, U slot + 1 of who is at it (0: nobody named), Pos.
                    var terminal = read.U();
                    read.U();
                    var user = (int)read.U() - 1;
                    var (x, y, z) = read.Pos(); TerminalPlaces[terminal] = (x, y, z);
                    if (user is < 0 or > 3 || TotalsOnly) break;
                    if (!TerminalOf(user, timeMs, out var before) || before != terminal) TerminalUses.Add(new TerminalUse(timeMs, user, terminal));
                    break;
                }
            case EventKind.GameLog:
                {
                    // The game's log also takes every chat line (type 1); those are Chat.
                    if (read.U() == 1) break;
                    var text = Untagged(read.Text());
                    if (text.Length > 0) AddGameLine(new GameLine(timeMs, text));
                    break;
                }
            case EventKind.Ping:
                {
                    var slot = read.U();
                    var (x, y, z) = read.Pos();
                    Pings.Add(new PingMark(timeMs, slot > 3 ? -1 : (int)slot, x, y, z));
                    Markers.Add(new Marker(timeMs, MarkerKind.Ping, slot > 3 ? -1 : (int)slot));
                    break;
                }
            case EventKind.Note:
                {
                    var note = new NoteLine(timeMs, read.Text(), read.Text(), read.Text(), read.U() != 0);
                    Notes.Add(note);
                    if (note.Mark) Markers.Add(new Marker(timeMs, MarkerKind.Note, -1));
                    break;
                }
            case EventKind.Chat:
                {
                    var sender = read.U();
                    var line = new ChatLine(timeMs, sender > 3 ? -1 : (int)sender, read.Text());
                    Chat.Add(line);
                    Markers.Add(new Marker(timeMs, MarkerKind.Chat, line.Slot));
                    break;
                }
        }
    }

    // An enemy no health sync came for is gone (or the recording ends): the client's own readings, no more than its full health; what they took off leaves the rest to the others.
    private void OwnReadings(uint timeMs, int entity, bool killed = false)
    {
        var taken = 0f;
        if (_readings.Remove(entity, out var readings))
        {
            var full = _state != null && _state.Enemies.TryGetValue(entity, out var enemy) ? enemy.FullHealth : _fullHealth.GetValueOrDefault(entity);
            var left = full > 0f ? full : float.MaxValue;
            foreach (var r in readings)
            {
                var took = Math.Min(r.Damage, left); left -= took; taken += took;
                Damaged(r.TimeMs, r.Slot, r.Kind, r.Wielded, took, r.Slot != RecorderSlot);
            }
        }
        _estimate?.Gone(entity, taken, killed && !(_otherKiller && timeMs - _otherKillerMs <= HitMatch.WindowMs));
    }

    // The end of the recording: enemies still there have their readings counted, and the estimate is settled.
    private void Settle(uint durationMs)
    {
        if (_state != null) _match?.Watch(durationMs, _state, _landed ??= EstimateSwing);
        foreach (var entity in _readings.Keys.ToList()) OwnReadings(durationMs, entity);
        _estimate?.Finish();
    }

    private bool Estimating(int entity, out (ulong DataId, ulong Dimension, float FullHealth) enemy)
    {
        enemy = default;
        return _estimate != null && !Exact && !_hostLive && entity >= 0 && !_syncedEnemies.Contains(entity) && _state!.Enemies.TryGetValue(entity, out enemy);
    }

    private void EstimateShot(uint timeMs, int entity, byte[] payload)
    {
        if (!Estimating(entity, out var enemy)) return;
        var read = new PayloadReader(payload);
        var slot = (int)read.U() & 3; var gun = read.U();
        var (ox, oy, oz) = read.Pos(); var (ex, ey, ez) = read.Pos();
        var metres = MathF.Sqrt((ex - ox) * (ex - ox) + (ey - oy) * (ey - oy) + (ez - oz) * (ez - oz));
        _estimate!.Shot(timeMs, entity, enemy.DataId, enemy.FullHealth, slot, _wielded[slot], gun, metres, DamageEstimate.BackDot(_state!, entity, timeMs, ex - ox, ey - oy, ez - oz));
    }

    // Who placed a sentry or a mine, as the game's own item owner is recorded; none known, its damage is nobody's.
    private void Placed(ushort type, PayloadReader read)
    {
        var key = read.U(); var state = read.U(); var owner = (int)read.U() - 1;
        if (state != 0 || owner is < 0 or > 3) return;
        if (type == EventKind.Sentry) _sentryOwners[key] = owner;
        else { var (x, y, z) = read.Pos(); _mineOwners[key] = (owner, x, y, z); }
    }

    private void EstimateSentry(uint timeMs, PayloadReader read)
    {
        read.U();
        var (x, y, z) = read.Pos(); var key = read.U(); var (ax, ay, az) = read.Pos(); read.U(); var mode = read.U();
        if (!_sentryOwners.TryGetValue(key, out var owner)) return;
        var entity = _match!.Sentry(timeMs, owner, x, y, z, ax, ay, az, _state!, out var metres);
        if (Estimating(entity, out var enemy)) _estimate!.Sentry(timeMs, entity, enemy.DataId, enemy.FullHealth, owner, mode, metres, DamageEstimate.BackDot(_state!, entity, timeMs, ax, ay, az));
    }

    // 35 Explosion: a mine's goes to whoever placed the mine at that place (a foam mine's, kind 1, does no damage).
    private void EstimateBlast(uint timeMs, PayloadReader read)
    {
        var kind = read.U(); var (x, y, z) = read.Pos();
        if (kind != 0) return;
        var owner = -1; var best = MineSameMetres * MineSameMetres;
        foreach (var mine in _mineOwners.Values)
        {
            var squared = (mine.X - x) * (mine.X - x) + (mine.Y - y) * (mine.Y - y) + (mine.Z - z) * (mine.Z - z);
            if (squared <= best) { best = squared; owner = mine.Owner; }
        }
        if (owner < 0) return;
        var most = read.U() / 10f; var least = read.U() / 10f; var near = read.U() / 10f; var far = read.U() / 10f;
        foreach (var entity in _state!.Enemies.Keys)
        {
            if (!_state.Place(entity, timeMs, out var ex, out var ey, out var ez, out _, out _, out _, out _)) continue;
            var metres = MathF.Sqrt((ex - x) * (ex - x) + (ey - y) * (ey - y) + (ez - z) * (ez - z));
            if (metres > far) continue;
            _match!.Hit(timeMs, entity, owner, 2);
            if (!Estimating(entity, out var enemy)) continue;
            _estimate!.Blast(timeMs, entity, enemy.DataId, enemy.FullHealth, owner, metres, near, far, most, least);
        }
    }

    // A MeleeHit: the game says this swing landed, at this place.
    private void EstimateStruck(uint timeMs, PayloadReader read)
    {
        var slot = (int)read.U() - 1;
        var (x, y, z) = read.Pos();
        if (slot is < 0 or > 3) return;
        var entity = _match!.Struck(timeMs, slot, x, y, z, _state!, out var kind, out var held);
        if (!Estimating(entity, out var enemy)) return;
        var dot = _state!.Place(slot, timeMs, out var px, out var py, out var pz, out _, out _, out _, out _) ? DamageEstimate.BackDot(_state, entity, timeMs, x - px, y - py, z - pz) : -1f;
        _estimate!.Swing(timeMs, entity, enemy.DataId, enemy.FullHealth, slot, _wielded[slot], kind, held, _estimate.MeleeOf(_loadouts[slot]), dot, _locomotion.GetValueOrDefault(entity) == HibernateState, struck: true);
    }

    private void EstimateSwing(uint swungMs, uint timeMs, int slot, int kind, float held, int entity)
    {
        if (!Estimating(entity, out var enemy) || !_state!.Place(slot, timeMs, out var x, out var y, out var z, out _, out _, out _, out _) || !_state.Place(entity, timeMs, out var ex, out var ey, out var ez, out _, out _, out _, out _)) return;
        _estimate!.Swing(timeMs, entity, enemy.DataId, enemy.FullHealth, slot, _wielded[slot], kind, held, _estimate.MeleeOf(_loadouts[slot]),
            DamageEstimate.BackDot(_state, entity, timeMs, ex - x, ey - y, ez - z), _locomotion.GetValueOrDefault(entity) == HibernateState);
    }

    // Kept in time order: a line due later than the ones read after it (a delayed subtitle) goes in its place.
    private void AddGameLine(GameLine line)
    {
        var at = GameLines.Count;
        while (at > 0 && GameLines[at - 1].TimeMs > line.TimeMs) at--;
        GameLines.Insert(at, line);
    }
    // Everyone in the level down at once: a wipe, once until someone is up again.
    private void Wiped(uint timeMs)
    {
        if (_state == null || TotalsOnly) return;
        var present = 0; var down = 0;
        for (var slot = 0; slot < 4; slot++)
        {
            if (!_state.Poses[slot].Present) continue;
            present++;
            if (_wasDown[slot]) down++;
        }
        var all = present > 0 && down == present;
        if (all && !_wiped) Wipes.Add(timeMs);
        _wiped = all;
    }

    // Who revived the player: the nearest other player standing within RescueMetres, and how much health they had (the sample's 0 to 127).
    private void Rescued(uint timeMs, int revived)
    {
        if (_state == null || TotalsOnly) return;
        ref var them = ref _state.Poses[revived];
        var best = -1; var bestSquared = RescueMetres * RescueMetres;
        for (var slot = 0; slot < 4; slot++)
        {
            ref var p = ref _state.Poses[slot];
            if (slot == revived || !p.Present || _wasDown[slot]) continue;
            var dx = p.X - them.X; var dy = p.Y - them.Y; var dz = p.Z - them.Z;
            var squared = dx * dx + dy * dy + dz * dz;
            if (squared <= bestSquared) { bestSquared = squared; best = slot; }
        }
        Rescues.Add(new RescueLine(timeMs, revived, best, best < 0 ? 1f : _state.Poses[best].State / 127f));
    }

    // What may wake sleepers without any player's own noise, newest last: a door opened or broken (its zone wakes), a blast, a sentry's gunfire. The cause of a wake-up nothing
    // names (a client's recording, or a host's target-less wake) is the nearest of these just before it. The reach and the window are not in the game data.
    private readonly List<(uint TimeMs, WakeCause Cause, float X, float Y, float Z)> _wakeSources = new();
    private readonly Dictionary<ulong, ulong> _doorStatus = new();
    private const uint WakeSourceMs = 3000;
    private const float DoorWakeMetres = 25f, BlastWakeMetres = 20f, SentryWakeMetres = 35f;

    private void WakeSource(uint timeMs, WakeCause cause, float x, float y, float z)
    {
        if (TotalsOnly) return;
        _wakeSources.Add((timeMs, cause, x, y, z));
        if (_wakeSources.Count > 256) _wakeSources.RemoveRange(0, 128);
    }

    private WakeCause SourceOf(uint timeMs, int entity)
    {
        if (_state == null || !_state.Place(entity, timeMs, out var x, out var y, out var z, out _, out _, out _, out _)) return WakeCause.Unknown;
        var found = WakeCause.Unknown; var best = float.MaxValue;
        for (var i = _wakeSources.Count - 1; i >= 0; i--)
        {
            var s = _wakeSources[i];
            if (s.TimeMs > timeMs) continue;
            if (timeMs - s.TimeMs > WakeSourceMs) break;
            var reach = s.Cause == WakeCause.Door ? DoorWakeMetres : s.Cause == WakeCause.Explosion ? BlastWakeMetres : SentryWakeMetres;
            var d = MathF.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) + (s.Z - z) * (s.Z - z));
            if (d <= reach && d < best) { best = d; found = s.Cause; }
        }
        return found;
    }

    private bool WokeLately(int entity, uint timeMs) => _wokeAt.TryGetValue(entity, out var at) && timeMs >= at && timeMs - at <= WakeSameMs;

    // A wake-up the host named the cause of: its line in the blame feed and, counted, the player it is put to (the one who woke the enemy, or whose wake-up it passed on).
    private void Waked(uint timeMs, int entity, bool propagated, int propagator, int detection, bool heard, int player, int action, bool count)
    {
        if (player > 3) player = -1;
        var cause = heard ? WakeCause.Noise : propagated || detection == 5 ? WakeCause.Passed
            : detection == 4 ? WakeCause.Damage : detection == 2 ? WakeCause.Sensed : WakeCause.Unknown;
        Woke(timeMs, entity, player, cause, cause == WakeCause.Sensed ? action : 0, propagated ? propagator : -1);
        if (!count) return;
        var who = propagated && propagator >= 0 && _wokenBy.TryGetValue(propagator, out var named) ? named : player;
        if (who < 0) return;
        _wokenBy[entity] = who;
        if (!TotalsOnly) _wakesPlaced.Add((entity, timeMs));
        Count(timeMs, who, StatKind.Woken, 1f);
    }
    // A wake-up line, or one more on the line of the enemy whose propagation passed it on (root: that enemy's entity, -1 none).
    private void Woke(uint timeMs, int entity, int waker, WakeCause cause, int action, int root)
    {
        _wokeAt[entity] = timeMs;
        if (TotalsOnly) return;
        if (cause == WakeCause.Unknown) cause = SourceOf(timeMs, entity);
        if (root >= 0 && _wakeLine.TryGetValue(root, out var line))
        {
            Wakes[line] = Wakes[line] with { Chain = Wakes[line].Chain + 1 };
            _wakeLine[entity] = line;
            return;
        }
        _wakeLine[entity] = Wakes.Count;
        Wakes.Add(new WakeLine(timeMs, entity, _state != null && _state.Enemies.TryGetValue(entity, out var enemy) ? enemy.DataId : _dataOf.GetValueOrDefault(entity), waker, cause, action, 0));
    }

    /// <summary>A text of the game's without its colour and size tags.</summary>
    internal static string Untagged(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "").Trim();

    private const int TerminalWidth = 60;
    private static readonly System.Text.RegularExpressions.Regex Spacing = new(@"<(pos|indent|space)=([0-9.]+)(%|px|em)?>|\t", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A line of a terminal's printout without its colour and size tags, its column tags (<c>&lt;pos&gt;</c>, <c>&lt;indent&gt;</c>,
    /// <c>&lt;space&gt;</c>, tabs) turned into the gap they made: padded to the same share of the line's width, at least two spaces, so the
    /// columns of a list stay apart. Stripping them left "IDITEM TYPESTATUS".</summary>
    internal static string TerminalColumns(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var made = new System.Text.StringBuilder();
            var from = 0;
            foreach (System.Text.RegularExpressions.Match gap in Spacing.Matches(line))
            {
                made.Append(System.Text.RegularExpressions.Regex.Replace(line.Substring(from, gap.Index - from), "<[^>]*>", ""));
                from = gap.Index + gap.Length;
                var want = made.Length + 2;
                if (gap.Groups[2].Success && float.TryParse(gap.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    var unit = gap.Groups[3].Value;
                    var column = unit == "%" ? (int)MathF.Round(value * TerminalWidth / 100f) : unit == "px" ? (int)MathF.Round(value / 10f) : (int)MathF.Round(value * 1.5f);
                    // <pos> is an absolute column; <indent> and <space> are as wide as given from where they stand.
                    want = gap.Groups[1].Value.Equals("pos", StringComparison.OrdinalIgnoreCase) ? Math.Max(want, column) : Math.Max(want, made.Length + column);
                }
                else if (gap.Value == "\t") want = (made.Length / 8 + 1) * 8 < made.Length + 2 ? made.Length + 2 : (made.Length / 8 + 1) * 8;
                made.Append(' ', want - made.Length);
            }
            made.Append(System.Text.RegularExpressions.Regex.Replace(line.Substring(from), "<[^>]*>", ""));
            lines[i] = made.ToString();
        }
        return string.Join("\n", lines).Trim();
    }


    // A state event is a marker only when the object's state is new: keyframe blocks write every state again.
    private void Changed(uint timeMs, ushort type, PayloadReader read, MarkerKind kind, Dictionary<(ushort, ulong), ulong> seen)
    {
        var key = read.U(); var state = read.U();
        if (seen.TryGetValue((type, key), out var before) && before == state) return;
        seen[(type, key)] = state;
        read.U();
        var (x, y, z) = read.Pos();
        Markers.Add(new Marker(timeMs, kind, -1, NearestPlayer(x, y, z)));
    }

    // The player nearest to a place at the moment being read, within NearMetres; -1 when nobody is.
    private int NearestPlayer(float x, float y, float z)
    {
        if (_state == null) return -1;
        var best = -1; var bestSquared = NearMetres * NearMetres;
        for (var slot = 0; slot < 4; slot++)
        {
            ref var pose = ref _state.Poses[slot];
            if (!pose.Present) continue;
            var dx = pose.X - x; var dy = pose.Y - y; var dz = pose.Z - z;
            var squared = dx * dx + dy * dy + dz * dz;
            if (squared <= bestSquared) { bestSquared = squared; best = slot; }
        }
        return best;
    }
}

/// <summary>Picks whom the automatic camera follows: the player with the most going on around them, or a big enemy hunting a player
/// (<see cref="BigEnemy"/>), with a minimum stay and a margin before it leaves the current one, so the camera does not flicker between two.</summary>
internal static class Director
{
    internal const uint WindowBeforeMs = 4000, WindowAfterMs = 1000, MinStayMs = 12000;
    internal const float NearEnemy = 15f, SwitchMargin = 1.3f;

    /// <summary>The score of a big enemy (mini boss class and up) hunting within <see cref="NearEnemy"/> of a player: above a player with a
    /// few enemies round them, below one who is down.</summary>
    internal const float BigEnemy = 7f;

    internal static float Weight(MarkerKind kind) => kind switch
    {
        MarkerKind.Down => 8f,
        MarkerKind.Revive => 6f,
        MarkerKind.Alarm => 5f,
        MarkerKind.Scan => 4f,
        MarkerKind.Reactor => 4f,
        MarkerKind.Kill => 3f,
        MarkerKind.Terminal => 2f,
        MarkerKind.Door => 1f,
        MarkerKind.Chat => 1f,
        _ => 0f,
    };

    /// <summary>Whom to follow at <paramref name="timeMs"/>: a player slot (0 to 3) or, when <paramref name="big"/> tells big enemies by their
    /// data block, an enemy entity (4 and up); given the current one (-1 for none) and when the camera last switched.</summary>
    internal static int Choose(WorldState state, Analysis analysis, uint timeMs, int current, uint lastSwitchMs, Func<ulong, bool>? big = null)
    {
        Span<float> score = stackalloc float[4];
        for (var slot = 0; slot < 4; slot++)
        {
            if (!state.Poses[slot].Present) { score[slot] = -1f; continue; }
            score[slot] = 1f;
            ref var me = ref state.Poses[slot];
            for (var e = 4; e < WorldState.MaxEntities; e++)
            {
                ref var enemy = ref state.Poses[e];
                if (!enemy.Present || EnemyAlerts.Settle((EnemyAlert)(enemy.State & 3), state.EnemyLocomotion[e]) != EnemyAlert.Hunting) continue;
                var dx = enemy.X - me.X; var dy = enemy.Y - me.Y; var dz = enemy.Z - me.Z;
                if (dx * dx + dy * dy + dz * dz <= NearEnemy * NearEnemy) score[slot] += 0.5f;
            }
        }
        foreach (var marker in analysis.Markers)
        {
            var who = marker.Slot >= 0 ? marker.Slot : marker.Near;
            if (who < 0 || marker.TimeMs + WindowBeforeMs < timeMs || marker.TimeMs > timeMs + WindowAfterMs || score[who] < 0f) continue;
            score[who] += Weight(marker.Kind);
        }
        var best = -1; var bestScore = -1f;
        for (var slot = 0; slot < 4; slot++) if (score[slot] >= 0f && (best < 0 || score[slot] > bestScore)) { best = slot; bestScore = score[slot]; }
        if (big != null)
            foreach (var (entity, _) in state.Enemies)
            {
                var s = EnemyScore(state, entity, big);
                if (s > bestScore) { best = entity; bestScore = s; }
            }
        var currentScore = current is >= 0 and < 4 ? score[current] : current >= 4 ? EnemyScore(state, current, big) : -1f;
        if (current < 0 || currentScore < 0f) return best;
        if (best == current || timeMs - lastSwitchMs < MinStayMs) return current;
        return bestScore > currentScore * SwitchMargin ? best : current;
    }

    // A big enemy that hunts within NearEnemy of a player scores BigEnemy; any other enemy is no candidate (-1).
    private static float EnemyScore(WorldState state, int entity, Func<ulong, bool>? big)
    {
        if (big == null || entity is < 4 or >= WorldState.MaxEntities || !state.Enemies.TryGetValue(entity, out var enemy) || !big(enemy.DataId)) return -1f;
        ref var e = ref state.Poses[entity];
        if (!e.Present || EnemyAlerts.Settle((EnemyAlert)(e.State & 3), state.EnemyLocomotion[entity]) != EnemyAlert.Hunting) return -1f;
        for (var slot = 0; slot < 4; slot++)
        {
            ref var p = ref state.Poses[slot];
            if (!p.Present) continue;
            var dx = p.X - e.X; var dy = p.Y - e.Y; var dz = p.Z - e.Z;
            if (dx * dx + dy * dy + dz * dz <= NearEnemy * NearEnemy) return BigEnemy;
        }
        return -1f;
    }
}
