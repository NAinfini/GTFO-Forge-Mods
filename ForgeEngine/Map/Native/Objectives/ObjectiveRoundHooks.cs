using ForgeCore;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;

namespace ForgeMap.Native;

// The facts of round-based objectives. The machines run on every peer; only the host publishes.
internal static class ObjectiveRoundFacts
{
    internal static void Publish(System.Action<LevelEventModule> report)
    {
        if (!Session.IsHost || MapHost.Session is not { NativeLifecycleFactsObserved: true } session) return;
        session.Run(session.LevelEvents, report, "level-event observation", wake: true);
    }
}

// The native body moves the round only after a correct code, so this entry is the round's completion, the last one too.
[HarmonyPatch(typeof(TerminalUplinkPuzzle), nameof(TerminalUplinkPuzzle.TryGoToNextRound))]
internal static class UplinkRoundAdvance
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(TerminalUplinkPuzzle __instance, out int __state) => __state = __instance.m_roundIndex;

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(TerminalUplinkPuzzle __instance, int __state, bool __runOriginal)
    {
        if (!__runOriginal || __instance.m_terminal == null) return;
        string layer = LevelEventObservation.LayerName(__instance.m_terminal.OriginLayer);
        ObjectiveRoundFacts.Publish(module => module.RoundCompleted(layer, __state + 1));
    }
}

[HarmonyPatch(typeof(TimedTerminalSequencePuzzle), nameof(TimedTerminalSequencePuzzle.TryGoToNextRound))]
internal static class TimedRoundAdvance
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(TimedTerminalSequencePuzzle __instance, out int __state) => __state = __instance.m_roundIndex;

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(TimedTerminalSequencePuzzle __instance, int __state, bool __runOriginal)
    {
        if (!__runOriginal || __instance.m_terminal == null) return;
        string layer = LevelEventObservation.LayerName(__instance.m_terminal.OriginLayer);
        ObjectiveRoundFacts.Publish(module => module.RoundCompleted(layer, __state + 1));
    }
}

[HarmonyPatch(typeof(TimedTerminalSequencePuzzle), nameof(TimedTerminalSequencePuzzle.StartTimedVerificationWithReceiver))]
internal static class TimedRoundStarted
{
    // Harmony emits `__runOriginal` for the postfix only when the patch also has a prefix.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(TimedTerminalSequencePuzzle __instance) { }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(TimedTerminalSequencePuzzle __instance, int roundIndex, bool __runOriginal)
    {
        if (!__runOriginal || __instance.m_terminal == null) return;
        MapHost.Session?.ObjectiveRounds?.TimedRoundStarted(__instance, roundIndex);
        string layer = LevelEventObservation.LayerName(__instance.m_terminal.OriginLayer);
        // The receiver's zone as the zone resource every zone reader answers with; unreadable leaves the port out.
        var node = __instance.CurrentRoundTerminal?.SpawnNode?.m_zone;
        ResourceRef? zone = node != null && !node.WasCollected && ZoneIndex.Coordinates(node) is { } at
            ? new ResourceRef(RuntimeZones.ResourceKind, RuntimeZones.Id(at.Dimension, at.Layer, at.Zone))
            : null;
        ObjectiveRoundFacts.Publish(module => module.TimedRoundStarted(layer, roundIndex + 1, zone));
    }
}

[HarmonyPatch(typeof(TimedTerminalSequencePuzzle), nameof(TimedTerminalSequencePuzzle.ResetRound))]
internal static class TimedRoundReset
{
    // The round is read before the reset runs: the reset may rewind the index it reports.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(TimedTerminalSequencePuzzle __instance, out int __state) => __state = __instance.m_roundIndex;

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(TimedTerminalSequencePuzzle __instance, bool wasFailure, int __state, bool __runOriginal)
    {
        if (!__runOriginal || __instance.m_terminal == null) return;
        MapHost.Session?.ObjectiveRounds?.TimedRoundReset(__instance);
        if (!wasFailure) return;
        string layer = LevelEventObservation.LayerName(__instance.m_terminal.OriginLayer);
        int round = __state + 1;
        ObjectiveRoundFacts.Publish(module => module.TimedRoundFailed(layer, round));
    }
}

// ---- declared only by authored rows (MapLazyHooks.Group.ObjectiveRounds) -----------------------------

[HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.TerminalUplinkVerify))]
internal static class UplinkVerifyGate
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(LG_ComputerTerminalCommandInterpreter __instance, string param1, string param2, ref bool __result,
        out ObjectiveRoundsNative.UplinkVerify __state)
    {
        __state = default;
        if (MapHost.Session?.ObjectiveRounds is not { } rounds || rounds.BeforeUplinkVerify(__instance, param1, param2, out __state)) return true;
        __result = true;
        return false;
    }

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_ComputerTerminalCommandInterpreter __instance, ObjectiveRoundsNative.UplinkVerify __state)
    {
        MapHost.Session?.ObjectiveRounds?.AfterUplinkVerify(__instance, __state);
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.TimedTerminalSequenceVerify))]
internal static class TimedVerifyGate
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(LG_ComputerTerminalCommandInterpreter __instance, string param1, string param2, ref bool __result)
    {
        if (MapHost.Session?.ObjectiveRounds is not { } rounds || rounds.BeforeTimedVerify(__instance, param1, param2)) return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(TimedTerminalSequencePuzzle), nameof(TimedTerminalSequencePuzzle.OnStartSequence))]
internal static class TimedSequenceStart
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(TimedTerminalSequencePuzzle __instance) => MapHost.Session?.ObjectiveRounds?.PickReceivers(__instance);
}

[HarmonyPatch(typeof(TimedTerminalSequencePuzzle), nameof(TimedTerminalSequencePuzzle.StartTimedConfirmation))]
internal static class TimedConfirmationStarted
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(TimedTerminalSequencePuzzle __instance) => MapHost.Session?.ObjectiveRounds?.TimedConfirmationStarted(__instance);
}

[HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), nameof(LG_ComputerTerminalCommandInterpreter.ReactorVerify))]
internal static class ReactorVerifyGate
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(LG_ComputerTerminalCommandInterpreter __instance, ref bool __result)
    {
        if (MapHost.Session?.ObjectiveRounds is not { } rounds || rounds.BeforeReactorVerify(__instance)) return true;
        __result = true;
        return false;
    }
}
