using System;
using System.Globalization;
using ForgeMap.Fields;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;
using Player;
using UnityEngine;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The level sentry's native half (`forge.eosext.LevelSpawnedSentry`): Map decides where a sentry stands and ForgeCore spawns it
/// (<see cref="ForgeCore.Gear.SpawnSentry"/>, the game's one sentry spawn entry, including the class-ammunition units).
///
/// - The gear is the authored `PlayerOfflineGearDataBlock`'s own `GearJSON`.
/// - The pose is the authored room-local point and Euler degrees composed with the room the level really built
///   (<see cref="RoomResolver"/>), and the course node is the navigation graph's own node at that point.
/// - `InitialAmmo` and `AmmoCap` are authored as fractions of a full load; ForgeCore multiplies them by the host's class pool.
/// - The owner is the host's own player agent, exactly as a player placing a sentry is its owner.
///
/// The spawn is the host's alone — the gear spawn is replicated to every peer, so a client's second call would stand
/// a second sentry — and runs once per world, when the level is built and in play and the host's own agent exists.
/// </summary>
internal static class LevelSentrySpawner
{
    /// <summary>The public gear name every level sentry carries (replicated to every peer with the spawn): the game's sentry code
    /// marks the owner's class tool slot deployed for any sentry it spawns, and the owner here is the host, so ForgeCore keeps a
    /// sentry with this name off the host's tool slot (<see cref="ForgeCore.Gear.KeepToolSlot"/>). The weapon identity does not read the
    /// name: a level sentry is bound by its authored address.</summary>
    private const string Mark = "FM:level-sentry";
    private static SystemObjectPlan? _plan;
    private static bool _toolSlotKept;

    internal static void Install(SystemObjectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan.AnySentries ? plan : null;
        if (_plan == null || _toolSlotKept) return;
        _toolSlotKept = true;
        ForgeCore.Gear.KeepToolSlot(name => name == Mark);
    }

    /// <summary>Whether the documents name a sentry for this level at all.</summary>
    internal static bool Pending(MapLevelReference? level) => _plan?.For(level)?.Sentries.Count > 0;

    /// <summary>Spawns this level's sentries. Returns false without spawning while the level is not built and in play,
    /// or when the host's own agent does not exist yet, so the caller asks again at its next native stage; true once the
    /// level's rows were handled. The host's agent and the level identity exist as soon as loading starts, but the
    /// rooms (a Forge room template among them) are generated afterwards, so a room is only resolvable once the
    /// native build is done and the expedition is running — the same fact the trigger zones and interaction points
    /// wait for.</summary>
    internal static bool Spawn(long epoch, MapLevelReference? level)
    {
        if (_plan?.For(level) is not { } rows || rows.Sentries.Count == 0) return true;
        if (!LG_Factory.BuildDone || GameStateManager.CurrentStateName != eGameStateName.InLevel) return false;
        var owner = Players.Local;
        if (owner == null || owner.WasCollected) return false;
        // The ammunition figures are fractions of the class pool, so the pool must be readable before any row is spawned.
        if (!ForgeCore.Gear.SentryAmmoReady(owner)) return false;
        var assetsFailure = EffectAssetsFailure();
        int spawned = 0;
        foreach (var row in rows.Sentries)
            if (SpawnOne(epoch, row, owner, assetsFailure)) spawned++;
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplied, "block=" + MapFieldSchema.LevelSentryBlock + " level=" + level
            + " sentries=" + spawned + "/" + rows.Sentries.Count + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture),
            RuntimeLogValue.Of("block", MapFieldSchema.LevelSentryBlock));
        return true;
    }

    /// <summary>The assets the game reads while it sets up a sentry's firing (the one entry every gun spawn asks first, see
    /// <see cref="ForgeCore.Gear"/>), or the reason they are not in the loaded-asset table. A sentry spawned without them throws inside
    /// the spawn callback and leaves a half-built gun, so the rows are refused by name instead.</summary>
    private static string? EffectAssetsFailure()
    {
        try
        {
            var reading = ForgeCore.Gear.PrepareSentry();
            return reading.Status == GearAssetStatus.Ready ? null : reading.Detail;
        }
        catch (Exception error) { return "prepare-threw: " + error.GetType().Name + ": " + error.Message; }
    }

    private static bool SpawnOne(long epoch, LevelSentryRow row, PlayerAgent owner, string? assetsFailure)
    {
        void Refuse(string code, string? detail = null) => MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyRefused,
            "group=level-sentry object=" + row.Object + " code=" + code + (detail == null ? "" : ": " + detail),
            RuntimeLogValue.Of("group", "level-sentry"), RuntimeLogValue.Quote("object", row.Object), RuntimeLogValue.Of("code", code));
        if (assetsFailure != null)
        {
            Refuse("sentry-effect-assets-missing", assetsFailure);
            return false;
        }
        if (!RoomResolver.TryPlace(epoch, row.Room, out var room, out var refusal))
        {
            Refuse(refusal!, "sourcePrefab=" + row.Room.SourcePrefab);
            return false;
        }
        var world = RoomPose.Of(room!.Pose.Point(row.Position),
            room.Pose.Compose(Rotations.Euler(row.Rotation[0], row.Rotation[1], row.Rotation[2])));
        if (!RoomResolver.TryCourseNode(row.Room.Dimension, world.Position, room.AreaCourseNode, out var node))
        {
            Refuse(RoomResolution.NoCourseNode);
            return false;
        }
        var gear = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(row.GearId);
        var json = gear?.GearJSON;
        if (string.IsNullOrEmpty(json))
        {
            Refuse("gear-block-missing", "gear=" + row.GearId.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        var position = new Vector3((float)world.Position[0], (float)world.Position[1], (float)world.Position[2]);
        var rotation = new Quaternion((float)world.Rotation[0], (float)world.Rotation[1], (float)world.Rotation[2], (float)world.Rotation[3]);
        var address = "sentry/" + row.Room.Dimension.ToString(CultureInfo.InvariantCulture) + "/"
            + row.Room.Layer.ToString(CultureInfo.InvariantCulture) + "/" + row.Room.LocalIndex.ToString(CultureInfo.InvariantCulture)
            + "/" + row.Object;
        var (ammo, cap) = ForgeCore.Gear.SpawnSentry(json, Mark, Spawned(address, epoch), position, rotation, node, owner, row.InitialAmmo, row.AmmoCap);
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplyRequested, "block=" + MapFieldSchema.LevelSentryBlock + " object="
            + row.Object + " gear=" + row.GearId.ToString(CultureInfo.InvariantCulture) + " pos=" + Show(position)
            + " ammo=" + ammo.ToString("0.###", CultureInfo.InvariantCulture)
            + " cap=" + cap.ToString("0.###", CultureInfo.InvariantCulture),
            RuntimeLogValue.Of("block", MapFieldSchema.LevelSentryBlock), RuntimeLogValue.Quote("object", row.Object));
        return true;
    }

    /// <summary>The spawn's completion callback: the game hands back the spawned item once its replicated spawn has
    /// run. Only Weapon's identity provider can associate that actual item with this exact author address.
    /// The captured epoch is the spawn's world, so a delayed callback cannot bind into another expedition.</summary>
    private static ItemReplicationManager.delItemCallback Spawned(string address, long epoch)
        => new Action<ISyncedItem, PlayerAgent>((item, _) =>
        {
            try
            {
                if (MapHost.Session == null || ForgeWeapon.Native.WeaponNativeSession.Current is not { } weapon)
                    throw new RuntimeContractException("level-sentry-provider-unavailable", address);
                var entity = weapon.Adapter.BindLevelSentry(address, epoch, item);
                MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FieldApplied,
                    "block=" + MapFieldSchema.LevelSentryBlock + " object=" + address + " entity=" + entity.Id + " spawned=true",
                    RuntimeLogValue.Of("block", MapFieldSchema.LevelSentryBlock), RuntimeLogValue.Quote("object", address),
                    RuntimeLogValue.Of("entity", entity.Id));
            }
            catch (Exception error)
            {
                var code = error is RuntimeContractException refusal ? refusal.Code : "level-sentry-bind-failed";
                MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FieldApplyRefused,
                    "group=level-sentry object=" + address + " code=" + code,
                    RuntimeLogValue.Of("group", "level-sentry"), RuntimeLogValue.Quote("object", address), RuntimeLogValue.Of("code", code));
            }
        });

    private static string Show(Vector3 value) => value.x.ToString("0.###", CultureInfo.InvariantCulture) + ","
        + value.y.ToString("0.###", CultureInfo.InvariantCulture) + "," + value.z.ToString("0.###", CultureInfo.InvariantCulture);
}
