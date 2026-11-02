using ForgeOrdnance;

/// <summary>The level-facing extension surface: definitions only a level brings (<c>"Callable": false</c>), levels declared from code,
/// and the call numbers of level-start drops.</summary>
internal static class LevelApiChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var payloads = new PayloadRegistry();
        payloads.Register("Stub", new StubParser());
        var categories = new CategoryRegistry(id => () => id);
        const string Payload = "\"Payload\":{\"Type\":\"Stub\",\"Count\":1}";
        ParseResult Parse(string entry) => DefinitionParser.ParseFile("lv.json", "[" + entry + "]", payloads, categories);

        // ---- Callable: false
        var level = Parse("{\"Id\":\"lv.suit\",\"Callable\":false," + Payload + "}");
        check(level.Errors.Count == 0 && level.Definitions.Count == 1 && !level.Definitions[0].Callable && level.Definitions[0].Code.Count == 0,
            "Callable false: an entry with only an Id and a Payload loads: " + string.Join(" | ", level.Errors));
        foreach (var field in new[] { "Category", "Code", "Cooldown", "Uses", "CallInDelay", "Stats", "BeamColour" })
        {
            var value = field switch { "Category" => "\"Supply\"", "Code" => "[\"Up\"]", "Stats" => "[]", "BeamColour" => "\"#FFFFFF\"", _ => "1" };
            var bad = Parse("{\"Id\":\"lv.suit\",\"Callable\":false," + Payload + ",\"" + field + "\":" + value + "}");
            check(bad.Definitions.Count == 0 && bad.Errors.Count == 1 && bad.Errors[0].StartsWith("lv.json [0] " + field + ":"), "Callable false refuses " + field + ": " + string.Join(" | ", bad.Errors));
        }
        check(Parse("{\"Id\":\"lv.suit\",\"Callable\":\"no\"," + Payload + "}").Errors.Count == 1, "Callable must be a boolean");
        var callable = Parse("{\"Id\":\"lv.gun\",\"Category\":\"Supply\",\"Code\":[\"Up\"],\"Cooldown\":1,\"CallInDelay\":1," + Payload + "}").Definitions[0];
        check(callable.Callable && callable.Key != level.Definitions[0].Key, "a definition is callable unless it says otherwise");
        var errors = new List<string>();
        var set = DefinitionSet.Build(new[] { level.Definitions[0], callable }, errors);
        var rules = new OrdnanceLevelRules();
        check(LoadoutValidator.Validate(new[] { level.Definitions[0].Key }, set, rules).Reason == Rejection.LoadoutInvalid
            && LoadoutValidator.Validate(new[] { callable.Key }, set, rules).Ok, "a loadout may hold a callable definition and never a level-only one");
        var withDrop = new OrdnanceLevelRules { Drops = new[] { new OrdnanceLevelDrop("suit", "lv.suit", DropWhen.LevelStart, OrdnanceArrivalMode.Direct, 0, 0, StartPlace) } };
        var settings = OrdnanceSettings.Default.WithDeclared(new Dictionary<string, OrdnanceLevelRules> { ["K1"] = withDrop }, errors);
        check(settings.WithUsableDrops(set, _ => true, errors).For("K1").Drops.Count == 1 && errors.Count == 0, "a level-only definition can be named by a drop");

        // ---- DeclareLevel agrees with the file
        var fileErrors = new List<string>();
        var file = OrdnanceSettings.Parse("{\"Levels\":{\"K2\":{\"LoadoutSize\":2,\"CallMenu\":false,\"MaxThrowDistance\":50,\"Drops\":[{\"Name\":\"suit\",\"Ordnance\":\"lv.suit\",\"When\":\"LevelStart\","
            + "\"Arrival\":\"Direct\",\"Yaw\":10,\"Where\":{\"DimensionIndex\":0,\"Layer\":0,\"LocalIndex\":1,\"Area\":0}}]}}}", fileErrors)!;
        var fromFile = file.For("K2");
        var checkErrors = new List<string>();
        OrdnanceSettings.Check(fromFile, "file", checkErrors);
        check(fileErrors.Count == 0 && checkErrors.Count == 0, "the checks of declared rules accept what the file parser accepts: " + string.Join(" | ", fileErrors.Concat(checkErrors)));
        var declared = new OrdnanceLevelRules
        {
            LoadoutSize = 2, CallMenu = false, MaxThrowDistance = 50,
            Drops = new[] { new OrdnanceLevelDrop("suit", "lv.suit", DropWhen.LevelStart, OrdnanceArrivalMode.Direct, 0, 10, new DropWhere(0, 0, 1, new DropPoint(0, 0, 0, 0))) }
        };
        var merged = OrdnanceSettings.Default.WithDeclared(new Dictionary<string, OrdnanceLevelRules> { ["K2"] = declared }, errors).For("K2");
        check(merged.LoadoutSize == fromFile.LoadoutSize && merged.CallMenu == fromFile.CallMenu && merged.MaxThrowDistance == fromFile.MaxThrowDistance
            && merged.Drops.SequenceEqual(fromFile.Drops), "a declared level has the rules the same level written in the file has");

        var conflict = new List<string>();
        var winner = file.WithDeclared(new Dictionary<string, OrdnanceLevelRules> { ["K2"] = new OrdnanceLevelRules { LoadoutSize = 4 }, ["K3"] = new OrdnanceLevelRules() }, conflict);
        check(winner.For("K2") == fromFile && winner.For("K3").LoadoutSize == 4 && conflict.Count == 1 && conflict[0].Contains("K2") && conflict[0].Contains("the file wins"),
            "a level both the file and a plugin set keeps the file's rules, with one error: " + string.Join(" | ", conflict));

        var badRules = new List<string>();
        OrdnanceSettings.Check(new OrdnanceLevelRules { LoadoutSize = 0, UsesMultiplier = 11, Drops = new[] { new OrdnanceLevelDrop("a", "x", DropWhen.LevelStart, OrdnanceArrivalMode.Drop, 0, 0, null) } }, "bad", badRules);
        check(badRules.Count == 3 && badRules.Exists(e => e.Contains("LoadoutSize")) && badRules.Exists(e => e.Contains("UsesMultiplier")) && badRules.Exists(e => e.Contains("Where")),
            "declared rules are refused for what the file would refuse: " + string.Join(" | ", badRules));

        Plugin.Errors.Clear();
        check(OrdnanceApi.DeclareLevel("api.K", new OrdnanceLevelRules { CallMenu = false }) && OrdnanceApi.DeclaredLevels["api.K"].CallMenu == false, "DeclareLevel takes a valid level");
        check(!OrdnanceApi.DeclareLevel("api.K", new OrdnanceLevelRules()) && Plugin.Errors.Count == 1, "DeclareLevel refuses a key already declared with one error");
        check(!OrdnanceApi.DeclareLevel("api.Bad", new OrdnanceLevelRules { LoadoutSize = 0 }) && !OrdnanceApi.DeclaredLevels.ContainsKey("api.Bad") && Plugin.Errors.Count == 2,
            "DeclareLevel refuses invalid rules with an error");
        var allowedList = new List<string> { "a" };
        var dropList = new List<OrdnanceLevelDrop> { new("d", "a", DropWhen.Event, OrdnanceArrivalMode.Drop, 0, 0, null) };
        OrdnanceApi.DeclareLevel("api.Copy", new OrdnanceLevelRules { Allowed = allowedList, Drops = dropList });
        allowedList.Add("b"); dropList.Clear();
        check(OrdnanceApi.DeclaredLevels["api.Copy"].Allowed!.Count == 1 && OrdnanceApi.DeclaredLevels["api.Copy"].Drops.Count == 1, "DeclareLevel keeps a copy: later changes to the caller's lists do not reach the level");
        Plugin.Errors.Clear();

        // ---- the level key warning
        var withDeclared = file.WithDeclared(new Dictionary<string, OrdnanceLevelRules> { ["api.D"] = new OrdnanceLevelRules() }, new List<string>());
        check(withDeclared.Unmatched("api.D") == null && OrdnanceSettings.Default.WithDeclared(new Dictionary<string, OrdnanceLevelRules> { ["api.D"] = new OrdnanceLevelRules() }, new List<string>()).Unmatched("other") == null,
            "no warning for a declared level being played, or when only declared levels have rules");
        check(withDeclared.Unmatched("R9Z9") is { } onlyFile && !onlyFile.Contains("api.D"), "the warning names only the keys Settings.json sets");
        check(file.Unmatched("K2") == null && OrdnanceSettings.Default.Unmatched("anything") == null, "no warning when the level matches, or when no level has rules");
        var warning = file.Unmatched("R9Z9");
        check(warning != null && warning.Contains("R9Z9") && warning.Contains("K2"), "a played level no rules match is warned about with both keys: " + warning);

        // ---- level-start call numbers
        check(DeveloperDrops.LevelStartCall("suit") == DeveloperDrops.LevelStartCall("suit") && DeveloperDrops.LevelStartCall("suit") != DeveloperDrops.LevelStartCall("other")
            && (DeveloperDrops.LevelStartCall("suit") & 0x80000000u) != 0, "a level-start call number is fixed by the drop's name, in the upper half");
        var next = DeveloperDrops.SeedCalls(ulong.MaxValue);
        var lower = true;
        for (var i = 0; i < 5; i++) { var call = DeveloperDrops.NextCall(ref next); lower &= call != 0 && (call & 0x80000000u) == 0; }
        check(lower && (DeveloperDrops.SeedCalls(0xFFFFFFFFFFUL) & 0x80000000u) == 0, "clock-seeded call numbers stay in the lower half, across the wrap, and are never 0");

        var seen = new Dictionary<uint, string>();
        string first = "", second = "";
        for (var i = 0; second == ""; i++)
        {
            var name = "drop" + i;
            var number = DeveloperDrops.LevelStartCall(name);
            if (seen.TryGetValue(number, out var other)) { first = other; second = name; }
            else seen[number] = name;
        }
        var collisions = new List<string>();
        var parsed = OrdnanceSettings.Parse("{\"Drops\":[" + string.Join(",", new[] { first, second }.Select(n =>
            "{\"Name\":\"" + n + "\",\"Ordnance\":\"t.a\",\"When\":\"Event\"}")) + "]}", collisions);
        check(parsed == null && collisions.Count == 1 && collisions[0].Contains("same call number"), "two drop names with one call number are refused when the file is read: " + string.Join(" | ", collisions));
    }

    private static readonly DropWhere StartPlace = new(0, 0, 0, new DropPoint(0, 0, 0, 0));
}
