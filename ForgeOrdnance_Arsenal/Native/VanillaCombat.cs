using Player;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>Arsenal's damage on the host, through ForgeCore's requests into the game's own damage entries (replication, health
/// authority and hit reactions stay the game's): explosions through <c>Combat.Explode</c> (players are explosion targets as with any
/// explosive, so friendly fire follows the game), and an area's fire damage to every living enemy (<c>Enemies.Near</c>) and player
/// inside it and in sight of its centre (a wall stops it, an open door does not) through <c>Combat.Fire</c>.</summary>
internal static class VanillaCombat
{
    /// <summary>The share of an area's tick a teammate takes: enough to make them leave, not enough to kill them on the way out (a player has 25 health against an enemy's hundreds).</summary>
    private const float PlayerShare = 0.4f;

    /// <summary>Area damage over time to every living enemy and player inside the sphere and not behind a wall (friendly fire included, at <see cref="PlayerShare"/>).</summary>
    internal static void Burn(Vector3 centre, float radius, float damage, PlayerAgent? source)
    {
        foreach (var enemy in ForgeCore.Enemies.NearVisible(centre, radius)) ForgeCore.Combat.Fire(enemy!.Damage, damage, source);
        ForgeCore.Players.Each((centre, radius, damage, source), static (s, info) =>
        {
            var player = info.Agent;
            if (!player.Alive) return;
            if ((player.Position - s.centre).sqrMagnitude > s.radius * s.radius || !ForgeCore.Level.HasSight(s.centre, player.Position + Vector3.up)) return;
            ForgeCore.Combat.Fire(player.Damage, s.damage * PlayerShare, s.source);
        });
    }
}
