using System.Text.Json;
using ForgeCore;

namespace ForgeOrdnance;

/// <summary>
/// How a turret or standing device settles in and goes out again, all of it data so another model changes only these numbers. Two models:
/// the base (the turret shell: a drill below, a sealed top of two lids <c>lid_a</c> and <c>lid_b</c>) and the upper part (the turret or
/// device) hidden inside it. The base falls drill first and bores down until its rim is level with the ground; after <see cref="HoldSeconds"/>
/// the two lids are thrown off to either side (<see cref="LidSpeed"/>, <see cref="LidLift"/>, <see cref="LidSpin"/>), lie
/// <see cref="LidRestSeconds"/> and vanish, with a puff of dust; <see cref="LidClearSeconds"/> later the upper part rises out of the opening
/// over <see cref="RiseSeconds"/>, its barrel straight up, and a gun turns level over <see cref="TurnSeconds"/>; only then is it ready.
/// At the end the barrel turns up, the upper part sinks back inside and the base sinks out of sight over <see cref="LeaveSeconds"/>. A base
/// model without lids skips that step. The base's own size is read from its model (scaled by <see cref="BaseScale"/>): its lowest point is the drill tip, the top of its <c>body</c> the rim.
/// </summary>
public sealed class BurrowSpec
{
    /// <summary>The definition fields <see cref="Parse"/> reads, for a payload's known-field list.</summary>
    public static readonly string[] Fields = { "BaseScale", "BurrowSeconds", "HoldSeconds", "LidSpeed", "LidLift", "LidSpin", "LidRestSeconds", "LidClearSeconds", "RiseHeight", "RiseSeconds", "TurnSeconds", "LeaveSeconds" };

    /// <summary>World scale of the base model, so that the upper part fits its opening.</summary>
    public float BaseScale { get; set; } = 1f;
    /// <summary>Seconds the base takes to bore in after touchdown.</summary>
    public float BurrowSeconds { get; set; } = 0.6f;
    /// <summary>Seconds the bored-in base waits before its lids go.</summary>
    public float HoldSeconds { get; set; } = 0.3f;
    /// <summary>Metres a second the lids fly out sideways, and up; degrees a second they tumble; seconds they lie before vanishing.</summary>
    public float LidSpeed { get; set; } = 4f;
    public float LidLift { get; set; } = 5f;
    public float LidSpin { get; set; } = 540f;
    public float LidRestSeconds { get; set; } = 1.5f;
    /// <summary>Seconds from the lids going until the upper part starts to rise (the lids clear the opening).</summary>
    public float LidClearSeconds { get; set; } = 0.35f;
    /// <summary>Metres the upper part is hidden below its standing place inside the base: its whole height, barrel up.</summary>
    public float RiseHeight { get; set; } = 1.6f;
    /// <summary>Seconds the upper part takes to rise out of the base (and to sink back); 0 shows it at once.</summary>
    public float RiseSeconds { get; set; } = 1.2f;
    /// <summary>Seconds a gun takes to turn from barrel up to level (and back); 0 for an upper part with no gun.</summary>
    public float TurnSeconds { get; set; }
    /// <summary>Seconds the empty base takes to sink out of sight at the end.</summary>
    public float LeaveSeconds { get; set; } = 0.8f;

    /// <summary>Seconds from the upper part starting to rise until it is ready, and again from the end until it is hidden.</summary>
    public float HeadSeconds => RiseSeconds + TurnSeconds;

    /// <summary>The first number that is out of range as (field, message), or null when they are usable.</summary>
    internal (string Field, string Message)? Check()
    {
        (string Field, string Message)? Bad(string field, float v, float min, float max)
            => float.IsFinite(v) && v >= min && v <= max ? null : (field, "must be between " + min + " and " + max + ".");
        return Bad("BaseScale", BaseScale, 0.2f, 6f) ?? Bad("BurrowSeconds", BurrowSeconds, 0.2f, 3f) ?? Bad("HoldSeconds", HoldSeconds, 0f, 3f) ?? Bad("LidSpeed", LidSpeed, 0f, 20f)
            ?? Bad("LidLift", LidLift, 0f, 20f) ?? Bad("LidSpin", LidSpin, 0f, 3000f) ?? Bad("LidRestSeconds", LidRestSeconds, 0f, 10f)
            ?? Bad("LidClearSeconds", LidClearSeconds, 0f, 3f) ?? Bad("RiseHeight", RiseHeight, 0.1f, 6f) ?? Bad("RiseSeconds", RiseSeconds, 0f, 6f)
            ?? Bad("TurnSeconds", TurnSeconds, 0f, 3f) ?? Bad("LeaveSeconds", LeaveSeconds, 0.2f, 3f);
    }

    /// <summary>What is wrong with these numbers, or null when they are usable.</summary>
    public string? Problem() => Check() is { } bad ? bad.Field + " " + bad.Message : null;

    /// <summary>Reads the <see cref="Fields"/> present in a definition's payload over <paramref name="defaults"/> (a fresh spec when null).
    /// Throws <see cref="StrictJsonException"/> for a number out of range.</summary>
    public static BurrowSpec Parse(JsonElement payload, BurrowSpec? defaults = null)
    {
        var d = defaults ?? new BurrowSpec();
        float N(string field, float fallback) => (float)StrictJson.Number(payload, "", field, fallback, -1e9, 1e9);
        var spec = new BurrowSpec
        {
            BaseScale = N("BaseScale", d.BaseScale),
            BurrowSeconds = N("BurrowSeconds", d.BurrowSeconds),
            HoldSeconds = N("HoldSeconds", d.HoldSeconds),
            LidSpeed = N("LidSpeed", d.LidSpeed),
            LidLift = N("LidLift", d.LidLift),
            LidSpin = N("LidSpin", d.LidSpin),
            LidRestSeconds = N("LidRestSeconds", d.LidRestSeconds),
            LidClearSeconds = N("LidClearSeconds", d.LidClearSeconds),
            RiseHeight = N("RiseHeight", d.RiseHeight),
            RiseSeconds = N("RiseSeconds", d.RiseSeconds),
            TurnSeconds = N("TurnSeconds", d.TurnSeconds),
            LeaveSeconds = N("LeaveSeconds", d.LeaveSeconds),
        };
        if (spec.Check() is { } bad) throw new StrictJsonException(bad.Field, bad.Message);
        return spec;
    }
}

/// <summary>Where the upper part is at a moment: <see cref="Lift"/> 0 (hidden in the base) to 1 (standing), <see cref="Level"/> 0 (barrel up)
/// to 1 (level, free for the game's own aiming).</summary>
internal readonly record struct BurrowPose(float Lift, float Level);

/// <summary>The upper part's one timeline, the same on every peer: rise, turn down, (work), turn up, sink.</summary>
internal static class BurrowTimeline
{

    private static float Fraction(float seconds, float length) => length <= 0f ? (seconds >= 0f ? 1f : 0f) : seconds / length;

    /// <summary>The pose <paramref name="t"/> seconds after the upper part started to rise, when it starts to stow <paramref name="stowAt"/> seconds
    /// after that (<see cref="float.PositiveInfinity"/>: never).</summary>
    internal static BurrowPose Pose(BurrowSpec burrow, float t, float stowAt)
    {
        if (t < stowAt)
            return new BurrowPose(ForgeUI.Ease.Smoothstep(Fraction(t, burrow.RiseSeconds)), burrow.TurnSeconds <= 0f ? 1f : ForgeUI.Ease.Smoothstep(Fraction(t - burrow.RiseSeconds, burrow.TurnSeconds)));
        var s = t - stowAt;
        var level = burrow.TurnSeconds <= 0f ? 1f : 1f - ForgeUI.Ease.Smoothstep(Fraction(s, burrow.TurnSeconds));
        return new BurrowPose(1f - ForgeUI.Ease.Smoothstep(Fraction(s - burrow.TurnSeconds, burrow.RiseSeconds)), level);
    }

    /// <summary>True once the upper part is hidden again.</summary>
    internal static bool Stowed(BurrowSpec burrow, float t, float stowAt) => t >= stowAt + burrow.HeadSeconds;
}
