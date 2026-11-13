using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CellMenu;
using ForgeUI;
using SNetwork;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The REPLAYS screen on the menu bar (PLAN 2.1), on the game's settings page and made of its own pieces (ForgeUI <c>MenuScreen</c>): a row of
/// filter tabs across the top (how it ended, the rundown choice, the files that cannot be read, the size on disk), under it two windows only as tall as
/// their rows: the recordings (an entry of two or three lines each, newest first unless another sort is chosen) and the chosen recording (the run; the
/// estimates and the file behind More) with play, star and More right under it. A recording of a rundown this game has not installed says
/// so by name and its play button stays dim; a failure is one sentence with a way to the log and goes away when closed. Values keep the game's own menu text
/// colour; labels, alerts and warnings are the game's colours (<see cref="UIKitColors"/>). Nothing pops up: deleting the chosen recording, and playing when
/// others are in the lobby (it leaves the lobby), are confirmed on the button itself (ForgeUI <c>AddConfirmButton</c>). After a replay the screen opens again.</summary>
internal static class ReplayList
{
    // Two columns under the filter tabs: the list takes what the screen leaves beside the details, which keep their width (a table of four players).
    // Both are only as tall as their rows (MenuScreen fit); the details leave room under them for their buttons, clear of the game's version text.
    private const float MinListWidth = 1100f, DetailWidth = 800f, UnderDetails = 310f;
    // The sort choices over the list: start, length, result, rundown (the ids of _sort), in the order they stand.
    private static readonly int[] SortChoices = { 3, 4, 5, 1 };
    private static readonly string[] SortKeys = { "list.start", "list.length", "list.result", "list.rundown" };
    private const int StartColumn = 3;
    private const int Unreadable = 4;
    // The rundown choice open (its options stand in the list's place) and the details' More buttons shown.
    private static bool _rundownOpen, _more;
    private static UIKitMod _ui = null!;
    private static MenuBarButton? _screen;
    // The whole reads kept (a recording is read through once for its tables): the last few only, the lighter totals of every read stay for the rows.
    private const int KeptReads = 3;
    private static readonly Dictionary<string, Analysis?> Summaries = new();
    private static readonly List<string> Recent = new();
    private static readonly Dictionary<string, (int Kills, int Downs)> Totals = new();
    // The recordings whose row needs a read for its kills (a client's file: the footer cannot settle them), read one at a time when nothing else is.
    private static readonly Dictionary<string, ReplayHeader> Wanted = new();
    // Of those, the ones only the member rating wants counted (no row waits for them).
    private static readonly HashSet<string> RatingOnly = new(StringComparer.OrdinalIgnoreCase);
    // What is shown: how it ended (Sections, or Unreadable for the files that cannot be read), which rundown (its name, null: all) and the chosen recording's page.
    private static int _section, _page;
    private static string? _rundown;
    // The column the list is sorted by (1 rundown, 2 level, 3 start, 4 length, 5 result) and which way; newest first until a heading is clicked.
    private static int _sort = StartColumn;
    private static bool _descending = true;
    private static string? _chosen, _waitingFor;
    private static bool _reopen, _wasActive;

    internal static void Load(UIKitMod ui)
    {
        _ui = ui;
        _screen = ui.AddMenuBarScreen("List", UIText.Key("list.title"), Build);
    }

    /// <summary>Per frame: starts a chosen recording once the lobby is gone, opens the list again after a replay ended, and builds the list
    /// again when the recordings on disk changed (one finished, was deleted or starred) so it never shows an older set than the folder holds.</summary>
    internal static void Tick()
    {
        PollSummaries();
        Refresh();
        if (Replayer.Active) _wasActive = true;
        else if (_wasActive) { _wasActive = false; _reopen = true; }
        // A chosen recording that did not start: the list opens again and says why.
        if (Replayer.Failure != null) _reopen = true;
        // The game's state is read once, and only on a frame that has something to open or start.
        var noLobby = (_reopen || _waitingFor != null) && GameStateManager.CurrentStateName == eGameStateName.NoLobby;
        if (_reopen && noLobby && _screen != null && _screen.Open()) _reopen = false;
        if (_waitingFor != null && noLobby && !SNet.IsInLobby && SNet.LocalPlayer?.IsInSessionHub != true)
        {
            var path = _waitingFor; _waitingFor = null;
            Replayer.Start(path);
        }
    }

    // The recordings as last built: their names, sizes and write times. A change is built again a second later, so a recording still being
    // written does not build the screen again on every block.
    private static string _files = "";
    private static float _filesAt;

    private static string Files()
    {
        var text = new StringBuilder();
        foreach (var folder in new[] { Plugin.ReplayFolder, System.IO.Path.Combine(Plugin.ReplayFolder, ReplayLibrary.StarredFolder) })
        {
            if (!System.IO.Directory.Exists(folder)) continue;
            foreach (var path in System.IO.Directory.GetFiles(folder, "*.frpl").OrderBy(p => p, StringComparer.Ordinal))
            {
                var info = new System.IO.FileInfo(path);
                text.Append(path).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }
        return text.ToString();
    }

    private static void Refresh()
    {
        if (!Shown()) return;
        var now = ForgeCore.Frame.UnscaledTime;
        if (now < _filesAt) return;
        _filesAt = now + 1f;
        var files = Files();
        if (files == _files) return;
        _files = files;
        _built?.Rebuild();
    }

    // The list screen is on the game's settings page: the only page it is shown on. Another page change closes it (ForgeUI does that), and
    // then the folder is not read again until the screen is built again. Nothing scans the replay folder while a level or a replay runs.
    private static bool _shown;

    private static bool Shown()
    {
        if (!_shown) return false;
        var layer = GuiManager.MainMenuLayer;
        if (layer != null && layer.m_currentPageEnum == eCM_MenuPage.CMP_SETTINGS && layer.GetPage(eCM_MenuPage.CMP_SETTINGS)?.TryCast<CM_PageSettings>() is { WasCollected: false } page
            && page.gameObject.activeInHierarchy) return true;
        _shown = false;
        return false;
    }

    private static void Build(MenuScreen screen)
    {
        UIKitColors.Refresh();
        _shown = true;
        _built = screen;
        _lastBuilt = ForgeCore.Frame.UnscaledTime;
        if (Replayer.TakeFailure() is { } failed) screen.Toast(failed, NoticeTone.Alert, ProblemSeconds, UIText.Key("list.viewlog"), ViewLog);
        var all = ReplayLibrary.List(Plugin.ReplayFolder);
        RundownNames.Learn(all);
        // The recordings the member rating has not counted yet are read in the background, one at a time, like the rows that wait for a read.
        if (RatingStore.Table != null) foreach (var e in all) if (e.Header != null && e.Complete && e.Summary != null && !IsReading(e.Path) && !RatingStore.Has(e.Path) && Wanted.TryAdd(e.Path, e.Header)) RatingOnly.Add(e.Path);
        if (_section == Unreadable && all.All(e => e.Header != null)) _section = 0;
        if (_rundown != null && all.All(e => RundownLabel(e) != _rundown)) _rundown = null;
        var entries = Sorted(all.Where(e => Shown(e, _section, _rundown))).ToList();
        var chosen = entries.FirstOrDefault(e => e.Path == _chosen) ?? entries.FirstOrDefault();
        _chosen = chosen?.Path;

        screen.FillScreen(MinListWidth + DetailWidth + 24f);
        Filters(screen, all);

        var listWidth = screen.RoomAcross(DetailWidth, MinListWidth);
        var title = _ui.Text(_section == Unreadable ? "list.oldfiles" : Sections[_section]) + (_rundown == null ? "" : "  ·  " + MenuText.Plain(_rundown));
        var list = screen.AddWindow(UIText.Plain(title), listWidth, screen.ToastRoom, fit: true);
        if (_rundownOpen) RundownChoices(screen, list, all);
        else if (entries.Count == 0) list.AddRow(UIText.Plain(Dim(_ui.Text(all.Count > 0 ? "list.nomatch" : "list.empty"))));
        else
        {
            SortRow(screen, list);
            foreach (var entry in entries)
            {
                var e = entry;
                list.AddEntry(Entry(e), () => { _chosen = e.Path; screen.Rebuild(); }, ReferenceEquals(e, chosen));
            }
        }
        if (chosen != null) Details(screen, chosen);
        _files = Files();
    }

    // How long a problem and a confirmation stay when not closed.
    private const float ProblemSeconds = 12f, DoneSeconds = 10f;

    // The tabs across the top: how it ended (each with how many it would show given the rundown chosen), the rundown choice, the files that cannot be
    // read (only when there are some), and at the right end what all recordings take on disk, starred ones included.
    private static void Filters(MenuScreen screen, IReadOnlyCollection<ReplayEntry> all)
    {
        screen.AddTab(UIText.Plain(_ui.Text("list.total", Bytes(ReplayLibrary.TotalBytes(Plugin.ReplayFolder)))), false, null, right: true);
        string Counted(string text, int count) => text + "  " + count.ToString(CultureInfo.InvariantCulture);
        for (var s = 0; s < Sections.Length; s++)
        {
            var section = s;
            screen.AddTab(UIText.Plain(Counted(_ui.Text(Sections[s]), all.Count(e => Shown(e, section, _rundown)))), _section == s, () => { _section = section; _rundownOpen = false; screen.Rebuild(); });
        }
        var chosen = _rundown == null ? _ui.Text("list.allrundowns") : RundownMissing(all, _rundown) ? Warn(MenuText.Plain(_rundown)) : MenuText.Plain(_rundown);
        screen.AddTab(UIText.Plain(chosen + (_rundownOpen ? " ^" : " v")), _rundownOpen || _rundown != null, () => { _rundownOpen = !_rundownOpen; screen.Rebuild(); });
        var old = all.Count(e => e.Header == null);
        if (old > 0) screen.AddTab(UIText.Plain(Counted(_ui.Text("list.oldfiles"), old)), _section == Unreadable, () => { _section = Unreadable; _rundown = null; _rundownOpen = false; screen.Rebuild(); });
    }

    private static bool RundownMissing(IEnumerable<ReplayEntry> all, string name)
        => all.Where(e => e.Header != null && RundownLabel(e) == name).All(e => RundownNames.Refusal(e) != null);

    // The rundown choice open: its options in the list's place, each with how many recordings it would show; a rundown no recording of which this game can
    // play is in the warning colour and says what is missing on the pointer. Choosing one closes the choice.
    private static void RundownChoices(MenuScreen screen, MenuScreenWindow list, IReadOnlyCollection<ReplayEntry> all)
    {
        var inSection = _section == Unreadable ? 0 : _section;
        list.Columns(new[] { 85f }, 1);
        void Option(string? name, string label, bool selected)
            => list.AddRow(new[] { label, all.Count(e => Shown(e, inSection, name)).ToString(CultureInfo.InvariantCulture) }, () => { _rundown = name; _section = inSection; _rundownOpen = false; screen.Rebuild(); }, selected);
        Option(null, _ui.Text("list.allrundowns"), _rundown == null);
        var unknown = _ui.Text("list.unknownrundown");
        foreach (var group in all.Where(e => e.Header != null).GroupBy(RundownLabel).OrderBy(g => g.Key == unknown).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.Key;
            var missing = group.All(e => RundownNames.Refusal(e) != null);
            Option(name, name == unknown ? Dim(name) : missing ? Warn(MenuText.Plain(name)) : MenuText.Plain(name), _rundown == name);
            if (missing) list.Tooltip(UIText.Plain(name), UIText.Plain(RundownNames.Refusal(group.First()) ?? name));
        }
    }

    // Over the list, the sort choice: start (newest first by default), length, result or rundown; the one in use lit with v or ^, the same again turns it
    // round. How many are kept is on the pointer over it rather than a line of its own.
    private static void SortRow(MenuScreen screen, MenuScreenWindow list)
    {
        list.Columns(new[] { 18f, 40f, 58f, 76f });
        var cells = new UIText[SortChoices.Length + 1];
        cells[0] = UIText.Key("list.sortby");
        var current = -1;
        for (var i = 0; i < SortChoices.Length; i++)
        {
            var on = SortChoices[i] == _sort;
            if (on) current = i + 1;
            // ASCII v and ^: the game's menu font has no arrow or triangle and would draw a box.
            cells[i + 1] = on ? UIText.Plain(_ui.Text(SortKeys[i]) + (_descending ? " v" : " ^")) : UIText.Key(SortKeys[i]);
        }
        list.AddHeader(column => { if (column >= 1) Sort(screen, SortChoices[column - 1]); }, current, cells);
        var limit = Math.Max(0, Plugin.DiskLimitMb.Value);
        list.Tooltip(UIText.Key("list.title"), limit > 0 ? UIText.Key("list.keeplimit", ReplayLibrary.Keep, limit) : UIText.Key("list.keep", ReplayLibrary.Keep));
    }

    /// <summary>The rundown as the list writes it: its name, or "unknown rundown" with no number.</summary>
    internal static string RundownLabel(ReplayEntry e) => RundownNames.Name(e) ?? _ui.Text("list.unknownrundown");

    /// <summary>A text of this mod's language file.</summary>
    internal static string Say(string key, params object[] args) => _ui.Text(key, args);

    // The game's menu colours for labels, failures and warnings; a value is tinted with the menu's own text colour.
    private static string Dim(string text) => UIKitColors.Tint(text, UIKitColors.Faded(UIKitColors.MenuText, 0.55f));
    private static string Value(string text) => UIKitColors.Tint(text, UIKitColors.MenuText);
    private static string Alert(string text) => UIKitColors.Tint(text, UIKitColors.Alert);
    private static string Warn(string text) => UIKitColors.Tint(text, UIKitColors.Warn);

    private static readonly string[] Sections = { "list.all", "list.starred", "list.cleared", "list.notcleared" };

    private static bool Shown(ReplayEntry e, int section, string? rundown) => section == Unreadable ? e.Header == null : e.Header != null
        && (rundown == null || RundownLabel(e) == rundown) && section switch
        {
            1 => e.Starred,
            2 => e.Outcome == ForgeReplay.Outcome.Success,
            3 => e.Outcome != ForgeReplay.Outcome.Success,
            _ => true,
        };

    // The recordings that can be read first, in the order chosen, then the ones that cannot (another format, damaged), newest first: they
    // only say a file is there. Within the same level, length or result the newest comes first.
    private static IEnumerable<ReplayEntry> Sorted(IEnumerable<ReplayEntry> entries)
    {
        var list = entries.ToList();
        var readable = list.Where(e => e.Header != null);
        IOrderedEnumerable<ReplayEntry> by<T>(Func<ReplayEntry, T> key) => _descending ? readable.OrderByDescending(key) : readable.OrderBy(key);
        var sorted = _sort switch
        {
            1 => by(e => RundownLabel(e)),
            4 => by(e => e.DurationMs),
            5 => by(e => (int)e.Outcome),
            _ => by(e => e.Start),
        };
        var ordered = _sort == StartColumn ? sorted : sorted.ThenByDescending(e => e.Start);
        return ordered.Concat(list.Where(e => e.Header == null).OrderByDescending(e => e.Start));
    }

    // A sort chosen: by it, or the other way round when it already was (time starts newest first, the others from the top).
    private static void Sort(MenuScreen screen, int column)
    {
        if (column == _sort) _descending = !_descending;
        else { _sort = column; _descending = column == StartColumn; }
        screen.Rebuild();
    }

    // A list entry, two lines and a third for the grades: "Rundown · A1" (in the warning colour when this game cannot play it, a star when starred) and how it
    // ended; when it started, how long, how many played and the team's kills and downs; then each member's grade in the player's colour, from the rating cache
    // (no recording is read for the row). A file that cannot be read says so and why under its start.
    private static readonly float[] TitleStops = { 74f }, InfoStops = { 30f, 48f, 64f }, GradeStops = { 25f, 50f, 75f };
    private static readonly int[] ResultRight = { 1 }, NoRight = Array.Empty<int>();

    private static List<MenuScreenWindow.MenuLine> Entry(ReplayEntry e)
    {
        var star = e.Starred ? Extra(_ui.Text("list.starmark")) + " " : "";
        if (e.Header == null)
            return new() { new(new[] { star + Dim(System.IO.Path.GetFileNameWithoutExtension(e.Path)), Alert(_ui.Text(ShortKey(e))) }, TitleStops, ResultRight), new(new[] { When(e) }, InfoStops, NoRight) };
        var title = RundownNames.Refusal(e) != null ? Warn(LevelName(e)) : LevelName(e);
        var people = e.Header.Players.Count <= 1 ? _ui.Text("list.solo") : _ui.Text("list.people", e.Header.Players.Count);
        var lines = new List<MenuScreenWindow.MenuLine>
        {
            new(new[] { star + title, Outcome(e) }, TitleStops, ResultRight),
            new(new[] { When(e), Duration(e), Dim(people), Counts(e) }, InfoStops, NoRight),
        };
        if (Grades(e) is { } grades) lines.Add(new(grades, GradeStops, NoRight));
        return lines;
    }

    // A member's overall grade for the team table, — when the rating cache has none.
    private static string Grade(ReplayEntry e, int slot)
        => RatingStore.Table is { } table && RatingStore.OfRecording(e.Path) is { } ratings && ratings[slot] is { Grade: >= 0 } r ? Value(table.Grades[r.Grade].Name) : Dim("—");

    // Each member's name and overall grade in the player's own colour; null when the rating cache has none for the recording.
    private static string[]? Grades(ReplayEntry e)
    {
        if (e.Header == null || RatingStore.Table is not { } table || RatingStore.OfRecording(e.Path) is not { } ratings) return null;
        var cells = new List<string>();
        for (var slot = 0; slot < 4; slot++)
            if (ratings[slot] is { } r) cells.Add(UIKitColors.Tint(MenuText.Plain(PlayerName.Plain(ReplayFeed.Holder(slot, uint.MaxValue, e.Header.Players, NoPresence, _ui.Text("player.slot", slot + 1)).Name)) + "  " + (r.Grade >= 0 ? table.Grades[r.Grade].Name : "—"), ReplayScreen.SlotColour(slot)));
        return cells.Count == 0 ? null : cells.ToArray();
    }

    // The members' ratings as text: the six dimension scores and the overall grade of each player, what the recording cannot say is — and a worked-out score is "about N".
    private static void RatingText(MenuScreenWindow details, ReplayEntry e)
    {
        if (e.Header is not { } header || RatingStore.Table is not { } table || RatingStore.OfRecording(e.Path) is not { } ratings) return;
        details.Columns(new[] { 20f, 30f, 40f, 50f, 60f, 70f, 80f }, 1, 2, 3, 4, 5, 6);
        var heads = new List<UIText> { UIText.Key("list.rating") };
        foreach (var dimension in RatingMetrics.Dimensions) heads.Add(UIText.Key("rating.dim." + dimension));
        heads.Add(UIText.Key("rating.total"));
        details.AddHeader(heads.ToArray());
        details.Tooltip(UIText.Key("list.rating"), UIText.Key("rating.legend"));
        for (var slot = 0; slot < 4; slot++)
        {
            if (ratings[slot] is not { } r) continue;
            var cells = new List<string> { SlotName(header, null, slot) };
            foreach (var d in r.Dimensions)
            {
                if (d.Score is not { } score) { cells.Add(Dim("—")); continue; }
                var text = ((int)Math.Round(score * 100)).ToString(CultureInfo.InvariantCulture);
                cells.Add(d.Estimated ? Warn(_ui.Text("estimate", text)) : Value(text));
            }
            cells.Add(r.Grade < 0 ? Dim(_ui.Text("rating.fewdims"))
                : Value(table.Grades[r.Grade].Name + (r.Score is { } total ? "  " + ((int)Math.Round(total * 100)).ToString(CultureInfo.InvariantCulture) : "")));
            details.AddRow(cells.ToArray());
        }
    }

    // Why a file cannot be read: another recording format, or damaged.
    private static string ShortKey(ReplayEntry e) => e.OtherFormat ? "short.format" : "short.damaged";

    private static string When(ReplayEntry e) => e.Start == default ? Dim("—") : Day(e.Start);

    // Today and yesterday by name, other days by month and day.
    private static string Day(DateTime at)
    {
        var time = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (DateTime.Now.Date - at.Date).Days;
        return days == 0 ? _ui.Text("list.today", time) : days == 1 ? _ui.Text("list.yesterday", time) : at.ToString("MM-dd", CultureInfo.InvariantCulture) + " " + time;
    }

    // The team's kills and downs from the finished recording's footer (no reading of the file); kills recorded by a client are partly worked
    // out unless the recorder played alone.
    private static string Counts(ReplayEntry e)
    {
        if (e.Summary == null || e.Header == null) return Dim("—");
        int kills = 0, downs = 0;
        var client = !e.Header.RecorderIsHost && e.Header.Players.Count > 1;
        if (client)
        {
            // A client's kills are settled by the whole read (hits matched to the host's health), not by the footer; the row waits for it.
            if (!Totals.TryGetValue(e.Path, out var read)) { RatingOnly.Remove(e.Path); Wanted.TryAdd(e.Path, e.Header); return Dim("—"); }
            (kills, downs) = read;
        }
        else foreach (var p in e.Summary.Players) { kills += p.Kills; downs += p.Downs; }
        var k = kills.ToString(CultureInfo.InvariantCulture);
        if (client && kills > 0) k = Warn(_ui.Text("estimate", k));
        return _ui.Text("list.counts", k, downs.ToString(CultureInfo.InvariantCulture));
    }

    private static string Duration(ReplayEntry e) => e.Header == null ? Dim("—") : TimelineMath.Clock(e.DurationMs);

    // One result column: how the level ended, that the recording stopped before it did, or that the file cannot be read at all (then
    // nothing of it is known: not unfinished).
    private static string Outcome(ReplayEntry e) => e.Header == null ? Dim(_ui.Text("list.unreadable")) : e.Outcome switch
    {
        ForgeReplay.Outcome.Success => _ui.Text("list.success"),
        ForgeReplay.Outcome.Failed => Alert(_ui.Text("list.failed")),
        ForgeReplay.Outcome.Left => Dim(_ui.Text("list.left")),
        _ => e.Complete ? Dim(_ui.Text("list.none")) : Warn(_ui.Text("list.unfinished")),
    };

    /// <summary>The chosen recording: why it cannot play when it cannot; the three pages, chosen on a row in the window itself (this run: how it went, the team and the
    /// run in a few lines; the estimates; the file: size, build, how complete, the mods); and its buttons right under the window, play first and dim when it cannot
    /// play, delete last.</summary>
    private static void Details(MenuScreen screen, ReplayEntry e)
    {
        var details = screen.AddWindow(UIText.Plain(LevelName(e) + "  " + (e.Header == null ? Alert(_ui.Text(ShortKey(e))) : Outcome(e))), DetailWidth, UnderDetails, fit: true);
        // Why it cannot play first, in a few words: it is what the player came to the window to learn; the sentence is on the pointer over it.
        var refusal = e.Header == null ? null : RundownNames.Refusal(e);
        if (e.Header == null)
        {
            details.AddRow(UIText.Plain(Alert(_ui.Text("list.unplayablewhy", _ui.Text(ShortKey(e)))) + Dim("  (i)")));
            details.Tooltip(UIText.Key("list.unplayable"), UIText.Key(e.OtherFormat ? "refuse.format" : "refuse.damaged"));
            details.AddField(UIText.Key("list.start"), When(e));
            details.AddField(UIText.Key("list.size"), Size(e.Path));
        }
        else
        {
            if (refusal != null)
            {
                details.AddRow(UIText.Plain(Warn(MenuText.Plain(refusal)) + Dim("  (i)")));
                details.Tooltip(UIText.Key("list.unplayable"), UIText.Key("missing.hint"));
            }
            else if (!CanStart()) details.AddRow(UIText.Plain(Warn(_ui.Text(Replayer.Starting ? "list.starting" : "list.inlevel"))));
            // The estimates and the file are pages of their own, opened from More; a row on top leads back to the run.
            if (_page != 0) details.AddRow(UIText.Plain(Extra(_ui.Text("list.back")) + "   " + Dim(_ui.Text(_page == 1 ? "list.estimates" : "list.pagefile"))), () => { _page = 0; screen.Rebuild(); });
            if (_page == 0) RunPage(details, e);
            else if (_page == 1) { Estimates(details, e); RatingText(details, e); }
            else FilePage(details, e);
        }
        // Play (red, the largest) and star; the rest behind More: the estimates, the file, export, show in folder and delete.
        details.Buttons(1.25f, 1f, 1f);
        if (e.Header == null)
        {
            details.AddButton(UIText.Key("list.folder"), () => Reveal(e.Path), false, 1);
            DeleteButton(screen, details, e, 2);
            return;
        }
        // A replay is its own level, started from the lobby or the main menu; with others in the lobby the button asks first, in place: playing leaves the lobby.
        var ready = refusal == null && CanStart();
        if (ready && OthersInLobby()) details.AddConfirmButton("play:" + e.Path, UIText.Key("list.play"), UIText.Key("list.leaveplay"), UIText.Key("list.cancel"), () => Choose(e), true);
        else details.AddButton(UIText.Key("list.play"), () => Choose(e), true, 0, ready);
        details.AddButton(UIText.Key(e.Starred ? "list.unstar" : "list.dostar"), () => Star(screen, e), false, 1);
        details.AddButton(UIText.Plain(_ui.Text("list.more") + (_more ? " ^" : " v")), () => { _more = !_more; screen.Rebuild(); }, false, 2);
        if (!_more) return;
        details.AddButton(UIText.Key("list.estimates"), () => { _page = 1; screen.Rebuild(); }, false, 1);
        details.AddButton(UIText.Key("list.pagefile"), () => { _page = 2; screen.Rebuild(); }, false, 2);
        details.AddButton(UIText.Key("list.export"), () => Export(screen, e), false, 1);
        details.AddButton(UIText.Key("list.folder"), () => Reveal(e.Path), false, 2);
        DeleteButton(screen, details, e, 1);
    }

    private static void DeleteButton(MenuScreen screen, MenuScreenWindow details, ReplayEntry e, int column)
        => details.AddConfirmButton("delete:" + e.Path, UIText.Key("list.delete"), UIText.Key("list.deleteconfirm"), UIText.Key("list.cancel"), () => Delete(screen, e), false, column);

    // The run: one line of when it started, how long it took and who recorded it (host or client); the team table; the fiercest wave and the most dangerous moment.
    private static void RunPage(MenuScreenWindow details, ReplayEntry e)
    {
        void Line(string key, string value) => details.AddField(UIText.Key(key), value);
        var recorder = string.IsNullOrEmpty(e.Header!.RecorderName) ? Dim("—") : PlayerName.Rich(e.Header.RecorderName) + "  " + Dim(_ui.Text(e.Header.RecorderIsHost ? "list.host" : "list.client"));
        details.Columns(new[] { 34f, 56f });
        details.AddRow(new[] { When(e), Duration(e), recorder });
        Players(details, e);
        var report = Summary(e)?.Report;
        if (report != null)
        {
            var wave = report.FiercestWave >= 0 ? report.Waves[report.FiercestWave] : null;
            Line("sum.fiercest", wave == null ? Dim("—") : _ui.Text("sum.wavetext", TimelineMath.Clock(wave.StartMs), wave.Spawned, (int)MathF.Round((wave.EndMs - wave.StartMs) / 1000f)));
            Line("sum.danger", report.Danger(out var at, out var hunters) ? _ui.Text("sum.dangertext", hunters, TimelineMath.Clock(at)) : Dim("—"));
        }
    }

    private static void FilePage(MenuScreenWindow details, ReplayEntry e)
    {
        var header = e.Header!;
        details.AddField(UIText.Key("list.size"), Size(e.Path));
        details.AddField(UIText.Key("list.build"), header.GameRevision.ToString(CultureInfo.InvariantCulture)
            + (header.GameRevision == Version.Revision ? "" : "  " + Warn(_ui.Text("list.thisbuild", Version.Revision))));
        var analysis = Summary(e);
        if (e.Summary?.Health is { } h)
            details.AddParagraph(UIText.Plain(_ui.Text("sum.complete") + ": " + _ui.Text("sum.completetext", h.LostSamples, h.LostEvents, h.HookFailures, h.MaxQueue, h.Hitches, h.Percentile(0.5).ToString("0", CultureInfo.InvariantCulture), h.Percentile(0.95).ToString("0", CultureInfo.InvariantCulture), analysis?.Report?.SkippedEvents ?? 0)));
        if (analysis?.ReportFailure is { } failure) details.AddParagraph(UIText.Plain(_ui.Text("sum.reportfailtext", failure)));
        Content(details, header);
        Mods(details, header);
    }

    // The run's statistics as JSON beside the recording (name.stats.forge), for balancing; the folder opens with the file selected and the path is said.
    private static void Export(MenuScreen screen, ReplayEntry e)
    {
        var analysis = Summary(e);
        if (analysis == null)
        {
            // Still being read: the button only has to be pressed again; for good: the file cannot be read.
            screen.Toast(UIText.Key(IsReading(e.Path) ? "list.reading" : "export.failed"), NoticeTone.Warn, DoneSeconds);
            return;
        }
        try
        {
            var path = System.IO.Path.GetFullPath(Write(e, analysis));
            Reveal(path);
            screen.Toast(UIText.Key("export.done", path), NoticeTone.Info, DoneSeconds);
        }
        catch (Exception error)
        {
            Plugin.Logger.Error("Exporting the statistics of " + e.Path + " failed: " + error);
            screen.Toast(UIText.Key("export.failed"), NoticeTone.Alert, ProblemSeconds, UIText.Key("list.viewlog"), ViewLog);
        }
    }

    private static string Write(ReplayEntry e, Analysis analysis)
    {
        var path = StatsExport.PathOf(e.Path);
        ForgeCore.Store.WriteText(path, StatsExport.Json(analysis, e.Header!, e.DurationMs, Highlights.Pick(analysis, Replayer.Boss), e.Summary));
        return path;
    }

    /// <summary>The Export button for a script: the statistics of <paramref name="e"/> written beside it, or why not yet (the read of the recording
    /// is still on its way: ask again). One line. With <paramref name="reveal"/> the folder opens as it does for the button.</summary>
    internal static string ExportStats(ReplayEntry e, bool reveal = false)
    {
        PollSummaries();
        var analysis = Summary(e);
        if (analysis == null) return Summaries.ContainsKey(e.Path) ? "export failed: " + System.IO.Path.GetFileName(e.Path) + " cannot be read" : "reading " + System.IO.Path.GetFileName(e.Path) + ", ask again";
        try
        {
            var path = Write(e, analysis);
            if (reveal) Reveal(path);
            return "exported " + System.IO.Path.GetFullPath(path) + " bytes=" + new System.IO.FileInfo(path).Length;
        }
        catch (Exception error)
        {
            Plugin.Logger.Error("Exporting the statistics of " + e.Path + " failed: " + error.Message);
            return "export failed: " + error.Message;
        }
    }

    // Confirmed on its button; the recording being written now cannot go.
    private static void Delete(MenuScreen screen, ReplayEntry entry)
    {
        if (string.Equals(entry.Path, Session.FilePath, StringComparison.OrdinalIgnoreCase)) { screen.Toast(UIText.Key("delete.recording"), NoticeTone.Warn); return; }
        try
        {
            ReplayLibrary.Delete(entry.Path);
            if (_chosen == entry.Path) _chosen = null;
            Forget(entry.Path);
        }
        catch (Exception error)
        {
            Plugin.Logger.Error("Deleting " + entry.Path + " failed: " + error);
            screen.Toast(UIText.Key("delete.failed"), NoticeTone.Alert, ProblemSeconds, UIText.Key("list.viewlog"), ViewLog);
        }
        screen.Rebuild();
    }

    // A file in the system's file manager, selected: to copy a recording out and share it, or to read what was exported. Its folder when the file manager cannot select.
    private static void Reveal(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + full + "\""); }
        catch (Exception error)
        {
            Plugin.Logger.Error("Selecting " + full + " in the file manager failed: " + error.Message);
            Application.OpenURL(new Uri(System.IO.Path.GetDirectoryName(full)!).AbsoluteUri);
        }
    }

    private static void ViewLog() => Reveal(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"));

    private static string Size(string path)
    {
        try { return Bytes(new System.IO.FileInfo(path).Length); }
        catch (Exception) { return "—"; } // gone or locked since the list was read: no size, nothing else is wrong
    }

    private static string Bytes(long bytes) => bytes >= 1 << 20 ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

    /// <summary>The team as a table, read from the recording once per file (<see cref="Analysis"/>): each player (who joined later included) with damage
    /// dealt, kills, downs and times revived, then the team's sums. What the recording cannot say is — and what was worked out rather than settled by the game
    /// is ~, as on the statistics panel; why is on the pointer over the header.</summary>
    private static void Players(MenuScreenWindow details, ReplayEntry e)
    {
        var header = e.Header!;
        var analysis = Summary(e);
        var slots = Slots(header, analysis);
        details.Columns(new[] { 32f, 51f, 68f, 84f }, 1, 2, 3, 4);
        var rows = new List<string[]>();
        var guessed = false;
        int kills = 0, downs = 0; float damage = 0;
        var players = analysis?.Players ?? e.Summary?.Players;
        string Whole(float value) => ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture);
        foreach (var slot in slots)
        {
            if (players != null && slot < players.Length)
            {
                var p = players[slot];
                kills += p.Kills; downs += p.Downs; damage += p.BulletDamage + p.MeleeDamage + p.ExplosionDamage;
            }
            if (analysis == null)
            {
                // The footer already holds the team's numbers; they are shown while the whole read is on its way.
                if (e.Summary is { } footer && slot < footer.Players.Length && IsReading(e.Path))
                {
                    var p = footer.Players[slot];
                    var killed = header.RecorderIsHost || header.Players.Count == 1 ? p.Kills.ToString(CultureInfo.InvariantCulture) : Dim("—");
                    // A client's footer holds only the recorder's damage: another player's is worked out by the read on its way.
                    var dealt = header.RecorderIsHost || slot == header.RecorderSlot ? Whole(p.BulletDamage + p.MeleeDamage + p.ExplosionDamage) : Dim("—");
                    rows.Add(new[] { SlotName(header, analysis, slot), dealt, killed, Whole(p.Downs), Grade(e, slot) });
                }
                else rows.Add(new[] { SlotName(header, analysis, slot), Dim("—"), Dim("—"), Dim("—"), Grade(e, slot) });
                continue;
            }
            var s = analysis.Players[slot];
            string Settled(StatRow row) { var cell = ReplayFeed.Cell(row, s, analysis, slot, 0f); guessed |= cell.Quality == StatQuality.Estimate; return Cell(cell); }
            rows.Add(new[] { SlotName(header, analysis, slot), Settled(StatRow.Damage), Settled(StatRow.Kills), Settled(StatRow.Downs), Grade(e, slot) });
        }
        details.AddHeader(UIText.Plain(_ui.Text("list.team") + (guessed ? "  (i)" : "")), UIText.Key("list.damage"), UIText.Key("stats.kills"), UIText.Key("stats.downs"), UIText.Key("list.rating"));
        if (guessed) details.Tooltip(UIText.Key("list.team"), UIText.Plain(EstimateWhy(true, false)));
        foreach (var row in rows) details.AddRow(row);
        // The team's damage says what it is: about N with anyone's worked out, — with anyone's unknown, never the sum of whom the recording happens to show.
        var teamDamage = analysis != null ? Cell(ReplayFeed.TeamDamage(analysis.Players, analysis, slots)) : header.RecorderIsHost ? Whole(damage) : Dim("—");
        if (players != null && slots.Count > 1) details.AddRow(new[] { Dim(_ui.Text("sum.team")), teamDamage, kills.ToString(CultureInfo.InvariantCulture), downs.ToString(CultureInfo.InvariantCulture), "" });
        if (analysis == null && IsReading(e.Path)) details.AddRow(UIText.Plain(Dim(_ui.Text("list.reading"))));
    }

    private static List<int> Slots(ReplayHeader header, Analysis? analysis)
    {
        var slots = new List<int>();
        for (var slot = 0; slot < 4; slot++) if (ReplayFeed.EverHeld(slot, header.Players, analysis?.Presence ?? NoPresence)) slots.Add(slot);
        return slots;
    }

    private static string SlotName(ReplayHeader header, Analysis? analysis, int slot)
        => PlayerName.Rich(ReplayFeed.Holder(slot, uint.MaxValue, header.Players, analysis?.Presence ?? NoPresence, _ui.Text("player.slot", slot + 1)).Name);

    /// <summary>The estimates of PLAN 5.6 (damage taken off the health bar, revives given, packs, supplies received, enemies woken, mines, tongues dodged), the
    /// players as columns.</summary>
    private static void Estimates(MenuScreenWindow details, ReplayEntry e)
    {
        var header = e.Header!;
        var analysis = Summary(e);
        if (analysis == null) { details.AddRow(UIText.Plain(Dim(_ui.Text(IsReading(e.Path) ? "list.reading" : "list.unreadable")))); return; }
        var slots = Slots(header, analysis);
        if (slots.Count == 0) return;
        var guessed = false;
        var fullHealth = ReplayScreen.FullHealth();
        var rows = new List<string[]>();
        foreach (var row in ReplayFeed.InferredRows)
        {
            var cells = new string[slots.Count + 1];
            cells[0] = _ui.Text(ReplayFeed.Key(row));
            for (var i = 0; i < slots.Count; i++)
            {
                var cell = ReplayFeed.Cell(row, analysis.Players[slots[i]], analysis, slots[i], fullHealth);
                guessed |= cell.Quality == StatQuality.Estimate;
                cells[i + 1] = Cell(cell);
            }
            rows.Add(cells);
        }
        var stops = new float[slots.Count];
        var right = new int[slots.Count];
        for (var i = 0; i < slots.Count; i++) { stops[i] = 32f + 68f * i / slots.Count; right[i] = i + 1; }
        details.Columns(stops, right);
        var names = new UIText[slots.Count + 1];
        names[0] = UIText.Plain(guessed ? "(i)" : " ");
        for (var i = 0; i < slots.Count; i++) names[i + 1] = UIText.Plain(SlotName(header, analysis, slots[i]));
        details.AddHeader(names);
        // Why some numbers are guesses, on the pointer over the heading; nothing when every number is exact.
        if (guessed) details.Tooltip(UIText.Key("list.estimates"), UIText.Plain(EstimateWhy(false, true)));
        foreach (var cells in rows) details.AddRow(cells);
    }

    /// <summary>Why the ~ numbers are guesses, in at most two short lines: the kills and damage a client's recording cannot settle, and the
    /// rest worked out from who was nearby; empty when nothing is a guess. Also the statistics panel's info icon.</summary>
    internal static string EstimateWhy(bool guessedKills, bool guessedRest)
        => guessedKills && guessedRest ? _ui.Text("estimate.client") + "\n" + _ui.Text("estimate.nearby")
            : guessedKills ? _ui.Text("estimate.client") : guessedRest ? _ui.Text("estimate.nearby") : "";

    // A number as on the statistics panel: exact in the menu's text colour, ~ estimated in the warning colour, — empty dimmed.
    private static string Cell(StatCell cell) => cell.Quality switch
    {
        StatQuality.Estimate => Warn(_ui.Text("estimate", cell.Text)),
        StatQuality.Empty => Dim(cell.Text),
        _ => Value(cell.Text),
    };

    private static readonly List<PresenceLine> NoPresence = new();

    // A recording read through once for its tables: the counts (equal to the footer's for a finished recording), the estimates of PLAN 5.5 and 5.6, and who
    // joined. The read runs on a worker thread (the one the replay uses to open a recording), one at a time and the chosen recording's first: choosing another
    // drops a read nobody waits for. Until it is in this is null and the details show the footer's numbers, and the screen is built again when it arrives.
    // Only the last few whole reads are kept (KeptReads), and they go when the list is left. Null for good when the file cannot be read.
    private static Analysis? Summary(ReplayEntry e)
    {
        if (Summaries.TryGetValue(e.Path, out var known)) { Touch(e.Path); return known; }
        if (_reading?.Path != e.Path) StartRead(e.Path, e.Header!, keep: true, e.Complete);
        return null;
    }

    private sealed class Reading
    {
        internal string Path = "";
        internal bool Keep, Complete;
        internal ReplayHeader Header = null!;
        internal System.Threading.Tasks.Task<Analysis> Task = null!;
        internal System.Threading.CancellationTokenSource Cancel = null!;
    }

    private static Reading? _reading;

    private static bool IsReading(string path) => _reading?.Path == path;

    // Starts the read of a recording, dropping the one on its way for another.
    private static void StartRead(string path, ReplayHeader header, bool keep, bool complete = true)
    {
        Stop();
        try
        {
            var cancel = new System.Threading.CancellationTokenSource();
            _reading = new Reading { Path = path, Keep = keep, Complete = complete, Header = header, Cancel = cancel, Task = Replayer.Analyse(path, header, cancel.Token) };
        }
        catch (Exception error) { Plugin.Logger.Error("The recording " + System.IO.Path.GetFileName(path) + " could not be read for its details: " + error.Message); Summaries[path] = null; }
    }

    private static void Stop()
    {
        if (_reading == null) return;
        _reading.Cancel.Cancel();
        _reading = null;
    }

    private static void Touch(string path)
    {
        Recent.Remove(path); Recent.Add(path);
        while (Recent.Count > KeptReads) { Summaries.Remove(Recent[0]); Recent.RemoveAt(0); }
    }

    // A recording is gone or renamed: what was read of it goes, or follows it.
    private static void Forget(string path)
    {
        if (_reading?.Path == path) Stop();
        Summaries.Remove(path); Recent.Remove(path); Totals.Remove(path); Wanted.Remove(path); RatingOnly.Remove(path);
    }

    private static void Moved(string from, string to)
    {
        if (_reading?.Path == from) Stop();
        if (Summaries.Remove(from, out var analysis)) { Summaries[to] = analysis; Recent.Remove(from); Recent.Add(to); }
        if (Totals.Remove(from, out var totals)) Totals[to] = totals;
        Wanted.Remove(from); RatingOnly.Remove(from);
    }

    // Nothing of the reads stays held (the totals the rows show are small and stay).
    private static void Release()
    {
        Stop();
        Summaries.Clear(); Recent.Clear(); Wanted.Clear(); RatingOnly.Clear();
    }

    private static MenuScreen? _built;
    private static float _lastBuilt;
    private const float IdleReleaseSeconds = 60f;

    // The read that finished: kept when the chosen recording waits for it (its totals always), and the screen built again when a row or the details were waiting.
    private static void PollSummaries()
    {
        // The list left (a level or a replay on the way) or shut a while ago: nothing of the reads stays held.
        if ((_reading != null || Summaries.Count > 0 || Wanted.Count > 0) && (!CanStart() || ForgeCore.Frame.UnscaledTime - _lastBuilt > IdleReleaseSeconds)) Release();
        if (_reading == null)
        {
            if (Wanted.Count > 0 && !Replayer.Starting)
            {
                var (path, header) = Wanted.First();
                Wanted.Remove(path);
                StartRead(path, header, keep: false);
            }
            else if (Wanted.Count == 0) RatingStore.Flush();
            return;
        }
        var done = _reading;
        if (!done.Task.IsCompleted) return;
        _reading = null;
        if (done.Task.IsCanceled) return;
        if (done.Task.IsFaulted) { Plugin.Logger.Error("The recording " + System.IO.Path.GetFileName(done.Path) + " could not be read for its details: " + done.Task.Exception?.GetBaseException().Message); Summaries[done.Path] = null; RatingStore.Skip(done.Path); _built?.Rebuild(); return; }
        var analysis = done.Task.Result;
        int kills = 0, downs = 0;
        foreach (var p in analysis.Players) { kills += p.Kills; downs += p.Downs; }
        Totals[done.Path] = (kills, downs);
        // A finished recording's members join the member rating cache (PLAN 6); the file is written once the reads stop.
        if (done.Complete) RatingStore.Record(done.Path, done.Header, analysis);
        if (done.Keep) { Summaries[done.Path] = analysis; Touch(done.Path); }
        // A read only the member rating asked for changes nothing on screen.
        if (done.Keep || !RatingOnly.Remove(done.Path)) _built?.Rebuild();
    }

    /// <summary>The rundown's name and the level as players call it, for a title: "Name · A1". Never the rundown's number.</summary>
    internal static string LevelName(ReplayEntry e) => MenuText.Plain(RundownLabel(e)) + (RundownNames.Code(e) is { } code ? " · " + code : "");

    /// <summary>Filters and a chosen recording for a script, as the clicks do: the section (0 all, 1 starred, 2 cleared, 3 not cleared, 4 the files that cannot be read), a rundown's name
    /// ("" all), a recording's file name ("" keeps) and the page (0 run, 1 estimates, 2 file).</summary>
    internal static void Show(int section, string rundown, string file, int page)
    {
        _section = section; _rundown = rundown.Length > 0 ? rundown : null; _page = page;
        if (file.Length > 0) _chosen = ReplayLibrary.List(Plugin.ReplayFolder).FirstOrDefault(e => System.IO.Path.GetFileName(e.Path) == file)?.Path ?? _chosen;
        _built?.Rebuild();
    }

    private static void Star(MenuScreen screen, ReplayEntry entry)
    {
        try
        {
            var moved = ReplayShelf.Star(entry, !entry.Starred, Plugin.ReplayFolder, Session.FilePath);
            if (moved != null) { _chosen = moved; Moved(entry.Path, moved); }
        }
        catch (Exception error)
        {
            Plugin.Logger.Error("Starring " + entry.Path + " failed: " + error);
            screen.Toast(UIText.Key("star.failed"), NoticeTone.Alert, ProblemSeconds, UIText.Key("list.viewlog"), ViewLog);
        }
        screen.Rebuild();
    }

    // Only out of a level: on the main menu or in a lobby, with no replay playing or on its way.
    private static bool CanStart() => !Replayer.Active && !Replayer.Starting
        && GameStateManager.CurrentStateName is eGameStateName.NoLobby or eGameStateName.Lobby;

    private static bool OthersInLobby() => SNet.IsInLobby && SNet.LobbyPlayers != null && SNet.LobbyPlayers.Count > 1;

    private static void Choose(ReplayEntry entry)
    {
        // Already on the way to a level: a second press must not leave the lobby that start is using.
        if (CanStart()) Play(entry.Path);
    }

    /// <summary>Opens the list as its menu button does; false when the menu cannot show it now.</summary>
    internal static bool Open() => _screen != null && _screen.Open();

    /// <summary>Plays the recording: at once from the menu, or after leaving the lobby (the solo drop starts only with no lobby open).</summary>
    internal static void Play(string path)
    {
        // A level this game does not have fails here, before the lobby is left for it.
        if (ReplayLibrary.List(Plugin.ReplayFolder).FirstOrDefault(e => e.Path == path) is { Header: not null } entry && RundownNames.Refusal(entry) != null) { Replayer.Start(path); return; }
        if (!SNet.IsInLobby && SNet.LocalPlayer?.IsInSessionHub != true) { Replayer.Start(path); return; }
        _waitingFor = path;
        try { SNet.SessionHub.LeaveHub(); }
        catch (Exception error) { _waitingFor = null; Plugin.Logger.Error("Leaving the lobby for a replay failed: " + error.Message); }
    }

    /// <summary>Every plugin the recording's header lists, a row each with its name (the GUID when it is not installed here) and version,
    /// sorted by name; then the ones installed here that the recording does not list. Against this game: recorded only in the alert colour,
    /// another version in the warning colour with the version here, installed here only in the menu's highlight colour, each also said in words.
    /// The window scrolls when they are many.</summary>
    private static void Mods(MenuScreenWindow details, ReplayHeader header)
    {
        var diff = ModDifference.Of(header.Plugins, Session.PluginsHere());
        var onlyRecorded = new HashSet<string>(diff.OnlyRecorded, StringComparer.Ordinal);
        var other = diff.OtherVersion.ToDictionary(v => v.Guid, v => v.Here, StringComparer.Ordinal);
        var here = Session.PluginsHere().ToDictionary(p => p.Guid, p => p.Version, StringComparer.Ordinal);
        details.Columns(new[] { 52f, 70f });
        details.AddHeader(UIText.Key("list.mods", header.Plugins.Count), UIText.Key("list.modversion"), UIText.Key("list.modnote"));
        foreach (var p in header.Plugins.OrderBy(p => ModName(p.Guid), StringComparer.OrdinalIgnoreCase))
        {
            var name = MenuText.Plain(ModName(p.Guid));
            if (onlyRecorded.Contains(p.Guid)) details.AddRow(new[] { Alert(name), Alert(p.Version), Alert(_ui.Text("mod.nothere")) });
            else if (other.TryGetValue(p.Guid, out var v)) details.AddRow(new[] { Warn(name), Warn(p.Version), Warn(_ui.Text("mod.otherversion", v)) });
            else details.AddRow(new[] { name, p.Version, "" });
        }
        foreach (var guid in diff.OnlyHere)
        {
            var name = MenuText.Plain(ModName(guid));
            details.AddRow(new[] { Extra(name), Extra(here.TryGetValue(guid, out var v) ? v : "—"), Extra(_ui.Text("mod.onlyhere")) });
        }
    }

    /// <summary>What the recording's content packages (the ones that give enemies, weapons and the like their look) did differently from this
    /// game: which are not installed here, which have other files; one line saying all match when none differ. Nothing for a recording made
    /// before the fingerprints were written. Without the package an enemy shows in its vanilla look: this says which to install.</summary>
    private static void Content(MenuScreenWindow details, ReplayHeader header)
    {
        if (header.Content.Count == 0) return;
        var diff = ContentDifference.Of(header.Content, Session.ContentHere());
        if (!diff.Any) { details.AddField(UIText.Key("list.content"), _ui.Text("content.same")); return; }
        if (diff.Missing.Count > 0) details.AddField(UIText.Key("list.content"), Alert(_ui.Text("content.missing", string.Join("、", diff.Missing))));
        if (diff.Changed.Count > 0) details.AddField(UIText.Key("list.content"), Warn(_ui.Text("content.changed", string.Join("、", diff.Changed))));
    }

    private static string Extra(string text) => UIKitColors.Tint(text, UIKitColors.MenuOver);

    // A plugin by the name it gives itself when it is installed here, else by its GUID (a recording keeps only that).
    private static string ModName(string guid)
        => BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.TryGetValue(guid, out var info) && !string.IsNullOrEmpty(info.Metadata.Name) ? info.Metadata.Name : guid;
}
