using ForgeDevelopment.Native;

internal static class TraceProfileCases
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-trace-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            ObservationParsing(directory, check);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void ObservationParsing(string directory, Action<bool, string> check)
    {
        var file = Path.Combine(directory, "observation.forge");
        const string valid = "{\"profile\":\"observe\",\"entries\":[{\"entryId\":\"returned.pick\",\"record\":\"observe\",\"phases\":\"after\","
            + "\"target\":{\"declaringType\":\"Game.Population\",\"method\":\"Pick\",\"parameterTypes\":[\"System.Int32\"],\"returnType\":\"System.Boolean\",\"static\":false}}]}";
        File.WriteAllText(file, valid);
        var parsed = RecTracer.Parse(new[] { file }).Single();
        check(parsed.Error == null && parsed.Entries.Single().Observes
            && parsed.Entries.Single().Target!.Signature
                == "instance System.Boolean Game.Population.Pick(System.Int32)",
            "observation preserves full signature");
        File.WriteAllText(file, valid, new System.Text.UTF8Encoding(true));
        parsed = RecTracer.Parse(new[] { file }).Single();
        check(parsed.Error == null && parsed.CatalogHash == ForgeCore.Hash.Sha256Hex(File.ReadAllBytes(file)),
            "BOM profile hashes exact source bytes while preserving existing decoding");
        foreach (var invalid in new[]
        {
            valid.Replace("\"entryId\":\"returned.pick\",", ""),
            valid.Replace("\"target\":", "\"unknown\":1,\"target\":"),
            valid.Replace("\"parameterTypes\":[\"System.Int32\"]", "\"parameterTypes\":[\"System.Int32&\"]"),
            valid.Replace("\"entryId\":\"returned.pick\",",
                "\"entryId\":\"returned.pick\",\"entryId\":\"second\",")
        })
        {
            File.WriteAllText(file, invalid);
            var rejected = RecTracer.Parse(new[] { file }).Single();
            check(rejected.Error != null && rejected.Entries.Count == 0
                && rejected.RejectedObservations.Count > 0,
                "observation refuses incomplete, mixed or unknown configuration with receipt");
        }
        File.WriteAllText(file, valid);
        var duplicate = Path.Combine(directory, "observation-duplicate.forge");
        File.WriteAllText(duplicate, valid.Replace("\"profile\":\"observe\"",
            "\"profile\":\"observe2\""));
        check(RecTracer.Parse(new[] { file, duplicate }).All(profile => profile.Error != null
            && profile.Entries.Count == 0),
            "duplicate observation entryId rejects every colliding profile");
        File.WriteAllText(file,
            "{\"entries\":[{\"type\":\"Game.Population\",\"methods\":[\"Pick\"],\"record\":\"count\"}]}");
        check(RecTracer.Parse(new[] { file }).Single().Entries.Single().Record == "count",
            "legacy trace retains its semantics");
    }
}
