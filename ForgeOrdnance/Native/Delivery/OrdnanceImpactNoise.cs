using System;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>The noise of a touchdown: the game's own world noise (ForgeCore's <c>Combat.Noise</c>, as a sentry's shot makes it), made
/// by the host only so sleeping enemies within the radius wake once, whoever's screen shows the landing.</summary>
internal static class OrdnanceImpactNoise
{
    /// <summary>Makes the noise at <paramref name="ground"/> when this peer is the host and <paramref name="radius"/> is above 0.
    /// Returns whether the game was asked to make it; an unresolvable course node or a failing call is logged, never thrown.</summary>
    internal static bool Make(Vector3 ground, float radius)
    {
        if (radius <= 0f || !SNet.IsMaster) return false;
        if (!ForgeCore.Level.CourseNodeAt(ground, 1f, out var node, out _) || node == null)
        {
            Plugin.Error("A touchdown's noise could not resolve a course node at " + ForgeCore.Log.Pos(ground) + ".");
            return false;
        }
        try
        {
            var made = ForgeCore.Combat.Noise(ground, radius, node);
            return made;
        }
        catch (Exception error)
        {
            Plugin.Error("A touchdown's noise failed: " + error);
            return false;
        }
    }
}
