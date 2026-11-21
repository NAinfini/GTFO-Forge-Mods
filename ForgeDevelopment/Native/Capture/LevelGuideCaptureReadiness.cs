namespace ForgeDevelopment.Native;

internal static class LevelGuideCaptureReadiness
{
    internal static bool CanPressInject(bool isIntroPage, bool waitingForInject, bool startupLoaded,
        bool sharedLoaded, bool enemiesLoaded, bool progressionReady)
        => isIntroPage && waitingForInject && startupLoaded && sharedLoaded && enemiesLoaded && progressionReady;

    internal static bool CanCreateLobby(bool hasRundown, bool progressionReady, bool isRundownPage, bool isNoLobby)
        => hasRundown && progressionReady && isRundownPage && isNoLobby;

    internal static bool CanDrop(bool isLoadoutPage, bool locallyReady, bool everyoneReady, bool dropButtonEnabled)
        => isLoadoutPage && locallyReady && everyoneReady && dropButtonEnabled;

    internal static bool HasLanded(bool isInLevel, bool floorBuilt, bool playerPresent, bool landingAnchorPresent)
        => isInLevel && floorBuilt && playerPresent && landingAnchorPresent;

    internal static bool CanSnapshotLanding(bool hasLanded, int factoryGroupCount, bool factorySpawnDone)
        => hasLanded && (factoryGroupCount == 0 || factorySpawnDone);
}
