using Agents;
using UnityEngine;
using EnemyAgent = Enemies.EnemyAgent;

namespace ForgeCore.Tests;

/// <summary>The ray that steps over the shooter's own colliders (<c>Combat.ShooterRay</c>) and the roster rule behind
/// <c>Level.PlayerByLookup</c>.</summary>
internal static class RayChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var me = new EnemyAgent { gameObject = new() };
        var other = new EnemyAgent { gameObject = new() };
        Collider Own() => new() { Owner = me };
        Collider Foreign() => new() { Owner = other };
        var up = new Vector3(0, 0, 1);

        Physics.Scripted = new();
        Physics.Scripted.Enqueue((Foreign(), 5f));
        var plain = Combat.ShooterRay(me, default, up, 50f, out var hit);
        check(plain && hit.distance == 5f && hit.collider!.Owner == other && Physics.LastRay.Mask == LayerManager.MASK_BULLETWEAPON_RAY,
            "ray: a foreign collider is the hit, against the bullet ray layers");

        Physics.Scripted = new();
        Physics.Scripted.Enqueue((Own(), 1f));
        Physics.Scripted.Enqueue((Own(), 0.5f));
        Physics.Scripted.Enqueue((Foreign(), 3f));
        var through = Combat.ShooterRay(me, default, up, 50f, out hit);
        check(through && Math.Abs(hit.distance - (1f + 0.01f + 0.5f + 0.01f + 3f)) < 1e-4f && hit.collider!.Owner == other,
            "ray: the shooter's own colliders are stepped over and the distance stays measured from the start");

        Physics.Scripted = new();
        Physics.Scripted.Enqueue((Own(), 1f));
        check(!Combat.ShooterRay(me, default, up, 50f, out hit) && hit.collider == null, "ray: only the shooter's own colliders is a miss");

        Physics.Scripted = new();
        Physics.Scripted.Enqueue((Own(), 49.995f));
        check(!Combat.ShooterRay(me, default, up, 50f, out _), "ray: stepping over an own collider at the end of the range is a miss");

        Physics.Scripted = new();
        for (var i = 0; i < 40; i++) Physics.Scripted.Enqueue((Own(), 0.1f));
        var threw = false;
        try { Combat.ShooterRay(me, default, up, 500f, out _); } catch (InvalidOperationException) { threw = true; }
        check(threw, "ray: a ray that crosses more than 32 own colliders throws");
        Physics.Scripted = null;

        var owners = new ulong[] { 0, 5, 7, 5 };
        check(RosterSearch.IndexOf(owners.Length, owners, static (list, i) => list[i], 5) == 1, "roster: the first entry with the lookup is found");
        check(RosterSearch.IndexOf(owners.Length, owners, static (list, i) => list[i], 0) == -1, "roster: a lookup of 0 names nobody, also when an entry reads 0");
        check(RosterSearch.IndexOf(owners.Length, owners, static (list, i) => list[i], 9) == -1, "roster: an absent lookup is -1");
        check(RosterSearch.IndexOf(0, owners, static (list, i) => list[i], 5) == -1, "roster: an empty roster is -1");
    }
}
