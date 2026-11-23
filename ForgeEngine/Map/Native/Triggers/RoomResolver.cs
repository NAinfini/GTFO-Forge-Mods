using System;
using System.Collections.Generic;
using AIGraph;
using LevelGeneration;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>
/// The three coordinates of one zone of the level being played, as the map-object addresses already spell them and
/// as an authored room locator carries them. Every search is scoped by them: the authored zone id cannot be resolved
/// on this side, so the document carries the level's own coordinates and the search runs inside exactly that zone.
/// The scope carries the coordinates rather than a zone object, because the object belongs to one world and a scope
/// outliving it would be a reference into a level that ended.
/// </summary>
internal readonly struct RoomScope
{
    public RoomScope(int dimension, int layer, int localZoneIndex)
    {
        Dimension = dimension;
        Layer = layer;
        LocalZoneIndex = localZoneIndex;
    }

    /// <summary>The zone's dimension, `LG_Zone.m_dimensionIndex`.</summary>
    public int Dimension { get; }
    /// <summary>The zone's layer type, `LG_Zone.m_layer.m_type`.</summary>
    public int Layer { get; }
    /// <summary>The zone's index inside its layer, `LG_Zone.LocalIndex`.</summary>
    public int LocalZoneIndex { get; }
}

/// <summary>
/// One generated room of this level: the geomorph the level really built, the zone it stands in, where its own
/// transform put it, and the course node the area itself carries. The pose is the room's world pose, which is the
/// only frame an authored room-local position and rotation can be read in — and the identity is native instance
/// ids, so a caller can find the same object again in its own traversal instead of matching a name. The area's own
/// course node travels with the hit because it is the fallback a spawn point resolves through when the navigation
/// graph has no node at the exact point.
/// </summary>
internal sealed class RoomHit
{
    internal RoomHit(int geomorphInstanceId, int zoneInstanceId, int dimension, int layer, int localZoneIndex,
        double[] position, double[] rotation, AIG_CourseNode? areaCourseNode, LG_Area? area = null)
    {
        GeomorphInstanceId = geomorphInstanceId;
        ZoneInstanceId = zoneInstanceId;
        Dimension = dimension;
        Layer = layer;
        LocalZoneIndex = localZoneIndex;
        Position = Array.AsReadOnly(position);
        Rotation = Array.AsReadOnly(rotation);
        AreaCourseNode = areaCourseNode;
        Area = area;
    }

    /// <summary>The geomorph's own instance id, the identity a report names it by.</summary>
    public int GeomorphInstanceId { get; }
    /// <summary>The instance id of the zone the room stands in.</summary>
    public int ZoneInstanceId { get; }
    public int Dimension { get; }
    public int Layer { get; }
    public int LocalZoneIndex { get; }
    /// <summary>The room's world position in metres.</summary>
    public IReadOnlyList<double> Position { get; }
    /// <summary>The room's world rotation, a unit quaternion in `x y z w` order.</summary>
    public IReadOnlyList<double> Rotation { get; }
    /// <summary>The course node the matched area carries, or null when the area holds none. It is the fallback a
    /// point inside the room resolves through, never a substitute for the graph's own answer at the point.</summary>
    internal AIG_CourseNode? AreaCourseNode { get; }
    /// <summary>The exact generated area that owns the room-part spawner context. Native placed models use it
    /// only to invoke the same LG_PrefabSpawner build path as the room itself.</summary>
    internal LG_Area? Area { get; }

    /// <summary>The room's world pose, the value an authored room-local point is composed with.</summary>
    internal RoomPose Pose => RoomPose.Of(ToArray(Position), ToArray(Rotation));

    private static double[] ToArray(IReadOnlyList<double> values)
    {
        var copy = new double[values.Count];
        for (var i = 0; i != values.Count; i++) copy[i] = values[i];
        return copy;
    }
}

/// <summary>
/// What the level answered for one authored room reference: the rooms that reference names, or the one reason the
/// question could not be answered. The rooms are the answer, not a guess: one room is the reference's room, and a
/// refusal is never a pick between candidates. The three reasons a level can refuse for are told apart, because
/// "the zone is not there", "no room of this source is in it" and "several rooms of this source are in it" are
/// three different things for an author to fix.
/// </summary>
internal sealed class RoomResolution
{
    /// <summary>The refusal of a world with no generated level: no room of it exists to be named.</summary>
    public const string NoWorld = "no-world";
    /// <summary>The refusal of a prefab path the game's own asset loader holds no object for. The identity an
    /// authored reference carries is the loaded prefab object, so a path that loads nothing names nothing —
    /// a prefab's display name is not that identity and is never substituted for it.</summary>
    public const string PrefabNotLoaded = "prefab-not-loaded";
    /// <summary>The refusal of coordinates the level has no zone for: nothing can be searched, so nothing is
    /// claimed — not even that the room is absent.</summary>
    public const string ZoneUnresolved = "zone-unresolved";
    /// <summary>The refusal of a zone that holds no room generated from this reference's prefab object.</summary>
    public const string NoRoom = "no-room";
    /// <summary>The refusal of a zone that holds several: the same prefab placed twice names no single room, and
    /// the rooms it found are carried so a report can name them.</summary>
    public const string MultipleRooms = "multiple-rooms";
    /// <summary>The refusal of a point no course node can be read for. A spawn without a node is a spawn the
    /// navigation graph and the culling system cannot place, so it is refused instead of being attempted.</summary>
    public const string NoCourseNode = "no-course-node";

    internal RoomResolution(string? refusal, IReadOnlyList<RoomHit> rooms)
    {
        Refusal = refusal;
        Rooms = rooms;
    }

    /// <summary>Why no room could be named at all, or null when the searched zone answered with exactly one room.</summary>
    public string? Refusal { get; }

    /// <summary>Every room of the searched zone whose source prefab is this reference's prefab object, in the
    /// level's own area order. Empty on every refusal that observed none; a `multiple-rooms` refusal carries the
    /// rooms it counted. Exactly one room and no refusal is the answer this reference names.</summary>
    public IReadOnlyList<RoomHit> Rooms { get; }

    /// <summary>The one room this reference names, or null when the search found none or more than one.</summary>
    public RoomHit? Room => Refusal == null && Rooms.Count == 1 ? Rooms[0] : null;
}

/// <summary>
/// The one resolver that can name an authored room in the level the game generated, shared by every reader of the
/// same reference: the trigger zones that compose a zone's room-local pose into world coordinates, the fixed items
/// and fixed enemies that place themselves in a room, and the development diagnostics that report which native
/// object an authored reference resolved to.
///
/// A room's identity is the prefab object the game's own asset loader returned for the reference's `Assets/`
/// path, compared by object reference against the prefab each generated geomorph was created from
/// (`LG_Geomorph.m_geoPrefab`). A name is never compared: a prefab's display name is not the original asset
/// identity, which is exactly why the reference carries the whole path and why this is a reference comparison.
///
/// The search is scoped by the locator's own native zone coordinates, asked for through the one zone table the
/// map-object addresses already use (`ZoneIndex`), so "the room a reference names" is answered from the same view
/// of the level the door and terminal addresses are built from, and never from a second walk of the native graph.
/// The scope is required: an authored zone id cannot be resolved on this side, and a search across the whole level
/// would refuse every zone whose room prefab the level happened to place twice. Within its zone the answer must be
/// unique: zero rooms and several rooms are both a refusal, because a room-local pose composed with the wrong
/// room's transform is an object at the wrong place, and a wrong place is worse than a missing one.
///
/// The course node is the second half of the same question: a point inside a room is spawnable exactly when the
/// navigation graph has a node there, and the game's own spawn paths ask that question with
/// `AIG_CourseNode.TryGetCourseNode` before they build anything. The area's own node is the fallback, because a
/// point a metre outside every node still stands in the area the level built.
/// </summary>
internal static class RoomResolver
{
    /// <summary>The sample distance the game's own spawn entries ask the course graph with, so a point this
    /// resolver accepts is a point they accept.</summary>
    private const float CourseNodeSampleDistance = 1f;

    /// <summary>Every room of one zone whose source prefab is the object the reference's path loads. The zone is
    /// the locator's own native coordinates, so this is the answer for a caller that can name the zone the
    /// reference claims — which every authored room locator now does.</summary>
    public static RoomResolution Resolve(long worldEpoch, string sourcePrefab, RoomScope zone)
        => Search(worldEpoch, sourcePrefab, zone);


    /// <summary>The room's world placement one authored locator names in this world, or the one reason this level
    /// cannot name it. The pose is the room's own; an authored room-local point is composed with it by the caller,
    /// because the composition belongs to the placed object and not to the search.</summary>
    internal static bool TryPlace(long worldEpoch, RoomLocator locator, out RoomHit? room, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var answer = Search(worldEpoch, locator.SourcePrefab,
            new RoomScope(locator.Dimension, locator.Layer, locator.LocalIndex), locator);
        room = answer.Room;
        refusal = answer.Refusal;
        return room != null;
    }

    /// <summary>Resolves one generated author's Area through the same room receipt used by fixed placement and
    /// map-object identity. The selector is the authored room instance plus authored Area id; its locked room and
    /// Area revisions remain on the receipt. Native Area letters, names and list positions are never consulted.</summary>
    internal static bool TryPlaceArea(long worldEpoch, MapLevelReference level, RoomScope scope,
        string roomInstance, string areaId, out RoomHit? room, out string? refusal)
    {
        room = null;
        refusal = null;
        if (worldEpoch == 0) { refusal = RoomResolution.NoWorld; return false; }
        var zone = ZoneIndex.Current(worldEpoch).ZoneAt(
            new ZoneCoordinates(scope.Dimension, scope.Layer, scope.LocalZoneIndex));
        if (zone == null) { refusal = RoomResolution.ZoneUnresolved; return false; }
        var area = GeneratedRoomRegistry.FindArea(worldEpoch, level, zone, roomInstance, areaId,
            out _, out refusal);
        if (area == null) return false;
        var geomorph = area.m_geomorph;
        if (geomorph == null || geomorph.WasCollected) { refusal = "authored-area-stale"; return false; }
        room = Hit(geomorph, zone, scope.Dimension, scope.Layer, scope.LocalZoneIndex, area);
        if (room != null) return true;
        refusal = "authored-area-stale";
        return false;
    }

    /// <summary>The world pose of one authored room locator, or null when this level cannot name exactly one room
    /// for it. This is the production answer a placement composes its own document pose with, and it searches the
    /// zone the locator itself carries.</summary>
    internal static RoomPose? Lookup(long worldEpoch, RoomLocator room)
        => TryPlace(worldEpoch, room, out var hit, out _) ? hit!.Pose : null;

    /// <summary>One fixed row's world pose and course node: its room in this world, the row's point composed with
    /// that room, and the node the point belongs to. Otherwise null, with the room's own refusal or
    /// <see cref="RoomResolution.NoCourseNode"/>.</summary>
    internal static (RoomPose World, AIG_CourseNode Node)? PlaceRow(long worldEpoch, FixedRow row, out string? refusal)
    {
        if (!TryPlace(worldEpoch, row.Locator, out var room, out refusal)) return null;
        var world = row.WorldPose(room!.Pose);
        if (!TryCourseNode(row.Locator.Dimension, world.Position, room.AreaCourseNode, out var node) || node == null)
        { refusal = RoomResolution.NoCourseNode; return null; }
        return (world, node);
    }

    /// <summary>
    /// The course node one world point belongs to, or the one reason it has none: the navigation graph's own
    /// answer at the point first, and the matched area's own node second. Both are the level's answers — the first
    /// is the node the game's own spawn entries would find, the second is the room the authored reference named —
    /// and a point neither can place is refused by name rather than spawned at a node nobody read.
    /// </summary>
    internal static bool TryCourseNode(int dimension, IReadOnlyList<double> worldPosition, AIG_CourseNode? areaNode,
        out AIG_CourseNode? node)
    {
        ArgumentNullException.ThrowIfNull(worldPosition);
        node = null;
        if (worldPosition.Count != 3) return false;
        var point = new Vector3((float)worldPosition[0], (float)worldPosition[1], (float)worldPosition[2]);
        if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z)) return false;
        if (ForgeCore.Level.CourseNodeIn(dimension, point, CourseNodeSampleDistance, out var sampled)) { node = sampled; return true; }
        if (areaNode != null && !areaNode.WasCollected) { node = areaNode; return true; }
        return false;
    }

    /// <summary>One search: the loaded prefab object's identity, then every zone the scope names — one zone, or the
    /// whole level when no scope was given — and inside it every generated area whose geomorph was created from
    /// that object. A geomorph is one room however many areas it has, so it is counted once. Coordinates the level
    /// has no zone for are their own refusal when a scope named them: the level was never asked the room question
    /// at all. A zone whose coordinates do not read is skipped in the whole-level search exactly as it is in a
    /// scoped one, because a room nothing can address is not a room an authored point may be placed in.</summary>
    private static RoomResolution Search(long worldEpoch, string sourcePrefab, RoomScope? zone, RoomLocator? locator = null)
    {
        if (worldEpoch == 0) return Refused(RoomResolution.NoWorld);
        if (string.IsNullOrEmpty(sourcePrefab)) return Refused(RoomResolution.PrefabNotLoaded);
        GameObject? prefab;
        // A Forge room template joins that dictionary only while the level that builds it generates, and a vanilla
        // geomorph only while its shard is loaded, so an unregistered path is the named refusal.
        prefab = ForgeCore.GameAssets.Get<GameObject>(sourcePrefab);
        if (prefab == null) return Refused(RoomResolution.PrefabNotLoaded);
        var identity = prefab.Pointer;
        var index = ZoneIndex.Current(worldEpoch);
        var rooms = new List<RoomHit>();
        var counted = new HashSet<int>();
        var found = false;
        var currentLevel = locator?.InstanceId == null ? null : LevelIdentity.Read();
        var candidates = zone is { } requested
            ? index.Zones(new ZoneCoordinates(requested.Dimension, requested.Layer, requested.LocalZoneIndex))
            : index.Zones();
        foreach (var candidate in candidates)
        {
            if (!index.TryCoordinates(candidate, out var coordinates)) continue;
            if (zone is { } scope && (coordinates.Dimension != scope.Dimension || coordinates.Layer != scope.Layer
                || coordinates.Zone != scope.LocalZoneIndex)) continue;
            found = true;
            var authored = locator?.InstanceId == null ? null
                : GeneratedRoomRegistry.Find(worldEpoch, currentLevel, candidate, locator);
            if (locator?.InstanceId != null && authored == null) continue;
            var areas = candidate.m_areas;
            if (areas == null) continue;
            for (var i = 0; i != areas.Count; i++)
            {
                var area = areas[i];
                var geomorph = area is { } held && !held.WasCollected ? held.m_geomorph : null;
                if (geomorph == null || geomorph.WasCollected || !From(geomorph, identity)) continue;
                if (authored != null && geomorph.Pointer != authored.Pointer) continue;
                if (!counted.Add(geomorph.GetInstanceID())) continue;
                if (Hit(geomorph, candidate, coordinates.Dimension, coordinates.Layer, coordinates.Zone, area) is { } hit)
                    rooms.Add(hit);
            }
        }
        if (zone is not null && !found) return Refused(RoomResolution.ZoneUnresolved);
        if (rooms.Count == 0) return Refused(RoomResolution.NoRoom);
        var answer = Array.AsReadOnly(rooms.ToArray());
        return new RoomResolution(rooms.Count > 1 ? RoomResolution.MultipleRooms : null, answer);
    }

    /// <summary>Whether this geomorph was created from that exact prefab object.</summary>
    private static bool From(LG_Geomorph geomorph, IntPtr identity)
    {
        var prefab = geomorph.m_geoPrefab;
        return prefab != null && !prefab.WasCollected && prefab.Pointer == identity;
    }

    private static RoomHit? Hit(LG_Geomorph geomorph, LG_Zone zone, int dimension, int layer, int localZoneIndex,
        LG_Area area)
    {
        var transform = geomorph.transform;
        if (transform == null || transform.WasCollected || area == null || area.WasCollected) return null;
        var position = transform.position;
        var rotation = transform.rotation;
        var courseNode = area.m_courseNode;
        return new RoomHit(geomorph.GetInstanceID(), zone.GetInstanceID(), dimension, layer, localZoneIndex,
            new[] { (double)position.x, (double)position.y, (double)position.z },
            new[] { (double)rotation.x, (double)rotation.y, (double)rotation.z, (double)rotation.w },
            courseNode == null || courseNode.WasCollected ? null : courseNode, area);
    }

    private static RoomResolution Refused(string refusal)
        => new(refusal, Array.Empty<RoomHit>());
}
