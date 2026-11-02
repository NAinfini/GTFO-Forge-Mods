using ForgeOrdnance;

internal static class HostRulesChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var source = new HostRulesSnapshot("Local_31_TierA_0", 0x123456789abcdef0, 8, new uint[] { 7, 11 }, 0.5, 2, 80, false);
        var encoded = HostRulesWire.Encode(source);
        check(HostRulesWire.TryDecode(encoded, out var decoded) && decoded != null
            && decoded.LevelKey == source.LevelKey && decoded.DefinitionHash == source.DefinitionHash && decoded.LoadoutSize == 8
            && decoded.AllowedKeys!.SequenceEqual(source.AllowedKeys!) && decoded.CooldownMultiplier == 0.5
            && decoded.UsesMultiplier == 2 && decoded.MaxThrowDistance == 80 && !decoded.CallMenu,
            "host rules round-trip the selected expedition and all effective rules");

        var all = HostRulesWire.Encode(new HostRulesSnapshot("", 1, 4, null, 1, 1, 80, true));
        check(HostRulesWire.TryDecode(all, out decoded) && decoded!.AllowedKeys == null, "host rules distinguish all call-ins from an empty allow-list");
        var none = HostRulesWire.Encode(new HostRulesSnapshot("", 1, 4, Array.Empty<uint>(), 1, 1, 80, true));
        check(HostRulesWire.TryDecode(none, out decoded) && decoded!.AllowedKeys is { Length: 0 }, "host rules preserve an empty allow-list");

        check(!HostRulesWire.TryDecode(encoded[..^1], out _), "host rules reject truncation");
        check(!HostRulesWire.TryDecode(encoded.Concat(new byte[] { 0 }).ToArray(), out _), "host rules reject trailing data");
        var duplicates = HostRulesWire.Encode(new HostRulesSnapshot("x", 1, 4, new uint[] { 7, 7 }, 1, 1, 80, true));
        check(!HostRulesWire.TryDecode(duplicates, out _), "host rules reject duplicate allowed keys");
        var invalid = HostRulesWire.Encode(new HostRulesSnapshot("x", 1, 0, null, double.NaN, 1, 80, true));
        check(!HostRulesWire.TryDecode(invalid, out _), "host rules reject invalid effective values");
    }
}
