namespace ForgeAudio;

/// <summary>What a request to control a playing sound (stop, gain, pitch) does depending on where it arrives from.</summary>
internal enum ControlRoute { Run, Queue, Drop }

internal static class ControlRouting
{
    /// <summary>On the main thread the request runs now. Off it (a finalizer, a callback thread) it is queued for the next driver
    /// update. After the kit shut down (plugin unload, game exit) its sounds are already gone, so the request is dropped without a warning.</summary>
    internal static ControlRoute Decide(bool ready, bool onMainThread) => !ready ? ControlRoute.Drop : onMainThread ? ControlRoute.Run : ControlRoute.Queue;
}
