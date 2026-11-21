namespace ForgeDevelopment.Native;

internal static class DevelopmentFrameActivity
{
    internal static bool Needed => RecSession.Active || LevelGuideCaptureMaterials.Recording
        || ExperimentRunner.Running || CombatSampling.Active;
}
