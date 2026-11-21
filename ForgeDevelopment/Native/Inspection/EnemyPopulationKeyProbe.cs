using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using GameData;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>F5 experiment, never an automatic startup action. Only detached DataBlocks are written.</summary>
internal static class EnemyPopulationKeyProbe
{
    private static EnemyPopulationKeyReport? _active;
    private static string? _path;
    private static int _thread, _lastFrame = -1;

    internal static string Begin()
    {
        RequireAuthoring();
        if (_active != null) throw new InvalidOperationException("A population-key probe is already active; run its cleanup first.");
        _thread = Environment.CurrentManagedThreadId;
        _lastFrame = -1;
        var revision = CellBuildData.GetRevision().ToString(CultureInfo.InvariantCulture);
        var report = new EnemyPopulationKeyReport(Guid.NewGuid().ToString("N"), revision, Application.version, RuntimeDiagnostics.SessionId);
        var directory = Path.Combine(Paths.BepInExRootPath, "ForgeReports", "enemy-population-keys", revision);
        Directory.CreateDirectory(directory);
        _active = report; _path = Path.Combine(directory, report.RunId + ".forge");
        try
        {
            var ids = new List<uint>();
            var loaded = GameDataBlockBase<EnemyDataBlock>.GetAllBlocks();
            if (loaded == null || loaded.Count > 4096) throw new InvalidOperationException("Loaded enemy table is unavailable or exceeds the probe input bound.");
            foreach (var block in loaded)
                if (block != null && block.internalEnabled && block.persistentID != 0 && !ids.Contains(block.persistentID)) ids.Add(block.persistentID);
            ids.Sort();
            if (ids.Count < 2) throw new InvalidOperationException("At least two enabled enemy DataBlocks must be loaded.");
            report.Start(ids[0], ids[1]);
            report.Write(_path);
            return "Population-key probe started: " + report.Cases.Count + " detached cases; gameplay acceptance remains pending.";
        }
        catch (Exception error)
        {
            report.Stop(error.GetType().Name + ": " + error.Message, rejected: true);
            report.Write(_path);
            _active = null; _path = null;
            throw;
        }
    }

    internal static string Step()
    {
        RequireActive();
        var report = _active!;
        if (report.Results.Count == report.Cases.Count) return "All cases recorded; finish to write the report.";
        var frame = Time.frameCount;
        if (frame == _lastFrame) throw new InvalidOperationException("Population-key probe permits at most one case per Unity frame.");
        _lastFrame = frame;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var observation = Observe(report.Cases[report.Results.Count], report.SourceEnemyIds);
            report.Record(observation, Elapsed(started));
        }
        catch (Exception error) { report.Record(null, Elapsed(started), error); }
        var result = report.Results[^1];
        return result.Id + ": " + result.Status + (result.Error == null ? "" : " (" + result.Error + ")");
    }

    internal static string Finish()
    {
        RequireActive();
        var report = _active!;
        report.Finish(); report.Write(_path!);
        var path = _path!;
        _active = null; _path = null;
        if (report.Execution == "completed-with-errors") throw new InvalidOperationException("Population-key observations include case errors: " + path);
        return "Detached observations written to " + path + "; this is not a generated-ID safety or gameplay pass.";
    }

    // ExperimentEngine runs cleanup after cancellation and failure as well as normal completion.
    internal static string Cleanup()
    {
        if (_active == null) return "No detached population-key probe remains active.";
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Probe cleanup must run on its experiment thread.");
        // Pure owned-report cleanup is still allowed if Authoring is suspended during the run.
        // It touches no game API and cannot start or continue a native case.
        var report = _active;
        report.Stop("Experiment cleanup before all cases completed; unrecorded cases remain pending.");
        try { report.Write(_path!); }
        finally { _active = null; _path = null; }
        // No Unity objects or global tables are owned. Each Step releases its detached wrappers locally.
        return "Population-key probe stopped; partial observations saved, no live tables changed.";
    }

    private static void RequireAuthoring()
    {
        if (ExperimentRunner.Instance == null || !ExperimentRunner.Running || ExperimentRunner.CurrentCommand != EnemyPopulationKeyReport.CommandId)
            throw new InvalidOperationException("Run this probe through an active ForgeDevelopment Authoring experiment.");
    }
    private static void RequireActive()
    {
        RequireAuthoring();
        if (_active == null || _thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("No population-key probe is active on this experiment thread.");
    }
    private static double Elapsed(long started) => (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;

    private static PopulationKeyObservation Observe(PopulationKeyCase test, uint[] enemies)
    {
        // Never GetBlock<EnemyPopulationDataBlock>, AddBlock, Setup globally, or assign a live table.
        // These wrappers are local to this call; no pointer is retained in the managed report.
        var population = new EnemyPopulationDataBlock();
        var rows = new Il2CppSystem.Collections.Generic.List<EnemyRoleData>();
        foreach (var input in test.Inputs) rows.Add(new EnemyRoleData
        {
            Role = (eEnemyRole)input.Role,
            Difficulty = (eEnemyRoleDifficulty)input.Difficulty,
            Enemy = enemies[input.EnemySlot],
            Cost = input.Cost,
            Weight = 1f
        });
        population.RoleDatas = rows;
        var before = population.EnemyDataIsSetup;
        population.SetupEnemyDataLookup();
        var buckets = population.m_enemyDataPerRoleAndDiff;
        var randomizers = population.m_enemyRandomizerPerRoleAndDiff;
        if (buckets == null || randomizers == null || buckets.Count > EnemyPopulationKeyReport.MaximumRowsPerCase
            || randomizers.Count > EnemyPopulationKeyReport.MaximumRowsPerCase) throw new InvalidOperationException("Detached native lookup exceeded the case bounds or was not created.");
        var observed = new List<PopulationKeyBucket>();
        foreach (var entry in buckets)
        {
            if (entry.Value == null || entry.Value.Count > EnemyPopulationKeyReport.MaximumRowsPerCase) throw new InvalidOperationException("Native bucket exceeded the case row bound.");
            var values = new List<PopulationKeyRow>();
            foreach (var row in entry.Value) values.Add(new((int)row.Role, (int)row.Difficulty, row.Enemy, row.Cost, row.Weight));
            observed.Add(new(entry.Key, values.ToArray()));
        }
        var keys = new List<ushort>();
        foreach (var entry in randomizers) keys.Add(entry.Key);
        // Do not call TryGetEnemyData or draw from BuilderWeightedRandom: detached table ownership
        // does not establish that native selection leaves shared random state untouched.
        return new(before, population.EnemyDataIsSetup, observed.OrderBy(row => row.Key).ToArray(), keys.OrderBy(key => key).ToArray());
    }
}
