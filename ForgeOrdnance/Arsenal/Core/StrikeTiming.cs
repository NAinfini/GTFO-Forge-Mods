namespace ForgeOrdnance.Arsenal;

/// <summary>How one shell comes in: <see cref="Warn"/> seconds of its landing mark and whistle, then <see cref="Drop"/> seconds of the
/// shell itself falling at full speed. The host counts its damage from <see cref="Lead"/>; every peer draws from the same numbers.</summary>
internal readonly record struct ShellPace(float Warn, float Drop)
{
    public float Lead => Warn + Drop;
}

/// <summary>The pacing of a strike from the host's planning to the last sound, one timeline for the host (damage, areas, the
/// beam) and every peer (shells, blasts, areas, the beam): all of it counts from the same moments, so what is heard, seen and
/// hurt line up. The seconds before these (the beacon's countdown) are each definition's <c>CallInDelay</c>.</summary>
internal static class StrikeTiming
{
    /// <summary>The pace of each kind's shells. The shell's top is 3 m above the ceiling, so a <see cref="ShellPace.Drop"/> of 0.4 s is about
    /// 20 to 35 m/s and the shell is in sight for a fifth of a second or more. Single heavy shells warn longest; every warning is long enough to read
    /// its ring and step out of the way.</summary>
    public static ShellPace Pace(StrikeKind kind) => kind switch
    {
        StrikeKind.Precision => new(0.9f, 0.4f),
        StrikeKind.Area or StrikeKind.Creeping => new(0.7f, 0.3f),
        StrikeKind.Cluster => new(0.6f, 0.35f),
        StrikeKind.Incendiary => new(0.8f, 0.35f),
        StrikeKind.Gas or StrikeKind.Stasis => new(0.7f, 0.45f),
        StrikeKind.Hunter => new(0f, 0.35f),
        _ => new(0f, 0f),
    };

    /// <summary>Fired shells come in on a slant: this much sideways per metre of fall (about 12 degrees off vertical). The side is the call's
    /// (<see cref="StrikePlan.ApproachSide"/>), so a barrage reads as one battery firing from one direction. Gas canisters and stasis emitters are dropped straight;
    /// the hunter's shell runs down its laser.</summary>
    public const float ApproachSlant = 0.22f;

    /// <summary>True for the kinds whose shells come in on <see cref="ApproachSlant"/>.</summary>
    public static bool Slants(StrikeKind kind) => kind is StrikeKind.Precision or StrikeKind.Area or StrikeKind.Creeping or StrikeKind.Cluster or StrikeKind.Incendiary;

    /// <summary>The fraction of the fall done at <paramref name="t"/> (0 to 1) of its time: the shell picks up speed all the way down, which is what gives it its weight.</summary>
    public static float FallCurve(float t) => t * (0.45f + 0.55f * t);

    /// <summary>Where in the incoming whistle (2.4 s, loudest here, fading after) a shell's impact must fall.</summary>
    public const float WhistlePeak = 1.8f;

    /// <summary>Where to start the whistle so that its peak meets an impact <paramref name="lead"/> seconds away.</summary>
    public static float WhistleStart(float lead) => lead >= WhistlePeak ? 0f : WhistlePeak - lead;

    /// <summary>Where in the beam start sound (3 s) it peaks: the charge or the lock ends on it.</summary>
    public const float ChargePeak = 2.2f;

    /// <summary>Where to start the beam start sound so that its peak comes <paramref name="lead"/> seconds from now.</summary>
    public static float ChargeStart(float lead) => lead >= ChargePeak ? 0f : ChargePeak - lead;

    /// <summary>Seconds the hunter's laser stays on its target before the shell drops.</summary>
    public const float HunterLockSeconds = 1f;

    /// <summary>The least seconds the laser stays on a new target after its first one died.</summary>
    public const float HunterRelockSeconds = 0.35f;

    /// <summary>Seconds the hunter's laser sweeps around the beacon for an enemy before it gives up.</summary>
    public const float HunterSearchSeconds = 5f;

    /// <summary>What is left of the cooldown when the hunter found no target in the room (the use is given back too).</summary>
    public const float HunterMissCooldown = 30f;

    /// <summary>Seconds between the host's laser updates (the peers carry the point on between them).</summary>
    public const float HunterSendSeconds = 0.1f;

    /// <summary>Seconds from a strafing call's beacon landing to its run starting at the beacon.</summary>
    public const float StrafeLeadSeconds = 1.2f;

    /// <summary>Seconds after a shell's blast that its debris is heard coming down.</summary>
    public const float DebrisDelay = 0.5f;

    /// <summary>Seconds after the incendiary shell lands that the ignition is heard.</summary>
    public const float IgniteDelay = 0.15f;

    /// <summary>Seconds the sky glow charges before the beam is drawn and starts to cut and move.</summary>
    public const float BeamChargeSeconds = 1f;

    /// <summary>Seconds between the stasis field's beats while it runs.</summary>
    public const float StasisBeatSeconds = 2.5f;

    /// <summary>Seconds from a gas, stasis or incendiary shell's landing to its area starting: the damage, the slow, the cloud, the
    /// light and the loop. The landing's own release, pulse or ignition plays first.</summary>
    public static float ZoneStartDelay(StrikeKind kind) => kind switch
    {
        StrikeKind.Gas => 0.4f,
        StrikeKind.Incendiary => 0.25f,
        StrikeKind.Stasis => 0.3f,
        _ => 0f,
    };
}
