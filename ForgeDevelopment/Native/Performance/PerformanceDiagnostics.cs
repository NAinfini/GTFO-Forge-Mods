using BepInEx;
using UnityEngine.SceneManagement;

namespace ForgeDevelopment.Native;

// ForgeDevelopment and the standalone plugin compile the very same collector
// sources. This file only adapts the existing, separately owned host entry point.
internal static partial class PerformanceDiagnostics
{
    private static PerformanceCollector? _collector;
    private static ForgeCore.HookScope _frameScope;
    internal static bool Enabled => _collector?.Running == true;
    internal static void Start(Scene scene)
    {
        var seconds = Settings.PerformanceSummarySeconds.Value;
        if (!float.IsFinite(seconds) || seconds < 0.25f || seconds > 10) seconds = 1;
        // Native timing registration is configured by the host plugin; the frame body is charged to its Forge counter.
        _collector = new PerformanceCollector(Plugin.PluginLog, new(seconds, (int)Settings.PerformanceSnapshotKey.Value),
            new(EnterFrame: () => _frameScope = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentCollector),
                ExitFrame: () => _frameScope.Dispose()));
        _collector.Start(Paths.BepInExRootPath, "ForgeDevelopment", Plugin.PluginVersion);
    }
    internal static void Tick() => _collector?.Tick();
    internal static void Stop() => _collector?.Stop();
}
