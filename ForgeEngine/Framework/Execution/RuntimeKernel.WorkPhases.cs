using System;

namespace ForgeEngine.Framework;

public sealed partial class RuntimeKernel
{
    // Host-owned upstream observations use exactly the existing lifecycle mutation guard.
    internal void RunNativeObservation(Action observe)
    {
        ReadThread();
        var previous = notifyingLifecycle;
        notifyingLifecycle = true;
        try { observe(); }
        finally { notifyingLifecycle = previous; }
    }
    internal void ReportNativeObserverFailure(string consumer, Exception error)
    {
        ReadThread();
        if (LifecycleFaultCount < long.MaxValue) LifecycleFaultCount++;
        var detail = CommandResult.TruncateDetail(consumer + ": " + error.GetType().Name + ": " + error.Message);
        LastLifecycleFault = new(Identity.Id, RuntimeLogReasonCodes.LifecycleObserverFailed, detail);
        if (!log.SessionEnded) log.LogObserverFailed(Identity.Id, detail);
    }
}
