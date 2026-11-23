using System;
using System.Collections.Generic;
using LevelGeneration;

namespace ForgeMap.Native;

internal sealed partial class FixedPlacementSession
{
    private sealed record PendingTask(long Epoch, MapLevelReference Level, Action Build);
    private readonly List<PendingTask> _pendingTasks = new();
    private IDisposable? _taskBatchDone;
    private long _taskGraphEpoch = -1;
    private MapLevelReference? _taskGraphLevel;
    private LG_PickupItem? _replayingTaskCarry;

    private void WatchTaskGeneration()
    {
        foreach (var row in _items)
            if (row.TaskItemsOnly)
            {
                _taskBatchDone = ForgeCore.FactoryEvents.OnBatchDone(TaskBatchDone);
                return;
            }
    }

    private bool TaskGraphReady => LG_Factory.BuildDone
        || _taskGraphEpoch == _worldEpoch() && _taskGraphLevel == _level();

    private void TaskBatchDone(LG_Factory.BatchName batch)
    {
        // Native prefab builders can request task contents in batch 20. The course graph and
        // clusters finish in 36, before resources, locks, culling and objective linking consume them.
        if ((int)batch != 36 || _disposed || _disposing || _level() is not { } level) return;
        var epoch = _worldEpoch();
        if (epoch == 0) return;
        _taskGraphEpoch = epoch;
        _taskGraphLevel = level;
        ZoneIndex.Reset();
        var requests = _pendingTasks.ToArray();
        _pendingTasks.Clear(); // Each original request is attempted once, including a native failure.
        foreach (var request in requests)
        {
            if (request.Epoch != epoch || request.Level != level)
            { TaskRefused("", "task-world-stale"); continue; }
            try { request.Build(); }
            catch (Exception error) { TaskPreparationFailed(error); }
        }
    }

    private bool DeferTask(Action build)
    {
        if (TaskGraphReady) return false;
        if (_level() is not { } level || _worldEpoch() == 0)
        { TaskRefused("", "task-world-unavailable"); return true; }
        _pendingTasks.Add(new PendingTask(_worldEpoch(), level, build));
        return true;
    }

    internal bool ReplayingTaskCarry(LG_PickupItem pickup)
        => _replayingTaskCarry?.Pointer == pickup.Pointer;

    internal bool DeferTaskCarry(LG_PickupItem pickup, uint itemId, bool isWardenObjectiveItem, int chainIndex)
    {
        if (!TaskContainersPending || TaskGraphReady || !IsRegionalTaskCarry(pickup)) return false;
        return DeferTask(() =>
        {
            if (pickup == null || pickup.WasCollected)
            { TaskRefused("", "task-native-pickup-missing"); return; }
            if (!PrepareTaskCarry(pickup, itemId, chainIndex, out var slot)) return;
            var previous = _replayingTaskCarry;
            _replayingTaskCarry = pickup;
            try { pickup.SetupBigPickupItemWithItemId(itemId, isWardenObjectiveItem, chainIndex); }
            finally { _replayingTaskCarry = previous; }
            if (slot != null) CompleteTaskCarry(pickup, slot);
        });
    }

    private void StopTaskGeneration()
    {
        _taskBatchDone?.Dispose();
        _taskBatchDone = null;
        _pendingTasks.Clear();
    }
}
