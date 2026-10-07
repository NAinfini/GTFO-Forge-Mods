using System;
using System.Numerics;

namespace ForgeWeaponEnergyLabExperimental;

internal static class SpecialWeaponMath
{
    internal static float HoleDamageAt(float distance, in WeaponNumbers tuning)
    {
        if (!float.IsFinite(distance) || distance < 0f || distance > tuning.Radius) return 0f;
        return tuning.Pulse.Damage * (1f - (1f - tuning.EdgeDamageFraction) * Math.Clamp(
            (distance - tuning.CoreRadius) / (tuning.Radius - tuning.CoreRadius), 0f, 1f));
    }

    public static Vector3 Reflect(Vector3 incoming, Vector3 normal)
    {
        if (!float.IsFinite(incoming.LengthSquared()) || !float.IsFinite(normal.LengthSquared())
            || incoming.LengthSquared() < 0.000001f || normal.LengthSquared() < 0.000001f)
            throw new ArgumentException("Ricochet needs a travel direction and surface normal.");
        return Vector3.Normalize(Vector3.Reflect(Vector3.Normalize(incoming), Vector3.Normalize(normal)));
    }

    public static bool InFlame(Vector3 start, Vector3 direction, Vector3 point, float range, in WeaponNumbers tuning)
    {
        if (!float.IsFinite(range) || range <= 0f || !float.IsFinite(direction.LengthSquared())
            || direction.LengthSquared() < 0.000001f) return false;
        direction = Vector3.Normalize(direction);
        var offset = point - start;
        var along = Vector3.Dot(offset, direction);
        if (along < 0f || along > range) return false;
        var width = tuning.StartRadius + along * tuning.RadiusPerMetre;
        return (offset - direction * along).LengthSquared() <= width * width;
    }

    public static Vector3 PullStep(Vector3 position, Vector3 center, float delta, in WeaponNumbers tuning)
    {
        var offset = center - position;
        var distance = offset.Length();
        if (!float.IsFinite(distance) || !float.IsFinite(delta) || delta <= 0f || distance <= tuning.CoreRadius || distance > tuning.Radius)
            return Vector3.Zero;
        // Frame stalls must not teleport a victim across a wall or through the core.
        return offset / distance * MathF.Min(distance - tuning.CoreRadius, tuning.PullSpeed * MathF.Min(delta, 0.05f));
    }
}

internal readonly record struct PlasmaPayload(float Speed, float BodyRadius, float TravelDistance, float Damage, float BlastRadius, float ArcRadius, float ArcDamage)
{
    internal float Lifetime => TravelDistance / Speed;
    internal static PlasmaPayload FromCharge(float charge, in WeaponNumbers tuning)
    {
        if (!float.IsFinite(charge)) throw new ArgumentOutOfRangeException(nameof(charge));
        charge = Math.Clamp(charge, 0f, 1f);
        static float Lerp(float low, float high, float power) => low + (high - low) * power;
        var damage = Lerp(tuning.Shot.Damage, tuning.FullDamage, charge);
        return new(Lerp(tuning.Speed, tuning.FullSpeed, charge), Lerp(tuning.BodyRadius, tuning.FullBodyRadius, charge),
            Lerp(tuning.TravelDistance, tuning.FullTravelDistance, charge), damage,
            Lerp(tuning.BlastRadius, tuning.FullBlastRadius, charge), Lerp(tuning.ArcRadius, tuning.FullArcRadius, charge), damage * tuning.PulseDamageFraction);
    }
}

// Remember each enemy for the entire flight, not each limb or each physics frame.
internal sealed class PlasmaContacts
{
    public const int Capacity = 128;
    private readonly long[] _identities = new long[Capacity];
    private int _count;

    public bool Remember(long identity)
    {
        if (identity == 0) throw new ArgumentOutOfRangeException(nameof(identity));
        if (_identities.AsSpan(0, _count).Contains(identity)) return false;
        if (_count == Capacity) throw new InvalidOperationException("Plasma contact identity budget exhausted; no truncated result used.");
        _identities[_count++] = identity;
        return true;
    }

    public void Reset() => _count = 0;
}

/// <summary>Holding charges without a time limit; release fires.</summary>
internal sealed class PlasmaCharge
{
    private double? _started;
    public bool Charging => _started.HasValue;
    public bool Ready(double now, in WeaponNumbers tuning) => _started is { } start && double.IsFinite(now)
        && now - start + 1e-7 >= tuning.MinimumCharge;
    public float Power(double now, in WeaponNumbers tuning) => _started is { } start
        ? (float)Math.Clamp((now - start - tuning.MinimumCharge)
            / (tuning.FullCharge - tuning.MinimumCharge), 0d, 1d) : 0f;

    public bool Update(double now, bool held, bool pressed, bool allowed, in WeaponNumbers tuning, out float power)
    {
        power = 0f;
        if (!allowed || !double.IsFinite(now)) { Cancel(); return false; }
        if (held) { if (pressed) _started ??= now; return false; }
        if (!_started.HasValue) return false;
        power = Power(now, tuning);
        var ready = Ready(now, tuning);
        Cancel();
        return ready;
    }

    public void Cancel() => _started = null;
}

internal readonly record struct ArcCandidate(long Identity, float DistanceSquared, bool Eligible);

internal static class ArcSelection
{
    // Native code resolves visibility, marks blocked candidates ineligible, and asks again.
    public static int Nearest(ReadOnlySpan<ArcCandidate> candidates, ReadOnlySpan<long> visited, float range)
    {
        var best = -1;
        for (var index = 0; index < candidates.Length; index++)
        {
            var item = candidates[index];
            if (!item.Eligible || item.Identity == 0 || visited.Contains(item.Identity)
                || !float.IsFinite(item.DistanceSquared) || item.DistanceSquared < 0f
                || !float.IsFinite(range) || range < 0f || item.DistanceSquared > range * range) continue;
            if (best < 0 || item.DistanceSquared < candidates[best].DistanceSquared
                || (item.DistanceSquared == candidates[best].DistanceSquared && item.Identity < candidates[best].Identity)) best = index;
        }
        return best;
    }
}
