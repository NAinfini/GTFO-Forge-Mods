namespace ForgeOrdnanceFrames;

/// <summary>The pilot's leaving motion ("PilotExit" in forge-frames.bundle's "pilot-motion", one motion for every model), in seconds from
/// the start of the clip: the hands come out, the pilot hops back and lands behind the suit at <see cref="Land"/>; the pilot's own black
/// clears once the head is out of the shell (<see cref="HeadOut"/>). The bundle's clip is checked against these numbers when it loads.
/// Boarding has no motion (<see cref="FrameSequence"/>).</summary>
public static class PilotMotion
{
    /// <summary>Seconds into the leaving clip by which the head has pulled out of the shell on all four models (everything in view at
    /// least 0.2 m away); the pilot's own black clears from here (<see cref="FrameBootScreen.DownClearStart"/>).</summary>
    public const float HeadOut = 0.8f;
    public const float ExitSeconds = 1.6f, Land = 26f / 30f;
}
