using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeCore;

namespace ForgeOrdnance.Arsenal;

/// <summary>The grenade a heavy weapon fires instead of a bullet: its explosion and flight. A weapon without one fires its own bullets.
/// <paramref name="TrueDamage"/>: the explosion hurts each enemy's body directly (no armour or weak spot step), falling off in a line from
/// <paramref name="Damage"/> at the centre to <paramref name="EdgeShare"/> of it at the radius; players are not hurt.</summary>
internal sealed record GrenadeSpec(float Damage, float Radius, float Speed, bool TrueDamage = false, float EdgeShare = 0.5f)
{
    internal const double DamageMax = 20000, RadiusMin = 0.5, RadiusMax = 20, SpeedMin = 5, SpeedMax = 150;

    /// <summary>All three values are inside what a payload may write.</summary>
    internal bool Valid => EdgeShare >= 0f && EdgeShare <= 1f && Damage >= 0f && Damage <= DamageMax && Radius >= RadiusMin && Radius <= RadiusMax && Speed >= SpeedMin && Speed <= SpeedMax;
}

/// <summary>A weapon whose bullets are steered at an enemy near the crosshair: the widest angle off the aim (degrees) and the farthest enemy (metres).</summary>
internal sealed record HomingSpec(float Angle, float Range)
{
    internal const double AngleMax = 30, RangeMax = 100;
    internal bool Valid => Angle >= 1f && Angle <= AngleMax && Range >= 5f && Range <= RangeMax;
}

/// <summary>Validated parameters of payload type <see cref="TypeName"/>: a game gear block lent into the special weapon slot,
/// shown with a ForgeVisual model, taken from the drop pod's rack.</summary>
internal sealed class HeavyWeaponSpec
{
    public const string TypeName = "arsenal.HeavyWeapon";

    /// <summary>The ForgeVisual model, <c>Pack:item</c>.</summary>
    public string Model { get; private set; } = "";
    /// <summary>The FNV-1a hash of <see cref="Model"/> and the public gear name that marks the lent weapon, both made once when the spec is
    /// made, so the per-frame readers allocate and hash nothing.</summary>
    public uint ModelHash { get; private set; }
    public string Marker { get; private set; } = "";
    /// <summary>The offline gear block: a persistentID or a block name (exactly one is set).</summary>
    public uint? GearId { get; private set; }
    public string? GearName { get; private set; }
    /// <summary>How many of this weapon lie in one pod (one per weapon bay).</summary>
    public int Count { get; private set; } = 1;
    /// <summary>The most weapons one pod holds: the built-in pod has two weapon bays.</summary>
    public const int MaxCount = 2;
    /// <summary>Ammunition given with the weapon besides its full magazine, as a fraction of the special slot's full reserve; 0 is the magazine alone.</summary>
    public float AmmoRatio { get; private set; }
    /// <summary>The grenade the weapon fires, or null for a weapon that fires bullets.</summary>
    public GrenadeSpec? Grenade { get; private set; }
    /// <summary>Set for a weapon whose bullets home in on enemies near the aim, or null.</summary>
    public HomingSpec? Homing { get; private set; }
    /// <summary>The muzzle flash drawn when it fires: <c>"Muzzle"</c> of the payload, <c>none</c>, <c>mg</c> (default) or <c>amr</c>.</summary>
    public HeavyMuzzle Muzzle { get; private set; } = HeavyMuzzle.Mg;
    /// <summary>How hard the gun kicks, as a share of the lent gear's own recoil.</summary>
    public float RecoilScale { get; private set; } = 1f;
    internal const double RecoilScaleMin = 0.1, RecoilScaleMax = 5;
    /// <summary>The factory numbers laid over the lent gear's archetype; unset fields keep the game's own value.</summary>
    public ArchetypeTuning Tuning { get; private set; } = ArchetypeTuning.None;

    private static readonly string[] Known = Concat(new[] { "Model", "Gear", "Count", "AmmoRatio", "RecoilScale", "Muzzle", "Grenade", "Homing" }, ArchetypeTuning.Names);
    private static readonly string[] HomingFields = { "Angle", "Range" };
    private static readonly string[] GrenadeFields = { "Damage", "Radius", "Speed", "TrueDamage", "EdgeShare" };

    private static readonly string[] TypeField = { "Type" };

    public static HeavyWeaponSpec Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new StrictJsonException("Payload", "must be an object.");
        StrictJson.OnlyKnown(payload, "", Known, TypeField, "a heavy weapon");
        if (!payload.TryGetProperty("Model", out var model) || model.ValueKind != JsonValueKind.String || !HeavyModels.IsId(model.GetString()))
            throw new StrictJsonException("Model", "is required: a ForgeVisual model id written Pack:item.");
        var spec = WithModel(model.GetString()!);
        (spec.GearId, spec.GearName) = StrictJson.Reference(payload, "", "Gear");
        spec.AmmoRatio = (float)StrictJson.Number(payload, "", "AmmoRatio", 1, 0, 1);
        spec.Count = (int)StrictJson.Integer(payload, "", "Count", 1, 1, MaxCount);
        spec.RecoilScale = (float)StrictJson.Number(payload, "", "RecoilScale", 1, RecoilScaleMin, RecoilScaleMax);
        spec.Tuning = ArchetypeTuning.Parse(payload);
        if (payload.TryGetProperty("Muzzle", out var muzzle))
            spec.Muzzle = muzzle.ValueKind == JsonValueKind.String && muzzle.GetString() is "none" or "mg" or "amr"
                ? Enum.Parse<HeavyMuzzle>(muzzle.GetString()!, true)
                : throw new StrictJsonException("Muzzle", "must be \"none\", \"mg\" or \"amr\".");
        if (payload.TryGetProperty("Grenade", out var grenade))
        {
            StrictJson.OnlyKnown(grenade, "Grenade.", GrenadeFields);
            spec.Grenade = new GrenadeSpec(
                (float)StrictJson.Number(grenade, "Grenade.", "Damage", 140, 0, GrenadeSpec.DamageMax),
                (float)StrictJson.Number(grenade, "Grenade.", "Radius", 4, GrenadeSpec.RadiusMin, GrenadeSpec.RadiusMax),
                (float)StrictJson.Number(grenade, "Grenade.", "Speed", 32, GrenadeSpec.SpeedMin, GrenadeSpec.SpeedMax),
                StrictJson.Bool(grenade, "Grenade.", "TrueDamage", false),
                (float)StrictJson.Number(grenade, "Grenade.", "EdgeShare", 0.5, 0, 1));
        }
        if (payload.TryGetProperty("Homing", out var homing))
        {
            StrictJson.OnlyKnown(homing, "Homing.", HomingFields);
            spec.Homing = new HomingSpec(
                (float)StrictJson.Number(homing, "Homing.", "Angle", 12, 1, HomingSpec.AngleMax),
                (float)StrictJson.Number(homing, "Homing.", "Range", 60, 5, HomingSpec.RangeMax));
        }
        return spec;
    }

    /// <summary>A spec rebuilt from a rack record (<see cref="Wire.TryRack"/>), the gear already resolved to
    /// <paramref name="gearId"/> and the model hash to its id; null when a value is outside what <see cref="Parse"/> accepts.</summary>
    public static HeavyWeaponSpec? Restore(string model, uint gearId, float ammoRatio, GrenadeSpec? grenade, ArchetypeTuning? tuning, HomingSpec? homing = null, float recoilScale = 1f, float count = 1f)
    {
        if (!HeavyModels.IsId(model) || gearId == 0 || !(ammoRatio >= 0f && ammoRatio <= 1f) || tuning == null || grenade is { Valid: false } || homing is { Valid: false } || !(recoilScale >= RecoilScaleMin && recoilScale <= RecoilScaleMax)
            || !(count >= 1f && count <= MaxCount) || count != MathF.Floor(count)) return null;
        var spec = WithModel(model);
        spec.GearId = gearId;
        spec.Muzzle = HeavyModels.MuzzleOf(model);
        spec.AmmoRatio = ammoRatio;
        spec.Grenade = grenade;
        spec.Homing = homing;
        spec.RecoilScale = recoilScale;
        spec.Tuning = tuning;
        spec.Count = (int)count;
        return spec;
    }

    internal const double MagazineMin = 1, MagazineMax = 1000;

    /// <summary>A copy with the ammunition a placement sets (null keeps the definition's): the reserve share, the rounds loaded and the reserve one shot spends.</summary>
    public HeavyWeaponSpec WithAmmo(float? ammoRatio, float? magazine, float? ammoCost)
    {
        var copy = (HeavyWeaponSpec)MemberwiseClone();
        if (ammoRatio is { } ratio) copy.AmmoRatio = ratio;
        copy.Tuning = Tuning.With(magazine, ammoCost);
        return copy;
    }

    private static HeavyWeaponSpec WithModel(string model)
    {
        var hash = HeavyModels.Hash(model);
        return new HeavyWeaponSpec { Model = model, ModelHash = hash, Marker = HeavyModels.Marker(hash) };
    }

    private static string[] Concat(string[] a, string[] b)
    {
        var all = new string[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all;
    }

}

/// <summary>The archetype numbers a heavy weapon's factory settings may set (absolute values). A NaN entry is "not set". The same
/// type carries the game block's readings, so the host and the lending client compute the effective numbers with one function.</summary>
internal sealed class ArchetypeTuning
{
    public const int Count = 12;
    /// <summary>The range of <see cref="AmmoCost"/>, the reserve one shot spends (the block's CostOfBullet).</summary>
    internal const double AmmoCostMin = 0.01, AmmoCostMax = 1000;
    private static readonly (string Name, double Min, double Max, bool Whole)[] Table =
    {
        ("Damage", 0.01, 20000, false), ("ShotDelay", 0.02, 10, false), ("Cooldown", 0, 30, false), ("Chargeup", 0, 10, false),
        ("Magazine", 1, 1000, true), ("Reload", 0.1, 30, false), ("PrecisionMultiplier", 0, 10, false), ("StaggerMultiplier", 0, 20, false),
        ("Pierce", 0, 20, true), ("AmmoCost", AmmoCostMin, AmmoCostMax, false), ("FalloffStart", 0, 1000, false), ("FalloffEnd", 0.01, 1000, false),
    };

    public static readonly string[] Names = Array.ConvertAll(Table, row => row.Name);
    public static readonly ArchetypeTuning None = new(Filled(float.NaN));

    private readonly float[] _v;

    public ArchetypeTuning(float[] values)
    {
        if (values.Length != Count) throw new ArgumentException("An archetype tuning has " + Count + " values.");
        _v = (float[])values.Clone();
    }

    private static float[] Filled(float value) { var all = new float[Count]; Array.Fill(all, value); return all; }

    public float this[int index] => _v[index];
    public float Damage => _v[0];
    public float ShotDelay => _v[1];
    /// <summary>Seconds between the special action and the next shot (the block's SpecialCooldownTime).</summary>
    public float Cooldown => _v[2];
    public float Chargeup => _v[3];
    public float Magazine => _v[4];
    public float Reload => _v[5];
    public float Precision => _v[6];
    public float Stagger => _v[7];
    /// <summary>Bodies one bullet passes through; 0 is no piercing.</summary>
    public float Pierce => _v[8];
    public float AmmoCost => _v[9];
    /// <summary>Metres a bullet flies at full damage; set together with <see cref="FalloffEnd"/>.</summary>
    public float FalloffStart => _v[10];
    /// <summary>Metres at which the damage falloff bottoms out (the game's own minimum factor).</summary>
    public float FalloffEnd => _v[11];

    /// <summary>A copy whose magazine and per-shot cost are the given values where not null.</summary>
    public ArchetypeTuning With(float? magazine, float? ammoCost)
    {
        var values = (float[])_v.Clone();
        if (magazine is { } rounds) values[4] = rounds;
        if (ammoCost is { } cost) values[9] = cost;
        return new ArchetypeTuning(values);
    }

    /// <summary>The values as text: two tunings with the same key are the same numbers (a cache key).</summary>
    public string Key => string.Join(",", _v);

    public bool IsEmpty { get { foreach (var v in _v) if (!float.IsNaN(v)) return false; return true; } }

    public static ArchetypeTuning Parse(JsonElement payload)
    {
        var values = new float[Count];
        for (var i = 0; i < Count; i++)
        {
            var row = Table[i];
            values[i] = !payload.TryGetProperty(row.Name, out _) ? float.NaN
                : (float)(row.Whole ? StrictJson.Integer(payload, "", row.Name, null, row.Min, row.Max) : StrictJson.Number(payload, "", row.Name, null, row.Min, row.Max));
        }
        if (float.IsNaN(values[10]) != float.IsNaN(values[11])) throw new StrictJsonException("FalloffStart", "is set together with FalloffEnd.");
        if (!float.IsNaN(values[10]) && values[11] <= values[10]) throw new StrictJsonException("FalloffEnd", "must be greater than FalloffStart.");
        return new ArchetypeTuning(values);
    }

    /// <summary>The tuning read from a rack record; null when a set value is outside what <see cref="Parse"/> accepts.</summary>
    public static ArchetypeTuning? FromWire(float[] values)
    {
        if (values.Length != Count) return null;
        for (var i = 0; i < Count; i++)
        {
            var v = values[i];
            if (float.IsNaN(v)) continue;
            var row = Table[i];
            if (!(v >= row.Min && v <= row.Max) || row.Whole && Math.Floor(v) != v) return null;
        }
        if (float.IsNaN(values[10]) != float.IsNaN(values[11]) || !float.IsNaN(values[10]) && values[11] <= values[10]) return null;
        return new ArchetypeTuning(values);
    }

    /// <summary>The numbers in force: this tuning where set, otherwise <paramref name="native"/> (the game block's readings).</summary>
    public ArchetypeTuning Over(ArchetypeTuning native)
    {
        var all = new float[Count];
        for (var i = 0; i < Count; i++) all[i] = float.IsNaN(_v[i]) ? native._v[i] : _v[i];
        return new ArchetypeTuning(all);
    }

    /// <summary>Rounds a borrower is granted with these numbers in force: the reserve the ratio of the pool buys, plus a full magazine.</summary>
    public double Rounds(float ammoRatio, float poolMax) => Math.Floor((double)ammoRatio * poolMax / AmmoCost) + Magazine;

    public bool Valid()
    {
        foreach (var v in _v) if (!float.IsFinite(v)) return false;
        return ShotDelay > 0 && AmmoCost > 0 && Magazine >= 1;
    }

    public bool SameAs(ArchetypeTuning other)
    {
        for (var i = 0; i < Count; i++) if (_v[i] != other._v[i] && !(float.IsNaN(_v[i]) && float.IsNaN(other._v[i]))) return false;
        return true;
    }
}

/// <summary>Why a lent heavy weapon goes back.</summary>
internal enum LoanEnd : byte { None = 0, Empty = 1, Dropped = 2, Down = 3 }

/// <summary>The local player's lent heavy weapon: decides from plain readings when the original gear comes back.</summary>
internal sealed class HeavyLoan
{
    /// <summary>Readings right after the swap can still show the old slot's ammunition; ignore emptiness this long.</summary>
    public const float SettleSeconds = 1.5f;
    /// <summary>A gun that has just fired its last round stays this long: the kick is seen and the shot reaches the host while the gun is still in the hand.</summary>
    public const float LastShotSeconds = 0.6f;

    public HeavyLoan(float startedAt) => StartedAt = startedAt;

    public float StartedAt { get; }
    public LoanEnd Ended { get; private set; }
    private float _emptySince = float.NaN;

    /// <summary>One reading per frame. <paramref name="clip"/> rounds in the weapon, <paramref name="reserve"/> rounds left in
    /// the pool. Returns the reason once, the first frame the weapon must go back.</summary>
    public LoanEnd Update(float now, int clip, int reserve, bool dropPressed, bool down)
    {
        if (Ended != LoanEnd.None) return LoanEnd.None;
        if (down) Ended = LoanEnd.Down;
        else if (dropPressed) Ended = LoanEnd.Dropped;
        else if (clip > 0 || reserve > 0) _emptySince = float.NaN;
        else if (float.IsNaN(_emptySince)) _emptySince = now;
        else if (now - StartedAt >= SettleSeconds && now - _emptySince >= LastShotSeconds) Ended = LoanEnd.Empty;
        return Ended;
    }
}

/// <summary>Host-owned launcher budget. Rejected requests never spend ammunition or advance cadence/sequence.</summary>
internal sealed class LauncherLoan
{
    internal const float OriginTolerance = 2.4f;
    internal readonly Guid Identity;
    internal readonly HeavyWeaponSpec Spec;
    internal readonly uint GearChecksum;
    internal int Remaining { get; private set; }
    private readonly float _interval;
    private float _nextShot;
    private uint _lastShot;

    /// <summary><paramref name="identity"/> is the identity the borrower already holds (a loan a new host adopts); null makes a new one.</summary>
    internal LauncherLoan(HeavyWeaponSpec spec, uint gearChecksum, int ammunition, float interval, float createdAt, Guid? identity = null)
    {
        if (spec.Grenade == null || ammunition <= 0 || !float.IsFinite(interval) || interval <= 0 || !float.IsFinite(createdAt))
            throw new ArgumentException("Invalid host launcher budget or cadence.");
        Identity = identity ?? Guid.NewGuid();
        Spec = spec;
        GearChecksum = gearChecksum;
        Remaining = ammunition;
        _interval = interval;
        _nextShot = createdAt;
    }

    /// <summary>Spend one granted round and place it on the host's native-cadence timeline. Requests that arrive together
    /// remain ordered instead of losing a shot to receive jitter.</summary>
    internal bool TrySchedule(Guid identity, uint sequence, float now, float originDistanceSquared, out float delay, out string reason)
    {
        delay = 0f;
        reason = identity != Identity ? "wrong loan identity"
            : sequence == 0 || sequence <= _lastShot ? "stale shot identity"
            : !(originDistanceSquared <= OriginTolerance * OriginTolerance) || !float.IsFinite(originDistanceSquared) ? "origin outside host-observed reach"
            : !float.IsFinite(now) || now < 0f ? "invalid host time"
            : Remaining <= 0 ? "loan ammunition exhausted" : "";
        if (reason.Length != 0) return false;
        var fireAt = Math.Max(now, _nextShot);
        var nextShot = fireAt + _interval;
        if (!float.IsFinite(fireAt) || !float.IsFinite(nextShot)) { reason = "fire schedule overflowed"; return false; }
        Remaining--;
        _lastShot = sequence;
        _nextShot = nextShot;
        delay = fireAt - now;
        return true;
    }
}

/// <summary>The host's installed loans: which borrower holds which rack's weapon, and who has switched to their own special weapon.
/// The rack's claim (reservation, install, commit) is the carrier's; a committed claim becomes a loan here.</summary>
internal sealed class LoanBook
{
    private readonly Dictionary<ulong, (ulong Summoner, uint ThrowId, uint Attempt)> _loans = new();
    /// <summary>Borrowers who switched to their own special weapon; the value is whether the host has already read that weapon in their slot.</summary>
    private readonly Dictionary<ulong, bool> _stashed = new();

    /// <summary>The borrower's claim was committed: the loan starts.</summary>
    public void Commit(ulong summoner, uint throwId, ulong player, uint attempt) => _loans[player] = (summoner, throwId, attempt);

    public bool Release(ulong summoner, uint throwId, ulong player, uint attempt)
    {
        if (!_loans.TryGetValue(player, out var loan) || loan != (summoner, throwId, attempt)) return false;
        _stashed.Remove(player);
        return _loans.Remove(player);
    }

    /// <summary>The borrower announced a switch to their own special weapon; the heavy weapon waits in their stash. The loan stays
    /// committed, so no second rack can be taken, and the missing marker is not read as a return.</summary>
    public bool Stash(ulong summoner, uint throwId, ulong player, uint attempt)
    {
        if (!_loans.TryGetValue(player, out var loan) || loan != (summoner, throwId, attempt)) return false;
        _stashed.TryAdd(player, false);
        return true;
    }

    /// <summary>The host's reading of the borrower's slot: whether the heavy weapon's marker is in it. Returns whether the loan still stands.
    /// A stashed loan stands while the marker is missing; the heavy weapon is taken back once the marker returns after the own weapon was
    /// seen (the marker that is still readable right after the switch was announced is the old reading).</summary>
    public bool Observe(ulong player, bool marked)
    {
        if (!_stashed.TryGetValue(player, out var seenOwn)) return marked;
        if (!marked) _stashed[player] = true;
        else if (seenOwn) _stashed.Remove(player);
        return true;
    }

    public bool HasLoan(ulong player) => _loans.ContainsKey(player);

    public bool Any => _loans.Count != 0;

    /// <summary>Release committed loans whose native inventory no longer carries the borrowed gear. The callback lets the
    /// host revoke any capability associated with the same player.</summary>
    internal void ReconcileLoans(Func<ulong, bool> isStillBorrowing, Action<ulong> released)
    {
        List<ulong>? returned = null;
        foreach (var loan in _loans)
        {
            if (isStillBorrowing(loan.Key)) continue;
            (returned ??= new()).Add(loan.Key);
        }
        if (returned == null) return;
        foreach (var player in returned)
        {
            _loans.Remove(player);
            _stashed.Remove(player);
            released(player);
        }
    }

    public void Clear() { _loans.Clear(); _stashed.Clear(); }
}
/// <summary>The heavy weapon's own magazine and reserve (the pool's raw ammunition value) while another gun is in the slot. The player's
/// own special weapon is kept by ForgeCore's shared special weapon slot (<c>Gear.SpecialSlot</c>).</summary>
internal sealed record AmmoStash(float Pool, int Clip);
