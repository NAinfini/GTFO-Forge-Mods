using ForgeCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIGraph;
using ForgeEngine.Framework;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The level's one extraction point (contract §4.5 M13). The native `LG_LevelExitGeo` is the only host the game's
/// extraction scan understands: its `m_securityScanAlign` decides where the scan stands and its `m_exitArea` decides
/// which Area (and so which wave nodes and HUD zone) the scan belongs to. This host attaches one such component to
/// the Area the authored point lies in, at the end of factory batch 45 — before the original batch 46 collects the
/// area and before the factory-done callback the component subscribes to in its own `Start`.
///
/// The point must resolve exactly: the authored room and Area identities select one current generation receipt,
/// while the room-local point and facing only compose the scan align's world pose. Height and navigation sampling
/// never choose another Area. A room row instead selects the one vanilla `LG_LevelExitGeo` the resolved room's own
/// geomorph holds and keeps its serialized scan align and exit Area untouched. Anything missing, stale or ambiguous
/// is refused by name. While the level has such a row, every other `LG_LevelExitGeo` is refused its `Start`, so the
/// custom win-condition slot (one per level) is never written twice.
/// </summary>
internal sealed class ExtractionPointHost
{
    private readonly IReadOnlyList<ExtractionPoint> _rows;
    private readonly Action<string> _report;
    private readonly OnceReporter _once = new();
    private readonly Dictionary<IntPtr, LG_LevelExitGeo> _suppressedStarts = new();
    private LG_Factory? _factory;
    private MapLevelReference? _level;
    private bool _prepared;
    private bool _windowClosed;
    // The authored point was refused: the level's own extraction stands, so the players can still leave.
    private bool _fallback;
    private LG_LevelExitGeo? _host;
    private LG_Area? _area;
    private Transform? _align;

    internal ExtractionPointHost(IReadOnlyList<ExtractionPoint> rows, Action<string> report)
    {
        _rows = rows;
        _report = report;
    }

    private ExtractionPoint? RowFor(MapLevelReference level)
    {
        foreach (var row in _rows) if (row.Level == level) return row;
        return null;
    }

    internal void BatchDone(LG_Factory? factory, LG_Factory.BatchName batch, long epoch, MapLevelReference level)
    {
        // Batch 45 is the last proven window before the original area's collection. A point not attached by then is
        // refused and the level's own extraction stands (see Refuse).
        if ((int)batch is not (0 or 45 or 46 or 47) || factory == null || factory.WasCollected) return;
        if (_factory == null || _factory.WasCollected || _factory.Pointer != factory.Pointer || (int)batch == 0)
        {
            _host = null; _area = null; _align = null; _once.Reset(); _suppressedStarts.Clear(); _prepared = false; _windowClosed = false;
            _fallback = false;
            _factory = factory; _level = level;
        }
        if (_level != level || RowFor(level) is not { } row) return;
        if ((int)batch >= 46)
        {
            _windowClosed = true;
            if (!_prepared) Refuse(row, "placement-window-missed");
            return;
        }
        if ((int)batch != 45 || _prepared || _windowClosed) return;
        _prepared = true;
        if (epoch == 0) { Refuse(row, "epoch-unavailable"); return; }
        // Earlier callbacks can have cached a partial level; batch 46 and 47 still have native work to do.
        ZoneIndex.Reset();
        try { Place(epoch, row); }
        catch (Exception error) { Refuse(row, "exception:" + error.GetType().Name); }
        finally { ZoneIndex.Reset(); }
    }

    private void Place(long epoch, ExtractionPoint row)
    {
        if (!RoomResolver.TryPlace(epoch, row.Locator, out var located, out var reason))
        { Refuse(row, reason ?? "room-unresolved"); return; }
        if (row.RoomExit) { SelectRoomExit(row, located!); return; }
        RoomHit? room = null;
        reason = null;
        if (row.Locator.InstanceId is not { } roomId || !RoomResolver.TryPlaceArea(epoch, row.Level,
            new RoomScope(row.Locator.Dimension, row.Locator.Layer, row.Locator.LocalIndex), roomId, row.AreaId,
            out room, out reason))
        { Refuse(row, reason ?? "authored-area-room-unbound"); return; }
        if (located!.GeomorphInstanceId != room!.GeomorphInstanceId || located.ZoneInstanceId != room.ZoneInstanceId)
        { Refuse(row, "authored-area-room-mismatch"); return; }
        var world = located.Pose.Point(row.Position);
        var area = room.Area;
        var node = room.AreaCourseNode;
        if (area == null || area.WasCollected || node == null || node.WasCollected || node.m_area == null
            || node.m_area.Pointer != area.Pointer
            || node.m_zone == null || node.m_zone.m_layer == null || (int)node.m_zone.LocalIndex != row.Locator.LocalIndex
            || (int)node.m_zone.m_dimensionIndex != row.Locator.Dimension || (int)node.m_zone.m_layer.m_type != row.Locator.Layer
            || area.m_geomorph == null || area.m_geomorph.GetInstanceID() != room.GeomorphInstanceId)
        { Refuse(row, "area-or-course-node"); return; }
        var position = new Vector3((float)world[0], (float)world[1], (float)world[2]);
        var q = room.Pose.Compose(Rotations.Yaw(row.YawDegrees));
        var rotation = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
        // A generated Area can already contain the vanilla exit component. It is the same single host the
        // authored point needs, so adopt it; only several live candidates are ambiguous.
        var existing = area.GetComponentsInChildren<LG_LevelExitGeo>()
            .Where(value => value != null && !value.WasCollected).ToArray();
        if (existing.Length > 1) { Refuse(row, "area-host"); return; }
        if (existing.Length == 1)
        {
            var host = existing[0];
            var align = host.m_securityScanAlign;
            if (align == null || align.WasCollected)
            {
                align = new GameObject("ForgeExtractionScanAlign").transform;
                align.SetParent(host.transform, true);
            }
            align.SetPositionAndRotation(position, rotation);
            host.m_exitArea = area;
            host.m_securityScanAlign = align;
            _host = host; _area = area; _align = align;
            ResumeSelectedHost(row, host);
            Record(row, area, node, position);
            return;
        }
        var obj = new GameObject("ForgeExtractionPoint");
        try
        {
            // Inactive until both spatial fields are written: the native Start must see the finished component.
            obj.SetActive(false);
            var host = obj.AddComponent<LG_LevelExitGeo>();
            obj.transform.SetParent(area.transform, true);
            obj.transform.SetPositionAndRotation(position, rotation);
            var align = new GameObject("ForgeExtractionScanAlign");
            align.transform.SetParent(obj.transform, true);
            align.transform.SetPositionAndRotation(position, rotation);
            host.m_exitArea = area;
            host.m_securityScanAlign = align.transform;
            _host = host; _area = area; _align = align.transform;
            obj.SetActive(true);
            if (!obj.activeInHierarchy) throw new InvalidOperationException("inactive-area");
        }
        catch
        {
            // Destroy is deferred by Unity. Keep an incomplete object out of the collection that can run in this
            // same Factory.Update, before requesting destruction.
            _host = null; _area = null; _align = null;
            obj.SetActive(false);
            obj.transform.SetParent(null, true);
            UnityEngine.Object.Destroy(obj);
            throw;
        }
        ResumeSelectedHost(row, _host!);
        Record(row, area, node, position);
    }

    /// <summary>The room's own vanilla exit: the one live `LG_LevelExitGeo` the resolved geomorph holds is the host,
    /// unchanged — its own scan align and exit Area. Every other host stays held back exactly as for a point.</summary>
    private void SelectRoomExit(ExtractionPoint row, RoomHit room)
    {
        var geomorph = room.Area?.m_geomorph;
        if (geomorph == null || geomorph.WasCollected || geomorph.GetInstanceID() != room.GeomorphInstanceId)
        { Refuse(row, "room-exit-missing"); return; }
        var hosts = geomorph.GetComponentsInChildren<LG_LevelExitGeo>()
            .Where(value => value != null && !value.WasCollected).ToArray();
        if (hosts.Length == 0) { Refuse(row, "room-exit-missing"); return; }
        if (hosts.Length > 1) { Refuse(row, "room-exit-ambiguous"); return; }
        var host = hosts[0];
        var area = host.m_exitArea;
        var align = host.m_securityScanAlign;
        var node = area?.m_courseNode;
        if (area == null || area.WasCollected || align == null || align.WasCollected || node == null || node.WasCollected
            || node.m_zone == null || node.m_zone.m_layer == null || area.m_geomorph == null
            || area.m_geomorph.GetInstanceID() != room.GeomorphInstanceId)
        { Refuse(row, "room-exit-fields"); return; }
        _host = host; _area = area; _align = align;
        ResumeSelectedHost(row, host);
        Record(row, area, node, align.position);
    }

    private void ResumeSelectedHost(ExtractionPoint row, LG_LevelExitGeo host)
    {
        var resume = _suppressedStarts.Remove(host.Pointer);
        if (_suppressedStarts.Count != 0) SkipOtherHost(row);
        _suppressedStarts.Clear();
        if (resume) host.Start();
    }

    private static void Record(ExtractionPoint row, LG_Area area, AIG_CourseNode node, Vector3 position)
    {
        var zone = node.m_zone!;
        var layer = zone.m_layer!;
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ExtractionPointPlaced,
            row.RoomExit ? "the room's own vanilla extraction host selected" : "extraction host attached at the authored point",
            RuntimeLogValue.Of("level", row.Level.ToString()), RuntimeLogValue.Of("mode", row.RoomExit ? "room" : "point"),
            RuntimeLogValue.Of("area", area.name),
            RuntimeLogValue.Of("zone", ((int)zone.m_dimensionIndex).ToString(CultureInfo.InvariantCulture) + "/"
                + ((int)layer.m_type).ToString(CultureInfo.InvariantCulture) + "/"
                + ((int)zone.LocalIndex).ToString(CultureInfo.InvariantCulture)),
            RuntimeLogValue.Of("areaId", row.AreaId), RuntimeLogValue.Of("x", (double)position.x),
            RuntimeLogValue.Of("y", (double)position.y), RuntimeLogValue.Of("z", (double)position.z),
            RuntimeLogValue.Of("yaw", row.YawDegrees));
    }

    /// <summary>The prefix of `LG_LevelExitGeo.Start`: true lets the native Start run. A level with an authored
    /// point lets only that point's own host start, with the spatial fields it was attached with.</summary>
    internal bool Starting(LG_LevelExitGeo component, MapLevelReference? level)
    {
        if (_fallback || level is not { } current || RowFor(current) is not { } row) return true;
        if (_host != null && !_host.WasCollected && _host.Pointer == component.Pointer)
        {
            if (component.m_exitArea == null || _area == null || component.m_exitArea.Pointer != _area.Pointer
                || component.m_securityScanAlign == null || _align == null || component.m_securityScanAlign.Pointer != _align.Pointer)
            { RefuseHost(row, "host-fields-changed"); return false; }
            return true;
        }
        if (_host == null)
        {
            _suppressedStarts[component.Pointer] = component;
            return false;
        }
        SkipOtherHost(row);
        return false;
    }

    internal bool Owns(Component endpoint, MapLevelReference? level)
    {
        if (level is not { } current) return false;
        if (RowFor(current) != null && !_fallback)
            return endpoint is LG_LevelExitGeo exit && _host != null && !_host.WasCollected && _host.Pointer == exit.Pointer;
        if (endpoint is not ElevatorShaftLanding landing || landing.WasCollected) return false;
        var currentLanding = ElevatorShaftLanding.Current;
        return currentLanding != null && !currentLanding.WasCollected && currentLanding.Pointer == landing.Pointer;
    }

    internal bool UsesPoint(MapLevelReference? level) => !_fallback && level is { } current && RowFor(current) != null;

    internal void Forget()
    {
        // There is no evidenced unregister operation. Native area/level teardown owns the attached host.
        _host = null; _area = null; _align = null; _once.Reset(); _suppressedStarts.Clear(); _factory = null; _level = null;
        _prepared = false; _windowClosed = false; _fallback = false;
    }

    /// <summary>The authored point cannot be attached: the level keeps its own extraction. The starts held back while the
    /// point was expected are let through now, and every later start runs untouched.</summary>
    private void Refuse(ExtractionPoint row, string reason)
    {
        _fallback = true;
        var held = _suppressedStarts.Values.ToArray();
        _suppressedStarts.Clear();
        foreach (var component in held) if (component != null && !component.WasCollected) component.Start();
        if (!_once.ShouldReport(row.Level + "/point/" + reason)) return;
        try { _report("extraction-point-refused level=" + row.Level + " reason=" + reason + "; the level's own extraction is used"); }
        catch (Exception reportError) { MapLog.Contained("extraction-point.refused-report", reportError); }
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ExtractionPointRefused, reason,
            RuntimeLogValue.Of("level", row.Level.ToString()), RuntimeLogValue.Of("reason", reason));
    }

    /// <summary>The level's own vanilla extraction host is held back because the authored point is the level's extraction.
    /// That is the designed outcome of every level with an authored point, so it is one Info record per level, not an error.</summary>
    private void SkipOtherHost(ExtractionPoint row)
    {
        if (!_once.ShouldReport(row.Level + "/host/other-host")) return;
        // Not through _report: that channel writes an Error map.native-report, and this is the designed outcome.
        MapLog.Record(RuntimeLogLevel.Info, MapLogCodes.ExtractionHostSkipped, "vanilla extraction host held back; the authored point is the extraction",
            RuntimeLogValue.Of("level", row.Level.ToString()), RuntimeLogValue.Of("reason", "other-host"));
    }

    private void RefuseHost(ExtractionPoint row, string reason)
    {
        if (!_once.ShouldReport(row.Level + "/host/" + reason)) return;
        try { _report("extraction-host-refused level=" + row.Level + " reason=" + reason); }
        catch (Exception reportError) { MapLog.Contained("extraction-point.host-refused-report", reportError); }
        MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.ExtractionHostRefused, reason,
            RuntimeLogValue.Of("level", row.Level.ToString()), RuntimeLogValue.Of("reason", reason));
    }
}

/// <summary>The session half of the extraction point: one factory-batch subscription for the lifetime of the
/// admitted package set, and the entry the declared `LG_LevelExitGeo.Start` hook asks.</summary>
internal sealed class ExtractionPointSession : IDisposable
{
    private readonly ExtractionPointHost _host;
    private readonly Func<long> _worldEpoch;
    private readonly Func<MapLevelReference?> _level;
    private readonly Action<string> _report;
    private IDisposable? _batchDone;
    internal int Rows { get; }

    internal ExtractionPointSession(IReadOnlyList<ExtractionPoint> rows, Func<long> worldEpoch, Func<MapLevelReference?> level, Action<string> report)
    {
        _worldEpoch = worldEpoch; _level = level; _report = report; Rows = rows.Count;
        _host = new ExtractionPointHost(rows, report);
        if (rows.Count == 0) return;
        _batchDone = ForgeCore.FactoryEvents.OnBatchDone(BatchDone);
    }

    private void BatchDone(LG_Factory.BatchName batch)
    {
        if ((int)batch is not (0 or 45 or 46 or 47)) return;
        try
        {
            if (_level() is { } level) _host.BatchDone(LG_Factory.Current, batch, _worldEpoch(), level);
        }
        catch (Exception error)
        {
            MapLog.Contained("extraction-point.batch-done", error);
            try { _report("Map extraction point callback failed: " + error.GetType().Name + ": " + error.Message); }
            catch (Exception reportError) { MapLog.Contained("extraction-point.batch-done-report", reportError); }
        }
    }


    internal bool Starting(LG_LevelExitGeo component)
    {
        if (Rows == 0) return true;
        return _host.Starting(component, _level());
    }

    internal bool Owns(Component component) => _host.Owns(component, _level());
    internal bool UsesPoint(MapLevelReference? level) => _host.UsesPoint(level);

    public void Dispose()
    {
        _batchDone?.Dispose();
        _batchDone = null;
        _host.Forget();
    }
}

/// <summary>Declared hook (installed by Runtime): the native extraction host starts only when it is the level's
/// authored point. A prefix that returns false keeps the component from subscribing to the factory-done callback,
/// which is what writes the level's one custom win-condition slot.</summary>
[HarmonyPatch(typeof(LG_LevelExitGeo), nameof(LG_LevelExitGeo.Start))]
internal static class ExtractionHostStart
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(LG_LevelExitGeo __instance)
        => MapHost.Session?.ExtractionPoints?.Starting(__instance) ?? true;
}
