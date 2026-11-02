using UnityEngine;
using EnemyAgent = Enemies.EnemyAgent;

namespace ForgeCore.Tests;

/// <summary>Enemy slowing (<c>Enemies.Slow</c> and the pure <c>SlowBook</c> behind it): the slowest lease wins and never compounds, the
/// enemy's own speed comes back when the last lease ends, a speed the game set meanwhile is left alone, death releases, and an enemy
/// without an animator gets an inactive lease and nothing is logged.</summary>
internal static class SlowChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        static EnemyAgent Enemy(float speed = 1f) => new() { gameObject = new(), Anim = new Animator { speed = speed } };
        static void Hook(string name, EnemyAgent e) => ForgeCore.GameHooks.Fire(name, e);
        static bool Is(float a, float b) => Math.Abs(a - b) < 1e-4f;

        ForgeCore.Enemies.Clear();
        var a = Enemy(1.5f);
        var half = ForgeCore.Enemies.Slow(a, 0.5f, "test");
        check(half.Active && half.ReadBack && Is(a.Anim!.speed, 0.75f), "slow: the animator speed is scaled from the enemy's own");
        var quarter = ForgeCore.Enemies.Slow(a, 0.25f, "test");
        check(Is(a.Anim!.speed, 0.375f), "slow: a second lease takes the slowest factor, not both");
        var weak = ForgeCore.Enemies.Slow(a, 0.9f, "test");
        check(Is(a.Anim!.speed, 0.375f), "slow: a weaker lease does not undo a stronger one");
        quarter.Dispose();
        check(Is(a.Anim!.speed, 0.75f) && !quarter.Active && half.Active, "slow: ending the slowest lease falls back to the next slowest");
        quarter.Dispose();
        check(Is(a.Anim!.speed, 0.75f), "slow: disposing twice changes nothing");
        half.Dispose(); weak.Dispose();
        check(Is(a.Anim!.speed, 1.5f), "slow: the enemy's own speed returns when the last lease ends");

        var b = Enemy();
        var lease = ForgeCore.Enemies.Slow(b, 0.5f, "test");
        b.Anim!.speed = 0f;
        lease.Dispose();
        check(Is(b.Anim!.speed, 0f), "slow: a speed the game set meanwhile is left alone");

        var c = Enemy();
        var l1 = ForgeCore.Enemies.Slow(c, 0.5f, "test");
        c.Anim!.speed = 2f;
        l1.Refresh();
        check(Is(c.Anim!.speed, 1f), "slow: refresh takes the game's new speed as the enemy's own and re-applies the factor");
        l1.Dispose();
        check(Is(c.Anim!.speed, 2f), "slow: the game's speed is what comes back after a refresh");

        var d = Enemy();
        var held = ForgeCore.Enemies.Slow(d, 0.5f, "test");
        Hook("Dead", d);
        check(!held.Active && Is(d.Anim!.speed, 1f), "slow: an enemy's death releases its leases and gives its speed back");

        var e = Enemy();
        var reused = ForgeCore.Enemies.Slow(e, 0.5f, "test");
        Hook("Setup", e);
        check(!reused.Active && Is(e.Anim!.speed, 1f), "slow: reusing the enemy releases its leases");
        var logged = errors.Count;
        var blind = Enemy(); blind.Anim = null;
        var none = ForgeCore.Enemies.Slow(blind, 0.5f, "test");
        check(!none.Active && errors.Count == logged, "slow: an enemy without an animator gets an inactive lease and nothing is logged");

        var book = new SlowBook<int>();
        var want = book.Add(1, 2f, 2f, out var id);
        check(Is(want, 2f) && id > 0, "slow book: a factor above 1 is clamped to 1");
        check(book.Remove(1, id, 2f, out _) == false && book.Count == 0, "slow book: nothing to set when the speed never changed");
    }
}
