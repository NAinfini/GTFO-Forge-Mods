using ForgeUI;

/// <summary>Icon packs and the game's catalog: names, duplicates, missing icons and packs, files that fail to load, each failure
/// reported once.</summary>
internal static class IconChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var reports = new List<string>();
        var loads = new List<string>();
        var game = new Dictionary<string, string>(StringComparer.Ordinal) { ["symbol_hostile"] = "the triangle" };
        var book = new IconBook<string>(
            path => { loads.Add(path); return path.Contains("broken") ? null : "picture:" + path; },
            item => game.TryGetValue(item, out var sprite) ? sprite : null,
            reports.Add);

        check(IconNames.CheckPack("Frames") == null && IconNames.CheckPack("My-Pack_2") == null, "pack names are letters, digits, '_' and '-'");
        check(IconNames.CheckPack("") != null && IconNames.CheckPack("a:b") != null && IconNames.CheckPack("a.b") != null && IconNames.CheckPack("a b") != null, "other pack names refused");
        check(IconNames.CheckPack("Game") != null && IconNames.CheckPack("game") != null, "Game is reserved");
        check(IconNames.TrySplit("Frames:skill-charge", out var pack, out var item, out _) && pack == "Frames" && item == "skill-charge", "Pack:item splits");
        check(!IconNames.TrySplit("skill-charge", out _, out _, out _) && !IconNames.TrySplit("a:b:c", out _, out _, out _) && !IconNames.TrySplit(":b", out _, out _, out _), "names without exactly one colon refused");

        // Registering.
        check(book.ClaimPack("Frames", "mod.frames") && book.ClaimPack("Frames", "mod.frames"), "a mod claims its pack, again without harm");
        check(!book.ClaimPack("Frames", "mod.other") && reports.Count == 1, "another mod cannot take the pack, and the refusal is reported");
        check(!book.ClaimPack("Game", "mod.frames") && reports.Count == 2, "Game cannot be claimed");
        check(book.Register("mod.frames", "Frames", "shield", "hud/shield.png"), "an icon registers");
        check(book.Find("Frames:shield") == "picture:hud/shield.png", "and is found by its full name");
        check(book.Entries.Count == 1 && book.Entries[0].Name == "Frames:shield" && book.Entries[0].Owner == "mod.frames" && book.Entries[0].Path == "hud/shield.png", "the listing names, pack, owner and file");

        // Duplicate.
        var before = reports.Count;
        check(!book.Register("mod.frames", "Frames", "shield", "hud/other.png") && reports.Count == before + 1 && reports[^1].Contains("already registered"), "a duplicate name is refused and reported");
        check(book.Find("Frames:shield") == "picture:hud/shield.png" && book.Entries.Count == 1, "the first registration is kept");
        check(!book.Register("mod.frames", "Frames", "shield", "hud/other.png") && reports.Count == before + 1, "the same refusal is reported once");

        // Wrong owner, bad names.
        check(!book.Register("mod.other", "Frames", "heat", "hud/heat.png") && reports[^1].Contains("does not own"), "a mod cannot add to another's pack");
        check(!book.Register("mod.frames", "Unclaimed", "heat", "hud/heat.png"), "nor to a pack nobody claimed");
        check(!book.Register("mod.frames", "Frames", "bad name", "hud/heat.png") && reports[^1].Contains("may only hold"), "a bad icon name is refused");

        // Load failure.
        before = reports.Count;
        check(!book.Register("mod.frames", "Frames", "broken", "hud/broken.png") && reports.Count == before + 1 && reports[^1].Contains("could not be loaded"), "a file that fails to load is refused and reported");
        check(book.Find("Frames:broken") == null, "and the name stays unregistered");
        check(!book.Register("mod.frames", "Frames", "broken", "hud/broken.png") && reports.Count == before + 2, "the same load failure is reported once (the lookup above added its own line)");

        // Missing.
        before = reports.Count;
        check(book.Find("Frames:missing") == null && reports.Count == before + 1 && reports[^1].Contains("pack 'Frames' has no such icon"), "a name its pack lacks is reported");
        check(book.Find("Frames:missing") == null && reports.Count == before + 1, "the same miss is reported once");
        check(book.Find("Ordnance:cannon") == null && reports.Count == before + 2 && reports[^1].Contains("no pack 'Ordnance' is loaded"), "a pack that is not loaded is told apart");
        check(book.Find("shield") == null && book.Find("") == null && book.Find(null) == null && reports.Count == before + 4,"names that are not Pack:item are reported (an empty one once)");

        // The game's catalog.
        check(GameIcons.All.Count > 0 && GameIcons.All.All(icon => icon.Summary.Length > 0 && icon.Name == "Game:" + icon.Item), "every catalog icon has a summary and a Game: name");
        check(GameIcons.All.Select(icon => icon.Item).Distinct().Count() == GameIcons.All.Count, "catalog items are unique");
        check(book.Find("Game:symbol_hostile") == "the triangle", "a catalog icon is taken from the game by name");
        before = reports.Count;
        check(book.Find("Game:cellUI_SmallTriangleFill") == null && reports.Count == before + 1 && reports[^1].Contains("game has no loaded picture"), "a catalog icon the game has not loaded is reported");
        check(book.Find("Game:cellUI_SmallTriangleFill") == null && reports.Count == before + 1, "once");
        game["cellUI_SmallTriangleFill"] = "the small triangle";
        check(book.Find("Game:cellUI_SmallTriangleFill") == "the small triangle", "and found once the game has it");
        check(book.Find("Game:NotInTheCatalog") == null && reports[^1].Contains("not in the game icon catalog"), "a name outside the catalog is refused, never guessed");
        check(!loads.Any(path => path.StartsWith("Game")), "game icons never touch a file");
    }
}
