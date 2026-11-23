using System;
using System.Collections.Generic;
using ForgeEngine.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>`forge.action.enemy.move_to`'s `area` port.
///
/// The field a request names is read from the same published runtime resources the field provider owns the kind
/// through, with the shared reader (`ForgeEngine.Framework.AreaFieldTable`), so the accepted grammar and the
/// duplicate-id rule exist once — the rule a constrained teleport is already measured against. A definition carries
/// a shape and its sizes and no world position, so the centre is the consumer's: the plan's own `destination` is the
/// centre, exactly as it is for that teleport.
///
/// The point an enemy is actually sent to is a navigation point inside the field's volume, solved once per command
/// by the game's own mesh query (`UnityEngine.AI.NavMesh.SamplePosition`, reached through `ForgeCore.Level.TrySnap`):
/// <list type="number">
/// <item>the nearest navigation point to the centre is asked for within the volume's own circumradius — no point of
/// the volume is farther from the centre than that — so an answer that falls inside the volume is the nearest
/// navigation point the volume holds;</item>
/// <item>when the nearest surface to the centre lies outside the volume, the volume is walked outward in shells of
/// probes and the first shell that answers a navigation point inside it wins, the nearest of that shell's answers
/// taken. The answer is therefore the nearest point those shells resolve, at that resolution: a coordinate no shell
/// answers for is never invented, and a field whose volume holds no navigation point at all is refused as
/// `destination-unreachable` instead of sending an enemy to a place nothing can walk to.</item>
/// </list>
///
/// The enemy walks there through the row's existing native write (`EnemyAI.NavmeshAgentGoal`), which this half does
/// not change: it only decides which point that write names. The port declares no centre of its own, so the centre
/// is always the plan's `destination`, and a request with no readable destination is refused as an invalid
/// destination — after any field it named has been resolved.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>How many shells a volume is walked outward in when the nearest surface to its centre is outside it.
    /// One shell is one step of the volume's circumradius and one probe asks the mesh within one further step, so
    /// the answers of neighbouring probes cover the volume between them.</summary>
    private const int SearchShells = 4;

    /// <summary>The directions the shells' probes are drawn in: the 26 cube neighbours, in the fixed order
    /// <see cref="Directions"/> produces, so one field is searched the same way every time. The order decides which
    /// probe of a shell is asked first, never which answer wins — a shell reports its nearest answer.</summary>
    private static readonly Vector3[] SearchDirections = Directions();

    /// <summary>The navigation point inside one field's volume that an enemy is sent to, or false when the volume
    /// holds none. Both buffers the shared membership test is asked with are metre coordinates: the volume's own
    /// centre, and one probe point the caller reuses because the test copies what it is handed.</summary>
    internal static bool TryAreaDestination(AreaFieldDefinition field, Vector3 centre, out Vector3 destination)
    {
        destination = default;
        var origin = new[] { (double)centre.x, (double)centre.y, (double)centre.z };
        var probe = new double[3];
        var reach = (float)Circumradius(field);
        // The volume's own reach: no point inside it is farther from the centre than `reach`, so a nearest-surface
        // answer found inside the volume is the nearest navigation point the volume holds, not merely the nearest
        // the query happened to see.
        if (Sample(centre, reach, out var near) && Inside(field, near, origin, probe))
        {
            destination = near;
            return true;
        }
        // The nearest surface to the centre is outside the volume: walk the volume outward in shells and take the
        // first shell that answers a navigation point inside it, its nearest answer.
        var step = reach / SearchShells;
        for (var shell = 1; shell <= SearchShells; shell++)
        {
            var radius = step * shell;
            var nearest = float.PositiveInfinity;
            var found = false;
            foreach (var direction in SearchDirections)
            {
                var where = new Vector3(centre.x + direction.x * radius, centre.y + direction.y * radius,
                    centre.z + direction.z * radius);
                probe[0] = where.x; probe[1] = where.y; probe[2] = where.z;
                if (!field.Contains(probe, origin)) continue;
                if (!Sample(where, step, out var point)) continue;
                if (!Inside(field, point, origin, probe)) continue;
                var dx = point.x - centre.x; var dy = point.y - centre.y; var dz = point.z - centre.z;
                var distance = dx * dx + dy * dy + dz * dz;
                if (distance >= nearest) continue;
                nearest = distance;
                destination = point;
                found = true;
            }
            if (found) return true;
        }
        return false;
    }

    /// <summary>One query at the game's own mesh entry point. A mesh with no bake under the query, a non-finite
    /// answer and a native call this process cannot make all answer the same way — the point is not one an enemy can
    /// be sent to — because none of them is a navigation point the row could honestly name.</summary>
    private static bool Sample(Vector3 position, float maxDistance, out Vector3 point)
    {
        point = default;
        try
        {
            if (!ForgeCore.Level.TrySnap(position, maxDistance, out point)) return false;
            return float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);
        }
        catch (Exception caught) { EnemyLog.Contained(caught); return false; }
    }

    /// <summary>Whether one mesh answer lies inside the field's own volume, asked through the shared definition's
    /// membership test — the same closed, world-axis aligned test the teleport landing and the spatial selector
    /// use.</summary>
    private static bool Inside(AreaFieldDefinition field, Vector3 point, double[] origin, double[] buffer)
    {
        buffer[0] = point.x; buffer[1] = point.y; buffer[2] = point.z;
        return field.Contains(buffer, origin);
    }

    /// <summary>The distance from the field's centre beyond which no point can be inside its volume: the shape's own
    /// circumradius. A capsule's axis is shortened by its radius before the hemispheres are added, exactly as the
    /// definition's own membership test shortens it, so the bound and the test agree on what the shape is.</summary>
    private static double Circumradius(AreaFieldDefinition field)
    {
        var half = (field.Height ?? 0) / 2;
        return field.Shape switch
        {
            AreaFieldShape.Box => Math.Sqrt(Square(field.Extents![0]) + Square(field.Extents[1]) + Square(field.Extents[2])),
            AreaFieldShape.Capsule => Math.Sqrt(Square(field.Radius) + Square(Math.Max(half - field.Radius, 0))),
            AreaFieldShape.Cylinder => Math.Sqrt(Square(field.Radius) + Square(half)),
            _ => field.Radius
        };
    }

    private static double Square(double value) => value * value;

    /// <summary>The 26 cube-neighbour directions, each of unit length, produced by one fixed loop so the search
    /// order is a property of this file rather than of a hash or a frame.</summary>
    private static Vector3[] Directions()
    {
        var directions = new List<Vector3>(26);
        for (var y = -1; y <= 1; y++)
            for (var z = -1; z <= 1; z++)
                for (var x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    var length = MathF.Sqrt(x * x + y * y + z * z);
                    directions.Add(new Vector3(x / length, y / length, z / length));
                }
        return directions.ToArray();
    }
}
