using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ForgePerformance.LoadShedding;

// Unity's job queue wakes every worker thread for each batch of jobs, and the main thread pays for each wake-up. The count of workers
// allowed to run is a native setting of the job queue (Unity 2019.4 UnityPlayer.dll, icalls of Unity.Jobs.LowLevel.Unsafe.JobsUtility, whose
// managed wrappers the game strips): GetJobQueueWorkerThreadCount returns the active limit, SetJobQueueMaximumActiveThreadCount(int) writes it
// (the native code clamps the value to 1 .. get_JobWorkerMaximumCount) and does nothing while the queue shuts down.
// Verified against the loaded UnityPlayer.dll disassembly: int Get(), void Set(int), int Max().
internal static class JobWorkers
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetCall(int count);

    private const string Prefix = "Unity.Jobs.LowLevel.Unsafe.JobsUtility::";
    private static GetCall? _get, _max;
    private static SetCall? _set;
    private static bool _resolved;

    // Resolves the icalls once; a missing one is logged by the caller through the exception and leaves the game's count alone.
    private static void Resolve()
    {
        if (_resolved) return;
        _get = IL2CPP.ResolveICall<GetCall>(Prefix + "GetJobQueueWorkerThreadCount");
        _max = IL2CPP.ResolveICall<GetCall>(Prefix + "get_JobWorkerMaximumCount");
        _set = IL2CPP.ResolveICall<SetCall>(Prefix + "SetJobQueueMaximumActiveThreadCount");
        if (_get == null || _max == null || _set == null) throw new InvalidOperationException("job-worker icalls are not registered in this UnityPlayer.dll");
        _resolved = true;
    }

    internal static int Get() { Resolve(); return _get!(); }
    internal static int Max() { Resolve(); return _max!(); }
    internal static void Set(int count) { Resolve(); _set!(count); }

    // For the probe line: -1 when the icalls are not available (never throws, never logs).
    internal static int Count()
    {
        try { return Get(); } catch { return -1; }
    }
}
