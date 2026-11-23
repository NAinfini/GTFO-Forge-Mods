using System;
using System.Collections.Generic;
using AIGraph;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The authored bulkhead keys of one level, moved to the points their rows name once the level's own generation
/// finished — the same stage every other fixed placement of this module runs at, and the stage at which the key the
/// level's generator built exists with its pickup, its node and the zone it stands in.
///
/// This layer never builds a key. The level's own `BulkheadKeyPlacements` generate the keys, and this half moves the
/// one that already generated in the layer a row names: the authored point's zone is the zone the site wrote into
/// that key group's candidate areas, so the key is generated in the room the point stands in and only its pose is
/// left to the author.
///
/// Which key a row claims is the level's own answer, not a search. Every generated key registers itself in the one
/// registry the bulkhead key objective keeps, together with the zone it generated in, and a row claims the first
/// key of its layer that no earlier row of the same level claimed — in the registry's own order. A row whose layer
/// carries no key, or whose layer's keys an earlier row already took, is refused by name rather than matched to a
/// key the author did not name. No hierarchy search is made, and none is needed to find the key: the level's own
/// key placement puts it in a resource container's keycard slot, and that container is reached from the pickup's own
/// hierarchy at move time.
///
/// A key that generated inside a container is moved the way the game itself takes an item out of one: the
/// container's account is cleared while the pickup is still its child, the pickup is reparented to the resolved
/// node's own object, its interaction is switched back on, the pose and both node facts are written, and the
/// pickup's replicated placement state is seeded with the authored point. Without that last step a recall, a late
/// joiner or any state applying would restore the container slot the generator placed the key in.
///
/// Switching the interaction back on is not optional. `LG_ResourceContainer_Storage.PlaceKeyCard` ends by calling
/// `DisableInteraction` on the pickup, and the only thing that switches a container's pickups back on is the
/// container's own interaction table — the very table the account step takes the key out of, so nothing would ever
/// switch it on again. The move therefore switches it on itself, through the member the game itself uses for an item
/// that stands in the level: `Interact_Base.SetActive(bool)`, which is what the item state handling reaches through
/// `iInteractPickup.SetActive` when a placed item's state is applied.
///
/// The position is not replicated state, so every machine moves its own copy from the same document, exactly as the
/// controller half does. The diagnostic is written before and after the move, because that pair is what an in-game
/// check reads.
///
/// **This route is not confirmed in game.** Whether the container's own account, the parent the key is left under,
/// the switched-on interaction and the seeded state really behave as the move expects is a question only a game
/// session answers; it is listed with the other unproven points in `evidence/fixed-placement-natives.json`.
/// </summary>
internal static class BulkheadKeyPlacement
{
    /// <summary>The factory placement state's own custom-data ammo. `KeyItemPickup_Core.SetPlacementFromFactory`
    /// (RVA 0x12A4B30) zeroes the whole state it seeds and then writes 1.0 into the state's `custom.ammo` field, so
    /// a state this half seeds carries the same figure the generator's own placement left in the replica.</summary>
    private const float FactoryCustomAmmo = 1f;

    /// <summary>Every authored key row of this level, placed one by one in the document's own order, which is also
    /// the order the rows claim their keys in. A row that fails any step is refused by name and its key stays exactly
    /// where the generator put it.</summary>
    internal static void Place(long epoch, IReadOnlyList<FixedBulkheadKey> rows, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(report);
        if (rows.Count == 0) return;
        var keys = Registry();
        // One candidate per registered key: the layer of the zone the generator placed it in is the whole of "which
        // layer did this key generate on", because a row can name no other layer.
        var candidates = new List<BulkheadKeyCandidate>();
        for (var index = 0; index != keys.Count; index++)
        {
            if (LayerOf(keys[index]) is not { } layer) continue;
            var zone = keys[index].SpawnZone!; // LayerOf answered, so the spawn zone reads
            candidates.Add(new BulkheadKeyCandidate(index, layer, (int)zone.m_dimensionIndex, (int)zone.LocalIndex));
        }
        var claimed = new HashSet<int>();
        foreach (var row in rows) PlaceRow(row, epoch, keys, candidates, claimed, report);
    }

    /// <summary>
    /// One row: its room, its world pose, its course node, the first key of its layer no earlier row claimed, and
    /// then the move. The room, the pose and the node are resolved before a key is looked up, because a row whose
    /// point this level cannot answer for must not claim a key the rows after it could still use. The move itself
    /// reads and checks every member it needs before it writes anything, so a key this half cannot move is refused
    /// while it still stands exactly where the generator put it, and only then does the row claim it.
    /// </summary>
    private static void PlaceRow(FixedBulkheadKey row, long epoch, IReadOnlyList<GateKeyItem> keys,
        IReadOnlyList<BulkheadKeyCandidate> candidates, HashSet<int> claimed, Action<string> report)
    {
        var label = "layer=" + FixedPlacement.WireKey(row.Layer);
        if (RoomResolver.PlaceRow(epoch, row, out var refusal) is not { } placed)
        {
            Refuse(report, row.Id, refusal!, label);
            return;
        }
        var (world, node) = placed;
        // The item joins the resolved node's own item list through the public `CourseNode` setter below, and that
        // setter writes into the list the node carries. A node the level built without one is refused before the
        // move rather than reached with a write that could not complete.
        if (node.m_itemsInNode == null)
        {
            Refuse(report, row.Id, "fixed-bulkhead-key-node-list-missing", label + " node=" + FixedPlacementSession.Node(node));
            return;
        }
        var open = new List<BulkheadKeyCandidate>();
        foreach (var candidate in candidates) if (!claimed.Contains(candidate.Index)) open.Add(candidate);
        var index = FixedPlacement.MatchBulkheadKey(open, row.Layer, row.Locator.Dimension, row.Locator.LocalIndex);
        if (index < 0)
        {
            // A key of this layer an earlier row of this level already claimed is the same key this row names, so
            // the two are told apart by the detail rather than by a code: both are a row that left the keys alone.
            var alreadyClaimed = ClaimedByEarlierRow(candidates, claimed, row.Layer);
            var detail = (alreadyClaimed
                    ? "an earlier row of this level already claimed every key generated on it"
                    : "none of this level's " + keys.Count + " key(s) generated on it")
                + "; the key stays where the generator placed it";
            Refuse(report, row.Id, "fixed-bulkhead-key-unmatched", label + ": " + detail);
            return;
        }
        var key = keys[index];
        // The claim is made only once the row is certain to have moved the key, so a refused row leaves every key
        // of its layer open to the rows after it.
        if (!Move(row, index, key, world, node, report)) return;
        claimed.Add(index);
    }

    /// <summary>
    /// One claimed key and the authored point it goes to. Every member the move needs is read and checked before
    /// anything is written, so a key this half cannot move is refused while it still stands inside the container the
    /// generator put it in; a true answer says the key was moved and the row may claim it.
    ///
    /// The move is the game's own exit from a container, in the game's own order: the container the pickup still
    /// hangs in is read through the hierarchy first, its account is cleared while the pickup is still its child,
    /// and only then is the pickup reparented to the resolved node's own object — the parent the game's own item
    /// state handling gives an item that stands in the level rather than in a container. The pose and both node
    /// facts follow, and the pickup's replicated placement state is seeded last with the authored point, in the same
    /// shape the factory placement wrote into it.
    /// </summary>
    internal static bool Move(FixedBulkheadKey row, int index, GateKeyItem key, RoomPose world, AIG_CourseNode node,
        Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(report);
        var label = "layer=" + FixedPlacement.WireKey(row.Layer);
        var pickup = key.keyPickupCore;
        if (pickup == null || pickup.WasCollected)
        {
            Refuse(report, row.Id, "fixed-bulkhead-key-pickup-missing", label + " key=" + index);
            return false;
        }
        var terminal = pickup.m_terminalItem;
        if (terminal == null || terminal.WasCollected)
        {
            Refuse(report, row.Id, "fixed-bulkhead-key-terminal-missing", label + " key=" + index);
            return false;
        }
        // The sync component is what carries the key's replicated placement, so a pickup whose own sync component
        // is not the level item sync is refused before anything moves: a key moved without its state seeded is a
        // key the next state applying would put back in the container.
        var sync = pickup.m_syncComp == null || pickup.m_syncComp.WasCollected
            ? null : pickup.m_syncComp.TryCast<LG_PickupItem_Sync>();
        if (sync == null || sync.WasCollected)
        {
            Refuse(report, row.Id, "fixed-bulkhead-key-sync-missing", label + " key=" + index);
            return false;
        }
        var transform = pickup.transform;
        if (transform == null || transform.WasCollected)
        {
            Refuse(report, row.Id, "fixed-bulkhead-key-transform-missing", label + " key=" + index);
            return false;
        }
        // The interaction a player picks the key up with. KeyItemPickup_Core.Setup resolves its serialized
        // m_interactComp once and stores the iInteractPickup in m_interact; OnSyncStateChange uses that same slot
        // when native placement switches the pickup back on. The component is not required to live on the core's
        // own GameObject, so a fresh GetComponent on that object can miss a valid interaction.
        //
        // The container's own placement switched this slot off and only the container's interaction table switches
        // its entries back on, so read and validate the native slot before anything is written and switch it on below.
        var interact = pickup.m_interact;
        if (interact == null || interact.WasCollected)
        {
            Refuse(report, row.Id, "fixed-bulkhead-key-interact-missing", label + " key=" + index);
            return false;
        }
        var host = node.gameObject;
        if (host == null || host.WasCollected || host.transform == null || host.transform.WasCollected)
        {
            Refuse(report, row.Id, "fixed-bulkhead-key-node-object-missing",
                label + " key=" + index + " node=" + FixedPlacementSession.Node(node));
            return false;
        }
        // Read before the pickup leaves the hierarchy: this climb is the container the generator placed the key in,
        // and the game's own item-state handling reads it the same way. A key that stands in no container answers
        // null and the account step below is skipped.
        var container = pickup.GetComponentInParent<LG_ResourceContainer_Storage>();
        if (container != null && container.WasCollected) container = null;
        var position = new Vector3((float)world.Position[0], (float)world.Position[1], (float)world.Position[2]);
        var rotation = new Quaternion((float)world.Rotation[0], (float)world.Rotation[1],
            (float)world.Rotation[2], (float)world.Rotation[3]);
        var courseNode = default(pCourseNode);
        courseNode.Set(node);
        var state = new pPickupItemState
        {
            status = ePickupItemStatus.PlacedInLevel,
            placement = new pPickupPlacement
            {
                position = position,
                rotation = rotation,
                node = courseNode,
                droppedOnFloor = false
            },
            custom = new Player.pItemData_Custom { ammo = FactoryCustomAmmo }
        };
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedBulkheadKey, "id=" + row.Id + " phase=before " + label + " key=" + index
                + " pos=" + FixedPlacementSession.Show(transform.position) + " node=" + FixedPlacementSession.Node(NodeOf(terminal)),
                RuntimeLogValue.Quote("id", row.Id), RuntimeLogValue.Of("phase", "before"));
        // The container's own account first, while the pickup is still its child: the interaction it registered is
        // taken out of the container's list and the container's item count drops by the one this key was. A key
        // whose account is left behind keeps a slot's interaction registered and the container reading as unemptied.
        if (container != null)
        {
            container.OnItemRemoved(pickup.gameObject);
            container.AddNumberOfItemsToContainer(-1);
        }
        // The pickup the container held becomes a child of the level's own node object, which is the parent the
        // game gives an item that stands in the level: it travels with the level's teardown exactly as the container
        // parent did, and it no longer moves with the container's own animation or destruction.
        transform.SetParent(host.transform, true);
        // The container's placement switched this interaction off, and the container's table — which the account step
        // above took the key out of — is the only other thing that would switch it back on. Switching it on here is
        // the state an item standing in the level has, and it is the member the game's own item state handling uses
        // when it places an item: waiting for the next state application would leave the key unpickable until one
        // happens to arrive, and after the level's own generation there may be none.
        interact.SetActive(true);
        transform.position = position;
        transform.rotation = rotation;
        // The pickup's own course node is the item's position as the level's systems read it: the game's public
        // `ItemInLevel.CourseNode` setter takes the item out of the node list it was in and adds it to the resolved
        // node's own, which is what culling and the node's item bookkeeping read. Writing the transform alone would
        // leave the key registered under the node the generator placed it at.
        pickup.CourseNode = node;
        // The terminal item's node is the second, separate position fact: its public `SpawnNode` setter is the entry
        // the game itself writes it through, and besides storing the node it rebuilds the floor item location from
        // the new node's own zone. Moving the transform alone would leave both naming the generator's room.
        terminal.SpawnNode = node;
        // The third and last position fact: the state every peer and every recall reads the item's placement from.
        // Seeding it through the sync component is the step the factory placement took when it put the key in the
        // container slot, so a recall, a late joiner or any later state applying finds the authored point and not
        // the slot the generator used.
        sync.SetStateFromFactory(state);
        if (MapLog.Logs(RuntimeLogLevel.Info))
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.FixedBulkheadKey, "id=" + row.Id + " phase=after " + label + " key=" + index
            + " pos=" + FixedPlacementSession.Show(transform.position) + " node=" + FixedPlacementSession.Node(NodeOf(terminal))
            + " container=" + (container == null ? "none" : "cleared")
            + " parent=" + ParentOf(transform)
            + " interaction=" + (interact.IsActive ? "active" : "inactive")
            + " state.pos=" + FixedPlacementSession.Show(state.placement.position)
            + " state.node=" + FixedPlacementSession.Node(node),
                RuntimeLogValue.Quote("id", row.Id), RuntimeLogValue.Of("phase", "after"));
        return true;
    }

    /// <summary>Whether a key of one layer has already been moved by an earlier row of this level. It is what tells
    /// a row whose layer's keys an earlier row already took apart from a row whose layer the level generated no key
    /// on: the first is a claim already made, the second a key that is not there.</summary>
    private static bool ClaimedByEarlierRow(IReadOnlyList<BulkheadKeyCandidate> candidates,
        HashSet<int> claimed, FixedBulkheadKeyLayer layer)
    {
        foreach (var candidate in candidates)
            if (candidate.Layer == layer && claimed.Contains(candidate.Index)) return true;
        return false;
    }

    /// <summary>Every bulkhead key this level registered, in the order its own registry lists them. The game
    /// registers each key the level's own key placement generated, so this table is the level's own answer to which
    /// keys exist and no hierarchy search is needed; a key the level already tore down is not one a row may claim.
    /// The indexes are the order this list holds, which is what every diagnostic of this half names a key by.</summary>
    private static List<GateKeyItem> Registry()
    {
        var keys = new List<GateKeyItem>();
        var registered = ProgressionObjective_BulkheadKey.m_allBulkheadKeysInLevel;
        if (registered == null) return keys;
        for (var index = 0; index != registered.Count; index++)
        {
            var key = registered[index];
            if (key != null && !key.WasCollected) keys.Add(key);
        }
        return keys;
    }

    /// <summary>The layer one registered key generated on, read off the key's own spawn zone — the zone the level's
    /// key placement wrote into it right before it registered the key, so it is the key's own answer to where it
    /// stands. Null answers a key whose zone or layer the build cannot read: such a key is no candidate, and a row
    /// its layer would have matched is refused as unmatched instead of moved onto a key of another layer.</summary>
    private static FixedBulkheadKeyLayer? LayerOf(GateKeyItem key)
    {
        var zone = key.SpawnZone;
        if (zone == null || zone.WasCollected) return null;
        var layer = zone.m_layer;
        if (layer == null || layer.WasCollected) return null;
        return layer.m_type switch
        {
            LG_LayerType.MainLayer => FixedBulkheadKeyLayer.Main,
            LG_LayerType.SecondaryLayer => FixedBulkheadKeyLayer.Secondary,
            LG_LayerType.ThirdLayer => FixedBulkheadKeyLayer.Third,
            _ => null
        };
    }

    /// <summary>The course node a key's own terminal item reports, or null when the item is gone. It is the
    /// diagnostic's read of the node: the value the move writes last, so the read-back after it is what proves the
    /// node followed the transform.</summary>
    private static AIG_CourseNode? NodeOf(iTerminalItem terminal)
        => terminal == null || terminal.WasCollected ? null : terminal.SpawnNode;

    /// <summary>The parent a moved pickup hangs under now, as the after line's own read-back of the reparent: the
    /// object it belongs to and its world position, or `<none>` for a pickup the move left at the scene root.</summary>
    private static string ParentOf(Transform transform)
    {
        var parent = transform.parent;
        if (parent == null || parent.WasCollected) return "<none>";
        var owner = parent.gameObject;
        return (owner == null || owner.WasCollected || string.IsNullOrEmpty(owner.name) ? "<object>" : owner.name)
            + "@" + FixedPlacementSession.Show(parent.position);
    }

    /// <summary>One row this half left out, named by its own id and the code that says why. The key it named is
    /// untouched: a row that did not move is a key still at the position the generator gave it.</summary>
    private static void Refuse(Action<string> report, string id, string code, string detail)
        => report("fixed-placement-refused id=" + id + " code=" + code + ": " + detail);
}
