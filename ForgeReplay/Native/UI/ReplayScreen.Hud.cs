using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeCore;
using ForgeUI;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>What the replay's screen draws, zone by zone (<see cref="ReplayZones"/>). Three canvases, so the plate of what is on top hides what is
/// under it: world marks under everything, the panels, and over them what a button opens (the keys, the view menu, the scoreboard, tooltips).
/// Sizes are HUD units (about 2560 x 1440 at 16:9); text sizes are 1080p pixels.</summary>
internal static partial class ReplayScreen
{
    private const float CardWide = 460f, CardHead = 90f, CardFoot = 6f, CardEmpty = 40f, CardGap = 6f, CardRowMin = 20f, CardRowMax = 30f;
    private const float FeedRow = 38f, ControlHeight = 76f, Row = 34f, Cut = 10f, PlateAlpha = 1f;
    private const float PanelTitle = 50f, PanelFoot = 12f, ListLine = 28f, ListMaxWidth = 900f, ListMaxHeight = 800f, ListMinWidth = 460f, PopupMax = 860f;
    private const float StatsLegend = 36f, Info = 26f;
    private const float NameLine = 26f;
    private const int FeedLines = 4;
    private const uint FeedMs = 8000, ChatMs = 8000;
    private const int ChatLines = 4;
    private const float ExitSeconds = 1f, TapSeconds = 0.35f;
    private static readonly string[] Numbers = { "1", "2", "3", "4" };

    private static HudCanvas? _world, _over;
    // Where the mission card ended when it last showed everything, and how far the view menu's button reaches.
    private static float _missionFull, _menuRight;

    private static readonly Dictionary<(string Text, float Px, bool Bold, float Width), List<string>> WrapCache = new();

    // The lines of a text at a width, kept: a panel asks every frame.
    private static List<string> Wrapped(HudCanvas c, string text, float px, bool bold, float width)
    {
        var key = (text, px, bold, width);
        if (WrapCache.TryGetValue(key, out var known)) return known;
        var lines = new List<string>();
        c.Wrap(text.Replace("\r", ""), px, bold, width, lines);
        if (WrapCache.Count >= 1024) WrapCache.Clear();
        return WrapCache[key] = lines;
    }

    private static void Chrome(HudCanvas c, HudBox box)
    {
        var cx = (box.Left + box.Right) / 2f; var cy = (box.Bottom + box.Top) / 2f;
        c.Plate(cx, cy, box.Width, box.Height, Cut, Plate());
        c.Frame(cx, cy, box.Width, box.Height, Cut, Fade(ReplayPlate.Line, 0.75f));
    }

    // A panel's title on its band, across the top of the box.
    private static void PanelHead(HudCanvas c, HudBox box, string title)
    {
        c.Rect((box.Left + box.Right) / 2f, box.Top - PanelTitle / 2f + 2f, box.Width - 8f, PanelTitle - 4f, Fade(ReplayPlate.Line, 0.14f));
        c.Text(title, box.Left + 18f, box.Top - PanelTitle / 2f, 20f, UIKitColors.MenuOver, HudAlign.Left, bold: true);
    }

    private static void BeginLayers(HudCanvas world, HudCanvas over) { world.Begin(); over.Begin(); }

    private static void EndLayers(HudCanvas c, HudCanvas world, HudCanvas over) { c.End(); world.End(); over.End(); }

    /// <summary>The whole screen for this frame.</summary>
    private static void Draw(WorldState state, Analysis? analysis, uint now)
    {
        var hud = _hud; var c = _canvas; var world = _world; var over = _over;
        if (hud == null || c == null || world == null || over == null || !c.Begin()) return;
        BeginLayers(world, over);
        if (!HudPointer.ScreenBounds(hud, out var screenLeft, out var screenBottom, out var screenRight, out var screenTop)) { EndLayers(c, world, over); return; }
        var screen = new HudBox(screenLeft, screenBottom, screenRight, screenTop);
        var midX = (screenLeft + screenRight) / 2f; var midY = (screenBottom + screenTop) / 2f;
        if (Hidden)
        {
            // Hidden: only the way back, faint in the bottom right corner, by the key's current name; flying the film path, nothing at all.
            PointerOnHud = false; WheelTaken = false;
            _hints?.Hide();
            if (!ReplayCamera.Flying) c.Text(_ui.Text("screen.showui", ReplayKeys.Name(ReplayKeys.HideUi)), screenRight - UIKit.SafeMargin, screenBottom + UIKit.SafeMargin + 12f, 16f, Fade(UIKitColors.GameText, 0.6f), HudAlign.Right);
            if (_closeFrom >= 0f) Leaving(over, midX, midY);
            EndLayers(c, world, over);
            return;
        }
        const float gap = HudRegions.Gap;
        var zones = ReplayZones.Of(screen, UIKit.SafeMargin, _bar?.Height ?? 0f, ControlHeight, gap, CardWide, FeedLines * FeedRow);
        var work = zones.Work;
        var keys = _pinKeys || _overKeys;
        var board = (_board || ReplayKeys.Held(ReplayKeys.Scores)) && analysis != null;
        var pointer = HudPointer.Position(hud, out var px, out var py);
        // The keys run before this frame is drawn, so a click is judged on the pointer's place of this frame and the one before: one that came onto a button in the same frame is on it.
        _onHudBefore = PointerOnHud;
        PointerOnHud = !pointer || py < work.Bottom || px > zones.Cards.Left - gap || py > zones.Feed.Bottom && px > zones.Feed.Left
            || _showMenu || _pinKeys || _overKeys || _showLog || _showEvents || _showStats || _showHighlights || board;

        // The control row: the panels' tabs right under the panels, the player's buttons on the right, who is watched between them.
        var rowY = zones.Control.Top - Row / 2f;
        var buttonsLeft = Buttons(hud, c, work.Right, rowY);
        var tabsRight = Tabs(c, work.Left, rowY);
        Watched(c, state, (tabsRight + buttonsLeft) / 2f, (zones.Control.Bottom + zones.Control.Top) / 2f, buttonsLeft - tabsRight - 2f * gap);
        var menuWidth = _showMenu ? MenuWidth(c, work.Width) : 0f;
        _overMenu = _showMenu && HudPointer.Over(hud, _menuRight - menuWidth / 2f, work.Bottom + MenuHeight / 2f, menuWidth, MenuHeight);

        // The keys list takes the screen: the feed, cards and panels give way while it is open.
        if (!keys)
        {
            Feed(hud, c, now, zones.Feed);
            Cards(hud, c, state, zones.Cards);
        }
        // Statistics that would not fit under the whole mission card: the card keeps only its title.
        var statsOpen = _showStats && !board && analysis != null && !keys;
        var whole = !statsOpen || StatsWanted() <= work.Height - _missionFull - gap;
        var missionBottom = keys ? work.Top : Mission(c, state, zones.Mission.Left, work.Top, zones.Mission.Width, whole);
        if (whole && !keys) _missionFull = work.Top - missionBottom;
        var panels = zones.Panels(missionBottom - gap, gap);
        var panelRight = work.Left;
        _overPanel = false;
        if (analysis != null && !keys)
        {
            if (statsOpen) panelRight = Stats(hud, c, over, panels, false);
            else if (_showLog) panelRight = ListPanel(hud, c, 0, _ui.Text("screen.chat"), _ui.Text("chat.none"), ChatRows, now, panels);
            else if (_showEvents) panelRight = ListPanel(hud, c, 1, _ui.Text("screen.events"), _ui.Text("events.none"), EventRows, now, panels);
            else if (_showHighlights) panelRight = ListPanel(hud, c, 2, _ui.Text("screen.highlights"), _ui.Text("highlights.none"), HighlightRows, now, panels);
            else Popups(c, analysis, now, work.Left, work.Bottom, Math.Min(panels.Width, PopupMax));
        }

        // The middle gives way to an open panel: it stands between the panel and the feed, or waits when there is no room for it there.
        var from = panelRight > work.Left ? Math.Max(zones.Middle.Left, panelRight + gap) : zones.Middle.Left;
        var midWidth = zones.Middle.Right - from;
        if (midWidth >= 320f && !keys)
        {
            var centre = (from + zones.Middle.Right) / 2f;
            var y = work.Top;
            if (_paceFrom > 0f) { Pill(c, _ui.Text("pace.idle"), centre, y - 17f, UIKitColors.Warn); y -= 40f; }
            if (analysis != null) y = GameLog(c, analysis, now, centre, y, midWidth);
            y = ScanBars(c, state, centre, y, midWidth);
            Toasts(c, centre, y, midWidth);
        }
        if (analysis != null && !keys) TerminalScreen(c, analysis, now, panelRight > work.Left ? (panelRight + zones.Cards.Left) / 2f : (work.Left + zones.Cards.Left) / 2f, midY, zones.Cards.Left - panelRight - 2f * gap);

        // Over the picture, under every plate: a name passing behind a panel is hidden by it.
        if (analysis != null)
        {
            WorldMarks(world, state, analysis, now, screen, work.Bottom);
            DamageNumbers(world, screen, work.Bottom);
        }

        // What a button opens, over the rest.
        if (board && !keys) Stats(hud, over, over, work, true);
        if (keys) KeysHelp(over, work);
        else
        {
            KeysHidden();
            if (_showMenu) Menu(hud, over, _menuRight, work.Bottom, menuWidth);
        }
        if (_closeFrom >= 0f) Leaving(over, midX, midY);
        // The wheel scrolls an open list or the time bar under the pointer while using the screen; otherwise it is the camera's.
        WheelTaken = ReplayCamera.Ui && pointer && (_overPanel || py < zones.Control.Bottom - gap);
        EndLayers(c, world, over);
    }

    private static void Pill(HudCanvas c, string text, float x, float y, Color ink)
    {
        var width = c.TextWidth(text, 17f, true) + 24f;
        c.Plate(x, y, width, 34f, 6f, Plate());
        c.Text(text, x, y, 17f, ink, HudAlign.Centre, bold: true);
    }

    /// <summary>Opens what the screen's buttons open, as a click does, for a scripted look: events, highlights, chat, stats, stats:team|rating|weapons|enemies|supplies|level, board, keys, view,
    /// none; or name:SLOT:TEXT shows a slot under another name. One line saying what is open.</summary>
    internal static string Show(string what)
    {
        if (!_on) return "no replay";
        if (what.StartsWith("name:", StringComparison.Ordinal) && what.Split(':', 3) is { Length: 3 } parts && int.TryParse(parts[1], out var slot) && slot is >= 0 and < 4)
        {
            Names[slot] = parts[2];
            BuildTeamTable();
            return "slot " + slot + " shown as " + parts[2];
        }
        _showEvents = _showLog = _showStats = _showHighlights = _board = _showMenu = _pinKeys = false;
        _scroll = 0;
        switch (what)
        {
            case "events": _showEvents = true; break;
            case "highlights": _showHighlights = true; break;
            case "chat": _showLog = true; break;
            case "stats": case "stats:team": _showStats = true; _page = StatsPage.Team; break;
            case "stats:weapons": _showStats = true; _page = StatsPage.Weapons; break;
            case "stats:enemies": _showStats = true; _page = StatsPage.Enemies; break;
            case "stats:supplies": _showStats = true; _page = StatsPage.Resources; break;
            case "stats:level": _showStats = true; _page = StatsPage.Level; break;
            case "stats:rating": _showStats = true; _page = StatsPage.Rating; break;
            case "board": _board = true; break;
            case "keys": _pinKeys = true; break;
            case "view": _showMenu = true; break;
        }
        return "open: " + what;
    }

    // ---- Mission ----------------------------------------------------------------------------------------------------------------

    // Top left: the level as players call it, when and by whom it was recorded; under them each objective with its state and its whole text, the timer and
    // the reactor's words. Returns the card's bottom. Not whole, only the title lines.
    private static float Mission(HudCanvas c, WorldState state, float left, float top, float width, bool whole)
    {
        const float pad = 20f;
        var x = left + pad;
        var inner = width - 2f * pad;
        var tag = _ui.Text("screen.replay");
        var tagWidth = c.TextWidth(tag, 15f, true) + 16f;
        var y = top - 28f;
        var title = c.FitText(_title, 26f, true, inner - tagWidth - 12f);
        var titleWidth = c.Text(title, x, y, 26f, UIKitColors.MenuOver, HudAlign.Left, bold: true);
        c.Rect(x + titleWidth + 12f + tagWidth / 2f, y, tagWidth, 26f, UIKitColors.Alert);
        c.Text(tag, x + titleWidth + 12f + tagWidth / 2f, y, 15f, Color.white, HudAlign.Centre, bold: true);
        y -= 16f;
        if (_subtitle.Length > 0)
            foreach (var line in Wrapped(c, _subtitle, 16f, false, inner)) { y -= 13f; c.Text(line, x, y, 16f, Fade(UIKitColors.GameText, 0.8f)); y -= 13f; }
        if (whole) y = MissionDetail(c, state, x, y, inner);
        var bottom = y - 12f;
        Chrome(c, new HudBox(left, bottom, left + width, top));
        c.Rect(left + 3f, (top + bottom) / 2f, 4f, top - bottom - 16f, UIKitColors.Alert);
        return bottom;
    }

    private static float MissionDetail(HudCanvas c, WorldState state, float x, float y, float inner)
    {
        var any = false;
        for (var layer = 0UL; layer < 3UL; layer++)
        {
            if (!state.Objects.TryGetValue((EventKind.Objective, layer), out var payload)) continue;
            var read = new PayloadReader(payload);
            read.U();
            var status = read.U(); var extra = read.U();
            if (status == 0) continue;
            var i = (int)layer;
            if (ObjectiveSeen[i] != (status, extra) || ObjectiveTexts[i].Head == null) { ObjectiveSeen[i] = (status, extra); ObjectiveTexts[i] = ObjectiveLine(i, status, extra); }
            var done = status >= 40;
            var colour = i == 0 ? UIKitColors.MenuOver : i == 1 ? UIKitColors.Warn : UIKitColors.Infection;
            if (!any) { y -= 8f; c.Rect(x + inner / 2f, y, inner, 1.5f, Fade(ReplayPlate.Line, 0.5f)); y -= 4f; any = true; }
            var rowTop = y;
            y -= 14f;
            c.Text(ObjectiveTexts[i].Head, x + 12f, y, 16f, done ? UIKitColors.Good : colour, HudAlign.Left, bold: true);
            y -= 14f;
            foreach (var line in Wrapped(c, ObjectiveTexts[i].Body, 16f, false, inner - 12f)) { y -= 12f; c.Text(line, x + 12f, y, 16f, done ? Fade(UIKitColors.GameText, 0.7f) : UIKitColors.GameText); y -= 13f; }
            c.Rect(x + 2f, (rowTop + y) / 2f, 3f, rowTop - y - 6f, done ? UIKitColors.Good : colour);
            y -= 4f;
        }
        if (ReplayExtras.Timer(state, out var timer) && timer.Active)
        {
            if (timer.Seconds != _timerSeconds || timer.Title != _timerTitle)
            {
                _timerSeconds = timer.Seconds; _timerTitle = timer.Title;
                _timerText = Analysis.Untagged(timer.Title) + "  " + TimelineMath.Clock((uint)Math.Max(0, timer.Seconds) * 1000u);
            }
            y -= 16f;
            foreach (var line in Wrapped(c, _timerText, 18f, true, inner)) { c.Text(line, x, y, 18f, timer.Blinking ? UIKitColors.Alert : UIKitColors.MenuOver, HudAlign.Left, bold: true); y -= 26f; }
        }
        foreach (var (id, payload) in state.Extras)
        {
            if (id.Kind != EventKind.ReactorText) continue;
            if (!ReferenceEquals(payload, _reactorPayload))
            {
                _reactorPayload = payload;
                _reactorText = ReplayExtras.ReactorText(state, id.Key, out var message, out var before, out var after) ? Analysis.Untagged(before + " " + message + " " + after).Replace('\n', ' ') : "";
            }
            foreach (var line in Wrapped(c, _reactorText, 16f, false, inner)) { y -= 13f; c.Text(line, x, y, 16f, UIKitColors.Warn); y -= 13f; }
        }
        return y;
    }

    // ---- Players ----------------------------------------------------------------------------------------------------------------

    /// <summary>The squad: a card for each of the four slots, top to bottom, whoever is in it (a bot's recorded state shows like a player's); an empty
    /// slot is one line. A card has the slot number and whole name; the kills and downs, and the state or zone; the health with its percent, infection
    /// and stamina; then a row for each thing the player has, by its name, with what is left of it, the one in the hands marked. The rows share
    /// the height the cards have.</summary>
    private static void Cards(HudElement hud, HudCanvas c, WorldState state, HudBox zone)
    {
        var filledCards = 0; var rowCount = 0; var fixedHeight = 3f * CardGap;
        for (var slot = 0; slot < 4; slot++)
        {
            ref var pose = ref state.Poses[slot];
            Refresh(slot, state.PlayerStatus[slot], pose.Present ? pose.State : -1, state.PlayerKits[slot]);
            if (InRecording[slot] || !Known[slot] && pose.Present) { filledCards++; rowCount += Reserve(slot); fixedHeight += CardHead + CardFoot + NameExtra(c, slot, zone.Width - 52f); }
            else fixedHeight += CardEmpty;
        }
        var rowHeight = rowCount == 0 ? CardRowMax : Math.Clamp((zone.Height - fixedHeight) / rowCount, CardRowMin, CardRowMax);
        var rowPx = rowHeight >= 24f ? 17f : 16f;
        var width = zone.Width;
        var left = zone.Left;
        var cx = left + width / 2f;
        var x = left + 36f;
        var inner = width - 52f;
        var y = zone.Top;
        for (var slot = 0; slot < 4; slot++)
        {
            ref var pose = ref state.Poses[slot];
            // A slot is filled while someone holds it (the header's player, or a join until its leave): a card left behind is an empty slot
            // again. A slot nothing names is filled while it has a recorded position.
            var filled = InRecording[slot] || !Known[slot] && pose.Present;
            var nameExtra = filled ? NameExtra(c, slot, inner) : 0f;
            var height = filled ? CardHead + nameExtra + Reserve(slot) * rowHeight + CardFoot : CardEmpty;
            var cy = y - height / 2f;
            var colour = SlotColour(slot);
            var followed = filled && slot == Replayer.Follow;
            var over = filled && !_overMenu && HudPointer.Hover(hud, cx, cy, width, height);
            c.Plate(cx, cy, width, height, Cut, Plate());
            c.Pointed(cx, cy, width, height, Cut, UIKitColors.MenuOver, over, over && HudPointer.PressedOn(hud, cx, cy, width, height), followed, rest: false);
            c.Rect(left + 4f, cy, 5f, height - 12f, filled ? colour : Fade(colour, 0.35f));
            var line = filled ? y - 20f : cy;
            c.Text(Numbers[slot], left + 21f, line, 18f, filled ? colour : Fade(colour, 0.5f), HudAlign.Centre, bold: true);
            var grabbed = ClientInference.Grabbed(CardMove[slot]);
            var (stateText, stateColour) = !filled ? (_ui.Text("player.empty"), Fade(UIKitColors.GameText, 0.55f))
                : !pose.Present ? (_ui.Text("player.absent"), Fade(UIKitColors.GameText, 0.65f))
                : CardDown[slot] ? (_ui.Text("player.down"), UIKitColors.Downed)
                : grabbed ? (_ui.Text("player.grabbed"), UIKitColors.Grabbed)
                // At a terminal: said on the card, so whoever is at one shows without following them (11-06).
                : CardMove[slot] == (int)PlayerLocomotion.PLOC_State.OnTerminal ? (_ui.Text("player.terminal"), UIKitColors.MenuOver)
                : (ZoneText(state, slot), followed ? UIKitColors.MenuOver : Fade(UIKitColors.GameText, 0.8f));
            var alarm = filled && (CardDown[slot] || grabbed);
            var stateWidth = stateText.Length > 0 ? c.TextWidth(stateText, 16f, true) + (alarm ? 18f : 0f) : 0f;
            // The name has the first line to itself; an empty slot says so on the same line.
            var nameInk = filled ? NameColour(slot, UIKitColors.MenuOver) : Fade(UIKitColors.GameText, 0.55f);
            if (nameExtra > 0f)
            {
                var lines = Wrapped(c, Names[slot], 20f, true, inner - BadgeRoom(c, slot));
                c.Text(lines[0], x, line, 20f, nameInk, HudAlign.Left, bold: true);
                c.Text(c.FitText(lines[1], 20f, true, inner), x, line - NameLine, 20f, nameInk, HudAlign.Left, bold: true);
            }
            else c.Text(c.FitText(Names[slot], 20f, true, filled ? inner - BadgeRoom(c, slot) : inner - stateWidth - 12f), x, line, 20f, nameInk, HudAlign.Left, bold: true);
            if (filled) Badge(c, slot, x + inner, line);
            var second = filled ? y - 46f - nameExtra : line;
            var stateRight = left + width - 14f;
            if (alarm) c.Rect(stateRight - stateWidth / 2f, second, stateWidth, 24f, stateColour);
            if (stateText.Length > 0) c.Text(stateText, alarm ? stateRight - 9f : stateRight, second, 16f, alarm ? Plate() : stateColour, HudAlign.Right, bold: true);
            if (filled)
            {
                var counts = c.FitText(CardCounts[slot], 16f, false, inner - stateWidth - 12f);
                if (counts.Length > 0) c.Text(counts, x, second, 16f, CardEstimate[slot] ? UIKitColors.Warn : UIKitColors.GameText);
                // The health with its number; the thin lines under it are infection and, for the recorder alone (the game does not send it), stamina.
                var health = CardHealth[slot] < 0 ? 0f : CardHealth[slot] / 127f;
                var number = PercentText(health);
                var healthColour = UIKitColors.Level(health);
                const float numberRoom = 56f;
                var bar = y - 71f - nameExtra;
                var barWidth = inner - numberRoom;
                c.Rect(x + barWidth / 2f, bar, barWidth, 10f, Fade(UIKitColors.GameText, 0.18f));
                if (health > 0f) c.Rect(x + barWidth * health / 2f, bar, barWidth * health, 10f, healthColour);
                c.Text(number, x + inner, bar, 16f, healthColour, HudAlign.Right, bold: true);
                if (CardInfection[slot] > 0f) c.Rect(x + barWidth * CardInfection[slot] / 2f, bar - 8f, barWidth * CardInfection[slot], 3f, UIKitColors.Infection);
                var hasGun = ReplayExtras.Gun(state, slot, out var gun);
                if (hasGun && gun.StaminaPercent >= 0) c.Rect(x + barWidth * gun.StaminaPercent / 200f, bar - 13f, barWidth * gun.StaminaPercent / 100f, 3f, Fade(UIKitColors.GameText, 0.7f));
                var rowY = y - CardHead - nameExtra - rowHeight / 2f;
                var rows = CardRows[slot];
                for (var r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    var inHands = row.Slot == CardHeld[slot];
                    if (inHands)
                    {
                        c.Rect(x + inner / 2f, rowY, inner + 8f, rowHeight - 1f, Fade(colour, 0.32f));
                        c.Rect(x - 3f, rowY, 3f, rowHeight - 1f, colour);
                    }
                    var count = row.Count;
                    var countWidth = count.Length > 0 ? c.TextWidth(count, rowPx, true) + 12f : 0f;
                    c.Text(c.FitText(row.Name, rowPx, inHands, inner - countWidth - 6f), x + 3f, rowY, rowPx, inHands ? UIKitColors.MenuOver : UIKitColors.GameText, HudAlign.Left, bold: inHands);
                    if (count.Length > 0) c.Text(count, x + inner - 3f, rowY, rowPx, row.Share >= 0f ? UIKitColors.Level(row.Share) : inHands ? UIKitColors.MenuOver : UIKitColors.GameText, HudAlign.Right, bold: true);
                    rowY -= rowHeight;
                }
                if (over && HudPointer.Click(hud, cx, cy, width, height)) ReplayCamera.Select(slot);
            }
            y -= height + CardGap;
        }
    }

    // The rows a card keeps room for: the most it has had since the replay began, so a card does not shrink when a player puts something away and the cards under it do not move.
    private static readonly int[] RowsReserved = new int[4];

    private static int Reserve(int slot) => RowsReserved[slot] = Math.Max(RowsReserved[slot], CardRows[slot].Count);

    // How much taller a card is for a name that needs a second line.
    private static float NameExtra(HudCanvas c, int slot, float width) => c.TextWidth(Names[slot], 20f, true) > width - BadgeRoom(c, slot) ? NameLine : 0f;

    // ---- Feed -------------------------------------------------------------------------------------------------------------------

    // Top right, newest first: who, with what, whom. Each line as wide as it needs: within the feed's width the weapon gives way first, then the names.
    // A click on a line (using the screen) watches its enemy from five seconds before the kill, or from its wake-up.
    private static void Feed(HudElement hud, HudCanvas c, uint now, HudBox zone)
    {
        var right = zone.Right;
        var y = zone.Top - FeedRow / 2f;
        var shown = 0;
        for (var i = FeedItems.Count - 1; i >= 0 && shown < FeedLines; i--)
        {
            var k = FeedItems[i];
            if (k.TimeMs > now) continue;
            if (now - k.TimeMs > FeedMs) break;
            var age = (now - k.TimeMs) / (float)FeedMs;
            var alpha = age > 0.8f ? (1f - age) / 0.2f : 1f;
            const float chrome = 58f, mark = 22f;
            var room = zone.Width - chrome - (k.Exact ? 0f : mark);
            var killer = c.FitText(k.Who, 18f, true, room * 0.4f);
            var enemy = c.FitText(k.Enemy, 18f, true, room * 0.4f);
            var enemyW = c.TextWidth(enemy, 18f, true); var killerW = c.TextWidth(killer, 18f, true);
            var weapon = k.How.Length > 0 ? c.FitText(k.How, 16f, false, room - enemyW - killerW - 16f) : "";
            var weaponW = weapon.Length > 0 ? c.TextWidth(weapon, 16f) + 16f : 0f;
            var width = enemyW + weaponW + killerW + (k.Exact ? 0f : mark) + chrome;
            // Every line is as wide as the widest shown so far (within the zone), so the lines line up and a name changing does not make the plate dance.
            width = Math.Min(_feedWidth = Math.Max(_feedWidth, width), zone.Width);
            var killerColour = k.Slot >= 0 && k.Slot < 4 ? SlotColour(k.Slot) : UIKitColors.GameText;
            var overLine = k.Entity >= 4 && HudPointer.Hover(hud, right - width / 2f, y, width, FeedRow - 4f);
            c.Plate(right - width / 2f, y, width, FeedRow - 4f, Cut, Plate(alpha));
            c.Pointed(right - width / 2f, y, width, FeedRow - 4f, Cut, UIKitColors.MenuOver, overLine, overLine && HudPointer.PressedOn(hud, right - width / 2f, y, width, FeedRow - 4f), false, rest: false);
            if (overLine && HudPointer.Click(hud, right - width / 2f, y, width, FeedRow - 4f)) { Replayer.Jump(k.JumpMs); ReplayCamera.WatchEnemy(k.Entity); ManualChoice(); }
            c.Rect(right - width + 3f, y, 4f, FeedRow - 14f, Fade(killerColour, alpha));
            var x = right - 14f;
            c.Text(enemy, x, y, 18f, Fade(UIKitColors.MenuOver, alpha), HudAlign.Right, bold: true);
            x -= enemyW + 11f;
            c.Sprite(UIKitSprites.Arrow(ArrowDirection.Right), x - 5f, y, 11f, 11f, Fade(k.Wake ? UIKitColors.Warn : UIKitColors.Alert, alpha));
            x -= 20f;
            if (weapon.Length > 0) { c.Text(weapon, x, y, 16f, Fade(UIKitColors.GameText, 0.85f * alpha), HudAlign.Right); x -= weaponW; }
            c.Text(killer, x, y, 18f, Fade(killerColour, alpha), HudAlign.Right, bold: true);
            x -= killerW + 6f;
            if (!k.Exact) c.Text("~", x, y, 18f, Fade(UIKitColors.Warn, alpha), HudAlign.Right);
            y -= FeedRow;
            shown++;
        }
    }

    // ---- Control row ------------------------------------------------------------------------------------------------------------

    // A switch that is on: the HUD's accent fill, which reads as lit at a glance (filled in the game's pale menu blue it read as greyed out).
    private static readonly Color Lit = UIKitColors.Accent;

    // The keys (shown while the pointer is on the question mark, kept by a click), the picture's switches and the exit. Returns the row's left end.
    private static float Buttons(HudElement hud, HudCanvas c, float right, float y)
    {
        var x = right;
        // As Esc with nothing open: the game's own leave dialog asks, so one click does not end the replay (a long Esc is the quick way out).
        if (c.Button(ref x, y, _ui.Text("screen.exit"), false, UIKitColors.Alert)) ReplayCamera.GameMenu();
        var from = x;
        _menuRight = from;
        if (c.Button(ref x, y, _ui.Text("screen.view"), _showMenu, Lit)) { _showMenu = !_showMenu; _pinKeys = false; }
        _overMore = HudPointer.Over(hud, (from + x + 10f) / 2f, y, from - x - 10f, Row);
        from = x;
        if (c.Button(ref x, y, "?", _pinKeys, Lit)) { _pinKeys = !_pinKeys; _showMenu = false; }
        _overKeys = HudPointer.Over(hud, (from + x + 10f) / 2f, y, from - x - 10f, Row);
        return x + 10f;
    }

    private const float MenuMinWidth = 300f, MenuRow = 40f, MenuText = 44f;
    private const int MenuRows = 4;
    private const float MenuHeight = MenuRows * MenuRow + 16f;
    private static readonly string[] MenuKeys = { "screen.outline", "screen.bright", "screen.fog", "screen.hideui" };

    private static float MenuWidth(HudCanvas c, float room)
    {
        var widest = 0f;
        foreach (var key in MenuKeys) widest = Math.Max(widest, c.TextWidth(_ui.Text(key), 18f, true));
        return Math.Min(Math.Max(MenuMinWidth, MenuText + widest + 24f), room);
    }

    // The picture's switches, right above their button, one line each with its state. It stays open for the next choice and closes on a click anywhere else.
    private static void Menu(HudElement hud, HudCanvas c, float right, float bottom, float width)
    {
        var box = new HudBox(right - width, bottom, right, bottom + MenuHeight);
        Chrome(c, box);
        var cx = right - width / 2f;
        var y = bottom + MenuHeight - 8f - MenuRow / 2f;
        if (MenuLine(hud, c, cx, width, ref y, 0, _outlines)) SetOutlines(!_outlines);
        if (MenuLine(hud, c, cx, width, ref y, 1, ReplayAssist.Bright)) ReplayAssist.Bright = !ReplayAssist.Bright;
        if (MenuLine(hud, c, cx, width, ref y, 2, ReplayAssist.Fog)) ReplayAssist.Fog = !ReplayAssist.Fog;
        if (MenuLine(hud, c, cx, width, ref y, 3, _hidden)) { _hidden = true; _showMenu = false; }
        if (HudPointer.Pressed && !_overMenu && !_overMore) _showMenu = false;
    }

    private static bool MenuLine(HudElement hud, HudCanvas c, float cx, float width, ref float y, int line, bool on)
    {
        var over = HudPointer.Hover(hud, cx, y, width - 12f, MenuRow);
        c.Pointed(cx, y, width - 12f, MenuRow - 2f, 6f, UIKitColors.MenuOver, over, HudPointer.PressedOn(hud, cx, y, width - 12f, MenuRow), false, rest: false);
        var left = cx - width / 2f;
        c.Frame(left + 24f, y, 18f, 18f, 8f, on ? UIKitColors.MenuOver : Fade(UIKitColors.GameText, 0.6f));
        if (on) c.Rect(left + 24f, y, 9f, 9f, UIKitColors.MenuOver);
        c.Text(c.FitText(_ui.Text(MenuKeys[line]), 18f, on, width - MenuText - 16f), left + MenuText, y, 18f, on ? UIKitColors.MenuOver : UIKitColors.GameText, HudAlign.Left, bold: on);
        var clicked = HudPointer.Click(hud, cx, y, width - 12f, MenuRow);
        y -= MenuRow;
        return clicked;
    }

    private static readonly HudKeyGuide KeyGuide = new();
    private static bool _keysShown;

    /// <summary>Not shown this frame: the keys list is written again, with the keys as they are then, the next time it opens.</summary>
    private static void KeysHidden() => _keysShown = false;

    private static KeyGuideRow KeyRow(string text, params string[] keys) => new(keys, _ui.Text(text));

    // The keys in the middle of the screen, by group: key caps and what they do, by each action's current key.
    private static void KeysHelp(HudCanvas c, HudBox work)
    {
        if (!_keysShown)
        {
            _keysShown = true;
            static string N(KeyAction? action) => ReplayKeys.Name(action);
            var wheel = _ui.Text("keys.wheel");
            KeyGuide.Set(new[]
            {
                new KeyGuideGroup(_ui.Text("keys.group.play"), new[]
                {
                    KeyRow("keys.play", N(ReplayKeys.Play)),
                    KeyRow("keys.speed", N(ReplayKeys.Faster), N(ReplayKeys.Slower)),
                    KeyRow("keys.frame", N(ReplayKeys.FrameBack), N(ReplayKeys.FrameForward)),
                    KeyRow("keys.ends", N(ReplayKeys.ToStart), N(ReplayKeys.ToEnd)),
                    KeyRow("keys.skip", N(ReplayKeys.Back), N(ReplayKeys.Forward)),
                    KeyRow("keys.mark", N(ReplayKeys.PreviousMark), N(ReplayKeys.NextMark)),
                    KeyRow("keys.mark", N(ReplayKeys.MarkBack), N(ReplayKeys.MarkForward)),
                }),
                new KeyGuideGroup(_ui.Text("keys.group.camera"), new[]
                {
                    KeyRow("keys.view", N(ReplayKeys.CameraNext), N(ReplayKeys.CameraNextKey)),
                    KeyRow("keys.free", N(ReplayKeys.Free)),
                    KeyRow("keys.above", N(ReplayKeys.Above)),
                    KeyRow("keys.reset", N(ReplayKeys.Reset)),
                    KeyRow("keys.distance", wheel),
                }),
                new KeyGuideGroup(_ui.Text("keys.group.who"), new[]
                {
                    KeyRow("keys.next", N(ReplayKeys.PlayerNext), N(ReplayKeys.PlayerNextKey)),
                    KeyRow("keys.previous", N(ReplayKeys.PlayerPrevious), N(ReplayKeys.PlayerPreviousKey)),
                    KeyRow("keys.watch", N(ReplayKeys.Watch[0]) + "–" + N(ReplayKeys.Watch[3])),
                    KeyRow("keys.auto", N(ReplayKeys.Auto)),
                    KeyRow("keys.enemy", N(ReplayKeys.Enemy)),
                }),
                new KeyGuideGroup(_ui.Text("keys.group.fly"), new[]
                {
                    KeyRow("keys.fly", _ui.Text("keys.movekeys")),
                    KeyRow("keys.updown", N(ReplayKeys.Rise), N(ReplayKeys.Sink)),
                    KeyRow("keys.fast", N(ReplayKeys.Fast)),
                    KeyRow("keys.flyspeed", wheel),
                }),
                new KeyGuideGroup(_ui.Text("keys.group.film"), new[]
                {
                    KeyRow("keys.pathmark", N(ReplayKeys.PathMark)),
                    KeyRow("keys.pathclear", N(ReplayKeys.PathClear)),
                    KeyRow("keys.pathplay", N(ReplayKeys.PathPlay)),
                }),
                new KeyGuideGroup(_ui.Text("keys.group.screen"), new[]
                {
                    KeyRow("keys.mouse", "Ctrl"),
                    KeyRow("keys.scores", N(ReplayKeys.Scores)),
                    KeyRow("keys.chat", N(ReplayKeys.Chat)),
                    KeyRow("keys.hide", N(ReplayKeys.HideUi)),
                    KeyRow("keys.keys", N(ReplayKeys.Help)),
                    KeyRow("keys.close", N(ReplayKeys.Close)),
                }),
            });
        }
        const float pad = 28f;
        KeyGuide.Layout(c, work.Width - 2f * pad, work.Height - PanelTitle - 2f * pad);
        var width = KeyGuide.Width + 2f * pad; var height = KeyGuide.Height + PanelTitle + 2f * pad - 16f;
        var cx = (work.Left + work.Right) / 2f; var cy = (work.Bottom + work.Top) / 2f;
        var box = new HudBox(cx - width / 2f, cy - height / 2f, cx + width / 2f, cy + height / 2f);
        Chrome(c, box);
        PanelHead(c, box, _ui.Text("screen.keys"));
        KeyGuide.Draw(c, box.Left + pad, box.Top - PanelTitle - 12f, UIKitColors.Accent, UIKitColors.GameText, UIKitColors.MenuOver, Fade(ReplayPlate.Line, 0.2f), Fade(ReplayPlate.Line, 0.7f));
    }

    // ---- Lists ------------------------------------------------------------------------------------------------------------------

    private readonly record struct ListRow(uint TimeMs, Color Colour, string Text, int Index);
    private static readonly List<ListRow> EventRows = new(), HighlightRows = new(), ChatRows = new();
    // The width each list wants, worked out once per analysis.
    private static readonly float[] ListWidths = new float[3];

    // The chat log (0), events (1) or highlights (2) around the moment on the bar: the time, a line in the row's colour, and the whole text broken over as
    // many lines as it takes; a click picks a row. Returns the panel's right edge.
    private static float ListPanel(HudElement hud, HudCanvas c, int list, string title, string empty, List<ListRow> rows, uint now, HudBox zone)
    {
        const float pad = 16f, bar = 12f;
        var duration = Replayer.Clock?.DurationMs ?? 0;
        var timeWidth = c.TextWidth(TimelineMath.Clock(duration), 16f, true) + 8f;
        var chrome = 2f * pad + bar + timeWidth + 12f;
        var most = Math.Min(zone.Width, ListMaxWidth);
        if (ListWidths[list] <= 0f)
        {
            var widest = c.TextWidth(title, 20f, true);
            foreach (var row in rows) widest = Math.Max(widest, c.TextWidth(row.Text, 18f));
            ListWidths[list] = widest + chrome;
        }
        var width = Math.Clamp(ListWidths[list], Math.Min(ListMinWidth, most), most);
        var inner = width - chrome;
        var room = Math.Min(zone.Height, ListMaxHeight) - PanelTitle - PanelFoot;
        var next = 0;
        while (next < rows.Count && rows[next].TimeMs <= now) next++;
        // The moment stands a little above the middle of the list; the wheel moves away from it and the list stays where it was scrolled to.
        var anchor = next - (int)(room / ListLine / 2f);
        var first = Math.Clamp(anchor + _scroll, 0, Math.Max(0, rows.Count - 1));
        var used = 0f; var shown = 0;
        for (var i = first; i < rows.Count; i++)
        {
            var h = RowHeightOf(c, rows[i].Text, inner);
            if (used + h > room && shown > 0) break;
            used += h; shown++;
        }
        while (first > 0 && first + shown >= rows.Count)
        {
            var h = RowHeightOf(c, rows[first - 1].Text, inner);
            if (used + h > room) break;
            used += h; shown++; first--;
        }
        _scroll = first - anchor;
        var height = PanelTitle + PanelFoot + Math.Max(used, ListLine + 8f);
        var box = new HudBox(zone.Left, zone.Bottom, zone.Left + width, zone.Bottom + height);
        Chrome(c, box);
        PanelHead(c, box, title);
        if (rows.Count == 0)
        {
            c.Text(c.FitText(empty, 18f, false, width - 2f * pad), box.Left + pad, box.Top - PanelTitle - 18f, 18f, Fade(UIKitColors.GameText, 0.7f));
            return box.Right;
        }
        Scroll(hud, (box.Left + box.Right) / 2f, (box.Bottom + box.Top) / 2f, width, height);
        var y = box.Top - PanelTitle;
        for (var i = first; i < first + shown && i < rows.Count; i++)
        {
            var row = rows[i];
            var lines = Wrapped(c, row.Text, 18f, false, inner);
            var h = Math.Max(1, lines.Count) * ListLine + 6f;
            var cy = y - h / 2f;
            var over = HudPointer.Hover(hud, box.Left + width / 2f, cy, width - 12f, h);
            c.Pointed(box.Left + width / 2f, cy, width - 12f, h - 2f, 6f, UIKitColors.MenuOver, over, HudPointer.PressedOn(hud, box.Left + width / 2f, cy, width - 12f, h), false, rest: false);
            var passed = row.TimeMs <= now;
            if (i == next && i > first) c.Rect(box.Left + width / 2f, y, width - 16f, 2f, UIKitColors.Accent);
            c.Rect(box.Left + pad - 4f, cy, 4f, h - 8f, passed ? row.Colour : Fade(row.Colour, 0.45f));
            var ink = passed ? UIKitColors.GameText : Fade(UIKitColors.GameText, 0.55f);
            var first0 = y - 3f - ListLine / 2f;
            c.Text(TimelineMath.Clock(row.TimeMs), box.Left + pad + bar + timeWidth - 8f, first0, 16f, Fade(ink, 0.85f), HudAlign.Right, bold: true);
            for (var l = 0; l < lines.Count; l++) c.Text(lines[l], box.Left + pad + bar + timeWidth + 12f - 4f, first0 - l * ListLine, 18f, ink);
            if (HudPointer.Click(hud, box.Left + width / 2f, cy, width - 12f, h)) { if (list == 2) PickHighlight(_highlights[row.Index]); else JumpToEvent(row.TimeMs); }
            y -= h;
        }
        HudTable.ScrollThumb(c, box.Right - 6f, box.Top - PanelTitle, room, first, shown, rows.Count, Fade(ReplayPlate.Line, 0.7f));
        return box.Right;
    }

    private static float RowHeightOf(HudCanvas c, string text, float width) => Math.Max(1, Wrapped(c, text, 18f, false, width).Count) * ListLine + 6f;

    // The wheel over an open list, using the screen: a line a notch (up shows earlier lines); the list then stays where it was scrolled to.
    private static void Scroll(HudElement hud, float cx, float cy, float width, float height)
    {
        if (!ReplayCamera.Ui || !HudPointer.Over(hud, cx, cy, width, height)) return;
        _overPanel = true;
        var notches = UnityEngine.Input.mouseScrollDelta.y;
        if (notches != 0f) _scroll -= Math.Sign(notches);
    }

    /// <summary>The recorded chat of the last few seconds while no panel is open, newest at the bottom, from <paramref name="bottom"/> upwards, the whole message over as many lines as it takes.</summary>
    private static void Popups(HudCanvas c, Analysis analysis, uint now, float left, float bottom, float room)
    {
        const float line = 28f, pad = 12f;
        var chat = analysis.Chat;
        var next = ReplayFeed.NextChat(chat, now + 1);
        var y = bottom;
        for (var i = next - 1; i >= 0 && i >= next - ChatLines; i--)
        {
            var said = chat[i];
            if (now - said.TimeMs > ChatMs) break;
            var age = (now - said.TimeMs) / (float)ChatMs;
            var alpha = age > 0.8f ? (1f - age) / 0.2f : 1f;
            var player = said.Slot >= 0 && said.Slot < 4;
            var name = player ? Names[said.Slot] : "?";
            var nameWidth = c.TextWidth(name + ":", 18f, true);
            var lines = Wrapped(c, said.Text, 18f, false, Math.Max(120f, room - nameWidth - 3f * pad));
            var wide = 0f;
            foreach (var text in lines) wide = Math.Max(wide, c.TextWidth(text, 18f));
            var width = nameWidth + wide + 3f * pad + 6f;
            var height = Math.Max(1, lines.Count) * line + 8f;
            c.Plate(left + width / 2f, y + height / 2f, width, height, 8f, Plate(alpha));
            c.Rect(left + 3f, y + height / 2f, 4f, height - 10f, Fade(player ? SlotColour(said.Slot) : UIKitColors.GameText, alpha));
            var top = y + height - 4f - line / 2f;
            c.Text(name + ":", left + pad, top, 18f, Fade(player ? NameColour(said.Slot, SlotColour(said.Slot)) : UIKitColors.GameText, alpha), HudAlign.Left, bold: true);
            for (var l = 0; l < lines.Count; l++) c.Text(lines[l], left + pad + nameWidth + pad, top - l * line, 18f, Fade(UIKitColors.MenuOver, alpha));
            y += height + 6f;
        }
    }

    // ---- Middle -----------------------------------------------------------------------------------------------------------------

    // The game's own log lines as it shows them (a player was downed, revived, the level's subtitles), newest at the bottom, each on one plate grown to its
    // lines; only the moment decides what shows, so a jump leaves nothing behind. Returns where the next thing starts.
    private static float GameLog(HudCanvas c, Analysis analysis, uint now, float centre, float top, float room)
    {
        const float row = 28f, most = 8f;
        var lines = analysis.GameLines;
        var inner = Math.Min(room, 960f) - 24f;
        var next = 0;
        while (next < lines.Count && lines[next].TimeMs <= now) next++;
        var first = next;
        var rows = 0;
        while (first > 0 && now - lines[first - 1].TimeMs <= GameLineMs)
        {
            rows += Math.Max(1, Wrapped(c, lines[first - 1].Text, 17f, false, inner).Count);
            if (rows > most && first < next) break;
            first--;
        }
        var y = top;
        for (var i = first; i < next; i++)
        {
            var line = lines[i];
            var wrapped = Wrapped(c, line.Text, 17f, false, inner);
            var age = (now - line.TimeMs) / (float)GameLineMs;
            var alpha = age > 0.8f ? (1f - age) / 0.2f : 1f;
            var wide = 0f;
            foreach (var text in wrapped) wide = Math.Max(wide, c.TextWidth(text, 17f));
            var height = wrapped.Count * row + 8f;
            c.Plate(centre, y - height / 2f, wide + 24f, height, 6f, Plate(alpha));
            var at = y - 4f - row / 2f;
            foreach (var text in wrapped) { c.Text(text, centre, at, 17f, Fade(UIKitColors.MenuOver, alpha), HudAlign.Centre); at -= row; }
            y -= height + 6f;
        }
        return y;
    }

    private static readonly Dictionary<ulong, string> ScanTexts = new();
    private const int ScanBarsShown = 3;

    // A running scan's progress, as the game's own bar (part of its hidden HUD) shows it, with who stands in the circle: the recorded extra is the percent in the low byte, the slots inside in the next four bits, how many it wants in the four above. A scan that stopped part way still shows; one nobody has started does not. Returns where the next thing starts.
    private static float ScanBars(HudCanvas c, WorldState state, float centre, float top, float room)
    {
        const float height = 58f;
        var width = Math.Min(room, 460f);
        var y = top;
        var shown = 0;
        foreach (var (id, payload) in state.Objects)
        {
            if (id.Kind != EventKind.Scan) continue;
            var read = new PayloadReader(payload);
            read.U();
            var status = read.U();
            var extra = read.U();
            var percent = (int)(extra & 255);
            if (status != 3 && !(status == 2 && percent > 0)) continue;
            if (shown++ == ScanBarsShown) break;
            if (!ScanTexts.TryGetValue(extra, out var text))
                ScanTexts[extra] = text = _ui.Text("scan.progress", percent, System.Numerics.BitOperations.PopCount((extra >> 8) & 15), (extra >> 12) & 15);
            var cy = y - height / 2f;
            c.Plate(centre, cy, width, height, Cut, Plate());
            c.Text(c.FitText(text, 17f, true, width - 24f), centre, cy + 10f, 17f, status == 3 ? UIKitColors.MenuOver : UIKitColors.GameText, HudAlign.Centre, bold: true);
            var inner = width - 24f;
            var share = Math.Clamp(percent / 100f, 0f, 1f);
            c.Rect(centre, cy - 14f, inner, 8f, Fade(UIKitColors.GameText, 0.18f));
            if (share > 0f) c.Rect(centre - inner / 2f + inner * share / 2f, cy - 14f, inner * share, 8f, status == 3 ? UIKitColors.MenuOver : UIKitColors.GameText);
            y -= height + 6f;
        }
        return y;
    }

    private static readonly NoticeQueue Notices = new(3);

    // The replay's notices in the middle: the shared corner list stands where the mission card is.
    private static void Toasts(HudCanvas c, float centre, float top, float room)
    {
        var now = ForgeCore.Frame.UnscaledTime;
        Notices.Tick(now);
        var y = top;
        for (var i = 0; i < Notices.Count; i++)
        {
            var alpha = Notices.Alpha(i, now);
            if (alpha <= 0f) continue;
            var ink = Notices.Tone(i) == NoticeTone.Warn ? UIKitColors.Warn : UIKitColors.MenuOver;
            var lines = Wrapped(c, Notices.Text(i), 17f, false, Math.Min(room, 800f) - 28f);
            var wide = 0f;
            foreach (var text in lines) wide = Math.Max(wide, c.TextWidth(text, 17f));
            var height = lines.Count * 28f + 8f;
            c.Plate(centre, y - height / 2f, wide + 28f, height, 6f, Plate(alpha));
            c.Rect(centre - wide / 2f - 11f, y - height / 2f, 4f, height - 8f, Fade(ink, alpha));
            var at = y - 4f - 14f;
            foreach (var text in lines) { c.Text(text, centre, at, 17f, Fade(ink, alpha), HudAlign.Centre); at -= 28f; }
            y -= height + 6f;
        }
    }

    // ---- Statistics -------------------------------------------------------------------------------------------------------------

    private static HudTable StatsTable => _page switch { StatsPage.Team => TeamTable, StatsPage.Weapons => WeaponTable, StatsPage.Enemies => EnemyTable, StatsPage.Resources => ResourceTable, StatsPage.Rating => CompareTable, _ => LevelTable };

    private static float _statsWidth, _feedWidth;

    private static float StatsWanted() => PanelTitle + (_page == StatsPage.Rating ? _ratingBody : StatsTable.FullHeight) + StatsLegend;

    /// <summary>The statistics in <paramref name="zone"/>: from its bottom left corner, or in its middle as the scoreboard. The table's columns come from
    /// what they hold, the page buttons stand on its title row. Returns the right edge.</summary>
    private static float Stats(HudElement hud, HudCanvas c, HudCanvas over, HudBox zone, bool middle)
    {
        var table = StatsTable;
        var title = _ui.Text("screen.stats");
        var buttons = 0f;
        foreach (var key in PageKeys) buttons += c.ButtonWidth(_ui.Text(key)) + 10f;
        var minimum = c.TextWidth(title, 20f, true) + 18f + (HowTexts.Count > 0 ? Info + 14f : 0f) + 28f + buttons + 12f;
        var rating = _page == StatsPage.Rating;
        var ratingBox = default(RatingBox);
        if (rating) { ratingBox = RatingLayout(c, zone.Width, zone.Height - PanelTitle - StatsLegend, minimum); _ratingBody = ratingBox.Height; }
        else table.Layout(c, zone.Width, zone.Height - PanelTitle - StatsLegend, minimum);
        // The panel only grows while the screen is open, so changing page or a number gaining a digit does not move its edge; the page buttons stand at a fixed place from the left.
        var width = _statsWidth = Math.Max(_statsWidth, rating ? ratingBox.Width : table.Width); var height = PanelTitle + (rating ? ratingBox.Height : table.Height) + StatsLegend;
        var left = middle ? (zone.Left + zone.Right - width) / 2f : zone.Left;
        var bottom = middle ? (zone.Bottom + zone.Top - height) / 2f : zone.Bottom;
        var box = new HudBox(left, bottom, left + width, bottom + height);
        Chrome(c, box);
        PanelHead(c, box, title);
        var cx = left + width / 2f; var cy = bottom + height / 2f;
        var rowY = box.Top - PanelTitle / 2f;
        // The info icon beside the title, only while a number is a guess: why, shown while the pointer is on it.
        var ix = left + 18f + c.TextWidth(title, 20f, true) + 14f + Info / 2f;
        var hovered = HowTexts.Count > 0 && HudPointer.Over(hud, ix, rowY, Info, Info);
        if (HowTexts.Count > 0)
        {
            c.Frame(ix, rowY, Info, Info, 13f, hovered ? UIKitColors.MenuOver : Fade(UIKitColors.GameText, 0.8f));
            c.Text("i", ix, rowY, 16f, hovered ? UIKitColors.MenuOver : UIKitColors.GameText, HudAlign.Centre, bold: true);
        }
        PageButtons(c, left + minimum - 12f, rowY);
        Scroll(hud, cx, cy, width, height);
        if (rating) { _scroll = 0; RatingBody(hud, c, left, box.Top - PanelTitle, ratingBox); }
        else
        {
            if (_scroll != 0) { table.Scroll(_scroll); _scroll = 0; }
            table.Draw(c, left, box.Top - PanelTitle);
        }
        c.Text(c.FitText(rating ? _ratingLegend : _ui.Text("stats.legend"), 15f, false, width - 40f), left + 18f, bottom + StatsLegend / 2f, 15f, Fade(UIKitColors.GameText, 0.7f));
        if (hovered) How(over, left + 8f, rowY - 18f, zone.Width);
        return box.Right;
    }

    // The explanation of the estimates, under the info icon, over the rest.
    private static void How(HudCanvas c, float left, float top, float room)
    {
        const float line = 28f;
        var inner = Math.Min(room, 760f) - 36f;
        var wide = 0f; var count = 0;
        foreach (var text in HowTexts)
            foreach (var part in Wrapped(c, text, 17f, false, inner)) { wide = Math.Max(wide, c.TextWidth(part, 17f)); count++; }
        var width = wide + 36f; var height = count * line + 20f;
        Chrome(c, new HudBox(left, top - height, left + width, top));
        var y = top - 10f - line / 2f;
        foreach (var text in HowTexts)
            foreach (var part in Wrapped(c, text, 17f, false, inner)) { c.Text(part, left + 18f, y, 17f, UIKitColors.GameText); y -= line; }
    }

    private static Color StatColour(StatQuality quality) => quality == StatQuality.Exact ? UIKitColors.MenuOver : quality == StatQuality.Estimate ? UIKitColors.Warn : Fade(UIKitColors.GameText, 0.55f);

    private static TableCell StatTableCell(StatCell cell) => new(cell.Text, StatColour(cell.Quality), cell.Quality == StatQuality.Exact);
}
