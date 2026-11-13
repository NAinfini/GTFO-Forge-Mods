
namespace ForgeOrdnance.Frames;

/// <summary>When and how much the suits' effects emit, as pure functions of the record's own times (no Unity types, so the
/// schedules are tested offline). <c>FrameFx</c> draws; <c>FrameVisual</c> asks this class what to emit this frame. Every peer
/// derives the same answer from the replicated state, so nothing is sent for effects.</summary>
internal static class FrameEffectsPlan
{

    // ---- arrival ----

    /// <summary>The touchdown blast (the dust-and-ring hit on the ground) starts at this fraction of the fall.</summary>
    public const float KickFrom = 0.92f;

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

    // ---- barrier pulse ----

    /// <summary>The push pulse rings at most this often per suit.</summary>
    public const float PulseGap = 0.75f;
}
