using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeEngine.Authoring;

/// <summary>
/// The actual layout of a generated level, recorded into `forge.log` (contract §3.2, `map.layout-generated`).
///
/// The report files already say which build jobs ran; they never said what the level became. This reads the
/// built level — every zone the generator created, the geomorph prefabs inside it and the gate through which the
/// zone was expanded from its parent — and writes one observation per generation attempt: an attempt opens with
/// `complete:false` when the level build starts and closes with the whole layout once
/// <see cref="LG_Factory.FactoryDone"/> returned. The elevator's landing is a later moment, so it is recorded by
/// the observation that carries a landing tick once the game enters the level: the website reads the last
/// observation, and an attempt that never lands stays visibly incomplete. Authoring mode only (<see cref="Start"/>).
///
/// Every value here is read, never inferred from the plan: a zone with no readable geomorph reports an empty
/// tile list, and a direction the two zone centres do not settle reports `unknown`.
/// </summary>
internal static class GeneratedLayoutRecord
{
    private static long? _landingTick;
    private static bool _attemptOpen;
    private static bool _completed;

    /// <summary>Subscribes the three level moments; called by the host after its own level lifecycle, so each record is
    /// written into the world the build started.</summary>
    internal static void Start()
    {
        GTFO.API.LevelAPI.OnBuildStart += AttemptStarted;
        GTFO.API.LevelAPI.OnFactoryDone += AttemptCompleted;
        GTFO.API.LevelAPI.OnEnterLevel += Landed;
    }

    internal static void Stop()
    {
        GTFO.API.LevelAPI.OnBuildStart -= AttemptStarted;
        GTFO.API.LevelAPI.OnFactoryDone -= AttemptCompleted;
        GTFO.API.LevelAPI.OnEnterLevel -= Landed;
    }

    /// <summary>Opens an attempt: a generation run has started and has built nothing yet.</summary>
    private static void AttemptStarted()
    {
        _landingTick = null;
        _attemptOpen = true;
        _completed = false;
        Write(new RuntimeLogLayout
        {
            Complete = false,
            ElevatorLandedTick = null,
            Zones = Array.Empty<RuntimeLogLayoutZone>(),
            Connections = Array.Empty<RuntimeLogLayoutConnection>(),
        });
    }

    /// <summary>Closes the attempt with the layout the generator actually built.</summary>
    private static void AttemptCompleted()
    {
        if (!_attemptOpen) return;
        _completed = true;
        Write(Capture());
    }

    /// <summary>Records the landing once, with the kernel tick of the level entry. Only an already-completed attempt gets a
    /// second observation here; otherwise the completion record carries the landing tick.</summary>
    private static void Landed()
    {
        if (!_attemptOpen || _landingTick != null) return;
        var tick = Plugin.Runtime?.CurrentTick ?? -1;
        _landingTick = tick >= 0 ? tick : null;
        if (_completed) Write(Capture());
    }

    private static RuntimeLogLayout Capture()
    {
        var floor = Builder.Current?.m_currentFloor;
        var zones = floor?.allZones;
        var rows = new List<RuntimeLogLayoutZone>();
        var links = new List<RuntimeLogLayoutConnection>();
        if (zones != null)
        {
            foreach (var zone in zones)
            {
                if (zone == null) continue;
                var locator = Locator(zone);
                if (locator == null) continue;
                rows.Add(new RuntimeLogLayoutZone
                {
                    Zone = locator,
                    Dimension = (int)zone.DimensionIndex,
                    Layer = LayerOf(zone),
                    LocalIndex = (int)zone.LocalIndex,
                    Floor = FloorOf(floor),
                    Tiles = Tiles(zone),
                });
                if (rows.Count >= RuntimeLogLayoutLimits.Zones) break;
            }
            foreach (var zone in zones)
            {
                if (links.Count >= RuntimeLogLayoutLimits.Connections) break;
                var child = Locator(zone);
                var parent = ParentOf(zone);
                if (child == null || parent == null) continue;
                links.Add(new RuntimeLogLayoutConnection { From = Locator(parent)!, To = child, Direction = DirectionBetween(parent, zone!) });
            }
        }
        return new RuntimeLogLayout
        {
            Complete = true,
            ElevatorLandedTick = _landingTick,
            Zones = rows,
            Connections = links,
        };
    }

    private static void Write(RuntimeLogLayout layout)
    {
        try { Plugin.Runtime?.ReportGeneratedLayout(in layout); }
        catch (Exception error) { Plugin.PluginLog.LogError("authoring.layout-record-failed " + error.GetType().Name + ": " + error.Message); }
    }
    /// <summary>The zone that expanded this zone, read off the gate it was built through: the gate links the
    /// parent's area to the new one, and which side is the parent is a fact of the built level, not an assumption.</summary>
    private static LG_Zone? ParentOf(LG_Zone? zone)
    {
        var gate = zone?.m_sourceGate;
        if (gate == null) return null;
        var from = gate.m_linksFrom?.m_zone;
        var to = gate.m_linksTo?.m_zone;
        if (from == null || to == null) return null;
        return ForgeCore.NativeObjects.Same(from, zone) ? to : from;
    }

    private static string? Locator(LG_Zone? zone)
    {
        if (zone == null) return null;
        var layer = LayerOf(zone);
        return layer < 0 ? null
            : (int)zone.DimensionIndex + "/" + layer.ToString(CultureInfo.InvariantCulture) + "/"
                + ((int)zone.LocalIndex).ToString(CultureInfo.InvariantCulture);
    }

    private static int LayerOf(LG_Zone? zone)
    {
        try { return zone?.Layer == null ? -1 : (int)zone.Layer.m_type; }
        catch (Exception) { return -1; } // Il2Cpp read guard: -1 is the documented "unknown layer" value in the layout record.
    }

    private static int FloorOf(LG_Floor? floor)
    {
        try { return floor == null ? -1 : floor.ID; }
        catch (Exception) { return -1; } // Il2Cpp read guard: -1 is the documented "unknown floor" value in the layout record.
    }

    /// <summary>The geomorph prefabs built inside one zone, in area order and without duplicates: the same
    /// geomorph placed twice is one tile of this zone, not two.</summary>
    private static IReadOnlyList<string> Tiles(LG_Zone zone)
    {
        var tiles = new List<string>();
        var areas = zone.m_areas;
        if (areas == null) return tiles;
        foreach (var area in areas)
        {
            if (tiles.Count >= RuntimeLogLayoutLimits.TilesPerZone) break;
            string? name;
            try
            {
                var geomorph = area?.m_geomorph;
                if (geomorph == null) continue;
                name = geomorph.m_geoPrefab == null ? geomorph.name : geomorph.m_geoPrefab.name;
            }
            catch (Exception) { continue; } // Il2Cpp read guard: an area whose geomorph is unreadable lists no tile; the record is a recorder observation, not a verdict.
            if (string.IsNullOrEmpty(name) || tiles.Contains(name)) continue;
            tiles.Add(name!);
        }
        return tiles;
    }

    /// <summary>The dominant world axis from the parent zone's centre to the child's, which is what "the child
    /// lies north of its parent" means for a generated level. A zero offset is `same` and an unreadable centre is
    /// `unknown`; neither is guessed into `north`.</summary>
    private static string DirectionBetween(LG_Zone parent, LG_Zone child)
    {
        Vector3 offset;
        try { offset = child.CenterPosition - parent.CenterPosition; }
        catch (Exception) { return RuntimeLogLayoutDirections.Unknown; } // Il2Cpp read guard: an unreadable centre is the explicit direction "unknown", never a guessed one.
        if (!float.IsFinite(offset.x) || !float.IsFinite(offset.y) || !float.IsFinite(offset.z)) return RuntimeLogLayoutDirections.Unknown;
        var ax = Math.Abs(offset.x); var ay = Math.Abs(offset.y); var az = Math.Abs(offset.z);
        if (ax == 0f && ay == 0f && az == 0f) return RuntimeLogLayoutDirections.Same;
        if (ax >= ay && ax >= az) return offset.x >= 0f ? RuntimeLogLayoutDirections.East : RuntimeLogLayoutDirections.West;
        if (az >= ay) return offset.z >= 0f ? RuntimeLogLayoutDirections.North : RuntimeLogLayoutDirections.South;
        return offset.y >= 0f ? RuntimeLogLayoutDirections.Up : RuntimeLogLayoutDirections.Down;
    }
}
