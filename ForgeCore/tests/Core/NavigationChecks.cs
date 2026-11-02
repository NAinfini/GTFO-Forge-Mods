using UnityEngine;
using UnityEngine.AI;

namespace ForgeCore.Tests;

/// <summary>The level's walkable path, line of sight and blocked-move queries (<c>Level.TryRoute</c>, <c>HasSight</c>, <c>Blocked</c>):
/// what each asks of the game's navigation mesh and physics, the refusals (off the mesh, partial path, too long, too many corners) and
/// the heights the lines are cast at.</summary>
internal static class NavigationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static bool At(Vector3 v, float x, float y, float z) => Math.Abs(v.x - x) < 1e-4f && Math.Abs(v.y - y) < 1e-4f && Math.Abs(v.z - z) < 1e-4f;
        static void Reset()
        {
            NavMesh.OnMesh = _ => true; NavMesh.PathStatus = NavMeshPathStatus.PathComplete; NavMesh.CalculateOk = true;
            NavMesh.PathCorners = new[] { new Vector3(0, 0, 0), new Vector3(3, 0, 0), new Vector3(3, 0, 4) };
            Physics.RayAnswer = null; Physics.LineBlocked = (_, _) => false;
        }
        var to = new Vector3(3, 0, 4);
        var buffer = new Vector3[8];

        Reset();
        check(Level.TryRoute(default, to, 10f, buffer, out var count) && count == 2 && At(buffer[0], 3, 0, 0) && At(buffer[1], 3, 0, 4),
            "route: a complete path gives every corner after the start, in order");
        check(NavMesh.LastSample == (2f, -1), "route: both ends are snapped onto the mesh within 2 m, every area");
        check(Level.TryRoute(default, to, 7f, buffer, out count) && count == 2, "route: a path exactly as long as allowed passes");
        check(!Level.TryRoute(default, to, 6.9f, buffer, out count) && count == 0, "route: a path longer than allowed is refused with no corners");
        NavMesh.PathStatus = NavMeshPathStatus.PathPartial;
        check(!Level.TryRoute(default, to, 10f, buffer, out count) && count == 0, "route: a partial path (a closed door, another room) is refused");
        Reset(); NavMesh.CalculateOk = false;
        check(!Level.TryRoute(default, to, 10f, buffer, out count) && count == 0, "route: no path at all is refused");
        Reset(); NavMesh.OnMesh = p => p.x != 0f;
        check(!Level.TryRoute(default, to, 10f, buffer, out count) && count == 0, "route: a start off the mesh is refused");
        Reset(); NavMesh.OnMesh = p => p.z != 4f;
        check(!Level.TryRoute(default, to, 10f, buffer, out count) && count == 0, "route: an end off the mesh is refused");
        Reset();
        check(!Level.TryRoute(default, to, 10f, new Vector3[1], out count) && count == 0, "route: a path with more corners than the buffer holds is refused, not cut short");
        check(Level.TryRoute(default, to, 10f, new Vector3[2], out count) && count == 2, "route: a buffer exactly as large as the corners passes");
        NavMesh.PathCorners = new[] { new Vector3(0, 0, 0) };
        check(Level.TryRoute(default, default, 10f, buffer, out count) && count == 0, "route: standing at the target is a complete path of no corners");
        NavMesh.PathCorners = new Vector3[70];
        check(!Level.TryRoute(default, to, 1000f, new Vector3[100], out count) && count == 0, "route: a path that fills the game's corner buffer is refused as possibly truncated");

        Reset();
        var floor = new Vector3(1, 5, 1);
        var aim = new Vector3(4, 6, 1);
        check(Level.HasSight(floor, aim) && At(Physics.LastLine.From, 1, 7, 1) && At(Physics.LastLine.To, 4, 6, 1)
            && Physics.LastLine.Mask == LayerManager.MASK_WORLD && Physics.LastLine.Trigger == QueryTriggerInteraction.Ignore,
            "sight: with a high ceiling the line starts 2 m above the floor point, against the level, triggers ignored");
        Physics.RayAnswer = (_, _) => new Vector3(1, 6f, 1);
        check(Level.HasSight(floor, aim) && At(Physics.LastLine.From, 1, 5.85f, 1), "sight: under a ceiling 1 m up the line starts 0.15 m below it");
        Physics.RayAnswer = (_, _) => new Vector3(1, 5.1f, 1);
        check(Level.HasSight(floor, aim) && At(Physics.LastLine.From, 1, 5.2f, 1), "sight: the line never starts lower than 0.2 m above the floor");
        Physics.LineBlocked = (_, _) => true;
        check(!Level.HasSight(floor, aim), "sight: a level surface on the line blocks it");

        Reset();
        var from = new Vector3(0, 2, 0);
        var onward = new Vector3(5, 3, 0);
        check(!Level.Blocked(from, onward) && At(Physics.LastLine.From, 0, 3, 0) && At(Physics.LastLine.To, 5, 4, 0)
            && Physics.LastLine.Mask == LayerManager.MASK_WORLD && Physics.LastLine.Trigger == QueryTriggerInteraction.Ignore,
            "blocked: the move is checked on a line 1 m above both floor points, against the level, triggers ignored");
        Physics.LineBlocked = (a, b) => a.x < 3f && b.x >= 3f;
        check(Level.Blocked(from, onward), "blocked: a level surface across the move blocks it");
        Reset();
    }
}
