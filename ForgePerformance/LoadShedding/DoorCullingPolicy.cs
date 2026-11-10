namespace ForgePerformance.LoadShedding;

// Pure door-state rules of ClosedDoorDepthLimit on the raw values of eDoorStatus, so they run offline.
internal static class DoorCullingPolicy
{
    // eDoorStatus values whose door does not cover the doorway: None (unknown), Open, Destroyed, Opening.
    internal const byte StatusNone = 0, StatusOpen = 10, StatusDestroyed = 11, StatusOpening = 16;

    internal enum Portal { Open, Close, CloseWhenAnimationEnds, Release }

    internal static bool Covers(byte status)
        => status is not (StatusNone or StatusOpen or StatusDestroyed or StatusOpening);

    // previous: the status before this change; None means the door has had no state yet and was never opened, so no
    // closing animation follows. dropIn: the game applies a joined or restored state at once, without the closing
    // animation. A damaged or destroyed door may show holes, so it is released to vanilla for the level.
    internal static Portal OnState(byte previous, byte status, bool damaged, bool dropIn)
    {
        if (damaged || status == StatusDestroyed) return Portal.Release;
        if (!Covers(status)) return Portal.Open;
        return dropIn || previous == StatusNone || Covers(previous) ? Portal.Close : Portal.CloseWhenAnimationEnds;
    }
}
