using System.Runtime.InteropServices;
using System.Text.Json;
using ForgeOrdnance;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    checks++;
}

// ---- definitions -------------------------------------------------------------------------------------------

var payloads = new PayloadRegistry();
payloads.Register("Stub", new StubParser());
var categories = new CategoryRegistry(id => () => id);

string Entry(string id, string code = "[\"Up\",\"Down\"]", string extra = "", string payload = "{\"Type\":\"Stub\",\"Count\":2}")
    => "{\"Id\":\"" + id + "\",\"Category\":\"Supply\",\"Code\":" + code + ",\"Cooldown\":60,\"Uses\":3,\"CallInDelay\":5,\"Payload\":" + payload + extra + "}";

ParseResult Parse(string file, params string[] entries) => DefinitionParser.ParseFile(file, "[" + string.Join(",", entries) + "]", payloads, categories);

{
    var good = Parse("a.json", Entry("a.one"));
    Check(good.Errors.Count == 0 && good.Definitions.Count == 1, "valid entry loads");
    var d = good.Definitions[0];
    Check(d.Name == "a.one" && d.Uses == 3 && d.Cooldown == 60 && d.CallInDelay == 5 && d.Code.Count == 2 && d.Code[0] == Direction.Up, "fields parsed, Name defaults to Id");
    Check(((StubParams)d.Parameters).Count == 2, "payload parser result is kept");
}

void Rejected(string name, string entry, string field)
{
    var result = Parse("bad.json", Entry("ok.first"), entry, Entry("ok.last"));
    Check(result.Definitions.Count == 2, name + ": the other entries still load");
    Check(result.Errors.Count == 1, name + ": exactly one error");
    Check(result.Errors[0].StartsWith("bad.json [1] " + field + ":"), name + ": error names file, index and field, got: " + result.Errors[0]);
}

Rejected("code with a letter key", Entry("t.x", "[\"Up\",\"W\"]"), "Code");
Rejected("lower-case direction", Entry("t.x", "[\"up\"]"), "Code");
Rejected("empty code", Entry("t.x", "[]"), "Code");
Rejected("code too long", Entry("t.x", "[\"Up\",\"Up\",\"Up\",\"Up\",\"Up\",\"Up\",\"Up\",\"Up\",\"Up\"]"), "Code");
Rejected("code not an array", Entry("t.x", "\"Up\""), "Code");
Rejected("unknown field", Entry("t.x", extra: ",\"Cooldwn\":1"), "Cooldwn");
Rejected("unregistered payload type", Entry("t.x", payload: "{\"Type\":\"Missing\"}"), "Payload.Type");
Rejected("payload parameter refused", Entry("t.x", payload: "{\"Type\":\"Stub\",\"Count\":99}"), "Payload.Count");
Rejected("missing payload", "{\"Id\":\"t.x\",\"Category\":\"Supply\",\"Code\":[\"Up\"],\"Cooldown\":1,\"Uses\":1,\"CallInDelay\":1}", "Payload");
Rejected("bad category", Entry("t.x").Replace("Supply", "Banana"), "Category");
{
    var kinds = new CategoryRegistry(id => () => id);
    kinds.Register("Exosuit", () => "Suit", 3, 0xFFC23D);
    Check(kinds.Ordered.Select(c => c.Id).SequenceEqual(new[] { "Supply", "Defensive", "Weapon", "Exosuit", "Offensive" }), "categories: a registered kind sits at its order among the built-ins");
    var suit = DefinitionParser.ParseFile("c.json", "[" + Entry("c.one").Replace("Supply", "Exosuit") + "]", payloads, kinds);
    Check(suit.Errors.Count == 0 && suit.Definitions[0].Category == "Exosuit" && suit.Definitions[0].BeamRgb == 0xFFC23D, "categories: a definition may name a registered kind and takes its default beam colour");
    Check(Parse("c.json", Entry("c.one").Replace("Supply", "Exosuit")).Errors.Count == 1, "categories: a kind that is not registered is rejected");
    var duplicate = false; try { kinds.Register("Exosuit", () => "x", 9, 0); } catch (InvalidOperationException) { duplicate = true; }
    Check(duplicate, "categories: registering a taken id throws");
    var bad = false; try { kinds.Register("not a name", () => "x", 9, 0); } catch (ArgumentException) { bad = true; }
    Check(bad, "categories: an id that is not a plain name throws");
    var supply = Parse("c.json", Entry("c.one")).Definitions[0];
    var suited = suit.Definitions[0];
    Check(Hashing.SetHash(new[] { supply }) != Hashing.SetHash(new[] { suited }), "categories: the category is part of the definition hash");
}
Rejected("empty id", Entry(""), "Id");
Rejected("id with space", Entry("a b"), "Id");
Rejected("id without a namespace", Entry("ammo"), "Id");
Rejected("id with an empty segment", Entry("author..ammo"), "Id");
Rejected("id with a trailing dot", Entry("author."), "Id");
Rejected("id with other characters", Entry("author.ammo!"), "Id");
Check(DefinitionParser.IsNamespacedId("nainfini.arsenal.orbital-strike_2"), "a namespaced id with dashes, digits and underscores is accepted");
{
    var kept = new RetainedData();
    Check(kept.Set(PayloadDataMessage.From(1, 10, 7, 0, new byte[] { 1 })) && kept.Set(PayloadDataMessage.From(1, 10, 7, 1, new byte[] { 2 }))
        && kept.Set(PayloadDataMessage.From(2, 11, 7, 0, new byte[] { 3 })) && kept.Count == 3, "kept state is one entry per type, call and tag");
    Check(kept.Set(PayloadDataMessage.From(1, 10, 7, 0, new byte[] { 9 })) && kept.Count == 3
        && kept.InOrder().First().ToBytes()![0] == 9, "a newer state replaces the kept one for its tag and keeps its replay position");
    Check(kept.Clear(7, 1, 10) == 2 && kept.Count == 1 && kept.InOrder().Single().Thrower == 2, "clearing a call drops all its tags and nothing else");
    Check(kept.Clear(8, 2, 11) == 0 && kept.Count == 1, "clearing another payload type's call leaves the state");
    var full = new RetainedData();
    for (uint i = 0; i < RetainedData.Capacity; i++) full.Set(PayloadDataMessage.From(1, i, 7, 0, Array.Empty<byte>()));
    Check(!full.Set(PayloadDataMessage.From(1, 99999, 7, 0, Array.Empty<byte>())) && full.Count == RetainedData.Capacity
        && full.Set(PayloadDataMessage.From(1, 0, 7, 0, new byte[] { 5 })), "a full store refuses new keys but still replaces kept ones");
}
Rejected("negative cooldown", Entry("t.x").Replace("\"Cooldown\":60", "\"Cooldown\":-1"), "Cooldown");
Rejected("cooldown beyond range", Entry("t.x").Replace("\"Cooldown\":60", "\"Cooldown\":1e30"), "Cooldown");
Rejected("zero uses", Entry("t.x").Replace("\"Uses\":3", "\"Uses\":0"), "Uses");
Rejected("fractional uses", Entry("t.x").Replace("\"Uses\":3", "\"Uses\":1.5"), "Uses");
Rejected("delay as text", Entry("t.x").Replace("\"CallInDelay\":5", "\"CallInDelay\":\"5\""), "CallInDelay");
Rejected("icon with a folder", Entry("t.x", extra: ",\"Icon\":\"icons/a.png\""), "Icon");
Rejected("icon that is not a png", Entry("t.x", extra: ",\"Icon\":\"a.jpg\""), "Icon");
Rejected("icon leaving the folder", Entry("t.x", extra: ",\"Icon\":\"..\\\\a.png\""), "Icon");
Rejected("empty description", Entry("t.x", extra: ",\"Description\":\"\""), "Description");
Rejected("description as a number", Entry("t.x", extra: ",\"Description\":5"), "Description");
Rejected("description too long", Entry("t.x", extra: ",\"Description\":\"" + new string('a', DefinitionParser.MaxDescriptionLength + 1) + "\""), "Description");
{
    var shown = Parse("d.json", Entry("t.d", extra: ",\"Icon\":\"Drop.PNG\",\"Description\":\"TWO AMMO PACKS\\nLANDS AFTER 5 SECONDS\""));
    Check(shown.Errors.Count == 0 && shown.Definitions[0].Icon == "Drop.PNG" && shown.Definitions[0].Description == "TWO AMMO PACKS\nLANDS AFTER 5 SECONDS", "Icon file name and Description are kept");
    var bare = Parse("d.json", Entry("t.d")).Definitions[0];
    Check(bare.Icon == "" && bare.Description == "", "Icon and Description default to empty");
    var tinted = Parse("c.json", Entry("t.c", extra: ",\"BeamColour\":\"#3fA9ff\"")).Definitions[0];
    Check(tinted.BeamColour == 0x3FA9FFu && tinted.BeamRgb == 0x3FA9FFu, "BeamColour #RRGGBB is read in either case");
    Check(bare.BeamColour == null && bare.BeamRgb == 0x3FA9FF, "BeamColour left out uses the category's colour");
    Check(HashOf(Entry("t.c")) != HashOf(Entry("t.c", extra: ",\"BeamColour\":\"#FFFFFF\"")), "BeamColour changes the set hash");
}
Rejected("beam colour without #", Entry("t.x", extra: ",\"BeamColour\":\"FFFFFF\""), "BeamColour");
Rejected("beam colour with alpha", Entry("t.x", extra: ",\"BeamColour\":\"#FFFFFFFF\""), "BeamColour");
Rejected("beam colour not hex", Entry("t.x", extra: ",\"BeamColour\":\"#GG0000\""), "BeamColour");
Rejected("beam colour as a number", Entry("t.x", extra: ",\"BeamColour\":16777215"), "BeamColour");
{
    var rated = Parse("s.json", Entry("t.s", extra: ",\"Stats\":[{\"Name\":\"Armor\",\"Value\":4},{\"Name\":\"Speed\",\"Value\":0,\"Max\":10}]"));
    var stats = rated.Definitions.Count == 1 ? rated.Definitions[0].Stats : Array.Empty<OrdnanceStat>();
    Check(rated.Errors.Count == 0 && stats.Count == 2 && stats[0].Name == "Armor" && stats[0].Value == 4 && stats[0].Max == DefinitionParser.DefaultStatBoxes
          && stats[1].Value == 0 && stats[1].Max == 10, "Stats rows keep name, value and max; max defaults to five boxes");
    Check(Parse("s.json", Entry("t.s")).Definitions[0].Stats.Count == 0, "Stats default to none");
    Check(HashOf(Entry("t.s")) != HashOf(Entry("t.s", extra: ",\"Stats\":[{\"Name\":\"Armor\",\"Value\":4}]")), "Stats change the set hash");
}
Rejected("stat value above max", Entry("t.x", extra: ",\"Stats\":[{\"Name\":\"Armor\",\"Value\":6}]"), "Stats[0].Value");
Rejected("stat value not whole", Entry("t.x", extra: ",\"Stats\":[{\"Name\":\"Armor\",\"Value\":2.5}]"), "Stats[0].Value");
Rejected("stat max too large", Entry("t.x", extra: ",\"Stats\":[{\"Name\":\"Armor\",\"Value\":1,\"Max\":11}]"), "Stats[0].Max");
Rejected("stat without name", Entry("t.x", extra: ",\"Stats\":[{\"Value\":1}]"), "Stats[0].Name");
Rejected("stat name too long", Entry("t.x", extra: ",\"Stats\":[{\"Name\":\"" + new string('A', DefinitionParser.MaxStatNameLength + 1) + "\",\"Value\":1}]"), "Stats[0].Name");
Rejected("stat unknown field", Entry("t.x", extra: ",\"Stats\":[{\"Name\":\"A\",\"Value\":1,\"Colour\":1}]"), "Stats[0].Colour");
Rejected("stats not an array", Entry("t.x", extra: ",\"Stats\":{}"), "Stats");
Rejected("too many stats", Entry("t.x", extra: ",\"Stats\":[" + string.Join(",", Enumerable.Repeat("{\"Name\":\"A\",\"Value\":1}", DefinitionParser.MaxStats + 1)) + "]"), "Stats");
{
    var open = Parse("u.json", Entry("t.u").Replace(",\"Uses\":3", "")).Definitions;
    Check(open.Count == 1 && open[0].Unlimited && open[0].Uses == OrdnanceDefinition.UnlimitedUses, "Uses left out means unlimited");
    Check(!Parse("u.json", Entry("t.u")).Definitions[0].Unlimited, "Uses given is limited");
}
Check(Parse("x.json", "42").Errors.Count == 1, "non-object entry is reported");
Check(DefinitionParser.ParseFile("t.json", "{}", payloads, categories).Errors[0].StartsWith("t.json:"), "non-array file is reported");
Check(DefinitionParser.ParseFile("t.json", "[", payloads, categories).Errors.Count == 1, "malformed JSON is reported, not thrown");

// ---- hash and set ------------------------------------------------------------------------------------------

Check(ForgeCore.Fnv.Hash32("Arsenal.Strike") == 0x01175927u, "a payload type travels as the FNV-1a 32 of its name (the key every peer computes)");

{
    // A real collision, found by birthday search over short ids (32-bit space, ~80k tries expected).
    var seen = new Dictionary<uint, string>();
    string? first = null, second = null;
    for (var i = 0; first == null; i++)
    {
        var id = "t.id" + i;
        var h = ForgeCore.Fnv.Hash32(id);
        if (seen.TryGetValue(h, out var other)) { first = other; second = id; }
        else seen[h] = id;
    }
    var clash = Parse("c.json", Entry(first!), Entry(second!), Entry("t.fine"));
    var errors = new List<string>();
    var set = DefinitionSet.Build(clash.Definitions, errors);
    Check(errors.Count == 1 && errors[0].Contains("network hash") && errors[0].Contains(second!), "hash collision inside a set is reported");
    Check(set.Definitions.Count == 2 && set.TryGetById(first!, out _) && !set.TryGetById(second!, out _), "the colliding later entry is skipped");
}

{
    var errors = new List<string>();
    var set = DefinitionSet.Build(Parse("a.json", Entry("t.dup")).Definitions.Concat(Parse("b.json", Entry("t.dup", "[\"Left\"]")).Definitions), errors);
    Check(errors.Count == 1 && errors[0].StartsWith("b.json:") && errors[0].Contains("a.json"), "duplicate Id across files is reported with both files");
    Check(set.Definitions.Count == 1 && set.Definitions[0].SourceFile == "a.json", "the first duplicate wins");
}

ulong HashOf(params string[] entries)
{
    var result = Parse("h.json", entries);
    Check(result.Errors.Count == 0, "hash fixture parses: " + string.Join(";", result.Errors));
    return DefinitionSet.Build(result.Definitions, new List<string>()).Hash;
}

{
    string Seed(string number) => Entry("t.seed", payload: "{\"Type\":\"Stub\",\"Count\":2,\"Seed\":" + number + "}");
    foreach (var (a, b) in new[]
    {
        ("2", "2.0"), ("2", "20e-1"), ("123.4500", "12345e-2"), ("-0.0", "0e400"),
        ("10e999999999999999999999", "1e1000000000000000000000")
    })
        Check(HashOf(Seed(a)) == HashOf(Seed(b)), "equivalent JSON numbers hash equally: " + a + " / " + b);
    foreach (var (a, b) in new[]
    {
        ("9007199254740992", "9007199254740993"), ("-9007199254740992", "-9007199254740993"),
        ("0.123456789012345678901234567890", "0.123456789012345678901234567891"),
        ("1e400", "2e400"), ("1e-400", "2e-400")
    })
        Check(HashOf(Seed(a)) != HashOf(Seed(b)), "distinct JSON numbers hash differently: " + a + " / " + b);
}

{
    // These sets had identical delimiter-based encodings: the name injected a whole record boundary.
    const string suffix = "|0|||1,2|60|3|5|Stub|{\"Count\":2,\"Type\":\"Stub\"}\n";
    var a = Entry("t.a", extra: ",\"Name\":\"Alpha\"");
    var b = Entry("t.b", extra: ",\"Name\":\"Beta\"");
    var injected = Entry("t.a", extra: ",\"Name\":" + JsonSerializer.Serialize("Alpha" + suffix + "t.b|Beta"));
    Check(HashOf(injected) != HashOf(a, b), "pipes and newlines in a name cannot inject a second definition");

    var fieldA = Entry("t.a", extra: ",\"Name\":\"Alpha|0|a.png\",\"Icon\":\"b.png\"");
    var fieldB = Entry("t.a", extra: ",\"Name\":\"Alpha\",\"Icon\":\"a.png\",\"Description\":\"0|b.png|\"");
    Check(HashOf(fieldA) != HashOf(fieldB), "pipes cannot move content between definition fields");
}

{
    var a = Entry("t.a", payload: "{\"Type\":\"Stub\",\"Count\":2}");
    var b = Entry("t.b", "[\"Left\"]");
    Check(HashOf(a, b) == HashOf(b, a), "set hash does not depend on definition order");
    Check(HashOf(a) == HashOf(Entry("t.a", payload: "{ \"Count\": 2, \"Type\": \"Stub\" }").Replace("\"Cooldown\":60", "\"Cooldown\":60.0")), "set hash is the same for reordered members and another number spelling");
    Check(HashOf(a) != HashOf(Entry("t.a", payload: "{\"Type\":\"Stub\",\"Count\":3}")), "a payload parameter changes the set hash");
    Check(HashOf(a) != HashOf(Entry("t.a", "[\"Down\",\"Up\"]")), "the code changes the set hash");
    Check(HashOf(a) != HashOf(a.Replace("\"Cooldown\":60", "\"Cooldown\":61")), "the cooldown changes the set hash");
    Check(HashOf(a) != HashOf(a, b), "adding a definition changes the set hash");
    Check(HashOf(a) != HashOf(Entry("t.a", payload: "{\"Type\":\"Stub\",\"Count\":2}", extra: ",\"Description\":\"x\"")), "the description changes the set hash");
    Check(HashOf(a) != HashOf(Entry("t.a", payload: "{\"Type\":\"Stub\",\"Count\":2}", extra: ",\"Icon\":\"a.png\"")), "the icon changes the set hash");
    Check(HashOf(a) != HashOf(a.Replace(",\"Uses\":3", "")), "leaving Uses out changes the set hash");
}

// ---- settings ----------------------------------------------------------------------------------------------

{
    Check(OrdnanceSettings.Default.For(null).LoadoutSize == 4 && OrdnanceSettings.Default.For("x").Allowed == null, "defaults: size 4, everything allowed");
    var errors = new List<string>();
    var settings = OrdnanceSettings.Parse("{\"LoadoutSize\":3,\"CooldownMultiplier\":0.5,\"Allowed\":[\"t.a\",\"t.b\"],\"Levels\":{\"R1A1\":{\"LoadoutSize\":2,\"Allowed\":null,\"UsesMultiplier\":2}}}", errors);
    Check(settings != null && errors.Count == 0, "settings parse");
    Check(settings!.For("Other").LoadoutSize == 3 && settings.For("Other").CooldownMultiplier == 0.5 && settings.For("Other").IsAllowed("t.a") && !settings.For("Other").IsAllowed("z"), "global rules apply to other levels");
    var level = settings.For("R1A1");
    Check(level.LoadoutSize == 2 && level.UsesMultiplier == 2 && level.CooldownMultiplier == 0.5 && level.IsAllowed("z"), "level override replaces only what it names");
    var bad = new List<string>();
    Check(OrdnanceSettings.Parse("{\"LoadoutSize\":99,\"Bogus\":1,\"Levels\":{\"L\":{\"CooldownMultiplier\":-1}}}", bad) == null && bad.Count == 3, "invalid settings report every problem and yield nothing: " + string.Join(" | ", bad));
    var set = DefinitionSet.Build(Parse("a.json", Entry("t.a")).Definitions, new List<string>());
    Check(settings.UnknownAllowed(set).SequenceEqual(new[] { "t.b" }), "Allowed ids no definition has are listed");
    Check(!ForgeOrdnance.OrdnanceApi.TryGetDefinition("t.a", out _), "no definition is found before definitions load");
    ForgeOrdnance.OrdnanceContent.Set = set;
    var found = ForgeOrdnance.OrdnanceApi.TryGetDefinition("t.a", out var byId);
    var foundKey = found && ForgeOrdnance.OrdnanceApi.TryGetDefinition(byId.Key, out var byKey) && ReferenceEquals(byId, byKey);
    Check(foundKey && !ForgeOrdnance.OrdnanceApi.TryGetDefinition(byId.Key + 1, out _) && !ForgeOrdnance.OrdnanceApi.TryGetDefinition("t.none", out _), "a definition is found by Id and by Key, both giving the same one");
    ForgeOrdnance.OrdnanceContent.Set = DefinitionSet.Empty;
}

// ---- matching and loadout ----------------------------------------------------------------------------------

OrdnanceDefinition Def(string id, params Direction[] code)
{
    var codeText = "[" + string.Join(",", code.Select(c => "\"" + c + "\"")) + "]";
    var result = Parse("m.json", Entry("t." + id, codeText));
    Check(result.Errors.Count == 0, "definition fixture parses");
    return result.Definitions[0];
}

var up = Direction.Up; var down = Direction.Down; var left = Direction.Left; var right = Direction.Right;
var resupply = Def("res", down, down, up, right);
var turret = Def("tur", down, right, down, up);
var eagle = Def("eag", up, right, down);
var loadout = new[] { resupply, turret, eagle };

Check(CommandMatcher.Match(loadout, Array.Empty<Direction>()).Kind == MatchKind.Prefix, "empty input still has candidates");
Check(CommandMatcher.Match(loadout, new[] { down }).Kind == MatchKind.Prefix, "shared first step is a prefix");
Check(CommandMatcher.Match(loadout, new[] { down, down }).Kind == MatchKind.Prefix, "second step narrows");
{
    var exact = CommandMatcher.Match(loadout, new[] { down, right, down, up });
    Check(exact.Kind == MatchKind.Exact && exact.Index == 1, "full code is an exact match for its slot");
    Check(CommandMatcher.Match(loadout, new[] { up, right, down }).Index == 2, "shorter code matches at its own length");
}
Check(CommandMatcher.Match(loadout, new[] { left }).Kind == MatchKind.None, "a wrong first step matches nothing");
Check(CommandMatcher.Match(loadout, new[] { down, up }).Kind == MatchKind.None, "a wrong later step matches nothing");
Check(CommandMatcher.Match(loadout, new[] { up, right, down, up }).Kind == MatchKind.None, "input longer than any code matches nothing");
Check(CommandMatcher.Match(Array.Empty<OrdnanceDefinition>(), new[] { up }).Kind == MatchKind.None, "empty loadout matches nothing");

{
    var set = DefinitionSet.Build(loadout, new List<string>());
    var rules = new OrdnanceLevelRules { LoadoutSize = 3 };
    Check(LoadoutValidator.Validate(new[] { resupply.Key, turret.Key }, set, rules).Ok, "valid loadout is accepted");
    Check(LoadoutValidator.Validate(new[] { resupply.Key, turret.Key, eagle.Key, 1u }, set, rules).Reason == Rejection.LoadoutInvalid, "over the size limit is refused");
    Check(LoadoutValidator.Validate(new[] { 12345u }, set, rules).Reason == Rejection.UnknownDefinition, "unknown key is refused");
    Check(LoadoutValidator.Validate(new[] { resupply.Key, resupply.Key }, set, rules).Reason == Rejection.LoadoutInvalid, "repeat is refused");
    Check(LoadoutValidator.Validate(new[] { resupply.Key }, set, new OrdnanceLevelRules { Allowed = new[] { "t.tur" } }).Reason == Rejection.LoadoutInvalid, "not in Allowed is refused");
    var prefixed = Def("pre", down, down);
    var withPrefix = DefinitionSet.Build(new[] { resupply, prefixed }, new List<string>());
    var verdict = LoadoutValidator.Validate(new[] { resupply.Key, prefixed.Key }, withPrefix, rules);
    Check(verdict.Reason == Rejection.LoadoutInvalid && verdict.Detail.Contains("beginning"), "a code that begins another is refused");

    var slots = new OrdnanceDefinition?[] { resupply, null, turret, null };
    Check(LoadoutValidator.CodeConflict(slots, 1, eagle) == null, "a code clashing with nothing fits the chosen slot");
    Check(LoadoutValidator.CodeConflict(slots, 1, prefixed) == resupply, "a code that begins a slotted code is named as the clash");
    Check(LoadoutValidator.CodeConflict(slots, 0, prefixed) == null, "the slot being replaced does not clash");
    Check(LoadoutValidator.CodeConflict(slots, 3, Def("longer", down, down, up, right, left)) == resupply, "a slotted code that begins the candidate clashes");
    Check(LoadoutValidator.CodeConflict(slots, 1, Def("same", down, down, up, right)) == resupply, "an equal code clashes");
    Check(LoadoutValidator.CodeConflict(slots, 1, turret) == null, "moving a slotted definition does not clash with itself");
}

// ---- saved lobby loadout -----------------------------------------------------------------------------------

{
    var errors = new List<string>();
    var back = LoadoutFile.Clean(new string?[] { "a.one", null, "b.two" }, errors);
    Check(errors.Count == 0 && back.Length == 3 && back[0] == "a.one" && back[1] == null && back[2] == "b.two", "saved loadout keeps its slots, empty ones included");
    var odd = LoadoutFile.Clean(new string?[] { "a", "", null }, errors);
    Check(odd.Length == 3 && odd[1] == null && odd[2] == null && errors.Count == 1, "a slot that is not an Id is reported and left empty");
    errors.Clear();
    Check(LoadoutFile.Clean(null, errors).Length == 0 && errors.Count == 1, "a file without Slots is reported");
    errors.Clear();
    var many = LoadoutFile.Clean(Enumerable.Repeat("x", Messages.MaxLoadout + 2).ToArray(), errors);
    Check(many.Length == Messages.MaxLoadout && errors.Count == 1, "more slots than the protocol carries are cut and reported");}

// ---- slots -------------------------------------------------------------------------------------------------

{
    const ulong p1 = 100, p2 = 200;
    var book = new SlotBook();
    book.SetLoadout(p1, new[] { resupply, turret }, 1.0);
    book.SetLoadout(p2, new[] { eagle }, 2.0);
    Check(book.Check(p1, resupply.Key, 0) == Rejection.None, "fresh slot can be called");
    Check(book.Check(p1, eagle.Key, 0) == Rejection.NotInLoadout, "another player's call-in is not in the loadout");
    Check(book.Check(999, resupply.Key, 0) == Rejection.NotInLoadout, "unknown player has no loadout");
    book.TryGet(p2, eagle.Key, 0, out var uses2, out _);
    Check(uses2 == 6, "uses multiplier applies to a first-seen slot");

    book.Consume(p1, resupply.Key, 10, 60);
    book.TryGet(p1, resupply.Key, 10, out var uses, out var cooldown);
    Check(uses == 2 && cooldown == 60, "consume spends a use and starts the cooldown");
    Check(book.Check(p1, resupply.Key, 69.9) == Rejection.Cooldown, "refused while cooling down");
    Check(book.Check(p1, resupply.Key, 70) == Rejection.None, "allowed again when the cooldown ends");
    Check(book.Check(p2, eagle.Key, 11) == Rejection.None, "players cool down independently");
    Check(book.Check(p1, turret.Key, 11) == Rejection.None, "slots cool down independently");
    bool threw = false;
    try { book.Consume(p1, resupply.Key, 11, 60); } catch (InvalidOperationException) { threw = true; }
    Check(threw, "consume refuses to bypass the cooldown check");

    for (var t = 100; book.Check(p1, resupply.Key, t) == Rejection.None; t += 100) book.Consume(p1, resupply.Key, t, 1);
    Check(book.Check(p1, resupply.Key, 1e6) == Rejection.NoUses, "uses run out");

    // Swapping a call-in out and back in does not refresh it.
    book.SetLoadout(p1, new[] { turret }, 1.0);
    Check(book.Check(p1, resupply.Key, 1e6) == Rejection.NotInLoadout, "removed from the loadout");
    book.SetLoadout(p1, new[] { resupply, turret }, 1.0);
    Check(book.Check(p1, resupply.Key, 1e6) == Rejection.NoUses, "re-adding keeps the spent uses");

    // A swap clears the cooldown, and a resubmitted identical loadout does not.
    book.SetLoadout(p1, new[] { turret }, 1.0);
    book.SetLoadout(p1, new[] { turret }, 1.0);
    book.Consume(p1, turret.Key, 2e6, 60);
    book.SetLoadout(p1, new[] { turret }, 1.0);
    book.TryGet(p1, turret.Key, 2e6, out _, out var keptCooldown);
    Check(keptCooldown == 60, "an unchanged loadout keeps the cooldown");
    book.SetLoadout(p1, new[] { resupply }, 1.0);
    book.SetLoadout(p1, new[] { turret }, 1.0);
    book.TryGet(p1, turret.Key, 2e6, out _, out var clearedCooldown);
    Check(clearedCooldown == 0, "swapping out and back clears the cooldown");
    book.SetLoadout(p1, new[] { resupply, turret }, 1.0);

    // Mirror application.
    var mirror = new SlotBook();
    mirror.ApplyLoadout(p1, new[] { resupply.Key });
    mirror.Apply(p1, resupply.Key, 1, 30, 500);
    Check(mirror.Check(p1, resupply.Key, 529) == Rejection.Cooldown && mirror.Check(p1, resupply.Key, 530) == Rejection.None, "mirror cooldown counts from its own clock");
    mirror.Apply(p1, resupply.Key, 0, 0, 500);
    Check(mirror.Check(p1, resupply.Key, 500) == Rejection.NoUses, "mirror follows the host's uses");
}

{
    // Unlimited call-ins: only the cooldown limits them, whatever the uses multiplier.
    const ulong p = 300;
    var unlimited = Parse("u.json", Entry("t.unl").Replace(",\"Uses\":3", "")).Definitions[0];
    var book = new SlotBook();
    book.SetLoadout(p, new[] { unlimited }, 0.5);
    for (var t = 0; t < 50; t++)
    {
        Check(book.Check(p, unlimited.Key, t * 100.0) == Rejection.None, "unlimited call " + t + " is allowed once off cooldown");
        book.Consume(p, unlimited.Key, t * 100.0, 60);
    }
    Check(book.Check(p, unlimited.Key, 4930) == Rejection.Cooldown, "an unlimited call still cools down");
    book.TryGet(p, unlimited.Key, 4930, out var usesLeft, out _);
    Check(usesLeft < 0, "unlimited uses read as negative");
    var image = book.Capture(5000);
    book.Restore(image, 9000);
    book.TryGet(p, unlimited.Key, 9000, out usesLeft, out _);
    Check(usesLeft < 0 && book.Check(p, unlimited.Key, 9000) == Rejection.None, "restore keeps a slot unlimited");
    var mirror = new SlotBook();
    mirror.ApplyLoadout(p, new[] { unlimited.Key });
    mirror.Apply(p, unlimited.Key, OrdnanceDefinition.UnlimitedUses, 0, 0);
    Check(mirror.Check(p, unlimited.Key, 0) == Rejection.None, "a mirror slot the host reports as unlimited is callable");
}

// ---- item lists --------------------------------------------------------------------------------------------

{
    uint? Known(uint? id, string? name) => id is 101 or 102 or 127 ? id : name == "MediPack" ? 102u : null;
    JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    ItemEntry[] Items(string list) => ItemList.Parse(Json("{\"Type\":\"Item\",\"Items\":" + list + "}"), Known);
    string Refused(string list)
    {
        try { Items(list); return "(accepted)"; }
        catch (ForgeCore.StrictJsonException error) { return error.Field; }
    }

    var mixed = Items("[{\"Item\":101,\"Count\":2},{\"Item\":\"MediPack\"},{\"Item\":127,\"AmmoRatio\":0.5}]");
    Check(mixed.Length == 3 && mixed[0] == new ItemEntry(101, 2, 1f) && mixed[1] == new ItemEntry(102, 1, 1f) && mixed[2] == new ItemEntry(127, 1, 0.5f),
        "a mixed list keeps each item, count and ratio, by id or name");
    var ids = ItemList.Expand(mixed, out var ratios);
    Check(ids.SequenceEqual(new uint[] { 101, 101, 102, 127 }) && ratios.SequenceEqual(new[] { 1f, 1f, 1f, 0.5f }), "the list expands to one pickup per anchor in order");
    Check(Refused("[{\"Item\":101,\"Count\":3},{\"Item\":102,\"Count\":2}]") == "Items", "more pickups than the pod holds are refused");
    Check(Refused("[]") == "Items", "an empty list is refused");
    Check(Refused("[{\"Item\":101},{\"Item\":555}]") == "Items[1].Item", "an unknown item names its entry");
    Check(Refused("[{\"Item\":101,\"Count\":1.5}]") == "Items[0].Count", "a fractional count is refused");
    Check(Refused("[{\"Item\":101,\"Scatter\":1}]") == "Items[0].Scatter", "an unknown entry field is refused");
    Check(Refused("[{\"Item\":101,\"AmmoRatio\":2}]") == "Items[0].AmmoRatio", "a ratio beyond 1 is refused");
    Check(Refused("[5]") == "Items[0]", "an entry that is not an object is refused");
    bool threw = false;
    try { ItemList.Parse(Json("{\"Type\":\"Item\",\"Item\":101}"), Known); } catch (ForgeCore.StrictJsonException error) { threw = error.Field == "Item"; }
    Check(threw, "the single-item form is not a field of the list form");
    Check(ItemList.AmmoPoints(0, 1f) == 100f && ItemList.AmmoPoints(0, 0.6f) == 60f && ItemList.AmmoPoints(0, 0.5f) == 60f, "a resource pack is given in whole 20-point uses out of 100");
    Check(ItemList.AmmoPoints(0, 0.01f) == 20f && ItemList.AmmoPoints(0, 0f) == 0f, "a small share still gives one use; only a zero share is empty");
    Check(ItemList.AmmoPoints(3, 1f) == 3f && ItemList.AmmoPoints(3, 0.5f) == 2f && ItemList.AmmoPoints(3, 0.1f) == 1f && ItemList.AmmoPoints(1, 1f) == 1f, "a consumable is given in whole uses, at least one");
}

// ---- snapshot and restore ----------------------------------------------------------------------------------

{
    const ulong p1 = 1, p2 = 2;
    var book = new SlotBook();
    book.SetLoadout(p1, new[] { resupply, turret }, 1.0);
    book.SetLoadout(p2, new[] { eagle }, 1.0);
    book.Consume(p1, resupply.Key, 100, 120);           // cooling until 220 at clock 100
    var snapshot = book.Capture(150);                    // 70 s of cooldown remain, 2 uses left
    book.Consume(p1, turret.Key, 160, 30);
    book.Consume(p2, eagle.Key, 160, 30);
    book.Consume(p1, resupply.Key, 400, 5);              // after the cooldown, spends another use
    book.Restore(snapshot, 1000);                        // restored on a later clock
    book.TryGet(p1, resupply.Key, 1000, out var uses, out var cooldown);
    Check(uses == resupply.Uses - 1 && Math.Abs(cooldown - 70) < 1e-9, "restore returns uses and the remaining cooldown on the new clock");
    book.TryGet(p1, turret.Key, 1000, out uses, out cooldown);
    Check(uses == turret.Uses && cooldown == 0, "a use spent after the snapshot is given back");
    book.TryGet(p2, eagle.Key, 1000, out uses, out cooldown);
    Check(uses == eagle.Uses && cooldown == 0, "every player's slots are restored");

    var later = new SlotBook();
    later.SetLoadout(p1, new[] { resupply }, 1.0);
    var empty = later.Capture(0);
    later.SetLoadout(p1, new[] { resupply, turret }, 1.0);
    later.Consume(p1, turret.Key, 5, 10);
    later.Restore(empty, 50);
    later.TryGet(p1, turret.Key, 50, out uses, out cooldown);
    Check(uses == turret.Uses && cooldown == 0, "a slot created after the snapshot is unused again");
}

{
    var scheduler = new SummonScheduler();
    scheduler.Add(new PendingSummon(1, 1, 7, 0, 0, 0, 15));
    scheduler.Add(new PendingSummon(1, 2, 7, 0, 0, 0, 12));
    scheduler.Add(new PendingSummon(2, 1, 7, 0, 0, 0, 40));
    bool threw = false;
    try { scheduler.Add(new PendingSummon(1, 1, 7, 0, 0, 0, 99)); } catch (InvalidOperationException) { threw = true; }
    Check(threw && scheduler.Has(1, 1) && !scheduler.Has(3, 1), "a throw id is scheduled once per thrower");
    var due = new List<PendingSummon>();
    scheduler.TakeDue(11, due);
    Check(due.Count == 0, "nothing due early");
    scheduler.TakeDue(20, due);
    Check(due.Count == 2 && due[0].ThrowId == 2 && due[1].ThrowId == 1 && scheduler.Count == 1, "due summons come out in arrival order and leave the queue");
    Check(scheduler.Clear() == 1 && scheduler.Count == 0, "clear voids what is left and counts it");
}

// A due batch is a detached snapshot: callbacks may add/clear pending calls without changing it.
{
    var scheduler = new SummonScheduler();
    var due = new List<PendingSummon>();
    scheduler.Add(new PendingSummon(1, 1, 7, 0, 0, 0, 15));
    scheduler.Add(new PendingSummon(1, 2, 7, 0, 0, 0, 12));
    scheduler.Add(new PendingSummon(1, 3, 7, 0, 0, 0, 12));
    scheduler.Add(new PendingSummon(1, 4, 7, 0, 0, 0, 40));
    scheduler.TakeDue(15, due);
    Check(due.Select(p => p.ThrowId).SequenceEqual(new uint[] { 2, 3, 1 }), "equal arrival times preserve acceptance order");
    var delivered = new List<uint>();
    foreach (var summon in due)
    {
        delivered.Add(summon.ThrowId);
        scheduler.Clear();
        scheduler.Add(new PendingSummon(2, summon.ThrowId, 7, 0, 0, 0, 0));
    }
    Check(delivered.SequenceEqual(new uint[] { 2, 3, 1 }) && scheduler.Count == 1, "callbacks changing the schedule cannot corrupt the detached due batch");
    scheduler.TakeDue(15, due);
    Check(due.Count == 1 && due[0].Thrower == 2 && due[0].ThrowId == 1, "callback additions wait for the next due batch");
    scheduler.TakeDue(15, due);
    Check(due.Count == 0, "reused due buffer does not replay the previous batch");
    scheduler.Add(new PendingSummon(1, 9, 7, 0, 0, 0, 100));
    for (var i = 0; i < 100; i++) scheduler.TakeDue(50, due);
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 1000; i++) scheduler.TakeDue(50, due);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated == 0 && scheduler.Count == 1 && due.Count == 0, "1000 idle ticks with a future call allocate zero bytes and keep the call");
    scheduler.Clear();
    before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 1000; i++) scheduler.TakeDue(50, due);
    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated == 0 && due.Count == 0, "1000 empty scheduler ticks allocate zero bytes");
}
{
    var book = new SlotBook();
    var version = book.LoadoutVersion;
    book.SetLoadout(1, new[] { resupply }, 1);
    Check(book.LoadoutVersion != version, "host loadout replacement invalidates resolved loadouts");
    version = book.LoadoutVersion;
    book.Consume(1, resupply.Key, 0, 10);
    book.Apply(1, resupply.Key, 1, 3, 0);
    book.Restore(book.Capture(0), 5);
    Check(book.LoadoutVersion == version, "slot-only changes leave resolved loadouts valid");
    book.ApplyLoadout(1, new[] { turret.Key });
    Check(book.LoadoutVersion != version, "mirror loadout replacement invalidates resolved loadouts");
    version = book.LoadoutVersion;
    book.RemovePlayer(1);
    Check(book.LoadoutVersion != version, "removing a player invalidates resolved loadouts");
    version = book.LoadoutVersion;
    book.Clear();
    Check(book.LoadoutVersion != version, "session reset invalidates even an empty resolved loadout");
}

{
    // A second submission in a level: the host validates and replaces the loadout, spent uses stay spent, and the version moves so
    // every end re-resolves; the submission waits for a call in progress.
    const ulong p = 7;
    var set = DefinitionSet.Build(loadout, new List<string>());
    var rules = new OrdnanceLevelRules { LoadoutSize = 3 };
    var book = new SlotBook();
    book.SetLoadout(p, LoadoutValidator.Validate(new[] { resupply.Key }, set, rules).Accepted, 1.0);
    book.Consume(p, resupply.Key, 0, 60);
    var version = book.LoadoutVersion;
    var second = LoadoutValidator.Validate(new[] { turret.Key }, set, rules);
    Check(second.Ok, "a second in-level submission is validated like the first");
    book.SetLoadout(p, second.Accepted, 1.0);
    Check(book.LoadoutVersion != version && book.Check(p, resupply.Key, 1e6) == Rejection.NotInLoadout && book.Check(p, turret.Key, 1e6) == Rejection.None,
        "the second submission replaces the loadout and moves its version");
    book.SetLoadout(p, LoadoutValidator.Validate(new[] { resupply.Key }, set, rules).Accepted, 1.0);
    Check(book.Check(p, resupply.Key, 1) == Rejection.None, "swapping back finds the cooldown cleared");

    var submission = new LoadoutSubmission();
    Check(submission.Due(true, false) && !submission.Due(false, false), "the first submission waits for the host");
    submission.Sent();
    Check(!submission.Due(true, false), "nothing is sent again while the picks are unchanged");
    submission.Changed();
    Check(!submission.Due(true, true) && submission.Due(true, false), "a change made while a call is in progress is sent when the call ends");
    submission.Sent(); submission.Reset();
    Check(submission.Due(true, false), "a new level or host asks for the loadout again");
}

// ---- host validation order ---------------------------------------------------------------------------------

{
    const ulong p = 5;
    var book = new SlotBook();
    book.SetLoadout(p, new[] { resupply }, 1.0);
    var origin = new Vec3(0, 0, 0);
    var near = new Vec3(10, 0, 0);
    var reached = 0;
    Func<bool> reachable = () => { reached++; return true; };
    Rejection Eval(bool compatible, uint key, Vec3 landing, Func<bool> r, double now = 0)
        => ThrowValidator.Evaluate(book, compatible, true, p, key, origin, landing, now, 80, r);

    Check(Eval(true, resupply.Key, near, reachable) == Rejection.None && reached == 1, "valid throw passes");
    Check(Eval(false, resupply.Key, near, reachable) == Rejection.DefinitionMismatch, "incompatible peer is refused first");
    Check(Eval(true, eagle.Key, near, reachable) == Rejection.NotInLoadout, "call-in outside the loadout is refused");
    Check(Eval(true, resupply.Key, new Vec3(80.5f, 0, 0), reachable) == Rejection.TooFar, "too far from the throw is refused");
    Check(Eval(true, resupply.Key, new Vec3(float.NaN, 0, 0), reachable) == Rejection.InvalidLanding, "non-finite landing is refused");
    reached = 0;
    Check(Eval(true, resupply.Key, near, () => false) == Rejection.InvalidLanding, "unreachable landing is refused");
    book.Consume(p, resupply.Key, 0, 50);
    reached = 0;
    Check(Eval(true, resupply.Key, near, reachable, 1) == Rejection.Cooldown && reached == 0, "cooldown is refused before the level is queried");
}

// ---- round-two behavioural regressions ---------------------------------------------------------------------
{
    foreach (var id in new uint[] { 1, 65535 })
        Check(PayloadReader.ReplicatedPrefabId(id, "Item") == id, "replicated pickup boundary accepted " + id);
    foreach (var id in new uint[] { 65536, 70000, uint.MaxValue })
    {
        var rejected = false;
        try { PayloadReader.ReplicatedPrefabId(id, "Item"); }
        catch (ForgeCore.StrictJsonException error) { rejected = error.Field == "Item"; }
        Check(rejected, "out-of-range native pickup rejected before truncation " + id);
    }
    var literal = Entry("t.escape", payload: "{\"Type\":\"Stub\",\"Count\":2,\"Text\":\"Item/汉字\"}");
    var escaped = literal.Replace("Stub", "\\u0053tub").Replace("Item/汉字", "\\u0049tem\\/\\u6c49\\u5b57");
    Check(HashOf(literal) == HashOf(escaped), "decoded-equivalent payload strings and escapes have equal hashes");
    Check(HashOf(literal) != HashOf(literal.Replace("Item/", "Other/")), "different decoded payload strings still change the hash");

    var history = new ThrowHistory();
    Check(history.Floor(7) == 0 && history.Add(7, 1) && history.Add(7, 40), "first process reports advance resume floor");
    Check(!history.Add(7, 1) && history.Floor(7) == 40, "duplicate report cannot rewind resume floor");
    Check(history.Add(7, history.Floor(7) + 1), "restarted process resumes without losing its first call");
    Check(history.Floor(8) == 0 && history.Add(8, 1), "resume floors are per player");
    history.Clear();
    Check(history.Floor(7) == 0 && history.Add(7, 1), "new level clears throw history");

    var schedule = new SummonScheduler();
    schedule.Add(new PendingSummon(1, 1, 99, 4, 5, 6, 30));
    var replay = schedule.Beacons();
    Check(replay.Length == 1 && replay[0].ArriveAt - 10 == 20 && replay[0].X == 4 && schedule.Count == 1,
        "late join snapshot retains pending call position and remaining countdown without consuming it");
    replay[0] = default;
    Check(schedule.Beacons()[0].ThrowId == 1, "replay snapshot cannot corrupt live pending calls");
    var due = new List<PendingSummon>();
    schedule.TakeDue(30, due);
    Check(due.Count == 1 && schedule.Beacons().Length == 0, "arrived calls stop appearing in pending replay");

    foreach (var pickable in new[] { true, false })
    {
        var marker = SentryIdentity.Marker(0x12345678, pickable);
        Check(marker.Length <= 25 && SentryIdentity.TryMarker(marker, out var identity, out var pickup, out var plainKey)
            && identity == 0x12345678 && pickup == pickable && plainKey == 0, "native sentry identity and pickup policy roundtrip");
        var looked = SentryIdentity.Marker(0x12345678, pickable, 0xCAFE1234u);
        Check(looked.Length <= 25 && SentryIdentity.TryMarker(looked, out identity, out pickup, out var key)
            && identity == 0x12345678 && pickup == pickable && key == 0xCAFE1234u, "the sentry marker carries the look key and still fits the gear name");
    }
    var wide = false; try { SentryIdentity.Marker(0x1_0000_0000UL, true); } catch (ArgumentOutOfRangeException) { wide = true; }
    Check(wide, "an identity beyond the marker's 8 hex digits is refused");
    foreach (var marker in new string?[] { null, "", "Player's sentry", "FO:00000000:P:00000000", "FO:12345678:X:00000000", "FO:1234567g:U:00000000", "FO:12345678:U", "FO:12345678:U:0000000g" })
        Check(!SentryIdentity.TryMarker(marker, out _, out _), "unmarked/malformed sentries retain vanilla ownership");
    // Turret looks: ids, keys, spec problems, the Look payload field.
    var looks = new TurretLookBook();
    Check(TurretLookBook.IsLookId("Arsenal:turret-burst") && !TurretLookBook.IsLookId("turret-burst") && !TurretLookBook.IsLookId("A:") && !TurretLookBook.IsLookId(":b") && !TurretLookBook.IsLookId("A:b:c") && !TurretLookBook.IsLookId("A:b c") && !TurretLookBook.IsLookId(null),
        "a turret look id is written Pack:item");
    Check(looks.Register("Arsenal:turret-burst", new TurretLookSpec { WorldScale = 1.24f }) == null && looks.IsRegistered("Arsenal:turret-burst")
        && looks.TryGetByKey(TurretLookBook.KeyOf("Arsenal:turret-burst"), out var burstId) && burstId == "Arsenal:turret-burst" && looks.TryGet(burstId, out var burstSpec) && burstSpec.WorldScale == 1.24f,
        "a registered look is found by id and by the key its sentries carry");
    Check(looks.Register("Arsenal:turret-burst", new TurretLookSpec()) != null, "a look id cannot be registered twice");
    Check(looks.Register("nope", new TurretLookSpec()) != null, "a malformed look id is refused");
    Check(looks.Register("Arsenal:a", new TurretLookSpec { Muzzle = "head" }) != null && looks.Register("Arsenal:b", new TurretLookSpec { Yaw = "" }) != null
        && looks.Register("Arsenal:c", new TurretLookSpec { WorldScale = 0f }) != null && looks.Register("Arsenal:d", new TurretLookSpec { WorldScale = float.NaN }) != null,
        "a spec with equal or empty part names or an unusable scale is refused");
    Check(!looks.IsRegistered("Arsenal:a"), "a refused look is not registered");
    {
        JsonElement LookJson(string text) => JsonDocument.Parse(text).RootElement.Clone();
        string LookFieldError(string text)
        {
            try { PayloadReader.Look(LookJson(text), looks.IsRegistered); return "(accepted)"; }
            catch (ForgeCore.StrictJsonException error) { return error.Field; }
        }
        Check(PayloadReader.Look(LookJson("{\"Type\":\"Gear\"}"), looks.IsRegistered) == "", "a Gear call-in without Look keeps the game's sentry look");
        Check(PayloadReader.Look(LookJson("{\"Look\":\"Arsenal:turret-burst\"}"), looks.IsRegistered) == "Arsenal:turret-burst", "a registered Look is accepted");
        Check(LookFieldError("{\"Look\":\"Arsenal:turret-unknown\"}") == "Look", "a Look no package registered is refused with its field name");
        Check(LookFieldError("{\"Look\":\"turret-burst\"}") == "Look" && LookFieldError("{\"Look\":5}") == "Look", "a Look that is not Pack:item is refused");
    }

    // The claim book: reservation, confirm, cancel.
    var claims = new ClaimBook();
    claims.Open(1, 7);
    Check(claims.IsOpen(1, 7) && !claims.IsReserved(1, 7), "an opened claimable is open and not reserved");
    Check(claims.TryClaim(1, 7, 100, 1) && claims.IsReserved(1, 7) && claims.Matches(1, 7, 100, 1), "the first request reserves it");
    Check(!claims.TryClaim(1, 7, 101, 1), "a reserved claimable refuses another player");
    claims.Open(1, 8);
    Check(!claims.TryClaim(1, 8, 100, 2), "one player holds one reservation at a time");
    Check(!claims.TryClaim(1, 9, 102, 1) && !claims.TryClaim(1, 8, 102, 0), "an unopened claimable and attempt 0 are refused");
    Check(!claims.Cancel(1, 7, 100, 2) && claims.IsReserved(1, 7), "a cancel for another attempt changes nothing");
    Check(claims.Cancel(1, 7, 100, 1) && !claims.IsReserved(1, 7) && claims.IsOpen(1, 7), "a cancel reopens it for anyone");
    Check(claims.TryClaim(1, 7, 101, 1) && !claims.Confirm(1, 7, 100, 1) && claims.Confirm(1, 7, 101, 1) && !claims.IsOpen(1, 7) && !claims.IsReserved(1, 7), "only the reserving attempt can confirm, and a confirmed claimable closes");
    Check(!claims.TryClaim(1, 7, 103, 1), "a taken claimable cannot be claimed again");
    claims.Close(1, 8);
    Check(!claims.IsOpen(1, 8), "a closed claimable is gone");
    claims.Open(2, 1); claims.Clear();
    Check(!claims.IsOpen(2, 1), "clearing the book drops every claimable");

    Check(SentryIdentity.ResolveTimer(1, 1, 5, 10) == SentryTimerResolution.Bind, "expiry binds only the intended spawn");
    Check(SentryIdentity.ResolveTimer(1, null, 5, 10) == SentryTimerResolution.Wait, "expiry can precede native spawn");
    Check(SentryIdentity.ResolveTimer(1, 2, 5, 10) == SentryTimerResolution.Wait, "reused native key cannot bind a different sentry");
    Check(SentryIdentity.ResolveTimer(1, null, 10, 10) == SentryTimerResolution.Discard, "unresolved expiry exits at deadline");
    Check(SentryIdentity.ResolveTimer(1, 2, 10, 10) == SentryTimerResolution.Discard, "reused key expiry exits without deleting the new sentry");
    // The replicated marker gives a sentry its look and pickup lock; the host's spawn callback registers it once more and must not take them back.
    var fromMarker = new SentryIdentity.SentryMark(7, true, 0xCAFE1234u);
    Check(SentryIdentity.Merge(fromMarker, new SentryIdentity.SentryMark(7, false, 0)) == fromMarker, "turret look: a second registration of the same sentry keeps its look and pickup lock");
    Check(SentryIdentity.Merge(new SentryIdentity.SentryMark(7, false, 0), new SentryIdentity.SentryMark(7, true, 0xCAFE1234u)) == fromMarker, "turret look: a second registration that knows more fills in what the first lacked");
    Check(SentryIdentity.Merge(fromMarker, new SentryIdentity.SentryMark(8, false, 0)) == new SentryIdentity.SentryMark(8, false, 0), "turret look: another sentry's registration replaces the old mark, never inherits its look");
    Check(SentryIdentity.Merge(null, fromMarker) == fromMarker, "turret look: the first registration is kept as given");
}
NetworkChecks.Run(Check);
LobbyChecks.Run(Check);
HostRulesChecks.Run(Check);
DropChecks.Run(Check);
DeliveryChecks.Run(Check);
ApiChecks.Run(Check);
LevelApiChecks.Run(Check);
ImpactNoiseChecks.Run(Check);

{
    var book = new SlotBook();
    book.SetLoadout(1, new[] { resupply }, 1);
    var origin = new Vec3(0, 0, 0);
    Check(ThrowValidator.Evaluate(book, true, true, 1, resupply.Key, origin, new Vec3(5, 0, 0), 0, 5, () => true) == Rejection.None,
        "landing exactly at the configured range remains legal");
    Check(ThrowValidator.Evaluate(book, true, true, 1, resupply.Key, origin, new Vec3(5.01f, 0, 0), 0, 5, () => true) == Rejection.TooFar,
        "configured maximum has no additional range allowance");
    Check(ThrowValidator.Evaluate(book, true, true, 1, resupply.Key, origin, new Vec3(12, 0, 0), 0, 5,
        () => throw new Exception("out-of-range landing reached native navigation")) == Rejection.TooFar,
        "out-of-range call is rejected before expensive native resolution");
}

{
    var history = new ThrowHistory();
    Check(!history.Add(1, 0) && history.Floor(1) == 0, "zero throw id cannot allocate or advance replay history");
    Check(history.Add(1, 12) && history.Add(1, 10) && !history.Add(1, 12), "reversed landings remain valid but duplicates do not");
    for (uint id = 13; id < 10000; id++) history.Add(1, id);
    Check(!history.Add(1, 10) && !history.Add(1, 9999) && history.Floor(1) == 9999, "expired IDs stay rejected after the bounded window rolls over");
    Check(history.Add(1, 10001) && history.Add(1, 10000), "nearby out-of-order landings survive window wrap");
    Check(history.Add(2, 10) && history.Floor(2) == 10, "throw histories are isolated by player");
    history.Clear();
    Check(history.Floor(1) == 0 && history.Add(1, 10), "new level resets both replay floor and window");
}
{
    var first = new PayloadRegistry();
    first.Register("test.Strike", new StubParser());
    first.Register("test.Frame", new StubParser());
    var reordered = new PayloadRegistry();
    reordered.Register("test.Frame", new StubParser());
    reordered.Register("test.Strike", new StubParser());
    Check(first.TypesHash == reordered.TypesHash, "payload type handshake is independent of registration order");
    var old = new PayloadRegistry();
    old.Register("test.Strike", new StubParser());
    Check(old.TypesHash != first.TypesHash, "a missing extension payload type makes the handshake differ even with identical definitions");
    Check(new PayloadRegistry().TypesHash != first.TypesHash, "no extension payload types differ from some");
    var invalid = false;
    try { old.Register("test.Strike", new StubParser()); } catch (InvalidOperationException) { invalid = true; }
    Check(invalid && old.TryGet("test.Strike", out _), "a payload type cannot be registered twice");
}

// ---- message layouts ---------------------------------------------------------------------------------------

void Layout(Type type, int size, params (string Field, int Offset)[] fields)
{
    Check(Marshal.SizeOf(type) == size, type.Name + " size " + Marshal.SizeOf(type) + " != " + size);
    var declared = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Select(f => f.Name).ToHashSet();
    Check(declared.SetEquals(fields.Select(f => f.Field)), type.Name + " fields changed: " + string.Join(",", declared));
    foreach (var (field, offset) in fields)
        Check(Marshal.OffsetOf(type, field).ToInt32() == offset, type.Name + "." + field + " offset " + Marshal.OffsetOf(type, field) + " != " + offset);
    Check(!type.GetFields().Any(f => !f.FieldType.IsValueType), type.Name + " has a reference field");
}

var assembly = typeof(Messages).Assembly;
Type Message(string name) => assembly.GetType("ForgeOrdnance." + name) ?? throw new Exception("missing " + name);

Layout(Message("Vec3"), 12, ("X", 0), ("Y", 4), ("Z", 8));
Layout(Message("HelloMessage"), 20, ("Epoch", 0), ("Greeting", 8), ("ThrowFloor", 16));
Layout(Message("LoadoutMessage"), 49, ("Epoch", 0), ("Player", 8), ("Count", 16), ("Keys", 17));
Layout(Message("LandingMessage"), 42, ("Epoch", 0), ("ThrowId", 8), ("Key", 12), ("Replicator", 16), ("Origin", 18), ("Landing", 30));
Layout(Message("RejectMessage"), 17, ("Epoch", 0), ("ThrowId", 8), ("Key", 12), ("Reason", 16));
Layout(Message("AcceptedMessage"), 40, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("Key", 20), ("Landing", 24), ("Delay", 36));
Layout(Message("ArrivedMessage"), 41, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("Key", 20), ("Landing", 24), ("Yaw", 36), ("Arrival", 40));
Layout(Message("DeliveryMessage"), 21, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("State", 20));
Layout(Message("DeliveryReplayMessage"), 53, ("Epoch", 0), ("Kind", 8), ("Thrower", 9), ("ThrowId", 17), ("Landing", 21), ("Yaw", 33), ("Elapsed", 37), ("SinceEmptied", 41), ("DefinitionKey", 45), ("LookKey", 49));
Layout(Message("ClaimMessage"), 25, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("Attempt", 20), ("Action", 24));
Layout(Message("ClaimStateMessage"), 49, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("Player", 20), ("Attempt", 28), ("State", 32), ("Token", 33));
Layout(Message("PayloadDataMessage"), 26 + Messages.MaxPayloadData, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("Type", 20), ("Tag", 24), ("Length", 25), ("Data", 26));
Layout(Message("SlotMessage"), 28,("Epoch", 0), ("Player", 8), ("Key", 16), ("UsesLeft", 20), ("CooldownRemaining", 24));

{
    // The loadout message survives a marshal round trip with every key in place.
    var keys = Enumerable.Range(1, Messages.MaxLoadout).Select(i => (uint)(i * 1000003)).ToArray();
    var message = LoadoutMessage.From(77, keys);
    var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<LoadoutMessage>());
    try
    {
        Marshal.StructureToPtr(message, buffer, false);
        var copy = Marshal.PtrToStructure<LoadoutMessage>(buffer);
        Check(copy.Player == 77 && copy.ToKeys()!.SequenceEqual(keys), "loadout message round-trips all fixed-array keys");
    }
    finally { Marshal.FreeHGlobal(buffer); }
    bool threw = false;
    try { LoadoutMessage.From(1, new uint[Messages.MaxLoadout + 1]); } catch (ArgumentException) { threw = true; }
    Check(threw, "a loadout longer than the fixed array cannot be built");
    var forged = default(LoadoutMessage);
    forged.Count = 200;
    Check(forged.ToKeys() == null, "a forged count beyond the array is detected");
}

{
    // Payload data survives a marshal round trip; oversize data cannot be built and a forged length is detected.
    var data = Enumerable.Range(0, Messages.MaxPayloadData).Select(i => (byte)(i * 7 + 3)).ToArray();
    var message = PayloadDataMessage.From(9, 4, ForgeCore.Fnv.Hash32("Arsenal.Strike"), 2, data);
    var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<PayloadDataMessage>());
    try
    {
        Marshal.StructureToPtr(message, buffer, false);
        var copy = Marshal.PtrToStructure<PayloadDataMessage>(buffer);
        Check(copy.Thrower == 9 && copy.ThrowId == 4 && copy.Tag == 2 && copy.Type == ForgeCore.Fnv.Hash32("Arsenal.Strike") && copy.ToBytes()!.SequenceEqual(data), "payload data round-trips all bytes");
    }
    finally { Marshal.FreeHGlobal(buffer); }
    Check(PayloadDataMessage.From(1, 1, 1, 0, Array.Empty<byte>()).ToBytes()!.Length == 0, "empty payload data is allowed");
    var threw = false;
    try { PayloadDataMessage.From(1, 1, 1, 0, new byte[Messages.MaxPayloadData + 1]); } catch (ArgumentException) { threw = true; }
    Check(threw, "payload data longer than the fixed array cannot be built");
    var forged = default(PayloadDataMessage);
    forged.Length = Messages.MaxPayloadData + 1;
    Check(forged.ToBytes() == null, "a forged payload data length is detected");
}

{
    var names = typeof(Messages).GetFields().Where(f => f.Name.EndsWith("Event")).Select(f => (string)f.GetRawConstantValue()!).ToArray();
    Check(names.Length == 16 && names.All(n => n.Length != 0 && !n.Contains('/')) && names.Distinct().Count() == names.Length, "sixteen distinct short event names (ForgeCore adds the mod and protocol)");
}

Layout(Message("CheckpointMessage"), 33, ("Epoch", 0), ("Kind", 8), ("Player", 9), ("Key", 17), ("UsesLeft", 21), ("CooldownRemaining", 25));
Layout(Message("SentryExpiryMessage"), 22, ("Epoch", 0), ("Replicator", 8), ("Identity", 10), ("Remaining", 18));

// ---- summoned turret leaves the tool slot alone ---------------------------------------------------------------

{
    // The game's sentry code marks the owner's class slot deployed in its own spawn, pick-up and cleanup, like a carried sentry.
    var guard = new ToolSlotGuard();
    var slot = new ToolSlotState("Burst Sentry", false, 1);
    void SetDeployed(bool classSlot, bool mode) { if (guard.AllowsDeployChange(classSlot)) slot = slot with { Deployed = mode }; }
    void NativeSentryStep() { SetDeployed(true, true); }

    foreach (var carried in new[] { "Burst Sentry", "C-Foam Launcher", "Bio Tracker", "Mine Deployer" })
    {
        slot = new ToolSlotState(carried, false, 1);
        var before = slot;
        guard.Enter();
        NativeSentryStep();
        guard.Exit();
        Check(slot == before && !guard.Active, "summoning with " + carried + " carried leaves the tool slot identical: " + before.Describe());
    }

    slot = new ToolSlotState("Burst Sentry", true, 2);
    var deployedBefore = slot;
    guard.Enter();
    SetDeployed(true, false);
    guard.Exit();
    Check(slot == deployedBefore, "a summoned turret's pick-up or despawn does not recall the sentry the player carries and has placed");

    slot = new ToolSlotState("Burst Sentry", false, 1);
    NativeSentryStep();
    Check(slot.Deployed, "a player-carried sentry still marks its slot deployed outside the guard");
    SetDeployed(true, false);
    Check(!slot.Deployed, "a player-carried sentry's recall still clears the marker");

    guard.Enter();
    Check(guard.AllowsDeployChange(false), "other slots stay writable inside the guard");
    guard.Enter();
    guard.Exit();
    Check(guard.Active && !guard.AllowsDeployChange(true), "nested guards hold until the outer one ends");
    guard.Exit();
    guard.Exit();
    Check(!guard.Active && guard.AllowsDeployChange(true), "extra exits never leave the guard negative or stuck");
    Check(new ToolSlotState("a", true, 2).Describe() == "a (deployed yes, status 2)", "the slot log line names item, marker and status");
}

ThrowChecks.Run(Check, resupply);
LangChecks.Run(Check);
PresentationChecks.Run(Check);
Console.WriteLine("ForgeOrdnance core: " + checks + " checks passed.");

sealed record StubParams(int Count);

sealed class StubParser : IOrdnancePayloadParser
{
    public object Parse(JsonElement payload)
    {
        if (!payload.TryGetProperty("Count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var value))
            throw new PayloadParameterException("Count", "is required.");
        if (value < 1 || value > 5) throw new PayloadParameterException("Count", "must be 1..5.");
        return new StubParams(value);
    }
}
