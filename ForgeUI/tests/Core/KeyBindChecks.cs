using ForgeUI;

/// <summary>Key actions: chord parsing, the saved file, defaults that follow a new version, conflicts, the capture and the per-frame state.</summary>
internal static class KeyBindChecks
{
    private static readonly string[] Keys = { "G", "V", "B", "Mouse3", "UpArrow", "LeftShift", "LeftControl", "Escape", "Backspace", "Delete", "F" };

    private static string? Known(string name)
    {
        foreach (var key in Keys) if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }

    internal static void Run(Action<bool, string> check)
    {
        // Chords: modifiers in any order and case, a canonical key name, None; mistakes are refused with a reason.
        check(KeyChord.TryParse("shift+ctrl+g", Known, out var chord, out _) && chord.ToString() == "Ctrl+Shift+G", "chord parses modifiers in any order");
        check(KeyChord.TryParse("mouse3", Known, out chord, out _) && chord == new KeyChord("Mouse3"), "mouse button chord");
        check(KeyChord.TryParse("None", Known, out chord, out _) && chord.IsNone && KeyChord.TryParse("", Known, out chord, out _) && chord.IsNone, "None and empty are unbound");
        check(!KeyChord.TryParse("Ctrl+Nope", Known, out _, out var why) && why!.Contains("Nope"), "unknown key refused");
        check(!KeyChord.TryParse("Hyper+G", Known, out _, out _) && !KeyChord.TryParse("Ctrl+Ctrl+G", Known, out _, out _) && !KeyChord.TryParse("Ctrl+", Known, out _, out _), "bad modifiers refused");
        check(new KeyChord("LeftShift", KeyMods.Shift).Mods == KeyMods.None, "a modifier key alone carries no modifier");
        check(new KeyChord("G") != new KeyChord("G", KeyMods.Ctrl), "G and Ctrl+G are different keys");

        // Registration: defaults and duplicate names.
        var book = new KeyBindBook(Known);
        var exit = book.Register("Mod.Frames", "Exit", "key.exit", "group", "G", KeyMode.Hold, "exosuit", out _);
        var drop = book.Register("Mod.Arsenal", "Drop", "key.drop", "group", "G", KeyMode.Press, "on-foot", out _);
        var summon = book.Register("Mod.Ordnance", "Summon", "key.summon", "group", "Mouse3", KeyMode.Hold, "", out _);
        check(exit == 0 && drop == 1 && summon == 2, "actions get indices in order");
        check(book.Register("Mod.Frames", "Exit", "k", "g", "B", KeyMode.Press, "", out var problem) < 0 && problem!.Contains("already"), "duplicate action refused");
        check(book.Register("Mod.Frames", "Bad", "k", "g", "Ctrl+Nope", KeyMode.Press, "", out problem) < 0 && problem!.Contains("default"), "bad default refused");
        check(book.Register("Mod.Frames", "has space", "k", "g", "G", KeyMode.Press, "", out _) < 0, "bad name refused");

        // Conflicts: same key in overlapping contexts only; nobody's key is changed.
        var others = new List<int>();
        check(book.Conflicts(exit, others) == 0, "same key in different contexts is no conflict");
        book.Set(summon, new KeyChord("G"));
        check(book.Conflicts(exit, others) == 1 && others[0] == summon && book.Conflicts(drop, others) == 1, "an everywhere action conflicts with both");
        check(book[exit].Effective == new KeyChord("G") && book[drop].Effective == new KeyChord("G"), "a conflict moves nobody's key");
        book.Set(summon, new KeyChord("G", KeyMods.Ctrl));
        check(book.Conflicts(summon, others) == 0, "a different modifier is no conflict");
        var game = new List<(string, string)> { ("Use", "E"), ("Flashlight", "G"), ("Flashlight", "G") };
        var hits = new List<string>();
        check(book.GameConflicts(exit, game, hits) == 1 && hits[0] == "Flashlight", "the game's own key conflicts, listed once");
        check(book.GameConflicts(summon, game, hits) == 1, "a game key fires whatever modifiers are held");
        book.Set(summon, KeyChord.None);
        check(book.Conflicts(summon, others) == 0 && book.GameConflicts(summon, game, hits) == 0, "unbound conflicts with nothing");

        // Saving: only changed keys, unbound kept as None; the default itself is stored as unchanged.
        book.Set(exit, new KeyChord("V", KeyMods.Shift));
        book.Set(drop, new KeyChord("G"));
        var saved = book.Save();
        check(saved.StartsWith("#") && saved.Contains("Mod.Frames/Exit = Shift+V") && saved.Contains("Mod.Ordnance/Summon = None") && !saved.Contains("Mod.Arsenal/Drop"),
            "only changed keys are saved");
        check(book[drop].Custom == null, "setting the default stores nothing");

        // Loading into a new version whose defaults changed: changed keys stay, untouched ones follow the new default, entries of
        // mods not installed are kept, bad lines are reported.
        var next = new KeyBindBook(Known);
        var problems = new List<string>();
        next.Load(saved + "Mod.Gone/Thing = B\nbroken line\nMod.Frames/Rocket = Ctrl+Nope\n", problems);
        var exit2 = next.Register("Mod.Frames", "Exit", "key.exit", "group", "B", KeyMode.Hold, "exosuit", out _);
        var drop2 = next.Register("Mod.Arsenal", "Drop", "key.drop", "group", "F", KeyMode.Press, "on-foot", out _);
        var summon2 = next.Register("Mod.Ordnance", "Summon", "key.summon", "group", "Mouse3", KeyMode.Hold, "", out _);
        check(next[exit2].Effective == new KeyChord("V", KeyMods.Shift), "a changed key survives a new default");
        check(next[drop2].Effective == new KeyChord("F"), "an unchanged key follows the new default");
        check(next[summon2].Effective.IsNone, "an unbound key stays unbound");
        check(problems.Count == 2, "bad lines are reported (" + string.Join("; ", problems) + ")");
        check(next.Save().Contains("Mod.Gone/Thing = B"), "a mod that is not installed keeps its keys");
        var late = new KeyBindBook(Known);
        late.Register("Mod.Frames", "Exit", "key.exit", "group", "B", KeyMode.Hold, "", out _);
        late.Load("Mod.Frames/Exit = G\n", new List<string>());
        check(late[0].Effective == new KeyChord("G"), "a file read after registration applies");
        next.Reset(exit2);
        check(next[exit2].Effective == new KeyChord("B") && !next.Save().Contains("Mod.Frames/Exit"), "reset returns to the default");
        next.Set(exit2, new KeyChord("G"));
        var version = next.Version;
        next.ResetAll();
        check(next.Version > version && next[exit2].Custom == null && next[summon2].Effective == new KeyChord("Mouse3"), "reset all");

        // Capture: waits for the starting click to be let go, then a key with the modifiers held; Esc cancels, Backspace clears, a
        // modifier alone binds itself.
        var capture = new KeyCapture();
        capture.Begin();
        check(capture.Step(true, "Mouse0", null, null, KeyMods.None) == KeyCapture.Result.Waiting, "the starting click is not taken");
        check(capture.Step(false, null, null, null, KeyMods.None) == KeyCapture.Result.Waiting, "armed after release");
        check(capture.Step(true, "G", null, null, KeyMods.Ctrl | KeyMods.Shift) == KeyCapture.Result.Bound && capture.Chord.ToString() == "Ctrl+Shift+G" && !capture.Active,
            "key with modifiers is bound");
        capture.Begin(); capture.Step(false, null, null, null, KeyMods.None);
        check(capture.Step(true, "Escape", null, null, KeyMods.None) == KeyCapture.Result.Cancelled, "Esc cancels");
        capture.Begin(); capture.Step(false, null, null, null, KeyMods.None);
        check(capture.Step(true, "Backspace", null, null, KeyMods.None) == KeyCapture.Result.Cleared, "Backspace clears");
        capture.Begin(); capture.Step(false, null, null, null, KeyMods.None);
        capture.Step(true, null, "LeftShift", null, KeyMods.Shift);
        check(capture.Step(false, null, null, "LeftShift", KeyMods.None) == KeyCapture.Result.Bound && capture.Chord == new KeyChord("LeftShift"), "a modifier alone binds itself");
        capture.Begin(); capture.Step(false, null, null, null, KeyMods.None);
        capture.Step(true, null, "LeftShift", null, KeyMods.Shift);
        check(capture.Step(true, "Delete", null, null, KeyMods.Shift) == KeyCapture.Result.Bound && capture.Chord.ToString() == "Shift+Delete", "Shift+Delete is a key, not a clear");

        // Per frame: pressed and released once, held throughout, a toggle flips per press.
        var pulse = new KeyPulse();
        pulse.Step(true, KeyMode.Toggle);
        var first = pulse.Pressed && pulse.Held && pulse.On;
        pulse.Step(true, KeyMode.Toggle);
        var second = !pulse.Pressed && pulse.Held && pulse.On;
        pulse.Step(false, KeyMode.Toggle);
        var released = pulse.Released && !pulse.Held;
        pulse.Step(true, KeyMode.Toggle);
        check(first && second && released && !pulse.On, "pressed, held, released and toggle");
        pulse.Drop();
        pulse.Step(false, KeyMode.Hold);
        check(!pulse.Released, "dropping lets go without a release");
    }
}
