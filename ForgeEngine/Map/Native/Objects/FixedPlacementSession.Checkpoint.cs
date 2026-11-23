using System;
using System.Collections.Generic;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

internal sealed partial class FixedPlacementSession
{
    /// <summary>The checkpoint restore has put the saved world back: the game despawned every enemy and respawned the saved
    /// ones as new agents under their original replicated ids, so each authored fixed enemy's old agent is gone and a new
    /// one carries its id. The restore callback runs in the frame of the despawn, when the old agent's object may still
    /// answer as present, so a row is bound again to the one other live agent that has exactly its native id and its
    /// enemy type, whatever state the old agent is in; the author id never changes and no other enemy is ever taken.
    /// The new agent has a new life in ForgeEnemy's ledger, which the next resolve and the <c>rebind</c> identity
    /// record both report. A row with no such agent (the enemy was not part of the saved state) stays on its old agent
    /// and keeps answering destroyed once that agent is gone.</summary>
    internal void RebindEnemiesAfterCheckpoint(IEnumerable<EnemyAgent> liveEnemies)
    {
        ArgumentNullException.ThrowIfNull(liveEnemies);
        if (_disposed || _disposing || _fixedEnemies.Count == 0) return;
        var epoch = _worldEpoch();
        if (_placedLevel is not { } level || _placedEpoch != epoch || _level() != level)
        {
            RecordRebindSkipped(epoch);
            return;
        }
        var byId = new Dictionary<long, List<EnemyAgent>>();
        foreach (var agent in liveEnemies)
        {
            if (agent == null || agent.WasCollected || !agent.Alive) continue;
            if (!byId.TryGetValue(agent.GlobalID, out var list)) byId[agent.GlobalID] = list = new List<EnemyAgent>();
            list.Add(agent);
        }
        var rows = new List<KeyValuePair<string, FixedEnemyLife>>(_fixedEnemies);
        foreach (var (id, row) in rows)
        {
            EnemyAgent? found = null;
            var matches = 0;
            if (byId.TryGetValue(row.NativeId, out var candidates))
                foreach (var agent in candidates)
                    if (agent.EnemyDataID == row.EnemyType && !ForgeCore.NativeObjects.Same(agent, row.Agent)) { found = agent; matches++; }
            if (matches == 0)
            {
                if (row.Agent == null || row.Agent.WasCollected) LogEnemyIdentity("rebind", id, row, null, "fixed-enemy-not-restored");
                continue;
            }
            // A native id held by two other live agents names neither of them.
            if (matches > 1) { LogEnemyIdentity("rebind", id, row, null, "fixed-enemy-native-id-ambiguous"); continue; }
            // The restore callback runs inside the runtime's lifecycle notification, which refuses every kernel call, so
            // ForgeEnemy's identity of the new agent is not asked for here: the row keeps no reference, and the first
            // resolve outside the notification reads the new agent's life and keeps it.
            try
            {
                _fixedEnemies[id] = new FixedEnemyLife(found!, null,
                    found!.GlobalID, found.Pointer, found.gameObject?.GetInstanceID() ?? -1, row.EnemyType);
                LogEnemyIdentity("rebind", id, row, null, "rebound");
            }
            catch (Exception error)
            {
                // One enemy that cannot be read stays on its old agent and answers destroyed; it never stops the rows after it.
                _report("Map fixed enemy " + id + " checkpoint rebind failed: " + RuntimeDiagnostics.Describe(error));
                LogEnemyIdentity("rebind", id, row, null, "fixed-enemy-rebind-failed");
            }
        }
    }

    private void RecordRebindSkipped(long sessionWorld)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        MapLog.Record(RuntimeLogLevel.Info, "map.fixed-enemy-identity", "Fixed enemy checkpoint rebind skipped.",
            RuntimeLogValue.Quote("phase", "rebind"), RuntimeLogValue.Of("placedWorld", _placedEpoch),
            RuntimeLogValue.Of("sessionWorld", sessionWorld), RuntimeLogValue.Quote("level", _placedLevel?.ToString() ?? ""),
            RuntimeLogValue.Quote("reason", "fixed-enemy-rebind-session-mismatch"));
    }
}
