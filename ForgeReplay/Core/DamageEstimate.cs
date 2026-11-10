using System;
using System.Collections.Generic;

namespace ForgeReplay;

/// <summary>What a gun's ray does before the limb it lands on: the game's Archetype block. <paramref name="Booster"/> is the block's DamageBoosterEffect (an AgentModifier).</summary>
internal readonly record struct GunDamage(float Damage, float FalloffStart, float FalloffEnd, float Precision, int Booster = 0, bool Piercing = false, float SentryLong = 0f, float SentryShort = 0f)
{
    // The game's own code, the same in the website's weapon calculator: full damage up to the start, falling straight to the end, never under a tenth.
    private const float FalloffFloor = 0.1f;

    internal float At(float metres)
    {
        if (metres <= FalloffStart) return Damage;
        if (FalloffEnd <= FalloffStart) return Damage * FalloffFloor;
        return Damage * Math.Max(FalloffFloor, 1f - (metres - FalloffStart) / (FalloffEnd - FalloffStart));
    }
}

/// <summary>What a melee weapon's swing does before the limb: the game's MeleeArchetype block (damage, precision, back and sleeper multipliers, light
/// and fully charged; <paramref name="Reach"/> is its camera ray and attack sphere, <paramref name="Multiple"/> whether a swing goes on past the first enemy)
/// and its MeleeAnimationSet block (the charge time of full damage, the seconds into a swing its damage runs, as the first-person animations have it).</summary>
internal readonly record struct SwingDamage(float Light, float Charged, float LightPrecision, float ChargedPrecision, float LightBack, float ChargedBack, float LightSleeper, float ChargedSleeper,
    float FullCharge, float Reach, bool Multiple, float LightFrom, float LightTo, float ChargedFrom, float ChargedTo)
{
    /// <summary>The game's charge: every frame of the charge sets the swing to the light numbers lerped to the charged ones by the cube of the held share of the full time.</summary>
    internal float Share(float heldSeconds)
    {
        var held = FullCharge > 0f ? Math.Clamp(heldSeconds / FullCharge, 0f, 1f) : 1f;
        return held * held * held;
    }
}

/// <summary>What an enemy type does to a hit: the game's EnemyBalancing block (the limbs, and whether a hit from behind counts more on it).</summary>
internal readonly record struct EnemyLimbs(float Weak, float Armour, bool FromBehind);

/// <summary>The game data <see cref="DamageEstimate"/> reads, as plain lookups so the analysis can run off the game thread: a gun by the archetype
/// id a Shot carries, an enemy by its data block, a melee weapon by the gear category in a loadout (null for a category that is no melee weapon), a booster effect by
/// its data block to its AgentModifier (null when the blocks are not at hand: boosters then count for nothing), a sentry's gun by the fire mode its shot carries.</summary>
internal sealed record DamageData(Func<ulong, GunDamage?> Gun, Func<ulong, EnemyLimbs?> Enemy, Func<uint, SwingDamage?> Swing, Func<uint, int?>? Booster = null, Func<ulong, ulong?>? SentryGun = null);

/// <summary>One landed ray or swing of a player with the health worked out for it: kind 0 bullet, 1 melee; what the player wielded.</summary>
internal readonly record struct EstimatedHit(uint TimeMs, int Slot, int Kind, int Wielded, float Damage);

/// <summary>The damage of the players a client's recording only shows rays and swings of, when the host syncs no enemy health (PLAN 5.5). A ray or
/// swing <see cref="HitMatch"/> puts on an enemy does the game's sum (Dam_EnemyDamageLimb, ArchetypeDataBlock.GetDamageWithBoosterEffect,
/// MeleeWeaponFirstPerson.SetNextDamageToDeal): the block's damage with the player's boosters, falloff by the ray's length or the swing's charge, the
/// limb, then the bonus from behind by the enemy's recorded facing, for a swing also its back and sleeper multipliers. The limb is not in the recording, so
/// it counts the mean multiplier of how the recorder's own hits fell on normal, weak and armoured limbs. An enemy's health caps the sum, what the
/// recorder took off it first, then the others in time order; an enemy killed with one other player's hits alone seen gives them all that is left.
/// Hits are settled in <see cref="Finish"/>, when all the recorder's limbs are known. Nothing is put on a player for a mine or a sentry.</summary>
internal sealed class DamageEstimate
{
    private readonly record struct Hit(uint TimeMs, int Slot, int Kind, int Wielded, float Base, float Precision, bool Struck = false);
    private sealed class Enemy { internal ulong Data; internal float Full, Taken; internal bool Died; internal readonly List<Hit> Hits = new(); }

    private readonly DamageData _data;
    private readonly Dictionary<int, Enemy> _open = new();
    private readonly List<Enemy> _closed = new();
    // The file holds the game's broadcasts of swings landing: a swing only judged by its reach counts for nothing.
    private bool _struck;

    /// <summary>The recorder's hits by [kind, limb type (0 normal, 1 weak spot, 2 armour)].</summary>
    internal int[,] Limbs { get; set; } = new int[2, 3];

    /// <summary>Each player's boosters: AgentModifier to what the effects add to 1, which is how the game stacks them.</summary>
    internal Dictionary<int, float>[] Boost { get; set; } = { new(), new(), new(), new() };

    internal List<EstimatedHit> Hits { get; } = new();

    internal DamageEstimate(DamageData data) { _data = data; }

    private Enemy Of(int entity, ulong data, float full)
    {
        if (!_open.TryGetValue(entity, out var enemy)) _open[entity] = enemy = new Enemy();
        enemy.Data = data; enemy.Full = full;
        return enemy;
    }

    /// <summary>The Boosters event of a player: replaces what the player wears.</summary>
    internal void Boosters(PayloadReader read)
    {
        var slot = (int)read.U() & 3;
        var boost = Boost[slot];
        boost.Clear();
        var worn = read.U();
        for (ulong i = 0; i < worn; i++)
        {
            read.U(); read.U();
            var effects = read.U();
            for (ulong e = 0; e < effects; e++)
            {
                var id = (uint)read.U(); var value = read.S() / 1000f;
                if (_data.Booster?.Invoke(id) is { } modifier) boost[modifier] = boost.GetValueOrDefault(modifier) + value - 1f;
            }
        }
    }

    /// <summary>The cosine between a hit's direction and an enemy's recorded facing: 1 straight into its back.</summary>
    internal static float BackDot(WorldState state, int entity, uint timeMs, float dx, float dy, float dz)
    {
        var length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length <= 0f || !state.Place(entity, timeMs, out _, out _, out _, out var x, out var y, out var z, out var w)) return -1f;
        return (dx * 2f * (x * z + w * y) + dy * 2f * (y * z - w * x) + dz * (1f - 2f * (x * x + y * y))) / length;
    }

    // The game's ApplyDamageFromBehindBonus: 1 + the clamped cosine + a quarter, on an enemy type that allows it.
    private static float Behind(float dot) => 1f + Math.Clamp(dot + BehindFrom, 0f, 1f);

    private const float BehindFrom = 0.25f, BackstabFrom = 0.55f;
    private const int StandardWeaponDamage = 70, SpecialWeaponDamage = 71, SentryGunDamage = 103, SentryLongRange = 104, SentryShortRange = 105, TripMineDamage = 106, MeleeDamage = 200; // AgentModifier
    private const int WieldedStandard = 1, WieldedSpecial = 2; // InventorySlot

    /// <summary>A ray on an enemy; <paramref name="backDot"/> as <see cref="BackDot"/>.</summary>
    internal void Shot(uint timeMs, int entity, ulong data, float full, int slot, int wielded, ulong gun, float metres, float backDot)
    {
        if (_data.Gun(gun) is not { } g) return;
        // GetDamageWithBoosterEffect: the gun's own modifier and its slot's both add to the block's damage.
        var boost = Boost[slot];
        var boosted = 1f + boost.GetValueOrDefault(g.Booster) + boost.GetValueOrDefault(wielded == WieldedStandard ? StandardWeaponDamage : wielded == WieldedSpecial ? SpecialWeaponDamage : -1);
        var behind = _data.Enemy(data) is { FromBehind: true } ? Behind(backDot) : 1f;
        Of(entity, data, full).Hits.Add(new Hit(timeMs, slot, 0, wielded, g.At(metres) * boosted * behind, g.Precision));
    }

    /// <summary>A swing that landed: <paramref name="swing"/> 0 light, 1 charged for <paramref name="held"/> seconds; <paramref name="struck"/> by the game's broadcast, not judged by reach.</summary>
    internal void Swing(uint timeMs, int entity, ulong data, float full, int slot, int wielded, int swing, float held, SwingDamage? weapon, float backDot, bool sleeping, bool struck = false)
    {
        _struck |= struck;
        if (swing > 1 || weapon is not { } w) return;
        var share = swing == 0 ? 0f : w.Share(held);
        float Lerp(float light, float charged) => light + (charged - light) * share;
        var damage = Lerp(w.Light, w.Charged) * (1f + Boost[slot].GetValueOrDefault(MeleeDamage));
        if (_data.Enemy(data) is { FromBehind: true })
        {
            damage *= Behind(backDot);
            if (backDot > BackstabFrom) damage *= Lerp(w.LightBack, w.ChargedBack);
        }
        if (sleeping) damage *= Lerp(w.LightSleeper, w.ChargedSleeper);
        Of(entity, data, full).Hits.Add(new Hit(timeMs, slot, 1, wielded, damage, Lerp(w.LightPrecision, w.ChargedPrecision), struck));
    }

    /// <summary>A sentry's shot on an enemy, its owner's: ArchetypeDataBlock.GetSentryDamage, then BulletHit's falloff and limb; the bonus from behind only for a piercing gun, whose call allows it.</summary>
    internal void Sentry(uint timeMs, int entity, ulong data, float full, int owner, ulong mode, float metres, float backDot)
    {
        if (_data.SentryGun?.Invoke(mode) is not { } id || _data.Gun(id) is not { } g) return;
        var boost = Boost[owner];
        var boosted = 1f + boost.GetValueOrDefault(SentryGunDamage);
        if (metres >= g.SentryLong && g.SentryLong > 0f) boosted *= 1f + boost.GetValueOrDefault(SentryLongRange);
        else if (metres <= g.SentryShort) boosted *= 1f + boost.GetValueOrDefault(SentryShortRange);
        var behind = g.Piercing && _data.Enemy(data) is { FromBehind: true } ? Behind(backDot) : 1f;
        Of(entity, data, full).Hits.Add(new Hit(timeMs, owner, 0, WeaponKey.Sentry, g.At(metres) * boosted * behind, g.Precision));
    }

    /// <summary>A mine's explosion with its numbers, on an enemy <paramref name="metres"/> from it: DoExplosionDamage_Capsule lerps by distance; the limb takes no part. TripMineDamage is the owner's booster.</summary>
    internal void Blast(uint timeMs, int entity, ulong data, float full, int owner, float metres, float near, float far, float most, float least)
    {
        var share = far > near ? Math.Clamp((metres - near) / (far - near), 0f, 1f) : 0f;
        Of(entity, data, full).Hits.Add(new Hit(timeMs, owner, 2, WeaponKey.Explosion, (most + (least - most) * share) * (1f + Boost[owner].GetValueOrDefault(TripMineDamage)), 1f));
    }

    /// <summary>The recorder's own hit landed on this kind of limb.</summary>
    internal void Own(int kind, int limb) { if (kind is 0 or 1 && limb is >= 0 and <= 2) Limbs[kind, limb]++; }

    /// <summary>The enemy is gone; <paramref name="taken"/> is what the recorder's own hits took off it. <paramref name="died"/>: a player's hit is what
    /// killed it as far as can be told (it died, and no mine or sentry went off in the moments before).</summary>
    internal void Gone(int entity, float taken, bool died)
    {
        if (!_open.Remove(entity, out var enemy)) return;
        enemy.Taken = taken; enemy.Died = died;
        _closed.Add(enemy);
    }

    /// <summary>The host synced this enemy's health: its losses are the game's numbers, nothing of it is worked out here.</summary>
    internal void Forget(int entity) => _open.Remove(entity);

    private float Multiplier(in Hit hit, ulong data)
    {
        if (hit.Kind > 1 || _data.Enemy(data) is not { } limbs) return 1f;
        float normal = Limbs[hit.Kind, 0], weak = Limbs[hit.Kind, 1], armour = Limbs[hit.Kind, 2], all = normal + weak + armour;
        return all == 0f ? 1f : (normal + weak * Math.Max(1f, limbs.Weak * hit.Precision) + armour * limbs.Armour) / all;
    }

    internal void Finish()
    {
        _closed.AddRange(_open.Values);
        _open.Clear();
        foreach (var enemy in _closed)
        {
            if (_struck) enemy.Hits.RemoveAll(h => h.Kind == 1 && !h.Struck);
            var left = enemy.Full > 0f ? Math.Max(0f, enemy.Full - enemy.Taken) : float.MaxValue;
            // Killed with only one player's hits seen besides the recorder's: the rest of its health was theirs, however few of their rays this machine saw.
            // A swing is not scaled: this machine sees them all (calibration: every swing that hit is in the file), so what is missing is rays.
            var rays = 0f; var swings = 0f; var one = true;
            foreach (var hit in enemy.Hits)
            {
                if (hit.Kind == 0) rays += hit.Base * Multiplier(hit, enemy.Data); else swings += hit.Base * Multiplier(hit, enemy.Data);
                one &= hit.Slot == enemy.Hits[0].Slot;
            }
            var scale = enemy.Died && one && rays > 0f && rays + swings < left && left < float.MaxValue ? (left - swings) / rays : 1f;
            foreach (var hit in enemy.Hits)
            {
                var took = Math.Min(hit.Base * Multiplier(hit, enemy.Data) * (hit.Kind == 0 ? scale : 1f), left);
                if (took <= 0f) break;
                left -= took;
                Hits.Add(new EstimatedHit(hit.TimeMs, hit.Slot, hit.Kind, hit.Wielded, took));
            }
        }
        _closed.Clear();
        Hits.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
    }

    /// <summary>The melee weapon in a loadout (gear JSON lines, as the file header keeps them): the first line whose gear category is one.</summary>
    internal SwingDamage? MeleeOf(string loadout)
    {
        const string Category = "\"c\":2,\"v\":"; // eGearComponent.Category in the gear JSON's Comps
        foreach (var line in loadout.Split('\n'))
        {
            var at = line.IndexOf(Category, StringComparison.Ordinal);
            if (at < 0) continue;
            var start = at + Category.Length; var end = start;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            if (uint.TryParse(line.AsSpan(start, end - start), out var category) && _data.Swing(category) is { } weapon) return weapon;
        }
        return null;
    }
}
