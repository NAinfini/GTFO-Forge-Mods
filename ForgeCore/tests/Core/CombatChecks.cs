using AIGraph;
using Agents;
using UnityEngine;
using EnemyAgent = Enemies.EnemyAgent;
using GameEnemyAllocator = Enemies.EnemyAllocator;

namespace ForgeCore.Tests;

/// <summary>The game's damage entries behind <c>Combat</c> (Push, Melee, ExplosionDamage, BulletDamage, Force, Fire, Explode, Noise, Bullet) and the
/// bullet kill event: each entry is called with exactly the arguments the doc says, the settlement is the receiver's health read before and
/// after, a missing receiver is refused, and a kill is reported once, on the host, for a bullet that took a living enemy to dead.</summary>
internal static class CombatChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        static bool Is(float a, float b) => Math.Abs(a - b) < 1e-4f;
        var at = new Vector3(1, 2, 3);
        var dir = new Vector3(0, 0, 1);
        var source = new EnemyAgent { gameObject = new() };

        var push = new Dam_SyncedDamageBase { Health = 100f };
        var pushed = Combat.Push(push, 1f, source, at, dir);
        check(Is(pushed.HealthBefore, 100f) && Is(pushed.HealthAfter, 100f) && Is(pushed.Dealt, 0f)
            && push.Calls.Single() == "push 1 from=True at=(1,2,3) dir=(0,0,1)",
            "combat: a push is the receiver's own push with the source, place and direction, and reports no health lost");

        var melee = new Dam_EnemyDamageBase { Health = 100f, Settles = 7f };
        var struck = Combat.Melee(melee, 7f, source, at, dir, 10f);
        check(Is(struck.HealthBefore, 100f) && Is(struck.HealthAfter, 93f) && Is(struck.Dealt, 7f)
            && melee.Calls.Single() == "melee 7 stagger=10 multi=1/1/1/1 directional=False noise=Normal gear=0",
            "combat: a melee hit is the enemy receiver's own with the stagger multiplier, every other multiplier 1, no directional bonus, normal noise, and reports the health taken");

        var blast = new Dam_SyncedDamageBase { Health = 50f, Settles = 30f };
        var boom = Combat.ExplosionDamage(blast, 30f, at, new Vector3(0, 4, 0));
        check(Is(boom.HealthBefore, 50f) && Is(boom.HealthAfter, 20f) && blast.Calls.Single() == "explosion 30 at=(1,2,3) force=(0,4,0)",
            "combat: explosion damage to one receiver carries its place and force vector and reports the health taken");

        var bullet = new Dam_SyncedDamageBase { Health = 10f, Settles = 12f };
        var shot = Combat.BulletDamage(bullet, 12f, null, at, dir, new Vector3(0, 0, -1));
        check(Is(shot.Dealt, 12f) && bullet.Calls.Single() == "bullet 12 from=False at=(1,2,3) dir=(0,0,1) normal=(0,0,-1)",
            "combat: bullet damage to one receiver carries its place, direction and surface normal and reports the health taken");

        var fire = new Dam_SyncedDamageBase { Health = 10f, Settles = 2f };
        check(Is(Combat.Fire(fire, 2f, source).Dealt, 2f) && fire.Calls.Single() == "fire 2", "combat: fire damage reports the health taken");

        var body = new Dam_EnemyDamageBase();
        Combat.Force(body, at, 4f);
        check(body.Calls.Single() == "force 4 from=(1,2,3)" && body.Health == 100f, "combat: a force is sent to the enemy receiver with its origin and magnitude and takes no health");

        var refused = 0;
        void Refuses(Action call) { try { call(); } catch (ArgumentNullException) { refused++; } }
        Refuses(() => Combat.Push(null!, 1f, null, at, dir));
        Refuses(() => Combat.Melee(null!, 1f, null, at, dir, 1f));
        Refuses(() => Combat.ExplosionDamage(null!, 1f, at, dir));
        Refuses(() => Combat.BulletDamage(null!, 1f, null, at, dir, dir));
        Refuses(() => Combat.Fire(null!, 1f, null));
        Refuses(() => Combat.Force(null!, at, 1f));
        check(refused == 6, "combat: a missing receiver is refused by every damage entry");

        DamageUtil.Calls.Clear();
        Combat.Explode(at, 5f, 0f, 1f);
        check(DamageUtil.Calls.Count == 0, "combat: an explosion of no damage requests nothing");
        Combat.Explode(at, 5f, 40f, 0f);
        Combat.Explode(at, 5f, 40f, 3f, 99, true, DamageUtil.ExplosionDamageType.Damage, 12u);
        check(DamageUtil.Calls.Count == 2 && DamageUtil.Calls[0] == "explosion r=5 d=40 t=8 b=16 f=False/0"
            && DamageUtil.Calls[1] == "capsule r=5 d=40 t=99 b=16 f=True/3 type=Damage source=12",
            "combat: an explosion is the game's own, push only with a force, and the targeted form carries the mask, damage type and source ID");

        NoiseManager.Made.Clear();
        var node = new AIG_CourseNode();
        check(!Combat.Noise(at, 0f, node) && !Combat.Noise(at, 5f, null) && !Combat.Noise(at, 5f, new AIG_CourseNode { WasCollected = true }) && NoiseManager.Made.Count == 0,
            "combat: noise of no radius, without a course node or with a collected one makes nothing");
        check(Combat.Noise(at, 6f, node) && NoiseManager.Made.Single() is { radiusMin: 0f, radiusMax: 6f, yScale: 1f, type: NM_NoiseType.InstaDetect, includeToNeightbourAreas: true, raycastFirstNode: false, noiseMaker: null } made
            && made.node == node, "combat: the loud-event noise is the mine's parameters with the given radius");

        Gear.BulletWeapon.Hits.Clear();
        var hit = new Weapon.WeaponHitData();
        check(Combat.Bullet(hit) && Gear.BulletWeapon.Hits.Single() == hit, "combat: a bullet hit goes to the game's own bullet resolution and returns its answer");

        // The kill event: the patch pair is exercised the way the game's receiver calls it.
        var hooks = typeof(Combat).Assembly.GetType("ForgeCore.CombatKillHooks")!;
        bool Prefix(Dam_EnemyDamageBase receiver)
        {
            var args = new object?[] { receiver, new pBulletDamageData(), false };
            hooks.GetMethod("Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, args);
            return (bool)args[2]!;
        }
        void Postfix(Dam_EnemyDamageBase receiver, pBulletDamageData data, bool state)
            => hooks.GetMethod("Postfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object?[] { receiver, data, state });

        var kills = new List<(EnemyAgent Enemy, Agent? Source)>();
        void Record(EnemyAgent e, Agent? s) => kills.Add((e, s));
        Combat.Killed += Record;
        check(Patches.Ensured.Contains("Combat.Killed:CombatKillHooks"), "killed: the bullet patch goes on with the first subscription");

        var victim = new EnemyAgent { gameObject = new() };
        var receiverOf = new Dam_EnemyDamageBase { Owner = victim };
        var withSource = new pBulletDamageData { source = new pAgentRef { Value = source } };
        var state = Prefix(receiverOf);
        victim.Alive = false;
        Postfix(receiverOf, withSource, state);
        check(state && kills.Count == 1 && kills[0].Enemy == victim && kills[0].Source == source, "killed: a bullet that took a living enemy to dead on the host reports the enemy and the source");

        kills.Clear();
        Postfix(receiverOf, withSource, Prefix(receiverOf));
        check(kills.Count == 0, "killed: a hit on an enemy that was already dead reports nothing");

        var survivor = new EnemyAgent { gameObject = new() };
        var survivorReceiver = new Dam_EnemyDamageBase { Owner = survivor };
        Postfix(survivorReceiver, withSource, Prefix(survivorReceiver));
        check(kills.Count == 0, "killed: a hit the enemy survives reports nothing");

        var anonymous = new EnemyAgent { gameObject = new() };
        var anonymousReceiver = new Dam_EnemyDamageBase { Owner = anonymous };
        var anonymousState = Prefix(anonymousReceiver);
        anonymous.Alive = false;
        Postfix(anonymousReceiver, default, anonymousState);
        check(kills.Count == 1 && kills[0].Enemy == anonymous && kills[0].Source == null, "killed: a packet that names no source reports the kill with a null source");

        kills.Clear();
        SNetwork.SNet.IsMaster = false;
        var client = new EnemyAgent { gameObject = new() };
        var clientReceiver = new Dam_EnemyDamageBase { Owner = client };
        var clientState = Prefix(clientReceiver);
        client.Alive = false;
        Postfix(clientReceiver, withSource, clientState);
        SNetwork.SNet.IsMaster = true;
        check(!clientState && kills.Count == 0, "killed: a client reports nothing, the host's enemy is the one that dies");

        errors.Clear();
        var second = 0;
        void Failing(EnemyAgent e, Agent? s) => throw new InvalidOperationException("handler");
        void Counting(EnemyAgent e, Agent? s) => second++;
        Combat.Killed += Failing; Combat.Killed += Counting;
        var third = new EnemyAgent { gameObject = new() };
        var thirdReceiver = new Dam_EnemyDamageBase { Owner = third };
        var thirdState = Prefix(thirdReceiver);
        third.Alive = false;
        Postfix(thirdReceiver, withSource, thirdState);
        check(second == 1 && errors.Count == 1 && errors[0].Contains("Combat.Killed handler failed") && errors[0].Contains("handler"), "killed: a handler that throws is one error line and the others still run");

        Combat.Killed -= Record; Combat.Killed -= Failing; Combat.Killed -= Counting;
        kills.Clear();
        var last = new EnemyAgent { gameObject = new() };
        var lastReceiver = new Dam_EnemyDamageBase { Owner = last };
        var lastState = Prefix(lastReceiver);
        last.Alive = false;
        Postfix(lastReceiver, withSource, lastState);
        check(kills.Count == 0 && second == 1, "killed: an unsubscribed handler is not called again");

        var limbed = new Dam_EnemyDamageBase { Health = 100f, Settles = 7f };
        var limbShot = Combat.BulletDamage(limbed, 10f, source, at, dir, dir, true, 3, 0.5f, 2f);
        check(Is(limbShot.Dealt, 7f) && limbed.Calls.Single() == "bullet-limb 10 limb=3 directional=True stagger=0.5 precision=2 gear=0",
            "combat: a limb bullet names the limb position, the directional bonus and both multipliers, with gear category 0");
        var limb = new Dam_EnemyDamageLimb();
        Combat.BulletDamage(limb, 4f, source, at, dir, dir, false, 1f, 1f);
        check(limb.Calls.Single() == "limb-bullet 4 directional=False stagger=1 precision=1 gear=0", "combat: a limb bullet goes to the limb's own request");
        var meleed = new Dam_EnemyDamageBase { Health = 100f, Settles = 2f };
        check(Is(Combat.Melee(meleed, 5f, source, at, dir, 2, 0.5f, 1.5f).Dealt, 2f) && meleed.Calls.Single() == "melee-limb 5 limb=2 stagger=0.5 precision=1.5",
            "combat: a limb melee hit carries the limb and both multipliers");
        var exploded = new Dam_EnemyDamageBase { Health = 100f, Settles = 3f };
        check(Is(Combat.ExplosionDamage(exploded, 9f, at, dir, 4).Dealt, 3f) && exploded.Calls.Single() == "explosion-limb 9 limb=4", "combat: a limb explosion hit carries the limb");
        var player = new Player.Dam_PlayerDamageBase { Health = 100f, Settles = 1f };
        Combat.Melee(player, 3f, source, at, dir);
        Combat.Tentacle(player, 4f, source, at);
        Combat.Projectile(player, 5f, at);
        var fell = Combat.Fall(player, 6f);
        check(player.Calls.SequenceEqual(new[] { "player-melee 3", "tentacle 4", "projectile 5", "fall 6" }) && Is(fell.HealthBefore, 97f) && Is(fell.HealthAfter, 96f),
            "combat: each player damage kind is the receiver's own request and reports the health around it");

        var allocator = new GameEnemyAllocator { Result = source };
        var spawnNode = new AIG_CourseNode();
        GameEnemyAllocator.Current = null;
        check(!EnemySpawn.Ready && EnemySpawn.Request(24, spawnNode, AgentMode.Hibernate, at, Quaternion.identity) == null, "spawn: no allocator means no request is made");
        GameEnemyAllocator.Current = allocator;
        allocator.WasCollected = true;
        check(!EnemySpawn.Ready && allocator.Requests.Count == 0 && EnemySpawn.Request(24, spawnNode, AgentMode.Hibernate, at, Quaternion.identity) == null, "spawn: a collected allocator is not asked");
        allocator.WasCollected = false;
        check(EnemySpawn.Ready && ReferenceEquals(EnemySpawn.Request(24, spawnNode, AgentMode.Agressive, at, Quaternion.identity), source)
            && allocator.Requests.Single() == "24 Agressive (1,2,3) group=False index=-1", "spawn: the request reaches the game's allocator with no group and the enemy it returns comes back");
        GameEnemyAllocator.Current = null;    }
}
