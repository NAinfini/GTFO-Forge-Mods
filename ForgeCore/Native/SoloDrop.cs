using System;
using CellMenu;
using GameData;
using GTFO.API;
using LevelGeneration;
using Player;
using SNetwork;
using Global = Globals.Global;

namespace ForgeCore;

public enum SoloDropState { Idle, Menu, Lobby, Select, Ready, Requested, InLevel, Failed, Leaving, Left }

/// <summary>Why this game cannot start an expedition (<see cref="SoloDrop.Check"/>): nothing, no block
/// holds that rundown, the rundown has no such enabled level, or the expedition is single-player and bots were asked for.</summary>
public enum SoloDropRefusal { None, RundownMissing, LevelMissing, NoBots }

/// <summary>Starts one expedition as a private host with no other human, with chosen seeds, through the game's own menu calls: pass the
/// intro and start pages, open a private lobby, close the other slots (or open some of them to the game's own bots), select the expedition,
/// set the seeds through the game's own setter, ready up and request the drop once the bots have joined. No input is simulated. It works
/// from the main menu with no lobby open (<see cref="Start"/>) or from a lobby this machine hosts alone (<see cref="Rejoin"/>); the first thing that is not as expected (another human in the lobby, the selection changing,
/// the drop returning to the lobby) ends it in <see cref="SoloDropState.Failed"/> with the reason in <see cref="Failure"/> and in the log,
/// and it never retries. Main thread; the plugin's frame update drives it.</summary>
public static class SoloDrop
{
    private static LevelId _level;
    private static LevelSeeds? _seeds;
    private static int _bots;
    private static bool _injectPressed, _startPressed, _readyPressed, _generationEntered, _keepSlots;
    // How long the lobby may keep the drop waiting for its bots, and how long the game may stay in the lobby after it was asked.
    private const float BotsLimit = 30f, RequestLimit = 5f;
    /// <summary>How long <see cref="Leave"/> waits for the game to be back in the lobby: its abort and after-level states and the level
    /// cleanup (which unloads the level's assets) take seconds; a minute without arriving means the game is stuck.</summary>
    public const float LeaveLimit = 60f;
    private static float _botsWaitFrom, _requestedAt, _leaveFrom;

    public static SoloDropState State { get; private set; }

    /// <summary>Why <see cref="SoloDropState.Failed"/> in one line with no stack (the log has that); empty otherwise.</summary>
    public static string Failure { get; private set; } = "";

    /// <summary>Begins the drop; with <paramref name="seeds"/> null the game draws its own seeds as in an ordinary start (read them afterwards with
    /// <see cref="Level.TryCurrentLevel"/>). <paramref name="bots"/> (0 to 3) of the other slots are opened to bots (<c>SlotPermission.Bot</c>, the
    /// lobby's own bot setting; the game spawns and equips them) instead of being closed, and the drop waits until they have joined with their
    /// player agents; an expedition the game plays single-player gets no bots and fails. Throws when one is already running or the level or the
    /// bot count is not valid; everything the game refuses later ends in <see cref="SoloDropState.Failed"/>.</summary>
    public static void Start(LevelId level, LevelSeeds? seeds = null, int bots = 0)
    {
        if (State is not (SoloDropState.Idle or SoloDropState.InLevel or SoloDropState.Failed or SoloDropState.Left))
            throw new InvalidOperationException("A solo drop is already running (" + State + ").");
        if (!level.IsValid) throw new ArgumentException("The level is not a valid expedition.", nameof(level));
        if (bots is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(bots), bots, "Bots fill 0 to 3 of the other slots.");
        Begin(level, seeds, bots, keepSlots: false, SoloDropState.Menu);
    }

    /// <summary>Drops into <paramref name="level"/> again (the author's rejoin after a content reload), with the game's own seeds. From the
    /// lobby this machine hosts with no other human in it, the level is selected again, the local player readies up and the drop is
    /// requested; the lobby's slots and bots stay as they are and the drop waits for bots already there to have their agents. Throws
    /// when a drop is already running, the level is not valid or the game is not in a lobby this machine hosts; everything
    /// the game refuses later ends in <see cref="SoloDropState.Failed"/>.</summary>
    public static void Rejoin(LevelId level)
    {
        if (State is not (SoloDropState.Idle or SoloDropState.InLevel or SoloDropState.Failed or SoloDropState.Left))
            throw new InvalidOperationException("A solo drop is already running (" + State + ").");
        if (!level.IsValid) throw new ArgumentException("The level is not a valid expedition.", nameof(level));
        if (GameStateManager.CurrentStateName != eGameStateName.Lobby || !SNet.IsInLobby || !SNet.IsMaster)
            throw new InvalidOperationException("Rejoin starts from a lobby this machine hosts.");
        if (Players.OtherHumanInLobby()) throw new InvalidOperationException("Another human is in the lobby.");
        Begin(level, null, 0, keepSlots: true, SoloDropState.Lobby);
    }

    /// <summary>Ends the expedition this machine hosts with no other human in it and goes back to its lobby, through the game's own
    /// menu-bar button for it (the confirm of "return to lobby" calls <c>SNet.Sync.SessionCommand(TryEndPlaying, 0)</c>; the game then
    /// passes its own abort and after-level states and cleans the level up). On a success, failure or abort screen or after the level the
    /// game is already on its way back and only the arrival is awaited. <see cref="SoloDropState.Left"/> once the game is in the lobby with
    /// no floor built (or in the menu with no lobby, if the session ended on the way); <see cref="SoloDropState.Failed"/> when the game
    /// refuses the command or has not arrived within <see cref="LeaveLimit"/> seconds, never retried. Throws when a drop is running, the
    /// game is already in the lobby or menu, this machine is not the host or another human is in the lobby.</summary>
    public static void Leave()
    {
        if (State is not (SoloDropState.Idle or SoloDropState.InLevel or SoloDropState.Failed or SoloDropState.Left))
            throw new InvalidOperationException("A solo drop is already running (" + State + ").");
        var state = GameStateManager.CurrentStateName;
        if (state is eGameStateName.Lobby or eGameStateName.NoLobby) throw new InvalidOperationException("The game is not in an expedition.");
        if (!SNet.IsInLobby || !SNet.IsMaster) throw new InvalidOperationException("This machine does not host the expedition.");
        if (Players.OtherHumanInLobby()) throw new InvalidOperationException("Another human is in the lobby.");
        Unhook();
        _level = default;
        Failure = "";
        _leaveFrom = UnityEngine.Time.realtimeSinceStartup;
        State = SoloDropState.Leaving;
        // These states send the game back to the lobby by themselves.
        if (state is eGameStateName.ExpeditionSuccess or eGameStateName.ExpeditionFail or eGameStateName.ExpeditionAbort or eGameStateName.AfterLevel) return;
        if (SNet.Sync == null || !SNet.Sync.SessionCommand(eSessionCommandType.TryEndPlaying, 0))
            Fail("The game refused to end the expedition in state " + state + ".");
    }

    private static void FollowLeave()
    {
        var state = GameStateManager.CurrentStateName;
        if (state == eGameStateName.NoLobby || state == eGameStateName.Lobby && Builder.CurrentFloor == null) { State = SoloDropState.Left; return; }
        if (UnityEngine.Time.realtimeSinceStartup - _leaveFrom > LeaveLimit)
            Fail("The game did not reach the lobby within " + LeaveLimit + " s of ending the expedition; it is in " + state + ".");
    }

    private static void Begin(LevelId level, LevelSeeds? seeds, int bots, bool keepSlots, SoloDropState first)
    {
        _level = level; _seeds = seeds; _bots = bots; _keepSlots = keepSlots; Failure = "";
        _injectPressed = _startPressed = _readyPressed = _generationEntered = false;
        _botsWaitFrom = -1f;
        State = first;
        LevelAPI.OnBuildStart -= VerifyBuilder;
        LevelAPI.OnBuildStart += VerifyBuilder;
        EventAPI.OnGameStateChanged -= GameState;
        EventAPI.OnGameStateChanged += GameState;
    }

    /// <summary>Gives up before the drop was requested (nothing is undone: a lobby already opened stays open).</summary>
    public static void Cancel()
    {
        if (State is SoloDropState.Idle or SoloDropState.InLevel or SoloDropState.Failed or SoloDropState.Left) return;
        Unhook();
        State = SoloDropState.Idle;
    }

    internal static void Tick()
    {
        if (State == SoloDropState.Requested) { CheckRequest(); return; }
        if (State == SoloDropState.Leaving) { FollowLeave(); return; }
        if (State is SoloDropState.Idle or SoloDropState.InLevel or SoloDropState.Failed or SoloDropState.Left) return;
        try
        {
            if (SNet.IsInLobby)
            {
                if (State != SoloDropState.Lobby && !SNet.IsMaster) throw new InvalidOperationException("Solo host authority was lost.");
                if (Players.OtherHumanInLobby()) throw new InvalidOperationException("Another human entered the lobby.");
            }
            switch (State)
            {
                case SoloDropState.Menu: Menu(); break;
                case SoloDropState.Lobby:
                    if (GameStateManager.CurrentStateName != eGameStateName.Lobby || !SNet.IsInLobby || !SNet.IsMaster || SNet.LocalPlayer?.IsInSlot != true) break;
                    if (!ArrangeSlots(false)) break;
                    RundownManager.MasterSelectActiveExpedition(eRundownKey.Local, (eRundownTier)_level.Tier, _level.Index,
                        RundownManager.GetLocalRundownKey(_level.RundownId), _level.RundownId);
                    GuiManager.MainMenuLayer.ChangePage(eCM_MenuPage.CMP_LOADOUT);
                    State = SoloDropState.Select;
                    break;
                case SoloDropState.Select:
                    // Opening the loadout page re-applies saved slot permissions; set them again on this later frame.
                    if (!ArrangeSlots(false) || !ActiveMatches() || CM_PageLoadout.Current == null
                        || GuiManager.MainMenuLayer.m_currentPageEnum != eCM_MenuPage.CMP_LOADOUT) break;
                    ApplySeeds();
                    State = SoloDropState.Ready;
                    break;
                case SoloDropState.Ready:
                    if (!ActiveMatches()) throw new InvalidOperationException("The selected expedition changed before the drop.");
                    if (GameStateManager.CurrentStateName != eGameStateName.Lobby) throw new InvalidOperationException("The game left the lobby before the drop request.");
                    if (!ArrangeSlots(true)) break;
                    var loadout = CM_PageLoadout.Current;
                    if (loadout == null) break;
                    if (!GameStateManager.IsReady)
                    {
                        if (!_readyPressed && loadout.m_readyButton?.m_collider?.enabled == true) { _readyPressed = true; loadout.OnReadyButtonClicked(); }
                        break;
                    }
                    if (!GameStateManager.IsEveryoneReady(false) || loadout.m_dropButton?.m_collider?.enabled != true) break;
                    VerifySeeds();
                    if (!BotsSettled()) break;
                    State = SoloDropState.Requested;
                    _requestedAt = UnityEngine.Time.realtimeSinceStartup;
                    loadout.EXT_StartExpedition();
                    break;
            }
        }
        catch (Exception error) { Fail(error.Message, error.ToString()); }
    }

    // The bot part of the game's drop gate (GS_Lobby.TryStartLevelTrigger), which the drop request goes through and which refuses without a word. Bots from a
    // level played before come back when the next lobby opens, and each bot spawned makes the game allow no bot action, the drop included, for its bot delay
    // (15 s, PlayerManager.s_botAutoSpawnDelay). The slots are as asked when this is reached, so that wait has nothing left to protect and is ended the way the
    // game ends it itself (m_playerMissingTime back to -1). The gate itself cannot be asked: when it says yes it has already told everyone to start.
    private static bool BotsSettled()
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        var manager = PlayerManager.Current;
        var settled = manager != null;
        var players = SNet.LobbyPlayers;
        if (settled && players != null)
            for (var i = 0; i < players.Count; i++)
                if (players[i] != null && players[i].IsBot && !players[i].HasPlayerAgent) settled = false;
        if (settled)
        {
            manager!.m_playerMissingTime = -1f;
            return true;
        }
        if (_botsWaitFrom < 0f) _botsWaitFrom = now;
        else if (now - _botsWaitFrom > BotsLimit) throw new InvalidOperationException("The lobby's bots did not settle in " + BotsLimit + " s, the game would refuse the drop: " + Lobby() + ".");
        return false;
    }

    private static void CheckRequest()
    {
        if (_generationEntered || GameStateManager.CurrentStateName != eGameStateName.Lobby || UnityEngine.Time.realtimeSinceStartup - _requestedAt <= RequestLimit) return;
        Fail("The game was asked for the drop and stayed in the lobby for " + RequestLimit + " s: " + Lobby() + ".");
    }

    private static string Lobby()
    {
        var text = "host " + SNet.IsMaster + ", in lobby";
        var players = SNet.LobbyPlayers;
        if (players != null)
            for (var i = 0; i < players.Count; i++)
                if (players[i] != null) text += " " + players[i].NickName + (players[i].IsBot ? " (bot, agent " + players[i].HasPlayerAgent + ")" : "");
        return text;
    }

    private static void Menu()
    {
        if (SNet.IsInLobby || SNet.LocalPlayer?.IsInSessionHub == true) throw new InvalidOperationException("A lobby or session already exists; the drop cannot take it over.");
        var menu = GuiManager.MainMenuLayer;
        var intro = menu?.PageIntro;
        if (!_injectPressed && intro != null && menu!.m_currentPageEnum == eCM_MenuPage.CMP_INTRO && intro.m_step == CM_IntroStep.WaitForInject
            && intro.m_startupLoaded && intro.m_sharedLoaded && intro.m_enemiesLoaded && RundownManager.RundownProgressionReady)
        { _injectPressed = true; intro.EXT_PressInject(0); }
        if (menu != null && menu.m_currentPageEnum == eCM_MenuPage.CMP_START && menu.PageStart.m_ready && !_startPressed)
        { _startPressed = true; menu.PageStart.EXT_PressStart(); }
        if (menu == null || RundownManager.Current == null || !RundownManager.RundownProgressionReady
            || menu.m_currentPageEnum is eCM_MenuPage.CMP_NONE or eCM_MenuPage.CMP_INTRO or eCM_MenuPage.CMP_START
            || GameStateManager.CurrentStateName != eGameStateName.NoLobby) return;
        // Started from another menu page (the settings page, where menu screens such as a replay list stand): back to the rundown first.
        if (menu.m_currentPageEnum is not (eCM_MenuPage.CMP_RUNDOWN_NEW or eCM_MenuPage.CMP_RUNDOWN)) { menu.ChangePage(eCM_MenuPage.CMP_RUNDOWN_NEW); return; }
        if (Global.UseLocalRundownOverride) throw new InvalidOperationException("The local rundown override is on; the active rundown cannot be selected.");
        RequireExpedition();
        Global.RundownIdToLoad = _level.RundownId;
        State = SoloDropState.Lobby;
        SNet.SessionHub.CreateHub(LobbyType.Private, true);
    }

    /// <summary>Whether this game could start <paramref name="level"/> with <paramref name="bots"/> bots, answered without starting anything, so a caller
    /// can say so before the player presses a button. Only the installed rundowns are looked at: a drop can still fail later for what the game does.</summary>
    public static SoloDropRefusal Check(LevelId level, int bots = 0)
    {
        // The menu's list does not decide it: a Rundown package takes a menu slot off a game's rundown, whose block and levels stay loaded.
        var rundown = RundownDataBlock.GetBlock(level.RundownId);
        if (rundown == null) return SoloDropRefusal.RundownMissing;
        if (!level.IsValid) return SoloDropRefusal.LevelMissing;
        var tiers = new[] { rundown.TierA, rundown.TierB, rundown.TierC, rundown.TierD, rundown.TierE };
        var entries = tiers[level.Tier - 1];
        var expedition = entries != null && level.Index < entries.Count ? entries[level.Index] : null;
        if (expedition == null || !expedition.Enabled || expedition.LevelLayoutData == 0) return SoloDropRefusal.LevelMissing;
        // PlayerManager spawns no bots for a single-player expedition.
        return bots > 0 && expedition.IsSinglePlayer ? SoloDropRefusal.NoBots : SoloDropRefusal.None;
    }

    private static void RequireExpedition()
    {
        var refusal = Check(_level, _bots);
        if (refusal != SoloDropRefusal.None) throw new InvalidOperationException("Expedition " + _level.Coordinate + " cannot start: " + refusal + ".");
    }

    private static bool ActiveMatches()
    {
        var active = RundownManager.GetActiveExpeditionData();
        return Global.RundownIdToLoad == _level.RundownId && active.rundownType == eRundownKey.Local
            && RundownManager.ActiveRundownKey == RundownManager.GetLocalRundownKey(_level.RundownId)
            && (int)active.tier == _level.Tier && active.expeditionIndex == _level.Index && RundownManager.ActiveExpedition != null;
    }

    private static void ApplySeeds()
    {
        if (_seeds is not { } seeds) return;
        var data = RundownManager.GetActiveExpeditionData();
        data.hostIDSeed = seeds.HostIdSeed; data.sessionSeed = seeds.SessionSeed;
        // The game's own re-roll uses this setter, which also updates the synchronized expedition package and the expedition's seeds.
        if (!RundownManager.Current.TrySetActiveExpedition(data)) throw new InvalidOperationException("The game's expedition setter rejected the seeds.");
        RundownManager.ActiveExpedition.Seeds.BuildSeed = seeds.BuildSeed;
        VerifySeeds();
    }

    private static void VerifySeeds()
    {
        if (!ActiveMatches()) throw new InvalidOperationException("The active expedition is not the requested one.");
        if (_seeds is not { } seeds) return;
        var data = RundownManager.GetActiveExpeditionData();
        var expedition = RundownManager.ActiveExpedition;
        expedition.GetSeeds(out var host, out var session);
        if (data.hostIDSeed != seeds.HostIdSeed || data.sessionSeed != seeds.SessionSeed || host != seeds.HostIdSeed
            || session != seeds.SessionSeed || expedition.Seeds.BuildSeed != seeds.BuildSeed)
            throw new InvalidOperationException("The game read the seeds back differently: build " + expedition.Seeds.BuildSeed + ", host " + host + ", session " + session + ".");
    }

    /// <summary>The first <see cref="_bots"/> other slots are open to bots, the rest closed (the game's PlayerManager spawns bots into the open
    /// ones and kicks a bot out of a closed one). True when the lobby holds the local player and bots only: no more bots than asked for, or,
    /// with <paramref name="botsJoined"/>, exactly that many, each in its slot with its player agent and backpack.</summary>
    private static bool ArrangeSlots(bool botsJoined)
    {
        // A rejoin keeps the lobby's own slots and bots: only the local player may be the human in it.
        if (_keepSlots)
        {
            var lobby = SNet.LobbyPlayers;
            return lobby != null && lobby.Count == 1 && lobby[0]?.IsLocal == true && SNet.LocalPlayer?.IsInSlot == true;
        }
        // Bots are not in SNet.LobbyPlayers (it lists the humans only): they are counted in the slots.
        var open = 0;
        var local = false;
        var ready = true;
        var bots = 0;
        var leaving = false;
        for (var i = 0; i < SNet.Slots.PlayerSlots.Length; i++)
        {
            var player = SNet.Slots.GetPlayerInSlot(i);
            if (player?.IsLocal == true) { local = true; continue; }
            if (player != null && !player.IsBot) throw new InvalidOperationException("Another human occupies a player slot.");
            var botSlot = open++ < _bots;
            var wanted = botSlot ? SNet_PlayerSlotManager.SlotPermission.Bot : SNet_PlayerSlotManager.SlotPermission.Forbidden;
            if (SNet.Slots.GetSlotPermission(i) != wanted)
            {
                SNet.Slots.SetSlotPermission(i, wanted);
                if (SNet.Slots.GetSlotPermission(i) != wanted) throw new InvalidOperationException("Slot " + i + " did not become " + wanted + ".");
            }
            if (player == null) continue;
            // A bot in a closed slot is being kicked out by the game: wait for it to go.
            if (!botSlot) { leaving = true; continue; }
            bots++;
            ready &= player.IsInSlot && player.HasPlayerAgent && PlayerBackpackManager.GetBackpack(player) != null;
        }
        var humans = SNet.LobbyPlayers;
        if (leaving || humans == null || humans.Count != 1 || humans[0]?.IsLocal != true) return false;
        return local && (!botsJoined || bots == _bots && ready);
    }

    private static void VerifyBuilder()
    {
        if (State != SoloDropState.Requested || _seeds is not { } seeds) return;
        if (Builder.BuildSeed != unchecked((uint)seeds.BuildSeed) || Builder.HostIDSeedRandom?.Seed != seeds.HostIdSeed
            || Builder.SessionSeedRandom?.Seed != seeds.SessionSeed)
            Fail("The builder generates with other seeds than requested: build " + Builder.BuildSeed + ", host " + Builder.HostIDSeedRandom?.Seed
                + ", session " + Builder.SessionSeedRandom?.Seed + ".");
    }

    private static void GameState(eGameStateName previous, eGameStateName next)
    {
        if (State != SoloDropState.Requested) return;
        if (next is not (eGameStateName.Lobby or eGameStateName.NoLobby)) _generationEntered = true;
        else if (_generationEntered) Fail("The drop returned to " + next + "; it is not retried.");
        if (next == eGameStateName.InLevel) { State = SoloDropState.InLevel; Unhook(); }
    }

    private static void Fail(string reason, string? detail = null)
    {
        if (State == SoloDropState.Failed) return;
        Failure = reason;
        State = SoloDropState.Failed;
        Unhook();
        Plugin.Logger.Error("core-solo-drop-failed", "Solo drop of " + _level.Coordinate + " failed: " + (detail ?? reason));
    }

    private static void Unhook()
    {
        LevelAPI.OnBuildStart -= VerifyBuilder;
        EventAPI.OnGameStateChanged -= GameState;
    }
}
