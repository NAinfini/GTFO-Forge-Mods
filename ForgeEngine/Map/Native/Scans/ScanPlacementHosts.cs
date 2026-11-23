using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Stage active area children at the end of batch 45. The original batch 46 collects, sets up and
/// registers them; the original batch 47 creates each puzzle. No shared table, native job or callback is replaced.
/// Successfully attached hosts belong to the area's native lifetime. This class retains observation receipts only.</summary>
internal sealed class ScanPlacementHosts
{
    private readonly IReadOnlyList<ScanPlacement> _rows;
    private readonly Action<string> _report;
    private readonly Dictionary<string, LG_WorldEventObject> _hosts = new(StringComparer.Ordinal);
    private readonly OnceReporter _once = new();
    private LG_Factory? _factory;
    private MapLevelReference? _level;
    private bool _prepared;
    private bool _windowClosed;
    private bool _validated;

    internal ScanPlacementHosts(IReadOnlyList<ScanPlacement> rows, Action<string> report)
    {
        _report = report;
        var accepted = new List<ScanPlacement>();
        foreach (var row in rows)
        {
            if (rows.Count(other => other.Level == row.Level
                && (other.Id == row.Id || other.ChainedPuzzleId == row.ChainedPuzzleId
                    || other.Filter == row.Filter)) > 1)
                Refuse(row, "duplicate-document", propagateReport: true);
            else accepted.Add(row);
        }
        _rows = accepted.AsReadOnly();
    }

    internal void BatchDone(LG_Factory? factory, LG_Factory.BatchName batch, long epoch, MapLevelReference level)
    {
        // Batch 45 is the last proven window before the original area's collection. No late fallback exists.
        if ((int)batch is not (0 or 45 or 46 or 47) || factory == null || factory.WasCollected) return;
        if (_factory == null || _factory.WasCollected || _factory.Pointer != factory.Pointer || (int)batch == 0)
        {
            _hosts.Clear(); _once.Reset(); _prepared = false; _windowClosed = false; _validated = false;
            _factory = factory; _level = level;
        }
        if ((int)batch >= 46) { _windowClosed = true; return; }
        if ((int)batch != 45 || _prepared || _windowClosed || _level != level) return;
        _prepared = true;
        if (epoch == 0)
        {
            foreach (var row in _rows) if (row.Level == level) Refuse(row, "epoch-unavailable");
            return;
        }
        // Earlier callbacks can have cached a partial level. Do not leave this stage's terminal/door snapshot
        // behind either: batch 47 still has native spawning work to do.
        ZoneIndex.Reset();
        try
        {
            foreach (var row in _rows)
            {
                if (row.Level != level) continue;
                try { PrepareRow(epoch, row); }
                catch (Exception error) { Refuse(row, "exception:" + error.GetType().Name); }
            }
        }
        finally { ZoneIndex.Reset(); }
    }

    private void PrepareRow(long epoch, ScanPlacement row)
    {
        // Substring conflicts are a placement refusal, not a new package-startup invariant. Each conflicting
        // author row refuses independently before creating anything, including across admitted packages.
        if (_rows.Any(other => other.Level == row.Level && other.Id != row.Id && NamesOverlap(other.Filter, row.Filter)))
        { Refuse(row, "author-filter-conflict"); return; }
        if (!RoomResolver.TryPlace(epoch, row.Locator, out var room, out var reason)) { Refuse(row, reason!); return; }
        var world = room!.Pose.Point(row.Position);
        // No area-node fallback: an invalid point must not turn into a scan at the area's centre. The detail names which check failed
        // and where, so a point off the room's walkable space can be told from a point in a neighbouring zone or room.
        var at = FormattableString.Invariant($" world=({world[0]:0.###},{world[1]:0.###},{world[2]:0.###})");
        if (!RoomResolver.TryCourseNode(row.Locator.Dimension, world, null, out var node) || node?.m_area is not { } area)
        { Refuse(row, "position-or-zone", detail: "no-course-node" + at); return; }
        if (node.m_zone == null || node.m_zone.m_layer == null || (int)node.m_zone.LocalIndex != row.Locator.LocalIndex
            || (int)node.m_zone.m_dimensionIndex != row.Locator.Dimension || (int)node.m_zone.m_layer.m_type != row.Locator.Layer)
        { Refuse(row, "position-or-zone", detail: "other-zone" + at); return; }
        if (area.m_geomorph == null || area.m_geomorph.GetInstanceID() != room.GeomorphInstanceId)
        { Refuse(row, "position-or-zone", detail: "other-room" + at); return; }
        // Native Setup gathers world-event components recursively. Attaching below an existing host on the
        // area object would make that original host adopt our chain as well as its own components.
        if (area.GetComponent<LG_WorldEventObject>() is { } areaHost && !areaHost.WasCollected)
        { Refuse(row, "area-host"); return; }
        var zone = node.m_zone;
        var spawns = zone.m_settings?.m_zoneData?.WorldEventChainedPuzzleDatas;
        var matches = 0;
        if (spawns != null) for (var i = 0; i < spawns.Count; i++)
            if (spawns[i].WorldEventObjectFilter == row.Filter && spawns[i].ChainedPuzzle == row.ChainedPuzzleId) matches++;
        if (matches != 1) { Refuse(row, "native-row-count"); return; }
        // The native job searches the whole level. Another native request must not consume this host first.
        foreach (var candidate in ZoneIndex.Current(epoch).NativeZones())
        {
            var requests = candidate.m_settings?.m_zoneData?.WorldEventChainedPuzzleDatas;
            if (requests == null) continue;
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                if (string.IsNullOrEmpty(request.WorldEventObjectFilter)) continue;
                if (row.Filter.Contains(request.WorldEventObjectFilter, StringComparison.Ordinal)
                    || request.WorldEventObjectFilter.Contains(row.Filter, StringComparison.Ordinal))
                {
                    if (candidate.Pointer != zone.Pointer || request.WorldEventObjectFilter != row.Filter
                        || request.ChainedPuzzle != row.ChainedPuzzleId)
                    { Refuse(row, "native-filter-conflict"); return; }
                }
            }
        }
        var data = ChainedPuzzleDataBlock.GetBlock(row.ChainedPuzzleId);
        if (data == null || data.UseRandomPositions) { Refuse(row, "preset"); return; }
        var position = new Vector3((float)world[0], (float)world[1], (float)world[2]);
        if (!ForgeCore.Level.GroundRay(position + new Vector3(0, .15f, 0), .3f, -1, out var floor)
            || floor.normal.y < .5f || Math.Abs(floor.point.y - position.y) > .15f)
        { Refuse(row, "floor"); return; }
        // Read exactly the current level's collection hierarchy, not a global asset/object search. The
        // native query's exact-key fast path and bounded shared scratch buffer cannot prove uniqueness.
        var collections = 0;
        foreach (var candidate in ZoneIndex.Current(epoch).NativeZones())
        {
            if (candidate.m_areas == null) continue;
            for (var i = 0; i < candidate.m_areas.Count; i++)
            {
                var collectionArea = candidate.m_areas[i];
                if (collectionArea == null || collectionArea.WasCollected) continue;
                if (area.transform.Pointer == collectionArea.transform.Pointer || area.transform.IsChildOf(collectionArea.transform)) collections++;
                foreach (var existing in collectionArea.gameObject.GetComponentsInChildren<LG_WorldEventObject>(true))
                    if (existing != null && !existing.WasCollected && NamesOverlap(existing.gameObject.name, row.Filter))
                    { Refuse(row, "host-name-conflict"); return; }
            }
        }
        if (collections != 1) { Refuse(row, "area-collection-count"); return; }
        var obj = new GameObject(row.Filter);
        try
        {
            obj.SetActive(false);
            obj.AddComponent<LG_WorldEventChainPuzzle>();
            var host = obj.AddComponent<LG_WorldEventObject>();
            obj.transform.SetParent(area.transform, true);
            obj.transform.position = position;
            obj.SetActive(true);
            if (!obj.activeInHierarchy) throw new InvalidOperationException("inactive-area");
            _hosts.Add(row.Id, host);
        }
        catch
        {
            // Destroy is deferred by Unity. Keep an incomplete object out of the collection that can run in
            // this same Factory.Update, before requesting destruction. Nothing was manually registered.
            obj.SetActive(false);
            obj.transform.SetParent(null, true);
            UnityEngine.Object.Destroy(obj);
            throw;
        }
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ScanPlacement, "host staged; awaiting native collection/generation " + row.Id,
            RuntimeLogValue.Of("id", row.Id), RuntimeLogValue.Of("chainedPuzzleId", (long)row.ChainedPuzzleId), RuntimeLogValue.Of("status", "prepared"));
    }

    internal void Validate(LG_Factory? factory, long epoch, MapLevelReference level)
    {
        if (_validated && _level == level && _factory?.Pointer == factory?.Pointer) return;
        _validated = true;
        foreach (var row in _rows)
        {
            if (row.Level != level) continue;
            if (!_prepared || epoch == 0 || _level != level || factory == null || factory.WasCollected
                || _factory == null || _factory.WasCollected || _factory.Pointer != factory.Pointer)
            { Refuse(row, "placement-window-missed"); continue; }
            if (!_hosts.TryGetValue(row.Id, out var host)) continue; // Preparation already reported the reason.
            if (host == null || host.WasCollected || host.ParentArea == null
                || ScanInstanceResolver.Instances(row.ChainedPuzzleId).Count != 1
                || host.GetComponent<LG_WorldEventChainPuzzle>()?.m_chainedPuzzleInstance?.Data?.persistentID != row.ChainedPuzzleId)
            { Refuse(row, "generated-count"); continue; }
            // Observation never clears or replaces a puzzle the native chain created, including on failure.
            MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ScanPlacement, "generated " + row.Id,
                RuntimeLogValue.Of("id", row.Id), RuntimeLogValue.Of("chainedPuzzleId", (long)row.ChainedPuzzleId), RuntimeLogValue.Of("status", "generated"));
        }
    }

    internal void Forget()
    {
        // There is no evidenced unregister operation. Native area/level teardown owns every admitted host.
        _hosts.Clear(); _once.Reset(); _factory = null; _level = null;
        _prepared = false; _windowClosed = false; _validated = false;
    }

    private static bool NamesOverlap(string left, string right)
        => !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right)
            && (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal));

    internal bool RoutePointAllowed(MapLevelReference level, uint puzzleId, RoomLocator locator)
    {
        foreach (var row in _rows)
        {
            if (row.Level != level || row.ChainedPuzzleId != puzzleId) continue;
            if (locator.Dimension != row.Locator.Dimension || locator.Layer != row.Locator.Layer || locator.LocalIndex != row.Locator.LocalIndex)
            { Refuse(row, "route-zone"); return false; }
        }
        return true;
    }

    private void Refuse(ScanPlacement row, string reason, bool propagateReport = false, string? detail = null)
    {
        if (!_once.ShouldReport(row.Level + "/" + row.Id + "/" + reason)) return;
        // Startup validation deliberately uses a throwing reporter. At runtime the record below is the one report: the
        // native-report sentence would write the same refusal a second time.
        if (propagateReport)
            _report("scan-placement-refused id=" + row.Id + " level=" + row.Level + " reason=" + reason + (detail == null ? "" : " detail=" + detail));
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ScanPlacementRefused, detail == null ? reason : reason + " " + detail,
            RuntimeLogValue.Of("id", row.Id), RuntimeLogValue.Of("level", row.Level.ToString()), RuntimeLogValue.Of("reason", reason),
            RuntimeLogValue.Of("detail", detail ?? ""));
    }
}
