using ForgeEngine.Framework;
using ForgeEngine.Trigger.Targeting;
using UnityEngine;

namespace ForgeEngine.Trigger.Native;

/// <summary>
/// The line-of-sight row's cast: the game's own <c>Physics.Linecast(Vector3, Vector3, int)</c> against one of the
/// game's own obstruction masks — <c>LayerManager.MASK_WORLD</c> for the level's geometry, or
/// <c>LayerManager.MASK_SENTRYGUN_DETECTION_BLOCKERS</c>, the set the sentry's own target check looks through. The
/// masks are read at each cast rather than cached, because the manager fills them when it loads; a mask that is
/// still empty would cast against nothing and read as "always visible", so it is refused by name instead.
/// </summary>
internal static class LevelSight
{
    internal const string MaskUnavailableCode = "sight-mask-unavailable";

    internal static bool Visible(double fromX, double fromY, double fromZ, double toX, double toY, double toZ,
        SightBlockers blockers)
    {
        var mask = blockers == SightBlockers.SentryDetection
            ? LayerManager.MASK_SENTRYGUN_DETECTION_BLOCKERS
            : LayerManager.MASK_WORLD;
        if (mask == 0) throw new RuntimeContractException(MaskUnavailableCode, "The game's obstruction mask is not loaded.");
        return !Physics.Linecast(new Vector3((float)fromX, (float)fromY, (float)fromZ),
            new Vector3((float)toX, (float)toY, (float)toZ), mask);
    }
}
