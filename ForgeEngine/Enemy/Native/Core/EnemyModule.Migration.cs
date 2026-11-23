using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Agents;
using Enemies;
using ForgeEngine.Framework;

namespace ForgeEnemy.Native;

/// <summary>
/// Host migration. The game recalls its world first, so the enemies a machine already tracked are still standing
/// when the kernel moves to the new world epoch; Forge's own tables are what the epoch change invalidates.
/// Every machine follows the same way: the tables are cleared as they are for any world end, the enemies that are
/// still standing are tracked again under the new epoch, and the action-state snapshot the old host captured
/// (which the kernel restores right after this notification) binds its lives to them. What the old host
/// held in ledgers only it owned is carried by <see cref="FollowMigration"/> where it can be (foam, markers) and
/// ends where it cannot (modifier contributions, in-flight abilities). The one native obligation this provider
/// registers with the kernel is the marker batch behind a `marker` handle; it is rebound to the same batch under
/// the same handle.
/// </summary>
internal sealed partial class EnemyModule
{
    private const string MigrationSchema = "forge.enemy.markers/1";
    private bool _followingMigration;

    /// <summary>The lifecycle observer's answer to a migration world change.</summary>
    private void FollowMigration()
    {
        CheckThread();
        var survivors = new List<(EnemyAgent Enemy, AgentMode Mode, bool ModeKnown)>();
        foreach (var entry in _entities.Values.OrderBy(entry => entry.GlobalId))
        {
            try
            {
                if (entry.Enemy != null && entry.Enemy.GlobalID == entry.GlobalId && entry.Enemy.Pointer == entry.EnemyPointer && entry.Enemy.Alive)
                    survivors.Add((entry.Enemy, entry.SpawnMode, entry.SpawnModeKnown));
            }
            catch (Exception error) { NativeFault.Note("enemy.migration.survivor", error); }
        }
        ClearWorldContained(releaseInstanceAppearance: false);
        try { ReleaseDestroyedInstanceAppearance(); }
        catch (Exception error) { Log.Exception(RuntimeLogLevel.Error, EnemyLogCodes.CleanupFailed, error); }
        // The life the snapshot names is bound by the restore that follows, so a re-tracked life is not its own
        // authority here, whichever machine it is.
        _followingMigration = true;
        try
        {
            foreach (var (enemy, mode, modeKnown) in survivors)
            {
                try
                {
                    TrackSpawn(enemy);
                    if (modeKnown && _entities.TryGetValue(enemy.GlobalID, out var entry) && entry.EnemyPointer == enemy.Pointer)
                    { entry.SpawnMode = mode; entry.SpawnModeKnown = true; }
                }
                catch (Exception error) { NativeFault.Note("enemy.migration.track", error); }
            }
        }
        finally { _followingMigration = false; }
        var lives = _entities.Values.ToDictionary(entry => entry.EnemyPointer, entry => entry.Reference);
        RetargetMarks(lives);
        _glue?.Follow(pointer => lives.TryGetValue(pointer, out var life) ? life : null, CurrentHandle);
    }

    /// <summary>Marker records name the enemy life they track; the life they tracked ended with the old world
    /// tables, so each follows its enemy to the life it was tracked under again, and a marker whose enemy is gone
    /// is removed.</summary>
    private void RetargetMarks(IReadOnlyDictionary<IntPtr, EntityReference> lives)
    {
        for (int batchIndex = _batches.Count - 1; batchIndex >= 0; batchIndex--)
        {
            var batch = _batches[batchIndex];
            for (int index = batch.Records.Count - 1; index >= 0; index--)
            {
                var record = batch.Records[index];
                if (lives.TryGetValue(record.EnemyPointer, out var life)) { batch.Records[index] = record with { Target = life }; continue; }
                batch.Records.RemoveAt(index);
                RemoveMarker(record.Marker);
            }
            if (batch.Records.Count == 0) _batches.RemoveAt(batchIndex);
        }
    }

    /// <summary>A handle as it is named under the world this machine is in now. A snapshot or ledger that outlived a
    /// host migration still names the world before it; the slot and life in it are the kernel's, kept across the
    /// migration, so only the world is replaced. A handle already in this world, or the default value, is returned
    /// unchanged.</summary>
    internal RuntimeHandle CurrentHandle(RuntimeHandle handle)
        => !handle.IsCast || handle.WorldEpoch == _kernel.WorldEpoch ? handle : handle.InWorld(_kernel.WorldEpoch);

    private void RegisterMigration()
        => _registration.RegisterProviderHandover(MigrationSchema, _ => RuntimeJson.From(new { markers = _batches.Count }), PrepareMigration);

    /// <summary>Read-only preflight: every native handle of this provider is a `marker` handle, whose batch is a
    /// local presentation object this machine placed, so it is found under the handle as it is now and rebound to
    /// the same handle in the new world. A machine that holds no such batch refuses by name.</summary>
    private ProviderHandoverAdoption PrepareMigration(JsonElement context)
    {
        long source = context.GetProperty("sourceEpoch").GetInt64();
        var rebinds = new List<ProviderHandleAdoption>();
        foreach (var held in context.GetProperty("handles").EnumerateArray())
        {
            if (!RuntimeHandle.TryRead(held.GetProperty("handle"), out var adopted) || !_registration.TryNative(adopted.InWorld(source), out var native)
                || native is not MarkerBatch batch)
                throw new RuntimeContractException("migration-native-handle", "An enemy marker handle names no marker batch on this machine.");
            Func<object, bool>? isLive = held.GetProperty("liveness").GetBoolean() ? placed => placed is MarkerBatch live && live.AnyLive() : null;
            Action? cancel = held.GetProperty("cancel").GetBoolean() ? () => RemoveMarked(batch) : null;
            rebinds.Add(new ProviderHandleAdoption(adopted, held.GetProperty("native").GetBoolean() ? batch : null, isLive, cancel));
        }
        return new ProviderHandoverAdoption(rebinds, static () => { }, static () => { });
    }
}
