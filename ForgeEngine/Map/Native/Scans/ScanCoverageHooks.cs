using ChainedPuzzles;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The one counting entry a room- or zone-covered scan's members are published from: the bioscan core's own
/// check-progress entry, which the player scanner calls on the host once per scan tick with the players it found
/// in its distance gate, the count it treats as its own maximum, and one required-item flag per required item.
///
/// The patch is a prefix that rewrites those three arguments and nothing else. Progress accumulation, the
/// replicated bioscan state, completion, failure, the HUD and the spline all stay the game's own, which is what
/// keeps one scan state and not a second one beside it. A scan this install authored no row for is looked up,
/// found absent and left exactly as the preset made it; a failure inside the callback disables the half instead
/// of throwing through the patch.
///
/// The entry is called only while the scan's own distance gate finds someone, which is why the host enlarges that
/// gate for a covered scan; the enlarged gate is a coarse trigger for this entry and never the member rule. The
/// enlarged gate is the reason this hook is in the in-game checklist: whether the entry is called with an empty
/// gate is the one fact that decides whether the gate alone is enough or the counting beat has to be taken over.
///
/// The class is listed in `MapNativeHooks.Types`, which is the list the plugin installs, and the entry point and
/// its whole signature are frozen in `evidence/scan-coverage-hooks.json`.
/// </summary>
[HarmonyPatch(typeof(CP_Bioscan_Core), nameof(CP_Bioscan_Core.Master_OnPlayerScanChangedCheckProgress))]
internal static class ScanMembershipInjection
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(CP_Bioscan_Core __instance, Il2CppSystem.Collections.Generic.List<PlayerAgent> playersInScan,
        ref int inScanMax, Il2CppStructArray<bool> reqObjsInScan)
        => ScanCoverageSession.Current?.Inject(__instance, playersInScan, ref inScanMax, reqObjsInScan);
}

/// <summary>
/// Release this instance's previous authored inputs before native Setup reuses its components.
/// New coverage is applied by ScanCoverageInstanceCreated after native registration, or by FactoryDone.
/// </summary>
[HarmonyPatch(typeof(ChainedPuzzleInstance), nameof(ChainedPuzzleInstance.Setup))]
internal static class ScanCoverageInstanceSetup
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(ChainedPuzzleInstance __instance) => ScanCoverageSession.Current?.BeforeInstanceSetup(__instance);
}

/// <summary>An authored point goes into the transform and the arguments before native Setup reads them, so the game's
/// own position, course node, sound and guide line follow it (evidence/scan-ring-bodies-20403457.json decision B).</summary>
[HarmonyPatch(typeof(CP_Bioscan_Core), nameof(CP_Bioscan_Core.Setup))]
internal static class ScanCoverageBioscanSetup
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(CP_Bioscan_Core __instance, int puzzleIndex, iChainedPuzzleOwner owner, ref LG_Area sourceArea, ref Vector3 prevPuzzlePos)
        => ScanCoverageSession.Current?.BeforeBioscanSetup(__instance, puzzleIndex, owner, ref sourceArea, ref prevPuzzlePos);
}

/// <summary>The group's scan count and centre, read throughout the group's own native Setup.</summary>
[HarmonyPatch(typeof(CP_Cluster_Core), nameof(CP_Cluster_Core.Setup))]
internal static class ScanCoverageClusterSetup
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(CP_Cluster_Core __instance, int puzzleIndex, iChainedPuzzleOwner owner, ref LG_Area sourceArea, ref Vector3 prevPuzzlePos)
        => ScanCoverageSession.Current?.BeforeClusterSetup(__instance, puzzleIndex, owner, ref sourceArea, ref prevPuzzlePos);
}

/// <summary>Only remaps the game's own speed lookup for shape membership. The coroutine body always runs.</summary>
[HarmonyPatch(typeof(CP_PlayerScanner._MasterScan_d__52), nameof(CP_PlayerScanner._MasterScan_d__52.MoveNext))]
internal static class ScanCoverageNativeBeat
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(CP_PlayerScanner._MasterScan_d__52 __instance, out ScanNativeBeat? __state)
        => __state = ScanCoverageSession.Current?.BeforeNativeBeat(__instance.__4__this);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static System.Exception? Finalizer(ScanNativeBeat? __state, System.Exception? __exception)
    {
        try { __state?.Dispose(); }
        catch (System.Exception error) { return __exception ?? error; }
        return __exception;
    }
}

/// <summary>Observe the graphics component's actual Setup boundary; the native body always runs.</summary>
[HarmonyPatch(typeof(CP_Bioscan_Graphics), nameof(CP_Bioscan_Graphics.Setup))]
internal static class ScanCoverageGraphicsSetup
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(CP_Bioscan_Graphics __instance, out Call __state)
    {
        var session = ScanCoverageSession.Current;
        var epoch = session?.GraphicsSetupEpoch ?? 0;
        __state = new Call(session, session?.BeforeGraphicsSetup(__instance, epoch), epoch);
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(CP_Bioscan_Graphics __instance, Call __state)
        => __state.Session?.AfterGraphicsSetup(__instance, __state.Colors, __state.Epoch);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static System.Exception? Finalizer(CP_Bioscan_Graphics __instance, Call __state, System.Exception? __exception)
    {
        if (__exception != null) __state.Session?.FailedGraphicsSetup(__instance, __state.Colors, __state.Epoch);
        return __exception;
    }
    internal readonly record struct Call(ScanCoverageSession? Session, ScanGraphicsState? Colors, long Epoch);
}

/// <summary>A single-ring HUD writes its localized name into the ring label at Setup and at every text refresh; an
/// authored label is written again after it. The native body always runs.</summary>
[HarmonyPatch(typeof(CP_Bioscan_Hud), nameof(CP_Bioscan_Hud.UpdateText))]
internal static class ScanCoverageHudText
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(CP_Bioscan_Hud __instance) => ScanCoverageSession.Current?.AfterHudText(__instance);
}
