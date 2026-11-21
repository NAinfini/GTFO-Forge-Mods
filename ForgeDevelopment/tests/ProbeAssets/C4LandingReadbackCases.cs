using ForgeDevelopment.Native;

internal static class C4LandingReadbackCases
{
    internal static void Run(IReadOnlyList<RecTraceProfile> profiles, Action<bool, string> check, bool required)
    {
        var profile = profiles.SingleOrDefault(row => row.Name == "dimension-landings");
        if (profile == null && !required) return;
        check(profile != null && profile.EnabledByDefault && profile.Error == null, "shared dimension landing readback profile present and enabled");
        if (profile == null) return;
        var agent = profile.Entries.SingleOrDefault(row => row.Type == "Player.PlayerAgent");
        var sync = profile.Entries.SingleOrDefault(row => row.Type == "Player.PlayerSync");
        check(agent != null && agent.RecordsPair && agent.RecordsArgs && agent.RecordsResult
            && agent.MatchesMethod("WarpTo") && agent.MatchesMethod("TeleportTo") && agent.MatchesMethod("WarpBack"), "C4 records paired actual warp calls, not just requests");
        check(agent != null && new[] { "Pointer", "PlayerSlotIndex", "Sync.SlotIndex", "IsLocallyOwned", "CourseNode.m_zone.DimensionIndex", "transform.position", "transform.forward" }
            .All(field => agent.InstanceFields.Contains(field)), "C4 captures each player identity, dimension and actual position/facing");
        check(sync != null && sync.RecordsPair && sync.MatchesMethod("IncomingDimensionChange") && sync.MatchesMethod("IncomingSyncWarp")
            && new[] { "Pointer", "SlotIndex", "m_agent.Pointer", "m_agent.PlayerSlotIndex", "m_agent.CourseNode.m_zone.DimensionIndex", "m_agent.transform.position" }
            .All(field => sync.InstanceFields.Contains(field)), "C4 captures client incoming warp readback");
        check(profile.Entries.All(row => row.MaxPerSecond > 0 && row.OnOverflow == "count" && !row.SignatureOnly), "C4 readback records bounded samples and reports overflow");
    }
}
