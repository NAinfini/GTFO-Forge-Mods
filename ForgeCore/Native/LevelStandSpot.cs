using UnityEngine;
using UnityEngine.AI;

namespace ForgeCore;

public static partial class Level
{
    private const float SampleReach = 0.6f, FloorProbe = 1.0f, MaxStep = 1.2f, CapsuleRadius = 0.3f, CapsuleLow = 0.45f, CapsuleHigh = 1.45f, SightHeight = 1.0f;

    /// <summary>A place a standing player can be put down around <paramref name="origin"/> (a suit, a pod, a lift) facing <paramref name="yaw"/>
    /// degrees: the first of the places at <paramref name="clearance"/> metres (straight behind first, then the back
    /// diagonals, the sides, further behind, the front; the mirror when <paramref name="preferBehind"/> is false) that the game itself says
    /// a body can stand on. A place passes when the game's navigation mesh has walkable ground within reach of it (<c>NavMesh.SamplePosition</c>,
    /// what the enemies walk on) no more than a step above or below <paramref name="origin"/>, the floor under it is found with a ray against
    /// the level (<c>LayerManager.MASK_WORLD</c>), a standing player's capsule there touches no level geometry, and nothing of the level lies
    /// between <paramref name="origin"/> and the place (no exit through a wall). <paramref name="tried"/> is the number of the place that
    /// passed (0 is the first tried); when none does the result is false, <paramref name="spot"/> is <paramref name="origin"/> and
    /// <paramref name="tried"/> is the number of places tried. Pure queries; nothing is allocated.</summary>
    public static bool FindStandSpot(Vector3 origin, float yaw, float clearance, bool preferBehind, out Vector3 spot, out int tried)
    {
        var mask = LayerManager.MASK_WORLD;
        for (tried = 0; tried < StandSpots.Count; tried++)
        {
            StandSpots.Offset(tried, yaw, clearance, preferBehind, out var dx, out var dz);
            var wanted = origin + new Vector3(dx, 0f, dz);
            if (!NavMesh.SamplePosition(wanted, out var nav, SampleReach, -1)) continue;
            var ground = nav.position;
            if (Mathf.Abs(ground.y - origin.y) > MaxStep) continue;
            if (Physics.Raycast(ground + Vector3.up * FloorProbe, Vector3.down, out var floor, FloorProbe * 2f, mask, QueryTriggerInteraction.Ignore)) ground = floor.point;
            if (Physics.CheckCapsule(ground + Vector3.up * CapsuleLow, ground + Vector3.up * CapsuleHigh, CapsuleRadius, mask, QueryTriggerInteraction.Ignore)) continue;
            if (Physics.Linecast(origin + Vector3.up * SightHeight, ground + Vector3.up * SightHeight, mask, QueryTriggerInteraction.Ignore)) continue;
            spot = ground;
            return true;
        }
        spot = origin;
        return false;
    }
}
