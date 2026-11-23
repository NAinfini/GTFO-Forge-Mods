using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using TMPro;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Records the world rectangle that ForgeMap actually drew for a room-authored label. This is an
/// informational readback only: it never validates placement or changes whether the label is shown.</summary>
internal static class StationSignPlacementReadback
{
    internal const string Code = "map.station-sign-placement";
    private const float GeometryTolerance = .0001f;
    private static readonly List<SeenSign> Seen = new();
    private static readonly Dictionary<(string Entity, string Reason, string? Detail), long> Counts = new();
    private static long _seenWorld = long.MinValue;
    private static bool _clearSummaryWritten;

    // A label is drawn again on every update of its text: the sign it already holds is refreshed in place, and the
    // room key and corner scratch are reused, so a redraw of a known sign allocates nothing here.
    private sealed class SeenSign
    {
        internal SeenSign(string entity, string room, Transform root, Vector3[] corners)
        { Entity = entity; Room = room; Root = root; Object = root.gameObject; Corners = (Vector3[])corners.Clone(); }
        internal readonly string Entity;
        internal string Room;
        internal Transform Root;
        internal GameObject Object;
        internal readonly Vector3[] Corners;
    }
    private readonly record struct RectangleShape(Vector3[] Corners, Bounds Bounds);
    private static readonly Vector3[] CornerScratch = new Vector3[4];
    private static readonly Dictionary<RoomLocator, string> RoomKeys = new(ReferenceEqualityComparer.Instance);

    internal static void Record(long world, EntityReference target, InteractionPoint? point, Component? owner,
        Transform root, TextMeshPro text)
        => RecordCore(world, target, point?.Room, point, owner, root, text);

    internal static void Record(long world, PlacedModelInstance model, Transform root, TextMeshPro text)
    {
        RoomLocator? room = null;
        var entity = EntityKinds.MapObject + ":model/unavailable";
        try
        {
            room = model.Model.Room;
            entity = EntityKinds.MapObject + ":" + PlacedModelAddress.Of(model.Model.Id);
            RecordCore(world,
                new EntityReference(entity, world, 1), room, null, null, root, text);
        }
        catch (Exception error) { RecordUnavailable(world, entity, room, "readback-exception", error.GetType().Name); }
    }

    internal static void Record(long world, TriggerZone zone, Transform root, TextMeshPro text)
    {
        RoomLocator? room = null;
        var entity = EntityKinds.MapObject + ":trigger-zone/unavailable";
        try
        {
            room = zone.Room;
            entity = EntityKinds.MapObject + ":" + TriggerZoneAddress.Of(zone.Id);
            RecordCore(world, new EntityReference(entity, world, 1), room, null, null, root, text);
        }
        catch (Exception error) { RecordUnavailable(world, entity, room, "readback-exception", error.GetType().Name); }
    }

    private static void RecordCore(long world, EntityReference target, RoomLocator? knownRoom, InteractionPoint? point,
        Component? owner, Transform root, TextMeshPro text)
    {
        var locator = knownRoom;
        try
        {
            if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
            BeginWorld(world);
            locator ??= Locator(target, point, owner);
            // Ordinary terminal, door, zone and item labels have no authored room locator. They remain valid labels
            // and do not produce a station-placement row; this observer never turns that into a refusal.
            if (locator == null) return;
            if (Rectangle(root, text) is not { } rectangle)
            {
                RecordUnavailable(world, target.Id, locator, "label-rectangle-invalid");
                return;
            }

            var room = locator.InstanceId ?? locator.RoomId;
            var (overlaps, overlap) = FindOverlaps(target.Id, RoomKey(locator), root, rectangle.Corners);
            // A clear sign does not need a full rectangle receipt. Announce readback once per world; retain geometry
            // for actual overlaps, grouped by entity and overlapping peer at the 1st, 10th, 100th ... observation.
            if (overlaps == 0)
            {
                if (!_clearSummaryWritten)
                {
                    _clearSummaryWritten = true;
                    MapLog.Record(RuntimeLogLevel.Info, Code, "Station sign placement readback active; overlap rectangles are reported separately.",
                        RuntimeLogValue.Of("summary", true), RuntimeLogValue.Of("signs", (long)Seen.Count));
                }
                return;
            }
            if (!Milestone(target.Id, "overlap", overlap, out var count)) return;
            var position = Vector(root.position); var rotation = Rotation(root.rotation);
            var corner0 = Vector(rectangle.Corners[0]); var corner1 = Vector(rectangle.Corners[1]);
            var corner2 = Vector(rectangle.Corners[2]); var corner3 = Vector(rectangle.Corners[3]);
            MapLog.Record(RuntimeLogLevel.Info, Code, "Station sign world rectangle read back.",
                RuntimeLogValue.Quote("entity", target.Id), RuntimeLogValue.Quote("room", room),
                RuntimeLogValue.Quote("position", position), RuntimeLogValue.Quote("rotation", rotation),
                RuntimeLogValue.Quote("worldCorner0", corner0),
                RuntimeLogValue.Quote("worldCorner1", corner1),
                RuntimeLogValue.Quote("worldCorner2", corner2),
                RuntimeLogValue.Quote("worldCorner3", corner3),
                RuntimeLogValue.Quote("worldMin", Vector(rectangle.Bounds.min)),
                RuntimeLogValue.Quote("worldMax", Vector(rectangle.Bounds.max)),
                RuntimeLogValue.Of("overlaps", (long)overlaps), RuntimeLogValue.Quote("overlap", overlap),
                RuntimeLogValue.Of("count", count));
        }
        catch (Exception error)
        {
            RecordUnavailable(world, target.Id, locator, "readback-exception", error.GetType().Name);
        }
    }

    private static RoomLocator? Locator(EntityReference target, InteractionPoint? point, Component? owner)
    {
        if (point != null) return point.Room;
        if (owner != null && PlacedModelHitRegistry.Resolve(owner) is { } hit) return hit.Model.Room;
        if (MapHost.Session?.TriggerZones?.LabelReceiver(target) is { } zone) return zone.Room;
        const string prefix = EntityKinds.MapObject + ":";
        if (!target.Id.StartsWith(prefix, StringComparison.Ordinal)
            || PlacedModelAddress.TryParse(target.Id[prefix.Length..]) is not { } address) return null;
        return MapHost.Session?.MapObjects?.PlacedModels.ById(address.Key)?.Model.Room;
    }

    private static void RecordUnavailable(long world, string entity, RoomLocator? locator, string reason, string? exceptionType = null)
    {
        if (!MapLog.Logs(RuntimeLogLevel.Info)) return;
        BeginWorld(world);
        if (!Milestone(entity, reason, exceptionType, out var count)) return;
        var room = RuntimeLogValue.Quote("room", locator == null ? "unavailable" : locator.InstanceId ?? locator.RoomId);
        if (exceptionType == null)
            MapLog.Record(RuntimeLogLevel.Info, Code, "Station sign world rectangle readback unavailable.",
                RuntimeLogValue.Quote("entity", entity), room, RuntimeLogValue.Of("reason", reason), RuntimeLogValue.Of("count", count));
        else
            MapLog.Record(RuntimeLogLevel.Info, Code, "Station sign world rectangle readback unavailable.",
                RuntimeLogValue.Quote("entity", entity), room, RuntimeLogValue.Of("reason", reason),
                RuntimeLogValue.Quote("exception", exceptionType), RuntimeLogValue.Of("count", count));
    }

    private static void BeginWorld(long world)
    {
        if (_seenWorld == world) return;
        Seen.Clear(); Counts.Clear(); RoomKeys.Clear(); _clearSummaryWritten = false; _seenWorld = world;
    }

    private static bool Milestone(string entity, string reason, string? detail, out long count)
    {
        var key = (entity, reason, detail);
        Counts.TryGetValue(key, out count);
        Counts[key] = ++count;
        var rest = count;
        while (rest % 10 == 0) rest /= 10;
        return rest == 1;
    }

    private static string RoomKey(RoomLocator locator)
    {
        if (!RoomKeys.TryGetValue(locator, out var key)) RoomKeys.Add(locator, key = RoomKeyText(locator));
        return key;
    }

    private static string RoomKeyText(RoomLocator locator)
        => locator.Dimension.ToString(CultureInfo.InvariantCulture) + "/"
            + locator.Layer.ToString(CultureInfo.InvariantCulture) + "/"
            + locator.LocalIndex.ToString(CultureInfo.InvariantCulture) + "/"
            + (locator.InstanceId ?? locator.RoomId);

    private static RectangleShape? Rectangle(Transform root, TextMeshPro text)
    {
        var size = text.rectTransform.sizeDelta;
        var pivot = text.rectTransform.pivot;
        var child = text.transform;
        if (!ForgeCore.Vec.Finite(root.position) || !ForgeCore.Vec.Finite(root.rotation) || !ForgeCore.Vec.Finite(root.lossyScale)
            || !float.IsFinite(size.x) || !float.IsFinite(size.y) || size.x <= 0 || size.y <= 0
            || !float.IsFinite(pivot.x) || !float.IsFinite(pivot.y) || !ForgeCore.Vec.Finite(child.localPosition)
            || !ForgeCore.Vec.Finite(child.localScale) || !ForgeCore.Vec.Finite(child.localRotation)) return null;

        var childMatrix = Matrix4x4.TRS(child.localPosition, child.localRotation, child.localScale);
        var rootMatrix = Matrix4x4.TRS(root.position, root.rotation, root.lossyScale);
        var corners = CornerScratch;
        Bounds bounds = default;
        for (var index = 0; index < corners.Length; index++)
        {
            var x = (index is 1 or 2 ? 1f : 0f) - pivot.x;
            var y = (index >= 2 ? 1f : 0f) - pivot.y;
            var world = rootMatrix.MultiplyPoint3x4(childMatrix.MultiplyPoint3x4(new Vector3(x * size.x, y * size.y, 0)));
            if (!ForgeCore.Vec.Finite(world)) return null;
            corners[index] = world;
            if (index == 0) bounds = new Bounds(world, Vector3.zero); else bounds.Encapsulate(world);
        }
        return new RectangleShape(corners, bounds);
    }

    private static (int Count, string First) FindOverlaps(string entity, string room, Transform root,
        Vector3[] corners)
    {
        // The entity's own earlier sign is the one being redrawn: it is not an overlap, and it keeps its place, so the
        // other signs' order — which decides the reported first overlap — is the same as when it was dropped and re-added.
        SeenSign? own = null;
        for (var index = Seen.Count - 1; index >= 0; index--)
        {
            var sign = Seen[index];
            if (sign.Root == null || sign.Root.WasCollected || !sign.Object.activeInHierarchy) { Seen.RemoveAt(index); continue; }
            if (sign.Entity != entity) continue;
            if (own != null) Seen.Remove(own);
            own = sign;
        }
        var count = 0;
        var first = "none";
        foreach (var sign in Seen)
            if (!ReferenceEquals(sign, own) && sign.Room == room && Intersects(corners, sign.Corners))
            {
                count++;
                if (first == "none") first = sign.Entity;
            }
        if (own == null) Seen.Add(new SeenSign(entity, room, root, corners));
        else
        {
            own.Room = room;
            if (own.Root.Pointer != root.Pointer) { own.Root = root; own.Object = root.gameObject; }
            Array.Copy(corners, own.Corners, own.Corners.Length);
        }
        return (count, first);
    }

    /// <summary>Separating-axis test for two zero-thickness convex rectangles in 3D.</summary>
    private static bool Intersects(Vector3[] left, Vector3[] right)
    {
        var leftEdge0 = left[1] - left[0]; var leftEdge1 = left[3] - left[0];
        var rightEdge0 = right[1] - right[0]; var rightEdge1 = right[3] - right[0];
        var leftNormal = Cross(leftEdge0, leftEdge1);
        var rightNormal = Cross(rightEdge0, rightEdge1);
        Span<Vector3> axes = stackalloc Vector3[10];
        axes[0] = leftNormal; axes[1] = rightNormal;
        axes[2] = Cross(leftNormal, leftEdge0); axes[3] = Cross(leftNormal, leftEdge1);
        axes[4] = Cross(rightNormal, rightEdge0); axes[5] = Cross(rightNormal, rightEdge1);
        axes[6] = Cross(leftEdge0, rightEdge0); axes[7] = Cross(leftEdge0, rightEdge1);
        axes[8] = Cross(leftEdge1, rightEdge0); axes[9] = Cross(leftEdge1, rightEdge1);

        foreach (var axis in axes)
        {
            var length = axis.magnitude;
            if (!float.IsFinite(length) || length <= GeometryTolerance) continue;
            var direction = axis * (1f / length);
            Project(left, direction, out var leftMin, out var leftMax);
            Project(right, direction, out var rightMin, out var rightMax);
            if (leftMax < rightMin - GeometryTolerance || rightMax < leftMin - GeometryTolerance) return false;
        }
        return true;
    }

    private static void Project(Vector3[] points, Vector3 axis, out float minimum, out float maximum)
    {
        minimum = maximum = ForgeCore.Vec.Dot(points[0], axis);
        for (var index = 1; index < points.Length; index++)
        {
            var value = ForgeCore.Vec.Dot(points[index], axis);
            minimum = MathF.Min(minimum, value);
            maximum = MathF.Max(maximum, value);
        }
    }

    private static Vector3 Cross(Vector3 left, Vector3 right)
        => new(left.y * right.z - left.z * right.y, left.z * right.x - left.x * right.z,
            left.x * right.y - left.y * right.x);
    private static string Vector(Vector3 value) => string.Format(CultureInfo.InvariantCulture,
        "[{0:0.###},{1:0.###},{2:0.###}]", value.x, value.y, value.z);
    private static string Rotation(Quaternion value) => string.Format(CultureInfo.InvariantCulture,
        "[{0:0.####},{1:0.####},{2:0.####},{3:0.####}]", value.x, value.y, value.z, value.w);
}
