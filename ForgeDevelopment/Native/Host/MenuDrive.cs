using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using CellMenu;
using SNetwork;
using UnityEngine;
using Global = Globals.Global;

namespace ForgeDevelopment.Native;

/// <summary>
/// The native menu, lobby and loadout actions of the game drive steps (<see cref="AuthoringGameDrive"/>). Each method issues the same native call the enabled UI control makes and reports a game call to the
/// caller's log; it never forces a ready flag, skips the elevator or changes a game state. The gates between the
/// actions stay with the caller, which also owns what the lobby may contain.
/// </summary>
internal sealed class MenuDrive
{
    private readonly Action<string> _call;
    internal MenuDrive(Action<string> call) => _call = call;
    internal bool InjectPressed { get; private set; }
    internal bool StartPressed { get; private set; }
    private float _nextPageRequest;

    /// <summary>Presses the intro and start pages when the menu allows it, and says whether the rundown menu can now
    /// create a lobby. These are the menu's live ready signals; Global.IsSetup, GameStateManager.IsReady and
    /// GotAllProgression remain false in the initialized vanilla NoLobby menu on this build.</summary>
    internal bool StartupReady(bool press = true)
    {
        var menu = GuiManager.MainMenuLayer;
        var intro = menu?.PageIntro;
        if (press && !InjectPressed && intro != null && LevelGuideCaptureReadiness.CanPressInject(
            menu!.m_currentPageEnum == eCM_MenuPage.CMP_INTRO, intro.m_step == CM_IntroStep.WaitForInject,
            intro.m_startupLoaded, intro.m_sharedLoaded, intro.m_enemiesLoaded, RundownManager.RundownProgressionReady))
        {
            InjectPressed = true;
            _call("CM_PageIntro.EXT_PressInject(0)"); intro.EXT_PressInject(0);
        }
        if (press && menu != null && menu.m_currentPageEnum == eCM_MenuPage.CMP_START
            && menu.PageStart.m_ready && !StartPressed)
        {
            StartPressed = true;
            _call("CM_PageStart.EXT_PressStart"); menu.PageStart.EXT_PressStart();
        }
        // Past the intro the menu can stand on another page (the rundown selector that lists a pack's own rundown beside the stock ones,
        // or the settings page): nothing on that page opens the rundown menu, so the lobby never becomes creatable.
        if (press && menu != null && RundownManager.Current != null && RundownManager.RundownProgressionReady
            && GameStateManager.CurrentStateName == eGameStateName.NoLobby && Time.realtimeSinceStartup >= _nextPageRequest
            && menu.m_currentPageEnum is not (eCM_MenuPage.CMP_NONE or eCM_MenuPage.CMP_INTRO or eCM_MenuPage.CMP_START
                or eCM_MenuPage.CMP_RUNDOWN_NEW or eCM_MenuPage.CMP_RUNDOWN))
        {
            _nextPageRequest = Time.realtimeSinceStartup + 2f;
            _call("MainMenuLayer.ChangePage(CMP_RUNDOWN_NEW) from " + menu.m_currentPageEnum); menu.ChangePage(eCM_MenuPage.CMP_RUNDOWN_NEW);
        }
        return LevelGuideCaptureReadiness.CanCreateLobby(RundownManager.Current != null,
            RundownManager.RundownProgressionReady,
            menu?.m_currentPageEnum is eCM_MenuPage.CMP_RUNDOWN_NEW or eCM_MenuPage.CMP_RUNDOWN,
            GameStateManager.CurrentStateName == eGameStateName.NoLobby);
    }

    /// <summary>The CreateHub/createLobby call of CM_Utils's host-button coroutine, with Private instead of its Public
    /// lobby. A public lobby is never opened by a driver.</summary>
    internal void CreatePrivateLobby(uint rundownId)
    {
        Global.RundownIdToLoad = rundownId;
        _call("SNet.SessionHub.CreateHub(Private, createLobby:true)");
        SNet.SessionHub.CreateHub(LobbyType.Private, true);
    }

    /// <summary>Selects the expedition and opens the loadout page. The page applies its saved slot permissions on
    /// activation, so a caller that restricts the lobby must apply its policy again on the next frame. The lobby's rundown is set again
    /// first, as the rundown page's own rundown button does before it selects: the selection's callbacks (CM_PageRundown_New.OnExpeditionUpdated)
    /// read Global.RundownIdToLoad, and the menu can have put it back to 0 since the lobby was created (the rundown page's own code writes
    /// it: its rundown buttons, its tutorial button and the popup close that undoes that), which left the selection on rundown 0 for good.</summary>
    internal void SelectExpedition(string label, uint rundownId, int tier, int ordinal)
    {
        Global.RundownIdToLoad = rundownId;
        _call("RundownManager.MasterSelectActiveExpedition(Local, " + label + ")");
        RundownManager.MasterSelectActiveExpedition(eRundownKey.Local, (eRundownTier)tier,
            ordinal, RundownManager.GetLocalRundownKey(rundownId), rundownId);
        GuiManager.MainMenuLayer.ChangePage(eCM_MenuPage.CMP_LOADOUT);
    }

    internal static bool ActiveMatches(uint rundownId, int tier, int ordinal)
    {
        var active = RundownManager.GetActiveExpeditionData();
        return Global.RundownIdToLoad == rundownId && active.rundownType == eRundownKey.Local
            && RundownManager.ActiveRundownKey == RundownManager.GetLocalRundownKey(rundownId)
            && (int)active.tier == tier
            && active.expeditionIndex == ordinal && RundownManager.ActiveExpedition != null;
    }

    /// <summary>Whether the current lobby was created for this rundown. A lobby keeps Global.RundownIdToLoad from its creation, so a
    /// level of another rundown needs a new lobby.</summary>
    internal static bool LobbyRundownIs(uint rundownId) => Global.RundownIdToLoad == rundownId;

    /// <summary>Advances the loadout page by one step and returns true on the frame the drop action was invoked.
    /// It uses the native ready action of the enabled UI button; EXT_StartExpedition itself does not enforce these
    /// UI gates. <paramref name="beforeDrop"/> runs immediately before the drop call.</summary>
    internal bool TryDrop(uint rundownId, int tier, int ordinal, Action beforeDrop)
    {
        var loadout = CM_PageLoadout.Current;
        if (!ActiveMatches(rundownId, tier, ordinal) || loadout == null
            || GameStateManager.CurrentStateName != eGameStateName.Lobby
            || GuiManager.MainMenuLayer.m_currentPageEnum != eCM_MenuPage.CMP_LOADOUT) return false;
        if (!GameStateManager.IsReady)
        {
            if (loadout.m_readyButton?.m_collider?.enabled == true)
            { _call("CM_PageLoadout.OnReadyButtonClicked"); loadout.OnReadyButtonClicked(); }
            return false;
        }
        if (!LevelGuideCaptureReadiness.CanDrop(true, GameStateManager.IsReady,
            GameStateManager.IsEveryoneReady(false), loadout.m_dropButton?.m_collider?.enabled == true)) return false;
        beforeDrop();
        _call("CM_PageLoadout.EXT_StartExpedition"); loadout.EXT_StartExpedition();
        return true;
    }

    /// <summary>The lobby gates of the drive session, the same ones the test runner's Select stage checks before it
    /// presses ready: the selection took effect, no human is present, the declared bot slots (a JSON array of the
    /// level's bot-slots.forge rows; "[]" is a solo lobby) hold joined bots, and the native start gate is open.
    /// Returns "ready" or the first unmet gate with its readback.</summary>
    internal static string LobbyPending(uint rundownId, int tier, int ordinal, string slotsJson)
    {
        if (!LobbyGate.SelectionApplied(rundownId, tier, ordinal)) return "selection-not-applied " + LobbyGate.SelectionText();
        if (LobbyGate.HasOtherHuman()) return "a human occupies another player slot or is joining the lobby";
        var pending = LobbyGate.LobbyRosterPending(LobbyGate.GroupBotSlots(JsonNode.Parse(slotsJson) as JsonArray
            ?? throw new ArgumentException("slotsJson must be a JSON array")));
        if (pending != null) return pending;
        return LobbyGate.NativeStartReady() ? "ready" : "native start gate closed (not master/network, no active expedition, state="
            + GameStateManager.CurrentStateName + ", or a bot without a PlayerAgent)";
    }

    /// <summary>What the game's drop action needs once everyone pressed ready: every player in the session hub (a bot the slot closing
    /// just kicked included, while it is still leaving) reports the replication mode <c>InSessionHub_ReadyToGenerate</c>. A press before
    /// that only logs the "some players arent ready" line and does not leave the lobby. Returns "ready" or the first player not there.</summary>
    internal static string StartPending()
    {
        var lobby = SNet.LobbyPlayers;
        if (lobby == null) return "lobby player list unavailable";
        for (var i = 0; i < lobby.Count; i++)
        {
            var player = lobby[i];
            if (player != null && player.Session.mode != eReplicationMode.InSessionHub_ReadyToGenerate)
                return "lobby player " + i + " mode=" + player.Session.mode + " " + LobbyGate.SlotFlags(player);
        }
        return "ready";
    }

    /// <summary>One compact readback of the menu and lobby facts the drive session waits on, for timeout reports.</summary>
    internal static string LobbyState()
    {
        var parts = new List<string>
        {
            "state=" + GameStateManager.CurrentStateName, "master=" + SNet.IsMaster, "inLobby=" + SNet.IsInLobby,
            LobbyGate.SelectionText(), "activeExpedition=" + (RundownManager.ActiveExpedition != null),
            "page=" + GuiManager.MainMenuLayer?.m_currentPageEnum, "ready=" + GameStateManager.IsReady,
            "everyoneReady=" + LobbyGate.NativeEveryoneReady()
        };
        var slots = SNet.Slots;
        for (var slot = 0; slot < slots.PlayerSlots.Length; slot++)
            parts.Add("slot" + slot + "=" + slots.GetSlotPermission(slot) + "/" + LobbyGate.SlotFlags(slots.GetPlayerInSlot(slot)));
        return string.Join("; ", parts);
    }
    /// <summary>Native factory enemy registration continues after InLevel; this is true once the player landed and that
    /// registration is complete.</summary>
    internal static bool LandingReady() => LevelGuideCaptureReadiness.CanSnapshotLanding(LevelLanding.HasLanded,
        Mastermind.Current?.m_factoryGroupDatas?.Count ?? 0, Mastermind.Current?.m_factoryGroupDatas_SpawnDone == true);
}
