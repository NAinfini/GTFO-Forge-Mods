using System;
using System.Collections.Generic;

namespace ForgeMap;

/// <summary>A borrowed index of this judgment's coordinates, never an entity registry. It only excludes
/// impossible new members; the caller still runs the exact exit test on the existing members.</summary>
internal sealed class TriggerZoneCandidates
{
    private double[] _coordinates = Array.Empty<double>();
    private int[] _slots = Array.Empty<int>();
    private int _axis, _count;

    /// <summary>Sorts the observed ones of <paramref name="targets"/> (slots into the two arrays) along the axis they spread widest on.</summary>
    internal void Prepare(double[][] positions, bool[] observed, List<int> targets)
    {
        if (_slots.Length < targets.Count)
        {
            var size = Math.Max(targets.Count, _slots.Length * 2);
            _coordinates = new double[size];
            _slots = new int[size];
        }
        _axis = 0;
        var widest = -1d;
        for (var axis = 0; axis < 3; axis++)
        {
            var low = double.PositiveInfinity; var high = double.NegativeInfinity;
            foreach (var slot in targets)
                if (observed[slot]) { low = Math.Min(low, positions[slot][axis]); high = Math.Max(high, positions[slot][axis]); }
            if (high - low > widest) { widest = high - low; _axis = axis; }
        }
        _count = 0;
        foreach (var slot in targets)
            if (observed[slot]) { _coordinates[_count] = positions[slot][_axis]; _slots[_count++] = slot; }
        Array.Sort(_coordinates, _slots, 0, _count);
    }

    /// <summary>The slot at one place of the sorted order.</summary>
    internal int Slot(int index) => _slots[index];

    /// <summary>The run of the sorted order the zone could hold: from <paramref name="first"/> up to, not including, the returned place.</summary>
    internal int Select(TriggerZone zone, IReadOnlyList<double>? previousCenter, out int first)
    {
        // Enclose the local point/segment test, including its epsilon. The reader accepts near-unit
        // quaternions, so use the minimum scale of the exact ToLocal matrix rather than normalizing it.
        var x = zone.Size[0] / 2 + 1e-9;
        var y = zone.Shape == TriggerZoneShape.Sphere ? x : Math.Max(zone.Size[1] / 2 + 1e-9,
            zone.Shape == TriggerZoneShape.Capsule ? x : 0);
        var z = zone.Shape == TriggerZoneShape.Box ? zone.Size[2] / 2 + 1e-9 : x;
        var q = zone.Rotation;
        var vectorLength = q[0] * q[0] + q[1] * q[1] + q[2] * q[2];
        var scale = Math.Sqrt(Math.Min(1, 1 + 4 * vectorLength * (vectorLength + q[3] * q[3] - 1)));
        // The judged column reaches above the feet position that is sorted here: a body's full height, or the foot lift.
        var radius = Math.Sqrt(x * x + y * y + z * z) / scale + 1e-7 + (zone.BodyHit ? TriggerZone.BodyHeight + TriggerZone.BodyRadius : TriggerZone.FootLift);
        var center = zone.Position[_axis];
        var before = previousCenter == null ? center : previousCenter[_axis];
        // The prior center and the complete allowed target travel enclose the original moving-zone segment.
        var travel = previousCenter == null ? 0 : TriggerZoneModule.MaximumJudgedTravel;
        var low = Math.Min(center, before) - radius - travel;
        var high = Math.Max(center, before) + radius + travel;
        first = 0; var end = _count;
        while (first < end)
        {
            var middle = first + (end - first) / 2;
            if (_coordinates[middle] < low) first = middle + 1;
            else end = middle;
        }
        while (end < _count && _coordinates[end] <= high) end++;
        return end;
    }
}
