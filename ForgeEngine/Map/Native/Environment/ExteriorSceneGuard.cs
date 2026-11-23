using ExteriorRendering;
using ForgeEngine.Framework;
using HarmonyLib;

namespace ForgeMap.Native;

// The native setter (build 34873, RVA 0x164B4F0) writes the switch and exterior light, then unconditionally
// calls s_current.SetDisabled(). Indoor scenes need not have that camera. A repeated close whose complete
// native state is already satisfied must not abort PlayerSync.OnSpawn. Requests that change either state,
// requests to enable rendering, and requests with a camera still execute the original body.
[HarmonyPatch(typeof(ExteriorCamera), "set_GlobalSwitch", new[] { typeof(bool) })]
internal static class ExteriorSceneGuard
{
    [HarmonyPrefix, HarmonyPriority(Priority.Normal)]
    internal static bool Prefix(bool value)
    {
        if (value || ExteriorCamera.s_current != null || ExteriorCamera.s_globalSwitch || ExteriorLight2.Enabled)
            return true;

        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ExteriorAlreadyDisabled,
            "Exterior close already satisfied without a scene camera; native switch=false light=false. Player spawn continues through its original body.");
        return false;
    }
}
