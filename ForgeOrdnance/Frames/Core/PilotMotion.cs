namespace ForgeOrdnance.Frames;

/// <summary>The pilot's leaving motion ("PilotExit" in forge-frames.bundle's "pilot-motion", one motion for every model), in seconds from
/// the start of the clip: the hands come out, the pilot hops back and lands behind the suit at <see cref="Land"/>; the head is out of the
/// shell at <see cref="HeadOut"/>. The screen's black does not clear there: it waits until the pilot has landed and the camera has eased
/// to the player's own eye (<see cref="FrameBootScreen.DownClearStart"/>), so the inside of the suit is never shown. The bundle's clip is
/// checked against these numbers when it loads. Boarding has no motion (<see cref="FrameSequence"/>).</summary>
internal static class PilotMotion
{
    /// <summary>Seconds into the leaving clip by which the head has pulled out of the shell on all four models (everything in view at
    /// least 0.2 m away); the black no longer clears here, but the motion still has to carry the head out by this point.</summary>
    public const float HeadOut = 0.8f;
    public const float ExitSeconds = 1.6f, Land = 26f / 30f;
}
