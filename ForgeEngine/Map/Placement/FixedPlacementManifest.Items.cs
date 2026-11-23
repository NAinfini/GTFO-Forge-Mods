using ForgeEngine.Framework;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeMap;

// Placement document parsing for Items. Shared envelope rules remain in FixedPlacementManifest.cs.
internal static partial class FixedPlacementManifest
{
    private const string ItemFileName = "fixed-items.forge";

    /// <summary>The item document's one file name, which is what a package scan looks for.</summary>
    public static string ItemFileNameOnly => ItemFileName;

    /// <summary>Reads one item document. A false answer is the document's own refusal — unreadable JSON, another
    /// schema version, a root that is not this shape — and a true answer carries every row plus every row-level
    /// refusal, so a caller reports the refusals and places the rest.</summary>
    public static bool TryParseItems(string text, out FixedDocument<FixedItem>? document, out string? code, out string? reason)
    {
        document = null;
        using var parsed = Read(text, ItemRootCodes, "items", out code, out reason);
        if (parsed == null) return false;
        var items = new List<FixedItem>();
        var refusals = new List<FixedPlacementRefusal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in parsed.RootElement.GetProperty("items").EnumerateArray())
        {
            index++;
            if (!TryItem(row, out var item, out var rowCode, out var rowReason))
            {
                refusals.Add(new FixedPlacementRefusal(rowCode!, Row(index, rowReason!), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (!TryPlacementRules(row, item!, out rowReason))
            {
                refusals.Add(new FixedPlacementRefusal("fixed-item-field", Row(index, rowReason!), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (!seen.Add(item!.Id))
            {
                refusals.Add(new FixedPlacementRefusal("fixed-item-duplicate",
                    Row(index, "Item id `" + item.Id + "` is claimed more than once; the later row is left out."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (item.TaskObject is { } role && !roles.Add(item.Level + "/" + role.Key(item.Locator)))
            {
                refusals.Add(new FixedPlacementRefusal("fixed-object-role-duplicate", Row(index, "This native task object already has a point."), FixedPlacementRefusal.IdOf(row)));
                continue;
            }
            if (item.ObjectiveCargo is { } cargo && !roles.Add(item.Level + "/cargo/" + cargo.Layer + "/" + cargo.ChainIndex + "/" + cargo.Index))
            { refusals.Add(new FixedPlacementRefusal("fixed-cargo-duplicate", Row(index, "Native battery already claimed."), FixedPlacementRefusal.IdOf(row))); continue; }
            items.Add(item);
        }
        var accepted = new List<FixedItem>();
        foreach (var item in items)
        {
            if (item.ContainerId != null)
            {
                var container = items.Find(candidate => candidate.Id == item.ContainerId && candidate.ContainerType != null);
                if (container == null)
                { refusals.Add(new FixedPlacementRefusal("container-missing", "Selected fixed container is not in this document: " + item.Id, item.Id)); continue; }
                if (container.Level != item.Level || container.Locator.ZoneAuthorId != item.Locator.ZoneAuthorId || container.Locator.Dimension != item.Locator.Dimension
                    || container.Locator.Layer != item.Locator.Layer || container.Locator.LocalIndex != item.Locator.LocalIndex)
                { refusals.Add(new FixedPlacementRefusal("container-zone-mismatch", "Item and selected container are in different zones: " + item.Id, item.Id)); continue; }
            }
            accepted.Add(item);
        }
        document = new FixedDocument<FixedItem>(Array.AsReadOnly(accepted.ToArray()), Array.AsReadOnly(refusals.ToArray()));
        return true;
    }

    /// <summary>The placement rules a row may carry: `hideBody`, and `acceptItems` and `group` on a row whose object has a
    /// receptacle (a generator).</summary>
    private static bool TryPlacementRules(JsonElement row, FixedItem item, out string? reason)
    {
        reason = null;
        if (row.TryGetProperty("group", out _))
        {
            if (!FixedItem.HasReceptacle(item))
            { reason = "group belongs only to a generator row."; return false; }
            if (!MapObjectGroup.TryRead(row, out var group))
            { reason = MapObjectGroup.Rule; return false; }
            item.Group = group;
        }
        var hasAccepts = row.TryGetProperty("acceptItems", out var accepts);
        var hasHide = row.TryGetProperty("hideBody", out var hide);
        if (!hasAccepts && !hasHide) return true;
        if (hasAccepts && !FixedItem.HasReceptacle(item))
        { reason = "acceptItems belongs only to a row whose object takes items (a generator)."; return false; }
        if (hasHide)
        {
            if (hide.ValueKind != JsonValueKind.True) { reason = "hideBody is written only as true."; return false; }
            item.HideBody = true;
        }
        if (!hasAccepts) return true;
        if (accepts.ValueKind != JsonValueKind.Array || accepts.GetArrayLength() is 0 or > 32)
        { reason = "acceptItems lists one to 32 item ids."; return false; }
        var ids = new List<uint>(accepts.GetArrayLength());
        foreach (var id in accepts.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var value) || value == 0)
            { reason = "acceptItems lists item ids."; return false; }
            ids.Add(value);
        }
        item.AcceptItems = ids.AsReadOnly();
        return true;
    }

    private static bool TryItem(JsonElement row, out FixedItem? item, out string? code, out string? reason)
    {
        item = null;
        code = "fixed-item-row";
        reason = null;
        if (row.ValueKind != JsonValueKind.Object) { reason = "An item row is an object."; return false; }
        if (!DocumentReading.Known(row, out var unknown, "id", "level", "locator", "p_u", "yaw_u", "itemId", "amount", "targetDoor", "kind", "role", "containerType", "containerId", "taskItemsOnly", "objectiveCargo", "lock", "startsOff", "acceptItems", "hideBody", "group"))
        { code = "fixed-item-field"; reason = "Unknown item field `" + unknown + "`."; return false; }
        if (!DocumentReading.Text(row, "id", FixedPlacement.MaximumIdChars, out var id) || !Prefixed(id!, "item:"))
        { code = "fixed-item-id"; reason = "An item needs an id of at most " + FixedPlacement.MaximumIdChars + " characters, written `item:<id>`."; return false; }
        if (!DocumentReading.Text(row, "level", out var levelText) || MapLevelReference.TryParse(levelText) is not { } level)
        { code = "fixed-item-level"; reason = "An item needs a level reference of the form `<rundown>:<tier>:<index>`."; return false; }
        if (!Locator(row, out var locator, out reason)) { code = "fixed-item-room"; return false; }
        if (!DocumentReading.Numbers(row, "p_u", 3, -FixedPlacement.MaximumCoordinate, FixedPlacement.MaximumCoordinate, out var position))
        { code = "fixed-item-position"; reason = "A position carries three finite metre coordinates in the room's prefab-root frame."; return false; }
        if (!DocumentReading.Number(row, "yaw_u", -FixedPlacement.MaximumYawDegrees, FixedPlacement.MaximumYawDegrees, out var yaw))
        { code = "fixed-item-yaw"; reason = "A yaw is a finite angle in degrees about +Y."; return false; }
        if (row.TryGetProperty("containerType", out _))
        {
            if (row.TryGetProperty("startsOff", out _))
            { code = "fixed-item-start-state"; reason = "startsOff belongs only to a Fog Repeller item row."; return false; }
            FixedContainerLock? containerLock = null;
            if (row.TryGetProperty("objectiveCargo", out _))
            { code = "fixed-cargo-role"; reason = "Cargo belongs only to a battery item."; return false; }
            if (row.TryGetProperty("lock", out var lockRow))
            {
                if (lockRow.ValueKind != JsonValueKind.Object
                    || !DocumentReading.Known(lockRow, out _, "lockedFromStart", "weakLockType")
                    || !lockRow.TryGetProperty("lockedFromStart", out var locked)
                    || locked.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !DocumentReading.Text(lockRow, "weakLockType", out var weakType)
                    || weakType is not ("None" or "Melee" or "Hackable")
                    || locked.GetBoolean() != (weakType != "None"))
                { code = "fixed-container-lock"; reason = "Lock must use consistent vanilla None/Melee/Hackable setup parameters."; return false; }
                containerLock = new FixedContainerLock(locked.GetBoolean(), Enum.Parse<FixedWeakLockType>(weakType!));
            }
            var tasks = false;
            if (row.TryGetProperty("taskItemsOnly", out var flag))
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { code = "fixed-task-container-flag"; reason = "taskItemsOnly is a boolean on a container row."; return false; }
                tasks = flag.GetBoolean();
            }
            if (!DocumentReading.Text(row, "kind", out var kind) || kind != "container"
                || !DocumentReading.Text(row, "containerType", out var type) || type is not ("box" or "locker")
                || row.TryGetProperty("containerId", out _) || row.TryGetProperty("itemId", out _)
                || row.TryGetProperty("targetDoor", out _) || row.TryGetProperty("role", out _)
                || !DocumentReading.Number(row, "amount", 0, 0, out _))
            { code = "fixed-container-type"; reason = "A container names box or locker, carries zero amount and no item target."; return false; }
            item = new FixedItem(id!, level, locator!, position!, yaw, null, 0, null, containerType: type, taskItemsOnly: tasks, containerLock: containerLock);
            return true;
        }
        if (row.TryGetProperty("lock", out _))
        { code = "fixed-container-lock"; reason = "Lock belongs only to a container."; return false; }
        if (row.TryGetProperty("taskItemsOnly", out _))
        { code = "fixed-task-container-flag"; reason = "taskItemsOnly belongs only to a native container row."; return false; }
        var startsOff = false;
        if (row.TryGetProperty("startsOff", out var offFlag))
        {
            if (offFlag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || row.TryGetProperty("kind", out _) || row.TryGetProperty("role", out _)
                || !RuntimeJson.TryInteger(row, "itemId", 1, uint.MaxValue, out var offItem) || offItem != FixedPlacement.FogRepellerItemId)
            { code = "fixed-item-start-state"; reason = "startsOff is a boolean on a Fog Repeller (" + FixedPlacement.FogRepellerItemId + ") item row."; return false; }
            startsOff = offFlag.GetBoolean();
        }
        string? containerId = null;
        if (row.TryGetProperty("containerId", out _)
            && (!DocumentReading.Text(row, "containerId", FixedPlacement.MaximumIdChars, out containerId) || !Prefixed(containerId!, "item:")))
        { code = "fixed-container-id"; reason = "A container reference names one fixed container item id."; return false; }
        if (row.TryGetProperty("kind", out _) || row.TryGetProperty("role", out _))
        {
            if (containerId != null || row.TryGetProperty("objectiveCargo", out _))
            { code = "fixed-container-recipient"; reason = "A task device cannot be stored as a pickup."; return false; }
            if (!TryTaskObject(row, locator!, out var taskObject, out code, out reason)) return false;
            item = new FixedItem(id!, level, locator!, position!, yaw, null, 0, null, taskObject);
            return true;
        }
        var hasItem = row.TryGetProperty("itemId", out _);
        var hasDoor = row.TryGetProperty("targetDoor", out _);
        if (hasItem == hasDoor)
        {
            code = "fixed-item-target";
            reason = "An item row names exactly one of `itemId` and `targetDoor`; a row with both or with neither names nothing.";
            return false;
        }
        uint? itemId = null;
        MapObjectReference? targetDoor = null;
        if (hasItem)
        {
            if (!RuntimeJson.TryInteger(row, "itemId", 1, uint.MaxValue, out var idValue))
            { code = "fixed-item-target"; reason = "An `itemId` is a native item block id: a whole number in [1, " + uint.MaxValue + "]."; return false; }
            itemId = (uint)idValue;
            // The one item id this document never carries. A bulkhead key is generated by the level's own
            // `BulkheadKeyPlacements`, and the authored point moves that key through `bulkhead-keys.forge`; a row
            // here would build a second key of an id the level already placed, so it is refused by name instead of
            // spawned.
            if (itemId == FixedPlacement.BulkheadKeyItemId)
            {
                code = "fixed-item-bulkhead-key";
                reason = "Bulkhead key " + FixedPlacement.BulkheadKeyItemId + " goes through `bulkhead-keys.forge`, "
                    + "which moves the key the level's own generator placed; this module never spawns a second one.";
                return false;
            }
        }
        else
        {
            if (!DocumentReading.Text(row, "targetDoor", MaximumDoorAddressChars, out var doorText)
                || MapObjectDoorAddress.TryParse(doorText) is not { } door || door.Key != MapObjectDoorAddress.Security)
            { code = "fixed-item-target"; reason = "A `targetDoor` is a zone entrance address of the form `door/<dimension>/<layer>/<zone>/" + MapObjectDoorAddress.Security + "`."; return false; }
            targetDoor = door;
        }
        if (!DocumentReading.Number(row, "amount", 0, FixedPlacement.MaximumAmount, out var amount))
        { code = "fixed-item-amount"; reason = "An amount is a finite number in [0, " + FixedPlacement.MaximumAmount + "]."; return false; }
        if (targetDoor != null && amount != 0)
        { code = "fixed-item-amount"; reason = "A keycard row carries no amount; write 0."; return false; }
        FixedObjectiveCargo? cargo = null;
        if (row.TryGetProperty("objectiveCargo", out var claim))
        {
            if (claim.ValueKind != JsonValueKind.Object || !DocumentReading.Known(claim, out _, "layer", "chainIndex", "index", "count")
                || !RuntimeJson.TryInteger(claim, "layer", 0, 2, out var cargoLayer)
                || !RuntimeJson.TryInteger(claim, "chainIndex", 0, int.MaxValue, out var chain)
                || !RuntimeJson.TryInteger(claim, "index", 0, int.MaxValue, out var cargoIndex)
                || !RuntimeJson.TryInteger(claim, "count", 1, int.MaxValue, out var count)
                || cargoIndex >= count || itemId != 131 || amount != 0 || containerId == null
                || locator!.Dimension != 0 || locator.Layer != cargoLayer)
            { code = "fixed-cargo-role"; reason = "A native battery claim needs item 131, zero amount, a same-layer primary-dimension box and a valid index/count."; return false; }
            cargo = new FixedObjectiveCargo((int)cargoLayer, (int)chain, (int)cargoIndex, (int)count);
        }
        item = new FixedItem(id!, level, locator!, position!, yaw, itemId, amount, targetDoor, containerId: containerId, objectiveCargo: cargo, startsOff: startsOff);
        return true;
    }
}
