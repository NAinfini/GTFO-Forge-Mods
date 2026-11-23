using System;
using System.Collections.Generic;
using System.Globalization;
using AIGraph;
using ForgeEngine.Framework;
using GameData;
using UnityEngine;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>Spawn through the vanilla replication manager and return the observed network instance.</summary>
internal sealed class ItemSpawnActions
{

    private readonly Func<bool> _ready;
    private readonly Func<long> _worldEpoch;
    private readonly Action<string> _report;
    private readonly Func<LevelObjectModule?> _levels;

    internal ItemSpawnActions(Func<bool> ready, Func<long> worldEpoch, Action<string> report,
        Func<LevelObjectModule?>? levels = null)
    {
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _worldEpoch = worldEpoch ?? throw new ArgumentNullException(nameof(worldEpoch));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _levels = levels ?? (() => MapHost.Session?.Levels);
    }

    internal CommandResult Handle(CommandContext context)
    {
        if (!context.IsHost || !Session.IsHost || !_ready()) return Refuse(context, ResultCode.AuthorityOrPhase);
        if (Mode(context) is not { } mode) return Refuse(context, ResultCode.ItemModeUnknown);
        if (!context.TryResource(ItemSpawnContract.Item, out string kind, out string id)
            || kind != ItemSpawnContract.ItemResourceKind) return Refuse(context, ResultCode.ItemResourceRequired);
        if (!uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint itemId)
            || GameDataBlockBase<ItemDataBlock>.GetBlock(itemId) is not { } block)
            return Refuse(context, ResultCode.ItemBlockMissing);
        if (context.Vector(ItemSpawnContract.Position) is not { } positionValue) return Refuse(context, ResultCode.ItemPoseInvalid);
        if (context.Vector(ItemSpawnContract.Rotation) is not { } rotationValue) return Refuse(context, ResultCode.ItemPoseInvalid);
        var position = new Vector3((float)positionValue.X, (float)positionValue.Y, (float)positionValue.Z);
        var euler = new Vector3((float)rotationValue.X, (float)rotationValue.Y, (float)rotationValue.Z);
        // The origin node is a real node of this level, found at the point the request named. It is resolved
        // before anything is built: an item asked for outside every course node this level generated would
        // otherwise be spawned with an origin the game never assigned.
        if (CourseNodeAt(position) is not { } node) return Refuse(context, ResultCode.ItemPositionNoCourseNode);
        if (_worldEpoch() != context.WorldEpoch || _levels() is not { } levels || levels.WorldEpoch != context.WorldEpoch)
            return Refuse(context, ResultCode.ItemSpawnRefused);
        EntityReference? spawned;
        try
        {
            // ForgeCore clears the replication manager's return slot before the call and reads it straight after, so the
            // answer is the instance this request made; the vanilla convenience callback only runs for a local owner.
            var native = ForgeCore.Items.Spawn(itemId, block.inventorySlot, null, mode, position,
                Quaternion.Euler(euler.x, euler.y, euler.z), node, out var why);
            if (why == ForgeCore.ItemSpawnRefusal.NotReady) return Refuse(context, ResultCode.ItemsNotReady);
            if (why == ForgeCore.ItemSpawnRefusal.ModeMissing) return Refuse(context, ResultCode.ItemModeMissing);
            spawned = _worldEpoch() == context.WorldEpoch && native != null
                ? LevelInstanceObservation.Item(native, levels) : null;
        }
        catch (Exception error)
        {
            Report("map.item-spawn-exception: " + error.GetType().Name + ": " + error.Message);
            return Row(context, Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
        }
        return spawned == null
            ? Row(context, Status.Failed, Commit.Unknown, ResultCode.ItemSpawnResultUnavailable)
            : Row(context, Status.Succeeded, Commit.Confirmed, ResultCode.ItemSpawned, spawned);
    }
    private static CommandResult Refuse(CommandContext context, string code)
        => Row(context, Status.Rejected, Commit.None, code);

    /// <summary>The one row, whose target is the item resource the request named on its recipient port, and the
    /// command's own conclusion with the same code. A request that carried no item reference has no recipient, so no
    /// row is written for it — a row's target is the recipient at its position — while the command's own status and
    /// code still report the decision. The spawned column is the observed native entity, or null when no real
    /// receipt returned; the named output carries this same value.</summary>
    private static CommandResult Row(CommandContext context, Status status, Commit commit, string code, EntityReference? spawned = null)
    {
        if (!context.TryResource(ItemSpawnContract.Item, out _, out _)) return new Outcome(status, commit, code);
        context.Rows.Begin(0).Entity(Blocks.ActionMapItemSpawn.RowSpawned, spawned).End(status, commit, code);
        context.Outputs.Entity(Blocks.ActionMapItemSpawn.OutSpawned, spawned);
        return new Outcome(status, commit, code);
    }

    /// <summary>The `mode` the request named, as the game's own enum member. A member outside the row's four is
    /// refused rather than mapped onto the cheapest one.</summary>
    private static ItemMode? Mode(CommandContext context) => context.Text(ItemSpawnContract.Mode) switch
    {
        "first_person" => ItemMode.FirstPerson,
        "third_person" => ItemMode.ThirdPerson,
        "pickup" => ItemMode.Pickup,
        "instance" => ItemMode.Instance,
        _ => null
    };

    /// <summary>The course node the named point stands in, searched across the dimensions the game indexes. The
    /// row carries no dimension of its own, so nothing is assumed about which one the point belongs to: the first
    /// dimension whose graph holds the point answers, and a point no dimension holds is refused.</summary>
    private static AIG_CourseNode? CourseNodeAt(Vector3 position) => ForgeCore.Level.CourseNodeAt(position, 1, out var node, out _) ? node : null;

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception reportError) { MapLog.Contained("item-spawn.report", reportError); }
    }

    // ---- the `item` resource kind ---------------------------------------------------------------------

    /// <summary>Every item this install's own data block table holds, keyed by the block's persistent id — the
    /// same id the spawn entry takes. The table is the game's, so a plan naming an item this install does not
    /// carry resolves to nothing instead of to the nearest block.</summary>
    internal static IReadOnlyList<ResourceRef> EnumerateItems()
    {
        var references = new List<ResourceRef>();
        var blocks = GameDataBlockBase<ItemDataBlock>.GetAllBlocks();
        if (blocks == null) return references.AsReadOnly();
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block == null || block.WasCollected) continue;
            references.Add(new ResourceRef(ItemSpawnContract.ItemResourceKind,
                block.persistentID.ToString(CultureInfo.InvariantCulture)));
        }
        return references.AsReadOnly();
    }

    /// <summary>One item by its persistent id, or nothing when this install loaded no block with it — which is
    /// what makes a plan naming an item this build does not have fail as `stale-resource`.</summary>
    internal static ResourceRef? ResolveItem(string resourceId)
        => uint.TryParse(resourceId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
            && GameDataBlockBase<ItemDataBlock>.GetBlock(id) != null
            ? new ResourceRef(ItemSpawnContract.ItemResourceKind, resourceId) : null;
}
