using System;
using Expedition;
using GameData;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>B3: replace only the input of vanilla SetupFloor. Vanilla owns SetupAreas,
/// SetPlaced, floor.Setup and job injection. The override never impersonates an AssetShard entry.</summary>
[HarmonyPatch(typeof(LG_SetupFloor), nameof(LG_SetupFloor.Build))]
internal static class RoomRecipeSetupFloor
{
    internal static GameObject? Current;
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(out GameObject? __state)
    {
        __state = Current; Current = null;
        try
        {
            if (RoomGenerationFailure.Pending) return false;
            if (LevelIdentity.Read() is not { } level)
            {
                if (RoomRecipeHost.HasFunctionalRecipes) throw new InvalidOperationException("recipe-functional-level-unreadable");
                return true;
            }
            var recipe = RoomRecipeHost.FunctionalRecipe(level, "start");
            if (recipe != null) Current = RoomRecipeHost.PreparedTemplate(Builder.ComplexResourceSetBlock, recipe);
            return true;
        }
        catch (Exception error) { RoomRecipeHost.FunctionalFailure(error, "setup-floor"); return false; }
    }
    [HarmonyFinalizer]
    internal static Exception? Finalizer(GameObject? __state, Exception? __exception)
    {
        Current = __state;
        if (__exception != null) RoomRecipeHost.FunctionalFailure(__exception, "setup-floor");
        return __exception;
    }
}
[HarmonyPatch(typeof(ComplexResourceSetDataBlock), nameof(ComplexResourceSetDataBlock.GetElevatorTile))]
internal static class RoomRecipeElevatorSelection
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static bool Prefix(ComplexResourceSetDataBlock __instance, uint seed, SubComplex subcomplex, ref GameObject __result)
    {
        // The selector's pool and seed go straight to the native primitive, never through the selector's detour.
        __result = __instance.GetRandomPrefab(seed, __instance.m_elevatorShafts_1x1, subcomplex);
        if (RoomRecipeSetupFloor.Current == null) return false;
        if (__result == null || __result.GetComponent<LG_FloorTransition>() == null || __result.GetComponent<ElevatorShaftLanding>() == null)
        {
            RoomRecipeHost.FunctionalFailure(new InvalidOperationException("recipe-native-start-input-invalid"), "elevator-selection");
            return false;
        }
        __result = RoomRecipeSetupFloor.Current;
        try { RoomRecipeHost.SelectedStart(__result); }
        catch (Exception error) { RoomRecipeHost.FunctionalFailure(error, "elevator-selection"); }
        return false;
    }
}
[HarmonyPatch(typeof(LG_FloorTransition), nameof(LG_FloorTransition.SetPlaced))]
internal static class RoomRecipeTransitionPlaced
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    internal static void Postfix(LG_FloorTransition __instance)
        => RoomRecipeHost.Placed(__instance);
}
