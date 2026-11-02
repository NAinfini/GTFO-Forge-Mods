using UnityEngine;
using EnemyAgent = Enemies.EnemyAgent;

namespace ForgeCore.Tests;

/// <summary>Zone slowing (<c>Enemies.SlowZone</c> over <c>Enemies.Slow</c>): one lease per enemy inside, taken and ended on the 0.1 s
/// passes, foam and animator-less enemies skipped, overlapping zones never compounding, the zone ending by time, by dispose and with the
/// level, and the readback counts a caller logs.</summary>
internal static class SlowZoneChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static EnemyAgent Enemy(float speed = 1f) => new() { gameObject = new(), Anim = new Animator { speed = speed } };
        static Collider Limb(EnemyAgent e)
        {
            var c = new Collider { gameObject = new(), Owner = e };
            c.transform.SetParent(e.transform, false);
            return c;
        }
        static void Hook(string name, EnemyAgent e) => ForgeCore.GameHooks.Fire(name, e);
        static bool Is(float a, float b) => Math.Abs(a - b) < 1e-4f;
        static void Reset() { ForgeCore.Enemies.Clear(); Physics.Hits = Array.Empty<Collider>(); Time.time = 10f; }

        Reset();
        var a = Enemy(1.5f);
        Physics.Hits = new[] { Limb(a) };
        var zone = ForgeCore.Enemies.SlowZone(default, 5f, 0.5f, 3f, "test");
        check(zone.Active && Is(a.Anim!.speed, 1.5f) && zone.Slowed == 0, "slow zone: nothing is written before the first pass");
        ForgeCore.Enemies.TickZones(10f);
        check(Is(a.Anim!.speed, 0.75f) && zone.Slowed == 1 && zone.Mismatches == 0 && zone.Owner == "test" && Is(zone.Ends, 13f),
            "slow zone: an enemy inside is slowed from its own speed on the first pass, and the write read back");
        Physics.Hits = Array.Empty<Collider>();
        ForgeCore.Enemies.TickZones(10.05f);
        check(Is(a.Anim!.speed, 0.75f), "slow zone: passes run at most every 0.1 s");
        ForgeCore.Enemies.TickZones(10.15f);
        check(Is(a.Anim!.speed, 1.5f) && zone.Slowed == 1 && zone.Active, "slow zone: an enemy that left gets its own speed back on the next pass; the count keeps it");
        Physics.Hits = new[] { Limb(a) };
        ForgeCore.Enemies.TickZones(10.3f);
        check(Is(a.Anim!.speed, 0.75f) && zone.Slowed == 1, "slow zone: an enemy that comes back is slowed again, counted once");
        Hook("Setup", a);
        check(Is(a.Anim!.speed, 1.5f), "slow zone: reusing the enemy puts its speed back at once");
        ForgeCore.Enemies.TickZones(10.5f);
        check(Is(a.Anim!.speed, 0.75f) && zone.Slowed == 1, "slow zone: a reused enemy still inside is slowed afresh");
        ForgeCore.Enemies.TickZones(13f);
        check(!zone.Active && Is(a.Anim!.speed, 1.5f), "slow zone: the zone ends by itself at its time and every speed comes back");
        ForgeCore.Enemies.TickZones(13.2f);
        check(Is(a.Anim!.speed, 1.5f), "slow zone: an ended zone slows nothing more");

        Reset();
        var glued = Enemy(); glued.Locomotion = new global::Enemies.EnemyLocomotion { CurrentStateEnum = ES_StateEnum.StuckInGlue };
        var blind = Enemy(); blind.Anim = null;
        var free = Enemy();
        Physics.Hits = new[] { Limb(glued), Limb(blind), Limb(free) };
        var skipping = ForgeCore.Enemies.SlowZone(default, 5f, 0.5f, 3f, "test");
        ForgeCore.Enemies.TickZones(10f);
        check(Is(glued.Anim!.speed, 1f) && Is(free.Anim!.speed, 0.5f) && skipping.Slowed == 1,
            "slow zone: an enemy held in foam keeps the foam's speed, one without an animator is skipped, the others are slowed");
        glued.Locomotion.CurrentStateEnum = ES_StateEnum.Hibernate;
        ForgeCore.Enemies.TickZones(10.15f);
        check(Is(glued.Anim!.speed, 0.5f) && skipping.Slowed == 2, "slow zone: an enemy the foam let go of is slowed on the next pass");
        skipping.Dispose();
        skipping.Dispose();
        check(!skipping.Active && Is(glued.Anim!.speed, 1f) && Is(free.Anim!.speed, 1f), "slow zone: disposing ends the zone and every lease; disposing twice changes nothing");

        Reset();
        var b = Enemy(2f);
        Physics.Hits = new[] { Limb(b) };
        var half = ForgeCore.Enemies.SlowZone(default, 5f, 0.5f, 5f, "test");
        var quarter = ForgeCore.Enemies.SlowZone(default, 5f, 0.25f, 1f, "test");
        ForgeCore.Enemies.TickZones(10f);
        check(Is(b.Anim!.speed, 0.5f), "slow zone: overlapping zones take the slowest factor, not both");
        ForgeCore.Enemies.TickZones(11f);
        check(!quarter.Active && half.Active && Is(b.Anim!.speed, 1f), "slow zone: when the stronger zone ends the weaker one still holds");
        ForgeCore.Enemies.Clear();
        check(!half.Active, "slow zone: level cleanup ends every zone");
        ForgeCore.Enemies.TickZones(12f);

        Reset();
        foreach (var (radius, seconds, factor) in new[] { (0f, 1f, 0.5f), (1f, 0f, 0.5f), (1f, 1f, -0.1f), (float.NaN, 1f, 0.5f) })
        {
            var refused = false;
            try { ForgeCore.Enemies.SlowZone(default, radius, factor, seconds, "test"); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            check(refused, "slow zone: a radius, time or factor out of range is refused (" + radius + ", " + seconds + ", " + factor + ")");
        }
        Reset();
    }
}
