using System;
using GTFO.API;
using HarmonyLib;
using SNetwork;

namespace ForgeEngine.GameBindings;

/// <summary>
/// A client that lands late keeps the host's StartPlaying. The host sends <c>StartPlaying</c> once, without waiting for every client to
/// stand in ReadyToStartLevel. A client whose first build ran long can still be in StopElevatorRide then, and vanilla
/// <c>GS_StopElevatorRide.OnMasterCommand</c> handles only EndPlaying: it logs "has not implemented functionality for StartPlaying" and
/// drops the command, so the client never reaches InLevel (held under the elevator, clips not filled, extraction refused). StopElevatorRide
/// always ends in ReadyToStartLevel (<c>OnElevatorHasArrived</c>), whose <c>OnMasterCommand</c> runs the whole start for StartPlaying
/// (ammo reset and fill, bots' ammo, the objective's <c>AttemptInteract</c>, ChangeState(InLevel)). A client therefore keeps that one
/// dropped command and hands it to the current state once, on the first frame after the game entered ReadyToStartLevel, not inside the
/// state-change event (a nested ChangeState there would show other listeners InLevel before ReadyToStartLevel). The prefix only records:
/// vanilla's body still runs, every other command and state is untouched, and the host keeps nothing. The hold ends at level cleanup, a
/// host change or any state other than these two.
/// </summary>
internal static class StartPlayingCatchUp
{
    private static bool _held, _due;
    private static pMasterCommand _command;

    internal static void Install(Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        var target = AccessTools.Method(typeof(GS_StopElevatorRide), nameof(GS_StopElevatorRide.OnMasterCommand))
            ?? throw new MissingMethodException(nameof(GS_StopElevatorRide), nameof(GS_StopElevatorRide.OnMasterCommand));
        harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(StartPlayingCatchUp), nameof(Received))));
        EventAPI.OnGameStateChanged += StateChanged;
        LevelAPI.OnLevelCleanup += LevelCleanedUp;
        ForgeCore.GameEvents.MasterChanged += MasterChanged;
    }

    /// <summary>Ends what <see cref="Install"/> subscribed; the patch comes off with the host's Harmony owner.</summary>
    internal static void Uninstall()
    {
        EventAPI.OnGameStateChanged -= StateChanged;
        LevelAPI.OnLevelCleanup -= LevelCleanedUp;
        ForgeCore.GameEvents.MasterChanged -= MasterChanged;
        _held = _due = false;
    }

    private static void Received(pMasterCommand command)
    {
        try
        {
            if (command.type != eMasterCommandType.StartPlaying || SNet.IsMaster) return;
            _command = command;
            _held = true;
            Info("start-playing-deferred", "StartPlaying arrived in StopElevatorRide; it runs once the game enters ReadyToStartLevel.");
        }
        catch (Exception error) { ForgeCore.Log.For(Plugin.PluginGuid).Error("start-playing-replay-failed", "Keeping StartPlaying failed: " + error); }
    }

    private static void StateChanged(eGameStateName previous, eGameStateName next)
    {
        if (!_held) return;
        if (next == eGameStateName.ReadyToStartLevel) _due = true;
        else if (next != eGameStateName.StopElevatorRide) Discard("state " + next);
    }

    /// <summary>On the host's Update: the first frame after the game entered ReadyToStartLevel with a kept command.</summary>
    internal static void Update()
    {
        if (!_due) return;
        _held = _due = false;
        Replay();
    }

    /// <summary>Hands the kept command to the current state: once per kept StartPlaying, on the frame <see cref="Update"/> finds it due.</summary>
    private static void Replay()
    {
        try
        {
            GameStateManager.CurrentState.OnMasterCommand(_command);
            Info("start-playing-replayed", "StartPlaying handed to ReadyToStartLevel; state is now " + GameStateManager.CurrentStateName + ".");
        }
        catch (Exception error) { ForgeCore.Log.For(Plugin.PluginGuid).Error("start-playing-replay-failed", "Handing StartPlaying to ReadyToStartLevel failed: " + error); }
    }

    private static void LevelCleanedUp() => Discard("level cleanup");
    private static void MasterChanged() => Discard("host changed");

    // Developer lines (info): the default play log level writes none of them.
    private static void Info(string code, string detail) { if (ForgeCore.Log.Developer) Plugin.PluginLog.LogInfo(code + ": " + detail); }

    private static void Discard(string reason)
    {
        if (!_held) return;
        _held = _due = false;
        Info("start-playing-discarded", "The kept StartPlaying was dropped (" + reason + ").");
    }
}
