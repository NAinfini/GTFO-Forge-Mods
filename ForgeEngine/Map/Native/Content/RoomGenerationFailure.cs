using HarmonyLib;
using LevelGeneration;
using System;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>Leave generation at the next factory boundary, outside a native job's stack.
/// GS_Generating.OnMasterCommand(EndPlaying) takes this same AfterLevel transition.</summary>
internal static class RoomGenerationFailure
{
    internal static bool Pending { get; private set; }
    private static bool dispatched, factoryDone, exitFailureReported;
    internal static bool Request()
    {
        if (Pending) return false;
        Pending = true;
        return true;
    }
    internal static void Reset() { Pending = false; dispatched = false; factoryDone = false; exitFailureReported = false; }
    /// <summary>LG_Factory.FactoryDone has returned, so LG_Factory.Update never runs again and a refusal raised inside
    /// it (or after it) can no longer leave through <see cref="ContinueFactory"/>.</summary>
    internal static void FactoryFinished() => factoryDone = true;
    /// <summary>The exit for a refusal made once the factory has finished. Called from the plugin's own frame driver, outside
    /// every native frame; it takes the same AfterLevel transition as <see cref="ContinueFactory"/>, once.</summary>
    internal static bool LeaveAfterFactory()
    {
        if (!Pending || !factoryDone || dispatched) return false;
        return Leave();
    }
    internal static void RecordException(Exception error)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Error)) return;
        // The existing writer limits each values text to 256 characters and truncates message.
        // Preserve the entire ToString (type, inner exceptions and stacks) in ordered values
        // chunks instead of relying on either message or Quote's 64-character preview.
        var exception = MapLog.SafeDetail(error.ToString());
        var parts = (exception.Length + 255) / 256;
        for (var part = 0; part < parts; part++)
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.RoomConnectionRefused, "Room generation exception; join values.exception in part order.",
                RuntimeLogValue.Of("phase", "generation-exception"), RuntimeLogValue.Of("reason", "room.generation-failed"),
                RuntimeLogValue.Of("part", part + 1), RuntimeLogValue.Of("parts", parts),
                RuntimeLogValue.Of("exception", exception.Substring(part * 256, Math.Min(256, exception.Length - part * 256))));
    }
    internal static bool ContinueFactory()
    {
        if (!Pending) return true;
        if (!dispatched) Leave();
        return false;
    }
    private static bool Leave()
    {
        try
        {
            GameStateManager.ChangeState(eGameStateName.AfterLevel);
            dispatched = true;
            // The player lands back in the lobby; tell them why in their own chat box. The cause is in the game log.
            ForgeEngine.GameBindings.SuspendNotice.Request(ForgeEngine.GameBindings.SuspendNotice.GenerationFailed);
        }
        catch (Exception error)
        {
            // A native refusal before the transition owes another attempt at the existing frame
            // boundary. A callback can also throw after the state changed; read that receipt before retrying.
            try { dispatched = GameStateManager.CurrentStateName == eGameStateName.AfterLevel; }
            catch (Exception receiptError) { MapLog.Contained("room-generation-failure.transition-receipt", receiptError); }
            if (!exitFailureReported)
            {
                exitFailureReported = true;
                try { RecordException(error); }
                catch (Exception recordError) { MapLog.Contained("room-generation-failure.record", recordError); }
            }
        }
        return dispatched;
    }
}

/// <summary>A refusal already pending stops the next zone job before the native expansion runs, and the job that raised it reports failure instead of throwing into the factory.</summary>
[HarmonyPatch(typeof(LG_ZoneJob_CreateExpandFromData), nameof(LG_ZoneJob_CreateExpandFromData.Build))]
internal static class RoomGenerationFailureBuild
{
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    internal static bool Prefix(ref bool __result)
    {
        if (!RoomGenerationFailure.Pending) return true;
        __result = false;
        return false;
    }
    [HarmonyFinalizer]
    internal static Exception? Finalizer(Exception? __exception, ref bool __result)
    {
        if (!RoomGenerationFailure.Pending) return __exception;
        __result = false;
        return null;
    }
}

[HarmonyPatch(typeof(LG_Factory), nameof(LG_Factory.Update))]
internal static class RoomGenerationFailureBoundary
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)] internal static bool Prefix() => RoomGenerationFailure.ContinueFactory();
}
