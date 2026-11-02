using System.Text;
using System.Text.Json;

namespace ForgeCore.Tests;

/// <summary>FNV-1a byte locks and the refusal branches of <see cref="StrictJson"/>.</summary>
internal static class JsonChecks
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string? Refused(Action action, string field)
    {
        try { action(); }
        catch (StrictJsonException error) { return error.Field == field ? error.Message : "wrong field " + error.Field + ": " + error.Message; }
        return null;
    }

    internal static void Run(Action<bool, string> check)
    {
        Fnv(check);
        Strict(check);
        Canonical(check);
    }

    private static void Fnv(Action<bool, string> check)
    {
        // Published FNV-1a test vectors; peers compute these over the wire, so they may never change.
        check(ForgeCore.Fnv.Hash32("") == 0x811C9DC5u && ForgeCore.Fnv.Hash32("a") == 0xE40C292Cu && ForgeCore.Fnv.Hash32("foobar") == 0xBF9CF968u,
            "fnv: 32-bit vectors for empty, a and foobar");
        check(ForgeCore.Fnv.Hash64("") == 0xCBF29CE484222325ul && ForgeCore.Fnv.Hash64("a") == 0xAF63DC4C8601EC8Cul && ForgeCore.Fnv.Hash64("foobar") == 0x85944171F73967E8ul,
            "fnv: 64-bit vectors for empty, a and foobar");
        check(ForgeCore.Fnv.Hash32("é") == 0x1E9DE8C1u && ForgeCore.Fnv.Hash64("é") == 0x0AC21707B7181E01ul, "fnv: text is hashed as UTF-8 (C3 A9)");
        check(ForgeCore.Fnv.Hash32("Arsenal.Strike") == 0x01175927u, "fnv: a payload type name keeps the 32-bit key every peer computes");
        var bytes = Encoding.UTF8.GetBytes("foobar");
        var h32 = ForgeCore.Fnv.Offset32; var h64 = ForgeCore.Fnv.Offset64;
        foreach (var b in bytes) { h32 = ForgeCore.Fnv.Mix32(h32, b); h64 = ForgeCore.Fnv.Mix64(h64, b); }
        check(h32 == ForgeCore.Fnv.Hash32(bytes) && h64 == ForgeCore.Fnv.Hash64(bytes) && h32 == 0xBF9CF968u, "fnv: feeding bytes one at a time equals hashing them together");
    }

    private static void Strict(Action<bool, string> check)
    {
        string[] known = { "A", "B" };
        check(Refused(() => StrictJson.OnlyKnown(Json("[1]"), "Shield.", known), "Shield") == "must be an object.", "strict: a non-object is refused under the object's own path");
        var unknown = Refused(() => StrictJson.OnlyKnown(Json("{\"A\":1,\"C\":2}"), "Shield.", known), "Shield.C");
        check(unknown != null && unknown.Contains("unknown field") && unknown.Contains("A, B"), "strict: an unknown member is refused by its path and the message lists the known ones");
        check(Refused(() => StrictJson.OnlyKnown(Json("{\"A\":1,\"A\":2}"), "", known), "A") == "duplicate field.", "strict: a member written twice is refused");
        check(Refused(() => StrictJson.OnlyKnown(Json("{\"Type\":\"x\",\"A\":1}"), "", known), "Type") != null, "strict: Type is unknown unless the caller allows it");
        StrictJson.OnlyKnown(Json("{\"Type\":\"x\",\"A\":1}"), "", known, new[] { "Type" });
        var owned = Refused(() => StrictJson.OnlyKnown(Json("{\"Z\":1}"), "", known, null, "Area"), "Z");
        check(owned != null && owned.Contains("for Area"), "strict: the message names what the object belongs to");

        var o = Json("{\"N\":2.5,\"S\":\"x\",\"I\":3,\"Big\":1e999,\"T\":true,\"F\":[1]}");
        check(StrictJson.Number(o, "", "N", null, 0, 5) == 2.5 && StrictJson.Number(o, "", "N", null, 2.5, 2.5) == 2.5, "strict: a number inside its range, bounds included, is read");
        check(StrictJson.Number(o, "", "Missing", 7, 0, 5) == 7, "strict: a missing field returns the fallback unchecked");
        check(Refused(() => StrictJson.Number(o, "P.", "Missing", null, 0, 5), "P.Missing") == "is required.", "strict: a missing field without a fallback is required");
        check(Refused(() => StrictJson.Number(o, "P.", "S", null, 0, 5), "P.S") == "must be a finite number.", "strict: a string where a number belongs names the field");
        check(Refused(() => StrictJson.Number(o, "", "T", null, 0, 5), "T") == "must be a finite number.", "strict: a bool is not a number");
        check(Refused(() => StrictJson.Number(o, "", "Big", null, 0, 5), "Big") == "must be a finite number.", "strict: a number that overflows a double is refused");
        var high = Refused(() => StrictJson.Number(o, "", "N", null, 0, 2), "N");
        var low = Refused(() => StrictJson.Number(o, "", "N", null, 3, 5), "N");
        check(high == "2.5 is outside 0..2." && low == "2.5 is outside 3..5.", "strict: a number outside its range is refused with the range, never clamped");
        check(StrictJson.Integer(o, "", "I", null, 1, 3) == 3, "strict: a whole number is read");
        check(Refused(() => StrictJson.Integer(o, "", "N", null, 0, 5), "N") == "must be a whole number.", "strict: a fraction where a whole number belongs is refused");
        check(Refused(() => StrictJson.Integer(o, "", "I", null, 4, 5), "I") == "3 is outside 4..5.", "strict: a whole number outside its range is refused");
        check(StrictJson.Bool(o, "", "T", false) && !StrictJson.Bool(o, "", "Missing", false) && StrictJson.Bool(o, "", "Missing", true), "strict: a bool is read, a missing one takes the fallback");
        check(Refused(() => StrictJson.Bool(o, "", "I", false), "I") == "must be true or false.", "strict: a number where a bool belongs is refused");
        check(Refused(() => StrictJson.Bool(o, "", "Missing", null), "Missing") == "is required.", "strict: a missing bool without a fallback is required");
        check(StrictJson.Finite(o.GetProperty("N"), out var finite) && finite == 2.5 && !StrictJson.Finite(o.GetProperty("S"), out _), "strict: Finite accepts only a finite number");
        check(Refused(() => StrictJson.Object(o, "P.", "F"), "P.F") == "is required and must be an object." && StrictJson.Object(Json("{\"X\":{}}"), "", "X").ValueKind == JsonValueKind.Object,
            "strict: an object member must be an object");

        check(StrictJson.Enum<Mode>(Json("{\"M\":\"Slow\"}"), "", "M") == Mode.Slow, "strict: an enum member is read by its exact name");
        foreach (var bad in new[] { "{\"M\":\"slow\"}", "{\"M\":\"2\"}", "{\"M\":\"Slow \"}", "{\"M\":7}", "{}" })
            check(Refused(() => StrictJson.Enum<Mode>(Json(bad), "", "M"), "M") != null, "strict: enum text " + bad + " is refused (case, digits, numbers and absence)");

        var (id, name) = StrictJson.Reference(Json("{\"G\":38}"), "", "G");
        check(id == 38 && name == null, "strict: a reference by persistentID");
        (id, name) = StrictJson.Reference(Json("{\"G\":\"Gun\"}"), "", "G");
        check(id == null && name == "Gun", "strict: a reference by name");
        foreach (var bad in new[] { "{\"G\":\"\"}", "{\"G\":-1}", "{\"G\":1.5}", "{\"G\":true}", "{}" })
            check(Refused(() => StrictJson.Reference(Json(bad), "", "G"), "G") != null, "strict: reference " + bad + " is refused");
    }

    private enum Mode : byte { Fast = 1, Slow = 2 }

    private static void Canonical(Action<bool, string> check)
    {
        check(StrictJson.Canonical(Json("{ \"b\": 1, \"a\": [ 1.0, 2 ], \"c\": { \"y\": null, \"x\": true } }")) == "{\"a\":[1,2],\"b\":1,\"c\":{\"x\":true,\"y\":null}}",
            "canonical: members sorted by name, no whitespace, nested too");
        check(StrictJson.Canonical(Json("{\"a\":1,\"b\":2}")) == StrictJson.Canonical(Json("{\"b\":2,\"a\":1}")), "canonical: member order does not change the text");
        check(StrictJson.Canonical(Json("[1, 1.0, 1e0, 10, 1e1, 0.10, 100, 1E2, -0, 0.0, -2.50]")) == "[1,1,1,1e1,1e1,1e-1,1e2,1e2,0,0,-25e-1]",
            "canonical: every number has one spelling (coefficient and exponent)");
        check(StrictJson.Canonical(Json("\"a\\\"b\"")) == "\"a\\u0022b\"", "canonical: strings are escaped by the default JSON serializer (a quote is \\u0022), the spelling saved hashes rely on");
    }
}
