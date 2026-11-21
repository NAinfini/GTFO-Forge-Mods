using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeDevelopment.Native;

// These records describe observations, not an implementation of the game's key function.
internal sealed record PopulationKeyInput(int Role, int Difficulty, int EnemySlot, float Cost);
internal sealed record PopulationKeyCase(string Id, PopulationKeyInput[] Inputs);
internal sealed record PopulationKeyRow(int Role, int Difficulty, uint Enemy, float Cost, float Weight);
internal sealed record PopulationKeyBucket(ushort Key, PopulationKeyRow[] Rows);
internal sealed record PopulationKeyObservation(bool SetupBefore, bool SetupAfter, PopulationKeyBucket[] Buckets,
    ushort[] RandomizerKeys);
internal sealed record PopulationKeyResult(string Id, string Status, double Milliseconds,
    PopulationKeyObservation? Observation, string? Error);

/// <summary>Pure report/case contract. No game types and no guessed role/difficulty packing.</summary>
internal sealed class EnemyPopulationKeyReport
{
    internal const int MaximumCases = 192;
    internal const int MaximumRowsPerCase = 2;
    internal const int MaximumBytes = 1024 * 1024;
    internal const string CommandId = "enemy-population-keys";
    internal const string PointId = "map.enemy-population-key-bounds";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly List<PopulationKeyResult> _results = new();

    public string Format => "forge-enemy-population-key-probe";
    public int SchemaVersion => 1;
    public string EvidenceScope => "detached-native-setup-keys-only";
    public string SelectionStatus => "not-invoked";
    public string SelectionAndRandomSemantics => "pending-game";
    public string CaseBasis => "revision-34873 role/difficulty enums and bounded generated-identity samples";
    public int CaseLimit => MaximumCases;
    public int RowsPerCaseLimit => MaximumRowsPerCase;
    public bool GameReady => false;
    public string Evidence => _results.Any(row => row.Observation != null) ? "native-observations" : "pending-game";
    public string Execution { get; private set; } = "not-run";
    public string? StopReason { get; private set; }
    public string RunId { get; }
    public string GameRevision { get; }
    public string GameVersion { get; }
    public string SessionId { get; }
    public uint[] SourceEnemyIds { get; private set; } = Array.Empty<uint>();
    public IReadOnlyList<PopulationKeyCase> Cases { get; } = BuildCases();
    public IReadOnlyList<PopulationKeyResult> Results => _results;
    public string[] Limits => new[] {
        "No live DataBlock registration or authoring changes; results concern detached tables only.",
        "A native setup call cannot be interrupted; one case (at most two input rows) runs per frame and elapsed time is recorded.",
        "Observed keys do not prove a packing formula or safety for every possible generated/exported identity.",
        "TryGetEnemyData is not invoked because its shared-RNG effects are unknown. Selection semantics remain pending; run setup observation in a disposable Authoring test session.",
        "No spawned-enemy, multiplayer or installed-package behavior is established by this probe."
    };

    internal EnemyPopulationKeyReport(string runId, string revision, string version, string session)
    {
        RunId = runId; GameRevision = revision; GameVersion = version; SessionId = session;
    }

    internal void Start(uint first, uint second)
    {
        if (Execution != "not-run" || first == 0 || second == 0 || first == second) throw new InvalidOperationException("Two distinct loaded enemy IDs and a fresh report are required.");
        SourceEnemyIds = new[] { first, second }; Execution = "running";
    }

    internal void Record(PopulationKeyObservation? observation, double milliseconds, Exception? error = null)
    {
        if (Execution != "running" || _results.Count >= Cases.Count) throw new InvalidOperationException("No population-key case is pending.");
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || (observation == null) == (error == null)) throw new ArgumentException("A case records one observation or one error.");
        if (observation != null && (observation.Buckets.Length > MaximumRowsPerCase || observation.RandomizerKeys.Length > MaximumRowsPerCase
            || observation.Buckets.Sum(bucket => bucket.Rows.Length) > MaximumRowsPerCase))
            throw new ArgumentException("Population-key observation exceeds the case bounds.");
        if (observation != null && observation.Buckets.SelectMany(bucket => bucket.Rows).Any(row => !float.IsFinite(row.Cost) || !float.IsFinite(row.Weight)))
            throw new ArgumentException("Native observation contains a non-finite number.");
        var reason = error == null ? null : error.GetType().Name + ": " + error.Message;
        if (reason?.Length > 1024) reason = reason[..1024];
        _results.Add(new(Cases[_results.Count].Id, observation == null ? "error" : "observed", milliseconds, observation, reason));
    }

    internal void Finish()
    {
        if (Execution != "running" || _results.Count != Cases.Count) throw new InvalidOperationException("An incomplete probe cannot finish as complete.");
        Execution = _results.Any(row => row.Status == "error") ? "completed-with-errors" : "completed";
    }

    internal void Stop(string reason, bool rejected = false)
    {
        if (Execution is "completed" or "completed-with-errors" or "cancelled" or "rejected") return;
        StopReason = reason.Length <= 1024 ? reason : reason[..1024];
        Execution = rejected ? "rejected" : "cancelled";
    }

    internal void Write(string path)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Population-key report exceeds its byte limit.");
        ForgeCore.Store.WriteBytes(path, bytes);
    }

    private static PopulationKeyCase[] BuildCases()
    {
        var cases = new List<PopulationKeyCase>();
        PopulationKeyInput Input(int role, int difficulty, int slot = 0) => new(role, difficulty, slot, 101 + slot);
        for (var role = 0; role <= 10; role++)
        {
            for (var difficulty = 0; difficulty <= 7; difficulty++) cases.Add(new($"vanilla:{role}:{difficulty}", new[] { Input(role, difficulty) }));
            foreach (var difficulty in new[] { 255, 256, 65535, 65536, 1112531239, 1197552167, 1743184167 })
                cases.Add(new($"boundary:{role}:{difficulty}", new[] { Input(role, difficulty) }));
        }
        // Real nativeBlockIdentity('EnemyGroup', 'budget:key-probe:35/40/680') outputs.
        // Shared low bits select useful counterexamples; they do not claim the game masks those bits.
        foreach (var role in new[] { 0, 1, 5, 10 })
        {
            cases.Add(new($"pair-low8:{role}", new[] { Input(role, 1112531239), Input(role, 1197552167, 1) }));
            cases.Add(new($"pair-low16:{role}", new[] { Input(role, 1112531239), Input(role, 1743184167, 1) }));
        }
        foreach (var difficulty in new[] { 1, 255, 256, 65536 })
            cases.Add(new($"cross-role:{difficulty}", new[] { Input(0, difficulty), Input(1, 0, 1) }));
        if (cases.Count > MaximumCases || cases.Any(row => row.Inputs.Length > MaximumRowsPerCase)) throw new InvalidOperationException("Unbounded population-key case plan.");
        return cases.ToArray();
    }
}
