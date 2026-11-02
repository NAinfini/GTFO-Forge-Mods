using Enemies;
using Player;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>Arsenal's damage on the host, through ForgeCore's requests into the game's own damage entries (replication, health
/// authority and hit reactions stay the game's): explosions through <c>Combat.Explode</c> (players are explosion targets as with any
/// explosive, so friendly fire follows the game), and an area's fire damage to every living enemy (<c>Enemies.Near</c>) and player
/// inside it through <c>Combat.Fire</c>.</summary>
internal static class VanillaCombat
{
    /// <summary>Area damage over time to every living enemy and player inside the sphere (friendly fire included).</summary>
    internal static void Burn(Vector3 centre, float radius, float damage, PlayerAgent? source)
    {
        foreach (var enemy in ForgeCore.Enemies.Near(centre, radius))
            ForgeCore.Combat.Fire(enemy!.Damage, damage, source);
        var players = PlayerManager.PlayerAgentsInLevel;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null || !player.Alive) continue;
            if ((player.Position - centre).sqrMagnitude > radius * radius) continue;
            ForgeCore.Combat.Fire(player.Damage, damage, source);
        }
    }
}
