using ForgeOrdnance;
using SNetwork;
using UnityEngine;

/// <summary>The touchdown's noise: made by the host only, from the impact's own radius, through the game's world noise.</summary>
internal static class ImpactNoiseChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var at = new Vector3(3f, 0f, 4f);
        var node = new AIGraph.AIG_CourseNode();
        ForgeCore.Level.Node = node;
        ForgeCore.Combat.Noises.Clear();
        Plugin.Errors.Clear();

        check(!OrdnanceImpactNoise.Make(at, 0f) && ForgeCore.Combat.Noises.Count == 0,
            "touchdown noise: an impact with no noise radius (the default of drops and strikes) makes none");

        SNet.IsMaster = false;
        check(!OrdnanceImpactNoise.Make(at, 40f) && ForgeCore.Combat.Noises.Count == 0, "touchdown noise: a client makes none, whoever's screen shows the landing");

        SNet.IsMaster = true;
        check(OrdnanceImpactNoise.Make(at, 40f) && ForgeCore.Combat.Noises.Count == 1
            && ForgeCore.Combat.Noises[0].At.x == at.x && ForgeCore.Combat.Noises[0].At.z == at.z && ForgeCore.Combat.Noises[0].Radius == 40f && ForgeCore.Combat.Noises[0].Node == node,
            "touchdown noise: the host makes the game's noise at the landing point with the impact's radius and the course node there");

        ForgeCore.Level.Node = null;
        ForgeCore.Combat.Noises.Clear();
        check(!OrdnanceImpactNoise.Make(at, 40f) && ForgeCore.Combat.Noises.Count == 0 && Plugin.Errors.Count == 1,
            "touchdown noise: no course node at the landing point is logged and makes no noise");
        Plugin.Errors.Clear();
    }
}
