using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Agents;
using AIGraph;
using BepInEx;
using Enemies;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using LevelGeneration;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The authored fixed placements of one level, put into the world once per level when the level's own generation
/// finished — the same stage the vanilla level places its pickups in, and the stage at which every room's world
/// pose and every course node exist.
///
/// Items are placed by every machine and enemies by the host alone, because that is how the game itself builds
/// them. A placed item's identity lives in its item data and only its state is replicated, so each peer builds
/// its own copy of every authored item from the same document, in the same order, with the same per-row seed;
/// the row's own id is that seed, which is the only thing all peers share before anything is spawned. An enemy
/// group is spawned by one machine and its own spawn data is replicated to the others, so a host-and-client pair
/// that both called it would stand two groups in the same room.
///
/// Fixed sacks and dimension landings are every machine's own work for the same reason items are: a sack prefab
/// carries only a local visual and audio marker and no state to replicate, and a dimension's spawn point list is a
/// local object of the level this machine generated. Neither is gated on the host. An authored bulkhead controller
/// is built by the level's own generator at the authored point while the level is being built, on every machine
/// (<see cref="FixedFunctionPoints"/>). An authored
/// bulkhead key moves an object the level built too — the key its own `BulkheadKeyPlacements` generated — so it is
/// every machine's own work for the same reason, and that half lives in <see cref="BulkheadKeyPlacement"/>.
///
/// Nothing is guessed. A row whose room this level cannot name exactly once, whose point has no course node, or
/// whose item the native tables do not carry is refused with its own code and left out — never placed at the
/// origin and never placed in a neighbouring room. Every refusal and every placement is reported once per level,
/// which is what the in-game check of a placement row reads.
/// </summary>
internal sealed partial class FixedPlacementSession : IDisposable
{
    /// <summary>The fog repeller's activation interaction: `pCarryItemWithGlobalState_Interaction.type` for
    /// "activate", as the weapon's own `OnWield` sends it. The value is a raw byte in the game's structure.</summary>
    private const byte ActivateInteraction = 0;

    private readonly IReadOnlyList<FixedItem> _items;
    private readonly IReadOnlyList<FixedEnemy> _enemies;
    private readonly IReadOnlyList<FixedSack> _sacks;
    private readonly IReadOnlyList<FixedLanding> _landings;
    private readonly IPlayerDirectory _players;
    private readonly IReadOnlyList<FixedBulkheadController> _controllers;
    private readonly IReadOnlyList<FixedBulkheadKey> _keys;
    private readonly Func<long> _worldEpoch;
    private readonly Func<MapLevelReference?> _level;
    private readonly Func<EnemyAgent, EntityReference?> _enemyReference;
    private readonly Action<string> _report;
    private readonly FixedLandingPoints _landingPoints = new();
    private long _placedEpoch = -1;
    /// <summary>A host migration keeps the level and everything placed in it: every world stamp of this session moves to the new number.</summary>
    internal void FollowWorld(long from, long to)
    {
        if (_placedEpoch == from) _placedEpoch = to;
        if (_cargoEpoch == from) _cargoEpoch = to;
        if (_containersEpoch == from) _containersEpoch = to;
        if (_taskGraphEpoch == from) _taskGraphEpoch = to;
    }
    internal LG_ResourceContainer_Storage? InspectContainer(string id, long epoch)
        => !_disposed && !_disposing && _placedEpoch == epoch && _worldEpoch() == epoch
            && _boxes.TryGetValue(id, out var row) && row.Item2 != null && !row.Item2.WasCollected ? row.Item2 : null;
    private MapLevelReference? _placedLevel;
    private Transform? _container;
    private readonly Dictionary<string, (FixedItem Row, LG_ResourceContainer_Storage Storage, AIG_CourseNode Node)> _boxes = new(StringComparer.Ordinal);
    /// <summary>The doors whose one generated key this level's rows already took over: a door has exactly one key,
    /// so a second row for it is refused instead of moving the same pickup again.</summary>
    private readonly HashSet<MapObjectReference> _claimedKeys = new();
    /// <summary>The enemy a fixed row spawned, with the native identities captured when the spawn returned.</summary>
    private sealed record FixedEnemyLife(EnemyAgent Agent, EntityReference? Reference,
        long NativeId, IntPtr NativePointer, int NativeInstance, uint EnemyType);
    private readonly Dictionary<string, FixedEnemyLife> _fixedEnemies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (EntityReference? Current, string Reason)> _enemyIdentityReadbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ItemInLevel Pickup, uint ItemId, bool FreeStanding)> _pickups = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _disposing;

    private FixedPlacementSession(IReadOnlyList<FixedItem> items, IReadOnlyList<FixedEnemy> enemies,
        IReadOnlyList<FixedSack> sacks, IReadOnlyList<FixedLanding> landings,
        IReadOnlyList<FixedBulkheadController> controllers, IReadOnlyList<FixedBulkheadKey> keys,
        Func<long> worldEpoch, Func<MapLevelReference?> level, Func<EnemyAgent, EntityReference?> enemyReference,
        Action<string> report, IPlayerDirectory players)
    {
        _items = items;
        _enemies = enemies;
        _sacks = sacks;
        _landings = landings;
        _players = players;
        _controllers = controllers;
        _keys = keys;
        _worldEpoch = worldEpoch;
        _level = level;
        _enemyReference = enemyReference;
        _report = report;
        WatchTaskGeneration();
    }

    /// <summary>Reads this install's fixed-placement documents and answers the session that places them. A
    /// document that does not read is reported and left out; it never stops the package from loading. What a level
    /// placed goes to the Map provider's own log.</summary>
    internal static FixedPlacementSession Start(Func<long> worldEpoch, Func<MapLevelReference?> level,
        Func<EnemyAgent, EntityReference?> enemyReference, Action<string> report, IPlayerDirectory players, RundownPlacementAdmission admission,
        MapPreparedContent? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(worldEpoch);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(enemyReference);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(admission);
        var items = prepared?.Items ?? FixedPlacementData.LoadItems(Paths.BepInExRootPath, admission, report);
        var enemies = prepared?.Enemies ?? FixedPlacementData.LoadEnemies(Paths.BepInExRootPath, admission, report);
        var sacks = prepared?.Sacks ?? FixedPlacementData.LoadSacks(Paths.BepInExRootPath, admission, report);
        var landings = prepared?.Landings ?? FixedPlacementData.LoadLandings(Paths.BepInExRootPath, admission, report);
        var controllers = prepared?.Controllers ?? FixedPlacementData.LoadBulkheadControllers(Paths.BepInExRootPath, admission, report);
        var keys = prepared?.Keys ?? FixedPlacementData.LoadBulkheadKeys(Paths.BepInExRootPath, admission, report);
        var session = new FixedPlacementSession(items, enemies, sacks, landings, controllers, keys, worldEpoch, level, enemyReference, report, players);
        return session;
    }

    /// <summary>How many rows of each kind this install's documents gave, for the plugin's one load record.</summary>
    internal (int Items, int Enemies, int Sacks, int Landings, int Controllers, int Keys) Loaded
        => (_items.Count, _enemies.Count, _sacks.Count, _landings.Count, _controllers.Count, _keys.Count);

    /// <summary>Whether this install authored any placement at all. A session with nothing to place answers false,
    /// and the hook that calls it then does no work.</summary>
    internal bool Pending => !_disposed && !_disposing && (_items.Count != 0 || _enemies.Count != 0 || _sacks.Count != 0
        || _landings.Count != 0 || _controllers.Count != 0 || _keys.Count != 0);

    /// <summary>
    /// Places this level's rows, once per level: a level is placed when its own identity is read and the world it
    /// belongs to is the one that asked. A second call for the same level in the same world places nothing —
    /// a checkpoint restore reloads state rather than the level, and a placement that ran twice would stand two of
    /// everything.
    /// </summary>
    internal void Place()
    {
        if (_disposed || !Pending) return;
        var epoch = _worldEpoch();
        if (epoch == 0) return;
        if (_level() is not { } level) return;
        if (_placedEpoch == epoch && _placedLevel == level) return;
        // Retire only our previous world's appended entries even when the new level has no landing rows.
        // A failed withdrawal keeps its receipt and leaves this world unplaced for a later retry.
        _landingPoints.Clear();
        _claimedKeys.Clear();
        _fixedEnemies.Clear(); _enemyIdentityReadbacks.Clear();
        _pickups.Clear();
        _placedEpoch = epoch;
        _placedLevel = level;
        if (Container() == null)
        {
            _report("fixed-placement-refused code=no-level-root: this level's own root transform did not read, so "
                + "nothing was placed and a later level is left to place its own rows.");
            return;
        }
        var items = FixedPlacement.ItemsForLevel(_items, level);
        var enemies = FixedPlacement.EnemiesForLevel(_enemies, level);
        var sacks = FixedPlacement.SacksForLevel(_sacks, level);
        var landings = FixedPlacement.LandingsForLevel(_landings, level);
        var controllers = FixedPlacement.BulkheadControllersForLevel(_controllers, level);
        var keys = FixedPlacement.BulkheadKeysForLevel(_keys, level);
        var placedItems = 0;
        EnsureContainers(epoch, level);
        foreach (var item in items) if (item.ContainerType != null && _boxes.ContainsKey(item.Id)) placedItems++;
        foreach (var item in items) if (item.ContainerType == null && item.TaskObject == null && PlaceItem(item, epoch)) placedItems++;
        placedItems += TaskObjectPlacement.Place(epoch, items, _report);
        // A sack is a local visual marker with no state to replicate and a dimension's spawn point list is a local
        // object of the level this machine built, so both halves run on every machine, exactly as items do.
        var placedSacks = 0;
        foreach (var sack in sacks) if (PlaceSack(sack, epoch)) placedSacks++;
        RegisterLandings(landings, epoch);
        // Controllers were built at their authored points while the level was built (FixedFunctionPoints); a row whose
        // point built nothing is named here, once the level is done.
        foreach (var controller in controllers)
            if (FixedFunctionPoints.Built(epoch, controller, out _) is { } reason)
                _report("fixed-placement-refused id=" + controller.Id + " code=" + reason + ": zone=" + controller.ZoneText);
        // A key's position is not replicated state either, and the level's own key placement generated one on every
        // machine: each machine moves the copy its own generated level placed to the authored point.
        var floorKeys = new List<FixedBulkheadKey>();
        foreach (var key in keys)
            if (HasTaskZone(key.Locator)) TaskRefused(key.Id, "fixed-key-outside-task-containers");
            else floorKeys.Add(key);
        BulkheadKeyPlacement.Place(epoch, floorKeys, _report);
        // An enemy group is spawned by the host alone, so a client's own report says so instead of reading as a
        // level whose every enemy was refused. A level that only shows a recording spawns none: its enemies come from the recording.
        if (!Session.IsHost || ForgeCore.Puppets.Playback)
        {
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedPlacement, "level=" + level + " items=" + placedItems + "/" + items.Count
                + " sacks=" + placedSacks + "/" + sacks.Count + " landings=" + landings.Count
                + " controllers=" + controllers.Count + " keys=" + keys.Count + " enemies=host-only epoch=" + epoch,
                RuntimeLogValue.Of("level", level.ToString()), RuntimeLogValue.Of("items", (long)placedItems), RuntimeLogValue.Of("host", false));
            return;
        }
        var placedEnemies = PlaceEnemies(enemies, epoch);
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedPlacement, "level=" + level + " items=" + placedItems + "/" + items.Count
            + " sacks=" + placedSacks + "/" + sacks.Count + " landings=" + landings.Count
            + " controllers=" + controllers.Count + " keys=" + keys.Count + " enemies=" + placedEnemies + "/" + enemies.Count
            + " epoch=" + epoch,
            RuntimeLogValue.Of("level", level.ToString()), RuntimeLogValue.Of("items", (long)placedItems),
            RuntimeLogValue.Of("enemies", (long)placedEnemies), RuntimeLogValue.Of("host", true));
    }

    internal IReadOnlyList<FixedItem> GeneratorRows => _level() is { } level
        ? FixedPlacement.ItemsForLevel(_items, level) : Array.Empty<FixedItem>();

    /// <summary>This level's bulkhead-controller rows, which pin and claim their native requests (<see cref="FixedFunctionPoints"/>).</summary>
    internal IReadOnlyList<FixedBulkheadController> ControllerRows => _level() is { } level
        ? FixedPlacement.BulkheadControllersForLevel(_controllers, level) : Array.Empty<FixedBulkheadController>();

    private bool PlaceContainer(FixedItem item, long epoch)
    {
        if (RoomResolver.PlaceRow(epoch, item, out var refusal) is not { } placed)
        { Refuse(item.Id, refusal!); return false; }
        var (world, node) = placed;
        var storage = FixedContainerPlacement.Create(item, _container!, node, Point(world.Position), Facing(world.Rotation), out refusal);
        if (storage == null) { Refuse(item.Id, refusal!); return false; }
        _boxes.Add(item.Id, (item, storage, node));
        FixedItemRules.Apply(storage.transform, item);
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedContainer, "Native container registered",
            RuntimeLogValue.Quote("id", item.Id), RuntimeLogValue.Of("type", item.ContainerType!),
            RuntimeLogValue.Of("slots", (long)storage.m_storageSlots.Length), RuntimeLogValue.Of("node", Node(node)),
            RuntimeLogValue.Of("lockedFromStart", item.Lock?.LockedFromStart ?? false),
            RuntimeLogValue.Of("weakLockType", (long)(storage.m_core?.TryCast<LG_WeakResourceContainer>()?.WeakLockComponent?.m_lockType ?? eWeakLockType.None)));
        return true;
    }

    /// <summary>One item row: its room, its world pose, its course node, what it is, and the native entry point
    /// its own block's slot is placed through. A row that fails any of those is refused by name and nothing is
    /// spawned for it.</summary>
    private bool PlaceItem(FixedItem item, long epoch)
    {
        if (item.ObjectiveCargo != null) return PlaceObjectiveCargo(item, epoch);
        if (RoomResolver.PlaceRow(epoch, item, out var roomRefusal) is not { } point)
        {
            Refuse(item.Id, roomRefusal!);
            return false;
        }
        var (world, node) = point;
        var position = Point(world.Position);
        var rotation = Facing(world.Rotation);
        uint itemId;
        if (item.TargetDoor is { } doorAddress)
        {
            if (GateKey(doorAddress, out var doorRefusal) is not { } key)
            {
                Refuse(item.Id, doorRefusal!);
                return false;
            }
            itemId = key;
        }
        else
        {
            itemId = item.ItemId!.Value;
        }
        if (GameDataBlockBase<ItemDataBlock>.GetBlock(itemId) is not { } block)
        {
            Refuse(item.Id, "item-block-missing", "item=" + itemId.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        var slot = (int)block.inventorySlot;
        if (FixedPlacement.RouteOf(slot) is not { } route)
        {
            Refuse(item.Id, "slot-not-placeable", "slot=" + slot.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        if (FixedPlacement.AmountRefusal(slot, item.Amount) is { } amountRefusal)
        {
            Refuse(item.Id, "amount-refused", amountRefusal);
            return false;
        }
        if (!ItemSpawnManager.ReadyToSpawnItems)
        {
            Refuse(item.Id, "items-not-ready", "the game has not finished loading its spawnable items");
            return false;
        }
        if (!ItemSpawnManager.ItemHasMode(itemId, ItemMode.Pickup))
        {
            Refuse(item.Id, "pickup-mode-missing", "item=" + itemId.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        LG_ResourceContainer_Storage? destination = null;
        Transform? align;
        if (item.ContainerId is { } containerId)
        {
            if (!_boxes.TryGetValue(containerId, out var box)) { Refuse(item.Id, "container-missing"); return false; }
            if (box.Row.Level != item.Level || box.Row.Locator.ZoneAuthorId != item.Locator.ZoneAuthorId || box.Row.Locator.Dimension != item.Locator.Dimension
                || box.Row.Locator.Layer != item.Locator.Layer || box.Row.Locator.LocalIndex != item.Locator.LocalIndex)
            { Refuse(item.Id, "container-zone-mismatch"); return false; }
            destination = box.Storage;
            node = box.Node;
            align = FixedContainerPlacement.FreeSlot(destination, out var slotRefusal);
            if (align == null) { Refuse(item.Id, slotRefusal); return false; }
            position = align.position;
        }
        else align = Align(item.Id, position, rotation);
        var seed = FixedPlacement.SeedOf(item.Id);
        if (align == null) { Refuse(item.Id, "no-level-root"); return false; }
        ItemInLevel? placed;
        HeavyFogRepellerGlobalState? fogState = null;
        string cause;
        bool spawned;
        if (item.TargetDoor is { } targetDoor)
        {
            // The level's generator already made this door's one key; the row takes that pickup over and never
            // spawns a second one, whether the authored slot is a container's or a point on the floor.
            if (!_claimedKeys.Add(targetDoor))
            { placed = null; cause = "door-key-already-placed"; spawned = false; }
            else
            {
                spawned = FixedContainerPlacement.MoveKey(targetDoor, align, node!, out placed, out cause, destination == null);
                if (!spawned) _claimedKeys.Remove(targetDoor);
            }
        }
        else spawned = PlacedItemSpawn.TrySpawn(itemId, align, node!, item.Amount, seed, out placed, out fogState, out cause, destination != null);
        if (!spawned)
        {
            Refuse(item.Id, cause, "item=" + itemId + " slot=" + slot + " node=" + Node(node)
                + (item.TargetDoor is { } refusedDoor ? " door=" + refusedDoor : ""));
            if (destination == null) UnityEngine.Object.Destroy(align.gameObject);
            else if (item.TargetDoor == null && placed != null) UnityEngine.Object.Destroy(placed.gameObject);
            return false;
        }
        if (destination != null)
        {
            if (!FixedContainerPlacement.Add(destination, placed!, out cause))
            { Refuse(item.Id, cause); UnityEngine.Object.Destroy(placed!.gameObject); return false; }
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedContainerItem, "Native pickup added to selected container",
                RuntimeLogValue.Quote("id", item.Id), RuntimeLogValue.Quote("container", item.ContainerId!));
        }
        if (!RegisterIdentity(item, placed!, epoch)) return false;
        FixedItemRules.Apply(placed!.transform, item);
        if (fogState != null)
        {
            var key = FixedItemIdentity.Key(item.Locator.Dimension, item.Locator.Layer, item.Locator.LocalIndex, item.Id)!;
            FogRepellerObservation.Register(epoch, key, fogState);
            if (Session.IsHost && !item.StartsOff) fogState.AttemptInteract(new pCarryItemWithGlobalState_Interaction
            {
                type = ActivateInteraction,
                owner = eCarryItemWithGlobalStateOwner.StaticPosition,
                staticPosition = position
            });
        }
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedItem, "id=" + item.Id + " item=" + itemId.ToString(CultureInfo.InvariantCulture)
                + " slot=" + slot + " route=" + route + " pos=" + Show(position) + " yaw=" + Show(item.Yaw) + " seed="
                + seed.ToString(CultureInfo.InvariantCulture), RuntimeLogValue.Quote("id", item.Id), RuntimeLogValue.Of("route", route.ToString()));
        return true;
    }

    /// <summary>The pickup sync's signed Unity instance id maps to the author identity. Missing sync is a real
    /// initialization failure; a negative id is a normal native object identity, never an absent-object sentinel.</summary>
    private bool RegisterIdentity(FixedItem item, ItemInLevel placed, long epoch)
    {
        if (FixedItemIdentity.Key(item.Locator.Dimension, item.Locator.Layer, item.Locator.LocalIndex, item.Id) is not { } key)
        {
            Refuse(item.Id, "identity-token", "the id carries no address token");
            return false;
        }
        var sync = placed.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (sync == null || sync.WasCollected || sync.gameObject == null || sync.gameObject.WasCollected)
        {
            Refuse(item.Id, "pickup-sync-missing", "the native pickup was not initialized");
            return false;
        }
        FixedItemIdentity.Register(epoch, sync.gameObject.GetInstanceID(), key);
        _pickups[item.Id] = (placed, placed.Get_pItemData().itemID_gearCRC,
            item.ContainerId == null && item.TargetDoor == null && item.ObjectiveCargo == null);
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedItemIdentity, "id=" + item.Id + " key=" + key, RuntimeLogValue.Quote("id", item.Id));
        return true;
    }

    /// <summary>An authored address aliases the exact native network instance already owned by LevelObjects.
    /// The placement token alone is insufficient: its full registered address, world, level and live native
    /// replicator must still agree. No missing or stale item is replaced by another pickup.</summary>
    internal EntityReference? ItemReference(string address, LevelObjectModule levels)
    {
        var world = _worldEpoch();
        EntityReference? Unavailable(string reason)
        {
            MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FixedItemAddressUnavailable,
                "Fixed item address could not be resolved",
                RuntimeLogValue.Quote("address", address), RuntimeLogValue.Of("reason", reason),
                RuntimeLogValue.Of("world", world), RuntimeLogValue.Of("placedWorld", _placedEpoch),
                RuntimeLogValue.Of("readerWorld", levels.WorldEpoch));
            return null;
        }
        if (_disposed || _disposing || _placedEpoch != world || levels.WorldEpoch != world)
            return Unavailable("fixed-item-world-unavailable");
        if (_level() is not { } level || _placedLevel != level) return Unavailable("fixed-item-level-mismatch");
        var parts = address.Split('/');
        if (parts.Length != 5 || parts[0] != FixedItemIdentity.Category
            || !_pickups.TryGetValue(FixedItemIdentity.IdPrefix + parts[4], out var row))
            return Unavailable("fixed-item-placement-missing");
        var pickup = row.Pickup;
        if (pickup == null || pickup.WasCollected) return Unavailable("fixed-item-pickup-expired");
        var sync = pickup.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>();
        if (sync == null || sync.WasCollected || sync.gameObject == null || sync.gameObject.WasCollected)
            return Unavailable("fixed-item-sync-unavailable");
        if (FixedItemIdentity.KeyOf(world, sync.gameObject.GetInstanceID()) != address)
            return Unavailable("fixed-item-identity-mismatch");
        return LevelInstanceObservation.Item(pickup, levels) ?? Unavailable("fixed-item-native-instance-unavailable");
    }

    /// <summary>The native objective item behind one placed address, for a scan's required items. Only a big carry item
    /// is one; any other pickup, or an address this level did not place, answers null.</summary>
    internal iWardenObjectiveItem? WardenItem(string address)
    {
        var parts = address.Split('/');
        if (_disposed || _placedEpoch != _worldEpoch() || parts.Length != 5 || parts[0] != FixedItemIdentity.Category
            || !_pickups.TryGetValue(FixedItemIdentity.IdPrefix + parts[4], out var row)
            || row.Pickup == null || row.Pickup.WasCollected) return null;
        return row.Pickup.TryCast<iWardenObjectiveItem>();
    }

    /// <summary>One level's enemy rows. Only the host spawns them: the group's own spawn data is replicated to
    /// every other machine, and a client's second call would stand a second group in the same room.</summary>
    private int PlaceEnemies(IReadOnlyList<FixedEnemy> enemies, long epoch)
    {
        if (enemies.Count == 0 || !Session.IsHost) return 0;
        var placed = 0;
        foreach (var enemy in enemies) if (PlaceEnemy(enemy, epoch)) placed++;
        return placed;
    }

    /// <summary>
    /// One enemy row, through the one native placement path this package has
    /// (<see cref="FixedEnemyPlacement.TryPlace"/>): the point the row resolved to, the mode its intent names and the
    /// enemy's own type, with the group's own spawn entry replicating that data to every peer. The debug enemy id
    /// is what makes the group the one enemy the row authored instead of the block's own population.
    /// </summary>
    private bool PlaceEnemy(FixedEnemy enemy, long epoch)
    {
        if (RoomResolver.PlaceRow(epoch, enemy, out var roomRefusal) is not { } placed)
        {
            Refuse(enemy.Id, roomRefusal!);
            return false;
        }
        var (world, node) = placed;
        var position = Point(world.Position);
        var rotation = Facing(world.Rotation);
        if (!FixedEnemyPlacement.TryPlace(position, rotation, node, enemy.TypeId, Mode(enemy.Intent), out var spawned, out var refusal))
        {
            Refuse(enemy.Id, "spawn-refused", refusal);
            return false;
        }
        var reference = _enemyReference(spawned!);
        var life = new FixedEnemyLife(spawned!, reference?.WorldEpoch == epoch ? reference : null,
            spawned!.GlobalID, spawned.Pointer, spawned.gameObject?.GetInstanceID() ?? -1, spawned.EnemyDataID);
        _fixedEnemies[enemy.Id] = life;
        LogEnemyIdentity("spawn", enemy.Id, life, reference, reference == null ? "fixed-enemy-identity-unregistered"
            : reference.WorldEpoch != epoch ? "fixed-enemy-identity-stale-world" : "resolved");
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedEnemy, "id=" + enemy.Id + " type=" + enemy.TypeId.ToString(CultureInfo.InvariantCulture)
                + " intent=" + enemy.Intent + " pos=" + Show(position) + " yaw=" + Show(enemy.Yaw) + " node=" + Node(node),
                RuntimeLogValue.Quote("id", enemy.Id), RuntimeLogValue.Of("type", (long)enemy.TypeId),
                RuntimeLogValue.Of("nativeId", (long)spawned!.GlobalID),
                RuntimeLogValue.Quote("nativePointer", spawned.Pointer.ToString("x")),
                RuntimeLogValue.Of("referenceWorld", reference?.WorldEpoch ?? -1),
                RuntimeLogValue.Of("referenceLife", reference?.LifeEpoch ?? -1),
                RuntimeLogValue.Of("requestedMode", (long)Mode(enemy.Intent)),
                RuntimeLogValue.Of("actualMode", (long)spawned.AI.Mode),
                RuntimeLogValue.Of("behaviourState", (long)spawned.AI.m_behaviour.m_currentStateName),
                RuntimeLogValue.Of("locomotionState", (long)spawned.AI.m_locomotion.CurrentStateEnum),
                RuntimeLogValue.Of("scoutPathValid", spawned.AI.m_scoutPath?.IsValid ?? false),
                RuntimeLogValue.Of("scoutPathSteps", (long)(spawned.AI.m_scoutPath?.m_pathData.pathSteps ?? 0)));
        return true;

    }

    /// <summary>The current ForgeEnemy identity of one authored fixed placement. The author id chooses the native
    /// agent; ForgeEnemy remains the owner of world/life identity, so a dead, replaced or previous-world agent
    /// resolves to null instead of another nearby enemy.</summary>
    internal EntityReference? EnemyReference(string address)
        => EnemyReference(address, out _);

    internal EntityReference? EnemyReference(string address, out string? refusal)
    {
        refusal = "fixed-enemy-session-disposed";
        if (_disposed || _disposing) return null;
        refusal = "fixed-enemy-world-mismatch";
        if (_placedEpoch <= 0 || _worldEpoch() != _placedEpoch) return null;
        refusal = "fixed-enemy-level-mismatch";
        if (_level() != _placedLevel) return null;
        var id = FixedEnemyId(address);
        refusal = "fixed-enemy-address-invalid";
        if (id == null) return null;
        refusal = "fixed-enemy-not-placed";
        if (!_fixedEnemies.TryGetValue(id, out var row)) return null;
        refusal = "fixed-enemy-agent-destroyed";
        if (row.Agent == null || row.Agent.WasCollected)
        { LogEnemyIdentity("resolve", id, row, null, refusal); return null; }
        var current = _enemyReference(row.Agent);
        string Identity() => " nativeId=" + row.Agent.GlobalID.ToString(CultureInfo.InvariantCulture)
            + " nativePointer=" + row.Agent.Pointer.ToString("x")
            + " world=" + _placedEpoch.ToString(CultureInfo.InvariantCulture)
            + " captured=" + (row.Reference?.ToString() ?? "none") + " current=" + (current?.ToString() ?? "none");
        var reason = current == null ? "fixed-enemy-identity-unregistered"
            : current.WorldEpoch != _placedEpoch ? "fixed-enemy-identity-stale-world"
            : row.Reference != null && row.Reference != current ? "fixed-enemy-identity-replaced" : "resolved";
        LogEnemyIdentity("resolve", id, row, current, reason);
        if (reason != "resolved") { refusal = reason + Identity(); return null; }
        if (row.Reference == null) _fixedEnemies[id] = row with { Reference = current };
        refusal = null;
        return current;
    }

    /// <summary>The native enemy type the placement spawned, read from the live agent the author id chose. Null when the
    /// address names no placed enemy of this world or its agent is gone; nothing nearby ever stands in.</summary>
    internal uint? EnemyType(string address)
    {
        if (_disposed || _disposing || _placedEpoch <= 0 || _worldEpoch() != _placedEpoch || _level() != _placedLevel) return null;
        var id = FixedEnemyId(address);
        if (id == null || !_fixedEnemies.TryGetValue(id, out var row) || row.Agent == null || row.Agent.WasCollected) return null;
        return row.Agent.EnemyDataID;
    }

    /// <summary>Whether <paramref name="subject"/> is the enemy the authored address placed in this world: the
    /// identity captured at spawn (or, before one was captured, the one Enemy publishes for the agent now) must be
    /// exactly the subject, world and life included. A death or replacement ends the match; the address never
    /// re-points to another enemy. Unlike <see cref="EnemyReference(string)"/> this does not require the agent to
    /// still be live, because the event that retires it (a kill) is judged against the same subject.</summary>
    internal bool EnemyMatches(string address, EntityReference subject)
    {
        if (_disposed || _disposing || _placedEpoch <= 0 || _worldEpoch() != _placedEpoch || _level() != _placedLevel) return false;
        if (subject.WorldEpoch != _placedEpoch) return false;
        var id = FixedEnemyId(address);
        if (id == null || !_fixedEnemies.TryGetValue(id, out var row)) return false;
        if (row.Reference != null) return row.Reference == subject;
        if (row.Agent == null || row.Agent.WasCollected) return false;
        var current = _enemyReference(row.Agent);
        if (current == null || current != subject) return false;
        _fixedEnemies[id] = row with { Reference = current };
        return true;
    }

    /// <summary>Join the author row to Enemy's own life ledger. Repeated passive reads log only a changed
    /// result; checkpoint phase snapshots are recorded separately by the development adapter.</summary>
    private void LogEnemyIdentity(string phase, string id, FixedEnemyLife row, EntityReference? current, string reason)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        if (phase == "resolve" && _enemyIdentityReadbacks.TryGetValue(id, out var previous)
            && previous == (current, reason)) return;
        _enemyIdentityReadbacks[id] = (current, reason);
        MapLog.Record(RuntimeLogLevel.Info, "map.fixed-enemy-identity", "Fixed enemy author/native identity readback.",
            RuntimeLogValue.Quote("phase", phase), RuntimeLogValue.Quote("id", id),
            RuntimeLogValue.Quote("level", _placedLevel?.ToString() ?? ""),
            RuntimeLogValue.Of("placedWorld", _placedEpoch), RuntimeLogValue.Of("sessionWorld", _worldEpoch()),
            RuntimeLogValue.Of("nativeId", (long)row.NativeId), RuntimeLogValue.Quote("nativePointer", row.NativePointer.ToString("x")),
            RuntimeLogValue.Of("nativeInstance", (long)row.NativeInstance),
            RuntimeLogValue.Quote("capturedId", row.Reference?.Id ?? ""),
            RuntimeLogValue.Of("capturedWorld", row.Reference?.WorldEpoch ?? -1), RuntimeLogValue.Of("capturedLife", row.Reference?.LifeEpoch ?? -1),
            RuntimeLogValue.Quote("currentId", current?.Id ?? ""),
            RuntimeLogValue.Of("currentWorld", current?.WorldEpoch ?? -1), RuntimeLogValue.Of("currentLife", current?.LifeEpoch ?? -1),
            RuntimeLogValue.Quote("reason", reason));
    }

    /// <summary>The website's exact fixed-enemy address grammar: identity is the author token, while the three
    /// coordinate fields are fixed zeroes because moving the placement does not change what it names.</summary>
    internal static string? FixedEnemyId(string address)
    {
        const string prefix = "enemy/0/0/0/";
        if (address == null || !address.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var token = address.Substring(prefix.Length);
        if (token.Length is < 1 or > 58) return null;
        foreach (var value in token)
            if (!((value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9')
                || value == '.' || value == '_' || value == ':' || value == '-')) return null;
        return "enemy:" + token;
    }

    /// <summary>The mode one authored intent is: the game's own agent mode, which is the state the enemy's setup
    /// is handed and not a behaviour this layer reimplements.</summary>
    private static AgentMode Mode(FixedEnemyIntent intent) => intent switch
    {
        FixedEnemyIntent.Aggressive => AgentMode.Agressive,
        FixedEnemyIntent.Patrolling => AgentMode.Patrolling,
        FixedEnemyIntent.Scout => AgentMode.Scout,
        _ => AgentMode.Hibernate
    };

    /// <summary>
    /// One sack row: its room, its authored world pose and scale, the prefab the variant names, and the level's
    /// container. The static-spawn block supplies the prefab paths; its random-placement scale window does not
    /// constrain an authored fixed placement. A row that fails a reference or spawn is refused by name.
    ///
    /// The instance carries no state and nothing replicates it: the prefab's own `EnemyRespawnerVisual` plays a
    /// local sound and nothing else, so every machine builds its own copy from the same document. It is placed
    /// under the level's own container, so the level's teardown destroys it with the level.
    /// </summary>
    private bool PlaceSack(FixedSack sack, long epoch)
    {
        if (!RoomResolver.TryPlace(epoch, sack.Locator, out var room, out var roomRefusal))
        {
            Refuse(sack.Id, roomRefusal!);
            return false;
        }
        var world = sack.WorldPose(room!.Pose);
        var position = Point(world.Position);
        var rotation = Facing(world.Rotation);
        if (GameDataBlockBase<StaticSpawnDataBlock>.GetBlock(FixedPlacement.RespawnerSackBlockId) is not { } block)
        {
            Refuse(sack.Id, "sack-block-missing", "block=" + FixedPlacement.RespawnerSackBlockId.ToString(CultureInfo.InvariantCulture));
            return false;
        }
        if (SackPath(block, sack.Variant) is not { } path)
        {
            Refuse(sack.Id, "sack-prefab-missing", "variant=" + sack.Variant);
            return false;
        }
        var prefab = ForgeCore.GameAssets.Get<GameObject>(path);
        if (prefab == null)
        {
            Refuse(sack.Id, "sack-prefab-not-loaded", path);
            return false;
        }
        if (Container() is not { } container)
        {
            Refuse(sack.Id, "no-level-root");
            return false;
        }
        var instance = Object.Instantiate(prefab, container);
        if (instance == null || instance.WasCollected)
        {
            Refuse(sack.Id, "sack-spawn-refused", path);
            return false;
        }
        var placed = instance.transform;
        placed.position = position;
        placed.rotation = rotation;
        placed.localScale = Vector3.one * (float)sack.Scale;
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedSack, "id=" + sack.Id + " variant=" + sack.Variant + " scale=" + Show(sack.Scale)
                + " pos=" + Show(position) + " yaw=" + Show(sack.Yaw), RuntimeLogValue.Quote("id", sack.Id));
        return true;
    }

    /// <summary>The prefab one authored variant names, read off the block: the large sack is the block's own
    /// `Path`, and the small one is the `DistancePrefabPath` of the pair whose `DistanceMin` is smallest — the
    /// vanilla generator's own nearest-range prefab. The smallest entry is the one the block names, so an entry
    /// whose own path is empty is that variant naming no prefab and is refused; the next entry is never
    /// substituted for it. The block's own data is read fresh from the live block, so a missing table and a
    /// missing entry are the two nulls this answers null for.</summary>
    private static string? SackPath(StaticSpawnDataBlock block, FixedSackVariant variant)
    {
        if (variant == FixedSackVariant.Large)
            return string.IsNullOrEmpty(block.Path) ? null : block.Path;
        var pairs = block.PrefabDistancePairs;
        if (pairs == null) return null;
        StaticSpawnPrefabDistancePair? nearest = null;
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair == null) continue;
            if (nearest == null || pair.DistanceMin < nearest.DistanceMin) nearest = pair;
        }
        return nearest == null || string.IsNullOrEmpty(nearest.DistancePrefabPath) ? null : nearest.DistancePrefabPath;
    }

    /// <summary>Registers this level's authored landings through the native append API. Native points and
    /// spawn mode remain intact; the scoped native-query postfix supplies author slot order. Runtime timing,
    /// multiplayer and Forge death-respawn still require game validation.</summary>
    private void RegisterLandings(IReadOnlyList<FixedLanding> landings, long epoch)
    {
        if (landings.Count == 0) return;
        var order = new List<int>();
        var groups = new Dictionary<int, List<FixedLanding>>();
        foreach (var landing in landings)
        {
            if (!groups.TryGetValue(landing.Dimension, out var group))
            {
                group = new List<FixedLanding>();
                groups.Add(landing.Dimension, group);
                order.Add(landing.Dimension);
            }
            group.Add(landing);
        }
        foreach (var dimension in order) RegisterDimension(dimension, groups[dimension], epoch);
    }

    /// <summary>One dimension's authored landings, appended to that dimension's native spawn point list.</summary>
    private void RegisterDimension(int dimension, IReadOnlyList<FixedLanding> rows, long epoch)
    {
        var label = "dimension=" + dimension.ToString(CultureInfo.InvariantCulture);
        if (rows.Count > FixedPlacement.MaximumLandingSlots)
        {
            Refuse(rows[0].Id, "dimension-landing-limit", label + " rows=" + rows.Count + " of at most "
                + FixedPlacement.MaximumLandingSlots);
            return;
        }
        // Every pose is resolved before anything is written, so a dimension this level cannot fully answer for is
        // refused as a whole instead of leaving a partly registered authored set behind.
        var points = new List<(FixedLanding Row, Vector3 Position, Quaternion Rotation)>();
        foreach (var row in rows)
        {
            if (!RoomResolver.TryPlace(epoch, row.Locator, out var room, out var roomRefusal))
            {
                Refuse(row.Id, roomRefusal!, label);
                return;
            }
            var world = row.WorldPose(room!.Pose);
            points.Add((row, Point(world.Position), Facing(world.Rotation)));
        }
        // Qualified because `AIGraph` is imported for the course node and declares its own `Dimension` in some
        // builds: the type this resolves is the level's own dimension table, which is the one the index names.
        if (!LevelGeneration.Dimension.GetDimension((eDimensionIndex)dimension, out var target) || target == null)
        {
            Refuse(rows[0].Id, "dimension-missing", label);
            return;
        }
        var level = target.DimensionLevel;
        if (level == null || level.WasCollected)
        {
            Refuse(rows[0].Id, "dimension-level-missing", label);
            return;
        }
        var spawnPoints = level.m_spawnPoints;
        if (spawnPoints == null)
        {
            Refuse(rows[0].Id, "dimension-spawn-points-missing", label);
            return;
        }
        var before = spawnPoints.Count;
        var authored = new Transform[FixedPlacement.MaximumLandingSlots];
        try
        {
            for (var slot = 0; slot < authored.Length; slot++)
            {
                var point = points[slot % points.Count];
                var host = new GameObject("forge-landing-" + point.Row.Id + "-" + slot.ToString(CultureInfo.InvariantCulture));
                var transform = host.transform;
                authored[slot] = transform;
                transform.SetParent(level.transform, false);
                transform.position = point.Position;
                transform.rotation = point.Rotation;
            }
            _landingPoints.Register(epoch, level, authored);
        }
        catch
        {
            foreach (var transform in authored)
                if (transform is not null && !transform.WasCollected && transform != null && !_landingPoints.Owns(transform)) Object.Destroy(transform.gameObject);
            throw;
        }
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedLanding, "level=" + _placedLevel + " " + label + " spawnPoints=" + before + "->"
                + spawnPoints.Count + " rows=" + points.Count, RuntimeLogValue.Of("before", (long)before),
                RuntimeLogValue.Of("after", (long)spawnPoints.Count), RuntimeLogValue.Of("rows", (long)points.Count));
    }

    /// <summary>The key one authored door lock asks for, read off the door the address names: the level's own
    /// generator assigns zone keys, so the card that opens a door is the card whose block id that door's lock
    /// holds, and an author cannot write that number. A door this level cannot resolve and a door whose lock asks
    /// for no key are two different refusals.</summary>
    private static uint? GateKey(MapObjectReference address, out string? refusal)
    {
        refusal = null;
        var door = DoorObservation.ByAddress(address);
        if (door == null || door.WasCollected) { refusal = "door-unresolved"; return null; }
        var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        var key = locks == null || locks.WasCollected ? null : locks.m_gateKeyItemNeeded;
        if (key == null || key.WasCollected) { refusal = "no-gate-key"; return null; }
        return key.DataBlockID;
    }

    /// <summary>
    /// The transform one authored item is built on, as a child of the level's own container at the authored world
    /// pose. The native factory parents the item and any carried global state to this transform, so it has to
    /// outlive the call: it stays where it was put, under
    /// the level's own root, which is what destroys it — and the item with it — when the level ends.
    /// </summary>
    private Transform? Align(string id, Vector3 position, Quaternion rotation)
    {
        if (Container() is not { } container) return null;
        var align = new GameObject("forge-fixed-" + id);
        var transform = align.transform;
        transform.SetParent(container, false);
        transform.position = position;
        transform.rotation = rotation;
        return transform;
    }

    /// <summary>
    /// The one container every placed object of this level hangs under, kept at the world origin and parented to
    /// the level's own root transform. It is what gives the placements a lifetime: the level's teardown destroys
    /// its root, and everything under it goes with it, so a placement of a level that ended is never left standing
    /// in the next one. A level root that does not read answers null, and the caller refuses its rows rather than
    /// parenting them to the scene.
    /// </summary>
    private Transform? Container()
    {
        if (_container != null && !_container.WasCollected) return _container;
        var builder = LG_LevelBuilder.Current;
        var floor = builder == null || builder.WasCollected ? null : builder.m_currentFloor;
        var root = floor == null || floor.WasCollected ? null : floor.Root;
        if (root == null || root.WasCollected) return null;
        var host = new GameObject("ForgeFixedPlacement");
        var transform = host.transform;
        transform.SetParent(root, false);
        transform.position = Vector3.zero;
        transform.rotation = Quaternion.identity;
        _container = transform;
        return _container;
    }

    /// <summary>One row this session left out, named by its own id and the code that says why. It is reported
    /// once: placement runs once per level, so a row cannot repeat its own refusal inside one level.</summary>
    private void Refuse(string id, string code, string? detail = null)
    {
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.FixedPlacementRefused, detail ?? code,
            RuntimeLogValue.Quote("id", id), RuntimeLogValue.Quote("code", code), RuntimeLogValue.Quote("reason", detail ?? code));
        _report("fixed-placement-refused id=" + id + " code=" + code + (detail == null ? "" : ": " + detail));
    }

    private static Vector3 Point(IReadOnlyList<double> position)
        => new((float)position[0], (float)position[1], (float)position[2]);

    private static Quaternion Facing(IReadOnlyList<double> rotation)
        => new((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]);

    /// <summary>One course node as a diagnostic names it: the level's own coordinates of the node, which are the
    /// same three numbers a zone address is written from.</summary>
    internal static string Node(AIG_CourseNode? node)
        => node == null || node.WasCollected || node.m_zone == null || node.m_zone.m_layer is not { } layer ? "<none>"
            : ((int)node.m_zone.m_dimensionIndex).ToString(CultureInfo.InvariantCulture) + "/"
                + ((int)layer.m_type).ToString(CultureInfo.InvariantCulture) + "/"
                + ((int)node.m_zone.LocalIndex).ToString(CultureInfo.InvariantCulture);

    internal static string Show(Vector3 value)
        => "(" + Show((double)value.x) + "," + Show((double)value.y) + "," + Show((double)value.z) + ")";

    internal static string Show(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    internal bool TryLandingPoint(LG_Dimension level, PlayerAgent? player, out Transform? point)
    {
        point = null;
        // The native answer remains authoritative for players not in this session's current directory
        // (including early initialization and unknown bot ownership); never retarget a stale player by slot.
        if (_disposed || _disposing || player is null || player.WasCollected || player == null) return false;
        var session = MapHost.Session;
        bool current;
        if (session == null)
            current = _players.ReferenceOf(player) is { } reference
                && _players.AgentOf(reference)?.Pointer == player.Pointer;
        else if (!session.TryOptionalPlayerService(static (players, value) =>
                     players.ReferenceOf(value) is { } reference
                         && players.AgentOf(reference)?.Pointer == value.Pointer,
                     player, "dimension-landing-player", out current)) return false;
        if (!current) return false;
        return _landingPoints.TrySelect(_worldEpoch(), level, player, out point);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposing = true;
        StopTaskGeneration();
        try { _landingPoints.Clear(); }
        finally
        {
            // Landing withdrawal may need another attempt; it must not delay the existing generator cleanup.
            FixedItemRules.Clear();
            // A mission item still in hand when the level ends gives its interaction switches back here, on the native side.
            CarryItemRestrictions.Released();
            try { GeneratorPlacementBuild.Clear(); FixedFunctionPoints.Clear(); }
            finally { GeneratorObservation.Clear(); }
        }
        _disposed = true;
        _boxes.Clear();
        _cargoSupply.Clear();
        _claimedCargo.Clear();
        _fixedEnemies.Clear(); _enemyIdentityReadbacks.Clear();
        _pickups.Clear();
    }
}
