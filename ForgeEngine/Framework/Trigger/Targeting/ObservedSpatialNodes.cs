using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEngine.Framework;
using ForgeEngine.Trigger.Pure;

namespace ForgeEngine.Trigger.Targeting;

/// <summary>The volumetric members of the catalog's `query_shape` set this build has an honest point test for, in
/// the set's own order. The set's other members — `room`, `area`, `zone` and `path` — name a region this row carries
/// no measurement for, so a plan that selects one is refused by name instead of being mapped onto the volume it most
/// resembles.</summary>
internal enum ObservedVolumeShape { Sphere, Cone, Cylinder, Capsule, Box }

/// <summary>
/// One measured volume: the shape a plan selected and only the sizes that shape defines. A sphere carries a radius,
/// a cylinder and a capsule a radius and a height, and a box its own three half sizes along the world axes. A box
/// therefore never carries a radius and no scalar stands in for one of its axes, and a shape whose sizes are not
/// these cannot be assembled at all: the factories are the only way to build a volume, so a caller cannot hand the
/// selection a height it happens to have for a sphere or a radius it happens to have for a box.
///
/// The sizes are validated where the volume is measured (<see cref="ObservedSpatialNodes.Overlap"/>), before any
/// position is read, and only the sizes the selected shape defines are looked at.
/// </summary>
internal readonly struct ObservedVolume
{
    private ObservedVolume(ObservedVolumeShape shape, double radius, double height, IReadOnlyList<double>? extents,
        double angle = 0d, IReadOnlyList<double>? direction = null)
    {
        Shape = shape; Radius = radius; Height = height; Extents = extents; Angle = angle; Direction = direction;
    }

    internal ObservedVolumeShape Shape { get; }
    /// <summary>The sphere's, the cylinder's and the capsule's own outer size, and the cone's length from its apex;
    /// zero for a box.</summary>
    internal double Radius { get; }
    /// <summary>The cone's largest angle, in degrees, between its axis and the line to a point it keeps; zero for
    /// every other shape.</summary>
    internal double Angle { get; }
    /// <summary>The cone's axis as the plan handed it (normalised where it is measured); null for every other
    /// shape.</summary>
    internal IReadOnlyList<double>? Direction { get; }
    /// <summary>The cylinder's length and the capsule's total extent along Y, caps included; zero for the other
    /// two shapes.</summary>
    internal double Height { get; }
    /// <summary>The box's three half sizes (x, y, z); null for the other three shapes.</summary>
    internal IReadOnlyList<double>? Extents { get; }

    /// <summary>A sphere of <paramref name="radius"/> metres; it carries no height and no half sizes.</summary>
    public static ObservedVolume Sphere(double radius) => new(ObservedVolumeShape.Sphere, radius, 0d, null);

    /// <summary>A cone whose apex is the centre, opening along <paramref name="direction"/> for
    /// <paramref name="length"/> metres: a point is inside when it is no farther than the length from the apex and
    /// the line to it is at most <paramref name="angle"/> degrees off the axis. The length is measured from the
    /// apex, so the far cap is a sphere segment rather than a flat disc.</summary>
    public static ObservedVolume Cone(double length, double angle, IReadOnlyList<double> direction)
        => new(ObservedVolumeShape.Cone, length, 0d, null, angle, direction);

    /// <summary>A cylinder of <paramref name="radius"/> metres around a <paramref name="height"/>-long Y axis.</summary>
    public static ObservedVolume Cylinder(double radius, double height)
        => new(ObservedVolumeShape.Cylinder, radius, height, null);

    /// <summary>A capsule whose total extent along Y is <paramref name="height"/>, with <paramref name="radius"/>
    /// hemispheres: the straight segment between them is <c>height - 2·radius</c> long, so an axis no longer than
    /// the diameter degenerates to the sphere.</summary>
    public static ObservedVolume Capsule(double radius, double height)
        => new(ObservedVolumeShape.Capsule, radius, height, null);

    /// <summary>A world-axis aligned box whose three half sizes are the plan's own `extents`, one per axis.</summary>
    public static ObservedVolume Box(IReadOnlyList<double> extents)
        => new(ObservedVolumeShape.Box, 0d, 0d, extents);
}

/// <summary>Current point-position selection over the references a step already holds. No discovery, LOS or
/// collision certificate.
///
/// Every read goes through the step's own <see cref="RuntimeQuerySession"/>: one budgeted call for the whole
/// explicit candidate set, and a set the kernel could not observe completely is refused rather than ranked, so a
/// selector can never answer with the best of the half it happened to see.</summary>
internal static class ObservedSpatialNodes
{
    /// <summary>The existing cone angle and direction threshold; length measurements use the shared Single bound.</summary>
    private const double MinimumConeParameter = 0.000001;

    public static IReadOnlyList<EntityReference> Overlap(RuntimeQuerySession session,
        IReadOnlyList<EntityReference> candidates, IReadOnlyList<double> center, ObservedVolume volume)
    {
        // The shape is dispatched first, so only the sizes it defines are read and validated: a sphere is asked for
        // its radius and never for a height, a box for its own three half sizes and never for a radius. An
        // unsupported shape is refused here, before an unrelated value could be read or a position observed.
        var (radius, height, extents, axis, cosine) = Measure(volume);
        var point = Point(center);
        var snapshots = Observe(session, candidates);
        // Every shape but the cone is world-axis aligned, and every shape is closed: a position exactly on the
        // boundary is inside.
        Span<int> matched = stackalloc int[snapshots.Count];
        var matchedCount = 0;
        for (var index = 0; index < snapshots.Count; index++)
        {
            var row = snapshots[index];
            var inside = volume.Shape switch
            {
                ObservedVolumeShape.Sphere => Distance(row.Position, point) <= radius,
                ObservedVolumeShape.Cone => InsideCone(row.Position, point, radius, axis, cosine),
                ObservedVolumeShape.Cylinder => Horizontal(row.Position, point) <= radius && Delta(row.Position[1], point[1]) <= height / 2d,
                // Capsule: `height` is the total extent along Y, so the straight segment is `height - 2·radius` long and an axis no longer than the diameter degenerates to the sphere.
                ObservedVolumeShape.Capsule => Magnitude(Delta(row.Position[0], point[0]),
                    Math.Max(Delta(row.Position[1], point[1]) - Math.Max(height / 2d - radius, 0), 0), Delta(row.Position[2], point[2])) <= radius,
                // Box: the remaining defined shape, one independent half size per world axis — x, y and z are each
                // measured against their own `extents` entry and none of the three stands in for another.
                _ => Delta(row.Position[0], point[0]) <= extents[0] && Delta(row.Position[1], point[1]) <= extents[1] && Delta(row.Position[2], point[2]) <= extents[2]
            };
            if (inside) matched[matchedCount++] = index;
        }
        var ordered = IdentityOrder(snapshots, matched[..matchedCount]);
        var selected = new EntityReference[ordered.Length];
        for (var index = 0; index < selected.Length; index++) selected[index] = snapshots[ordered[index].Index].Ref;
        return Array.AsReadOnly(selected);
    }
    public static ReferenceSelection Nearest(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> center, int count) => OrderedDistance(session, candidates, center, count, false);
    public static ReferenceSelection Farthest(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        IReadOnlyList<double> center, int count) => OrderedDistance(session, candidates, center, count, true);
    private static ReferenceSelection OrderedDistance(RuntimeQuerySession session,
        IReadOnlyList<EntityReference> candidates, IReadOnlyList<double> center, int count, bool farthest)
    {
        if (count < 1 || count > 256)
            throw new RuntimeContractException("pure-selection-count", "Selection count must be in [1, 256].");
        var point = Point(center); var snapshots = Observe(session, candidates);
        Span<int> indices = stackalloc int[snapshots.Count];
        Span<double> distances = stackalloc double[snapshots.Count];
        for (var index = 0; index < snapshots.Count; index++)
        {
            indices[index] = index;
            distances[index] = Distance(snapshots[index].Position, point);
        }
        // Measure every position before validating identity keys, as in the original distance projection.
        var ordered = IdentityOrder(snapshots, indices);
        Span<DistanceIndex> ranked = stackalloc DistanceIndex[snapshots.Count];
        for (var index = 0; index < ranked.Length; index++)
        {
            var distance = distances[ordered[index].Index];
            ranked[index] = new DistanceIndex(farthest ? -distance : distance, index);
        }
        ranked.Sort();
        var selected = new EntityReference[Math.Min(count, snapshots.Count)];
        for (var index = 0; index < selected.Length; index++)
            selected[index] = snapshots[ordered[ranked[index].Index].Index].Ref;
        return new ReferenceSelection(selected, candidates.Count, snapshots.Count, count);
    }
    public static ReferenceSelection Chain(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference start, int maximumHops, double radius)
    {
        ArgumentNullException.ThrowIfNull(candidates); RuntimeEntityReferences.Validate(start);
        if (maximumHops < 1 || maximumHops > 64)
            throw new RuntimeContractException("spatial-hop-budget", "Chain maximum hops must be in [1, 64].");
        Length(radius);
        var requested = new List<EntityReference>(candidates);
        if (!requested.Contains(start)) requested.Add(start);
        // The start is one more reference the step must be allowed to read, and its count is what the query budget
        // charges, so the explicit input is bounded with it rather than after it.
        if (requested.Count > RuntimeKernel.MaximumEntityReferencesPerQuery)
            throw new RuntimeContractException("entity-query-budget", "Explicit candidate query exceeds the Runtime limit.");
        var snapshots = Observe(session, requested, start);
        RuntimeEntitySnapshot? origin = null;
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (snapshots[index].Ref != start) continue;
            if (origin != null) throw new InvalidOperationException("Sequence contains more than one matching element");
            origin = snapshots[index];
        }
        if (origin == null) throw new InvalidOperationException("Sequence contains no matching element");
        Span<int> remaining = stackalloc int[snapshots.Count - 1];
        var available = 0;
        for (var index = 0; index < snapshots.Count; index++)
            if (snapshots[index].Ref != start) remaining[available++] = index;
        Span<bool> used = stackalloc bool[snapshots.Count];
        used.Clear();
        Span<int> hops = stackalloc int[Math.Min(available, maximumHops)];
        var keys = new string?[snapshots.Count];
        var selectedCount = 0;
        var from = origin.Position;
        while (selectedCount < hops.Length)
        {
            var next = -1;
            var nextDistance = 0d;
            for (var index = 0; index < available; index++)
            {
                var candidate = remaining[index];
                if (used[candidate]) continue;
                // The original projection streams into First: read this distance and its required key before
                // measuring the next candidate, so a later overflow cannot hide an earlier invalid identity.
                var distance = Distance(snapshots[candidate].Position, from);
                // OrderBy(distance).ThenBy(identity).First() reads the first key, then only a new minimum's
                // or an equal-distance candidate's key. Preserve that validation boundary, caching only keys
                // already read in this query; a farther malformed identity must not become a new refusal.
                if (next < 0 || distance < nextDistance)
                {
                    keys[candidate] ??= ReferenceCollections.OrderKey(snapshots[candidate].Ref);
                    next = candidate;
                    nextDistance = distance;
                }
                else if (distance == nextDistance)
                {
                    keys[candidate] ??= ReferenceCollections.OrderKey(snapshots[candidate].Ref);
                    if (StringComparer.Ordinal.Compare(keys[candidate], keys[next]) < 0) next = candidate;
                }
            }
            if (nextDistance > radius) break;
            hops[selectedCount++] = next;
            used[next] = true;
            from = snapshots[next].Position;
        }
        var selected = new EntityReference[selectedCount];
        for (var index = 0; index < selected.Length; index++) selected[index] = snapshots[hops[index]].Ref;
        return new ReferenceSelection(selected, candidates.Count, available, maximumHops);
    }

    // The query is complete and bounded before these local indices are allocated. Keys are constructed once
    // per relevant snapshot; the original observation index preserves stable ordering even for equal keys.
    private readonly record struct IdentityIndex(string Key, int Index) : IComparable<IdentityIndex>
    {
        public int CompareTo(IdentityIndex other)
        {
            var compared = StringComparer.Ordinal.Compare(Key, other.Key);
            return compared != 0 ? compared : Index.CompareTo(other.Index);
        }
    }

    private readonly record struct DistanceIndex(double Distance, int Index) : IComparable<DistanceIndex>
    {
        public int CompareTo(DistanceIndex other)
        {
            var compared = Distance.CompareTo(other.Distance);
            return compared != 0 ? compared : Index.CompareTo(other.Index);
        }
    }

    private static IdentityIndex[] IdentityOrder(IReadOnlyList<RuntimeEntitySnapshot> snapshots, ReadOnlySpan<int> indices)
    {
        var ordered = new IdentityIndex[indices.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            var snapshotIndex = indices[index];
            ordered[index] = new IdentityIndex(ReferenceCollections.OrderKey(snapshots[snapshotIndex].Ref), snapshotIndex);
        }
        Array.Sort(ordered);
        return ordered;
    }
    /// <summary>Every snapshot of an explicit candidate set, in the order the kernel answered it. Do not batch
    /// around the Runtime per-query or per-tick budget: one set is one call, and an incomplete answer — a spent
    /// budget, a stale identity, an observer that refused — rejects the step with the kernel's own code.</summary>
    private static IReadOnlyList<RuntimeEntitySnapshot> Observe(RuntimeQuerySession session, IReadOnlyList<EntityReference> candidates,
        EntityReference? required = null)
        => ObservedSpaceNodes.ObserveCurrent(session, candidates, required);
    /// <summary>The sizes the selected shape defines, validated before any position is read. Dispatch is by shape,
    /// so a size the shape does not define is not merely ignored: it is never looked at, and a volume assembled for
    /// another shape cannot smuggle one in. A shape this build has no volume for is refused with `spatial-shape`
    /// before an unrelated value could be read.</summary>
    private static (double Radius, double Height, double[] Extents, double[] Axis, double Cosine) Measure(ObservedVolume volume)
    {
        switch (volume.Shape)
        {
            case ObservedVolumeShape.Sphere:
                Length(volume.Radius);
                return (volume.Radius, 0d, Array.Empty<double>(), Array.Empty<double>(), 0d);
            case ObservedVolumeShape.Cone:
                // The angle is the half opening, so (0, 180]: 90 keeps the whole hemisphere in front of the apex
                // and 180 degenerates to the sphere of the same length.
                Length(volume.Radius);
                Bounds(volume.Angle, MinimumConeParameter, 180d);
                return (volume.Radius, 0d, Array.Empty<double>(), Axis(volume.Direction),
                    Math.Cos(volume.Angle * Math.PI / 180d));
            case ObservedVolumeShape.Cylinder:
            case ObservedVolumeShape.Capsule:
                Length(volume.Radius);
                Length(volume.Height);
                return (volume.Radius, volume.Height, Array.Empty<double>(), Array.Empty<double>(), 0d);
            case ObservedVolumeShape.Box:
                return (0d, 0d, HalfSizes(volume.Extents), Array.Empty<double>(), 0d);
            default:
                throw new RuntimeContractException("spatial-shape", "Only point sphere/cone/cylinder/capsule/box selection is implemented.");
        }
    }
    /// <summary>The cone's axis as a unit vector. A direction with no length names no axis, so it is refused rather
    /// than defaulted to a world axis.</summary>
    private static double[] Axis(IReadOnlyList<double>? direction)
    {
        if (direction is null || direction.Count != 3 || direction.Any(value => !double.IsFinite(value)))
            throw new RuntimeContractException("spatial-parameter", "A cone requires a direction of three finite components.");
        var length = Magnitude(Math.Abs(direction[0]), Math.Abs(direction[1]), Math.Abs(direction[2]));
        if (length < MinimumConeParameter)
            throw new RuntimeContractException("spatial-parameter", "A cone direction has no length.");
        return new[] { direction[0] / length, direction[1] / length, direction[2] / length };
    }
    /// <summary>The apex itself is inside; any other point is inside when it is within the length and its angle
    /// off the axis is at most the half opening, compared as cosines so no inverse trigonometry runs per
    /// candidate.</summary>
    private static bool InsideCone(IReadOnlyList<double> position, IReadOnlyList<double> apex, double length,
        IReadOnlyList<double> axis, double cosine)
    {
        var distance = Distance(position, apex);
        if (distance > length) return false;
        if (distance == 0) return true;
        var along = (position[0] - apex[0]) * axis[0] + (position[1] - apex[1]) * axis[1] + (position[2] - apex[2]) * axis[2];
        return along >= distance * cosine;
    }
    /// <summary>A box's own measurement: exactly three finite positive half sizes the game's Single can store,
    /// one per world axis. They are kept apart — the x half size is never read as the z half size.</summary>
    private static double[] HalfSizes(IReadOnlyList<double>? extents)
    {
        if (extents is null || extents.Count != 3)
            throw new RuntimeContractException("spatial-parameter", "A box volume requires exactly three half sizes.");
        var values = new double[3];
        for (var index = 0; index < values.Length; index++)
        {
            Length(extents[index]);
            values[index] = extents[index];
        }
        return values;
    }
    private static double[] Point(IReadOnlyList<double> point)
    {
        if (point is null || point.Count != 3)
            throw new RuntimeContractException("spatial-point", "Center requires three finite metre coordinates.");
        var copy = new[] { point[0], point[1], point[2] };
        if (copy.Any(value => !double.IsFinite(value)))
            throw new RuntimeContractException("spatial-point", "Center contains a non-finite coordinate.");
        return copy;
    }
    private static void Length(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > AreaFieldDefinition.MaximumSize)
            throw new RuntimeContractException("spatial-parameter", "A size must be finite, greater than 0 and storable by the game.");
    }
    private static void Bounds(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new RuntimeContractException("spatial-parameter", "Invalid or out-of-range spatial parameter.");
    }
    private static double Delta(double a, double b) => PointMeasure.Delta(a, b);
    internal static double Distance(IReadOnlyList<double> a, IReadOnlyList<double> b)
        => PointMeasure.Distance(a[0], a[1], a[2], b[0], b[1], b[2]);
    private static double Horizontal(IReadOnlyList<double> a, IReadOnlyList<double> b)
        => Magnitude(Delta(a[0], b[0]), 0, Delta(a[2], b[2]));
    private static double Magnitude(double x, double y, double z) => PointMeasure.Magnitude(x, y, z);
}
