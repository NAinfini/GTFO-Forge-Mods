using System;
using System.Collections.Generic;

namespace ForgeReplay;

internal enum HurtSource : byte { Unknown, EnemyAttack, Tongue, Projectile, Explosion }

/// <summary>A player's health went down: the share of full health lost (the sample's health byte is 0..127 of the maximum) and the nearest attack event in time and place.</summary>
internal readonly record struct InferredHurt(uint TimeMs, int Slot, float Fraction, HurtSource Source, int Entity);

/// <summary>An enemy's alert state left asleep, and what woke it: <see cref="NoiseSlot"/> is the player who made the noise, -1 when nothing a
/// player did reached the enemy, the nearest noise was not a player's, or the nearest ones do not agree (<see cref="Ambiguous"/>).</summary>
internal readonly record struct InferredWake(uint TimeMs, int Entity, int NoiseSlot, bool Ambiguous);

internal readonly record struct InferredMine(uint TimeMs, ulong Key, int Owner);

/// <summary>A resource pack used: who used it, who got it (the same player for a self-use) and what changed for the receiver.</summary>
internal readonly record struct InferredPack(uint TimeMs, int User, int Receiver, float HealthFraction, int AmmoGained, int InfectionRemoved);

/// <summary>A tongue that missed. <see cref="TargetRecorded"/>: the game named whom it went for; otherwise the nearest player is assumed.</summary>
internal readonly record struct InferredDodge(uint TimeMs, int Slot, int Entity, bool TargetRecorded = false);

internal readonly record struct InferredRevive(uint TimeMs, int Slot, int Reviver);

/// <summary>The rows of PLAN 5.6 that a recording can fill from what every machine sees: damage taken from the drop of a player's health,
/// what woke an enemy (a recorded noise, or a player action rebuilt by <see cref="NoiseRebuild"/> inside the enemy's hearing), who laid a
/// mine, who used a pack on whom, a tongue that missed, who revived. It never reads the host-only events (PlayerHurt, Pack, Wake,
/// EnemyHurt), so running it on a host's recording gives the error of each inference against the host's exact values. Every result is an
/// estimate and is shown as one. Feed it in time order, then call <see cref="Finish"/>: a health drop and a wake-up are settled once the
/// events just after them are known.</summary>
internal sealed class ClientInference
{
    // How long before a health drop an attack may have started, and how soon after it still counts.
    internal const uint HurtBeforeMs = 1500, HurtAfterMs = 100, WakeBeforeMs = 3000, WakeAfterMs = 200, WakeTieMs = 250, UseWindowMs = 3000, MineWindowMs = 6000;
    internal const float AttackReach = 3f, ExplosionReach = 8f, ProjectileReach = 15f, MineReach = 6f, ReviveReach = 3f, TongueReach = 3f;
    // Health bytes a rise must exceed to count as a pack (one byte is 0.8% of full health).
    internal const int PackHealthBytes = 3;

    // How far an enemy type hears a player, by its enemy data block id; null where the game data is not at hand (the native side reads it
    // from EnemyDetectionDataBlock). Without it, only recorded noises explain a wake-up.
    private readonly Func<ulong, Hearing?>? _hearingOf;

    // The locomotion states (PlayerLocomotion.PLOC_State) and interaction types (pGenericInteractAnimation.TypeEnum) read here.
    private const int Downed = 7, GrabbedByTrap = 12, GrabbedByTank = 13, GrabbedByPouncer = 16;

    /// <summary>Whether a recorded locomotion state is one of the game's grabbed states (trap, tank, snatcher).</summary>
    internal static bool Grabbed(int locomotion) => locomotion is GrabbedByTrap or GrabbedByTank or GrabbedByPouncer;
    private const int GiveResource = 5, ConsumeResource = 6, PlaceTripmine = 0;
    private const int Sleeping = 1, Awake = 2; // EnemyAlert (Off, Sleeping, Awake, Hunting), the low two bits of an enemy's sample state

    private readonly WorldState _state;
    private readonly NoiseRebuild _rebuild;
    private readonly int[] _health = { -1, -1, -1, -1 };
    private readonly uint[] _lastDropMs = new uint[4];
    private readonly uint[] _grabbedMs = new uint[4];
    // The last moment each player was seen down (0 never; the lowest bit is set so a moment 0 is told from none), and how long after it a rise of health is the revive's.
    private readonly uint[] _downedMs = new uint[4];
    private const uint ReviveHealthMs = 4000;
    private readonly int[] _infection = { -1, -1, -1, -1 };
    private readonly int[,] _ammo = { { -1, -1, -1 }, { -1, -1, -1 }, { -1, -1, -1 }, { -1, -1, -1 } };
    private readonly bool[] _healthSeen = new bool[4];
    private readonly Dictionary<int, int> _alert = new();
    private readonly HashSet<ulong> _mines = new();

    private readonly record struct Cause(uint TimeMs, HurtSource Source, int Entity, float X, float Y, float Z);
    private readonly record struct Noise(uint TimeMs, int Slot, float X, float Y, float Z, float Radius);
    private readonly record struct Use(uint TimeMs, int User, int Kind);
    private readonly record struct Place(uint TimeMs, int Slot);
    private readonly record struct Tongue(uint TimeMs, int Entity, float X, float Y, float Z, int Target, bool TargetRecorded);
    private readonly record struct Drop(uint TimeMs, int Slot, float Fraction, bool Placed, float X, float Y, float Z);
    private readonly record struct Wakeup(uint TimeMs, int Entity, bool Placed, float X, float Y, float Z, Hearing? Hearing, int Runners);

    private readonly List<Cause> _causes = new();
    private readonly List<Noise> _noises = new();
    private readonly List<Use> _uses = new();
    private readonly List<Place> _places = new();
    private readonly Dictionary<int, Tongue> _tongues = new();
    private readonly List<Drop> _drops = new();
    private readonly List<Wakeup> _wakeups = new();
    private readonly List<(uint TimeMs, int Slot)> _candidates = new();

    internal List<InferredHurt> Hurts { get; } = new();
    internal List<InferredWake> Wakes { get; } = new();
    internal List<InferredMine> Mines { get; } = new();
    internal List<InferredPack> Packs { get; } = new();
    internal List<InferredDodge> Dodges { get; } = new();
    internal List<InferredRevive> Revives { get; } = new();

    /// <summary>The player actions rebuilt as noise (PLAN 5.6, the noise row).</summary>
    internal IReadOnlyList<PlayerAction> Actions => _rebuild.Actions;

    internal ClientInference(WorldState state, Func<ulong, Hearing?>? hearingOf = null) { _state = state; _rebuild = new NoiseRebuild(state); _hearingOf = hearingOf; }

    // Every list below is appended at the time of the record being fed. The feed is in time order but for a few milliseconds (a sample's
    // time is rounded; 1 ms back in the 11-06 recordings): _slackMs is the furthest any record came before the latest one fed, so no entry
    // is more than that earlier than one before it in its list. A health drop or wake-up looks at the slice of its window widened by it.
    private uint _maxMs, _slackMs;
    private void At(uint timeMs) { if (timeMs < _maxMs) _slackMs = Math.Max(_slackMs, _maxMs - timeMs); else _maxMs = timeMs; }

    /// <summary>The entries a settled health drop or wake-up looked at; the core test sets it against a scan of every entry.</summary>
    internal long Checked { get; private set; }

    /// <summary>True when the recording holds a health sample of the player, so the damage it took can be read from its health.</summary>
    internal bool HealthSeen(int slot) => _healthSeen[slot & 3];

    /// <summary>Damage taken by a player as a share of full health, summed over the recording; multiply by the full health for game units.</summary>
    internal float TakenFraction(int slot)
    {
        var sum = 0f;
        foreach (var hurt in Hurts) if (hurt.Slot == (slot & 3)) sum += hurt.Fraction;
        return sum;
    }

    /// <summary>A position sample (<paramref name="state"/> is a player's health byte or an enemy's alert and health).</summary>
    internal void Sample(uint timeMs, int entity, byte state)
    {
        At(timeMs);
        if (entity < 4)
        {
            var before = _health[entity];
            _health[entity] = state;
            _healthSeen[entity] = true;
            if (before < 0) return;
            if (state < before)
            {
                var placed = Where(entity, out var px, out var py, out var pz);
                _drops.Add(new Drop(timeMs, entity, (before - state) / 127f, placed, px, py, pz));
                _lastDropMs[entity] = timeMs;
            }
            // A player getting up from down comes back with health: a revive, not a pack (it would take the use of an ammunition or infection pack that is still to show).
            else if (state - before >= PackHealthBytes && !(_downedMs[entity] != 0 && timeMs - _downedMs[entity] <= ReviveHealthMs)) Gained(timeMs, entity, (state - before) / 127f, 0, 0);
            return;
        }
        var alert = state & 3;
        if (_alert.TryGetValue(entity, out var was) && was == Sleeping && alert >= Awake) Woke(timeMs, entity);
        _alert[entity] = alert;
    }

    /// <summary>One event, in time order (events a client does not have are ignored).</summary>
    internal void Take(uint timeMs, ushort type, byte[] payload)
    {
        At(timeMs);
        _rebuild.Take(timeMs, type, new PayloadReader(payload));
        var read = new PayloadReader(payload);
        switch (type)
        {
            case EventKind.EnemyGone:
                {
                    // The number is reused by the next enemy in the slot: what the dead one was doing is not the new one's.
                    var entity = (int)read.U();
                    _alert.Remove(entity); _tongues.Remove(entity);
                    break;
                }
            case EventKind.EnemyAttack:
                {
                    var entity = (int)read.U(); read.U(); read.U();
                    var (x, y, z) = read.Pos();
                    _causes.Add(new Cause(timeMs, HurtSource.EnemyAttack, entity, x, y, z));
                    break;
                }
            case EventKind.Tongue:
                {
                    var entity = (int)read.U(); var phase = read.U();
                    var (x, y, z) = read.Pos();
                    if (phase == 0)
                    {
                        _causes.Add(new Cause(timeMs, HurtSource.Tongue, entity, x, y, z));
                        _tongues[entity] = new Tongue(timeMs, entity, x, y, z, (int)read.U() - 1, true);
                    }
                    else if (phase == 1) Retracted(timeMs, entity); // 2 is the launch, before it is out
                    break;
                }
            case EventKind.Projectile:
                {
                    if (read.U() != 0) break;
                    read.U();
                    var (x, y, z) = read.Pos();
                    _causes.Add(new Cause(timeMs, HurtSource.Projectile, -1, x, y, z));
                    break;
                }
            case EventKind.Explosion:
                {
                    read.U();
                    var (x, y, z) = read.Pos();
                    _causes.Add(new Cause(timeMs, HurtSource.Explosion, -1, x, y, z));
                    break;
                }
            case EventKind.Noise:
                {
                    var slot = read.U(); read.U();
                    var (x, y, z) = read.Pos();
                    _noises.Add(new Noise(timeMs, slot > 3 ? -1 : (int)slot, x, y, z, read.U() / 10f));
                    break;
                }
            case EventKind.Interact:
                {
                    var slot = (int)read.U() & 3; var kind = (int)read.U();
                    if (kind == PlaceTripmine) _places.Add(new Place(timeMs, slot));
                    else if (kind is GiveResource or ConsumeResource) _uses.Add(new Use(timeMs, slot, kind));
                    break;
                }
            case EventKind.Mine:
                {
                    var key = read.U(); var state = read.U(); read.U();
                    var (x, y, z) = read.Pos();
                    if (state == 0 && _mines.Add(key)) Mines.Add(new InferredMine(timeMs, key, MineOwner(timeMs, x, y, z)));
                    break;
                }
            case EventKind.Revive:
                {
                    var slot = (int)read.U() & 3;
                    Revives.Add(new InferredRevive(timeMs, slot, Reviver(slot)));
                    break;
                }
            case EventKind.PlayerStatus:
                {
                    var slot = (int)read.U() & 3;
                    var locomotion = (int)read.U(); read.U();
                    var infection = (int)read.U();
                    if (locomotion == Downed) _downedMs[slot] = timeMs | 1;
                    var gained = 0;
                    for (var i = 0; i < 3; i++)
                    {
                        var ammo = (int)read.U();
                        if (_ammo[slot, i] >= 0 && ammo > _ammo[slot, i]) gained += ammo - _ammo[slot, i];
                        _ammo[slot, i] = ammo;
                    }
                    var removed = _infection[slot] >= 0 && infection < _infection[slot] ? _infection[slot] - infection : 0;
                    _infection[slot] = infection;
                    if (Grabbed(locomotion)) _grabbedMs[slot] = timeMs;
                    if (gained > 0 || removed > 0) Gained(timeMs, slot, 0f, gained, removed);
                    break;
                }
        }
    }

    /// <summary>Settles the health drops and wake-ups still open; call once after the last record.</summary>
    internal void Finish()
    {
        foreach (var drop in _drops) Hurts.Add(Settle(drop));
        _drops.Clear();
        foreach (var wakeup in _wakeups) Wakes.Add(Settle(wakeup));
        _wakeups.Clear();
        _causes.Clear(); _noises.Clear(); _causeFrom = _noiseFrom = _actionFrom = 0;
    }

    // Where the window of the last settled drop or wake-up began in each list: the next one, no earlier in time, starts looking there.
    private int _causeFrom, _noiseFrom, _actionFrom;

    // Moves `from` past the entries that are before the window of `timeMs` and of every later drop or wake-up (whose time is at most _slackMs
    // earlier), and returns it. The windows' own tests still decide each entry, so the slice only saves looking at entries they would reject.
    private int From<T>(List<T> list, Func<T, uint> time, ref int from, uint timeMs, uint before)
    {
        while (from < list.Count && (long)time(list[from]) + before + _slackMs < timeMs) from++;
        return from;
    }

    // True when an entry and every one after it in its list are past the window of `timeMs`.
    private bool Past(uint entryMs, uint timeMs, uint after) => entryMs > (long)timeMs + after + _slackMs;

    private bool Where(int entity, out float x, out float y, out float z)
    {
        ref var p = ref _state.Poses[entity];
        x = p.X; y = p.Y; z = p.Z;
        return p.Present;
    }

    private static float Squared(float ax, float ay, float az, float bx, float by, float bz) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz);

    private InferredHurt Settle(Drop drop)
    {
        var source = HurtSource.Unknown; var entity = -1;
        if (drop.Placed)
        {
            var best = float.MaxValue;
            for (var i = From(_causes, static c => c.TimeMs, ref _causeFrom, drop.TimeMs, HurtBeforeMs); i < _causes.Count; i++)
            {
                var cause = _causes[i];
                if (Past(cause.TimeMs, drop.TimeMs, HurtAfterMs)) break;
                Checked++;
                if (cause.TimeMs + HurtBeforeMs < drop.TimeMs || cause.TimeMs > drop.TimeMs + HurtAfterMs) continue;
                var reach = cause.Source switch { HurtSource.Explosion => ExplosionReach, HurtSource.Projectile => ProjectileReach, _ => AttackReach };
                var squared = Squared(cause.X, cause.Y, cause.Z, drop.X, drop.Y, drop.Z);
                if (squared > reach * reach) continue;
                // The nearest in time wins; the distance only breaks a tie.
                var score = Math.Abs((long)drop.TimeMs - cause.TimeMs) * 1000f + squared;
                if (score < best) { best = score; source = cause.Source; entity = cause.Entity; }
            }
        }
        return new InferredHurt(drop.TimeMs, drop.Slot, drop.Fraction, source, entity);
    }

    // A sleeping enemy woke: what a player was doing at that moment within its hearing (running) is known now; recorded noises and
    // rebuilt actions in the moments around it are looked at in Finish.
    private void Woke(uint timeMs, int entity)
    {
        var placed = Where(entity, out var ex, out var ey, out var ez);
        Hearing? hearing = _state.Enemies.TryGetValue(entity, out var info) ? _hearingOf?.Invoke(info.DataId) : null;
        var runners = 0;
        if (placed && hearing is { } h && h.Movement > 0f)
            for (var slot = 0; slot < 4; slot++)
                if (_rebuild.Locomotion(slot) == NoiseRebuild.Run && Where(slot, out var px, out var py, out var pz) && Squared(px, py, pz, ex, ey, ez) <= h.Movement * h.Movement)
                    runners |= 1 << slot;
        _wakeups.Add(new Wakeup(timeMs, entity, placed, ex, ey, ez, hearing, runners));
    }

    private InferredWake Settle(Wakeup wake)
    {
        if (!wake.Placed) return new InferredWake(wake.TimeMs, wake.Entity, -1, false);
        _candidates.Clear();
        for (var slot = 0; slot < 4; slot++) if ((wake.Runners & (1 << slot)) != 0) _candidates.Add((wake.TimeMs, slot));
        for (var i = From(_noises, static n => n.TimeMs, ref _noiseFrom, wake.TimeMs, WakeBeforeMs); i < _noises.Count; i++)
        {
            var noise = _noises[i];
            if (Past(noise.TimeMs, wake.TimeMs, WakeAfterMs)) break;
            Checked++;
            if (noise.TimeMs + WakeBeforeMs < wake.TimeMs || noise.TimeMs > wake.TimeMs + WakeAfterMs) continue;
            if (Squared(noise.X, noise.Y, noise.Z, wake.X, wake.Y, wake.Z) <= noise.Radius * noise.Radius) _candidates.Add((noise.TimeMs, noise.Slot));
        }
        if (wake.Hearing is { } hearing)
        {
            var actions = _rebuild.Actions;
            for (var i = From(actions, static a => a.TimeMs, ref _actionFrom, wake.TimeMs, WakeBeforeMs); i < actions.Count; i++)
            {
                var action = actions[i];
                if (Past(action.TimeMs, wake.TimeMs, WakeAfterMs)) break;
                Checked++;
                if (action.TimeMs + WakeBeforeMs < wake.TimeMs || action.TimeMs > wake.TimeMs + WakeAfterMs) continue;
                var reach = NoiseRebuild.Reach(action.Action, hearing);
                if (!(reach > 0f) || Squared(action.X, action.Y, action.Z, wake.X, wake.Y, wake.Z) > reach * reach) continue;
                _candidates.Add((action.TimeMs, action.Slot));
            }
        }
        if (_candidates.Count == 0) return new InferredWake(wake.TimeMs, wake.Entity, -1, false);
        var first = _candidates[0];
        foreach (var c in _candidates) if (Math.Abs((long)wake.TimeMs - c.TimeMs) < Math.Abs((long)wake.TimeMs - first.TimeMs)) first = c;
        // Another source close in time to the nearest one: nothing tells which of them it was.
        foreach (var c in _candidates)
            if (c.Slot != first.Slot && Math.Abs((long)c.TimeMs - first.TimeMs) <= WakeTieMs) return new InferredWake(wake.TimeMs, wake.Entity, -1, true);
        return new InferredWake(wake.TimeMs, wake.Entity, first.Slot, false);
    }

    private int MineOwner(uint timeMs, float x, float y, float z)
    {
        var owner = -1; var latest = 0L;
        foreach (var place in _places)
        {
            if (place.TimeMs > timeMs || place.TimeMs + MineWindowMs < timeMs || !Where(place.Slot, out var px, out var py, out var pz)) continue;
            if (Squared(px, py, pz, x, y, z) > MineReach * MineReach || place.TimeMs < latest) continue;
            latest = place.TimeMs; owner = place.Slot;
        }
        return owner;
    }

    private int Reviver(int downed)
    {
        if (!Where(downed, out var x, out var y, out var z)) return -1;
        var best = -1; var bestSquared = ReviveReach * ReviveReach;
        for (var slot = 0; slot < 4; slot++)
        {
            if (slot == downed || _rebuild.Locomotion(slot) == Downed || !Where(slot, out var px, out var py, out var pz)) continue;
            var squared = Squared(px, py, pz, x, y, z);
            if (squared <= bestSquared) { bestSquared = squared; best = slot; }
        }
        return best;
    }

    // Health, ammunition or infection of a player changed for the better: a pack used just before it explains it.
    private void Gained(uint timeMs, int receiver, float health, int ammo, int infection)
    {
        for (var i = _uses.Count - 1; i >= 0; i--)
        {
            var use = _uses[i];
            if (use.TimeMs + UseWindowMs < timeMs) break;
            if (use.TimeMs > timeMs || (use.Kind == ConsumeResource) != (use.User == receiver)) continue;
            Packs.Add(new InferredPack(timeMs, use.User, receiver, health, ammo, infection));
            _uses.RemoveAt(i);
            return;
        }
    }

    // The tongue went back: a player it reached for who lost no health and was not caught dodged it.
    private void Retracted(uint timeMs, int entity)
    {
        if (!_tongues.Remove(entity, out var out_)) return;
        var target = out_.Target; var best = TongueReach * TongueReach;
        // A recording that names the target needs no guess (0: it went for a place, not a player).
        for (var slot = 0; slot < 4 && !out_.TargetRecorded; slot++)
        {
            if (!Where(slot, out var px, out var py, out var pz)) continue;
            var squared = Squared(px, py, pz, out_.X, out_.Y, out_.Z);
            if (squared <= best) { best = squared; target = slot; }
        }
        if (target is < 0 or > 3) return;
        var locomotion = _rebuild.Locomotion(target);
        var grabbed = Grabbed(locomotion) || _grabbedMs[target] >= out_.TimeMs && _grabbedMs[target] != 0;
        if (_lastDropMs[target] >= out_.TimeMs && _lastDropMs[target] != 0 || grabbed) return;
        Dodges.Add(new InferredDodge(timeMs, target, entity, out_.TargetRecorded));
    }
}
