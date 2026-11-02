using EnemyAgent = Enemies.EnemyAgent;

namespace ForgeCore.Tests;

/// <summary>The in-level enemy registry (<c>Enemies.Live</c>): enemies already in the level are found at the first read, later ones
/// come in through the setup patch, a death, despawn or destroyed object vacates the slot, and a reused slot has a new generation.</summary>
internal static class LiveEnemyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static EnemyAgent Enemy() => new() { gameObject = new() };
        static void Hook(string name, EnemyAgent e) => ForgeCore.GameHooks.Fire(name, e);

        ForgeCore.Enemies.Clear();
        var early = Enemy(); var deadAlready = Enemy(); deadAlready.Alive = false;
        UnityEngine.Object.Scene.Add(early); UnityEngine.Object.Scene.Add(deadAlready);
        check(ForgeCore.Enemies.Live.Length == 1 && ForgeCore.Enemies.Live[0] == early && ForgeCore.Enemies.LiveCount == 1,
            "live: enemies already in the level are found at the first read, dead ones are not");
        UnityEngine.Object.Scene.Clear();

        var late = Enemy();
        var version = ForgeCore.Enemies.Version;
        Hook("Spawned", late);
        Hook("Spawned", late);
        check(ForgeCore.Enemies.LiveCount == 2 && ForgeCore.Enemies.Live[1] == late && ForgeCore.Enemies.Version == version + 1,
            "live: an enemy that spawns takes the next slot once, however often its setup runs");

        var first = ForgeCore.Enemies.Generation(0);
        Hook("Dead", early);
        check(ForgeCore.Enemies.Live[0] == null && ForgeCore.Enemies.LiveCount == 1 && ForgeCore.Enemies.Generation(0) == first + 1
            && ForgeCore.Enemies.Generation(1) == first, "live: a death vacates its slot and only that slot's generation moves");

        var third = Enemy();
        Hook("Spawned", third);
        check(ForgeCore.Enemies.Live.Length == 2 && ForgeCore.Enemies.Live[0] == third && ForgeCore.Enemies.Generation(0) == first + 1,
            "live: a vacated slot is reused, under the generation the death gave it");

        Hook("Despawned", late);
        check(ForgeCore.Enemies.Live[1] == null && ForgeCore.Enemies.LiveCount == 1, "live: an enemy the game despawns without a death leaves its slot");
        var destroyed = Enemy();
        Hook("Spawned", destroyed);
        Hook("Destroyed", destroyed);
        check(ForgeCore.Enemies.Live[1] == null && ForgeCore.Enemies.LiveCount == 1, "live: an enemy whose object is destroyed without a death leaves its slot");
        check(ForgeCore.Enemies.Generation(-1) == 0 && ForgeCore.Enemies.Generation(99) == 0, "live: a slot that does not exist has generation 0");

        var many = Enumerable.Range(0, 300).Select(_ => Enemy()).ToArray();
        foreach (var e in many) Hook("Spawned", e);
        check(ForgeCore.Enemies.LiveCount == 301 && ForgeCore.Enemies.Live.ToArray().Count(e => e != null) == 301, "live: the registry grows past its first capacity and keeps every enemy");

        ForgeCore.Enemies.Clear();
        check(ForgeCore.Enemies.Live.Length == 0 && ForgeCore.Enemies.LiveCount == 0, "live: level cleanup empties the registry");

        // The game sets its alive flag after setup returns: a spawn that does not read alive yet is still registered and reads alive afterwards.
        var young = Enemy(); young.Alive = false; young.AI = new() { Mode = Agents.AgentMode.Hibernate };
        Hook("Spawned", young);
        young.Alive = true;
        check(ForgeCore.Enemies.LiveCount == 1 && ForgeCore.Enemies.Live[0] == young && ForgeCore.Enemies.Alert(young) == EnemyAlert.Sleeping,
            "live: an enemy whose alive flag is not set yet at the end of its setup is registered and outlined once it is alive");

        // After a level cleanup the next read finds the enemies already there again, so a cleanup that runs after a spawn loses none.
        ForgeCore.Enemies.Clear();
        UnityEngine.Object.Scene.Clear();
        var present = Enemy(); UnityEngine.Object.Scene.Add(present);
        check(ForgeCore.Enemies.Live.Length == 1 && ForgeCore.Enemies.Live[0] == present, "live: the first read after a level cleanup finds the enemies already in the level");
        UnityEngine.Object.Scene.Clear();
        ForgeCore.Enemies.Clear();
    }
}
