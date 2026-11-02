namespace ForgeOrdnanceFrames;

/// <summary>The sounds of one press-and-hold prompt, heard by the local player only: a completion sound when the hold finishes
/// or, when the key is let go early, a cancel sound. The key being held has no sound of its own.</summary>
internal static class HoldSound
{
    internal static void Complete() => FrameAudio.Local(FrameSound.HoldComplete);

    internal static void Cancel() => FrameAudio.Local(FrameSound.HoldCancel);
}
