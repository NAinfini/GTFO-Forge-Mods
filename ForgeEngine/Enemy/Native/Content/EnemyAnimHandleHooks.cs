using Enemies;
using HarmonyLib;

namespace ForgeEnemy.Native;

// Build 20403457 dump.cs:663169. The game's InitController copies the selected vanilla handle first;
// the postfix applies only that controller's authored fields to this instance's copy.
[HarmonyPatch(typeof(EnemyLocomotion), nameof(EnemyLocomotion.InitController))]
internal static class EnemyAnimHandleInitializedHook
{
    [HarmonyPostfix]
    internal static void Postfix(EnemyLocomotion __instance)
    {
        var session = EnemyHost.Offer(EnemyFeature.Animation);
        if (session == null) return;
        session.Guard(ref __instance, static (EnemyModule module, ref EnemyLocomotion locomotion) => module.ControllerInitialized(locomotion));
    }
}
