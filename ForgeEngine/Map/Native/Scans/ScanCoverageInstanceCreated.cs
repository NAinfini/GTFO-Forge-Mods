using ChainedPuzzles;
using GameData;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

// Setup finishes before the manager parents and registers the instance. Resolve authored coverage
// after that original creation body, using the same instance table as the generation pass and actions.
[HarmonyPatch(typeof(ChainedPuzzleManager), nameof(ChainedPuzzleManager.CreatePuzzleInstance), new[]
{
    typeof(ChainedPuzzleDataBlock), typeof(LG_Area), typeof(LG_Area), typeof(Vector3), typeof(Transform), typeof(bool)
})]
internal static class ScanCoverageInstanceCreated
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(ChainedPuzzleInstance __result) => ScanCoverageSession.Current?.ApplyInstance(__result);
}
