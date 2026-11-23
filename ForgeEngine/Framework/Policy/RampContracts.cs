using System;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>
/// One `forge.control.flow.ramp` envelope, resolved from the step's own inputs when it is entered. The row walks a
/// value from `from` to `to` over `duration` ticks while publishing it, and — when the author asked for one — waits
/// `release_delay` ticks at the far end and walks back to `from` over `release_duration` ticks.
///
/// <b>What a pulse is.</b> The envelope is one pulse per tick, and every pulse re-enters the row's single execution
/// output `next`, so the region after a ramp runs once per tick and reads the value that tick holds. Pulse
/// <c>i</c> of the ramp leg publishes the value the envelope holds at the <i>end</i> of its i-th tick —
/// <c>ease(i / duration)</c>, which is exactly `to` on the last ramp pulse — and the release leg's last pulse is
/// exactly `from`. `from` itself is the envelope's starting point rather than a published tick: the author already
/// holds it as the row's own input. A ramp with no release ticks holds at `to` through its last pulse and ends.
///
/// <b>The curve.</b> `exponent` shapes a leg as <c>t^exponent</c>: 1 is the straight line, a value above 1 starts
/// slow and ends fast, a value below 1 starts fast and ends slow. It is required to be finite and positive, because
/// a zero or negative exponent is not a curve any author can reason about.
///
/// <b>`mode`.</b> `continuous` publishes the exact interpolated value; `stepped` publishes it rounded to a whole
/// number, which is the "grow in unit steps" form of the same envelope.
///
/// <b>Cancellation.</b> The handle the row publishes is a live timer, so a `cancel` on it ends the envelope where
/// it stands and spends the handle — after which no further pulse runs. Falling back after a cancellation is what
/// the `release_*` pair is for, and it is a leg of this same envelope rather than a cancellation hook, because a
/// cancelled timer is a timer that no longer exists to publish anything.
/// </summary>
internal sealed record RampEnvelope(double From, double To, int DurationTicks, int ReleaseTicks, int ReleaseDelayTicks,
    double Exponent, bool Stepped)
{
    /// <summary>The most pulses one ramp may run, so a long ramp is refused by the same ceiling every other
    /// counted control uses rather than by the schedule pool's own.</summary>
    public const int MaximumPulses = 1024;

    /// <summary>The whole pulse series: the ramp leg, the hold at the far end, and the release leg.</summary>
    public int TotalPulses => DurationTicks + ReleaseDelayTicks + ReleaseTicks;

    /// <summary>
    /// Reads one ramp step's inputs. Every number is required — the row declares all seven of its ports, so a plan
    /// that wired none of them is refused by name instead of run on an invented default — and the three tick counts
    /// stay inside the same iteration ceiling a counted control is held to.
    /// </summary>
    internal static RampEnvelope Read(JsonElement inputs, JsonElement parameters, string detail)
    {
        var from = Number(inputs, "from", detail);
        var to = Number(inputs, "to", detail);
        var exponent = Number(inputs, "exponent", detail);
        RuntimeJson.Require(double.IsFinite(exponent) && exponent > 0d, "ramp-exponent", detail);
        var duration = Ticks(inputs, "duration", detail, minimum: 1);
        var release = Ticks(inputs, "release_duration", detail, minimum: 0);
        var delay = Ticks(inputs, "release_delay", detail, minimum: 0);
        RuntimeJson.Require((long)duration + release + delay <= MaximumPulses, RuntimeAbiCodes.IterationBudget, detail);
        return new RampEnvelope(from, to, duration, release, delay, exponent, Mode(parameters, detail) == "stepped");
    }

    /// <summary>The value one pulse publishes, addressed by the 1-based pulse number the schedule dispatched.</summary>
    internal double ValueAt(int pulse)
    {
        if (pulse <= DurationTicks) return At(Ease((double)pulse / DurationTicks));
        var held = DurationTicks + ReleaseDelayTicks;
        if (pulse <= held || ReleaseTicks == 0) return Publish(To);
        return At(1d - Ease((double)(pulse - held) / ReleaseTicks));
    }

    private double At(double eased) => Publish(From + (To - From) * eased);

    private double Publish(double value) => Stepped ? Math.Round(value, MidpointRounding.AwayFromZero) : value;

    private double Ease(double fraction) => Math.Pow(fraction, Exponent);

    private static double Number(JsonElement inputs, string port, string detail)
    {
        if (!inputs.TryGetProperty(port, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new RuntimeContractException("ramp-input", detail + "." + port);
        return number;
    }

    private static int Ticks(JsonElement inputs, string port, string detail, int minimum)
    {
        var value = Number(inputs, port, detail);
        RuntimeJson.Require(value == Math.Floor(value) && value >= minimum && value <= MaximumPulses, "ramp-input", detail + "." + port);
        return (int)value;
    }

    /// <summary>The row's one structural parameter, as the member name the kernel resolved it to. An absent one is
    /// `continuous`, which is the member the catalog lists first.</summary>
    private static string Mode(JsonElement parameters, string detail)
    {
        if (!parameters.TryGetProperty("mode", out var value) || value.ValueKind != JsonValueKind.String) return "continuous";
        var mode = value.GetString()!;
        RuntimeJson.Require(mode is "continuous" or "stepped", "ramp-mode", detail);
        return mode;
    }
}
