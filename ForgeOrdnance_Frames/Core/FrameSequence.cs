using System;

namespace ForgeOrdnanceFrames;

/// <summary>Timing of boarding and leaving, in seconds from the host's state start. Every peer derives the suit's plates, its steam and the
/// pilot's leaving motion (<see cref="PilotMotion"/>) from that start, so a late joiner lands at the right point; the pilot's own screen
/// uses the same numbers. The suit never moves during either sequence.
/// Boarding (<see cref="EnterSeconds"/>, no pilot motion): the plates open in front of the pilot on the model's "open" clip (open by
/// <see cref="OpenBy"/>) while the pilot stands still, the view where it was, and the screen fades out; at
/// <see cref="Black"/> the pilot's own screen is black, the pilot is put on the seat and is no longer drawn for anyone, the power-on starts and
/// the plates close from there (<see cref="CloseStart"/>); the boarding ends, and the pilot moves, the moment the power-on's last block
/// starts to come on (<see cref="BootPlan.ControlsAt"/>), the last blocks and the seams finishing while the pilot walks. Leaving (<see cref="ExitSeconds"/>): the power-down goes black block by block, the plates open under
/// it, the pilot pulls out of the suit and hops back (<see cref="ExitClipStart"/>), the black stays while the pilot lands behind the suit and
/// the camera eases to the player's own eye (<see cref="FrameBootScreen.DownClearStart"/>), and the leaving ends once the black has cleared; the suit then closes on its own
/// (<see cref="ClosePlates"/>) without holding anyone.</summary>
internal static class FrameSequence
{
    /// <summary>Boarding: the pilot's screen fades to black over the last <see cref="FadeSeconds"/> up to <see cref="Black"/>, when the
    /// plates stand open: the pilot is inside (put on the seat, hidden from everyone) and the power-on starts.</summary>
    public const float Black = 1.9f, FadeSeconds = 0.3f;
    /// <summary>Boarding: the plates start closing on the hidden pilot at the black, so they latch by the boarding's end.</summary>
    public const float CloseStart = Black;
    /// <summary>The worn suit stands this far behind its pilot's feet: the pilot's eye sits at the front of its folded helmet.</summary>
    public const float CockpitBack = 0.3f;
    /// <summary>How far from the suit's feet the exit spot is (first choice straight behind), metres.</summary>
    public const float ExitClearance = 1.0f;
    /// <summary>Boarding: the plates stand open by here (the open clip plays faster than its own pace when it must).</summary>
    public const float OpenBy = Black;
    /// <summary>A landed suit settles this long (landing sound and its tail) before it can be boarded: the arrival state lasts the
    /// fall plus this, and the boarding prompt shows once it is over.</summary>
    public const float LandSettleSeconds = 1.5f;
    /// <summary>Near clip plane while the pilot's camera rides the head out of the suit, metres.</summary>
    public const float NearClip = 0.02f;
    /// <summary>Leaving: from the landing the camera goes from the head back to the player's own eye over this long, while the player
    /// already moves.</summary>
    public const float HandBackSeconds = 0.25f;

    /// <summary>Leaving: the plates open from the start, under the power-down, and stand open by <see cref="ExitClipStart"/> (every block is
    /// black), when the pilot's leaving motion starts.</summary>
    public const float ExitOpenStart = 0f, ExitClipStart = FrameBootScreen.DownDark;
    /// <summary>Leaving: the pilot lands behind the suit; the leaving state ends here, but never before the power-down's black has cleared
    /// (<see cref="FrameBootScreen.DownSeconds"/>), or releasing the suit would cut the black off half faded.</summary>
    public const float ExitSeconds = ExitClipStart + PilotMotion.Land > FrameBootScreen.DownSeconds ? ExitClipStart + PilotMotion.Land : FrameBootScreen.DownSeconds;
    /// <summary>After leaving, the empty suit starts closing this long after the landing, at the close clip's own pace.</summary>
    public const float CloseAfterLand = 0.2f;

    /// <summary>Boarding: the state ends and the pilot moves when the power-on's last block starts to come on.</summary>
    public static readonly float EnterSeconds = Black + BootPlan.Default.ControlsAt;

    // An ejection squeezes the leaving into its own seconds: open from 5 % to 31 %, close from 60 % to 98 %.
    private const float EjectOpenStart = 0.05f, EjectOpenBy = 0.31f, EjectCloseStart = 0.6f, EjectCloseBy = 0.98f;

    public static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Seconds into a clip <paramref name="length"/> seconds long that starts <paramref name="start"/> seconds into a
    /// sequence and must have ended by <paramref name="by"/>: its own pace, faster only when it would not fit.</summary>
    public static float Play(float elapsed, float start, float by, float length)
    {
        if (length <= 0f) return 0f;
        var pace = Math.Max(1f, length / Math.Max(1e-3f, by - start));
        return Math.Clamp((elapsed - start) * pace, 0f, length);
    }

    /// <summary>When a clip <paramref name="length"/> long played by <see cref="Play"/> from <paramref name="start"/> by <paramref name="by"/> is over.</summary>
    public static float Ends(float start, float by, float length) => start + Math.Min(Math.Max(0f, length), Math.Max(1e-3f, by - start));

    private static PlatePose Plates(float elapsed, float openStart, float openBy, float closeStart, float closeBy, float openLength, float closeLength)
        => elapsed < closeStart
            ? new PlatePose(false, Play(elapsed, openStart, openBy, openLength))
            : new PlatePose(true, Play(elapsed, closeStart, closeBy, closeLength));

    /// <summary>The opening parts <paramref name="elapsed"/> seconds into a boarding, for the model's "open" and "close" clips of the given
    /// lengths: the open clip from the start, open by <see cref="OpenBy"/>, the close clip from <see cref="CloseStart"/>, latched by the end.</summary>
    public static PlatePose EnterPlates(float elapsed, float openLength, float closeLength)
        => Plates(elapsed, 0f, OpenBy, CloseStart, EnterSeconds, openLength, closeLength);

    /// <summary>Boarding: the pilot's movement keys are given back <paramref name="elapsed"/> seconds into the state once the last block
    /// starts to come on (<see cref="EnterSeconds"/>). The pilot's own screen decides this from the state clock alone: the NEURAL LINK message, the
    /// self-test, the ready voice and the host's state message never hold the movement.</summary>
    public static bool ControlsOpen(float elapsed) => elapsed >= EnterSeconds;

    /// <summary>The boarding's plates have shut.</summary>
    public static float EnterShut(float closeLength) => Ends(CloseStart, EnterSeconds, closeLength);

    /// <summary>Leaving: the open clip from <see cref="ExitOpenStart"/>, open by <see cref="ExitClipStart"/>, held open to the landing.</summary>
    public static PlatePose ExitPlates(float elapsed, float openLength) => new(false, Play(elapsed, ExitOpenStart, ExitClipStart, openLength));

    /// <summary>After leaving, <paramref name="sinceLanding"/> seconds after the landing: open until <see cref="CloseAfterLand"/>, then the
    /// close clip at its own pace; null once it has shut.</summary>
    public static PlatePose? ClosePlates(float sinceLanding, float openLength, float closeLength)
    {
        if (sinceLanding < CloseAfterLand) return new PlatePose(false, openLength);
        var t = sinceLanding - CloseAfterLand;
        return t >= closeLength ? null : new PlatePose(true, t);
    }

    /// <summary>An ejection's plates at fraction <paramref name="t"/> of its <paramref name="seconds"/>.</summary>
    public static PlatePose EjectPlates(float t, float seconds, float openLength, float closeLength)
        => Plates(t * seconds, EjectOpenStart * seconds, EjectOpenBy * seconds, EjectCloseStart * seconds, EjectCloseBy * seconds, openLength, closeLength);

    /// <summary>Boarding: the pilot stands as the player until the black, then is inside; leaving: drawn during the leaving motion.</summary>
    public static bool PilotShownEntering(float elapsed) => elapsed < Black;
    public static bool PilotShownExiting(float elapsed) => elapsed >= ExitClipStart;

    /// <summary>Boarding: how black the pilot's own screen is <paramref name="elapsed"/> seconds in (over the last <see cref="FadeSeconds"/>
    /// before <see cref="Black"/>).</summary>
    public static float EnterFade(float elapsed) => Math.Clamp((elapsed - (Black - FadeSeconds)) / FadeSeconds, 0f, 1f);

    /// <summary>A point <paramref name="ahead"/> metres in front of (x, z) for a facing of <paramref name="yawDegrees"/> (negative:
    /// behind). Boarding ends with the pilot's feet <see cref="CockpitBack"/> ahead of the suit's.</summary>
    public static void Ahead(float x, float z, float yawDegrees, float ahead, out float ax, out float az)
    {
        var yaw = yawDegrees * MathF.PI / 180f;
        ax = x + MathF.Sin(yaw) * ahead;
        az = z + MathF.Cos(yaw) * ahead;
    }
}

/// <summary>Where the opening parts are: <see cref="Time"/> seconds into the model's "open" clip, or its "close" clip when
/// <see cref="Closing"/>.</summary>
internal readonly record struct PlatePose(bool Closing, float Time);
