using ForgeOrdnance;

/// <summary>Lobby picks as a ForgeCore lobby value: the bytes each player publishes. Delivery to joiners and forgetting leavers is
/// ForgeCore's lobby data (tested in Mods/ForgeCore/tests).</summary>
internal static class LobbyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var full = Enumerable.Range(1, Messages.MaxLoadout).Select(i => (uint)(i * 2654435761u)).ToArray();
        var into = new uint[Messages.MaxLoadout];
        var count = LobbyPicks.Decode(LobbyPicks.Encode(full), into);
        check(count == Messages.MaxLoadout && into.SequenceEqual(full), "lobby picks round-trip with every key in place");
        check(LobbyPicks.Decode(LobbyPicks.Encode(full.Concat(new uint[] { 9 }).ToArray()), into) == Messages.MaxLoadout,
            "more picks than the fixed capacity are cut to it");
        check(LobbyPicks.Decode(LobbyPicks.Encode(ReadOnlySpan<uint>.Empty), into) == 0, "an empty loadout is kept as empty, not as unknown");
        var two = LobbyPicks.Encode(new uint[] { 11, 22 });
        var overfull = (byte[])two.Clone(); overfull[0] = Messages.MaxLoadout + 1;
        var short1 = two[..^1];
        check(LobbyPicks.Decode(overfull, into) == -1 && LobbyPicks.Decode(short1, into) == -1 && LobbyPicks.Decode(Array.Empty<byte>(), into) == -1,
            "a count past the capacity, a truncated value or no bytes is refused");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) LobbyPicks.Decode(two, into);
        check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "reading picks allocates nothing");
    }
}
