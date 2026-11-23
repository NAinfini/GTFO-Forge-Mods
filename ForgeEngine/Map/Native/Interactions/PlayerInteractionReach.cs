using System;
using ForgeCore;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Where the game judges an interaction from. PlayerInteraction searches with the first-person camera (m_playerCam): a
/// camera ray plus a sphere check of m_searchRadius / m_proximityRadius around it. The origin is the eye, which drops when the
/// player crouches, not the feet. Every range decision (the point's own judge and the test fixture's standing pose) reads this one
/// origin and these radii from the live player, never a constant.</summary>
internal static class PlayerInteractionReach
{
    /// <summary>About a quarter second of a sprinting player's replicated lag, in metres.</summary>
    private const double RemoteSlack = 1d;

    /// <summary>The eye position and the larger of the game's two interaction radii, or false when either cannot be read.</summary>
    internal static bool TryRead(PlayerAgent agent, out Vector3 origin, out double radius)
    {
        origin = default; radius = 0;
        if (!agent.IsLocallyOwned) return TryReadRemote(agent, out origin, out radius);
        var interaction = agent.Interaction;
        if (interaction == null) return false;
        var camera = interaction.m_playerCam != null ? interaction.m_playerCam.transform : agent.FPSCamera?.transform;
        if (camera == null) return false;
        origin = camera.position;
        radius = Math.Max(interaction.m_searchRadius, interaction.m_proximityRadius);
        return float.IsFinite(origin.x) && float.IsFinite(origin.y) && float.IsFinite(origin.z) && double.IsFinite(radius) && radius > 0;
    }

    /// <summary>A player of another machine, judged on the host: there is no first-person camera of theirs here, so the origin is the agent's
    /// own eye position, as replicated, and the radii are read from the agent or, when theirs were never set up here, from the local player.
    /// The replicated position runs behind what the player's own machine sees, so <see cref="RemoteSlack"/> keeps an interaction the game
    /// allowed there from being refused here.</summary>
    private static bool TryReadRemote(PlayerAgent agent, out Vector3 origin, out double radius)
    {
        origin = agent.EyePosition;
        var interaction = agent.Interaction;
        radius = interaction == null ? 0 : Math.Max(interaction.m_searchRadius, interaction.m_proximityRadius);
        if (radius <= 0 && Players.Local?.Interaction is { } local) radius = Math.Max(local.m_searchRadius, local.m_proximityRadius);
        if (radius > 0) radius += RemoteSlack;
        return float.IsFinite(origin.x) && float.IsFinite(origin.y) && float.IsFinite(origin.z) && double.IsFinite(radius) && radius > 0;
    }
}
