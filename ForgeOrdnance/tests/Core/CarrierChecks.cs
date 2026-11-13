using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ForgeOrdnance.Tests;

/// <summary>The carrier's Core: definition files, the set hash, the host's order of throw checks and the message layouts.</summary>
internal static class CarrierChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // ---- definitions -------------------------------------------------------------------------------------------

        var payloads = new PayloadRegistry();
        payloads.Register("Stub", new StubParser());
        var categories = new CategoryRegistry(id => () => id);

        string Entry(string id, string code = "[\"Up\",\"Down\"]", string extra = "", string payload = "{\"Type\":\"Stub\",\"Count\":2}")
            => "{\"Id\":\"" + id + "\",\"Category\":\"Supply\",\"Code\":" + code + ",\"Cooldown\":60,\"Uses\":3,\"CallInDelay\":5,\"Payload\":" + payload + extra + "}";

        ParseResult Parse(string file, params string[] entries) => DefinitionParser.ParseFile(file, "[" + string.Join(",", entries) + "]", payloads, categories);

        {
            var good = Parse("a.json", Entry("a.one"));
            check(good.Errors.Count == 0 && good.Definitions.Count == 1, "valid entry loads");
            var d = good.Definitions[0];
            check(d.Name == "a.one" && d.Uses == 3 && d.Cooldown == 60 && d.CallInDelay == 5 && d.Code.Count == 2 && d.Code[0] == Direction.Up, "fields parsed, Name defaults to Id");
            check(((StubParams)d.Parameters).Count == 2, "payload parser result is kept");
        }

        // A refused entry: the other entries still load, and the one error names the file, the index and the field.
        void Rejected(string name, string entry, string field)
        {
            var result = Parse("bad.json", Entry("ok.first"), entry, Entry("ok.last"));
            check(result.Definitions.Count == 2 && result.Errors.Count == 1 && result.Errors[0].StartsWith("bad.json [1] " + field + ":"),
                name + ": " + string.Join(" | ", result.Errors));
        }

        Rejected("code with a letter key", Entry("t.x", "[\"Up\",\"W\"]"), "Code");
        Rejected("unknown field", Entry("t.x", extra: ",\"Cooldwn\":1"), "Cooldwn");
        Rejected("unregistered payload type", Entry("t.x", payload: "{\"Type\":\"Missing\"}"), "Payload.Type");
        Rejected("payload parameter refused", Entry("t.x", payload: "{\"Type\":\"Stub\",\"Count\":99}"), "Payload.Count");
        Rejected("missing payload", "{\"Id\":\"t.x\",\"Category\":\"Supply\",\"Code\":[\"Up\"],\"Cooldown\":1,\"Uses\":1,\"CallInDelay\":1}", "Payload");
        Rejected("bad category", Entry("t.x").Replace("Supply", "Banana"), "Category");
        {
            var kinds = new CategoryRegistry(id => () => id);
            kinds.Register("Exosuit", () => "Suit", 3, 0xFFC23D);
            check(kinds.Ordered.Select(c => c.Id).SequenceEqual(new[] { "Supply", "Defensive", "Weapon", "Exosuit", "Offensive" }), "categories: a registered kind sits at its order among the built-ins");
            var suit = DefinitionParser.ParseFile("c.json", "[" + Entry("c.one").Replace("Supply", "Exosuit") + "]", payloads, kinds);
            check(suit.Errors.Count == 0 && suit.Definitions[0].Category == "Exosuit" && suit.Definitions[0].BeamRgb == 0xFFC23D, "categories: a definition may name a registered kind and takes its default beam colour");
            check(Parse("c.json", Entry("c.one").Replace("Supply", "Exosuit")).Errors.Count == 1, "categories: a kind that is not registered is rejected");
            var duplicate = false; try { kinds.Register("Exosuit", () => "x", 9, 0); } catch (InvalidOperationException) { duplicate = true; }
            check(duplicate, "categories: registering a taken id throws");
            var bad = false; try { kinds.Register("not a name", () => "x", 9, 0); } catch (ArgumentException) { bad = true; }
            check(bad, "categories: an id that is not a plain name throws");
        }
        Rejected("id without a namespace", Entry("ammo"), "Id");
        check(DefinitionParser.IsNamespacedId("nainfini.arsenal.orbital-strike_2"), "a namespaced id with dashes, digits and underscores is accepted");
        Rejected("negative cooldown", Entry("t.x").Replace("\"Cooldown\":60", "\"Cooldown\":-1"), "Cooldown");
        Rejected("delay as text", Entry("t.x").Replace("\"CallInDelay\":5", "\"CallInDelay\":\"5\""), "CallInDelay");
        Rejected("icon leaving the folder", Entry("t.x", extra: ",\"Icon\":\"..\\\\a.png\""), "Icon");
        Rejected("beam colour not hex", Entry("t.x", extra: ",\"BeamColour\":\"#GG0000\""), "BeamColour");
        Rejected("too many stats", Entry("t.x", extra: ",\"Stats\":[" + string.Join(",", Enumerable.Repeat("{\"Name\":\"A\",\"Value\":1}", DefinitionParser.MaxStats + 1)) + "]"), "Stats");
        {
            var open = Parse("u.json", Entry("t.u").Replace(",\"Uses\":3", "")).Definitions;
            check(open.Count == 1 && open[0].Unlimited && open[0].Uses == OrdnanceDefinition.UnlimitedUses, "Uses left out means unlimited");
        }
        check(Parse("x.json", "42").Errors.Count == 1, "non-object entry is reported");
        check(DefinitionParser.ParseFile("t.json", "{}", payloads, categories).Errors[0].StartsWith("t.json:"), "non-array file is reported");
        check(DefinitionParser.ParseFile("t.json", "[", payloads, categories).Errors.Count == 1, "malformed JSON is reported, not thrown");

        // ---- hash and set ------------------------------------------------------------------------------------------

        check(ForgeCore.Fnv.Hash32("Arsenal.Strike") == 0x01175927u, "a payload type travels as the FNV-1a 32 of its name (the key every peer computes)");

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
            check(errors.Count == 1 && errors[0].Contains("network hash") && errors[0].Contains(second!), "hash collision inside a set is reported");
            check(set.Definitions.Count == 2 && set.TryGetById(first!, out _) && !set.TryGetById(second!, out _), "the colliding later entry is skipped");
        }

        {
            var errors = new List<string>();
            var set = DefinitionSet.Build(Parse("a.json", Entry("t.dup")).Definitions.Concat(Parse("b.json", Entry("t.dup", "[\"Left\"]")).Definitions), errors);
            check(errors.Count == 1 && errors[0].StartsWith("b.json:") && errors[0].Contains("a.json"), "duplicate Id across files is reported with both files");
            check(set.Definitions.Count == 1 && set.Definitions[0].SourceFile == "a.json", "the first duplicate wins");
        }

        ulong HashOf(params string[] entries)
        {
            var result = Parse("h.json", entries);
            if (result.Errors.Count != 0) throw new Exception("FAIL: hash fixture parses: " + string.Join(";", result.Errors));
            return DefinitionSet.Build(result.Definitions, new List<string>()).Hash;
        }

        {
            string Seed(string number) => Entry("t.seed", payload: "{\"Type\":\"Stub\",\"Count\":2,\"Seed\":" + number + "}");
            var equal = new[]
            {
                ("2", "2.0"), ("2", "20e-1"), ("123.4500", "12345e-2"), ("-0.0", "0e400"),
                ("10e999999999999999999999", "1e1000000000000000000000")
            };
            check(equal.All(p => HashOf(Seed(p.Item1)) == HashOf(Seed(p.Item2))), "equivalent JSON numbers hash equally");
            var distinct = new[]
            {
                ("9007199254740992", "9007199254740993"), ("-9007199254740992", "-9007199254740993"),
                ("0.123456789012345678901234567890", "0.123456789012345678901234567891"),
                ("1e400", "2e400"), ("1e-400", "2e-400")
            };
            check(distinct.All(p => HashOf(Seed(p.Item1)) != HashOf(Seed(p.Item2))), "distinct JSON numbers hash differently");
        }

        {
            // These sets had identical delimiter-based encodings: the name injected a whole record boundary.
            const string suffix = "|0|||1,2|60|3|5|Stub|{\"Count\":2,\"Type\":\"Stub\"}\n";
            var a = Entry("t.a", extra: ",\"Name\":\"Alpha\"");
            var b = Entry("t.b", extra: ",\"Name\":\"Beta\"");
            var injected = Entry("t.a", extra: ",\"Name\":" + JsonSerializer.Serialize("Alpha" + suffix + "t.b|Beta"));
            check(HashOf(injected) != HashOf(a, b), "pipes and newlines in a name cannot inject a second definition");

            var fieldA = Entry("t.a", extra: ",\"Name\":\"Alpha|0|a.png\",\"Icon\":\"b.png\"");
            var fieldB = Entry("t.a", extra: ",\"Name\":\"Alpha\",\"Icon\":\"a.png\",\"Description\":\"0|b.png|\"");
            check(HashOf(fieldA) != HashOf(fieldB), "pipes cannot move content between definition fields");
        }

        {
            var a = Entry("t.a", payload: "{\"Type\":\"Stub\",\"Count\":2}");
            var b = Entry("t.b", "[\"Left\"]");
            check(HashOf(a, b) == HashOf(b, a), "set hash does not depend on definition order");
            check(HashOf(a) == HashOf(Entry("t.a", payload: "{ \"Count\": 2, \"Type\": \"Stub\" }").Replace("\"Cooldown\":60", "\"Cooldown\":60.0")), "set hash is the same for reordered members and another number spelling");
            check(HashOf(a) != HashOf(Entry("t.a", payload: "{\"Type\":\"Stub\",\"Count\":3}")), "a payload parameter changes the set hash");
        }

        // ---- matching and loadout ----------------------------------------------------------------------------------

        OrdnanceDefinition Def(string id, params Direction[] code)
        {
            var codeText = "[" + string.Join(",", code.Select(c => "\"" + c + "\"")) + "]";
            var result = Parse("m.json", Entry("t." + id, codeText));
            if (result.Errors.Count != 0) throw new Exception("FAIL: definition fixture parses");
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
            Rejection Eval(uint key, Vec3 landing, Func<bool> r, double now = 0)
                => ThrowValidator.Evaluate(book, true, p, key, origin, landing, now, 80, r);

            check(Eval(resupply.Key, near, reachable) == Rejection.None && reached == 1, "valid throw passes");
            check(Eval(eagle.Key, near, reachable) == Rejection.NotInLoadout, "call-in outside the loadout is refused");
            check(Eval(resupply.Key, new Vec3(80.5f, 0, 0), reachable) == Rejection.TooFar, "too far from the throw is refused");
            check(Eval(resupply.Key, new Vec3(float.NaN, 0, 0), reachable) == Rejection.InvalidLanding, "non-finite landing is refused");
            reached = 0;
            check(Eval(resupply.Key, near, () => false) == Rejection.InvalidLanding, "unreachable landing is refused");
            book.Consume(p, resupply.Key, 0, 50);
            reached = 0;
            check(Eval(resupply.Key, near, reachable, 1) == Rejection.Cooldown && reached == 0, "cooldown is refused before the level is queried");
        }


        // ---- message layouts ---------------------------------------------------------------------------------------

        // One message's wire layout: its size, exactly these public fields at these offsets, and no reference field.
        void Layout(Type type, int size, params (string Field, int Offset)[] fields)
        {
            var declared = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Select(f => f.Name).ToHashSet();
            check(Marshal.SizeOf(type) == size && declared.SetEquals(fields.Select(f => f.Field))
                && fields.All(f => Marshal.OffsetOf(type, f.Field).ToInt32() == f.Offset) && type.GetFields().All(f => f.FieldType.IsValueType),
                type.Name + " layout changed: size " + Marshal.SizeOf(type) + ", fields " + string.Join(",", declared));
        }

        var assembly = typeof(Messages).Assembly;
        Type Message(string name) => assembly.GetType("ForgeOrdnance." + name) ?? throw new Exception("missing " + name);

        Layout(Message("Vec3"), 12, ("X", 0), ("Y", 4), ("Z", 8));
        Layout(Message("LoadoutRow"), 33, ("Count", 0), ("Keys", 1));
        Layout(Message("SlotRow"), 16, ("UsesLeft", 0), ("CooldownRemaining", 4), ("InitialUses", 8), ("Write", 12));
        Layout(Message("LandingMessage"), 34, ("ThrowId", 0), ("Key", 4), ("Replicator", 8), ("Origin", 10), ("Landing", 22));
        Layout(Message("CallRow"), 20, ("Key", 0), ("Landing", 4), ("Delay", 16));
        Layout(Message("ArrivedMessage"), 33, ("Thrower", 0), ("ThrowId", 8), ("Key", 12), ("Landing", 16), ("Yaw", 28), ("Arrival", 32));
        Layout(Message("DeliveryRow"), 28, ("Landing", 0), ("Yaw", 12), ("Elapsed", 16), ("SinceEmptied", 20), ("DefinitionKey", 24));
        Layout(Message("SentryRow"), 12, ("Identity", 0), ("Remaining", 8));
        Layout(Message("ClaimMessage"), 18, ("Thrower", 0), ("ThrowId", 8), ("Attempt", 12), ("Action", 16), ("Bay", 17));
        Layout(Message("ClaimStateMessage"), 42, ("Thrower", 0), ("ThrowId", 8), ("Player", 12), ("Attempt", 20), ("State", 24), ("Token", 25), ("Bay", 41));
        // The table keys are packed as written: their size is what goes on the wire.
        check(Unsafe.SizeOf<CallKey>() == 12 && Unsafe.SizeOf<SlotKey>() == 12 && Unsafe.SizeOf<StateKey>() == 17 && Unsafe.SizeOf<DeliveryKey>() == 13, "table keys keep their packed sizes");

        {
            // The loadout row survives a marshal round trip with every key in place.
            var keys = Enumerable.Range(1, Messages.MaxLoadout).Select(i => (uint)(i * 1000003)).ToArray();
            var message = LoadoutRow.From(keys);
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<LoadoutRow>());
            try
            {
                Marshal.StructureToPtr(message, buffer, false);
                var copy = Marshal.PtrToStructure<LoadoutRow>(buffer);
                check(copy.ToKeys()!.SequenceEqual(keys), "loadout row round-trips all fixed-array keys");
            }
            finally { Marshal.FreeHGlobal(buffer); }
            bool threw = false;
            try { LoadoutRow.From(new uint[Messages.MaxLoadout + 1]); } catch (ArgumentException) { threw = true; }
            check(threw, "a loadout longer than the fixed array cannot be built");
            var forged = default(LoadoutRow);
            forged.Count = 200;
            check(forged.ToKeys() == null, "a forged count beyond the array is detected");
        }

        {
            // The checkpoint copy: the slot book's image survives the bytes; a cut or malformed copy is no image.
            var image = new SlotSnapshot(new[] { new SlotSnapshot.Entry(7, 0xABCDEF01, 12.5, 2), new SlotSnapshot.Entry(9, 3, 0, -1) });
            var bytes = image.Encode();
            var back = SlotSnapshot.Decode(bytes);
            check(back != null && back.Entries.SequenceEqual(image.Entries), "the checkpoint copy round-trips every slot");
            check(CheckpointCopy.TrySplit(CheckpointCopy.Join(bytes, new byte[] { 1, 2 }), out var slots, out var content) && slots!.SequenceEqual(bytes) && content!.SequenceEqual(new byte[] { 1, 2 })
                && !CheckpointCopy.TrySplit(new byte[] { 9, 0, 0, 0, 1 }, out _, out _) && !CheckpointCopy.TrySplit(null, out _, out _),
                "the one copy carries the slot book, then the content parts' bytes; a cut copy or none splits into nothing");
            check(SlotSnapshot.Decode(bytes.AsSpan(0, bytes.Length - 1)) == null && SlotSnapshot.Decode(ReadOnlySpan<byte>.Empty) == null, "a cut checkpoint copy is refused");
            BitConverter.TryWriteBytes(bytes.AsSpan(4 + 16), double.NaN);
            check(SlotSnapshot.Decode(bytes) == null, "a checkpoint copy with a cooldown that is not a number is refused");
        }
    }
}

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
