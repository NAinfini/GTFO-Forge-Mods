
namespace ForgeOrdnanceFrames;

/// <summary>When and how much the suits' effects emit, as pure functions of the record's own times (no Unity types, so the
/// schedules are tested offline). <c>FrameFx</c> draws; <c>FrameVisual</c> asks this class what to emit this frame. Every peer
/// derives the same answer from the replicated state, so nothing is sent for effects.</summary>
public static class FrameEffectsPlan
{
    // ---- arrival ----

    /// <summary>The falling suit's thrusters stay dark until this fraction of the fall.</summary>
    public const float ThrustFrom = 0.12f;
    /// <summary>The touchdown blast (the thrusters kick the dust off the ground) starts at this fraction of the fall.</summary>
    public const float KickFrom = 0.92f;

    /// <summary>0 before the thrusters light, rising to 1 at touchdown (they burn hardest near the ground).</summary>
    public static float ArrivalThrust(float fall)
    {
        var t = (fall - ThrustFrom) / (1f - ThrustFrom);
        return t <= 0f ? 0f : t >= 1f ? 1f : t * t;
    }

    /// <summary>The flame's speed away from the nozzle (m/s), on top of the suit's own: faster, so longer, the harder it burns.</summary>
    public static float JetSpeed(float strength) => 10f + 10f * strength;

    /// <summary>The flame's width in metres (its length follows from the speed and the particle stretch).</summary>
    public static float JetSize(float strength) => 0.45f + 0.5f * strength;

    /// <summary>How long one flame particle lives, seconds.</summary>
    public static float JetLife(float strength) => 0.11f + 0.06f * strength;

    /// <summary>The thruster strength of a charging suit.</summary>
    public const float ChargeJet = 0.6f;

    // ---- charge ----

    /// <summary>Seconds between a charging suit's flame and dust emissions.</summary>
    public const float ChargeInterval = 0.06f;
    /// <summary>The foot dust is emitted on every second charge tick.</summary>
    public const int ChargeDustEvery = 2;

    // ---- wreck ----

    /// <summary>The fire dies down over this long before the wreck is removed.</summary>
    public const float WreckFadeSeconds = 3f;
    /// <summary>The wreck sinks into the ground over this long, ending when it is removed (its removal is hidden by the floor).</summary>
    public const float WreckSinkSeconds = 2f;
    public const float WreckFlameInterval = 0.16f;
    public const float WreckSmokeInterval = 0.14f;
    /// <summary>Smoke stops this long before removal; what is already in the air thins out on its own.</summary>
    public const float WreckSmokeLead = 1.5f;

    /// <summary>1 while the wreck burns at full, falling to 0 over its last <see cref="WreckFadeSeconds"/>.</summary>
    public static float WreckFire(float age, float life)
    {
        if (age < 0f || age >= life) return 0f;
        var t = (age - (life - WreckFadeSeconds)) / WreckFadeSeconds;
        return t <= 0f ? 1f : 1f - (t >= 1f ? 1f : t);
    }

    /// <summary>0 while the wreck stands, rising to 1 as it sinks in its last <see cref="WreckSinkSeconds"/>.</summary>
    public static float WreckSink(float age, float life)
    {
        var t = (age - (life - WreckSinkSeconds)) / WreckSinkSeconds;
        return t <= 0f ? 0f : t >= 1f ? 1f : t;
    }

    public static bool WreckSmoking(float age, float life) => age >= 0f && age < life - WreckSmokeLead;

    /// <summary>The flash of light at the moment of destruction fades over this long.</summary>
    public const float ExplosionLightSeconds = 0.45f;

    /// <summary>1 at the moment of destruction, falling straight to 0 over <see cref="ExplosionLightSeconds"/>: the flash light's strength.</summary>
    public static float ExplosionFlash(float age) => age <= 0f ? 1f : age >= ExplosionLightSeconds ? 0f : 1f - age / ExplosionLightSeconds;

    // ---- overheat ----

    /// <summary>Haze starts at this sprint heat (0..1).</summary>
    public const float HeatFrom = 0.6f;

    /// <summary>0 below <see cref="HeatFrom"/>, rising to 1 at full heat; an overheated suit is always 1.</summary>
    public static float HeatLevel(float heat, bool overheated)
    {
        if (overheated) return 1f;
        var t = (heat - HeatFrom) / (1f - HeatFrom);
        return t <= 0f ? 0f : t >= 1f ? 1f : t;
    }

    /// <summary>Seconds between haze puffs from each shoulder at a heat level (slow at first, quick when overheated).</summary>
    public static float HeatInterval(float level) => 0.34f - 0.22f * level;

    // ---- rockets ----

    /// <summary>A rocket's tail leaves this many flames a second, and smoke with every second one.</summary>
    public const float RocketTailFlamesPerSecond = 1f / 0.045f;
    public const float RocketTailSmokePerSecond = RocketTailFlamesPerSecond / 2f;

    // ---- barrier pulse ----

    /// <summary>The push pulse rings at most this often per suit.</summary>
    public const float PulseGap = 0.75f;
}
