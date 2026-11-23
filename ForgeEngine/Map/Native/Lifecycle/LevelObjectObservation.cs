using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ChainedPuzzles;
using ForgeEngine.Framework;
using Il2CppInterop.Runtime;
using LevelGeneration;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the level-object facts: it reads one native subject, builds the key that subject is
/// addressed by, and hands the values to the game-independent publication half
/// (<c>ForgeMap.LevelObjectModule</c>). Nothing here knows a binding id, an event id or a transition, and
/// nothing in the publication half knows a game type — which is the seam that lets every publishing branch be
/// tested without a game.
///
/// The keys this half builds are `<category>/<dimension>/<layer>/<zone>/<serial>`: the zone the object stands
/// in, read through the same <see cref="ZoneIndex"/> the map-object addresses use so both halves agree on what
/// a zone's coordinates are, and the serial the level assigned the object. A subject whose zone or serial does
/// not read produces no key, and the fact is then not published at all rather than published under a guess.
///
/// The scan value row is resolved from the level's own instance table every time it is asked, not from a table
/// of subjects this half happened to observe: a scan no callback has run for yet is a scan the row can still
/// read, because the instance is in the manager's list from the moment the level built it
/// (<see cref="ChainedPuzzleLookup"/>). Its progress is the native reading the cores themselves carry, so no
/// value has to be remembered between calls either. The generators are not here: they are a map-object category
/// with their own reader and their own table (`GeneratorSource.cs`).
/// </summary>
internal static class LevelObjectObservation
{
    // ---- scan ---------------------------------------------------------------------------------------

    /// <summary>One scan progress reading. The uid is the instance's own, and the participants are the players
    /// the native callback itself named, resolved through the player half so this provider never invents a
    /// player identity. A group's scan reports its own number and share, and the whole group's mean share.</summary>
    internal static void ScanProgress(CP_Bioscan_Core core, float progress,
        Il2CppSystem.Collections.Generic.List<PlayerAgent>? players, LevelObjectModule module)
    {
        var cluster = core.m_owner?.TryCast<CP_Cluster_Core>();
        var instance = cluster != null ? cluster.m_owner?.TryCast<ChainedPuzzleInstance>() : core.m_owner?.TryCast<ChainedPuzzleInstance>();
        if (instance == null || instance.WasCollected || ScanIdentity(instance, module) is not { } identity) return;
        double whole = progress;
        if (cluster?.m_childCores is { Length: > 0 } children)
        {
            whole = 0;
            for (var i = 0; i != children.Length; i++)
            {
                var child = children[i]?.TryCast<CP_Bioscan_Core>();
                if (child != null && child.Pointer == core.Pointer) whole += progress;
                else if (child != null && !child.WasCollected) whole += child.State.progress;
            }
            whole /= children.Length;
        }
        int count = players?.Count ?? 0;
        var participants = new List<ForgeEngine.Framework.EntityReference>(count);
        for (int i = 0; i != count; i++)
            if (Actor(players![i]) is { } actor) participants.Add(actor);
        module.ScanProgress(identity.Uid, identity.ResourceId, whole, participants, cluster != null ? core.m_puzzleIndex + 1 : 1, progress);
    }

    /// <summary>One scan state change, read after the instance's own body returned so the reading is the state
    /// the instance is really in. `justSolved` is the transition the callback itself carries — the state before
    /// and after agree that the instance was not solved and now is — because that edge is what the alarm row is,
    /// and an instance that was already solved when the provider attached is not an alarm stopping now.
    /// `justActivated` and `leftActive` are the same callback's `status` becoming and leaving `Active`, which is the
    /// alarm start row's edge. Every scan/alarm fact uses the same canonical data-block scope.</summary>
    internal static void ScanStateChanged(ChainedPuzzleInstance instance, bool justSolved, bool justActivated,
        bool leftActive, LevelObjectModule module)
    {
        if (ScanIdentity(instance, module) is not { } identity) return;
        var scanIndex = ChainedPuzzleLookup.ActiveStepIndex(instance);
        // OnStateChange can precede the first core's Activate. Only the core may report an unresolved start;
        // a resolved chain readback and the core readback deduplicate on the same native step.
        module.ScanStateChanged(identity.Uid, identity.ResourceId, instance.IsActive && scanIndex.HasValue, instance.IsSolved, scanIndex);
        var live = ChainedPuzzleLookup.Liveness(instance);
        if (justSolved && IsAlarm(instance))
            module.AlarmStopped(identity.Uid, identity.ResourceId, instance, live, AlarmSource(instance, module.WorldEpoch));
        if (leftActive) module.AlarmSettled(identity.Uid);
        if (justActivated && module.AlarmStartedObserved && IsAlarm(instance))
            module.AlarmStarted(identity.Uid, identity.ResourceId, instance, live, AlarmSource(instance, module.WorldEpoch));
    }

    /// <summary>Only a door whose locks hold this exact native puzzle is a proven alarm source. The state callback
    /// carries no initiating actor; an unbound scan or an unrelated parent has no observable door source.</summary>
    internal static EntityReference? AlarmSource(ChainedPuzzleInstance instance, long worldEpoch)
    {
        if (instance.WasCollected) return null;
        var door = instance.m_parent?.GetComponentInParent(Il2CppType.Of<LG_SecurityDoor>())?.TryCast<LG_SecurityDoor>();
        if (door == null || door.WasCollected) return null;
        var locks = door.m_locks?.TryCast<LG_SecurityDoor_Locks>();
        if (locks == null || locks.WasCollected) return null;
        var held = locks.ChainedPuzzleToSolve;
        if (held == null || held.WasCollected || held.Pointer != instance.Pointer) return null;
        return DoorObservation.Decide(door).Address is { } address
            ? new EntityReference(EntityKinds.MapObject + ":" + address, worldEpoch, 1) : null;
    }

    private static (string Uid, string ResourceId)? ScanIdentity(ChainedPuzzleInstance instance, LevelObjectModule module)
    {
        var resourceId = ScanInstanceResolver.ResourceId(instance);
        var uid = resourceId == null ? null : ScanUid(instance);
        if (resourceId != null && uid != null) return (uid, resourceId);
        module.ScanStepRefused(resourceId == null ? "scan-resource-missing" : "scan-unavailable");
        return null;
    }

    /// <summary>Whether the game's own data block marks this chained puzzle as an alarm. The block is the author's
    /// definition — the same one the catalog's `chained-puzzle` resource names — and its `TriggerAlarmOnActivate`
    /// member is what the game's own alarm machinery reads, so this is the game's answer and not a guess from the
    /// instance's name.</summary>
    private static bool IsAlarm(ChainedPuzzleInstance instance)
    {
        try
        {
            var data = instance.Data;
            return data != null && !data.WasCollected && data.TriggerAlarmOnActivate;
        }
        catch (Exception containedError) { MapLog.Contained("level-object-observation.is-alarm", containedError); return false; }
    }

    internal static string? ScanUid(ChainedPuzzleInstance instance)
    {
        string uid = instance.m_puzzleUID ?? "";
        return uid.Length == 0 ? null : uid;
    }

    // ---- value row ----------------------------------------------------------------------------------

    /// <summary>One scan's current state and progress, for `forge.condition.predicate.scan`. The instance is
    /// resolved from the level's own instance table by the address the row names (<see cref="ChainedPuzzleLookup"/>),
    /// so a scan no callback has run for yet is still readable; the state is the instance's own two public
    /// readings — `IsSolved` first, because a solved instance stays active, and `disabled` when it is neither;
    /// and the fraction is the native progress the instance's own cores carry
    /// (<see cref="ScanInstanceResolver.Progress"/>), which is what makes the reading independent of whether this
    /// machine ever observed a scan callback.
    ///
    /// The manager's own table is per level and an instance of a world that is gone is a collected object, so an
    /// address of another world is simply not in this one: an address this world does not hold is refused by the
    /// code the row always answered with, `scan-unavailable`. An instance whose cores carry no readable fraction —
    /// a puzzle whose components were collected, or one this build found no progress member on — is refused as
    /// `scan-progress-unread` rather than answered zero, because a fraction nobody read is not a fraction of
    /// nothing.
    ///
    /// The members are the game's own: `ChainedPuzzleInstance.IsActive`/`IsSolved` (dump.cs 610148/610151) name
    /// the state, and each bioscan core's `CP_Bioscan_Core.State` — a `pBioscanState` whose `progress` field is
    /// the fraction the core's sync last wrote (dump.cs 608753, 609008) — is the one reading this row reports.</summary>
    internal static JsonElement ReadScanState(string scanUid)
    {
        if (ChainedPuzzleLookup.ById(scanUid) is not { } instance || instance.WasCollected)
            throw new RuntimeContractException("scan-unavailable", scanUid);
        if (ScanInstanceResolver.Progress(instance) is not { } progress)
            throw new RuntimeContractException("scan-progress-unread", scanUid);
        if (ScanInstanceResolver.PlayerCount(instance) is not { } count)
            throw new RuntimeContractException("scan-count-unread", scanUid);
        return RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["state"] = RuntimeJson.From(ScanStateName(instance)),
            ["progress"] = RuntimeJson.From(progress),
            ["count"] = RuntimeJson.From(count)
        });
    }

    /// <summary>One instance's own status, as the state names the value row declares. The three names are the
    /// game's own `eChainedPuzzleStatus` members: an instance that is neither active nor solved is `Disabled`,
    /// which is the status it holds before it is activated.</summary>
    internal static string ScanStateName(ChainedPuzzleInstance instance)
        => instance.IsSolved ? "solved" : instance.IsActive ? "active" : "disabled";

    // ---- container and item -------------------------------------------------------------------------

    /// <summary>One container state change. The status is the game's own enum ordinal; the reading is resolved
    /// to the row's declared name here, where the enum lives, and the publication half refuses a name its row
    /// does not declare.</summary>
    internal static void ContainerStateChanged(LG_ResourceContainer_Sync container,
        pResourceContainerItemState newState, LevelObjectModule module)
    {
        if (LevelInstanceObservation.Container(container, module, out var zone, out int serial) is not { } reference
            || zone == null) return;
        if (Session.IsHost) module.ContainerStateChanged(reference, zone, serial, ContainerStateName((int)newState.status));
    }

    internal static string ContainerStateName(int status) => status switch
    {
        0 => "not-setup",
        1 => "locked",
        2 => "closed",
        3 => "open",
        4 => "player-close",
        5 => "player-far",
        _ => ""
    };

    /// <summary>One level item changing hands. The direction is the game's own `ePickupItemStatus`, and the
    /// actor is the player the replicated state named.</summary>
    internal static void ItemStateChanged(LG_PickupItem_Sync item, pPickupItemState newState, bool isRecall,
        LevelObjectModule module)
    {
        // The address scopes the authored trigger; its entity is the same native instance a selector returns.
        var reference = LevelInstanceObservation.Item(item.item, module);
        if (newState.status != ePickupItemStatus.PlacedInLevel)
        {
            if (reference != null) module.RemoveInstancePresentation(reference);
            MapLabels.ItemLeftLevel(item);
        }
        if (isRecall || !Session.IsHost || reference == null || SerialOf(item.gameObject) is not { } serial) return;
        var key = ItemIdentityKey(serial, reference, module);
        if (newState.status is not (ePickupItemStatus.PlacedInLevel or ePickupItemStatus.PickedUp)) return;
        module.ItemStateChanged(key, reference, newState.status == ePickupItemStatus.PickedUp, ActorOf(newState.pPlayer));
    }

    /// <summary>The one item identity every item fact carries: the placement address when this package spawned the
    /// item, otherwise the native replicator key of the captured instance. Pickup, insertion and removal all read it
    /// here, so the three facts name the same item the same way.</summary>
    internal static string ItemIdentityKey(int serial, EntityReference reference, LevelObjectModule module)
        => FixedItemIdentity.KeyOf(module.WorldEpoch, serial) ?? reference.Id.Substring((EntityKinds.LevelObject + ":").Length);

    // ---- shared reads -------------------------------------------------------------------------------

    /// <summary>The zone a level object stands in: its build hierarchy when it sits under one, otherwise the zone of
    /// the course node the game spawned it in. A power generator or generator group parented to a bare marker
    /// outside any zone transform still belongs to its spawn node's zone; only an object with neither source has none.</summary>
    internal static LG_Zone? ZoneOf(UnityEngine.Component component)
    {
        var zone = component.GetComponentInParent<LG_Zone>();
        if (zone != null && !zone.WasCollected) return zone;
        var node = component.TryCast<LG_PowerGenerator_Core>()?.SpawnNode ?? component.TryCast<LG_PowerGeneratorCluster>()?.SpawnNode;
        return node == null || node.WasCollected ? null : node.m_zone;
    }

    /// <summary>One level object's key. The zone is read through <see cref="ZoneOf"/> and the one zone table every
    /// reader asks, and the serial is the object's signed Unity instance id.</summary>
    internal static string? KeyOf(UnityEngine.Component component, string category, int? serial)
    {
        if (serial == null || component == null || component.WasCollected) return null;
        var zone = ZoneOf(component);
        if (zone == null || zone.WasCollected) return null;
        if (ZoneIndex.Coordinates(zone) is not { } coordinates) return null;
        return category + "/" + coordinates.Dimension.ToString(CultureInfo.InvariantCulture) + "/"
            + coordinates.Layer.ToString(CultureInfo.InvariantCulture) + "/"
            + coordinates.Zone.ToString(CultureInfo.InvariantCulture) + "/"
            + serial.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The local Unity lookup key for fixed item placements and interaction hosts. It is never a shared
    /// item/cabinet identity; captured network instances use LevelInstanceObservation.</summary>
    internal static int? SerialOf(UnityEngine.GameObject? gameObject)
        => gameObject == null || gameObject.WasCollected ? null : gameObject.GetInstanceID();

    /// <summary>The player one replicated state names, read through the game's own struct accessor: the struct
    /// carries the replicated player, and the player directory is what turns it into the entity a port
    /// carries. A state that names none — a level-build change or a drop-in replay — yields nothing rather than
    /// the last player that touched the object.</summary>
    internal static ForgeEngine.Framework.EntityReference? ActorOf(SNetStructs.pPlayer player)
    {
        try
        {
            if (!player.TryGetPlayer(out var replicated) || replicated == null || replicated.Pointer == IntPtr.Zero
                || MapHost.Session is not { } session) return null;
            return session.TryOptionalPlayerService(static (s, value) => s.ReferenceOf(value), replicated,
                    "level-object-actor", out EntityReference? reference) ? reference : null;
        }
        catch (Exception containedError) { MapLog.Contained("level-object-observation.actor-of", containedError); return null; }
    }

    /// <summary>The entity reference of one player, resolved through the player directory so
    /// this provider never invents a player identity. A player the directory does not track is absent.</summary>
    internal static ForgeEngine.Framework.EntityReference? Actor(PlayerAgent? agent)
    {
        if (agent == null || MapHost.Session is not { } session) return null;
        return session.TryOptionalPlayerService(static (s, value) => s.ReferenceOf(value), agent,
            "level-object-player", out EntityReference? reference) ? reference : null;
    }
}
