using UnityEngine;

namespace ForgeCore.Tests;

/// <summary>A gun that is still coming up starts its shot from the raised muzzle in front of the look camera, and a charge point kept in the
/// shooter's yaw frame turns with the shooter instead of staying where the shooter once stood.</summary>
internal static class GunPoseChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-6f;
        var position = new Vector3(5, 1.6f, 2);
        var muzzle = GunPose.RaisedMuzzle(position, new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1));
        check(Near(muzzle, new Vector3(5.08f, 1.52f, 2.6f)), "gun pose: the raised muzzle is ahead of, right of and below the look camera");

        var north = new Vector3(0, 0, 1);
        var east = new Vector3(1, 0, 0);
        var offset = new Vector3(0.1f, 1.4f, 0.5f);
        var local = GunPose.ToYawFrame(offset, north);
        check(Near(local, offset) && Near(GunPose.FromYawFrame(local, north), offset), "gun pose: facing the reference direction the yaw frame is the world frame");
        var turned = GunPose.FromYawFrame(local, east);
        check(Near(turned, new Vector3(0.5f, 1.4f, -0.1f)), "gun pose: after the shooter turns a quarter turn right the charge point turns with it");
        check(Near(GunPose.FromYawFrame(GunPose.ToYawFrame(offset, new Vector3(3, 2, -4)), new Vector3(3, 2, -4)), offset), "gun pose: the yaw frame round-trips for any facing, pitch ignored");
        check(Near(GunPose.ToYawFrame(offset, Vector3.zero), offset), "gun pose: a facing with no horizontal part falls back to the reference direction");
    }
}
