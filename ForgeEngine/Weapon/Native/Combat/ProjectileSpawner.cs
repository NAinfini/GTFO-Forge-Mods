using System;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// Ordinary projectile launch uses the manager's own complete request
/// <c>ProjectileManager.WantToFireTargeting(ProjectileType, Agent, Vector3, Vector3, int, float)</c> (dump.cs 586704):
/// the game's own entry for a fired projectile, which spawns and replicates it itself. The call is made directly on the
/// typed signature: no member is looked up by name and no argument is boxed.
///
/// A game-free build compiles the same file against doubles that declare `ProjectileManager` and `ProjectileType`
/// under the same global names, so launch tests exercise this exact call. Extended weapon flights draw ForgeVisual's
/// <c>ProjectileLooks.Copy</c> instead of launching a native projectile and taking over its components.
/// </summary>
internal static class ProjectileSpawner
{
    /// <summary>The native member one profile names, or false when this build's enum has no such member.</summary>
    internal static bool TryType(string member, out ProjectileType type)
        => Enum.TryParse(member, false, out type) && Enum.IsDefined(typeof(ProjectileType), type);

    /// <summary>One projectile at <paramref name="origin"/> flying along <paramref name="heading"/>, requested
    /// through the game's own burst of one. <paramref name="target"/> is the agent it homes on, or null for none:
    /// the request packs it into its own packet (<c>pFireTargeting.target</c>) and broadcasts that, so every machine
    /// spawns the projectile with the same target (`Weapon/evidence/projectile-launch-target.json`). True only when
    /// the manager's projectile list then holds a projectile it did not hold before; false is "not seen", which does
    /// not prove nothing was launched. A throw is left to the caller, which cannot prove whether native side effects
    /// happened before it.</summary>
    internal static bool Launch(ProjectileType type, Agents.Agent? target, Vector3 origin, Vector3 heading)
    {
        var manager = ProjectileManager.Current;
        var projectiles = manager?.Projectiles;
        if (projectiles == null) return false;
        var before = projectiles.Count;
        ProjectileManager.WantToFireTargeting(type, target, origin, heading, 1, 0f);
        return projectiles.Count > before;
    }
}
