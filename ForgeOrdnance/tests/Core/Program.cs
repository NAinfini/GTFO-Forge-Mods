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
Rejected("code too long", Entry("t.x", "[\"Up\",\"Up\",\"Up\",\"Up\",\"Up\",\"Up\",\"Up\",\"Up\",\"Up\"]"), "Code");
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
Rejected("id without a namespace", Entry("ammo"), "Id");
Rejected("id with an empty segment", Entry("author..ammo"), "Id");
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
Rejected("zero uses", Entry("t.x").Replace("\"Uses\":3", "\"Uses\":0"), "Uses");
Rejected("delay as text", Entry("t.x").Replace("\"CallInDelay\":5", "\"CallInDelay\":\"5\""), "CallInDelay");
Rejected("icon that is not a png", Entry("t.x", extra: ",\"Icon\":\"a.jpg\""), "Icon");
Rejected("icon leaving the folder", Entry("t.x", extra: ",\"Icon\":\"..\\\\a.png\""), "Icon");
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
Rejected("beam colour not hex", Entry("t.x", extra: ",\"BeamColour\":\"#GG0000\""), "BeamColour");
{
    var rated = Parse("s.json", Entry("t.s", extra: ",\"Stats\":[{\"Name\":\"Armor\",\"Value\":4},{\"Name\":\"Speed\",\"Value\":0,\"Max\":10}]"));
    var stats = rated.Definitions.Count == 1 ? rated.Definitions[0].Stats : Array.Empty<OrdnanceStat>();
    Check(rated.Errors.Count == 0 && stats.Count == 2 && stats[0].Name == "Armor" && stats[0].Value == 4 && stats[0].Max == DefinitionParser.DefaultStatBoxes
          && stats[1].Value == 0 && stats[1].Max == 10, "Stats rows keep name, value and max; max defaults to five boxes");
    Check(Parse("s.json", Entry("t.s")).Definitions[0].Stats.Count == 0, "Stats default to none");
    Check(HashOf(Entry("t.s")) != HashOf(Entry("t.s", extra: ",\"Stats\":[{\"Name\":\"Armor\",\"Value\":4}]")), "Stats change the set hash");
}
Rejected("stat value above max", Entry("t.x", extra: ",\"Stats\":[{\"Name\":\"Armor\",\"Value\":6}]"), "Stats[0].Value");
Rejected("stat without name", Entry("t.x", extra: ",\"Stats\":[{\"Value\":1}]"), "Stats[0].Name");
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

// ---- matching and loadout ----------------------------------------------------------------------------------

OrdnanceDefinition Def(string id, params Direction[] code)
{
    var codeText = "[" + string.Join(",", code.Select(c => "\"" + c + "\"")) + "]";
    var result = Parse("m.json", Entry("t." + id, codeText));
    Check(result.Errors.Count == 0, "definition fixture parses");
    return result.Definitions[0];
}

var up = Direction.Up; var down = Direction.Down; var right = Direction.Right;
var resupply = Def("res", down, down, up, right);
var turret = Def("tur", down, right, down, up);
var eagle = Def("eag", up, right, down);
var loadout = new[] { resupply, turret, eagle };
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
Layout(Message("DeliveryReplayMessage"), 49, ("Epoch", 0), ("Kind", 8), ("Thrower", 9), ("ThrowId", 17), ("Landing", 21), ("Yaw", 33), ("Elapsed", 37), ("SinceEmptied", 41), ("DefinitionKey", 45));
Layout(Message("ClaimMessage"), 26, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("Attempt", 20), ("Action", 24), ("Bay", 25));
Layout(Message("ClaimStateMessage"), 50, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("Player", 20), ("Attempt", 28), ("State", 32), ("Token", 33), ("Bay", 49));
Layout(Message("PayloadDataMessage"), 26 + Messages.MaxPayloadData, ("Epoch", 0), ("Thrower", 8), ("ThrowId", 16), ("Type", 20), ("Tag", 24), ("Length", 25), ("Data", 26));
Layout(Message("SlotMessage"), 32, ("Epoch", 0), ("Player", 8), ("Key", 16), ("UsesLeft", 20), ("CooldownRemaining", 24), ("InitialUses", 28));

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

Layout(Message("CheckpointMessage"), 33, ("Epoch", 0), ("Kind", 8), ("Player", 9), ("Key", 17), ("UsesLeft", 21), ("CooldownRemaining", 25));
Layout(Message("SentryExpiryMessage"), 22, ("Epoch", 0), ("Replicator", 8), ("Identity", 10), ("Remaining", 18));

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
