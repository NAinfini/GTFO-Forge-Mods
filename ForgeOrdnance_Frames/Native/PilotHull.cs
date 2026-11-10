using Player;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>The worn suit's hit volume. The game lands an attack on a player only where it reaches one of that player's limb
/// colliders, and the suit is far larger than the person inside it: a shooter's projectile, a blast or a teammate's bullet reaching the
/// shell away from the person's body went through it without a hit. While a suit is worn, every peer gives its pilot one more limb: a
/// capsule from the feet to the helmet top, centred on the pilot, carrying the game's own player limb (<c>Dam_PlayerDamageLimb</c>) that
/// points at the pilot's damage base. A hit on it is the game's own hit on that player (a projectile is applied by the pilot's own
/// peer and reaches the host by the game's packet), so it goes through the one accept path to <see cref="PilotDamage"/> like any
/// other. It takes the layer and trigger flag of the pilot's own limbs, so every query that sees the body sees it the same way, and a
/// blast still counts once per player (the game marks the damage base, not the limb).</summary>
internal sealed class PilotHull
{
    /// <summary>The helmet top over the suit's eye, metres.</summary>
    private const float HelmetOverEye = 0.3f;
    /// <summary>The capsule's radius as a share of its height: about the shoulders of the shut shell.</summary>
    private const float RadiusPerHeight = 0.25f;

    private GameObject? _node;

    internal void Wear(PlayerAgent agent, float eyeHeight)
    {
        Remove();
        Collider? body = null;
        foreach (var limb in agent.GetComponentsInChildren<Dam_PlayerDamageLimb>(true))
        {
            body = limb.GetComponent<Collider>();
            if (body != null) break;
        }
        // The local player's damage-limbs can hang above the colliders that carry the hit: take any player collider under one.
        if (body == null)
            foreach (var collider in agent.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || collider.GetComponentInParent<Dam_PlayerDamageLimb>() == null) continue;
                body = collider;
                break;
            }
        if (body == null || agent.Damage == null)
        {
            // The message names which pilot (this peer's own player or a teammate) lacks which part, so the log can settle whether a local player has limbs at all.
            var local = agent.IsLocallyOwned;
            Plugin.Logger.ErrorOnce("frames-hull-unavailable", "Exosuit pilot in slot " + agent.PlayerSlotIndex
                + (local ? " (this peer's own player)" : " (a teammate)") + " has no " + (body == null ? "limb collider" : "damage base")
                + "; hits on the suit's shell outside the pilot's body do not count.");
            return;
        }
        var height = eyeHeight + HelmetOverEye;
        _node = new GameObject("ForgeOrdnanceFrames.Hull") { layer = body.gameObject.layer };
        _node.transform.SetParent(agent.transform, false);
        _node.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
        var capsule = _node.AddComponent<CapsuleCollider>();
        capsule.direction = 1;
        capsule.height = height;
        capsule.radius = height * RadiusPerHeight;
        capsule.isTrigger = body.isTrigger;
        var hull = _node.AddComponent<Dam_PlayerDamageLimb>();
        hull.SetBaseDamagable(agent.Damage);
        hull.m_damageMultiplier = 1f;
    }

    internal void Remove()
    {
        if (_node != null && !_node.WasCollected) Object.Destroy(_node);
        _node = null;
    }
}
