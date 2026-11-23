using System;
using System.Collections.Generic;
using System.Globalization;
using AIGraph;
using ForgeEngine.Framework;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// One authored anchor resolved against the level being played: the spawn type the native wave entry is handed,
/// the course node it is anchored at and, for a point anchor, the world position beside it. A zone or area anchor
/// leaves the position alone — the game's own scorer reads the node's zone or the node itself for those two
/// branches — while a point anchor carries the point the wave object is built at. A named anchor leaves the
/// caller's node and position alone and carries the exact world-event object filter the native OnSpawnPoints
/// branch reads.
/// </summary>
internal sealed class WaveAnchorTarget
{
    internal WaveAnchorTarget(SurvivalWaveSpawnType spawnType, AIG_CourseNode? refNode, bool positioned,
        Vector3 position, string? worldEventFilter = null, LG_Area? nativeArea = null,
        RoomLocator? authorRoom = null, string? authorAreaId = null)
    {
        SpawnType = spawnType;
        RefNode = refNode;
        Positioned = positioned;
        Position = position;
        WorldEventFilter = worldEventFilter;
        NativeArea = nativeArea ?? refNode?.m_area;
        AuthorRoom = authorRoom;
        AuthorAreaId = authorAreaId;
    }

    internal SurvivalWaveSpawnType SpawnType { get; }
    internal AIG_CourseNode? RefNode { get; }
    /// <summary>Whether <see cref="Position"/> is this anchor's own world point and has to be written to the
    /// wave entry, or the entry keeps the position it was called with.</summary>
    internal bool Positioned { get; }
    internal Vector3 Position { get; }
    /// <summary>The exact world-event object filter for OnSpawnPoints, or null for another spawn branch.</summary>
    internal string? WorldEventFilter { get; }
    /// <summary>The actual native Area that owns <see cref="RefNode"/>, when this anchor resolves to one concrete
    /// node. An Area anchor supplies the exact receipt Area rather than deriving an identity from its name.</summary>
    internal LG_Area? NativeArea { get; }
    /// <summary>The locked author room receipt behind an Area anchor. Null for anchors that do not address a room.</summary>
    internal RoomLocator? AuthorRoom { get; }
    /// <summary>The stable author Area id resolved through that room receipt.</summary>
    internal string? AuthorAreaId { get; }
}

/// <summary>
/// The one table that turns the settings id a wave is started with into the anchor the author wrote for it, for
/// one world. It is keyed by the world epoch, like the zone table it resolves through: every instance it holds
/// belongs to one level, so a table of an ended world can never answer for the next one.
///
/// Resolution happens when the table is built, once per level and not once per wave: a row that cannot be
/// resolved is reported with its own settings id and reason code at that point, and every later wave of the same
/// level reads the same answer instead of repeating the refusal. A row of another level is not this world's row
/// and is left out rather than refused.
///
/// Nothing is guessed. A zone this level does not have, an authored Area with no exact generation receipt, and a
/// point whose room or course node does not resolve are three refusals by name; none of them is answered with the
/// vanilla spawn behaviour or with another anchor's node.
/// </summary>
internal sealed class WaveAnchorTable
{
    /// <summary>The refusal of a zone anchor the level has no single zone for, or whose zone holds no course node
    /// to anchor the wave at.</summary>
    internal const string ZoneUnresolved = "wave-anchor-zone-unresolved";
    /// <summary>The refusal of an area anchor whose authored room/Area receipt does not resolve to one live native
    /// Area and course node in this level/world.</summary>
    internal const string AreaUnresolved = "wave-anchor-area-unresolved";
    /// <summary>The refusal of a point anchor: its room does not resolve to exactly one room of this level, or
    /// the point has no course node.</summary>
    internal const string PointUnresolved = "wave-anchor-point-unresolved";
    /// <summary>The refusal of a named anchor: no registered vanilla world-event spawn point has that key, or a
    /// matching object cannot supply the course node the native OnSpawnPoints scorer dereferences.</summary>
    internal const string NamedUnresolved = "wave-anchor-named-unresolved";
    /// <summary>I-DIAG info/error records emitted once when one world's immutable anchor table is built.</summary>
    internal const string ResolvedLogCode = "map.wave-anchor-resolved";
    internal const string RefusedLogCode = "map.wave-anchor-refused";

    private static WaveAnchorTable? Cached;

    private readonly long _epoch;
    private readonly MapLevelReference? _level;
    private readonly Dictionary<uint, WaveAnchorTarget> _targets;
    private readonly Dictionary<uint, string> _refusals;

    private WaveAnchorTable(long epoch, MapLevelReference? level, Dictionary<uint, WaveAnchorTarget> targets,
        Dictionary<uint, string> refusals)
    {
        _epoch = epoch;
        _level = level;
        _targets = targets;
        _refusals = refusals;
    }

    /// <summary>
    /// The current level's anchor table, rebuilt when the world changed. A level that cannot be read answers an
    /// empty table — no row of it is this world's row — and is not remembered: the level may be readable on the
    /// next ask, and a refusal cached from before it was there would refuse a wave the level does author. The
    /// level's own identity is what a row is matched by, so a row of another level never reaches the table at
    /// all.
    /// </summary>
    internal static WaveAnchorTable Current(long epoch, IReadOnlyList<WaveAnchor> anchors, MapLevelReference? level,
        Action<string> report)
    {
        var cached = Cached;
        if (cached != null && cached._epoch == epoch && cached._level == level) return cached;
        var built = Build(epoch, anchors, level, report);
        if (level != null) Cached = built;
        return built;
    }

    /// <summary>Drops the table. The world epoch is what invalidates it in production, and a test that stands a
    /// different level up inside one world drops it here instead.</summary>
    internal static void Reset() => Cached = null;

    /// <summary>
    /// The anchor one settings id resolves to. Three answers are told apart: a resolved anchor, a refusal of an
    /// anchor this level authored, and "this install authored no anchor for this id on this level" — the last one
    /// is the caller's leave-alone case, which is why a refusal and an absence are not one answer.
    /// </summary>
    internal bool TryResolve(uint settingsId, out WaveAnchorTarget? target, out string? refusal)
    {
        refusal = null;
        if (_targets.TryGetValue(settingsId, out target)) return true;
        target = null;
        _refusals.TryGetValue(settingsId, out refusal);
        return false;
    }

    /// <summary>One world's rows resolved: the anchors of this level, each resolved or refused with the code that
    /// names why. Every refusal is reported once here, with the settings id that names the author's wave and the
    /// reason code that names the check it failed.</summary>
    private static WaveAnchorTable Build(long epoch, IReadOnlyList<WaveAnchor> anchors, MapLevelReference? level,
        Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(report);
        var targets = new Dictionary<uint, WaveAnchorTarget>();
        var refusals = new Dictionary<uint, string>();
        if (level is { } world)
            foreach (var anchor in anchors)
            {
                if (anchor.Level != world) continue;
                if (Resolve(anchor, epoch, out var target, out var refusal, out var detail))
                {
                    targets[anchor.SettingsId] = target!;
                    Record(anchor, target!, null, detail);
                    continue;
                }
                refusals[anchor.SettingsId] = refusal!;
                Record(anchor, null, refusal, detail);
                report("wave-anchor-refused settingsId=" + anchor.SettingsId.ToString(CultureInfo.InvariantCulture)
                    + " code=" + refusal + (detail == null ? "" : ": " + detail));
            }
        return new WaveAnchorTable(epoch, level, targets, refusals);
    }

    /// <summary>One row resolved against this level. Each kind is answered from the level's own objects: the
    /// zone table for the zone, the shared room/Area registry for an authored Area, and the room resolver for
    /// the point — the same resolver every other authored room-local placement of this module uses, so a point
    /// anchor lands where a fixed placement at the same point would.</summary>
    private static bool Resolve(WaveAnchor anchor, long epoch, out WaveAnchorTarget? target, out string? refusal,
        out string? detail)
    {
        target = null;
        refusal = null;
        detail = null;
        switch (anchor.Kind)
        {
            case WaveAnchorKind.Zone:
                {
                    var coordinates = new ZoneCoordinates(anchor.Zone.Dimension, anchor.Zone.Layer, anchor.Zone.LocalIndex);
                    var zone = ZoneIndex.Current(epoch).ZoneAt(coordinates);
                    if (zone == null) return Refused(ZoneUnresolved, "no zone of this level is at " + Show(coordinates), out refusal, out detail);
                    if (First(zone) is not { } node)
                        return Refused(ZoneUnresolved, "the zone at " + Show(coordinates) + " holds no course node", out refusal, out detail);
                    target = new WaveAnchorTarget(SurvivalWaveSpawnType.InSuppliedCourseNodeZone, node,
                        false, default);
                    return true;
                }
            case WaveAnchorKind.Area:
                {
                    var coordinates = new ZoneCoordinates(anchor.Zone.Dimension, anchor.Zone.Layer, anchor.Zone.LocalIndex);
                    var identity = anchor.Area!.Value;
                    if (!RoomResolver.TryPlaceArea(epoch, anchor.Level,
                            new RoomScope(coordinates.Dimension, coordinates.Layer, coordinates.Zone),
                            identity.RoomId, identity.AreaId, out var room, out var areaRefusal))
                        return Refused(AreaUnresolved, areaRefusal, out refusal, out detail);
                    if (room!.AreaCourseNode is not { } node || node.WasCollected)
                        return Refused(AreaUnresolved, RoomResolution.NoCourseNode, out refusal, out detail);
                    var zone = ZoneIndex.Current(epoch).ZoneAt(coordinates);
                    RoomLocator? locator = null;
                    string? identityRefusal = null;
                    var nativeArea = zone == null ? null : GeneratedRoomRegistry.FindArea(epoch, anchor.Level, zone,
                        identity.RoomId, identity.AreaId, out locator, out identityRefusal);
                    if (nativeArea == null || locator == null || room.Area == null
                        || nativeArea.Pointer != room.Area.Pointer || nativeArea.m_courseNode?.Pointer != node.Pointer)
                        return Refused(AreaUnresolved, identityRefusal ?? "authored-area-readback-mismatch", out refusal, out detail);
                    target = new WaveAnchorTarget(SurvivalWaveSpawnType.InSuppliedCourseNode, node,
                        false, default, nativeArea: nativeArea, authorRoom: locator, authorAreaId: identity.AreaId);
                    return true;
                }
            case WaveAnchorKind.Point:
                {
                    if (!RoomResolver.TryPlace(epoch, anchor.Locator!, out var room, out var roomRefusal))
                        return Refused(PointUnresolved, roomRefusal, out refusal, out detail);
                    if (anchor.WorldPosition(room!.Pose) is not { } world)
                        return Refused(PointUnresolved, "the row carries no point", out refusal, out detail);
                    if (!RoomResolver.TryCourseNode(anchor.Locator!.Dimension, world, room.AreaCourseNode, out var node))
                        return Refused(PointUnresolved, RoomResolution.NoCourseNode, out refusal, out detail);
                    var position = new Vector3((float)world[0], (float)world[1], (float)world[2]);
                    target = new WaveAnchorTarget(SurvivalWaveSpawnType.InSuppliedCourseNode_OnPosition, node!, true, position);
                    // The native voxel lookup picks the node; when it is not the point's own room's node, the wave's
                    // enemies stand in this room while the game files them under another room's node.
                    var roomNode = room.AreaCourseNode;
                    long roomPointer = roomNode == null || roomNode.WasCollected ? 0L : roomNode.Pointer.ToInt64();
                    detail = FormattableString.Invariant(
                        $"pos=({position.x:0.0},{position.y:0.0},{position.z:0.0}) roomNode={roomPointer} same={roomPointer == node!.Pointer.ToInt64()}");
                    return true;
                }
            case WaveAnchorKind.Named:
                {
                    if (WorldEventManager.Current == null)
                        return Refused(NamedUnresolved, "the native world-event manager is unavailable", out refusal, out detail);
                    var results = WorldEventManager.WorldEventObjectResults;
                    int matches;
                    try { matches = WorldEventManager.GetWorldEventObjectsNonAlloc(anchor.Name!, results); }
                    catch (Exception error)
                    {
                        return Refused(NamedUnresolved, "the native world-event query failed (" + error.GetType().Name + ")",
                            out refusal, out detail);
                    }
                    if (matches <= 0)
                        return Refused(NamedUnresolved, "no registered world-event spawn point has the key `" + anchor.Name + "`",
                            out refusal, out detail);
                    // Vanilla's scorer dereferences ParentArea.m_courseNode after it randomly selects one matching
                    // object. Validate every candidate the same bounded native query exposes, without choosing one
                    // here; the original filter and its multi-match seed semantics still reach OnSpawnPoints.
                    for (var i = 0; i < matches && i < results.Length; i++)
                    {
                        var spawnPoint = results[i];
                        var node = spawnPoint?.ParentArea?.m_courseNode;
                        if (spawnPoint == null || spawnPoint.WasCollected || node == null || node.WasCollected)
                            return Refused(NamedUnresolved, "a matching world-event spawn point has no live course node",
                                out refusal, out detail);
                    }
                    target = new WaveAnchorTarget(SurvivalWaveSpawnType.OnSpawnPoints, null, false, default, anchor.Name);
                    return true;
                }
        }
        return Refused(NamedUnresolved, "the anchor kind is unavailable", out refusal, out detail);
    }

    /// <summary>The zone's own first course node, which is the node a zone anchor is written from. The zone
    /// branch of the game's scorer reads the node's zone and not the node itself, so any node of the zone would
    /// be equivalent — the first one is the one the anchor is defined by, and a zone whose list is empty or whose
    /// first node is gone is refused rather than answered with another zone.</summary>
    private static AIG_CourseNode? First(LG_Zone zone)
    {
        var nodes = zone.m_courseNodes;
        if (nodes == null || nodes.Count == 0) return null;
        var node = nodes[0];
        return node == null || node.WasCollected ? null : node;
    }

    /// <summary>One structured I-DIAG fact per matching wave resource; an anchor without a resource still gets one
    /// row keyed by level and settings id, which is enough to diagnose a vanilla caller of the same settings.</summary>
    private static void Record(WaveAnchor anchor, WaveAnchorTarget? target, string? refusal, string? detail)
    {
        var level = refusal == null ? RuntimeLogLevel.Info : RuntimeLogLevel.Error;
        if (!MapLog.Logs(level)) return;
        var matchedResource = false;
        if (WaveResourceSession.Current is { } waves)
            foreach (var resource in waves.Registry.Resources)
            {
                if (resource.SettingsId != anchor.SettingsId || resource.Level != anchor.Level.ToString()) continue;
                matchedResource = true;
                Write(resource.Address);
            }
        if (!matchedResource) Write("");

        void Write(string resource)
        {
            var values = new List<RuntimeLogValue>
            {
                RuntimeLogValue.Of("settingsId", (long)anchor.SettingsId),
                RuntimeLogValue.Of("level", anchor.Level.ToString()),
                RuntimeLogValue.Of("kind", anchor.Kind.ToString().ToLowerInvariant()),
                RuntimeLogValue.Quote("roomId", anchor.Area?.RoomId ?? ""),
                RuntimeLogValue.Quote("areaId", anchor.Area?.AreaId ?? ""),
                RuntimeLogValue.Quote("name", anchor.Name ?? ""),
                RuntimeLogValue.Quote("resource", resource),
                RuntimeLogValue.Of("spawnType", target == null ? -1 : (long)target.SpawnType),
                RuntimeLogValue.Of("reason", refusal ?? "resolved"),
                RuntimeLogValue.Quote("detail", detail ?? "")
            };
            if (target?.AuthorRoom is { } authorRoom)
            {
                values.Add(RuntimeLogValue.Quote("authorRoomId", authorRoom.RoomId));
                values.Add(RuntimeLogValue.Quote("authorRoomRevision", authorRoom.Revision));
                values.Add(RuntimeLogValue.Quote("authorRoomInstance", authorRoom.InstanceId ?? ""));
                values.Add(RuntimeLogValue.Quote("authorAreaId", target.AuthorAreaId ?? ""));
            }
            if (target?.NativeArea is { } nativeArea && !nativeArea.WasCollected)
                values.Add(RuntimeLogValue.Of("nativeArea", nativeArea.Pointer.ToInt64()));
            if (target?.RefNode is { } node && !node.WasCollected)
                values.Add(RuntimeLogValue.Of("nativeCourseNode", node.Pointer.ToInt64()));
            MapLog.Record(level, refusal == null ? ResolvedLogCode : RefusedLogCode,
                refusal == null ? "Wave anchor resolved against the generated level."
                    : "Wave anchor refused against the generated level.",
                values.ToArray());
        }
    }

    private static bool Refused(string code, string? reason, out string? refusal, out string? detail)
    {
        refusal = code;
        detail = reason;
        return false;
    }

    private static string Show(ZoneCoordinates coordinates)
        => coordinates.Dimension.ToString(CultureInfo.InvariantCulture) + "/"
            + coordinates.Layer.ToString(CultureInfo.InvariantCulture) + "/"
            + coordinates.Zone.ToString(CultureInfo.InvariantCulture);
}
