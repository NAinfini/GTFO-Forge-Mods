using System;
using CellMenu;
using GameData;
using GTFO.API;
using LevelGeneration;
using SNetwork;
using Global = Globals.Global;

namespace ForgeCore;

public enum SoloDropState { Idle, Menu, Lobby, Select, Ready, Requested, InLevel, Failed }

/// <summary>Starts one expedition as a private solo host, with chosen seeds, through the game's own menu calls: pass the intro and start
/// pages, open a private lobby, close the other slots, select the expedition, set the seeds through the game's own setter, ready up and
/// request the drop. No input is simulated. It works only from the main menu with no lobby open; the first thing that is not as expected
/// (another human in the lobby, the selection changing, the drop returning to the lobby) ends it in <see cref="SoloDropState.Failed"/> with the reason in
/// <see cref="Failure"/> and in the log, and it never retries. Main thread; the plugin's frame update drives it.</summary>
public static class SoloDrop
{
    private static LevelId _level;
    private static LevelSeeds? _seeds;
    private static bool _injectPressed, _startPressed, _readyPressed, _generationEntered;

    public static SoloDropState State { get; private set; }

    /// <summary>Why <see cref="SoloDropState.Failed"/>; empty otherwise.</summary>
    public static string Failure { get; private set; } = "";

    /// <summary>Begins the drop; with <paramref name="seeds"/> null the game draws its own seeds as in an ordinary start (read them afterwards with
    /// <see cref="Level.TryCurrentLevel"/>). Throws when one is already running or the level is not valid; everything the game refuses later ends in
    /// <see cref="SoloDropState.Failed"/>.</summary>
    public static void Start(LevelId level, LevelSeeds? seeds = null)
    {
        if (State is not (SoloDropState.Idle or SoloDropState.InLevel or SoloDropState.Failed))
            throw new InvalidOperationException("A solo drop is already running (" + State + ").");
        if (!level.IsValid) throw new ArgumentException("The level is not a valid expedition.", nameof(level));
        _level = level; _seeds = seeds; Failure = "";
        _injectPressed = _startPressed = _readyPressed = _generationEntered = false;
        State = SoloDropState.Menu;
        LevelAPI.OnBuildStart -= VerifyBuilder;
        LevelAPI.OnBuildStart += VerifyBuilder;
        EventAPI.OnGameStateChanged -= GameState;
        EventAPI.OnGameStateChanged += GameState;
    }

    /// <summary>Gives up before the drop was requested (nothing is undone: a lobby already opened stays open).</summary>
    public static void Cancel()
    {
        if (State is SoloDropState.Idle or SoloDropState.InLevel or SoloDropState.Failed) return;
        Unhook();
        State = SoloDropState.Idle;
    }

    internal static void Tick()
    {
        if (State is SoloDropState.Idle or SoloDropState.InLevel or SoloDropState.Failed or SoloDropState.Requested) return;
        try
        {
            if (SNet.IsInLobby)
            {
                if (State != SoloDropState.Lobby && !SNet.IsMaster) throw new InvalidOperationException("Solo host authority was lost.");
                var players = SNet.LobbyPlayers;
                if (players != null)
                    for (var i = 0; i < players.Count; i++)
                        if (players[i] != null && !players[i].IsLocal && !players[i].IsBot)
                            throw new InvalidOperationException("Another human entered the lobby.");
            }
            switch (State)
            {
                case SoloDropState.Menu: Menu(); break;
                case SoloDropState.Lobby:
                    if (GameStateManager.CurrentStateName != eGameStateName.Lobby || !SNet.IsInLobby || !SNet.IsMaster || SNet.LocalPlayer?.IsInSlot != true) break;
                    if (!CloseOtherSlots()) break;
                    RundownManager.MasterSelectActiveExpedition(eRundownKey.Local, (eRundownTier)_level.Tier, _level.Index,
                        RundownManager.GetLocalRundownKey(_level.RundownId), _level.RundownId);
                    GuiManager.MainMenuLayer.ChangePage(eCM_MenuPage.CMP_LOADOUT);
                    State = SoloDropState.Select;
                    break;
                case SoloDropState.Select:
                    // Opening the loadout page re-applies saved slot permissions; close them again on this later frame.
                    if (!CloseOtherSlots() || !ActiveMatches() || CM_PageLoadout.Current == null
                        || GuiManager.MainMenuLayer.m_currentPageEnum != eCM_MenuPage.CMP_LOADOUT) break;
                    ApplySeeds();
                    State = SoloDropState.Ready;
                    break;
                case SoloDropState.Ready:
                    if (!ActiveMatches()) throw new InvalidOperationException("The selected expedition changed before the drop.");
                    if (GameStateManager.CurrentStateName != eGameStateName.Lobby) throw new InvalidOperationException("The game left the lobby before the drop request.");
                    if (!CloseOtherSlots()) break;
                    var loadout = CM_PageLoadout.Current;
                    if (loadout == null) break;
                    if (!GameStateManager.IsReady)
                    {
                        if (!_readyPressed && loadout.m_readyButton?.m_collider?.enabled == true) { _readyPressed = true; loadout.OnReadyButtonClicked(); }
                        break;
                    }
                    if (!GameStateManager.IsEveryoneReady(false) || loadout.m_dropButton?.m_collider?.enabled != true) break;
                    VerifySeeds();
                    State = SoloDropState.Requested;
                    loadout.EXT_StartExpedition();
                    break;
            }
        }
        catch (Exception error) { Fail(error.ToString()); }
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
        if (RundownManager.Current == null || !RundownManager.RundownProgressionReady
            || menu?.m_currentPageEnum is not (eCM_MenuPage.CMP_RUNDOWN_NEW or eCM_MenuPage.CMP_RUNDOWN)
            || GameStateManager.CurrentStateName != eGameStateName.NoLobby) return;
        if (Global.UseLocalRundownOverride) throw new InvalidOperationException("The local rundown override is on; the active rundown cannot be selected.");
        RequireExpedition();
        Global.RundownIdToLoad = _level.RundownId;
        State = SoloDropState.Lobby;
        SNet.SessionHub.CreateHub(LobbyType.Private, true);
    }

    private static void RequireExpedition()
    {
        var active = Global.ActiveRundownIds;
        var listed = false;
        if (active != null) foreach (var id in active) listed |= id == _level.RundownId;
        if (!listed) throw new InvalidOperationException("Rundown " + _level.RundownId + " is not an active rundown.");
        var rundown = RundownDataBlock.GetBlock(_level.RundownId) ?? throw new InvalidOperationException("The rundown block is unavailable: " + _level.RundownId);
        var tiers = new[] { rundown.TierA, rundown.TierB, rundown.TierC, rundown.TierD, rundown.TierE };
        var entries = tiers[_level.Tier - 1];
        var expedition = entries != null && _level.Index < entries.Count ? entries[_level.Index] : null;
        if (expedition == null || !expedition.Enabled || expedition.LevelLayoutData == 0)
            throw new InvalidOperationException("Expedition " + _level.Coordinate + " does not exist or is not enabled.");
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

    private static bool CloseOtherSlots()
    {
        for (var i = 0; i < SNet.Slots.PlayerSlots.Length; i++)
        {
            var player = SNet.Slots.GetPlayerInSlot(i);
            if (player?.IsLocal == true) continue;
            if (player != null && !player.IsBot) throw new InvalidOperationException("Another human occupies a player slot.");
            if (SNet.Slots.GetSlotPermission(i) != SNet_PlayerSlotManager.SlotPermission.Forbidden)
            {
                SNet.Slots.SetSlotPermission(i, SNet_PlayerSlotManager.SlotPermission.Forbidden);
                if (SNet.Slots.GetSlotPermission(i) != SNet_PlayerSlotManager.SlotPermission.Forbidden)
                    throw new InvalidOperationException("Slot " + i + " did not become forbidden.");
            }
        }
        var players = SNet.LobbyPlayers;
        return players != null && players.Count == 1 && players[0]?.IsLocal == true;
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

    private static void Fail(string reason)
    {
        if (State == SoloDropState.Failed) return;
        Failure = reason;
        State = SoloDropState.Failed;
        Unhook();
        Plugin.Logger.Error("Solo drop of " + _level.Coordinate + " failed: " + reason);
    }

    private static void Unhook()
    {
        LevelAPI.OnBuildStart -= VerifyBuilder;
        EventAPI.OnGameStateChanged -= GameState;
    }
}