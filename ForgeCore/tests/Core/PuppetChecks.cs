namespace ForgeCore.Tests;

/// <summary>The velocity arithmetic that drives a puppet's walk animation.</summary>
internal static class PuppetChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static bool Is(float a, float b) => Math.Abs(a - b) < 1e-4f;

        var (vx, vz) = PuppetMotion.Velocity(1f, 1f, 3f, 1f, 0.5f);
        check(Is(vx, 4f) && Is(vz, 0f), "puppet: velocity is the position change over the time");
        check(PuppetMotion.Velocity(0f, 0f, 5f, 5f, 0f) == (0f, 0f), "puppet: no time passed gives no velocity");

        PuppetMotion.Local(0f, 3f, 0f, 2f, out var forward, out var right);
        check(Is(forward, 3f) && Is(right, 0f), "puppet: moving the way it faces is all forward, whatever the facing length");
        PuppetMotion.Local(2f, 0f, 0f, 1f, out forward, out right);
        check(Is(forward, 0f) && Is(right, 2f), "puppet: facing +Z and moving +X is to the right");
        PuppetMotion.Local(0f, -2f, 0f, 1f, out forward, out right);
        check(Is(forward, -2f) && Is(right, 0f), "puppet: backing away is negative forward");
        PuppetMotion.Local(1f, 1f, 0f, 0f, out forward, out right);
        check(forward == 0f && right == 0f, "puppet: a facing of zero length gives no animation input");
    }
}
