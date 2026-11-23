using System;
using ForgeEngine.Framework;
using SNetwork;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>The native half of `forge.action.map.item_remove`: each recipient is resolved through the level-instance table
/// the spawn readback fills, and a dynamic item is removed through the game's own `ItemReplicationManager.DeSpawn`
/// (host only; every peer drops the instance). Every recipient gets its own row; none is ever replaced by another item.</summary>
internal sealed class ItemRemoveActions
{
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;

    internal ItemRemoveActions(Func<bool> ready, Action<string> report)
    {
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    private const string NetworkItemPrefix = EntityKinds.LevelObject + ":item/network/";
    private static readonly TargetStep<ItemRemoveActions> RemoveStep = Remove;

    internal CommandResult Handle(CommandContext context)
    {
        if (!Session.IsHost || !_ready()) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        var self = this;
        return context.EachTarget(ref self, RemoveStep, EntityKinds.LevelObject, CommandResult.MaximumFacts,
            ResultCode.NativeCommitException, report: _reportException ??= ReportException);
    }

    private Action<EntityReference, Exception>? _reportException;

    private void ReportException(EntityReference target, Exception error)
    {
        try { _report("map.item-remove-exception: " + error.GetType().Name + ": " + error.Message); }
        catch (Exception reportError) { MapLog.Contained("item-remove.report", reportError); }
    }

    /// <summary>One item: removed through the game's own entry and read back from the level-instance table.</summary>
    private static void Remove(ref ItemRemoveActions self, EntityReference target, int index, RowWriter rows)
    {
        rows.Integer(Blocks.ActionMapItemRemove.RowTargetCount, 1);
        var levels = MapHost.Session?.Levels;
        if (!target.Id.StartsWith(NetworkItemPrefix, StringComparison.Ordinal))
        { rows.End(Status.Rejected, Commit.None, ResultCode.UnsupportedRecipient); return; }
        if (levels == null || levels.WorldEpoch != target.WorldEpoch)
        { rows.End(Status.Rejected, Commit.None, ResultCode.StaleEntity); return; }
        var native = levels.NativeInstance(target);
        if (native == null) { rows.End(Status.Rejected, Commit.None, ResultCode.ItemRemoveTargetMissing); return; }
        if (native is not ItemReplicator replicator) { rows.End(Status.Rejected, Commit.None, ResultCode.ItemRemoveLevelItemUnsupported); return; }
        if (replicator.WasCollected || replicator.Item == null) { rows.End(Status.Rejected, Commit.None, ResultCode.ItemRemoveTargetMissing); return; }
        ForgeCore.Items.Despawn(replicator.Cast<IReplicator>());
        if (levels.IsCurrent(target)) rows.End(Status.Failed, Commit.Unknown, ResultCode.ItemRemoveNotGone);
        else rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.ItemRemoved);
    }
}
