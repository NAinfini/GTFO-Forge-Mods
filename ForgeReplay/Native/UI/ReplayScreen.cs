using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ForgeCore;
using ForgeUI;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeReplay;

/// <summary>
/// The replay's screen while a recording plays (PLAN 2.2): the time bar (ForgeUI <see cref="HudTimeline"/>) with the recording's marks,
/// a card per player (health, infection, ammunition, held weapon, downed or grabbed; a click watches that player), the kill feed, recorded
/// chat popping up above the time bar as it is reached, a chat log whose lines jump to their moment, the statistics with exact, estimated
/// and empty told apart (beside the cards, or held Tab in the middle as a scoreboard), the automatic camera, the watching or
/// using-the-screen state with the view and its keys, and the way back to the list. Every colour is the game's own (<see cref="UIKitColors"/>):
/// the HUD's cyan-white text on the settings windows' dark, its red for alerts and the exit, the teammate notices for downed and grabbed,
/// the player colours for the players. The camera and its keys are <see cref="ReplayCamera"/>; the enemy outlines <see cref="EnemyMarks"/>.
/// The game's own HUD is hidden (its chat box too: recorded chat is drawn here, not posted into the observer's chat); the HUD pointer
/// shows only while the screen is in use.
/// </summary>
internal static partial class ReplayScreen
{
    /// <summary>The colour of a player's card, name and outline: the game's colour of the slot.</summary>
    internal static Color SlotColour(int slot) => UIKit.PlayerColour(slot);

    // A player's own name colour (a colour tag in the name, ForgeUI PlayerName), else the given colour; the names in Names are without tags.
    private static readonly Color?[] NameColours = new Color?[4];
    private static Color NameColour(int slot, Color otherwise) => slot is >= 0 and < 4 && NameColours[slot] is { } own ? own : otherwise;

    private static UIKitMod _ui = null!;
    private static HudElement? _hud;
    private static HudCanvas? _canvas;
    private static HudTimeline? _bar;
    private static GameHudHide? _hide;
    /// <summary>The pointer is on the screen's own controls (as of the last frame drawn): a left click there is not the camera's.</summary>
    internal static bool PointerOnHud { get; private set; }
    private static bool _onHudBefore;
    /// <summary>The pointer was on the screen's own controls this frame or the one before (the camera's mouse keys read it before the screen is drawn).</summary>
    internal static bool PointerNearHud => PointerOnHud || _onHudBefore;

    /// <summary>The wheel belongs to the screen this frame (the pointer on the time bar or an open list), not to the camera.</summary>
    internal static bool WheelTaken { get; private set; }

    private static HintRow? _hints;
    private static bool _menuEsc, _wasCovered;
    // When Esc went down (-1: not held): a tap closes the top layer, held ExitSeconds it leaves the replay.
    private static float _closeFrom = -1f;
    private static bool _overPanel;
    private static bool _on, _auto = true, _showLog, _showEvents, _showStats, _showHighlights, _outlines = true, _board, _showMenu, _pinKeys, _overMenu, _overMore, _overKeys, _hidden;
    // The highlights of the recording and its quiet stretches, once per analysis; the speed the quiet skip left (0: not skipping).
    private static List<Highlight> _highlights = new();
    private static readonly List<string> HighlightTexts = new();
    private static List<IdleSpan> _idle = new();
    private static float _paceFrom;
    private static int _scroll;
    // Each player's "no aiming recorded" notice is shown once a replay.
    private static readonly bool[] NoAimTold = new bool[4];
    // How many skipped things the screen has told of, and when it may tell again: a quiet notice, not one per thing.
    private static int _skippedTold;
    private static long _skippedNext;
    private static uint _lastSwitchMs;
    private static Analysis? _marked;
    private static string _title = "", _subtitle = "";

    // Who holds each slot at the moment on the bar: the file header's players, changed by the recording's joins and leaves (Presence).
    private static readonly string[] Names = new string[4], Loadouts = new string[4];
    private static readonly bool[] InRecording = new bool[4];
    // The slots held at any moment of the recording: the statistics' columns.
    private static readonly bool[] EverHeld = new bool[4];
    private static ReplayHeader? _header;
    private static int _presenceSeen = -1;
    private static List<KillLine> _kills = new();
    private static readonly List<(string Killer, string Weapon, string Enemy)> KillTexts = new();
    // The top-right feed: kills and wake-ups in time order. Who is the player's slot (-1 when the game does not say), How the weapon or the
    // cause, Exact false marks a kill worked out by this machine (~); a wake-up whose waker the game does not give reads 「无法确定」.
    // Entity and JumpMs: the enemy of the line and where a click on it goes (five seconds before a kill, not before the enemy came; a wake-up itself).
    private static readonly List<(uint TimeMs, int Slot, string Who, string How, string Enemy, bool Exact, bool Wake, int Entity, uint JumpMs)> FeedItems = new();
    private static StatCell[,] _stats = new StatCell[0, 0];
    // Per player: the weapon and the enemy kind with the most certain kills up to the moment on the bar ("" when none).
    private static readonly string[] TopWeapon = { "", "", "", "" }, TopEnemy = { "", "", "", "" };
    private static readonly List<string> HowTexts = new();
    private static readonly string[] RowTitles = new string[ReplayFeed.Rows.Length];
    private static string _topWeaponTitle = "", _topEnemyTitle = "";
    private static float _fullHealth = -1f;
    private static bool _hearingFailed;
    private static readonly Dictionary<ulong, Hearing?> Hearings = new();

    // Per player card, rebuilt only when the recorded status changes.
    private static readonly byte[]?[] CardStatus = new byte[]?[4];
    private static readonly int[] CardHealth = { -1, -1, -1, -1 }, CardMove = new int[4];
    // What the player has, one row an item: its name, what is left of it, and the inventory slot it is in, to mark the one in the hands.
    // Share: how full it is (0 to 1) when Count is a percent, else below 0.
    private readonly record struct CardRow(string Name, string Count, InventorySlot Slot, float Share = -1f);
    private static readonly List<CardRow>[] CardRows = { new(), new(), new(), new() };
    private static readonly Analysis?[] CardReport = new Analysis?[4];
    private static readonly PlayerKit[] CardKit = new PlayerKit[4];
    private static readonly InventorySlot[] CardHeld = new InventorySlot[4];
    private static readonly bool[] CardDown = new bool[4];
    private static readonly float[] CardInfection = new float[4];
    internal static void Load(UIKitMod ui)
    {
        _ui = ui;
        ReplayKeys.Load(ui);
        // Three layers, each wholly over the one before: world marks, panels, what a button opens. Small texts on plates read best without glow or colour fringe.
        _hud = ui.AddHud("Screen", new HudSlot(HudRegion.World), hud =>
        {
            hud.Effects = false;
            _world = new HudCanvas(hud, layer: -3000);
            _canvas = new HudCanvas(hud, rectOrder: -800, stackPlates: true);
            _over = new HudCanvas(hud, rectOrder: -800, layer: 3000, stackPlates: true);
            _hints = new HintRow(hud);
        });
        if (_hud != null) _hud.Visible = false;
        _bar = ui.AddTimeline("Timeline");
        if (_bar != null)
        {
            _bar.Seek = Replayer.Jump;
            _bar.SeekMark = JumpToEvent;
            _bar.PlayPause = () => Replayer.Clock?.Toggle();
            _bar.Step = Step;
            _bar.SpeedChosen = Speed;
            _bar.Skip = ms => { if (Replayer.Clock is { } clock) Replayer.Jump((uint)Math.Clamp((long)clock.PositionMs + ms, 0L, clock.DurationMs)); };
            _bar.JumpMark = direction => { if (Replayer.Report is { } report && Replayer.Clock is { } clock) JumpMark(report, clock.PositionMs, direction > 0); };
        }
        _hide = ui.HideGameHud("Replay", GameHudPart.None);
        // In the eyes the middle of the screen is marked as in play: a dot and four ticks drawn from the game's crosshair image on its layer,
        // gone while the watched player aims down sights (the game shows no crosshair then).
        _aim = ui.AddCrosshairAttachment("Eyes");
        if (_aim != null) { _aim.Dot = true; _aim.Ring = false; _aim.Radius = 10f; _aim.TickLength = 6f; _aim.HideWhileAiming = true; }
    }

    private static CrosshairAttachment? _aim;

    /// <summary>How far an enemy type hears a player, from its detection data block; null when the block is missing (logged once).</summary>
    internal static Hearing? HearingOf(ulong enemyDataId)
    {
        if (Hearings.TryGetValue(enemyDataId, out var known)) return known;
        Hearing? hearing = null;
        try
        {
            var enemy = GameData.EnemyDataBlock.GetBlock((uint)enemyDataId);
            var detection = enemy == null ? null : GameData.EnemyDetectionDataBlock.GetBlock(enemy.DetectionDataId);
            if (detection != null) hearing = new Hearing(detection.movementDetectionDistance, detection.weaponDetectionDistanceMax);
            else Once("The detection data of enemy data block " + enemyDataId + " is not loaded; its wake-ups are explained by recorded noises only.");
        }
        catch (Exception error) { Once("Reading the detection data of enemy data block " + enemyDataId + " failed: " + error.Message); }
        Hearings[enemyDataId] = hearing;
        return hearing;
    }

    private static void Once(string message)
    {
        if (_hearingFailed) return;
        _hearingFailed = true;
        Plugin.Logger.Error(message);
    }

    /// <summary>The game's full player health (PlayerDataBlock 1), 0 when it cannot be read: the damage taken read off a health bar is a share of it.</summary>
    internal static float FullHealth()
    {
        if (_fullHealth >= 0f) return _fullHealth;
        try { _fullHealth = GameData.PlayerDataBlock.GetBlock(1u)?.health ?? 0f; }
        catch (Exception error) { Plugin.Logger.Error("Reading the full player health failed: " + error.Message); _fullHealth = 0f; }
        if (_fullHealth <= 0f) { Plugin.Logger.Error("PlayerDataBlock 1 gives no full health; damage taken read off health bars is left empty."); _fullHealth = 0f; }
        return _fullHealth;
    }

    internal static void Tick()
    {
        var clock = Replayer.Clock;
        var track = Replayer.Track;
        if (!Replayer.Active || clock == null || track == null || Replayer.Pending?.Header == null)
        {
            if (_on) Off();
            return;
        }
        if (!_on) On(Replayer.Pending);
        // The game's own menu (Esc: its leave-to-lobby dialog) is the top layer: the screen steps out of the picture while it is open, so the two
        // are not drawn over each other (11-08: the HUD ran through the dialog), and comes back when it closes.
        var covered = Replayer.GameMenuOpen();
        // An Esc that comes while the game's menu is open, or the frame after it closed, is the menu's own (it closes it): the replay's
        // Esc keys read the raw key whatever has the focus, and acting on it too gave the menu straight back on the key coming up.
        _menuEsc = covered || _wasCovered;
        _wasCovered = covered;
        if (covered) _closeFrom = -1f;
        if (_bar != null) _bar.Visible = !Replayer.Preparing && !Hidden && !covered;
        if (_aim != null) _aim.Visible = !Replayer.Preparing && !Hidden && !covered && FirstPerson.Active && !FirstPerson.Aiming;
        // Hidden, the screen still draws one faint line saying how to bring it back (Draw).
        if (_hud != null) _hud.Visible = !covered;
        if (covered) PointerOnHud = false;
        // The hide key's edge is read every frame, also while preparing and while the game's menu covers the screen, so a press then is not taken as a new one afterwards.
        var hideDown = ReplayKeys.Held(ReplayKeys.HideUi) || ReplayKeys.Pressed(ReplayKeys.HideUi);
        if (hideDown && !_hideWasDown && !_menuEsc && !Replayer.Preparing) _hidden = !_hidden;
        _hideWasDown = hideDown;
        if (Replayer.Preparing)
        {
            if (ReplayKeys.Pressed(ReplayKeys.Play)) Replayer.SkipPreparing();
            Preparing();
            return;
        }
        if (Skipped.Count > _skippedTold && Stopwatch.GetTimestamp() >= _skippedNext)
        {
            _skippedTold = Skipped.Count; _skippedNext = Stopwatch.GetTimestamp() + 20 * Stopwatch.Frequency;
            Notices.Push(_ui.Text("toast.skipped", _skippedTold), NoticeTone.Warn, 5f, ForgeCore.Frame.UnscaledTime);
        }
        ReplayCamera.Keys(InRecording, clock);
        var now = clock.PositionMs;
        var analysis = Replayer.Report;
        MarkKeys(analysis, now);
        Roster(analysis, now);
        if (_bar != null)
        {
            _bar.DurationMs = clock.DurationMs; _bar.PositionMs = now; _bar.Playing = clock.Playing; _bar.Speed = clock.Speed;
            if (!ReferenceEquals(analysis, _marked)) Mark(analysis);
            // In a fight a number changes nearly every frame: the table (cells, texts, top weapons) is made again at most four times a second.
            if (_statsClock != null)
            {
                _statsDirty |= _statsClock.Advance(now);
                if (_statsDirty && ForgeCore.Frame.UnscaledTime >= _statsNext) { _statsDirty = false; _statsNext = ForgeCore.Frame.UnscaledTime + 0.25f; Statistics(_statsClock.Players, now); }
            }
        }
        if (_auto && analysis != null)
        {
            // Jumped back past the last switch: the stay counts from here. A big enemy hunting a player may be chosen too, but not while the view is a
            // player's eyes (the first-person view is of a player: it only changes whose).
            if (now < _lastSwitchMs) _lastSwitchMs = now;
            var follow = Director.Choose(track.State, analysis, now, ReplayCamera.Target, _lastSwitchMs, ReplayCamera.Mode == ViewMode.Eye ? null : Replayer.Big);
            if (follow >= 0 && follow != ReplayCamera.Target)
            {
                if (follow >= 4) ReplayCamera.WatchEnemy(follow, byHand: false);
                else { Replayer.Follow = follow; ReplayCamera.StopEnemy(); }
                _lastSwitchMs = now;
            }
        }
        Pace(clock, now);
        Draw(track.State, analysis, now);
        EnemyMarks.On = _outlines;
        EnemyMarks.Tick(track.State, ReplayCamera.Shown, ReplayCamera.ShownTurn * Vec.Forward);
    }

    /// <summary>The screen is out of the picture: hidden by the player, or while the film path is flown.</summary>
    private static bool Hidden => _hidden || ReplayCamera.Flying;

    // The quiet skip (PLAN 2.2), only while the automatic camera is on: a quiet stretch plays at 8x, and the speed it had comes back when it
    // ends, when the camera is taken by hand or playing stops. A speed changed by hand during the stretch is left as it is.
    private static void Pace(PlaybackClock clock, uint now)
    {
        var idle = _auto && clock.Playing && Pacing.Idle(_idle, now);
        if (idle && _paceFrom == 0f && clock.Speed < Pacing.Fast) { _paceFrom = clock.Speed; Speed(Pacing.Fast); }
        else if (!idle && _paceFrom > 0f)
        {
            if (clock.Speed == Pacing.Fast) Speed(_paceFrom);
            _paceFrom = 0f;
        }
    }

    /// <summary>Every way to an event (the Events list, the feed, the chat log, a mark on the bar): five seconds before it, watching the player when it is a down; the speed is left as it is.</summary>
    private static void JumpToEvent(uint timeMs)
    {
        Replayer.Jump(Highlights.Before(timeMs));
        if (Replayer.Report is { } analysis && Highlights.DownAt(analysis, timeMs) is var downed and >= 0) WatchPlayer(downed);
    }

    // A player chosen from a list or a card's button: watched in the current view (not the swap a second click on the card does).
    private static void WatchPlayer(int slot)
    {
        if (slot is < 0 or > 3) return;
        Replayer.Follow = slot;
        ReplayCamera.StopEnemy();
        ManualChoice();
    }

    /// <summary>A short notice in the replay's own corner, its text from the language file.</summary>
    internal static void Notice(string key, params object[] args) => Notices.Push(_ui.Text(key, args), NoticeTone.Info, 3f, ForgeCore.Frame.UnscaledTime);

    // Until the clock starts the world behind is not yet what the recording shows: the whole screen is covered, and says what it is waiting for. Every number is
    // read from the level; Space starts the clock at once. The bar counts what has arrived: the landing, each body and each piece of the watched player's gear.
    private static void Preparing()
    {
        var hud = _hud;
        var c = _canvas;
        if (hud == null || c == null || !c.Begin()) return;
        _world?.Clear();
        _over?.Clear();
        if (HudPointer.ScreenBounds(hud, out var left, out var bottom, out var right, out var top))
        {
            var x = (left + right) / 2f;
            var y = (bottom + top) / 2f;
            _hints?.Hide(); // what the keys do comes when the replay has started
            c.Rect(x, y, right - left, top - bottom, Color.black);
            var p = Replayer.PrepareProgress();
            var total = 1 + p.BodiesWanted + FirstPerson.GearLines;
            var done = (p.Landed ? 1 : 0) + Math.Min(p.Bodies, p.BodiesWanted) + p.Gear;
            var share = Math.Clamp(done / (float)total, 0f, 1f);
            const float barWidth = 480f;
            c.Text(_ui.Text("screen.preparing"), x, y + 58f, 22f, UIKitColors.MenuOver, HudAlign.Centre, bold: true);
            c.Text(_ui.Text(p.Landed ? "prepare.waiting" : "prepare.landing", p.Bodies, p.BodiesWanted, p.Gear, FirstPerson.GearLines), x, y + 24f, 16f, UIKitColors.GameText, HudAlign.Centre);
            c.Rect(x, y - 4f, barWidth, 8f, Fade(UIKitColors.GameText, 0.15f));
            if (share > 0f) c.Rect(x - barWidth / 2f + barWidth * share / 2f, y - 4f, barWidth * share, 8f, UIKitColors.MenuOver);
            c.Text(_ui.Text("prepare.skip", Math.Min(p.Waited, p.Limit).ToString("0", CultureInfo.InvariantCulture), p.Limit.ToString("0", CultureInfo.InvariantCulture), ReplayKeys.Name(ReplayKeys.Play)), x, y - 32f, 14f, Fade(UIKitColors.GameText, 0.7f), HudAlign.Centre);
        }
        c.End();
    }

    // The screen's own keys: Shift and an arrow (or the mouse's side buttons) the next mark after the moment on the bar, or the one before it (a
    // mark just passed counts as the moment itself, so pressing back twice goes back two marks); the chat log (using the screen, to click a line),
    // the keys list and hiding the whole screen.
    private static bool _hideWasDown;

    private static void MarkKeys(Analysis? analysis, uint now)
    {
        if (ReplayKeys.Pressed(ReplayKeys.Close) && !_menuEsc) _closeFrom = ForgeCore.Frame.UnscaledTime;
        if (_closeFrom >= 0f)
        {
            var held = ForgeCore.Frame.UnscaledTime - _closeFrom;
            if (ReplayKeys.Held(ReplayKeys.Close) && held >= ExitSeconds) { _closeFrom = -1f; Exit(); return; }
            if (!ReplayKeys.Held(ReplayKeys.Close)) { _closeFrom = -1f; if (held < TapSeconds) CloseTop(); }
        }
        if (ReplayKeys.Pressed(ReplayKeys.Help)) { _pinKeys = !_pinKeys; _showMenu = false; }
        if (ReplayKeys.Pressed(ReplayKeys.Chat))
        {
            Panel(Tab.Chat);
            if (_showLog) ReplayCamera.SetUi(true);
        }
        if (analysis == null) return;
        if (ReplayKeys.Pressed(ReplayKeys.NextMark) || ReplayKeys.Pressed(ReplayKeys.MarkForward)) JumpMark(analysis, now, true);
        if (ReplayKeys.Pressed(ReplayKeys.PreviousMark) || ReplayKeys.Pressed(ReplayKeys.MarkBack)) JumpMark(analysis, now, false);
    }

    // Esc tapped: the top layer closes (the menu or the keys, the hidden screen, a panel, using the screen); with none, the game's own menu opens.
    private static void CloseTop()
    {
        if (ReplayCamera.Flying) { ReplayCamera.Land(); return; }
        if (_showMenu || _pinKeys) { _showMenu = _pinKeys = false; return; }
        if (_hidden) { _hidden = false; return; }
        if (_showEvents || _showLog || _showStats || _showHighlights) { _showEvents = _showLog = _showStats = _showHighlights = false; return; }
        if (ReplayCamera.Enemy >= 0) { ReplayCamera.StopEnemy(); return; }
        if (ReplayCamera.Ui) { ReplayCamera.SetUi(false); return; }
        ReplayCamera.GameMenu();
    }

    private enum Tab { Events, Highlights, Chat, Stats }

    // One panel at a time beside its tab; the open one's tab closes it.
    private static void Panel(Tab tab)
    {
        var open = tab switch { Tab.Events => _showEvents, Tab.Highlights => _showHighlights, Tab.Chat => _showLog, _ => _showStats };
        _showEvents = _showLog = _showStats = _showHighlights = false;
        _scroll = 0;
        if (open) return;
        if (tab == Tab.Events) _showEvents = true;
        else if (tab == Tab.Highlights) _showHighlights = true;
        else if (tab == Tab.Chat) _showLog = true;
        else _showStats = true;
    }

    private static long _lastLanding = -1;

    private static void JumpMark(Analysis analysis, uint now, bool next)
    {
        var landing = Highlights.MarkLanding(analysis, now, next, _lastLanding);
        if (landing < 0) return;
        _lastLanding = landing;
        Replayer.Jump((uint)landing);
    }

    private static void On(ReplayEntry entry)
    {
        _on = true;
        Array.Clear(RowsReserved); _statsWidth = _feedWidth = _hintsWidth = 0f;
        _header = entry.Header!;
        UIKitColors.Refresh();
        _title = ReplayList.LevelName(entry);
        _subtitle = (entry.Start == default ? "" : entry.Start.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "   ") + (string.IsNullOrEmpty(_header.RecorderName) ? "" : _ui.Text("screen.recorder", PlayerName.Plain(_header.RecorderName), _ui.Text(_header.RecorderIsHost ? "list.host" : "list.client")));
        _hidden = false; _scroll = 0; _closeFrom = -1f; Array.Clear(NoAimTold); _skippedTold = 0; _skippedNext = 0; for (var i = 0; i < 4; i++) CardCounts[i] = "";
        Notices.Clear(); _missionFull = 0f;
        for (var r = 0; r < ReplayFeed.Rows.Length; r++) RowTitles[r] = _ui.Text(ReplayFeed.Key(ReplayFeed.Rows[r]));
        _topWeaponTitle = _ui.Text("stats.topweapon"); _topEnemyTitle = _ui.Text("stats.topenemy");
        _presenceSeen = -1;
        Roster(null, 0);
        for (var i = 0; i < 4; i++) EverHeld[i] = InRecording[i];
        _kills = new List<KillLine>();
        KillTexts.Clear();
        FeedItems.Clear();
        _marked = null; _lastSwitchMs = 0; _auto = true; _paceFrom = 0f; _highlights = new(); _idle = new(); HighlightTexts.Clear(); Pops.Clear(); PopsMade = 0;
        _showLog = _showEvents = _showStats = _showHighlights = _board = _showMenu = _pinKeys = false;
        _page = StatsPage.Team;
        ChatRows.Clear();
        if (_hud != null) _hud.Visible = true;
        if (_bar != null) { _bar.Visible = true; _bar.Colour = UIKitColors.GameText; _bar.Backing = Plate(); }
        ReplayCamera.On();
        if (_hide != null) _hide.Active = true;
        _ui.HudAlone = true;
        ReplayAssist.Allies = _outlines;
    }

    private static void Off()
    {
        _on = false;
        if (_hud != null) _hud.Visible = false;
        if (_bar != null) _bar.Visible = false;
        if (_aim != null) _aim.Visible = false;
        ReplayCamera.Off();
        if (_hide != null) _hide.Active = false;
        _ui.HudAlone = false;
        EnemyMarks.On = false;
        EnemyMarks.Clear();
        _marked = null;
        ClearRating();
    }

    /// <summary>The observer picked a player or a view: the automatic camera stops choosing.</summary>
    internal static void ManualChoice() => _auto = false;
    internal static bool Auto => _auto;

    /// <summary>The automatic camera on or off (its button, the 0 key).</summary>
    internal static void ToggleAuto() => _auto = !_auto;

    private static void Step(int direction)
    {
        var clock = Replayer.Clock;
        if (clock == null) return;
        clock.Step(direction);
        if (direction < 0) Replayer.Jump(clock.PositionMs);
    }

    private static void Speed(float speed)
    {
        var clock = Replayer.Clock;
        if (clock == null) return;
        var want = Array.IndexOf(PlaybackClock.Speeds, speed);
        var have = Array.IndexOf(PlaybackClock.Speeds, clock.Speed);
        if (want >= 0 && have >= 0) clock.ChangeSpeed(want - have);
    }

    /// <summary>The marks of the recording on the bar, the chat log lines and the statistics table, once per analysis.</summary>
    private static void Mark(Analysis? analysis)
    {
        _marked = analysis;
        Array.Clear(ListWidths);
        if (_bar == null) return;
        _bar.Marks.Clear();
        _bar.Spans.Clear();
        _bar.Lanes = new[] { _ui.Text("tl.team"), _ui.Text("tl.level") };
        _bar.PreviousLabel = _ui.Text("tl.prev"); _bar.NextLabel = _ui.Text("tl.next");
        if (analysis != null) Spans(analysis, _bar.Spans);
        ChatRows.Clear();
        if (analysis == null) return;
        _kills = analysis.Kills;
        KillTexts.Clear();
        // Every name and weapon as of its own moment: a player who joined later, or a bot in the slot before them, keeps their own.
        foreach (var k in _kills)
        {
            var holder = HolderAt(analysis, k.Killer, k.TimeMs);
            KillTexts.Add((PlayerName.Plain(holder.Name), k.Kind == 1 ? MeleeWeapon(holder.Loadout) : k.Kind == 2 ? _ui.Text("feed.explosion") : k.Kind == HitMatch.SentryKind ? _ui.Text("wp.sentry") : Weapon(holder.Loadout, k.Wielded), EnemyName(k.EnemyDataId)));
        }
        FeedItems.Clear();
        for (var i = 0; i < _kills.Count; i++)
            FeedItems.Add((_kills[i].TimeMs, _kills[i].Killer, KillTexts[i].Killer, KillTexts[i].Weapon, KillTexts[i].Enemy, _kills[i].Exact, false, _kills[i].Entity, Highlights.Before(_kills[i].TimeMs, _kills[i].SpawnMs)));
        foreach (var w in analysis.Wakes)
        {
            var known = w.Waker >= 0 && w.Waker < 4;
            var who = known ? PlayerName.Plain(HolderAt(analysis, w.Waker, w.TimeMs).Name) : _ui.Text("feed.unknown");
            var how = w.Cause switch
            {
                WakeCause.Sensed => w.Action >= 2 && w.Action <= 10 ? _ui.Text("feed.act." + w.Action.ToString(CultureInfo.InvariantCulture)) : _ui.Text("feed.wake.sensed"),
                WakeCause.Noise => _ui.Text("feed.wake.noise"),
                WakeCause.Damage => _ui.Text("feed.wake.damage"),
                WakeCause.Passed => _ui.Text("feed.wake.passed"),
                WakeCause.Door => _ui.Text("feed.wake.door"),
                WakeCause.Explosion => _ui.Text("feed.wake.blast"),
                WakeCause.Sentry => _ui.Text("feed.wake.sentry"),
                _ => "",
            };
            var enemy = EnemyName(w.EnemyDataId) + (w.Chain > 0 ? " " + _ui.Text("feed.wake.chain", w.Chain) : "");
            FeedItems.Add((w.TimeMs, known ? w.Waker : -1, who, how, enemy, true, true, w.Entity, Highlights.Before(w.TimeMs)));
        }
        AddDownFeed(analysis);
        FeedItems.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        EventRows.Clear();
        var worldEvent = 0; var noteAt = 0;
        var lastPing = new uint[4]; var pinged = new bool[4];
        foreach (var m in analysis.Markers)
        {
            var who = m.Slot >= 0 && m.Slot < 4 ? "  " + HolderAt(analysis, m.Slot, m.TimeMs).Name : "";
            var text = _ui.Text("mark." + m.Kind.ToString().ToLowerInvariant()) + who;
            // One kind of mark for every level event with something to read; its type, delay and words are in the hover.
            if (m.Kind == MarkerKind.WorldEvent && worldEvent < analysis.WorldEvents.Count) text += WorldEventDetail(analysis.WorldEvents[worldEvent++].Line);
            // A mod's marked note: its own words (they are the mod's, not translated), in the order the notes were written.
            if (m.Kind == MarkerKind.Note)
            {
                while (noteAt < analysis.Notes.Count && !analysis.Notes[noteAt].Mark) noteAt++;
                if (noteAt < analysis.Notes.Count) { var note = analysis.Notes[noteAt++]; text = note.ModId + "  " + note.Kind + (note.Payload.Length > 0 ? ": " + (note.Payload.Length > 60 ? note.Payload.Substring(0, 60) + "…" : note.Payload) : ""); }
            }
            // A ping said again within three seconds by the same player is one mark.
            var repeat = m.Kind == MarkerKind.Ping && m.Slot is >= 0 and < 4 && pinged[m.Slot] && m.TimeMs - lastPing[m.Slot] < 3000;
            if (m.Kind == MarkerKind.Ping && m.Slot is >= 0 and < 4) { pinged[m.Slot] = true; lastPing[m.Slot] = m.TimeMs; }
            if (repeat) continue;
            if (OnBar(m.Kind)) _bar.Marks.Add(new TimelineMark(m.TimeMs, MarkColour(m.Kind, m.Slot), text, MarkLane(m.Kind), MarkShape(m.Kind)));
            EventRows.Add(new ListRow(m.TimeMs, MarkColour(m.Kind, m.Slot), text, EventRows.Count));
        }
        EventRows.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        _highlights = Highlights.Pick(analysis, Replayer.Boss);
        _idle = Pacing.IdleSpans(analysis.Busy, Replayer.Clock?.DurationMs ?? 0);
        HighlightTexts.Clear();
        foreach (var h in _highlights)
        {
            var who = h.Slot is >= 0 and < 4 ? HolderAt(analysis, h.Slot, h.TimeMs).Name : "";
            HighlightTexts.Add(h.Kind switch
            {
                HighlightKind.MultiKill => _ui.Text("hl.multikill", who, h.Count),
                HighlightKind.Rescue => _ui.Text("hl.rescue", who, h.Count is >= 0 and < 4 ? HolderAt(analysis, h.Count, h.TimeMs).Name : "?"),
                HighlightKind.BossKill => _ui.Text("hl.boss", who, EnemyName(h.EnemyDataId)),
                _ => _ui.Text("hl.wipe"),
            });
        }
        HighlightRows.Clear();
        for (var i = 0; i < _highlights.Count; i++)
        {
            var h = _highlights[i];
            HighlightRows.Add(new ListRow(h.TimeMs, h.Kind == HighlightKind.Wipe ? UIKitColors.Alert : h.Kind == HighlightKind.Rescue ? UIKitColors.Good : h.Slot is >= 0 and < 4 ? SlotColour(h.Slot) : UIKitColors.GameText, HighlightTexts[i], i));
        }
        foreach (var c in analysis.Chat) ChatRows.Add(new ListRow(c.TimeMs, c.Slot is >= 0 and < 4 ? SlotColour(c.Slot) : UIKitColors.GameText, (c.Slot >= 0 ? PlayerName.Plain(HolderAt(analysis, c.Slot, c.TimeMs).Name) : "?") + ": " + c.Text, ChatRows.Count));
        for (var slot = 0; slot < 4; slot++) EverHeld[slot] = ReplayFeed.EverHeld(slot, _header!.Players, analysis.Presence);
        AddBarExtras(analysis);
        BuildPages(analysis);
        BuildRating(analysis);
        _statsClock = new StatsClock(analysis);
        Statistics(_statsClock.Players, Replayer.Clock?.PositionMs ?? 0);
    }

    // "  +3 s  the words": the delay when there is one, then the intel, sub objective and subtitle texts the event holds. The game's internal event type name is not shown.
    private static string WorldEventDetail(WorldEventLine line)
    {
        var words = new List<string>(3);
        foreach (var part in new[] { line.Intel, (line.SubHeader + " " + line.SubText).Trim(), line.Subtitle })
            if (part.Length > 0 && !words.Contains(part)) words.Add(part);
        // A subtitle holds line breaks of its own (\r too); a list row and the bar's tip are one line.
        var said = System.Text.RegularExpressions.Regex.Replace(string.Join(" / ", words), @"\s*[\r\n]+\s*", " ");
        if (said.Length > 160) said = said.Substring(0, 160) + "...";
        return "  " + (line.DelayS > 0f ? _ui.Text("mark.delay", line.DelayS.ToString("0.#", CultureInfo.InvariantCulture)) + "  " : "") + said;
    }

    private static SlotHolder HolderAt(Analysis? analysis, int slot, uint timeMs)
        => ReplayFeed.Holder(slot, timeMs, _header!.Players, analysis?.Presence ?? NoPresence, _ui.Text("player.slot", slot + 1));

    private static readonly List<PresenceLine> NoPresence = new();
    private static Analysis? _rosterOf;

    // The slots' holders at the moment on the bar, made again only when a join or leave is passed (or the analysis arrives).
    private static void Roster(Analysis? analysis, uint now)
    {
        var presence = analysis?.Presence ?? NoPresence;
        var seen = ReplayFeed.PresenceUpTo(presence, now);
        if (seen == _presenceSeen && ReferenceEquals(analysis, _rosterOf)) return;
        _presenceSeen = seen; _rosterOf = analysis;
        for (var slot = 0; slot < 4; slot++)
        {
            var holder = HolderAt(analysis, slot, now);
            if (!ReferenceEquals(holder.Loadout, Loadouts[slot])) CardStatus[slot] = null; // the held weapon's name comes from the loadout
            Names[slot] = PlayerName.Plain(holder.Name);
            NameColours[slot] = PlayerName.Colour(holder.Name) is { } hex && ColorUtility.TryParseHtmlString(hex, out var own) ? Readable(own) : null;
            Loadouts[slot] = holder.Loadout; InRecording[slot] = holder.Filled; Known[slot] = holder.Known;
        }
    }

    private static readonly bool[] Known = new bool[4];

    // The team's marks (kills, downs, revives, chat) in the top lane, the level's in the bottom one; each kind its own shape.
    private static int MarkLane(MarkerKind kind) => kind is MarkerKind.Kill or MarkerKind.Down or MarkerKind.Revive or MarkerKind.Chat or MarkerKind.Ping ? 0 : 1;

    private static TimelineShape MarkShape(MarkerKind kind) => kind switch
    {
        MarkerKind.Kill => TimelineShape.Triangle,
        MarkerKind.Down => TimelineShape.Cross,
        MarkerKind.Revive => TimelineShape.Plus,
        MarkerKind.Alarm or MarkerKind.Checkpoint => TimelineShape.Diamond,
        MarkerKind.Terminal or MarkerKind.Scan => TimelineShape.Square,
        _ => TimelineShape.Tick,
    };

    // Under the bar, each objective layer from the moment it starts until it is done: main in the HUD text colour, secondary in the warning
    // colour, overload in the infection colour; what is still running at the end runs to the end.
    private static void Spans(Analysis analysis, List<TimelineSpan> spans)
    {
        var from = new long[] { -1, -1, -1 };
        Color Colour(int layer) => layer == 0 ? Fade(UIKitColors.GameText, 0.8f) : layer == 1 ? Fade(UIKitColors.Warn, 0.8f) : Fade(UIKitColors.Infection, 0.8f);
        foreach (var o in analysis.Objectives)
        {
            if (o.Layer is < 0 or > 2) continue;
            var running = o.Status is > 0 and < 40;
            if (running && from[o.Layer] < 0) from[o.Layer] = o.TimeMs;
            else if (!running && from[o.Layer] >= 0) { spans.Add(new TimelineSpan((uint)from[o.Layer], o.TimeMs, Colour(o.Layer))); from[o.Layer] = -1; }
        }
        var end = Replayer.Clock?.DurationMs ?? 0;
        for (var layer = 0; layer < 3; layer++) if (from[layer] >= 0 && end > from[layer]) spans.Add(new TimelineSpan((uint)from[layer], end, Colour(layer)));
    }

    private static Color MarkColour(MarkerKind kind, int slot) => kind switch
    {
        MarkerKind.Kill => slot >= 0 && slot < 4 ? SlotColour(slot) : UIKitColors.GameText,
        MarkerKind.Down => UIKitColors.Downed,
        MarkerKind.Revive => UIKitColors.Good,
        MarkerKind.Alarm or MarkerKind.Wave => UIKitColors.Alert,
        MarkerKind.Ping => UIKitColors.Warn,
        MarkerKind.Chat => UIKitColors.MenuOver,
        MarkerKind.Note => UIKitColors.Good,
        _ => UIKitColors.Faded(UIKitColors.GameText, 0.8f),
    };

    // The numbers up to the moment on the bar: they follow the clock, forward and back.
    private static StatsClock? _statsClock;
    private static bool _statsDirty;
    private static float _statsNext;

    // The table's cells for the numbers up to the moment on the bar (made when a number changes, not every frame), and each player's
    // weapon and enemy kind with the most kills so far.
    private static void Statistics(PlayerStats[] players, uint now)
    {
        var analysis = _marked;
        if (analysis == null) return;
        _stats = new StatCell[ReplayFeed.Rows.Length, 4];
        var fullHealth = FullHealth();
        var guessedKills = false; var guessedRest = false;
        for (var r = 0; r < ReplayFeed.Rows.Length; r++)
            for (var slot = 0; slot < 4; slot++)
            {
                var cell = ReplayFeed.Cell(ReplayFeed.Rows[r], players[slot], analysis, slot, fullHealth);
                if (cell.Quality == StatQuality.Estimate && Array.IndexOf(ReplayFeed.InferredRows, ReplayFeed.Rows[r]) >= 0) guessedRest = true;
                else if (cell.Quality == StatQuality.Estimate) guessedKills = true;
                _stats[r, slot] = cell.Quality == StatQuality.Estimate ? new StatCell(_ui.Text("estimate", cell.Text), cell.Quality) : cell;
            }
        HowTexts.Clear();
        var why = ReplayList.EstimateWhy(guessedKills, guessedRest);
        if (why.Length > 0) HowTexts.AddRange(why.Split('\n'));
        var kills = Array.IndexOf(ReplayFeed.Rows, StatRow.Kills); var downs = Array.IndexOf(ReplayFeed.Rows, StatRow.Downs);
        for (var slot = 0; slot < 4; slot++) { CardCounts[slot] = _ui.Text("card.counts", _stats[kills, slot].Text, _stats[downs, slot].Text); CardEstimate[slot] = _stats[kills, slot].Quality == StatQuality.Estimate; }
        for (var slot = 0; slot < 4; slot++)
        {
            var weapons = new Dictionary<string, int>(); var enemies = new Dictionary<string, int>();
            for (var i = 0; i < _kills.Count && i < KillTexts.Count; i++)
            {
                var k = _kills[i];
                if (k.Killer != slot || k.TimeMs > now) continue;
                var (_, weapon, enemy) = KillTexts[i];
                if (weapon.Length > 0) weapons[weapon] = weapons.TryGetValue(weapon, out var w) ? w + 1 : 1;
                enemies[enemy] = enemies.TryGetValue(enemy, out var e) ? e + 1 : 1;
            }
            TopWeapon[slot] = Top(weapons); TopEnemy[slot] = Top(enemies);
        }
        BuildTeamTable();
    }

    // Each card's kills and downs up to the moment on the bar (the kills in the warning colour when estimated).
    private static readonly string[] CardCounts = { "", "", "", "" };
    private static readonly bool[] CardEstimate = new bool[4];

    private static string Top(Dictionary<string, int> counts)
    {
        string best = ""; var most = 0;
        foreach (var (name, n) in counts) if (n > most || n == most && string.CompareOrdinal(name, best) < 0) { best = name; most = n; }
        return most == 0 ? "" : best + " · " + most.ToString(CultureInfo.InvariantCulture);
    }

    private static Color Plate(float alpha = 1f) => UIKitColors.Faded(new Color(ReplayPlate.Back.r, ReplayPlate.Back.g, ReplayPlate.Back.b, 1f), Math.Min(1f, PlateAlpha * alpha));
    private static Color Fade(Color colour, float alpha) => UIKitColors.Faded(colour, alpha);

    // A colour a player gave their name can be too dark for a dark plate: lifted towards white just far enough to read.
    private static Color Readable(Color c)
    {
        const float least = 0.45f;
        var light = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
        return light >= least ? c : Color.Lerp(c, Color.white, (least - light) / (1f - light));
    }

    // Esc held: how far it is to leaving the replay, in the middle (letting go before it fills cancels).
    private static void Leaving(HudCanvas c, float x, float y)
    {
        if (ForgeCore.Frame.UnscaledTime - _closeFrom < TapSeconds) return;
        var share = Math.Clamp((ForgeCore.Frame.UnscaledTime - _closeFrom - TapSeconds) / (ExitSeconds - TapSeconds), 0f, 1f);
        const float width = 360f;
        c.Plate(x, y, width, 64f, Cut, Plate(1.2f));
        c.Text(_ui.Text("exit.hold", ReplayKeys.Name(ReplayKeys.Close)), x, y + 12f, 16f, UIKitColors.MenuOver, HudAlign.Centre, bold: true);
        c.Rect(x, y - 14f, width - 40f, 6f, Fade(UIKitColors.GameText, 0.15f));
        if (share > 0f) c.Rect(x - (width - 40f) / 2f + (width - 40f) * share / 2f, y - 14f, (width - 40f) * share, 6f, UIKitColors.Alert);
    }

    private const float PlateWidth = 700f, PlateHeight = 76f, NameRoom = 160f, CountRoom = 84f;
    private static float _hintsWidth;
    private static readonly string[] HintNames = { "", "", "" };
    private static bool _hintUi, _hintSet;
    private static string _foeText = "", _foeClose = "";
    private static ulong _foeData = ulong.MaxValue;

    /// <summary>Bottom middle: who is watched (the name in the slot's colour, under it what is in the hands, its ammunition and the health),
    /// the views as buttons (the one shown lit; the views behind or at a player unavailable while nobody can be watched), the automatic
    /// camera's switch, and the keys for what is done most, by their current names. The plate is as wide as its wider line needs (key names
    /// and words differ by language and binding; the key line is measured as last drawn), at least <see cref="PlateWidth"/> and at most
    /// <paramref name="room"/>, the space between the tabs and the buttons beside it.</summary>
    private static void Watched(HudCanvas c, WorldState state, float cx, float cy, float room)
    {
        var follow = Replayer.Follow;
        var player = follow is >= 0 and <= 3;
        var views = c.ButtonWidth(_ui.Text("cam.auto")) + c.ButtonWidth(_ui.Text("cam.free")) + c.ButtonWidth(_ui.Text("cam.eyes"))
            + c.ButtonWidth(_ui.Text("cam.chase")) + c.ButtonWidth(_ui.Text("cam.orbit")) + 5f * 10f + 8f;
        var mode = ReplayCamera.Mode;
        var noAim = player && mode == ViewMode.Eye && !ReplayExtras.Aim(state, follow, out _) && !ReplayExtras.Gun(state, follow, out _);
        if (noAim && !NoAimTold[follow]) { NoAimTold[follow] = true; Notices.Push(_ui.Text("toast.noaim", Names[follow]), NoticeTone.Warn, 6f, ForgeCore.Frame.UnscaledTime); }
        var tag = noAim ? _ui.Text("plate.noaim") : "";
        // Watching an enemy: its name in the alert colour, how to go back to the player, and the health line that shares the second line.
        // The health is measured before the plate so its column can be as wide as the value (the player's "Health 80%" is short and keeps
        // its fixed column; an enemy's "Health about 1129 / 2500" does not fit there and ran out of the plate's left edge).
        var enemy = ReplayCamera.Enemy;
        string? foe = null;
        var foeHealth = "";
        var foeEstimated = false;
        if (enemy >= 0 && state.Enemies.TryGetValue(enemy, out var foeData))
        {
            var close = ReplayKeys.Name(ReplayKeys.Close);
            if (foeData.DataId != _foeData || close != _foeClose) { _foeData = foeData.DataId; _foeClose = close; _foeText = _ui.Text("plate.enemy", EnemyName(foeData.DataId), close); }
            foe = _foeText;
            (foeHealth, foeEstimated) = EnemyHealthText(state, enemy);
        }
        var foeWidth = foeHealth.Length > 0 ? c.TextWidth(foeHealth, 16f, true) : 0f;
        // The plate's width is set by the view buttons alone: what the second line holds (the hands, the health, the keys) changes with play and must not move the plate or the buttons on it.
        var width = Math.Min(Math.Max(Math.Max(PlateWidth, views + NameRoom + 32f), foeWidth + 64f), room);
        var healthRoom = Math.Max(130f, foeWidth + 8f);
        var left = cx - width / 2f; var right = cx + width / 2f;
        c.Plate(cx, cy, width, PlateHeight, Cut, Plate());
        var top = cy + 17f; var under = cy - 20f;
        // The views, from the right: the automatic switch, then free, eyes, chase, orbit.
        var x = right - 10f;
        if (c.Button(ref x, top, _ui.Text("cam.auto"), _auto, UIKitColors.Alert)) ToggleAuto();
        x -= 8f;
        if (c.Button(ref x, top, _ui.Text("cam.free"), mode == ViewMode.Free, Lit)) ReplayCamera.SetMode(ViewMode.Free);
        if (c.Button(ref x, top, _ui.Text("cam.eyes"), mode == ViewMode.Eye, Lit, player)) ReplayCamera.SetMode(ViewMode.Eye);
        if (c.Button(ref x, top, _ui.Text("cam.chase"), mode == ViewMode.Follow, Lit, player)) ReplayCamera.SetMode(ViewMode.Follow);
        if (c.Button(ref x, top, _ui.Text("cam.orbit"), mode == ViewMode.Orbit, Lit, player)) ReplayCamera.SetMode(ViewMode.Orbit);
        // The name has the whole first line up to the view buttons.
        var name = c.FitText(foe ?? (player ? Names[follow] : _ui.Text("cam.nobody")), 20f, true, x - left - 24f);
        c.Text(name, left + 16f, top, 20f, foe != null ? UIKitColors.Alert : player ? NameColour(follow, SlotColour(follow)) : UIKitColors.MenuOver, HudAlign.Left, bold: true);
        // The keys on the right of the second line (left out when the plate is too narrow for them); left of them why first person shows no
        // aim (in words, in the warning colour), then what the hands hold, each cut with "…" to the room left before the keys.
        var hintsRoom = width - 32f - healthRoom - 48f;
        Hints(right - 16f, under, hintsRoom);
        // The columns stand where the widest key line seen so far would leave them, so the key line changing with Ctrl (or a rebinding) does not move the health or the hands.
        var hintsW = Math.Min(_hintsWidth, hintsRoom);
        // The second line is in columns of fixed places: what is held on the left, the health right-aligned in a column of its own, the keys at the right end.
        var keysLeft = right - 16f - (hintsW > 0f ? hintsW + 16f : 0f);
        var healthRight = keysLeft - 8f;
        if (foe != null)
        {
            if (foeHealth.Length > 0)
            {
                // The column reserved above leaves the value's whole width inside the plate; the shrink is only for a plate forced narrower
                // than the recording's text (the room between the tabs and the buttons), so no digit is ever cut.
                var px = 16f;
                var healthWidth = Math.Max(1f, healthRight - (left + 16f));
                while (px > 11f && c.TextWidth(foeHealth, px, true) > healthWidth) px -= 1f;
                c.Text(foeHealth, healthRight, under, px, foeEstimated ? UIKitColors.Warn : UIKitColors.GameText, HudAlign.Right, bold: true);
            }
            return;
        }
        if (!player) return;
        var held = left + 16f;
        var healthLeft = healthRight - healthRoom;
        if (noAim && healthLeft > held)
        {
            var shown = c.FitText(tag, 16f, true, healthLeft - held);
            if (shown.Length > 0) held += c.Text(shown, held, under, 16f, UIKitColors.Warn, HudAlign.Left, bold: true) + 16f;
        }
        // What is held: the name on the left, the count (ammunition or uses; empty for a hammer or a knife, the column stays) right-aligned in a column of its own before the health.
        var (heldName, heldCount) = HeldText(follow);
        var countRight = healthLeft - 12f;
        if (countRight - CountRoom > held) c.Text(c.FitText(heldName, 16f, false, countRight - CountRoom - held - 12f), held, under, 16f, UIKitColors.GameText);
        if (heldCount.Length > 0 && countRight - CountRoom > held) c.Text(c.FitText(heldCount, 16f, false, CountRoom), countRight, under, 16f, UIKitColors.GameText, HudAlign.Right);
        var health = CardHealth[follow] < 0 ? 0 : CardHealth[follow] * 100 / 127;
        c.Text(_ui.Text("plate.health", health), healthRight, under, 16f, UIKitColors.GameText, HudAlign.Right, bold: true);
    }

    // The watched enemy's health: the full health and what is left. What is left is exact where the host synced its losses (the recording holds the health after each), else
    // read from the sampled share of full health (31 steps), which is only about. Nothing when the enemy's full health is not known.
    private static (string Text, bool Estimated) EnemyHealthText(WorldState state, int enemy)
    {
        if (!state.Enemies.TryGetValue(enemy, out var data) || !(data.FullHealth > 0f)) return ("", false);
        var now = Replayer.Clock?.PositionMs ?? 0u;
        if (_marked?.HealthTrack.At(enemy, now, out var exact) == true)
            return (_ui.Text("plate.foehealth", Whole(Math.Max(0f, exact)), Whole(data.FullHealth)), false);
        var share = (state.Poses[enemy].State >> 2) / 31f;
        return (_ui.Text("plate.foehealthabout", Whole(share * data.FullHealth), Whole(data.FullHealth)), true);
    }

    // What the watched player holds and has left, made again only when it changes.
    private static (string Name, string Count) HeldText(int slot)
    {
        foreach (var row in CardRows[slot])
            if (row.Slot == CardHeld[slot]) return (row.Name, row.Count);
        return ("", "");
    }

    // The hint line: watching, the mouse buttons and the numbers; using the screen, how to go back. Set again only when it or a key changes.
    // Wider than max, it is not shown (returns 0; its width is still kept in _hintsWidth for the plate's next layout).
    private static float Hints(float right, float y, float max)
    {
        var hints = _hints;
        if (hints == null) return 0f;
        var ui = ReplayCamera.Ui;
        var a = ui ? "Ctrl" : ReplayKeys.Name(ReplayKeys.CameraNext);
        var b = ui ? ReplayKeys.Name(ReplayKeys.Close) : ReplayKeys.Name(ReplayKeys.PlayerNext);
        var n = ReplayKeys.Name(ReplayKeys.Watch[0]) + "–" + ReplayKeys.Name(ReplayKeys.Watch[3]);
        if (!_hintSet || ui != _hintUi || a != HintNames[0] || b != HintNames[1] || n != HintNames[2])
        {
            _hintSet = true; _hintUi = ui; HintNames[0] = a; HintNames[1] = b; HintNames[2] = n;
            if (ui) hints.Set(HintPart.Key(a), HintPart.Word(_ui.Text("hint.back")), HintPart.Key(b, true), HintPart.Word(_ui.Text("hint.close")));
            else hints.Set(HintPart.Key(a), HintPart.Word(_ui.Text("hint.camera")), HintPart.Key(b, true), HintPart.Word(_ui.Text("hint.player")),
                HintPart.Key(n, true), HintPart.Word(_ui.Text("hint.watch")), HintPart.Key("Ctrl", true), HintPart.Word(_ui.Text("hint.mouse")));
        }
        var width = hints.Place(right, y);
        _hintsWidth = Math.Max(_hintsWidth, width);
        // The plate is sized to the line's width, so max equals it up to rounding: a bare comparison could flip between frames.
        if (width <= max + 0.5f) return width;
        hints.Hide();
        return 0f;
    }

    // Bottom left: the tabs of the panels above them (one open at a time; the open one's tab closes it). Returns their right end.
    private static float Tabs(HudCanvas c, float left, float y)
    {
        var x = left;
        TabButton(c, ref x, y, "tab.events", _showEvents, Tab.Events);
        TabButton(c, ref x, y, "tab.highlights", _showHighlights, Tab.Highlights);
        TabButton(c, ref x, y, "tab.chat", _showLog, Tab.Chat);
        TabButton(c, ref x, y, "tab.stats", _showStats, Tab.Stats);
        return x - 10f;
    }

    private static void TabButton(HudCanvas c, ref float x, float y, string key, bool on, Tab tab)
    {
        var text = _ui.Text(key);
        var width = c.ButtonWidth(text);
        var edge = x + width;
        if (c.Button(ref edge, y, text, on, Lit)) Panel(tab);
        x += width + 10f;
    }

    private static readonly string?[] Percents = new string?[101];

    // "73%", kept: the cards ask every frame.
    private static string PercentText(float share)
    {
        var whole = Math.Clamp((int)MathF.Round(share * 100f), 0, 100);
        return Percents[whole] ??= whole.ToString(CultureInfo.InvariantCulture) + "%";
    }

    // A gear's row: what is left of a full pack as a percent, the full pack by the game's data for this gear or, where that says nothing or too
    // little (ammunition boosters are not recorded), the most this recording shows. Nothing known yet: no number rather than a wrong one.
    private static void AmmoRow(List<CardRow> rows, string loadout, int slot, int line, int ammo, ulong now, InventorySlot item)
    {
        var name = GearTypeNames.Name(loadout, line);
        if (name.Length == 0) return;
        var lines = loadout.Split('\n');
        var peak = Replayer.Report?.AmmoPeak[slot, ammo] ?? 0UL;
        var full = Math.Max(peak, AmmoCaps.Bullets(line < lines.Length ? lines[line] : "", line));
        if (full == 0UL) { rows.Add(new CardRow(name, "", item)); return; }
        var share = Math.Min(1f, now / (float)full);
        rows.Add(new CardRow(name, PercentText(share), item, share));
    }

    /// <summary>Rebuilds a card's texts when the recorded status or kit changed: PlayerStatus is U slot, locomotion, wielded slot,
    /// infection percent, standard, special and class ammunition, down.</summary>
    private static void Refresh(int slot, byte[]? status, int health, PlayerKit kit)
    {
        CardHealth[slot] = health;
        var report = Replayer.Report;
        if (ReferenceEquals(status, CardStatus[slot]) && kit == CardKit[slot] && ReferenceEquals(report, CardReport[slot])) return;
        CardStatus[slot] = status; CardKit[slot] = kit; CardReport[slot] = report;
        var rows = CardRows[slot];
        rows.Clear();
        if (status == null) { CardDown[slot] = false; CardInfection[slot] = 0f; CardMove[slot] = 0; CardHeld[slot] = InventorySlot.None; return; }
        var read = new PayloadReader(status);
        read.U();
        CardMove[slot] = (int)read.U();
        CardHeld[slot] = (InventorySlot)read.U();
        CardInfection[slot] = Math.Clamp(read.U() / 100f, 0f, 1f);
        var standard = read.U(); var special = read.U(); var tool = read.U();
        CardDown[slot] = read.U() == 1;
        var loadout = Loadouts[slot];
        void Add(string name, string count, InventorySlot item) { if (name.Length > 0) rows.Add(new CardRow(name, count, item)); }
        AmmoRow(rows, loadout, slot, 0, 0, standard, InventorySlot.GearStandard);
        AmmoRow(rows, loadout, slot, 1, 1, special, InventorySlot.GearSpecial);
        Add(GearTypeNames.Name(loadout, 2), "", InventorySlot.GearMelee);
        AmmoRow(rows, loadout, slot, 3, 2, tool, InventorySlot.GearClass);
        if (!kit.Known) return;
        if (kit.PackItem != 0) Add(ItemNames.Name(kit.PackItem), kit.PackCount.ToString(CultureInfo.InvariantCulture), InventorySlot.ResourcePack);
        if (kit.ConsumableItem != 0) Add(ItemNames.Name(kit.ConsumableItem), kit.ConsumableCount.ToString(CultureInfo.InvariantCulture), InventorySlot.Consumable);
        if (kit.CarryItem != 0) Add(ItemNames.Name(kit.CarryItem), "", InventorySlot.InLevelCarry);
    }

    // The killing blow of a swing names the melee weapon the loadout carries.
    private static string MeleeWeapon(string loadout) => Weapon(loadout, (int)InventorySlot.GearMelee) is { Length: > 0 } name ? name : _ui.Text("feed.melee");

    private static string Weapon(string loadout, int wielded)
    {
        var line = (InventorySlot)wielded switch
        {
            InventorySlot.GearStandard => 0,
            InventorySlot.GearSpecial => 1,
            InventorySlot.GearMelee => 2,
            InventorySlot.GearClass => 3,
            _ => -1,
        };
        return line < 0 ? "" : GearTypeNames.Name(loadout, line);
    }

    // What players call the enemy (EnemyNames); a custom rundown's enemy by its data block name.
    private static string EnemyName(ulong dataId)
    {
        string? name;
        try { name = GameData.EnemyDataBlock.GetBlock((uint)dataId)?.name; }
        catch (Exception) { name = null; }
        if (name == null) return _ui.Text("feed.enemy");
        var key = EnemyNames.Key(name);
        return key != null ? _ui.Text(key) : name.Replace('_', ' ');
    }

    // A click on a highlight: five seconds before it, watching its player (or the boss it killed).
    private static void PickHighlight(Highlight h)
    {
        Replayer.Jump(h.JumpMs);
        if (h.Kind == HighlightKind.BossKill && h.Entity >= 4) { ReplayCamera.WatchEnemy(h.Entity); ManualChoice(); }
        else WatchPlayer(h.Slot);
    }

    /// <summary>The highlights panel opened, as its tab opens it; with <paramref name="number"/> (1 up) also that highlight picked, as a click on its row
    /// does. One line: the count, or the highlight's kind, time and jump target.</summary>
    internal static string OpenHighlights(int number)
    {
        if (!_on || Replayer.Report == null) return "no replay";
        _showEvents = _showLog = _showStats = false; _showHighlights = true; _scroll = 0;
        if (number < 1) return "highlights open count=" + _highlights.Count;
        if (number > _highlights.Count) return "highlights open count=" + _highlights.Count + " no highlight " + number;
        var h = _highlights[number - 1];
        PickHighlight(h);
        return "highlight " + number + "/" + _highlights.Count + " kind=" + h.Kind + " at=" + h.TimeMs + "ms jump=" + h.JumpMs + "ms slot=" + h.Slot + " entity=" + h.Entity;
    }

    /// <summary>Damage numbers over the picture: on for a player; only a scripted run switches them off (to see the screen without them).</summary>
    internal static bool NumbersOn = true;

    /// <summary>How many damage numbers have been made in the replay playing now.</summary>
    internal static int PopsMade;

    internal static string NumbersState() => "damage numbers " + (NumbersOn ? "on" : "off") + " shown=" + Pops.Count + " made=" + PopsMade;

    private const uint PingMs = 6000, GameLineMs = 6000;
    private const float TagHeight = 2.0f;

    // Over the picture: the name of everyone in the game at this moment (the game's own tags are hidden with its HUD, and nobody who has left gets one), and the
    // places pinged in the last seconds. Off with the outlines. Screen places come from the camera's view, whatever the HUD's size.
    private static void WorldMarks(HudCanvas c, WorldState state, Analysis analysis, uint now, HudBox screen, float floor)
    {
        floor += 11f; // half a name plate or ping
        var camera = Players.WorldCamera();
        if (camera == null || !_outlines) return;
        var tags = 0;
        for (var slot = 0; slot < 4; slot++)
        {
            if (!InRecording[slot] || !state.Poses[slot].Present) continue;
            if (slot == Replayer.Follow && (ReplayCamera.Mode == ViewMode.Eye || FirstPerson.Active)) continue;
            if (!state.Place(slot, now, out var x, out var y, out var z, out _, out _, out _, out _)) continue;
            if (!OnScreen(camera, new Vector3(x, y + TagHeight, z), screen, out var sx, out var sy) || sy < floor) continue;
            var width = c.TextWidth(Names[slot], 16f, true) + 16f;
            sy = TagFree(sx, sy, width, tags++);
            c.Plate(sx, sy, width, 26f, 3f, Plate(0.8f));
            c.Text(Names[slot], sx, sy, 16f, NameColour(slot, SlotColour(slot)), HudAlign.Centre, bold: true);
        }
        var pings = analysis.Pings;
        for (var i = pings.Count - 1; i >= 0; i--)
        {
            var ping = pings[i];
            if (ping.TimeMs > now) continue;
            if (now - ping.TimeMs > PingMs) break;
            if (!OnScreen(camera, new Vector3(ping.X, ping.Y, ping.Z), screen, out var sx, out var sy) || sy < floor) continue;
            var colour = Fade(ping.Slot >= 0 ? SlotColour(ping.Slot) : UIKitColors.GameText, 1f - (now - ping.TimeMs) / (float)PingMs);
            c.Frame(sx, sy, 22f, 22f, 7f, colour);
            c.Rect(sx, sy, 5f, 5f, colour);
        }
    }


    private static readonly (float X, float Y, float Width)[] TagsPlaced = new (float, float, float)[4];

    // Where a name tag goes so it does not cover those placed before it this frame: above them when it would.
    private static float TagFree(float x, float y, float width, int placed)
    {
        const float row = 28f;
        for (var moved = true; moved;)
        {
            moved = false;
            for (var i = 0; i < placed; i++)
            {
                var other = TagsPlaced[i];
                if (Math.Abs(other.X - x) < (other.Width + width) / 2f && Math.Abs(other.Y - y) < row) { y = other.Y + row; moved = true; }
            }
        }
        TagsPlaced[placed] = (x, y, width);
        return y;
    }

    private const float PickReach = 48f, PopSeconds = 1f, PopRise = 0.6f;
    private const int PopsMost = 48;
    private readonly record struct Pop(Vector3 At, string Text, Color Colour, float Born);
    private static readonly List<Pop> Pops = new();

    /// <summary>A hit's number over the limb it struck (PLAN 2.2): the health it took off, in the warning colour on a weak spot and the
    /// alert colour in the back; drawn by the screen's own canvas over the picture, rising and fading in a second.</summary>
    internal static void DamagePop(Vector3 at, float amount, bool weak, bool behind)
    {
        if (!_on || !NumbersOn || amount <= 0f) return;
        PopsMade++;
        if (Pops.Count >= PopsMost) Pops.RemoveAt(0);
        Pops.Add(new Pop(at, amount.ToString(amount < 10f ? "0.#" : "0", CultureInfo.InvariantCulture), weak ? UIKitColors.Warn : behind ? UIKitColors.Alert : UIKitColors.MenuOver, ForgeCore.Frame.UnscaledTime));
    }

    private static void DamageNumbers(HudCanvas c, HudBox screen, float floor)
    {
        if (Pops.Count == 0) return;
        var camera = Players.WorldCamera();
        var now = ForgeCore.Frame.UnscaledTime;
        Pops.RemoveAll(p => now - p.Born > PopSeconds);
        if (camera == null) return;
        foreach (var p in Pops)
        {
            var age = (now - p.Born) / PopSeconds;
            if (!OnScreen(camera, p.At + Vec.Up * (PopRise * age), screen, out var sx, out var sy) || sy < floor) continue;
            c.Text(p.Text, sx, sy, 18f, Fade(p.Colour, age > 0.7f ? (1f - age) / 0.3f : 1f), HudAlign.Centre, bold: true);
        }
    }

    /// <summary>The puppet enemy whose body is nearest the pointer on the picture (within a short reach), for a click that watches it; -1 when none.</summary>
    internal static int EnemyUnderPointer()
    {
        var hud = _hud;
        var camera = Players.WorldCamera();
        if (hud == null || camera == null || !HudPointer.Position(hud, out var px, out var py) || !HudPointer.ScreenBounds(hud, out var l, out var b, out var r, out var t)) return -1;
        var screen = new HudBox(l, b, r, t);
        var best = -1; var bestSquared = PickReach * PickReach;
        foreach (var (entity, agent) in Puppeteer.Visible())
        {
            if (agent == null || agent.WasCollected || !OnScreen(camera, agent.transform.position + Vec.Up, screen, out var sx, out var sy)) continue;
            var squared = (sx - px) * (sx - px) + (sy - py) * (sy - py);
            if (squared < bestSquared) { bestSquared = squared; best = entity; }
        }
        return best;
    }

    private static bool OnScreen(Camera camera, Vector3 world, HudBox screen, out float x, out float y)
    {
        var view = camera.WorldToViewportPoint(world);
        x = screen.Left + view.x * (screen.Right - screen.Left);
        y = screen.Bottom + view.y * (screen.Top - screen.Bottom);
        return view.z > 0f && view.x is >= 0f and <= 1f && view.y is >= 0f and <= 1f;
    }

    // The level's objectives (the recorded layer states): the layer and its state, and their own text from the level's data block. Layers: 0 main, 1 secondary, 2 overload.
    private static readonly (string Head, string Body)[] ObjectiveTexts = new (string, string)[3];
    private static readonly (ulong Status, ulong Extra)[] ObjectiveSeen = new (ulong, ulong)[3];
    private static int _timerSeconds = -1;
    private static string _timerTitle = "", _timerText = "", _reactorText = "";
    private static byte[]? _reactorPayload;

    // What the followed player reads at a terminal: the lines it printed, as of this moment, whatever way the bar got here. What it printed before the level was entered is not in the recording.
    private const int TerminalRows = 12;
    private const float TerminalMax = 860f, TerminalMin = 480f, TerminalRow = 26f;
    private static readonly List<string> TerminalShown = new();

    // The panel opens whenever the followed player stands at a terminal, from the moment they step up to it: the terminal is the one the recording
    // names them at (TerminalState), not whichever terminal printed last (a teammate's, or an old one: 11-06), with its title saying whose screen
    // it is; one that has printed nothing since the level began says so. A recording without the names falls back to the terminal that printed last.
    private static int _terminalTitleSlot = -1;
    private static string _terminalTitleName = "", _terminalTitle = "", _typedRaw = "", _typedLine = "> ";

    private static void TerminalScreen(HudCanvas c, Analysis analysis, uint now, float centre, float middle, float room)
    {
        var follow = Replayer.Follow;
        if (room < TerminalMin || follow is < 0 or > 3 || CardMove[follow] != (int)PlayerLocomotion.PLOC_State.OnTerminal) return;
        var wide = Math.Min(room, TerminalMax);
        var texts = analysis.TerminalTexts;
        var last = texts.Count - 1;
        while (last >= 0 && texts[last].TimeMs > now) last--;
        if (!analysis.TerminalOf(follow, now, out var terminal))
        {
            if (last < 0) return;
            terminal = texts[last].Terminal;
        }
        // What is being typed: the terminal's latest entry when that is typing (a printed line after it means it was sent).
        string? typed = null;
        TerminalShown.Clear();
        for (var i = last; i >= 0 && TerminalShown.Count < TerminalRows; i--)
        {
            if (texts[i].Terminal != terminal) continue;
            if (!texts[i].Typed) { TerminalShown.Add(texts[i].Text); typed ??= ""; }
            else typed ??= texts[i].Text;
        }
        if (follow != _terminalTitleSlot || !ReferenceEquals(Names[follow], _terminalTitleName))
        {
            _terminalTitleSlot = follow; _terminalTitleName = Names[follow];
            _terminalTitle = _ui.Text("terminal.title", Names[follow]);
        }
        var empty = TerminalShown.Count == 0;
        var rows = 2 + (empty ? 1 : TerminalShown.Count);
        var height = rows * TerminalRow + 24f;
        Chrome(c, new HudBox(centre - wide / 2f, middle - height / 2f, centre + wide / 2f, middle + height / 2f));
        var y = middle + height / 2f - 12f - TerminalRow / 2f;
        var x = centre - wide / 2f + 16f;
        c.Text(c.FitText(_terminalTitle, 16f, true, wide - 32f), x, y, 16f, NameColour(follow, SlotColour(follow)), HudAlign.Left, bold: true);
        y -= TerminalRow;
        if (empty) { c.Text(c.FitText(_ui.Text("terminal.empty"), 16f, false, wide - 32f), x, y, 16f, Fade(UIKitColors.GameText, 0.7f)); y -= TerminalRow; }
        for (var row = TerminalShown.Count - 1; row >= 0; row--)
        {
            c.Text(c.FitText(TerminalShown[row], 16f, false, wide - 32f), x, y, 16f, UIKitColors.GameText);
            y -= TerminalRow;
        }
        if (!ReferenceEquals(typed ?? "", _typedRaw)) { _typedRaw = typed ?? ""; _typedLine = "> " + _typedRaw; }
        c.Text(c.FitText(_typedLine, 16f, true, wide - 32f), x, y, 16f, UIKitColors.MenuOver, HudAlign.Left, bold: true);
    }

    // Which zone a player is in, as the door signs call it (recorded every half second).
    private static readonly Dictionary<int, string> ZoneTexts = new();
    private static string ZoneText(WorldState state, int slot)
    {
        if (!ReplayExtras.Place(state, slot, out var place) || place.Zone < 0) return "";
        if (!ZoneTexts.TryGetValue(place.Zone, out var text)) ZoneTexts[place.Zone] = text = _ui.Text("player.zone", place.Zone);
        return text;
    }

    private static (string Head, string Body) ObjectiveLine(int layer, ulong status, ulong extra)
    {
        var name = _ui.Text("objective.layer" + layer);
        var what = _ui.Text(status >= 40 ? "objective.done" : status >= 30 ? "objective.partly" : "objective.active");
        var text = "";
        try
        {
            var lg = (LevelGeneration.LG_LayerType)layer; var chain = (int)(extra & 255);
            if (WardenObjectiveManager.TryGetWardenObjectiveDataForLayer(lg, chain, out var data) && data?.MainObjective is { } objective)
            {
                // The words carry fragments such as [ITEM_SERIAL] that the game fills in with the level's own item names and zones: ask the game's own replacement, as its objective screen does.
                var raw = objective.Id != 0 ? Localization.Text.Get(objective.Id) : objective.UntranslatedText;
                text = Analysis.Untagged(WardenObjectiveManager.ReplaceFragmentsInString(lg, chain, raw ?? "", false) ?? "");
            }
        }
        catch (Exception error) { Skipped.Note("the text of a recorded objective could not be read", error.Message); }
        return (name + "  ·  " + what, text);
    }

    private static void SetOutlines(bool on)
    {
        _outlines = on;
        ReplayAssist.Allies = on;
    }

    /// <summary>Leaves the replay: the level is left as any expedition is, and the list opens again once the menu is back.</summary>
    internal static void Exit()
    {
        Replayer.Clock?.Pause();
        try { SNet.SessionHub.LeaveHub(); }
        catch (Exception error) { Plugin.Logger.Error("Leaving the replay failed: " + error.Message); }
    }
}
