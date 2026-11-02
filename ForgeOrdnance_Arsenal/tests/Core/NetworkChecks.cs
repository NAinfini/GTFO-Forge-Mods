using ForgeOrdnance;
using GTFO.API;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceArsenal.Tests;

internal static class NetworkChecks
{
    internal static void Run(Action<bool, string> check)
    {
        SNet.IsMaster = false;
        SNet.LocalPlayer = new() { Lookup = 7 };
        SNet.Master = new() { Lookup = 8 };
        Physics.Hit = false;
        Physics.Hits = Array.Empty<Collider>();
        Grenades.Clear();
        ArsenalNetwork.Register(); ArsenalNetwork.Clear();
        ArsenalAudio.ExplosionAt.Clear();
        Time.time = 2;
        var shot = new GrenadeShot { Shooter = 7, Loan = Guid.NewGuid(), Sequence = 1, VZ = 32 };
        Grenades.OnShot(8, shot);
        var projectile = GameObject.Created.Last();
        Physics.Hit = true;
        Grenades.Tick(3);
        check(!projectile.WasCollected && ArsenalAudio.ExplosionAt.Count == 0,
            "network: client wall collision cannot settle an explosion independently");
        var burst = new GrenadeBurst { Shooter = shot.Shooter, Loan = shot.Loan, Sequence = shot.Sequence, X = 11, Y = 3, Z = 5 };
        Grenades.OnBurst(99, burst);
        check(!projectile.WasCollected && ArsenalAudio.ExplosionAt.Count == 0, "network: only current host can settle a grenade");
        Grenades.OnBurst(8, burst);
        check(projectile.WasCollected && ArsenalAudio.ExplosionAt.Count == 1 && ArsenalAudio.ExplosionAt[0].x == 11,
            "network: shooter receives the host's final explosion position and removes prediction");
        Grenades.OnBurst(8, burst);
        Grenades.OnShot(8, shot);
        check(ArsenalAudio.ExplosionAt.Count == 1 && GameObject.Created.Last() == projectile,
            "network: duplicate burst and late shot cannot repeat settlement or recreate projectile");
        burst.Sequence = 2;
        Grenades.OnBurst(8, burst);
        shot.Sequence = 2;
        Grenades.OnShot(8, shot);
        check(ArsenalAudio.ExplosionAt.Count == 2 && GameObject.Created.Last() == projectile,
            "network: burst before launch still shows exactly one canonical explosion");
        shot.Sequence = 3;
        Grenades.OnShot(8, shot);
        projectile = GameObject.Created.Last();
        Grenades.Tick(11);
        check(projectile.WasCollected && ArsenalAudio.ExplosionAt.Count == 2,
            "network: missing settlement expires a projectile without inventing an explosion");
        Physics.Hit = false;

        // Each registration result must leave exactly the successful types with functioning shared services.
        foreach (var refused in new[] { StrikeSpec.TypeName, HeavyWeaponSpec.TypeName })
        {
            LevelAPI.Reset(); EventAPI.Reset(); NetworkAPI.Handlers.Clear();
            OrdnanceApi.Registration = type => type != refused;
            var frames = ForgeCore.Frame.Updates.Count;
            var plugin = new Plugin();
            plugin.Load();
            check(ForgeCore.Frame.Updates.Count == frames + 1 && NetworkAPI.Handlers.ContainsKey(GrenadeShot.Name),
                "network: partial registration still starts driver and receive services: " + refused);
            Grenades.Clear();
            shot.Sequence++;
            var count = GameObject.Created.Count;
            NetworkAPI.Deliver(GrenadeShot.Name, 8, shot);
            ArsenalNetwork.Pump();
            check(GameObject.Created.Count == count + 1, "network: surviving payload services actually process incoming work: " + refused);
            projectile = GameObject.Created.Last();
            EventAPI.Reload();
            check(projectile.WasCollected, "network: partial registration installs lifecycle cleanup: " + refused);

            foreach (var cleanup in new Action[] { EventAPI.Reload, ForgeCore.GameEvents.RaiseLevelEnded, ForgeCore.GameEvents.RaiseMaster })
            {
                shot.Sequence++;
                NetworkAPI.Deliver(GrenadeShot.Name, 8, shot);
                NetworkAPI.Deliver(GrenadeBurst.Name, 8, new GrenadeBurst
                { Shooter = 7, Loan = shot.Loan, Sequence = shot.Sequence, X = 50 });
                cleanup();
                count = GameObject.Created.Count;
                var sounds = ArsenalAudio.ExplosionAt.Count;
                ArsenalNetwork.Pump();
                check(GameObject.Created.Count == count && ArsenalAudio.ExplosionAt.Count == sounds,
                    "network: reset discards queued old launch and settlement: " + refused);
            }
        }
        LevelAPI.Reset(); EventAPI.Reset(); NetworkAPI.Handlers.Clear();
        OrdnanceApi.Registration = _ => false;
        var unavailableFrames = ForgeCore.Frame.Updates.Count;
        var unavailable = new Plugin(); unavailable.Load();
        check(ForgeCore.Frame.Updates.Count == unavailableFrames && NetworkAPI.Handlers.Count == 0,
            "network: two refused types do not initialize an unused plugin");
        OrdnanceApi.Registration = _ => true;
        Grenades.Clear(); ArsenalNetwork.Clear();
    }
}
