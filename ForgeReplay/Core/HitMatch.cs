using System;
using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>Puts a health loss the host synced (EnemyHealth) to the players whose hits it can be (PLAN 5.5): a client's recording has the
/// host's health value of every enemy but not who took it off, so the loss goes to the players whose hits on that enemy lie in the window
/// before it. A hit is the recorder's own reading of its hit, another player's ray that ended on an enemy limb at that enemy's place, or a
/// swing with an enemy in its reach before it while its damage runs. One player is the game's number; several at once share it by their hits, and that share is an estimate.</summary>
internal sealed class HitMatch
{
    /// <summary>How far from an enemy's recorded place (its middle) a ray may end and still count as hitting it.</summary>
    internal const float Reach = 2.5f;

    /// <summary>How far past the weapon's camera ray and sphere an enemy's recorded place (its feet) may be: the body the game's limbs stand out by (calibrated, PLAN 5.5).</summary>
    internal const float SwingBody = 0.5f;
    /// <summary>A swing's reach and how long its damage runs when the melee weapon is not known.</summary>
    internal const float SwingReach = 3f;
    private const uint SwingMs = 400;
    /// <summary>How far from a sentry's aim an enemy's recorded place may be: not in the game data, the same order as a body's height.</summary>
    internal const float SentryBody = 1.5f;
    /// <summary>A sentry's bullet flies at its target, not along the muzzle (Sentry_FireTowardsTargetInsteadOfForward), and the muzzle turns after the target at the archetype's rotation speed, so the
    /// recorded aim lags it: an enemy within this cosine of the aim (15 degrees) and within <see cref="SentryRange"/> metres is the one it fires at; the nearest such one is taken.</summary>
    internal const float SentryCone = 0.966f;
    /// <summary>The longest damage falloff of the sentry archetypes (40 m): no shot reaches further.</summary>
    internal const float SentryRange = 40f;
    /// <summary>The kind of a kill by a sentry's bullet: EnemyHurt's are 0 bullet, 1 melee, 2 explosion.</summary>
    internal const int SentryKind = 3;

    /// <summary>How long before a synced loss a hit may be and still be the hit the host settled (the trip to the host and back).</summary>
    internal const uint WindowMs = 1500;

    private readonly List<(uint TimeMs, int Entity, int Slot, int Kind)> _hits = new();
    private readonly int _recorder;
    private readonly List<Watched> _swings = new();
    /// <summary>The kind of each slot's newest hit in the window of the last <see cref="Match"/>.</summary>
    internal readonly int[] KindOf = new int[4];
    private readonly uint[] _charging = new uint[4];
    private readonly bool[] _chargingNow = new bool[4];
    private readonly (uint Ms, int Kind, float Held)[] _lastSwing = new (uint, int, float)[4];
    // A hit the game broadcast (MeleeHit) has landed on an enemy: from here on what landed is what the game says, not the reach.
    private bool _struck;
    private sealed class Watched { internal uint Ms, From, To; internal int Slot, Kind; internal float Held, Reach; internal bool Multiple; internal readonly List<int> Enemies = new(); }

    // The recorder's hits are its own readings (EnemyHurt), not its rays and swings.
    internal HitMatch(int recorderSlot) { _recorder = recorderSlot; }

    internal void Hit(uint timeMs, int entity, int slot, int kind)
    {
        _hits.RemoveAll(h => h.TimeMs + WindowMs < timeMs);
        _hits.Add((timeMs, entity, slot & 3, kind));
    }

    /// <summary>A Shot of any player whose ray ended on an enemy limb: a hit on the enemy standing at the end of the ray, if one does. Returns that enemy, -1 for none.</summary>
    internal int Shot(uint timeMs, PayloadReader read, WorldState state)
    {
        var slot = (int)read.U() & 3; read.U(); read.Pos();
        var (ex, ey, ez) = read.Pos();
        if (slot == _recorder || read.U() != 1 || read.U() != 2) return -1;
        var found = Nearest(state, timeMs, ex, ey, ez, Reach);
        if (found >= 0) Hit(timeMs, found, slot, 0);
        return found;
    }

    /// <summary>A player starts or ends a charge (MeleeCharge). Starting one is no light swing after all: a press only becomes one when it is let go, which an older file did not wait for.</summary>
    internal void Charge(uint timeMs, int slot, bool charging)
    {
        var was = _chargingNow[slot];
        _chargingNow[slot] = charging;
        if (!charging || was) return; // a keyframe writes a charge under way again: it did not begin there
        _charging[slot] = timeMs;
        _swings.RemoveAll(s => s.Slot == slot && s.Kind == 0);
    }

    /// <summary>A Melee swing (kind 0 light, 1 charged; a push, 2, has no damage): watched while its damage runs, by the weapon's block.</summary>
    internal void Swung(uint timeMs, int slot, int kind, SwingDamage? weapon)
    {
        if (slot == _recorder || kind > 1) return;
        _lastSwing[slot] = (timeMs, kind, kind == 1 && _charging[slot] != 0 ? (timeMs - _charging[slot]) / 1000f : float.MaxValue);
        if (_struck) return;
        var (from, to) = weapon is not { } w ? (0f, SwingMs / 1000f) : kind == 0 ? (w.LightFrom, w.LightTo) : (w.ChargedFrom, w.ChargedTo);
        _swings.Add(new Watched
        {
            Ms = timeMs,
            From = timeMs + (uint)(from * 1000f),
            To = timeMs + (uint)(to * 1000f),
            Slot = slot,
            Kind = kind,
            Held = kind == 1 && _charging[slot] != 0 ? (timeMs - _charging[slot]) / 1000f : float.MaxValue,
            Reach = weapon is { } r ? r.Reach + SwingBody : SwingReach,
            Multiple = weapon is { Multiple: true },
        });
    }

    /// <summary>Goes through the swings whose damage runs at this time, each enemy they land on going to <paramref name="landed"/> (swung at, time, slot, kind, seconds charged, enemy).</summary>
    internal void Watch(uint timeMs, WorldState state, Action<uint, uint, int, int, float, int> landed)
    {
        if (_struck) { _swings.Clear(); return; }
        for (var i = _swings.Count - 1; i >= 0; i--)
        {
            var swing = _swings[i];
            if (timeMs >= swing.From && timeMs <= swing.To && state.Place(swing.Slot, timeMs, out var x, out var y, out var z, out var qx, out var qy, out var qz, out var qw))
            {
                var found = InFront(state, timeMs, x, y, z, 2f * (qx * qz + qw * qy), 1f - 2f * (qx * qx + qy * qy), swing.Reach, swing.Enemies);
                if (found >= 0)
                {
                    Hit(timeMs, found, swing.Slot, 1);
                    swing.Enemies.Add(found);
                    landed(swing.Ms, timeMs, swing.Slot, swing.Kind, swing.Held, found);
                    if (!swing.Multiple) swing.To = 0;
                }
            }
            if (timeMs >= swing.To) _swings.RemoveAt(i);
        }
    }

    /// <summary>The game's broadcast of another player's swing landing (MeleeHit) at this place: the enemy it is on, -1 for none (a wall, say). Its charge is that of the player's
    /// last swing, or of the charge still running when the broadcast overtook the swing on the way here.</summary>
    internal int Struck(uint timeMs, int slot, float x, float y, float z, WorldState state, out int kind, out float held)
    {
        kind = 0; held = 0f;
        if (slot == _recorder) return -1;
        var found = Nearest(state, timeMs, x, y, z, Reach);
        if (found < 0) return -1;
        var last = _lastSwing[slot];
        if (last.Ms != 0 && timeMs - last.Ms <= WindowMs) { kind = last.Kind; held = last.Held; }
        else if (_chargingNow[slot]) { kind = 1; held = (timeMs - _charging[slot]) / 1000f; }
        _struck = true;
        Hit(timeMs, found, slot, 1);
        return found;
    }

    /// <summary>A sentry's shot (its muzzle and aim; the recording does not say what it hit): the first enemy along the aim within a body's width. Its owner hit it.</summary>
    internal int Sentry(uint timeMs, int owner, float x, float y, float z, float ax, float ay, float az, WorldState state, out float metres)
    {
        var found = -1; metres = float.MaxValue;
        foreach (var entity in state.Enemies.Keys)
        {
            if (!state.Place(entity, timeMs, out var ex, out var ey, out var ez, out _, out _, out _, out _)) continue;
            var dx = ex - x; var dy = ey - y; var dz = ez - z;
            var along = dx * ax + dy * ay + dz * az;
            if (along < 0f || along >= metres || along > SentryRange) continue;
            var cx = dx - ax * along; var cy = dy - ay * along; var cz = dz - az * along;
            var perpendicular = cx * cx + cy * cy + cz * cz;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (perpendicular <= SentryBody * SentryBody || along >= SentryCone * distance) { metres = along; found = entity; }
        }
        if (found >= 0) Hit(timeMs, found, owner, SentryKind);
        return found;
    }

    private static int InFront(WorldState state, uint timeMs, float px, float py, float pz, float fx, float fz, float reach, List<int> done)
    {
        var found = -1; var best = reach * reach;
        foreach (var entity in state.Enemies.Keys)
        {
            if (done.Contains(entity) || !state.Place(entity, timeMs, out var x, out var y, out var z, out _, out _, out _, out _)) continue;
            var dx = x - px; var dy = y - py; var dz = z - pz;
            var flat = dx * dx + dz * dz;
            if (flat <= best && dy * dy <= reach * reach && dx * fx + dz * fz >= 0f) { best = flat; found = entity; }
        }
        return found;
    }

    private static int Nearest(WorldState state, uint timeMs, float px, float py, float pz, float reach)
    {
        var found = -1; var best = reach * reach;
        foreach (var entity in state.Enemies.Keys)
        {
            if (!state.Place(entity, timeMs, out var x, out var y, out var z, out _, out _, out _, out _)) continue;
            var dx = x - px; var dy = y - py; var dz = z - pz;
            var squared = dx * dx + dy * dy + dz * dz;
            if (squared <= best) { best = squared; found = entity; }
        }
        return found;
    }

    /// <summary>The hits on <paramref name="entity"/> in the window before <paramref name="timeMs"/>, counted per slot into
    /// <paramref name="counts"/>; the result is the number of players among them, and <paramref name="last"/> the slot and kind of the newest.</summary>
    internal int Match(uint timeMs, int entity, int[] counts, out int last, out int lastKind)
    {
        Array.Clear(counts);
        last = -1; lastKind = 0;
        var players = 0; uint newest = 0;
        Span<uint> newestOf = stackalloc uint[4];
        foreach (var h in _hits)
        {
            if (h.Entity != entity || h.TimeMs > timeMs || h.TimeMs + WindowMs < timeMs) continue;
            if (counts[h.Slot]++ == 0) players++;
            if (h.TimeMs >= newestOf[h.Slot]) { newestOf[h.Slot] = h.TimeMs; KindOf[h.Slot] = h.Kind; }
            if (last < 0 || h.TimeMs >= newest) { newest = h.TimeMs; last = h.Slot; lastKind = h.Kind; }
        }
        return players;
    }

    internal void Forget(int entity) => _hits.RemoveAll(h => h.Entity == entity);
}
