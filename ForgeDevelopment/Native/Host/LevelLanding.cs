using LevelGeneration;

namespace ForgeDevelopment.Native;

/// <summary>Whether the local player has landed in a generated level: the structure is complete, the game is in
/// the level, the floor is built, the local player exists and the elevator's landing anchor is present.</summary>
internal static class LevelLanding
{
    internal static bool HasLanded => RuntimeDiagnostics.StructureComplete && LevelGuideCaptureReadiness.HasLanded(
        GameStateManager.CurrentStateName == eGameStateName.InLevel,
        Builder.Current?.m_currentFloor?.IsBuilt == true,
        ForgeCore.Players.Local != null,
        ElevatorShaftLanding.Current?.m_cargoCageAlign != null);
}

