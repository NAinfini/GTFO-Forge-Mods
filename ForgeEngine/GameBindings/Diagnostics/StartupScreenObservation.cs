using System;
using System.Diagnostics;
using System.Globalization;
using ForgeEngine.Framework;

namespace ForgeEngine.GameBindings;

// Native menu observation, once per process on the existing main-thread Update. No input or menu transitions.
internal static class StartupScreenObservation
{
    private static bool observed;
    /// <summary>True once there is nothing left to observe (the line was written, refused, or the log level drops it), which ends
    /// the frame callback that runs this.</summary>
    internal static bool Poll()
    {
        var kernel = GameRuntimeBridge.Kernel;
        if (observed) return true;
        if (kernel == null) return false;
        if (!kernel.RuntimeLogs(RuntimeLogLevel.Info)) return true;
        try
        {
            var menu = GuiManager.MainMenuLayer;
            if (RundownManager.Current == null || !RundownManager.RundownProgressionReady
                || menu?.m_currentPageEnum is not (eCM_MenuPage.CMP_RUNDOWN_NEW or eCM_MenuPage.CMP_RUNDOWN)
                || GameStateManager.CurrentStateName != eGameStateName.NoLobby) return false;
            using var process = Process.GetCurrentProcess();
            var started = process.StartTime.ToUniversalTime();
            observed = true;
            kernel.LogRuntime(RuntimeLogLevel.Info, "startup.rundown-ready", new RuntimeLogFacts
            {
                Values = new[] {
                RuntimeLogValue.Of("observedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                RuntimeLogValue.Of("processStartedUtc", started.ToString("O", CultureInfo.InvariantCulture))
            }
            });
            return true;
        }
        catch (Exception error)
        {
            observed = true;
            kernel.LogRuntime(RuntimeLogLevel.Error, "startup.rundown-observation-refused",
                new RuntimeLogFacts { Detail = error.GetType().Name + ": " + error.Message });
            return true;
        }
    }
}
