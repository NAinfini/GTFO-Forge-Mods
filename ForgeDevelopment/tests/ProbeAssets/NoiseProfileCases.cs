using ForgeDevelopment.Native;

internal static class NoiseProfileCases
{
    internal static void Run(IReadOnlyList<RecTraceProfile> profiles, Action<bool, string> check)
    {
        var enemy = profiles.SingleOrDefault(p => p.Name == "enemy");
        if (enemy == null) return; // Custom config-only directories need not contain shipped profiles.
        foreach (var method in new[] { "MakeNoise", "ProcessNoise", "ProcessNode" })
        {
            var matches = enemy.Entries.Where(e => e.Type == "NoiseManager" && e.MatchesMethod(method) && !e.SignatureOnly).ToArray();
            check(matches.Length == 1 && matches[0].Methods.SequenceEqual(new[] { method })
                && matches[0].RecordsPair && matches[0].InstanceFields.Count == 0,
                "noise exact receiver-free pair: " + method);
        }
        var listeners = enemy.Entries.Where(e => e.Type == "Enemies.EnemyAgent"
            && e.MatchesMethod("ListenerDetectNoise") && !e.SignatureOnly).ToArray();
        check(listeners.Length == 1 && listeners[0].Methods.SequenceEqual(new[] { "ListenerDetectNoise" })
            && listeners[0].RecordsPair, "listener has one exact recording owner; broad EnemyAgent entry excludes it");
        var process = enemy.Entries.Single(e => e.Type == "NoiseManager" && e.Methods.SequenceEqual(new[] { "ProcessNode" }));
        check(process.Acoustic?.Fields["radiusMax"] == "arg1.radiusMax"
            && process.Acoustic.Fields["maxRays"] == "arg3"
            && process.Acoustic.Fields["raycast"] == "arg2", "ProcessNode preserves positional argument indices");
        var signature = profiles.SingleOrDefault(p => p.Name == "sound-isolation-signatures");
        check(signature != null && signature.Entries.All(e => e.SignatureOnly && e.Type != "NoiseManager"),
            "remaining sound signature evidence stays non-recording; reviewed NoiseManager entries leave the pending list");
    }
}
