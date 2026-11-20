using ForgeCore;
using UnityEngine;

namespace ForgeFrameGen;

/// <summary>Reflex and PCL markers for frame generation, one frame token per Unity frame (native frame_generation.cpp), without touching
/// Unity's player loop: Reflex sleeps and the simulation starts in ForgeCore's <c>Frame.OnUpdate</c>, the simulation ends at the end of
/// <c>Frame.OnLateUpdate</c>, where a render event marks the start of the frame's render submission on the render thread; the native
/// present bridge marks the end of submission and the present. Unity has read input before Update, so the sleep comes after input is
/// sampled (plan §3.3). The render event is queued with <c>GL.IssuePluginEvent</c>, which only appends it to the render thread's queue:
/// executing a command buffer there instead made the main thread wait (about 1 ms a frame, measured by the performance lane).</summary>
internal static class FrameMarkers
{
    private const int SimulationStart = 0, SimulationEnd = 1;
    private static int _frame; // read once in Update; LateUpdate of the same frame marks the same number

    internal static void Load()
    {
        Frame.OnUpdate(Plugin.Id, Start);
        Frame.OnLateUpdate(Plugin.Id, End);
    }

    private static void Start() => DlssBridge.Marker(SimulationStart, _frame = Time.frameCount);

    private static void End()
    {
        DlssBridge.Marker(SimulationEnd, _frame);
        GL.IssuePluginEvent(DlssBridge.RenderSubmitStartEvent, _frame);
    }
}
