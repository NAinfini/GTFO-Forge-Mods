using BepInEx.Configuration;

namespace ForgeDevelopment.Native;

internal static class DropMeasurement
{
    // Frozen at startup. This isolates all Development work, rather than leaving
    // an installed observer that returns early after paying the native detour cost.
    internal static bool CaptureWorkEnabled { get; private set; } = true;
    internal static void Bind(ConfigFile config)
        => CaptureWorkEnabled = config.Bind("DropMeasurement", "CaptureWorkEnabled", true,
            "Measurement only: false starts no Development observers, recorder, exports, auto-tests or collectors. Runtime drop timing remains active. Restart required.").Value;
}
