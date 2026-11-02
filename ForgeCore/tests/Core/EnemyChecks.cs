using UnityEngine;
using EnemyAgent = Enemies.EnemyAgent;

namespace ForgeCore.Tests;

/// <summary>The nearby-enemy query (moved from ForgeOrdnance_Arsenal's VanillaCombat): one entry per living enemy however many
/// limbs are in range, cached collider ownership that is dropped on setup, death, reparenting and clear, and full coverage when the
/// query saturates.</summary>
internal static class EnemyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static EnemyAgent Enemy() => new() { gameObject = new() };
        static Collider Limb(EnemyAgent e)
        {
            var c = new Collider { gameObject = new(), Owner = e };
            c.transform.SetParent(e.transform, false);
            return c;
        }
        static void Hook(string name, EnemyAgent e) => ForgeCore.GameHooks.Fire(name, e);

        ForgeCore.Enemies.Clear();
        var enemy = Enemy(); var limb = Limb(enemy); var limb2 = Limb(enemy);
        Physics.Hits = new[] { limb, limb2, limb };
        check(ForgeCore.Enemies.Near(default, 5).Length == 1, "enemies: several limbs of one enemy count once");
        check(Patches.Ensured.Contains("GameHooks.EnemySetup:GameHooks"), "enemies: the setup and death patches go on at the first query");
        check(limb.Lookups == 1 && limb2.Lookups == 1, "enemies: a repeated collider uses its cached owner");
        ForgeCore.Enemies.Near(default, 5);
        check(limb.Lookups == 1 && limb2.Lookups == 1, "enemies: the owner cache survives successive queries");
        Hook("Setup", enemy);
        ForgeCore.Enemies.Near(default, 5);
        check(limb.Lookups == 2, "enemies: a reused enemy's setup drops its cached limbs");
        enemy.Alive = false;
        Hook("Dead", enemy);
        check(ForgeCore.Enemies.Near(default, 5).Length == 0 && limb.Lookups == 3, "enemies: death drops the cache and dead enemies are left out");
        var next = Enemy(); limb.Owner = next; limb.transform.SetParent(next.transform, false);
        Physics.Hits = new[] { limb };
        check(ForgeCore.Enemies.Near(default, 5)[0] == next && limb.Lookups == 4, "enemies: a reparented collider resolves its new enemy");
        limb.Owner = null; ForgeCore.Enemies.Forget(next);
        check(ForgeCore.Enemies.Near(default, 5).Length == 0, "enemies: a collider without a receiver is left out");
        limb.Owner = next;
        check(ForgeCore.Enemies.Near(default, 5).Length == 1, "enemies: a miss is not cached");
        var reused = Limb(enemy); reused.InstanceId = limb.GetInstanceID(); enemy.Alive = true;
        Physics.Hits = new[] { reused };
        check(ForgeCore.Enemies.Near(default, 5)[0] == enemy && reused.Lookups == 1, "enemies: a reused collider ID cannot take another collider's cached owner");
        ForgeCore.Enemies.Clear(); ForgeCore.Enemies.Near(default, 5);
        check(reused.Lookups == 2, "enemies: level cleanup drops the cache");
        Physics.Hits = Enumerable.Range(0, 257).Select(_ => Limb(Enemy())).ToArray();
        check(ForgeCore.Enemies.Near(default, 5).Length == 257 && Physics.Hits[256].Lookups == 1, "enemies: a saturated query grows and keeps every enemy");

        // Nearest first, at most a limit: the nearest ones are kept whatever order the physics query returns them in.
        ForgeCore.Enemies.Clear();
        var far = Enemy(); far.Position = new Vector3(9, 0, 0);
        var near = Enemy(); near.Position = new Vector3(1, 0, 0);
        var middle = Enemy(); middle.Position = new Vector3(0, 0, -4);
        var behind = Enemy(); behind.Position = new Vector3(-6, 0, 0);
        Physics.Hits = new[] { Limb(far), Limb(near), Limb(middle), Limb(behind), Limb(near) };
        var two = ForgeCore.Enemies.Nearest(default, 10, 2).ToArray();
        check(two.Length == 2 && two[0] == near && two[1] == middle, "enemies: a limited query keeps the nearest, nearest first");
        var every = ForgeCore.Enemies.Nearest(default, 10, 10).ToArray();
        check(every.SequenceEqual(new[] { near, middle, behind, far }), "enemies: a limit above the count sorts every enemy by distance, each once");
        check(ForgeCore.Enemies.Nearest(new Vector3(9, 0, 0), 10, 1)[0] == far, "enemies: distance is measured from the query point");
        check(ForgeCore.Enemies.Nearest(default, 10, 0).Length == 0, "enemies: a limit of 0 returns nothing");

        var sleeping = Enemy();
        check(ForgeCore.Enemies.Alert(sleeping) == EnemyAlert.Sleeping, "enemies: hibernating is asleep");
        sleeping.AI.Mode = Agents.AgentMode.Scout;
        check(ForgeCore.Enemies.Alert(sleeping) == EnemyAlert.Awake, "enemies: a scout is awake, not hunting");
        sleeping.AI.Mode = Agents.AgentMode.Agressive;
        check(ForgeCore.Enemies.Alert(sleeping) == EnemyAlert.Hunting, "enemies: aggressive is hunting");
        sleeping.AI.Mode = Agents.AgentMode.Off;
        check(ForgeCore.Enemies.Alert(sleeping) == EnemyAlert.Off, "enemies: the off mode is off");
        sleeping.AI.Mode = Agents.AgentMode.Agressive; sleeping.Alive = false;
        check(ForgeCore.Enemies.Alert(sleeping) == EnemyAlert.Off, "enemies: a dead enemy is off whatever its mode");
        var other = Enemy(); other.DimensionIndex = 1;
        check(ForgeCore.Enemies.FromCollider(Limb(other), enemy) == null && ForgeCore.Enemies.FromCollider(Limb(other)) == other,
            "enemies: a collider resolves its enemy, and only in the context's dimension when one is given");
        ForgeCore.Enemies.Clear();
    }
}
