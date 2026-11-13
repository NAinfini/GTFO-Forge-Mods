using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;

namespace ForgeReplay;

/// <summary>What brought a player down: the game's own damage kind (PlayerHurt), a grab by a trap, a tank or a snatcher, or nothing known.</summary>
internal enum DownCause : byte { Unknown, Bullet, Melee, Explosion, Tongue, Projectile, Fall, Other, Grabbed }

/// <summary>A player going down: when, who, why (the damage in the three seconds before; <see cref="Grab"/> the PLOC grab state when held),
/// the enemy and its data block or the player (<see cref="Attacker"/>, -1 none) that did it, how long until they were up
/// (<see cref="DownMs"/>, to the end of the recording when <see cref="Rescued"/> is false) and who got them up.
/// <see cref="Estimated"/>: the cause was worked out by <see cref="ClientInference"/> (no PlayerHurt for that player on this machine).</summary>
internal readonly record struct DownLine(uint TimeMs, int Slot, DownCause Cause, int Grab, ulong EnemyDataId, int Attacker, uint DownMs, bool Rescued, int Reviver, bool Estimated);

internal enum SessionKind : byte { Joined, Left, CheckpointReached, CheckpointReloaded, HostChanged }

internal readonly record struct SessionLine(uint TimeMs, SessionKind Kind, int Slot);

/// <summary>Per weapon (what the player wielded, <see cref="WeaponKey.Explosion"/> for mines and <see cref="WeaponKey.Sentry"/> for a sentry's bullets):
/// rays, hits on enemies, weak spot hits, damage, kills, and the share of rays fired while aiming (only the recorder's own aim is recorded).</summary>
internal sealed class WeaponStat
{
    internal int Slot, Wielded, Shots, Hits, Weak, Backstabs, Kills, AimedShots;
    internal float Damage;
    internal bool Estimated, AimKnown;
}

internal static class WeaponKey { internal const int Explosion = -2, Sentry = -3; }

/// <summary>Per enemy type (its data block id): how many came, died, went away unkilled, the mean seconds from the first hit to the death,
/// the mean distance the hits came from, the health taken off and the damage that went past the last health (waste).</summary>
internal sealed class EnemyTypeStat
{
    internal ulong DataId;
    internal int Appeared, Killed, Escaped, TtkCount, DistCount;
    internal double TtkMs, DistMetres;
    internal float HealthTaken, Overkill;
    internal float MeanTtkSeconds => TtkCount == 0 ? 0f : (float)(TtkMs / TtkCount / 1000.0);
    internal float MeanDistance => DistCount == 0 ? 0f : (float)(DistMetres / DistCount);
}

/// <summary>A zone as the players' PlayerPlace events show it: who came first and when, how long anyone stood in it, who opened the door
/// before it (the last door opened before the first arrival, so worked out, -1 when none).</summary>
internal sealed class ZoneStat
{
    internal int Dimension, Layer, Zone, FirstSlot, OpenedBy = -1;
    internal uint FirstMs, StayMs;
    internal int Occupants; internal uint SinceMs;
}

internal readonly record struct DoorOpen(uint TimeMs, ulong Key, int Slot);
/// <summary>A scan: from the first progress to completion (-1 when never finished), and the slots seen inside.</summary>
internal readonly record struct ScanSpan(ulong Key, uint FromMs, long DoneMs, int Slots);
internal readonly record struct TerminalSpan(ulong Key, int Slot, uint FromMs, uint ToMs);
internal readonly record struct ObjectiveSpan(int Layer, uint FromMs, uint ToMs);

/// <summary>A wave: the game's start and end events (<see cref="Ended"/> false: no end came, it is cut at the next start or <see cref="RunReport.WaveCapMs"/>),
/// the enemies that came into the level and died in its window, the damage players took and the downs in it.</summary>
internal sealed class WaveStat
{
    internal uint StartMs, EndMs; internal ulong WaveId, SettingsId, PopulationId;
    internal bool Ended; internal int Spawned, Killed, Downs; internal float DamageTaken;
}

/// <summary>A series sampled for a curve: at most one point per <see cref="MinGapMs"/>.</summary>
internal sealed class Series
{
    internal readonly List<uint> Times = new(); internal readonly List<short> Values = new();
    internal uint MinGapMs = 1000;
    internal void Add(uint ms, int value) { if (Times.Count > 0 && ms - Times[^1] < MinGapMs) { Values[^1] = (short)value; return; } Times.Add(ms); Values.Add((short)value); }
    internal int At(uint ms) { var v = -1; for (var i = 0; i < Times.Count && Times[i] <= ms; i++) v = Values[i]; return v; }
}

/// <summary>Everything the statistics pages, the list's summary and the export read beyond <see cref="Analysis"/>, worked out offline in the
/// same pass over the file (<see cref="Analysis.Run"/>): friendly fire, downs and their causes, weapons, enemy types, ammunition, supplies,
/// tension, zones, waves, who joined and left, deployables, curves. Nothing here is written while recording. Feed <see cref="Take"/> and
/// <see cref="Sample"/> in time order, then <see cref="Finish"/>. A number that is worked out and not read from the game is marked
/// (<see cref="DownLine.Estimated"/>, <see cref="WeaponStat.Estimated"/>, the <c>*Estimated</c> flags).</summary>
internal sealed class RunReport
{
    internal const uint CauseWindowMs = 3000, SentryMatchMs = 120, WaveCapMs = 90000, TensionStepMs = 1000;
    private const int GrabbedByTrap = 12, GrabbedByTank = 13, GrabbedByPouncer = 16;

    private readonly WorldState _state;
    internal RunReport(WorldState state)
    {
        _state = state;
        for (var s = 0; s < 4; s++) for (var i = 0; i < 3; i++) { Ammo[s, i] = new Series(); _lastAmmo[s, i] = -1; }
    }

    // 1. friendly fire
    /// <summary>Health slot [0] took from slot [1]'s shots (PlayerHurt names the player); exact where <see cref="HurtKnown"/>.</summary>
    internal readonly float[,] FriendlyDamage = new float[4, 4];
    internal readonly int[] FriendlyShots = new int[4];
    /// <summary>True for a slot whose own PlayerHurt events are in the recording: its damage taken and its source are exact; else "—".</summary>
    internal readonly bool[] HurtKnown = new bool[4];
    internal bool FriendlyFireKnown => HurtKnown.Any(k => k);

    // 2/3. downs
    internal List<DownLine> Downs { get; } = new();
    internal readonly uint[] DownMsOf = new uint[4];
    internal readonly int[] RescuedOf = new int[4];
    internal readonly uint[] RescueMsOf = new uint[4];
    internal uint TeamDownMs;

    // 4. weapons, 5. enemy types
    internal Dictionary<(int Slot, int Wielded), WeaponStat> Weapons { get; } = new();
    internal Dictionary<ulong, EnemyTypeStat> Enemies { get; } = new();

    // 6/7. ammunition and supplies
    /// <summary>The three ammunition pools (standard, special, class) of each player as a curve.</summary>
    internal readonly Series[,] Ammo = new Series[4, 3];
    internal readonly int[,] AmmoUsed = new int[4, 3], AmmoGained = new int[4, 3];
    internal readonly int[] AmmoPickups = new int[4];
    /// <summary>Supply events by giver and kind (0 health, 1 ammunition, 2 disinfection), exact.</summary>
    internal readonly int[,] Gave = new int[4, 3], Got = new int[4, 3];
    internal readonly float[] HealedShare = new float[4], InfectionCleared = new float[4];
    internal readonly int[] AmmoRestored = new int[4];

    // 8. tension
    internal List<ushort> Tension { get; } = new();
    internal int PeakTension, SkippedEvents; internal uint PeakTensionMs;

    // 9. zones
    internal Dictionary<(int Dimension, int Layer, int Zone), ZoneStat> Zones { get; } = new();
    internal List<DoorOpen> Doors { get; } = new();
    internal List<ScanSpan> Scans { get; } = new();
    internal List<TerminalSpan> TerminalSpans { get; } = new();
    internal List<ObjectiveSpan> ObjectiveSpans { get; } = new();

    // 10. waves
    internal List<WaveStat> Waves { get; } = new();
    internal int FiercestWave = -1;

    // 11. session
    internal List<SessionLine> Session { get; } = new();

    // 13. deployables
    internal readonly int[] SentriesPlaced = new int[4], MinesPlaced = new int[4], FoamFired = new int[4];
    internal readonly float[] SentryDamage = new float[4];
    internal readonly int[] SentryKills = new int[4];
    internal bool SentryEstimated;

    // B3. resource boxes and packs: the game's box state carries no operator, so who opened one is the nearest player (worked out); who picks a pack up is exact (PlayerKit).
    internal List<(uint TimeMs, ulong Key, int Slot)> BoxesOpened { get; } = new();
    internal readonly int[] BoxesBy = new int[4];
    internal List<(uint TimeMs, int Slot, uint Item)> PackPickups { get; } = new();
    internal readonly int[] PackPickupsBy = new int[4];

    // 14. curves
    internal readonly Series[] Health = { new(), new(), new(), new() }, Infection = { new(), new(), new(), new() }, Stamina = { new(), new(), new(), new() };

    // C7 / C6 inputs
    internal uint DurationMs;

    // ---------------------------------------------------------------- the feed

    // A player's hit on an enemy, for the weapon page's counts; the damage it did is <see cref="Analysis.Damages"/>, settled once there.
    private readonly record struct Hit(uint TimeMs, int Entity, int Slot, int Kind, bool Settled, bool Lethal, bool Weak, bool Behind, int Wielded);
    private readonly record struct Hurt(uint TimeMs, int Slot, int Kind, ulong EnemyDataId, int AttackerPlayer, float Damage);
    private readonly record struct ShotAt(uint TimeMs, int Slot, int Wielded, bool Aimed, int Hit);

    private readonly List<Hit> _hits = new();
    private readonly List<Hurt> _hurts = new();
    private readonly List<ShotAt> _shots = new();
    private readonly List<(uint TimeMs, int Slot, int Locomotion)> _grabs = new();
    private readonly List<(uint TimeMs, int Target, int Source)> _reviveBy = new();
    private readonly List<(uint TimeMs, int Slot, bool Up, bool Revived)> _downEdges = new();
    private readonly List<uint>[] _sentryShots = { new(), new(), new(), new() };
    private readonly Dictionary<ulong, int> _sentryOwner = new();
    private readonly HashSet<ulong> _sentriesSeen = new(), _foamSeen = new(), _minesSeen = new();
    private readonly Dictionary<int, List<(uint SpawnMs, ulong DataId)>> _spawns = new();
    private readonly HashSet<int> _live = new();
    private readonly Dictionary<int, uint> _firstHit = new();
    private readonly Dictionary<(ushort, ulong), ulong> _stateSeen = new();
    private readonly int[] _wielded = { -1, -1, -1, -1 };
    private readonly bool[] _down = new bool[4];
    private readonly bool[] _aiming = new bool[4];
    private readonly bool[] _aimFromEvent = new bool[4];
    private readonly int[,] _lastAmmo = new int[4, 3];
    private readonly (int Dimension, int Layer, int Zone)?[] _in = new (int, int, int)?[4];
    private readonly Dictionary<ulong, (uint From, int Slots, long Done)> _scanning = new();
    private readonly Dictionary<ulong, (int Slot, uint From)> _terminalOpen = new();
    private readonly uint[] _packItem = new uint[4];
    private uint _nextTension;
    private int _wave = -1;
    private bool _anyAiming;

    private ulong DataAt(int entity, uint timeMs)
    {
        if (!_spawns.TryGetValue(entity, out var list)) return 0;
        ulong found = 0;
        foreach (var (spawn, data) in list) { if (spawn > timeMs) break; found = data; }
        return found;
    }

    /// <summary>A position sample (<see cref="Analysis.Run"/> passes every one); the walk and health of the four players, and the tension clock.</summary>
    internal void Sample(uint timeMs, int entity, byte state)
    {
        if (entity < 4)
        {
            var slot = entity;
            Health[slot].Add(timeMs, state);
        }
        if (entity >= 4) Seen(entity, timeMs);
        Tick(timeMs);
    }

    private void Seen(int entity, uint timeMs)
    {
        if (_live.Contains(entity) || !_state.Enemies.TryGetValue(entity, out var enemy)) return;
        _live.Add(entity);
        if (!_spawns.TryGetValue(entity, out var list)) _spawns[entity] = list = new();
        list.Add((timeMs, enemy.DataId));
        Type(enemy.DataId).Appeared++;
        if (ActiveWave(timeMs) is var wave and >= 0) Waves[wave].Spawned++;
    }

    // Once per second of the recording: the number of enemies hunting. The world state is already applied up to the record being fed.
    private void Tick(uint timeMs)
    {
        while (timeMs >= _nextTension)
        {
            var n = 0;
            foreach (var (entity, _) in _state.Enemies)
            {
                ref var p = ref _state.Poses[entity];
                if (p.Present && EnemyAlerts.Settle((EnemyAlert)(p.State & 3), _state.EnemyLocomotion[entity]) == EnemyAlert.Hunting) n++;
            }
            Tension.Add((ushort)Math.Min(n, ushort.MaxValue));
            if (n > PeakTension) { PeakTension = n; PeakTensionMs = _nextTension; }
            _nextTension += TensionStepMs;
        }
    }

    internal void Take(uint timeMs, ushort type, byte[] payload)
    {
        Tick(timeMs);
        try { TakeEvent(timeMs, type, payload); }
        catch (FormatException) { SkippedEvents++; } // an event cut short is skipped, the rest of the run still counts
    }

    private void TakeEvent(uint timeMs, ushort type, byte[] payload)
    {
        var read = PayloadReader.Over(payload);
        // The timeline does not report a spawn: an enemy is seen the first time a sample, a hit or its end names it, when its data block is in the world state.
        if (type is EventKind.EnemyHurt or EventKind.EnemyGone) Seen((int)PayloadReader.Over(payload).U(), timeMs);
        else if (type == EventKind.PlayerHurt) { var peek = PayloadReader.Over(payload); peek.U(); peek.U(); peek.U(); if (peek.U() is var e and > 0) Seen((int)e - 1, timeMs); }
        read = PayloadReader.Over(payload);
        switch (type)
        {
            case EventKind.EnemyHurt:
                {
                    var hurt = EnemyHurtEvent.Read(read);
                    var (entity, kind, attacker, settled, lethal, weak, behind) = (hurt.Entity, hurt.Kind, hurt.Attacker, hurt.Settled, hurt.Lethal, hurt.Weak, hurt.Behind);
                    var (taken, overkill, metres) = (hurt.Taken, hurt.Overkill, hurt.Metres);
                    if (attacker > 3) break;
                    var slot = (int)attacker; var data = DataAt(entity, timeMs);
                    _firstHit.TryAdd(entity, timeMs);
                    _hits.Add(new Hit(timeMs, entity, slot, kind, settled, lethal, weak, behind, kind == 2 ? WeaponKey.Explosion : _wielded[slot]));
                    var t = Type(data);
                    if (settled) { t.HealthTaken += taken; t.Overkill += overkill; }
                    if (metres >= 0f && kind <= 1) { t.DistMetres += metres; t.DistCount++; }
                    break;
                }
            case EventKind.EnemyGone:
                {
                    var entity = (int)read.U(); var killed = read.U() == 1;
                    var data = DataAt(entity, timeMs);
                    var t = Type(data);
                    if (killed)
                    {
                        t.Killed++;
                        if (_firstHit.TryGetValue(entity, out var first)) { t.TtkMs += timeMs - first; t.TtkCount++; }
                        if (ActiveWave(timeMs) is var killWave and >= 0) Waves[killWave].Killed++;
                    }
                    else t.Escaped++;
                    _live.Remove(entity); _firstHit.Remove(entity);
                    break;
                }
            case EventKind.Shot:
                {
                    var slot = (int)read.U() & 3; read.U(); read.Pos(); read.Pos();
                    var landed = read.U() == 1; var what = (int)read.U();
                    _shots.Add(new ShotAt(timeMs, slot, _wielded[slot], _aiming[slot], what));
                    if (what == 3) FriendlyShots[slot]++;
                    break;
                }
            case EventKind.PlayerHurt:
                {
                    var slot = (int)read.U() & 3; var kind = (int)read.U(); var damage = read.U() / 10f; var enemy = read.U();
                    var player = (int)read.U() - 1;
                    HurtKnown[slot] = true;
                    var data = enemy > 0 ? DataAt((int)enemy - 1, timeMs) : 0UL;
                    if (player is >= 0 and <= 3 && player != slot) FriendlyDamage[slot, player] += damage;
                    _hurts.Add(new Hurt(timeMs, slot, kind, data, player is >= 0 and <= 3 && player != slot ? player : -1, damage));
                    if (ActiveWave(timeMs) is var hurtWave and >= 0) Waves[hurtWave].DamageTaken += damage;
                    break;
                }
            case EventKind.HostHurt:
                {
                    // U slot, U kind, U damage x10, U attacking player + 1, U attacking enemy entity + 1, U age: a hit the host settled on its own player
                    // or a bot, which a client's own recording has no PlayerHurt for. The amount of the player's damage is already in PlayerHealth.
                    var slot = (int)read.U() & 3; var kind = (int)read.U(); var damage = read.U() / 10f;
                    var player = (int)read.U() - 1; var enemy = (int)read.U() - 1;
                    var age = Math.Min(read.U(), timeMs);
                    var at = timeMs - (uint)age;
                    HurtKnown[slot] = true;
                    var data = enemy >= 0 ? DataAt(enemy, at) : 0UL;
                    if (player is >= 0 and <= 3 && player != slot) FriendlyDamage[slot, player] += damage;
                    _hurts.Add(new Hurt(at, slot, kind, data, player is >= 0 and <= 3 && player != slot ? player : -1, damage));
                    if (ActiveWave(at) is var hostHurtWave and >= 0) Waves[hostHurtWave].DamageTaken += damage;
                    break;
                }
            case EventKind.PlayerStatus:
                {
                    var slot = (int)read.U() & 3; var loco = (int)read.U();
                    _wielded[slot] = (int)read.U();
                    var infection = (int)read.U();
                    var risen = false;
                    for (var i = 0; i < 3; i++)
                    {
                        var now = (int)read.U(); var was = _lastAmmo[slot, i];
                        if (was >= 0 && now < was) AmmoUsed[slot, i] += was - now;
                        else if (was >= 0 && now > was) { AmmoGained[slot, i] += now - was; risen = true; }
                        _lastAmmo[slot, i] = now;
                        Ammo[slot, i].Add(timeMs, now);
                    }
                    var down = read.U() == 1;
                    if (risen) AmmoPickups[slot]++;
                    Infection[slot].Add(timeMs, infection);
                    if (loco is GrabbedByTrap or GrabbedByTank or GrabbedByPouncer) _grabs.Add((timeMs, slot, loco));
                    if (down != _down[slot])
                    {
                        _down[slot] = down;
                        _downEdges.Add((timeMs, slot, !down, false));
                        if (down && ActiveWave(timeMs) >= 0) Waves[ActiveWave(timeMs)].Downs++;
                    }
                    break;
                }
            case EventKind.Revive:
                _downEdges.Add((timeMs, (int)read.U() & 3, true, true));
                break;
            case EventKind.ReviveBy:
                _reviveBy.Add((timeMs, (int)read.U(), (int)read.U()));
                break;
            case EventKind.PlayerGun:
                {
                    var slot = (int)read.U() & 3; read.U(); read.U();
                    var aim = read.U() == 1;
                    // Before event 108 the 10 Hz gun sample was the only aim there was; a recording of this version holds 0 there and the
                    // transition in 108, which is read below and is never overwritten by a gun sample.
                    if (!_aimFromEvent[slot]) { _aiming[slot] = aim; _anyAiming = true; }
                    var stamina = (int)read.U() - 1;
                    if (stamina >= 0) Stamina[slot].Add(timeMs, stamina);
                    break;
                }
            case EventKind.PlayerAim:
                {
                    var slot = (int)read.U() & 3;
                    _aiming[slot] = read.U() == 1; _anyAiming = true; _aimFromEvent[slot] = true;
                    break;
                }
            case EventKind.Supply:
                {
                    var giver = (int)read.U(); var receiver = (int)read.U(); var kind = (int)read.U();
                    if (giver > 3 || receiver > 3 || kind > 2) break;
                    Gave[giver, kind]++; if (giver != receiver) Got[receiver, kind]++;
                    break;
                }
            case EventKind.HostSupply:
                {
                    // U giver, U receiver, U kind, U age: a gift the host's machine made (its own player or a bot), which a client's own Supply events do not hold.
                    var giver = (int)read.U(); var receiver = (int)read.U(); var kind = (int)read.U();
                    read.U();
                    if (giver > 3 || receiver > 3 || kind > 2) break;
                    Gave[giver, kind]++; if (giver != receiver) Got[receiver, kind]++;
                    break;
                }
            case EventKind.PlayerKit:
                {
                    var slot = (int)read.U() & 3;
                    var kit = PlayerKit.Read(read);
                    if (kit.PackItem != 0 && kit.PackItem != _packItem[slot]) { PackPickups.Add((timeMs, slot, kit.PackItem)); PackPickupsBy[slot]++; }
                    _packItem[slot] = kit.PackItem;
                    break;
                }
            case EventKind.Door:
                {
                    var key = read.U(); var state = read.U(); read.U(); read.Pos(); var who = (int)read.U() - 1;
                    if (state is 10 or 16 && _stateSeen.TryAdd((type, key), state)) Doors.Add(new DoorOpen(timeMs, key, who is >= 0 and <= 3 ? who : -1));
                    break;
                }
            case EventKind.Scan:
                {
                    var key = read.U(); read.U(); var extra = read.U();
                    var percent = (int)(extra & 0xFF); var slots = (int)((extra >> 8) & 0xF);
                    if (percent <= 0) break;
                    if (!_scanning.TryGetValue(key, out var s)) s = (timeMs, 0, -1);
                    s.Slots |= slots;
                    if (percent >= 100 && s.Done < 0) s.Done = timeMs;
                    _scanning[key] = s;
                    break;
                }
            case EventKind.TerminalState:
                {
                    var key = read.U(); read.U(); var user = (int)read.U() - 1;
                    if (_terminalOpen.TryGetValue(key, out var open) && open.Slot != user)
                    {
                        TerminalSpans.Add(new TerminalSpan(key, open.Slot, open.From, timeMs));
                        _terminalOpen.Remove(key);
                    }
                    if (user is >= 0 and <= 3 && !_terminalOpen.ContainsKey(key)) _terminalOpen[key] = (user, timeMs);
                    break;
                }
            case EventKind.Wave:
                {
                    var started = read.U() == 1; var id = read.U();
                    if (started)
                    {
                        CloseWave(timeMs, false);
                        var w = new WaveStat { StartMs = timeMs, WaveId = id };
                        w.SettingsId = read.U(); w.PopulationId = read.U();
                        Waves.Add(w); _wave = Waves.Count - 1;
                    }
                    else CloseWave(timeMs, true);
                    break;
                }
            case EventKind.PlayerPlace:
                {
                    var slot = (int)read.U() & 3; var dimension = (int)read.U(); var zone = (int)read.U() - 1; var layer = (int)read.U();
                    Leave(slot, timeMs);
                    if (zone < 0) { _in[slot] = null; break; }
                    var key = (dimension, layer, zone);
                    _in[slot] = key;
                    if (!Zones.TryGetValue(key, out var z))
                    {
                        Zones[key] = z = new ZoneStat { Dimension = dimension, Layer = layer, Zone = zone, FirstSlot = slot, FirstMs = timeMs };
                        for (var i = Doors.Count - 1; i >= 0; i--)
                            if (Doors[i].Slot >= 0 && timeMs - Doors[i].TimeMs <= 120000 && Doors[i].TimeMs <= timeMs) { z.OpenedBy = Doors[i].Slot; break; }
                    }
                    if (z.Occupants++ == 0) z.SinceMs = timeMs;
                    break;
                }
            case EventKind.ResourceBox:
                {
                    var key = read.U(); var state = read.U(); read.U(); var (x, y, z) = read.Pos();
                    // A box stands in another state first (the game writes it when the level is built), so "first seen" would never count it: only the change to open counts.
                    var opened = state == 3 && (!_stateSeen.TryGetValue((type, key), out var boxWas) || boxWas != 3);
                    _stateSeen[(type, key)] = state;
                    if (!opened) break;
                    var who = Nearest(x, y, z);
                    BoxesOpened.Add((timeMs, key, who));
                    if (who >= 0) BoxesBy[who]++;
                    break;
                }
            case EventKind.Presence:
                {
                    var slot = (int)read.U();
                    if (slot <= 3) Session.Add(new SessionLine(timeMs, read.U() == 1 ? SessionKind.Joined : SessionKind.Left, slot));
                    break;
                }
            case EventKind.Checkpoint: Session.Add(new SessionLine(timeMs, read.U() == 1 ? SessionKind.CheckpointReloaded : SessionKind.CheckpointReached, -1)); break;
            case EventKind.HostChanged: Session.Add(new SessionLine(timeMs, SessionKind.HostChanged, (int)read.U())); break;
            case EventKind.Sentry:
                {
                    var key = read.U(); var state = read.U(); var owner = (int)read.U() - 1;
                    if (owner is < 0 or > 3) break;
                    _sentryOwner[key] = owner;
                    if (state == 0 && _sentriesSeen.Add(key)) SentriesPlaced[owner]++;
                    break;
                }
            case EventKind.SentryShot:
                {
                    read.U(); read.Pos(); var key = read.U();
                    if (_sentryOwner.TryGetValue(key, out var owner)) _sentryShots[owner].Add(timeMs);
                    break;
                }
            case EventKind.Mine:
                {
                    var key = read.U(); var state = read.U(); var owner = (int)read.U() - 1;
                    if (state == 0 && owner is >= 0 and <= 3 && _minesSeen.Add(key)) MinesPlaced[owner]++;
                    break;
                }
            case EventKind.Foam:
                {
                    var key = read.U(); var phase = read.U();
                    // layout: phase, volume, Pos, Pos, expand, scale, strength, allow splat, owner + 1
                    read.U(); read.Pos(); read.Pos(); read.U(); read.U(); read.U(); read.U();
                    var owner = (int)read.U() - 1;
                    if (phase == 0 && owner is >= 0 and <= 3 && _foamSeen.Add(key)) FoamFired[owner]++;
                    break;
                }
        }
    }

    // The wave a moment belongs to: the one open, and not past the cap when no end came.
    private int ActiveWave(uint timeMs) => _wave >= 0 && timeMs - Waves[_wave].StartMs <= WaveCapMs ? _wave : -1;

    private EnemyTypeStat Type(ulong data)
    {
        if (!Enemies.TryGetValue(data, out var t)) Enemies[data] = t = new EnemyTypeStat { DataId = data };
        return t;
    }

    private void CloseWave(uint timeMs, bool ended)
    {
        if (_wave < 0) return;
        var w = Waves[_wave];
        w.EndMs = timeMs; w.Ended = ended; _wave = -1;
    }

    private void Leave(int slot, uint timeMs)
    {
        if (_in[slot] is not { } key || !Zones.TryGetValue(key, out var z)) return;
        if (--z.Occupants == 0) z.StayMs += timeMs - z.SinceMs;
        _in[slot] = null;
    }

    private int Nearest(float x, float y, float z)
    {
        var best = -1; var bestSquared = Analysis.NearMetres * Analysis.NearMetres;
        for (var slot = 0; slot < 4; slot++)
        {
            ref var p = ref _state.Poses[slot];
            if (!p.Present) continue;
            var dx = p.X - x; var dy = p.Y - y; var dz = p.Z - z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d <= bestSquared) { bestSquared = d; best = slot; }
        }
        return best;
    }

    // ---------------------------------------------------------------- the end of the pass

    /// <summary>Closes what is open and works out what needs the whole run: downs with their causes, the weapons and the sentry split.</summary>
    internal void Finish(Analysis analysis, ClientInference? inferred, uint durationMs)
    {
        DurationMs = durationMs;
        Tick(durationMs);
        for (var slot = 0; slot < 4; slot++)
        {
            Leave(slot, durationMs);
        }
        foreach (var z in Zones.Values) if (z.Occupants > 0) { z.StayMs += durationMs - z.SinceMs; z.Occupants = 0; }
        foreach (var (key, s) in _scanning) Scans.Add(new ScanSpan(key, s.From, s.Done, s.Slots));
        foreach (var (key, open) in _terminalOpen) TerminalSpans.Add(new TerminalSpan(key, open.Slot, open.From, durationMs));
        TerminalSpans.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));
        Scans.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));
        CloseWave(Math.Min(durationMs, Waves.Count > 0 ? Waves[^1].StartMs + WaveCapMs : durationMs), false);
        for (var i = 0; i < Waves.Count; i++)
        {
            var w = Waves[i];
            if (w.Ended) continue;
            var cap = w.StartMs + WaveCapMs;
            w.EndMs = Math.Min(Math.Min(cap, durationMs), i + 1 < Waves.Count ? Waves[i + 1].StartMs : durationMs);
        }
        for (var i = 0; i < Waves.Count; i++) if (FiercestWave < 0 || Waves[i].Spawned > Waves[FiercestWave].Spawned) FiercestWave = i;
        ObjectiveSpansOf(analysis);
        DownsOf(inferred, durationMs);
        WeaponsOf(analysis);
        if (inferred != null)
            foreach (var p in inferred.Packs)
            {
                HealedShare[p.Receiver & 3] += p.HealthFraction; AmmoRestored[p.Receiver & 3] += p.AmmoGained; InfectionCleared[p.Receiver & 3] += p.InfectionRemoved;
            }
    }

    private void ObjectiveSpansOf(Analysis analysis)
    {
        var from = new long[] { -1, -1, -1 };
        foreach (var o in analysis.Objectives)
        {
            if (o.Layer is < 0 or > 2) continue;
            var running = o.Status is > 0 and < 40;
            if (running && from[o.Layer] < 0) from[o.Layer] = o.TimeMs;
            else if (!running && from[o.Layer] >= 0) { ObjectiveSpans.Add(new ObjectiveSpan(o.Layer, (uint)from[o.Layer], o.TimeMs)); from[o.Layer] = -1; }
        }
        for (var layer = 0; layer < 3; layer++) if (from[layer] >= 0 && DurationMs > from[layer]) ObjectiveSpans.Add(new ObjectiveSpan(layer, (uint)from[layer], DurationMs));
    }

    private void DownsOf(ClientInference? inferred, uint durationMs)
    {
        // Down edges to intervals: down at one edge, up (revived or not) at the next of the same slot.
        var open = new long[] { -1, -1, -1, -1 };
        foreach (var (time, slot, up, revived) in _downEdges.OrderBy(e => e.TimeMs))
        {
            if (!up) { if (open[slot] < 0) open[slot] = time; continue; }
            if (open[slot] < 0)
            {
                // A Revive event arrives just after the status edge: it marks the interval just closed as rescued.
                if (revived) MarkRescued(slot, time);
                continue;
            }
            AddDown(slot, (uint)open[slot], time, revived || Revived(slot, time), inferred);
            open[slot] = -1;
        }
        for (var slot = 0; slot < 4; slot++) if (open[slot] >= 0) AddDown(slot, (uint)open[slot], durationMs, false, inferred);
        Downs.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        for (var slot = 0; slot < 4; slot++)
        {
            foreach (var d in Downs) if (d.Slot == slot) { DownMsOf[slot] += d.DownMs; if (d.Rescued) { RescuedOf[slot]++; RescueMsOf[slot] += d.DownMs; } }
            TeamDownMs += DownMsOf[slot];
        }
    }

    private bool Revived(int slot, uint upMs) => _downEdges.Exists(e => e.Revived && e.Slot == slot && e.TimeMs >= upMs && e.TimeMs - upMs <= 500);

    private void MarkRescued(int slot, uint reviveMs)
    {
        for (var i = Downs.Count - 1; i >= 0; i--)
            if (Downs[i].Slot == slot) { if (!Downs[i].Rescued && reviveMs - (Downs[i].TimeMs + Downs[i].DownMs) <= 500) Downs[i] = Downs[i] with { Rescued = true }; return; }
    }

    private void AddDown(int slot, uint fromMs, uint toMs, bool rescued, ClientInference? inferred)
    {
        var cause = DownCause.Unknown; var data = 0UL; var attacker = -1; var estimated = false; var grab = 0;
        var from = fromMs > CauseWindowMs ? fromMs - CauseWindowMs : 0;
        if (HurtKnown[slot])
        {
            Hurt best = default; var found = false;
            foreach (var h in _hurts)
                if (h.Slot == slot && h.TimeMs >= from && h.TimeMs <= fromMs && (!found || h.TimeMs >= best.TimeMs)) { best = h; found = true; }
            if (found)
            {
                cause = best.AttackerPlayer >= 0 ? DownCause.Bullet : best.Kind switch { 0 => DownCause.Bullet, 1 => DownCause.Melee, 2 => DownCause.Explosion, 3 => DownCause.Tongue, 4 => DownCause.Projectile, 5 => DownCause.Fall, _ => DownCause.Other };
                data = best.EnemyDataId; attacker = best.AttackerPlayer;
            }
        }
        else if (inferred != null)
        {
            InferredHurt best = default; var found = false;
            foreach (var h in inferred.Hurts)
                if (h.Slot == slot && h.TimeMs >= from && h.TimeMs <= fromMs && (!found || h.Fraction >= best.Fraction)) { best = h; found = true; }
            if (found)
            {
                estimated = true;
                cause = best.Source switch { HurtSource.EnemyAttack => DownCause.Melee, HurtSource.Tongue => DownCause.Tongue, HurtSource.Projectile => DownCause.Projectile, HurtSource.Explosion => DownCause.Explosion, _ => DownCause.Unknown };
                data = best.Entity >= 0 ? DataAt(best.Entity, best.TimeMs) : 0UL;
            }
        }
        foreach (var g in _grabs) if (g.Slot == slot && g.TimeMs + CauseWindowMs >= fromMs && g.TimeMs <= fromMs) { grab = g.Locomotion; }
        if (grab != 0) cause = DownCause.Grabbed;
        var reviver = -1;
        if (rescued)
            foreach (var r in _reviveBy) if (r.Target == slot && r.TimeMs + 500 >= toMs && r.TimeMs <= toMs + 500) reviver = r.Source is >= 0 and <= 3 ? r.Source : -1;
        Downs.Add(new DownLine(fromMs, slot, cause, grab, data, attacker, toMs - fromMs, rescued, reviver, estimated));
    }

    private void WeaponsOf(Analysis analysis)
    {
        WeaponStat Of(int slot, int wielded)
        {
            if (!Weapons.TryGetValue((slot, wielded), out var w)) Weapons[(slot, wielded)] = w = new WeaponStat { Slot = slot, Wielded = wielded };
            return w;
        }
        var recorder = analysis.RecorderSlot;
        var shotTimes = new List<uint>[4]; for (var i = 0; i < 4; i++) shotTimes[i] = new List<uint>();
        foreach (var s in _shots)
        {
            shotTimes[s.Slot].Add(s.TimeMs);
            var w = Of(s.Slot, s.Wielded);
            w.Shots++;
            if (s.Hit == 2) w.Hits++;
            if (s.Slot == recorder && _anyAiming) { w.AimKnown = true; if (s.Aimed) w.AimedShots++; }
        }
        // A sentry's bullets name its owner too. A bullet hit is a sentry's when one of the owner's sentries fired within SentryMatchMs of it
        // and the owner fired no ray of their own then: worked out, so shown as an estimate.
        bool SentryBullet(int slot, uint timeMs) => NearIn(_sentryShots[slot], timeMs) && !NearIn(shotTimes[slot], timeMs);
        var sentryLethal = new Dictionary<int, List<uint>>();
        foreach (var h in _hits)
        {
            var wielded = h.Wielded;
            if (h.Kind == 0 && SentryBullet(h.Slot, h.TimeMs))
            {
                wielded = WeaponKey.Sentry;
                if (h.Lethal && h.Settled) { if (!sentryLethal.TryGetValue(h.Entity, out var times)) sentryLethal[h.Entity] = times = new(); times.Add(h.TimeMs); }
                Of(h.Slot, wielded).Hits++;
            }
            var w = Of(h.Slot, wielded);
            if (wielded == WeaponKey.Sentry) { w.Estimated = true; SentryEstimated = true; }
            else if (h.Kind <= 1) { if (h.Weak) w.Weak++; if (h.Behind) w.Backstabs++; }
        }
        // The damage: the settlement the statistics rows are made from (host, synced health, own readings capped by health, worked out from rays), not a second sum of the hits.
        foreach (var d in analysis.Damages)
        {
            var wielded = !d.WeaponKnown && d.Kind == 0 && SentryBullet(d.Slot, d.TimeMs) ? WeaponKey.Sentry : d.Wielded;
            var w = Of(d.Slot, wielded);
            w.Damage += d.Amount;
            if (d.Estimated) w.Estimated = true;
            if (wielded == WeaponKey.Sentry) { w.Estimated = true; SentryEstimated = true; SentryDamage[d.Slot] += d.Amount; }
        }
        foreach (var k in analysis.Kills)
        {
            var slot = k.Killer & 3;
            var wielded = k.Kind == 2 ? WeaponKey.Explosion : k.Kind == HitMatch.SentryKind ? WeaponKey.Sentry : k.Wielded;
            if (k.Kind == HitMatch.SentryKind) SentryKills[slot]++;
            if (k.Kind == 0 && sentryLethal.TryGetValue(k.Entity, out var times) && times.Exists(t => (long)k.TimeMs - t is >= -50 and <= 50)) { SentryKills[slot]++; wielded = WeaponKey.Sentry; }
            var w = Of(slot, wielded);
            w.Kills++;
            if (!k.Exact || wielded == WeaponKey.Sentry) w.Estimated = true;
        }
    }
    private static bool NearIn(List<uint> sorted, uint t)
    {
        var lo = 0; var hi = sorted.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] + SentryMatchMs < t) lo = mid + 1; else if (sorted[mid] > t + SentryMatchMs) hi = mid - 1; else return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- derived values

    /// <summary>The most dangerous moment: the second with the most enemies hunting; false when nothing ever hunted.</summary>
    internal bool Danger(out uint timeMs, out int hunters) { timeMs = PeakTensionMs; hunters = PeakTension; return PeakTension > 0; }

    /// <summary>The weapon rows of one player, most damage first.</summary>
    internal List<WeaponStat> WeaponsOfSlot(int slot) => Weapons.Values.Where(w => w.Slot == slot).OrderByDescending(w => w.Damage).ToList();

    internal List<EnemyTypeStat> EnemyTypes() => Enemies.Values.Where(t => t.DataId != 0).OrderByDescending(t => t.Killed + t.Escaped).ToList();

    /// <summary>The enemy types with every data block that players call the same thing (a Striker that hatched in a wave and one that slept, a Giant) as one row: counts and sums added, the row's
    /// <see cref="EnemyTypeStat.DataId"/> that of the block that appeared most. <paramref name="name"/> is what a data block id is called on screen.</summary>
    internal List<EnemyTypeStat> EnemyTypes(Func<ulong, string> name) => EnemyTypes().GroupBy(t => name(t.DataId)).Select(g =>
    {
        var sum = new EnemyTypeStat { DataId = g.OrderByDescending(t => t.Appeared).First().DataId };
        foreach (var t in g)
        {
            sum.Appeared += t.Appeared; sum.Killed += t.Killed; sum.Escaped += t.Escaped; sum.TtkCount += t.TtkCount; sum.DistCount += t.DistCount;
            sum.TtkMs += t.TtkMs; sum.DistMetres += t.DistMetres; sum.HealthTaken += t.HealthTaken; sum.Overkill += t.Overkill;
        }
        return sum;
    }).OrderByDescending(t => t.Killed + t.Escaped).ToList();
}
