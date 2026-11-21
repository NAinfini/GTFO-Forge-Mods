using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Agents;
using Enemies;
using GameData;
using LevelGeneration;
using UnityEngine;

namespace ForgeDevelopment.Native;

// One command-owned batch. Native allocation/AI/despawn do the work; the existing experiment
// engine owns waits and cancellation. No game-side test service or ForgeEnemy dependency is added.
internal static class AuthoringEnemyBatch
{
    // The native spawn returns an agent whose native life and replicator the game finishes handing out on its own
    // schedule, so both are resolved by the confirmation poll (and again by cleanup) instead of at the spawn call.
    private sealed record Owned(IntPtr Pointer, int Instance, int GlobalId)
    {
        internal NativeLife? Life;
        internal EnemyReplicator? Replicator;
        internal bool Cleaned;
    }
    private sealed record Observation(Owned Owned, EnemyAgent? Agent, string State, string Reason);
    private sealed record TraceRow(float Time, Vector3 Player, Vector3?[] Enemy, string?[] State);
    private sealed class Batch
    {
        internal long World = RecTime.WorldEpoch;
        internal long StartFrame = RecTime.Frame;
        internal uint EnemyId;
        internal int Requested, Generated;
        internal readonly List<Owned> Enemies = new();
        internal readonly List<TraceRow> Trace = new();
    }
    private const long LifeResolutionFrames = 300;
    private const string LifeUnresolved = "enemy-batch.native-life-unresolved";
    private static Batch? _batch;

    internal static ExperimentCallResult Spawn(string json)
    {
        Gate();
        if (_batch != null) throw new InvalidOperationException("enemy-batch.already-owned");
        if (json.Length > 16 * 1024) throw new ArgumentException("enemy-batch.input-budget");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string[] keys = { "schemaVersion", "enemyId", "count", "dimension", "positions", "mode" };
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("enemy-batch.object-required");
        var fields = root.EnumerateObject().Select(field => field.Name).ToArray();
        if (fields.Length != keys.Length || keys.Any(key => fields.Count(field => field == key) != 1))
            throw new ArgumentException("enemy-batch.unknown-duplicate-or-missing-field");
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.String
            || root.GetProperty("schemaVersion").GetString() != "1.0.0") throw new ArgumentException("enemy-batch.schema-version");
        var enemyId = root.GetProperty("enemyId").GetUInt32();
        var count = root.GetProperty("count").GetInt32();
        var dimension = root.GetProperty("dimension").GetInt32();
        if (count is < 1 or > 128 || !Enum.IsDefined(typeof(eDimensionIndex), dimension))
            throw new ArgumentException("enemy-batch.count-or-dimension");
        var mode = root.GetProperty("mode").GetString() switch
        {
            "aggressive" => AgentMode.Agressive,
            "hibernate" => AgentMode.Hibernate,
            _ => throw new ArgumentException("enemy-batch.mode")
        };
        var definition = EnemyDataBlock.GetBlock(enemyId);
        if (definition == null || !definition.internalEnabled) throw new ArgumentException("enemy-batch.enemy-unavailable");
        var boss = definition.name.Contains("Boss", StringComparison.OrdinalIgnoreCase);
        if (definition.BasePrefabs != null)
            foreach (var prefab in definition.BasePrefabs)
                if (prefab != null && prefab.Contains("SquidBossBase", StringComparison.OrdinalIgnoreCase)) boss = true;
        if (definition.isCoccoon || boss)
            throw new ArgumentException("enemy-batch.cocoon-or-boss-refused");
        var player = ForgeCore.Players.Local;
        if (player == null || player.WasCollected || !player.Alive || (int)player.DimensionIndex != dimension)
            throw new InvalidOperationException("enemy-batch.local-player-or-dimension-unavailable");
        var positions = root.GetProperty("positions");
        if (positions.ValueKind != JsonValueKind.Array || positions.GetArrayLength() != count)
            throw new ArgumentException("enemy-batch.one-position-per-enemy-required");
        var sites = new List<(Vector3 Position, AIGraph.AIG_CourseNode Node)>();
        // Resolve every declared site before the first native write. No nearest-point substitution.
        foreach (var row in positions.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 3) throw new ArgumentException("enemy-batch.position");
            var point = new Vector3(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle());
            if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z)
                || !Dimension.TryGetCourseNodeFromPos(point, out var node) || node == null
                || node.m_dimension == null || (int)node.m_dimension.DimensionIndex != dimension)
                throw new ArgumentException("enemy-batch.position-node-or-dimension-unresolved");
            sites.Add((point, node));
        }
        if (!ForgeCore.EnemySpawn.Ready) throw new InvalidOperationException("enemy-batch.allocator-unavailable");
        var batch = new Batch { EnemyId = enemyId, Requested = count };
        _batch = batch; // Cleanup owns even a partial native allocation or a throwing AI setup.
        foreach (var site in sites)
        {
            var enemy = ForgeCore.EnemySpawn.Request(enemyId, site.Node, mode, site.Position, Quaternion.identity);
            if (enemy == null || enemy.WasCollected) throw new InvalidOperationException("enemy-batch.native-spawn-unresolved");
            var owned = new Owned(enemy.Pointer, enemy.GetInstanceID(), enemy.GlobalID);
            batch.Enemies.Add(owned);
            Resolve(owned, enemy);
            if (mode == AgentMode.Agressive)
            {
                if (enemy.AI == null) throw new InvalidOperationException("enemy-batch.native-ai-unavailable");
                enemy.AI.SetTarget(player);
            }
        }
        // The first sample is on the next engine slice, independently enumerating the native world.
        return ExperimentCallResult.Await(() => Confirm(batch));
    }

    private static void Gate()
    {
        RecObservation.RequireNativeRead();
        if (!ExperimentRunner.Running || !RecSession.Active || !ForgeCore.Session.IsHost
            || GameStateManager.CurrentStateName != eGameStateName.InLevel)
            throw new InvalidOperationException("enemy-batch.authoring-host-level-recorder-required");
    }

    private static void CheckContext(Batch batch, bool cleanup)
    {
        if (cleanup)
        {
            if (!ForgeCore.Session.IsHost) throw new InvalidOperationException("enemy-batch.cleanup-host-required");
        }
        else Gate();
        if (RecTime.WorldEpoch != batch.World) throw new InvalidOperationException("enemy-batch.stale-world");
    }

    private static Observation[] Observe(Batch batch, bool cleanup = false)
    {
        CheckContext(batch, cleanup);
        var native = UnityEngine.Object.FindObjectsOfType<EnemyAgent>().Where(enemy => enemy != null && !enemy.WasCollected).ToArray();
        var found = new List<Observation>();
        foreach (var owned in batch.Enemies)
        {
            var matches = native.Where(enemy => enemy.Pointer == owned.Pointer).ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException("enemy-batch.stale-or-ambiguous-native-life");
            if (matches.Length == 1 && (owned.Life == null || owned.Replicator == null)) Resolve(owned, matches[0]);
            found.Add(Inspect(batch, owned, matches.SingleOrDefault()));
        }
        return found.ToArray();
    }

    private static Observation Inspect(Batch batch, Owned owned, EnemyAgent? enemy)
    {
        // An owned enemy whose life was never confirmed (a failed or cancelled confirmation) is still this
        // exact native agent: pointer, instance, global id and replicator identify it, so cleanup can end it.
        var life = owned.Life;
        var replicator = owned.Replicator;
        if (replicator == null || replicator.WasCollected || replicator.Pointer == IntPtr.Zero)
            throw new InvalidOperationException("enemy-batch.cleanup-readback-missing");
        var observed = NativeLife.Of(enemy);
        var sync = enemy?.Sync;
        var native = sync?.Replicator;
        var supplier = replicator.ReplicatorSupplier;
        var current = life != null && observed == life && supplier != null;
        // Native OnDeSpawn detaches the exact replicator's supplier; the pooled agent may remain visible
        // and IsSetup need not reset.
        if (!current && supplier == null && (enemy == null
            || enemy.GetInstanceID() == owned.Instance && (native == null || native.Pointer == replicator.Pointer)))
            return new(owned, null, owned.Cleaned ? "cleaned" : "already-absent", "native-life-ended");
        if (enemy == null || life != null && (!current || observed != life) || enemy.Pointer != owned.Pointer
            || enemy.GetInstanceID() != owned.Instance || enemy.GlobalID != owned.GlobalId || enemy.EnemyDataID != batch.EnemyId
            || !enemy.IsSetup || native?.Pointer != replicator.Pointer || supplier?.Pointer != sync?.Pointer)
            throw new InvalidOperationException("enemy-batch.stale-or-ambiguous-native-life;expected=" + life
                + ";observed=" + observed + ";nativeId=" + enemy?.GlobalID + ";nativeSupplier=" + supplier?.Pointer);
        if (!enemy.Alive)
            return new(owned, null, "already-absent", "native-dead");
        return new(owned, enemy, "present", "native-life-current");
    }

    /// <summary>Takes the native life and the native replicator of one owned enemy as the game holds them now. Neither is ever
    /// guessed: a value stays null until the game answers for exactly this native agent.</summary>
    private static void Resolve(Owned owned, EnemyAgent enemy)
    {
        owned.Replicator ??= enemy.Sync?.Replicator?.TryCast<EnemyReplicator>();
        if (owned.Life == null && NativeLife.Of(enemy) is { } life) owned.Life = life;
    }

    private static void ResolveAll(Batch batch)
    {
        CheckContext(batch, cleanup: false);
        var native = UnityEngine.Object.FindObjectsOfType<EnemyAgent>().Where(enemy => enemy != null && !enemy.WasCollected).ToArray();
        foreach (var owned in batch.Enemies)
            if (owned.Life == null || owned.Replicator == null)
                if (native.FirstOrDefault(enemy => enemy.Pointer == owned.Pointer) is { } agent) Resolve(owned, agent);
    }

    private static string UnresolvedReason(Batch batch)
    {
        var native = UnityEngine.Object.FindObjectsOfType<EnemyAgent>().Where(enemy => enemy != null && !enemy.WasCollected).ToArray();
        return string.Join(",", batch.Enemies.Where(owned => owned.Life == null || owned.Replicator == null).Select(owned =>
            "globalId=" + owned.GlobalId + "/native=" + native.Any(enemy => enemy.Pointer == owned.Pointer)
            + "/life=" + (owned.Life != null) + "/replicator=" + (owned.Replicator != null)));
    }

    private static ExperimentCallResult Confirm(Batch batch)
    {
        Observation[] found;
        try
        {
            ResolveAll(batch);
            var waiting = batch.Enemies.Any(owned => owned.Life == null || owned.Replicator == null)
                && UnityEngine.Object.FindObjectsOfType<EnemyAgent>().Any(enemy => enemy != null && !enemy.WasCollected
                    && batch.Enemies.Any(owned => owned.Pointer == enemy.Pointer && (owned.Life == null || owned.Replicator == null)));
            if (waiting && RecTime.Frame - batch.StartFrame <= LifeResolutionFrames) return ExperimentCallResult.Await(() => Confirm(batch));
            if (batch.Enemies.Any(owned => owned.Life == null || owned.Replicator == null))
                return ExperimentCallResult.Failed(LifeUnresolved + ";" + UnresolvedReason(batch));
            found = Observe(batch);
        }
        catch (InvalidOperationException error) { return ExperimentCallResult.Failed(error.Message); }
        if (found.Length != batch.Requested || found.Any(row => row.Agent == null || row.Agent.Sync?.Replicator == null))
            return ExperimentCallResult.Failed("enemy-batch.native-spawn-readback-missing");
        if (batch.Enemies.Select(enemy => enemy.Pointer).Distinct().Count() != batch.Requested)
            return ExperimentCallResult.Failed("enemy-batch.duplicate-native-spawn");
        batch.Generated = found.Length;
        var value = Snapshot(batch, found);
        return ExperimentCallResult.Done(value, value);
    }

    /// <summary>One chase sample: the local player and every owned enemy as the game holds them now. The command steps
    /// own the cadence (wait, then sample); nothing here advances time or moves an actor.</summary>
    /// <summary>Each owned enemy now: state, position, course node and AI mode (a sleeper that heard a noise has left Hibernate).</summary>
    internal static string Modes()
    {
        var batch = _batch ?? throw new InvalidOperationException("enemy-batch.not-owned");
        var rows = Observe(batch).Select((row, index) => new
        {
            index,
            state = row.State,
            node = row.Agent?.CourseNode?.NodeID,
            position = row.Agent == null ? null : new[] { MathF.Round(row.Agent.transform.position.x, 2), MathF.Round(row.Agent.transform.position.y, 2), MathF.Round(row.Agent.transform.position.z, 2) },
            mode = row.Agent?.AI?.Mode.ToString(),
            locomotion = row.Agent?.Locomotion?.CurrentStateEnum.ToString()
        }).ToArray();
        return JsonSerializer.Serialize(new { schemaVersion = "1.0.0", rows });
    }

    internal static string Sample()
    {
        var batch = _batch ?? throw new InvalidOperationException("enemy-batch.not-owned");
        var player = ForgeCore.Players.Local;
        if (player == null || player.WasCollected) throw new InvalidOperationException("enemy-batch.local-player-unavailable");
        var found = Observe(batch);
        batch.Trace.Add(new TraceRow(Time.realtimeSinceStartup, player.transform.position,
            found.Select(row => row.Agent == null ? (Vector3?)null : row.Agent.transform.position).ToArray(),
            found.Select(row => row.Agent == null ? row.State : row.Agent.Locomotion?.CurrentStateEnum.ToString()).ToArray()));
        return batch.Trace.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Arrival and stalls from the sampled trace. Arrival: within 2 m horizontally and 2 m vertically of the
    /// player. Stall: positions that stay within 0.1 m of where the run began for 5 s or more before arrival.</summary>
    internal static string Report()
    {
        var batch = _batch ?? throw new InvalidOperationException("enemy-batch.not-owned");
        var trace = batch.Trace;
        if (trace.Count < 2) throw new InvalidOperationException("enemy-batch.trace-too-short");
        float t0 = trace[0].Time;
        var enemies = new List<object>();
        for (int e = 0; e < batch.Enemies.Count; e++)
        {
            float? arrived = null; float longest = 0, longestAt = 0, min3 = float.MaxValue;
            Vector3? stallPoint = null; int runStart = 0; Vector3? last = null; float minY = float.MaxValue, maxY = float.MinValue;
            var path = new List<object>();
            float nextPath = 0;
            for (int i = 0; i < trace.Count; i++)
            {
                var row = trace[i]; var pos = row.Enemy[e];
                if (pos is not { } p) { runStart = i + 1; continue; }
                last = p; minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                var d = row.Player - p; float flat = new Vector2(d.x, d.z).magnitude;
                min3 = Mathf.Min(min3, d.magnitude);
                if (row.Time - t0 >= nextPath)
                {
                    nextPath += 3f;
                    path.Add(new { t = Math.Round(row.Time - t0, 1), p = new[] { Math.Round(p.x, 2), Math.Round(p.y, 2), Math.Round(p.z, 2) }, s = row.State[e] });
                }
                if (arrived == null && flat <= 2f && Mathf.Abs(d.y) <= 2f) { arrived = row.Time - t0; }
                if (arrived != null) break;
                if (trace[runStart].Enemy[e] is not { } start || (p - start).magnitude > 0.1f) { runStart = i; continue; }
                float held = row.Time - trace[runStart].Time;
                if (held > longest) { longest = held; longestAt = trace[runStart].Time - t0; stallPoint = p; }
            }
            enemies.Add(new
            {
                id = batch.Enemies[e].GlobalId,
                arrived = arrived != null,
                arrivalSeconds = arrived == null ? (double?)null : Math.Round(arrived.Value, 1),
                stalled = longest >= 5f,
                longestStillSeconds = Math.Round(longest, 1),
                stillFromSeconds = Math.Round(longestAt, 1),
                stillAt = stallPoint is { } s ? new[] { Math.Round(s.x, 2), Math.Round(s.y, 2), Math.Round(s.z, 2) } : null,
                minDistance = Math.Round(min3, 2),
                minY = Math.Round(minY, 2),
                maxY = Math.Round(maxY, 2),
                last = last is { } l ? new[] { Math.Round(l.x, 2), Math.Round(l.y, 2), Math.Round(l.z, 2) } : null,
                path
            });
        }
        var end = trace[^1];
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            samples = trace.Count,
            seconds = Math.Round(end.Time - t0, 1),
            player = new[] { Math.Round(end.Player.x, 2), Math.Round(end.Player.y, 2), Math.Round(end.Player.z, 2) },
            enemies
        });
    }
    internal static string Read()
    {
        var batch = _batch ?? throw new InvalidOperationException("enemy-batch.not-owned");
        if (batch.Generated != batch.Requested) throw new InvalidOperationException("enemy-batch.initial-readback-unconfirmed");
        return Snapshot(batch, Observe(batch));
    }

    private static string Snapshot(Batch batch, Observation[] found) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0.0",
        worldEpoch = batch.World,
        enemyId = batch.EnemyId,
        requestedCount = batch.Requested,
        generatedCount = batch.Generated,
        observedCount = found.Count(row => row.Agent != null),
        aliveCount = found.Count(row => row.Agent?.Alive == true),
        missingCount = found.Count(row => row.State == "already-absent"),
        cleanedCount = found.Count(row => row.State == "cleaned"),
        enemyGlobalIds = batch.Enemies.Select(enemy => enemy.GlobalId).ToArray(),
        lives = found.Select(row => new
        {
            globalId = row.Owned.GlobalId,
            nativePointer = row.Owned.Pointer.ToString("x"),
            instanceId = row.Owned.Instance,
            worldEpoch = row.Owned.Life?.WorldEpoch,
            lifeInstance = row.Owned.Life?.Instance,
            state = row.State,
            reason = row.Reason
        }).ToArray()
    });

    internal static string Cleanup()
    {
        var batch = _batch;
        if (batch == null) return "native-enemy-batch-empty";
        if (RecTime.WorldEpoch != batch.World)
        {
            _batch = null; // The old world's native lifetime ended; never write into its replacement.
            throw new InvalidOperationException("enemy-batch.cleanup-world-changed");
        }
        var failures = new List<Exception>();
        var issued = new List<Owned>();
        // Cleanup can outlive the run, recorder and InLevel state. The unchanged world, current host and
        // exact native identities remain the write authority.
        foreach (var row in Observe(batch, cleanup: true))
        {
            if (row.Agent == null) continue;
            try
            {
                CheckContext(batch, cleanup: true);
                // A preceding despawn may run callbacks. Recheck this exact life immediately before writing it.
                var enemy = Inspect(batch, row.Owned, row.Agent).Agent;
                if (enemy == null) continue;
                var replicator = enemy.Sync?.Replicator ?? throw new InvalidOperationException("enemy-batch.cleanup-readback-missing");
                replicator.Despawn();
                issued.Add(row.Owned);
            }
            catch (Exception error) { failures.Add(error); }
        }
        var after = Observe(batch, cleanup: true);
        for (var index = 0; index < after.Length; index++)
        {
            var row = after[index];
            if (!issued.Contains(row.Owned)) continue;
            if (row.Agent != null || row.Reason != "native-life-ended")
                failures.Add(new InvalidOperationException("enemy-batch.cleanup-native-despawn-unconfirmed:" + row.Owned.GlobalId));
            else
            {
                row.Owned.Cleaned = true;
                after[index] = row with { State = "cleaned" };
            }
        }
        var remaining = after.Count(row => row.Agent != null);
        if (remaining != 0) failures.Add(new InvalidOperationException("enemy-batch.cleanup-native-lives-remain:" + remaining));
        if (failures.Count != 0) throw new AggregateException("enemy-batch.cleanup-failed", failures);
        _batch = null;
        return (after.Any(row => row.State == "cleaned") ? "native-enemy-batch-cleaned;" : "native-enemy-batch-already-absent;")
            + Snapshot(batch, after);
    }
}
