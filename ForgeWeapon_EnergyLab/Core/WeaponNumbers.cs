namespace ForgeWeaponEnergyLabExperimental;

internal static class EnergyLimits
{
    internal const int ArcTargets = 4, PulseTargets = 3;
    // Shots without a tuned range: farther than any GTFO sightline, and finite so a miss still has a drawable end.
    internal const float Reach = 1000f;
}

internal readonly record struct ShotTuning(float Range, double Interval, float Damage, float Stagger, float Precision);

// Flattened once at installation. No record allocation, reflection or string dispatch during combat.
internal readonly record struct WeaponNumbers
{
    internal ShotTuning Shot { get; init; }
    internal ShotTuning Pulse { get; init; }
    internal int Targets { get; init; }
    internal float JumpRange { get; init; }
    internal float DamageRetention { get; init; }
    internal float ArcLifetime { get; init; }
    internal float HopDelay { get; init; }
    internal double MinimumCharge { get; init; }
    internal double FullCharge { get; init; }
    internal float BotChargePower { get; init; }
    internal float Speed { get; init; }
    internal float FullSpeed { get; init; }
    internal float BodyRadius { get; init; }
    internal float FullBodyRadius { get; init; }
    internal float TravelDistance { get; init; }
    internal float FullTravelDistance { get; init; }
    internal float FullDamage { get; init; }
    internal int ActiveProjectiles { get; init; }
    internal float BlastRadius { get; init; }
    internal float FullBlastRadius { get; init; }
    internal float ExplosionLifetime { get; init; }
    internal float NoiseRadius { get; init; }
    internal float ExplosionForce { get; init; }
    internal float ArcRadius { get; init; }
    internal float FullArcRadius { get; init; }
    internal float PulseDamageFraction { get; init; }
    internal int PulseTargets { get; init; }
    internal float FadeLifetime { get; init; }
    internal int Bounces { get; init; }
    internal float Lifetime { get; init; }
    internal float StartRadius { get; init; }
    internal float RadiusPerMetre { get; init; }
    internal float FlightLifetime { get; init; }
    internal float Gravity { get; init; }
    internal float Radius { get; init; }
    internal float CoreRadius { get; init; }
    internal float CollapseDuration { get; init; }
    internal float GrowthDuration { get; init; }
    internal float EdgeDamageFraction { get; init; }
    internal float PullSpeed { get; init; }
    internal float QueryInterval { get; init; }

    internal static WeaponNumbers Resolve(EnergyTuning t)
    {
        var range = t switch { ArcChainTuning arc => arc.Range, FlameTuning flame => flame.Range, _ => EnergyLimits.Reach };
        var values = new WeaponNumbers { Shot = new(range, t.Interval, t.Damage, t.Stagger, t.Precision) };
        if (t is PlasmaTuning orb)
            values = values with { MinimumCharge = orb.MinimumCharge, FullCharge = orb.FullCharge,
                BotChargePower = orb.BotChargePower,
                Speed = orb.Speed, FullSpeed = orb.FullSpeed, BodyRadius = orb.BodyRadius, FullBodyRadius = orb.FullBodyRadius,
                TravelDistance = orb.TravelDistance, FullTravelDistance = orb.FullTravelDistance, FullDamage = orb.FullDamage,
                ActiveProjectiles = orb.ActiveProjectiles };
        return t switch
        {
            ArcChainTuning arc => values with { Targets = arc.Targets, JumpRange = arc.JumpRange, DamageRetention = arc.DamageRetention, ArcLifetime = arc.ArcLifetime, HopDelay = arc.HopDelay },
            PlasmaBlastTuning blastOrb => values with { BlastRadius = blastOrb.BlastRadius, FullBlastRadius = blastOrb.FullBlastRadius, ExplosionLifetime = blastOrb.ExplosionLifetime, NoiseRadius = blastOrb.NoiseRadius, ExplosionForce = blastOrb.ExplosionForce },
            PlasmaArcTuning arcOrb => values with { ArcRadius = arcOrb.ArcRadius, FullArcRadius = arcOrb.FullArcRadius,
                Pulse = new(0, arcOrb.PulseInterval, 0, arcOrb.PulseStagger, arcOrb.Precision), PulseDamageFraction = arcOrb.PulseDamageFraction, PulseTargets = arcOrb.PulseTargets, FadeLifetime = arcOrb.FadeLifetime },
            BlastTuning blast => values with { BlastRadius = blast.BlastRadius, FullBlastRadius = blast.BlastRadius, ExplosionLifetime = blast.ExplosionLifetime, ActiveProjectiles = blast.ActiveProjectiles, NoiseRadius = blast.NoiseRadius, ExplosionForce = blast.ExplosionForce },
            DiscTuning disc => values with { Speed = disc.Speed, BodyRadius = disc.BodyRadius, Bounces = disc.Bounces, Lifetime = disc.Lifetime, ActiveProjectiles = disc.ActiveProjectiles },
            FlameTuning flame => values with { StartRadius = flame.StartRadius, RadiusPerMetre = flame.RadiusPerMetre },
            BlackHoleTuning hole => values with { Speed = hole.Speed, BodyRadius = hole.BodyRadius, FlightLifetime = hole.FlightLifetime, Gravity = hole.Gravity,
                Radius = hole.Radius, CoreRadius = hole.CoreRadius, Lifetime = hole.Lifetime, CollapseDuration = hole.CollapseDuration, GrowthDuration = hole.GrowthDuration,
                Pulse = new(hole.Radius, hole.PulseInterval, hole.Damage, hole.Stagger, hole.Precision), EdgeDamageFraction = hole.EdgeDamageFraction, PullSpeed = hole.PullSpeed, QueryInterval = hole.QueryInterval },
            _ => values
        };
    }
}
