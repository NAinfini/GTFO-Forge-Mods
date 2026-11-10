using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeCore;
using ForgeUI;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The statistics panel's pages (PLAN 2.2): the team table that follows the clock, then the whole run by weapon, by enemy type, by
/// resource and by level. Worked out once per analysis from <see cref="RunReport"/>; exact, worked out (about N) and empty (—) are told apart
/// as on the team table.</summary>
internal static partial class ReplayScreen
{
    private enum StatsPage { Team, Rating, Weapons, Enemies, Resources, Level }

    private static StatsPage _page;

    private sealed class PageRow { internal string Label = ""; internal StatCell[] Cells = Array.Empty<StatCell>(); }

    private static readonly HudTable TeamTable = new(), WeaponTable = new(), EnemyTable = new(), ResourceTable = new(), LevelTable = new();
    // Per player: whole-run rows of the team table (friendly fire, downs), by their language key.
    private static readonly Dictionary<string, PageRow> TeamExtra = new();

    private static StatCell Exact(string text) => new(text, StatQuality.Exact);
    private static StatCell Guess(string text) => new(_ui.Text("estimate", text), StatQuality.Estimate);
    private static readonly StatCell None = new("—", StatQuality.Empty);
    private static string Whole(float value) => ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture);
    private static string Seconds(float ms) => (ms / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static Color Dim => Fade(UIKitColors.GameText, 0.85f);
    private static Color Head => Fade(UIKitColors.GameText, 0.8f);
    private static Color Group => UIKitColors.Accent;

    // A guess of a count: a nothing is simply 0, not "about 0".
    private static StatCell GuessCount(int count) => count == 0 ? Exact("0") : Guess(count.ToString(CultureInfo.InvariantCulture));

    private static void BuildPages(Analysis analysis)
    {
        TeamExtra.Clear();
        foreach (var table in new[] { WeaponTable, EnemyTable, ResourceTable, LevelTable, TeamTable }) { table.Clear(); table.First = 0; }
        var r = analysis.Report;
        if (r == null) return;
        var full = FullHealth();
        string Who(int slot, uint at = uint.MaxValue) => PlayerName.Plain(HolderAt(analysis, slot, at).Name);
        var slots = Enumerable.Range(0, 4).Where(s => EverHeld[s]).ToList();
        StatCell[] PerSlot(Func<int, StatCell> cell) { var cells = new StatCell[4]; for (var s = 0; s < 4; s++) cells[s] = EverHeld[s] ? cell(s) : None; return cells; }
        void Extra(string key, Func<int, StatCell> cell) => TeamExtra[key] = new PageRow { Label = _ui.Text(key), Cells = PerSlot(cell) };

        // Team page, beside the numbers that follow the clock.
        Extra("stats.friendgave", s =>
        {
            float dmg = 0; for (var v = 0; v < 4; v++) dmg += r.FriendlyDamage[v, s];
            if (dmg > 0f) return Exact(Whole(dmg));
            return r.FriendlyShots[s] > 0 ? Guess(_ui.Text("stats.shotsn", r.FriendlyShots[s])) : r.FriendlyFireKnown ? Exact("0") : None;
        });
        Extra("stats.friendtook", s => { if (!r.HurtKnown[s]) return None; float dmg = 0; for (var a = 0; a < 4; a++) dmg += r.FriendlyDamage[s, a]; return Exact(Whole(dmg)); });
        Extra("stats.downtime", s => Exact(Seconds(r.DownMsOf[s])));
        Extra("stats.rescuetime", s => r.RescuedOf[s] == 0 ? None : Exact(Seconds(r.RescueMsOf[s] / (float)r.RescuedOf[s])));
        Extra("stats.downcause", s =>
        {
            var mine = r.Downs.Where(d => d.Slot == s).ToList();
            if (mine.Count == 0) return Exact("0");
            var top = mine.GroupBy(d => (d.Cause, d.EnemyDataId, d.Grab)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Cause).First();
            var text = CauseText(top.Key.Cause, top.Key.Grab, top.Key.EnemyDataId, top.First().Attacker, analysis);
            return mine.Exists(d => d.Estimated) ? Guess(text) : Exact(text);
        });

        // Weapons: who, what, then the numbers; a player's name only on their first row.
        WeaponTable.SetColumns(new[] { _ui.Text("wp.player"), _ui.Text("wp.weapon"), _ui.Text("wp.shots"), _ui.Text("wp.hits"), _ui.Text("wp.weak"), _ui.Text("wp.damage"), _ui.Text("wp.kills"), _ui.Text("wp.aim") }, Head);
        WeaponTable.SetAlign(1, HudAlign.Left);
        foreach (var slot in slots)
        {
            var firstRow = true;
            foreach (var w in r.WeaponsOfSlot(slot))
            {
                if (w.Shots == 0 && w.Damage <= 0f && w.Kills == 0) continue;
                var sentry = w.Wielded == WeaponKey.Sentry; var mine = w.Wielded == WeaponKey.Explosion;
                var shootsRays = !sentry && !mine && w.Wielded >= 0 && w.Shots > 0;
                var hitText = !shootsRays ? None : Cell(slot == analysis.RecorderSlot, w.Hits + "/" + w.Shots + "  " + (100f * w.Hits / w.Shots).ToString("0", CultureInfo.InvariantCulture) + "%");
                WeaponTable.AddRow(new TableCell(firstRow ? Who(slot) : "", SlotColour(slot), true), new TableCell(WeaponName(analysis, slot, w.Wielded), UIKitColors.GameText),
                    StatTableCell(w.Shots == 0 ? None : Exact(w.Shots.ToString(CultureInfo.InvariantCulture))), StatTableCell(hitText),
                    StatTableCell(sentry || mine || !analysis.HitsKnown(slot) ? None : Exact(w.Weak.ToString(CultureInfo.InvariantCulture))),
                    StatTableCell(!analysis.DamageKnown(slot) ? None : Cell(!w.Estimated, Whole(w.Damage))),
                    StatTableCell(!analysis.KillsKnown(slot) ? None : Cell(!w.Estimated, w.Kills.ToString(CultureInfo.InvariantCulture))),
                    StatTableCell(w.AimKnown && w.Shots > 0 ? Exact((100f * w.AimedShots / w.Shots).ToString("0", CultureInfo.InvariantCulture) + "%") : None));
                firstRow = false;
            }
        }

        // Enemy types.
        EnemyTable.SetColumns(new[] { _ui.Text("en.enemy"), _ui.Text("en.appeared"), _ui.Text("en.killed"), _ui.Text("en.escaped"), _ui.Text("en.time"), _ui.Text("en.distance"), _ui.Text("en.waste") }, Head);
        foreach (var t in r.EnemyTypes(EnemyName))
        {
            var known = analysis.Exact;
            EnemyTable.AddRow(new TableCell(EnemyName(t.DataId), UIKitColors.GameText),
                StatTableCell(Exact(t.Appeared.ToString(CultureInfo.InvariantCulture))), StatTableCell(Exact(t.Killed.ToString(CultureInfo.InvariantCulture))), StatTableCell(Exact(t.Escaped.ToString(CultureInfo.InvariantCulture))),
                StatTableCell(t.TtkCount == 0 ? None : Cell(known, Seconds(t.MeanTtkSeconds * 1000f))),
                StatTableCell(t.DistCount == 0 ? None : Cell(known, t.MeanDistance.ToString("0.0", CultureInfo.InvariantCulture) + " m")),
                StatTableCell(known ? Exact(Whole(t.Overkill)) : None));
        }

        // Resources by kind, one column per player.
        var heads = new string[slots.Count + 1];
        heads[0] = "";
        for (var i = 0; i < slots.Count; i++) heads[i + 1] = Who(slots[i]);
        ResourceTable.SetColumns(heads, Head);
        for (var i = 0; i < slots.Count; i++) ResourceTable.SetHead(i + 1, NameColour(slots[i], UIKitColors.MenuOver), SlotColour(slots[i]));
        void Res(string key, Func<int, StatCell> cell)
        {
            var cells = new TableCell[slots.Count + 1];
            cells[0] = new TableCell(_ui.Text(key), Dim);
            for (var i = 0; i < slots.Count; i++) cells[i + 1] = StatTableCell(cell(slots[i]));
            ResourceTable.AddRow(cells);
        }
        string Triple(int[,] v, int s) => v[s, 0] + "/" + v[s, 1] + "/" + v[s, 2];
        ResourceTable.AddSection(_ui.Text("res.group.ammo"), Group);
        Res("res.ammoused", s => Exact(Triple(r.AmmoUsed, s)));
        Res("res.ammogained", s => Exact(Triple(r.AmmoGained, s)));
        ResourceTable.AddSection(_ui.Text("res.group.supplies"), Group);
        // A supply is seen where the giver is: another player's gives are not in a client's recording (nothing there is an estimate of them), and what a
        // player got is only the part given by those whose machine was recorded. Nothing seen from a machine that cannot see it is "—", never a 0.
        bool NoSupply(int[,] v, int s) => v[s, 0] + v[s, 1] + v[s, 2] == 0;
        Res("res.gave", s => s == analysis.RecorderSlot || analysis.Exact ? Exact(Triple(r.Gave, s)) : NoSupply(r.Gave, s) ? None : Guess(Triple(r.Gave, s)));
        Res("res.got", s => analysis.Exact ? Exact(Triple(r.Got, s)) : NoSupply(r.Got, s) ? None : Guess(Triple(r.Got, s)));
        ResourceTable.AddSection(_ui.Text("res.group.found"), Group);
        Res("res.ammopickups", s => Exact(r.AmmoPickups[s].ToString(CultureInfo.InvariantCulture)));
        Res("res.packpickups", s => Exact(r.PackPickupsBy[s].ToString(CultureInfo.InvariantCulture)));
        Res("res.boxes", s => GuessCount(r.BoxesBy[s]));
        ResourceTable.AddSection(_ui.Text("res.group.effect"), Group);
        // What a pack did is worked out from the receiver's numbers jumping, which finds some uses and misses others: nothing found is a 0 only where the
        // game itself said so (the host), else "—" (a found use is "about N").
        Res("res.healed", s => full <= 0f ? None : r.HealedShare[s] > 0f ? Guess(Whole(r.HealedShare[s] * full)) : analysis.Exact ? Exact("0") : None);
        Res("res.ammorestored", s => r.AmmoRestored[s] > 0 ? Guess(r.AmmoRestored[s].ToString(CultureInfo.InvariantCulture)) : analysis.Exact ? Exact("0") : None);
        Res("res.infection", s => r.InfectionCleared[s] > 0f ? Guess(Whole(r.InfectionCleared[s])) : analysis.Exact ? Exact("0") : None);
        ResourceTable.AddSection(_ui.Text("res.group.tools"), Group);
        Res("res.sentries", s => GuessCount(r.SentriesPlaced[s]));
        // A sentry's damage and kills are put to its owner by estimate: with the owner's sentry placed and nothing counted it is no 0 either, unless the host settled it.
        Res("res.sentrydamage", s => r.SentryDamage[s] > 0f ? Guess(Whole(r.SentryDamage[s])) : analysis.Exact || r.SentriesPlaced[s] == 0 ? Exact("0") : None);
        Res("res.sentrykills", s => r.SentryKills[s] > 0 ? Guess(r.SentryKills[s].ToString(CultureInfo.InvariantCulture)) : analysis.Exact || r.SentriesPlaced[s] == 0 ? Exact("0") : None);
        // Foam blobs the recording saw fired but could not put to a player leave every 0 here unknown.
        var foamUnplaced = analysis.FoamLaunches.Count > r.FoamFired.Sum();
        Res("res.foam", s => r.FoamFired[s] > 0 || !foamUnplaced ? Exact(r.FoamFired[s].ToString(CultureInfo.InvariantCulture)) : None);

        // The level by kind: tension, waves, scans, terminals, zones, who joined and left; a long line goes on over more rows.
        LevelTable.HeadHeight = 0f;
        LevelTable.SetColumns(new[] { "" }, Head);
        var text0 = UIKitColors.GameText;
        void Level(string key, IEnumerable<(string Text, Color Colour)> lines)
        {
            var any = false;
            foreach (var (line, colour) in lines)
            {
                if (!any) { LevelTable.AddSection(_ui.Text(key), Group); any = true; }
                var parts = new List<string>();
                if (_canvas != null) _canvas.Wrap(line, LevelTable.Px, false, LevelWidth, parts); else parts.Add(line);
                foreach (var part in parts) LevelTable.AddRow(new TableCell(part, colour));
            }
        }
        var tension = new List<(string, Color)>();
        if (r.Danger(out var dangerMs, out var hunters)) tension.Add((_ui.Text("lv.tension", hunters, TimelineMath.Clock(dangerMs)), UIKitColors.Warn));
        Level("lv.group.tension", tension);
        Level("lv.group.waves", r.Waves.Select(w => (_ui.Text("lv.wave", TimelineMath.Clock(w.StartMs), Whole((w.EndMs - w.StartMs) / 1000f), w.Spawned, w.Killed) + (w.Ended ? "" : " " + _ui.Text("lv.endguess")), UIKitColors.Alert)));
        Level("lv.group.scans", r.Scans.Select(s => (_ui.Text(s.DoneMs >= 0 ? "lv.scan" : "lv.scanopen", TimelineMath.Clock(s.FromMs), Whole((s.DoneMs >= 0 ? s.DoneMs - s.FromMs : 0) / 1000f)), text0)));
        Level("lv.group.terminals", r.TerminalSpans.Select(t => (_ui.Text("lv.terminal", TimelineMath.Clock(t.FromMs), Who(t.Slot, t.FromMs), Whole((t.ToMs - t.FromMs) / 1000f)), text0)));
        Level("lv.group.zones", r.Zones.Values.OrderBy(z => z.FirstMs).Select(z => (_ui.Text("lv.zone", z.Zone, TimelineMath.Clock(z.FirstMs), Who(z.FirstSlot, z.FirstMs), Whole(z.StayMs / 1000f)) + (z.OpenedBy >= 0 ? "  " + _ui.Text("lv.opened", Who(z.OpenedBy, z.FirstMs)) : ""), text0)));
        Level("lv.group.session", r.Session.Where(l => l.Kind is SessionKind.Left or SessionKind.CheckpointReloaded or SessionKind.HostChanged || l.Kind == SessionKind.Joined && l.TimeMs > 5000)
            .Select(l => (TimelineMath.Clock(l.TimeMs) + "  " + _ui.Text("lv.session." + l.Kind.ToString().ToLowerInvariant(), l.Slot is >= 0 and < 4 ? Who(l.Slot, l.TimeMs) : ""), Dim)));
    }

    private const float LevelWidth = 900f;

    // The team table, again when a number changes: in groups, the numbers up to the moment on the bar and the whole run's rows.
    private static void BuildTeamTable()
    {
        var slots = Enumerable.Range(0, 4).Where(s => EverHeld[s]).ToList();
        var heads = new string[slots.Count + 1];
        heads[0] = "";
        for (var i = 0; i < slots.Count; i++) heads[i + 1] = Names[slots[i]];
        TeamTable.Clear();
        TeamTable.SetColumns(heads, Head);
        for (var i = 0; i < slots.Count; i++) TeamTable.SetHead(i + 1, NameColour(slots[i], UIKitColors.MenuOver), SlotColour(slots[i]));
        void Live(StatRow row)
        {
            var cells = new TableCell[slots.Count + 1];
            cells[0] = new TableCell(RowTitles[(int)row], Dim);
            for (var i = 0; i < slots.Count; i++) cells[i + 1] = StatTableCell(_stats[(int)row, slots[i]]);
            TeamTable.AddRow(cells);
        }
        void Run(string key)
        {
            if (!TeamExtra.TryGetValue(key, out var extra)) return;
            var cells = new TableCell[slots.Count + 1];
            cells[0] = new TableCell(extra.Label, Dim);
            for (var i = 0; i < slots.Count; i++) cells[i + 1] = StatTableCell(extra.Cells[slots[i]]);
            TeamTable.AddRow(cells);
        }
        void Most(string title, string[] values)
        {
            var cells = new TableCell[slots.Count + 1];
            cells[0] = new TableCell(title, Dim);
            for (var i = 0; i < slots.Count; i++) cells[i + 1] = values[slots[i]].Length == 0 ? StatTableCell(None) : new TableCell(values[slots[i]], UIKitColors.GameText);
            TeamTable.AddRow(cells);
        }
        TeamTable.AddSection(_ui.Text("stats.group.combat"), Group);
        Live(StatRow.Kills); Live(StatRow.Damage); Live(StatRow.Hits); Live(StatRow.WeakSpots); Live(StatRow.Backstabs);
        Most(_topWeaponTitle, TopWeapon); Most(_topEnemyTitle, TopEnemy);
        Run("stats.friendgave");
        TeamTable.AddSection(_ui.Text("stats.group.survival"), Group);
        Live(StatRow.Taken); Live(StatRow.Downs);
        Run("stats.downtime"); Run("stats.downcause"); Run("stats.friendtook");
        Live(StatRow.Dodges);
        TeamTable.AddSection(_ui.Text("stats.group.team"), Group);
        Live(StatRow.Revives); Live(StatRow.Rescues);
        Run("stats.rescuetime");
        Live(StatRow.Packs); Live(StatRow.Supplied);
        TeamTable.AddSection(_ui.Text("stats.group.field"), Group);
        Live(StatRow.Woken); Live(StatRow.Mines);
        BuildCompare();
    }

    // An exact cell, or an estimated one when <paramref name="exact"/> is false.
    private static StatCell Cell(bool exact, string text) => exact ? Exact(text) : Guess(text);

    private static string WeaponName(Analysis analysis, int slot, int wielded)
    {
        if (wielded == WeaponKey.Sentry) return _ui.Text("wp.sentry");
        if (wielded == WeaponKey.Explosion) return _ui.Text("feed.explosion");
        var name = Weapon(HolderAt(analysis, slot, uint.MaxValue).Loadout, wielded);
        return name.Length > 0 ? name : _ui.Text("wp.unknown");
    }

    private static string CauseText(DownCause cause, int grab, ulong enemy, int attacker, Analysis analysis)
    {
        if (attacker >= 0) return _ui.Text("cause.friend", PlayerName.Plain(HolderAt(analysis, attacker, uint.MaxValue).Name));
        var what = cause == DownCause.Grabbed ? _ui.Text("cause.grab." + grab.ToString(CultureInfo.InvariantCulture)) : _ui.Text("cause." + cause.ToString().ToLowerInvariant());
        return enemy != 0 ? what + " · " + EnemyName(enemy) : what;
    }

    // The feed's lines for downs and rescues: the player, what happened, and what did it.
    private static void AddDownFeed(Analysis analysis)
    {
        var r = analysis.Report;
        if (r == null) return;
        foreach (var d in r.Downs)
            FeedItems.Add((d.TimeMs, d.Slot, PlayerName.Plain(HolderAt(analysis, d.Slot, d.TimeMs).Name), _ui.Text("feed.down"), CauseText(d.Cause, d.Grab, d.EnemyDataId, d.Attacker, analysis), !d.Estimated, false, -1, d.TimeMs));
        foreach (var rescue in analysis.Rescues)
        {
            var known = rescue.Reviver is >= 0 and < 4;
            FeedItems.Add((rescue.TimeMs, known ? rescue.Reviver : -1, known ? PlayerName.Plain(HolderAt(analysis, rescue.Reviver, rescue.TimeMs).Name) : _ui.Text("feed.unknown"),
                _ui.Text("feed.revive"), PlayerName.Plain(HolderAt(analysis, rescue.Revived, rescue.TimeMs).Name), true, false, -1, rescue.TimeMs));
        }
    }

    // The time bar keeps to what matters: a down, a wipe, a wave, a scan or alarm, the reactor, a checkpoint, a kill worth a mark, a ping and
    // the highlights; doors, terminals, chat and revives are in the event list, not on the bar.
    private static bool OnBar(MarkerKind kind) => kind is MarkerKind.Down or MarkerKind.Wave or MarkerKind.Alarm or MarkerKind.Scan or MarkerKind.Reactor or MarkerKind.Checkpoint or MarkerKind.Kill or MarkerKind.Ping or MarkerKind.WorldEvent or MarkerKind.Note;

    private static void AddBarExtras(Analysis analysis)
    {
        if (_bar == null) return;
        foreach (var w in analysis.Wipes) _bar.Marks.Add(new TimelineMark(w, UIKitColors.Alert, _ui.Text("mark.wipe"), 0, TimelineShape.Cross));
        foreach (var h in _highlights) _bar.Marks.Add(new TimelineMark(h.TimeMs, UIKitColors.MenuOver, HighlightTexts.Count > 0 ? HighlightTexts[Math.Min(_highlights.IndexOf(h), HighlightTexts.Count - 1)] : _ui.Text("tab.highlights"), 0, TimelineShape.Diamond));
    }

    private static readonly string[] PageKeys = { "stats.page.team", "stats.page.rating", "stats.page.weapons", "stats.page.enemies", "stats.page.resources", "stats.page.level" };

    // The page buttons on the title row, right to left.
    private static void PageButtons(HudCanvas c, float right, float y)
    {
        var x = right;
        for (var i = PageKeys.Length - 1; i >= 0; i--)
            if (c.Button(ref x, y, _ui.Text(PageKeys[i]), (int)_page == i, Lit)) _page = (StatsPage)i;
    }
}
