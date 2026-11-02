using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeCore;

public static partial class Level
{
    // How far from a point the navigation mesh may be sampled to snap a route's ends onto it.
    private const float RouteSampleRadius = 2f;
    // Height above the floor of the line that checks a move; a move through a wall or closed door crosses it.
    private const float MoveLineHeight = 1f;
    // Height above the floor a line of sight starts from, lowered under a low ceiling.
    private const float LookHeight = 2f;
    private const int RouteBuffer = 64;

    private static NavMeshPath? _path;
    private static readonly Il2CppStructArray<Vector3> PathCorners = new(RouteBuffer);

    /// <summary>A complete path on the game's navigation mesh (<c>NavMesh.CalculatePath</c>, the surface the enemies walk on, closed doors
    /// included) from <paramref name="from"/> to <paramref name="to"/>, no longer than <paramref name="maxLength"/> metres, with every
    /// corner after the start written to <paramref name="corners"/> and their number to <paramref name="count"/>. Both ends are first
    /// snapped onto the mesh within 2 m. False, with a count of 0, when either end is off the mesh, the path is partial or blocked (a
    /// closed door, another room), it is longer than allowed, or it has more corners than <paramref name="corners"/> holds (a path is
    /// never cut short). Pure queries; the path object and corner buffer are reused, nothing is allocated.</summary>
    public static bool TryRoute(Vector3 from, Vector3 to, float maxLength, Span<Vector3> corners, out int count)
    {
        count = 0;
        if (!NavMesh.SamplePosition(from, out var start, RouteSampleRadius, -1) || !NavMesh.SamplePosition(to, out var end, RouteSampleRadius, -1)) return false;
        _path ??= new NavMeshPath();
        if (!NavMesh.CalculatePath(start.position, end.position, -1, _path) || _path.status != NavMeshPathStatus.PathComplete) return false;
        var found = _path.GetCornersNonAlloc(PathCorners);
        if (found >= PathCorners.Length) return false;
        var length = 0f;
        for (var i = 1; i < found; i++)
        {
            length += (PathCorners[i] - PathCorners[i - 1]).magnitude;
            if (length > maxLength || i > corners.Length) return false;
            corners[i - 1] = PathCorners[i];
        }
        count = Math.Max(0, found - 1);
        return true;
    }

    /// <summary>Whether nothing of the level (<c>LayerManager.MASK_WORLD</c>, trigger volumes ignored) lies between a point above
    /// <paramref name="floorPoint"/> and <paramref name="aim"/>. The line starts 2 m above the floor, or just under the ceiling when a
    /// ceiling is lower than that.</summary>
    public static bool HasSight(Vector3 floorPoint, Vector3 aim)
    {
        var lift = LookHeight;
        if (Physics.Raycast(floorPoint + Vector3.up * 0.1f, Vector3.up, out var roof, LookHeight, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore))
            lift = Mathf.Max(0.2f, roof.point.y - floorPoint.y - 0.15f);
        return !Physics.Linecast(floorPoint + Vector3.up * lift, aim, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore);
    }

    /// <summary>Whether the move from one floor point to another runs into the level: a line 1 m above both against
    /// <c>LayerManager.MASK_WORLD</c>, trigger volumes ignored.</summary>
    public static bool Blocked(Vector3 from, Vector3 to)
        => Physics.Linecast(from + Vector3.up * MoveLineHeight, to + Vector3.up * MoveLineHeight, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore);
}
