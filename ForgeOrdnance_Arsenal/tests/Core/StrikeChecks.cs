using System.Text.Json;
using Enemies;
using ForgeOrdnance;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceArsenal.Tests;

internal static class StrikeChecks
{
    private static bool Is(Vector3 p, EnemyAgent e) => Math.Abs(p.x - e.Position.x) < 1e-3f && Math.Abs(p.z - e.Position.z) < 1e-3f;

    internal static void Run(Action<bool, string> check)
    {
        Plugin.ExtraLights = new(false);
        SNet.IsMaster = true;
        SNet.LocalPlayer = new() { Lookup = 7 };
        SNet.Master = SNet.LocalPlayer;
        Physics.Hit = false;
        PlayerManager.PlayerAgentsInLevel.Clear();
        PlayerManager.Local = new PlayerAgent { gameObject = new(), IsLocallyOwned = true };
        PlayerManager.Local.FPSCamera.transform.position = default;
        var enemy = new EnemyAgent { gameObject = new() };
        var collider = new Collider { gameObject = new(), Owner = enemy };
        collider.transform.SetParent(enemy.transform, false);
        Physics.Hits = new[] { collider };

        StrikeSpec Spec(string json) => StrikeSpec.Parse(JsonDocument.Parse(json).RootElement);
        void Start(StrikeSpec spec, uint id = 90)
        {
            Strikes.Clear(); StrikeVisuals.Clear(); StrikeVisuals.BeginLevel();
            OrdnanceApi.Sent.Clear();
            ArsenalAudio.PlayedAt.Clear();
            Time.time = 100;
            new StrikePayload().OnBeaconLanded(new() { SummonerLookup = 7, ThrowId = id, Parameters = spec });
            Strikes.Start(new() { SummonerLookup = 7, ThrowId = id, Parameters = spec });
        }
        void Tick(float now) { Time.time = now; Strikes.Tick(now); StrikeVisuals.Tick(now); }
        int Live(string name) => GameObject.Created.Count(g => g.name == name && !g.WasCollected);

        OrdnanceApi.RetainState = true;
        Start(Spec("{\"Kind\":\"Precision\"}"));
        var explosions = DamageUtil.Explosions;
        Tick(103);
        var shell = OrdnanceApi.Sent.Single(m => m.Tag == (byte)DataTag.Shell);
        check(Wire.TryShell(shell.Data, out var record) && record.Fall == 0 && DamageUtil.Explosions == explosions + 1,
            "strikes: a host stall emits a valid immediate impact alongside exactly one damage settlement");
        check(ArsenalAudio.PlayedAt.Count(c => c == ArsenalAudio.Cue.BigBoom) == 1
            && ArsenalAudio.PlayedAt.Count(c => c == ArsenalAudio.Cue.Debris) == 0,
            "strikes: overdue shell plays its near impact on the receiving frame, its debris only after the debris delay");
        Tick(103 + StrikeTiming.DebrisDelay + 0.01f);
        check(ArsenalAudio.PlayedAt.Count(c => c == ArsenalAudio.Cue.Debris) == 1,
            "strikes: the debris is heard once, a moment after the blast");

        {
            Start(Spec("{\"Kind\":\"Gas\",\"Duration\":4,\"Tick\":0.5}"));
            var before = enemy.Damage.Burns;
            Tick(102); // the canister lands: the area waits for its start delay and hurts nothing yet
            check(enemy.Damage.Burns == before, "strikes: Gas does no damage between its landing and its area starting");
            Tick(102.81f); // zone begins and applies its initial pulse
            Tick(104.4f); // three more scheduled pulses, without moving the cadence
            var after = enemy.Damage.Burns;
            check(after - before == 4, "strikes: Gas catches up each active scheduled pulse after a stall");
            Tick(104.79f);
            check(enemy.Damage.Burns == after, "strikes: Gas retains its original cadence instead of drifting with frame time");
            Tick(107);
            Tick(110);
            check(enemy.Damage.Burns == after && OrdnanceApi.Sent.Count(m => m.Tag == (byte)DataTag.ZoneEnd) == 1,
                "strikes: Gas expires before any late pulse and ends once");
        }

        Start(Spec("{\"Kind\":\"Stasis\",\"Duration\":4,\"Slow\":0.3}"));
        var burnsBefore = enemy.Damage.Burns;
        Tick(102);
        check(!OrdnanceApi.Sent.Any(m => m.Tag == (byte)DataTag.Zone), "strikes: the stasis field is not sent while its start delay runs");
        Tick(103.1f);
        var stasisZone = OrdnanceApi.Sent.Single(m => m.Tag == (byte)DataTag.Zone);
        check(Wire.TryZone(stasisZone.Data, out var stasisRecord) && stasisRecord.Kind == StrikeKind.Stasis && Math.Abs(stasisRecord.Slow - 0.3f) < 1e-6f
            && enemy.Damage.Burns == burnsBefore,
            "strikes: stasis does no damage and sends its slow share to every peer");
        check(StasisSlow.Live.Count == 1 && Math.Abs(StasisSlow.Live[0].Factor - 0.3f) < 1e-6f && Math.Abs(StasisSlow.Live[0].Radius - stasisRecord.Radius) < 1e-6f,
            "strikes: the stasis zone record starts a slow field of its real radius on this peer");
        Tick(105); Tick(108);
        check(StasisSlow.Live.Count == 0 && OrdnanceApi.Sent.Count(m => m.Tag == (byte)DataTag.ZoneEnd) == 1,
            "strikes: the stasis field ends with its zone");

        Physics.Hits = Array.Empty<Collider>();
        Start(Spec("{\"Kind\":\"Beam\",\"Duration\":4,\"Tick\":0.5}"));
        Tick(100);
        var idleBeam = OrdnanceApi.Sent.Last(m => m.Tag == (byte)DataTag.Beam);
        Tick(101.6f); Tick(102.1f);
        idleBeam = OrdnanceApi.Sent.Last(m => m.Tag == (byte)DataTag.Beam);
        check(Wire.TryBeam(idleBeam.Data, out var idle) && !idle.HasTarget && Math.Abs(idle.TX) < 0.01f && Math.Abs(idle.TZ) < 0.01f,
            "strikes: a beam without enemies in reach holds still on the floor where it is");
        Physics.Hits = new[] { collider };

        Start(Spec("{\"Kind\":\"Beam\",\"Duration\":4,\"Tick\":0.5}"));
        Tick(100);
        check(!ArsenalAudio.PlayedAt.Contains(ArsenalAudio.Cue.BeamCutting), "strikes: a beam locks on nothing while its sky glow charges");
        Tick(101.6f);
        check(ArsenalAudio.PlayedAt.Contains(ArsenalAudio.Cue.BeamCutting)
            && Live("ForgeOrdnanceArsenal.BeamCuttingAudio") == 1,
            "strikes: a beam starts its cutting loop only while locked on a target");
        enemy.Alive = false;
        Tick(102.2f);
        check(Live("ForgeOrdnanceArsenal.BeamCuttingAudio") == 0,
            "strikes: losing the beam target stops its cutting loop owner");
        enemy.Alive = true;
        Start(Spec("{\"Kind\":\"Beam\",\"Duration\":4,\"Tick\":0.5}"), 91);
        var bullets = enemy.Damage.Bullets;
        Tick(100); Tick(103.1f);
        check(enemy.Damage.Bullets - bullets == 4, "strikes: beam catches up active pulses on its original timeline, counted from the end of its charge");
        Tick(106);
        check(enemy.Damage.Bullets - bullets == 4 && Live("ForgeOrdnanceArsenal.Beam") == 0,
            "strikes: expired beam applies no late damage and closes its visual");

        {
            // The beam chases the nearest living enemy to its own point, hurts it only while standing on it, and moves on when it dies.
            var near = new EnemyAgent { gameObject = new(), Position = new Vector3(6, 0, 0) };
            var nearCollider = new Collider { gameObject = new(), Owner = near };
            nearCollider.transform.SetParent(near.transform, false);
            var far = new EnemyAgent { gameObject = new(), Position = new Vector3(0, 0, -9) };
            var farCollider = new Collider { gameObject = new(), Owner = far };
            farCollider.transform.SetParent(far.transform, false);
            Physics.Hits = new[] { nearCollider, farCollider };
            Start(Spec("{\"Kind\":\"Beam\",\"Duration\":30,\"Speed\":4}"), 95);
            var now = 100f;
            void Run(float seconds) { for (var end = now + seconds; now < end;) { now += 1f / 20f; Tick(now); } }
            // The sky glow charges first (nothing moves or hurts); the beam goes active between two frames.
            Run(1.4f); now += 0.025f;
            Run(1f);
            var first = OrdnanceApi.Sent.Last(m => m.Tag == (byte)DataTag.Beam);
            check(Wire.TryBeam(first.Data, out var chasing) && chasing.HasTarget && chasing.TX > 1f && chasing.TX < 6f && chasing.TX <= 4f * 1f + 0.01f && Math.Abs(chasing.TZ) < 0.01f
                && near.Damage.Bullets == 0 && far.Damage.Bullets == 0,
                "strikes: the beam point moves toward the nearest living enemy at no more than its speed and hurts nothing on the way");
            Run(3f);
            check(near.Damage.Bullets > 0 && far.Damage.Bullets == 0, "strikes: the beam hurts its target once it stands on it");
            near.Alive = false;
            var nearBullets = near.Damage.Bullets;
            Run(2f);
            var second = OrdnanceApi.Sent.Last(m => m.Tag == (byte)DataTag.Beam);
            check(Wire.TryBeam(second.Data, out var moved) && moved.HasTarget && moved.TZ < -1f && moved.TX < 6f && near.Damage.Bullets == nearBullets,
                "strikes: when its target dies the beam retargets the next nearest enemy and leaves the dead one alone");
            far.Alive = false;
            Run(1.5f);
            var held = OrdnanceApi.Sent.Last(m => m.Tag == (byte)DataTag.Beam);
            Run(2f);
            var held2 = OrdnanceApi.Sent.Last(m => m.Tag == (byte)DataTag.Beam);
            check(Wire.TryBeam(held.Data, out var h1) && Wire.TryBeam(held2.Data, out var h2) && !h2.HasTarget && Math.Abs(h1.TX - h2.TX) < 0.05f && Math.Abs(h1.TZ - h2.TZ) < 0.05f,
                "strikes: with no living enemy in reach the beam stays where it stopped");
            Physics.Hits = new[] { collider };
        }

        {
            // The beam only goes for enemies it can walk to and see, follows the walkable path, and never moves through the level.
            var near = new EnemyAgent { gameObject = new(), Position = new Vector3(6, 0, 0) };
            var nearCollider = new Collider { gameObject = new(), Owner = near };
            nearCollider.transform.SetParent(near.transform, false);
            var far = new EnemyAgent { gameObject = new(), Position = new Vector3(0, 0, -9) };
            var farCollider = new Collider { gameObject = new(), Owner = far };
            farCollider.transform.SetParent(far.transform, false);
            Physics.Hits = new[] { nearCollider, farCollider };
            var now = 100f;
            void Begin(uint id) { Start(Spec("{\"Kind\":\"Beam\",\"Duration\":60,\"Speed\":4}"), id); now = 100f; Run(1.4f); now += 0.025f; }
            void Run(float seconds) { for (var end = now + seconds; now < end;) { now += 1f / 20f; Tick(now); } }
            BeamRecord Last() { Wire.TryBeam(OrdnanceApi.Sent.Last(m => m.Tag == (byte)DataTag.Beam).Data, out var r); return r; }

            ForgeCore.Level.Reset();
            Begin(96);
            ForgeCore.Level.Walkable = p => !Is(p, near);
            Run(2f);
            check(Last().HasTarget && Last().TZ < -1f && Math.Abs(Last().TX) < 0.01f && near.Damage.Bullets == 0,
                "strikes: an enemy with no walkable path (behind a wall, in another room) is not chosen even when it is nearer; the next one is");
            ForgeCore.Level.Reset();
            Begin(97);
            ForgeCore.Level.Sight = p => !Is(p, near);
            Run(2f);
            check(Last().HasTarget && Last().TZ < -1f && Math.Abs(Last().TX) < 0.01f,
                "strikes: an enemy the beam cannot see is not chosen even when its path is clear");
            ForgeCore.Level.Reset();
            Begin(98);
            Run(1f);
            check(Last().HasTarget && Last().TX > 1f, "strikes: with a path and sight the nearest enemy is chased");
            ForgeCore.Level.Walkable = p => !Is(p, near);
            Run(1f);
            check(Last().HasTarget && Last().TZ < -1f, "strikes: when a door closes on its target the beam takes the next reachable enemy");
            ForgeCore.Level.Walkable = _ => false;
            Run(1f);
            var stillHere = Last();
            Run(3f);
            check(!Last().HasTarget && Math.Abs(Last().TX - stillHere.TX) < 0.05f && Math.Abs(Last().TZ - stillHere.TZ) < 0.05f,
                "strikes: with no enemy it can walk to the beam holds where it is");
            ForgeCore.Level.Walkable = _ => true;
            Run(1f);
            check(Last().HasTarget, "strikes: when a door opens the beam goes on again");

            ForgeCore.Level.Reset();
            ForgeCore.Level.Wall = (a, b) => a.x < 3f && b.x >= 3f;
            Begin(99);
            var maxX = 0f;
            var cutBefore = near.Damage.Bullets;
            for (var i = 0; i < 160; i++) { Run(1f / 20f); maxX = Math.Max(maxX, Last().TX); }
            check(maxX <= 3.001f && near.Damage.Bullets == cutBefore,
                "strikes: a wall across the beam path stops it; the beam point never passes it and the enemy behind it is not cut");
            ForgeCore.Level.Reset();
            Begin(100);
            Run(4f);
            check(ForgeCore.Level.Searches <= 4 * 5 + 2, "strikes: the path to the target is searched at most five times a second");
            ForgeCore.Level.Reset();
            Physics.Hits = new[] { collider };
        }

        OrdnanceApi.RetainState = false;
        Start(Spec("{\"Kind\":\"Gas\"}"));
        var burns = enemy.Damage.Burns;
        Tick(102); Tick(102.9f);
        check(enemy.Damage.Burns == burns && Live("ForgeOrdnanceArsenal.GasArea") == 0
            && OrdnanceApi.Sent.Count(m => m.Tag == (byte)DataTag.ZoneEnd) == 1,
            "strikes: failed zone retention retracts dispatched visuals and cancels all damage");
        Start(Spec("{\"Kind\":\"Beam\"}"));
        Tick(100);
        bullets = enemy.Damage.Bullets;
        Tick(101);
        check(enemy.Damage.Bullets == bullets && Live("ForgeOrdnanceArsenal.Beam") == 0
            && OrdnanceApi.Sent.Count(m => m.Tag == (byte)DataTag.BeamEnd) == 1,
            "strikes: failed beam retention cancels future damage and retracts the dispatched beam");
        // Cluster scatter: every peer derives the same sparklet points from the call and the bomblet's impact point.
        var a = new float[ClusterScatter.Sparklets * 2];
        var b = new float[ClusterScatter.Sparklets * 2];
        var seed = ClusterScatter.Seed(7, 42, 3.25f, -8.5f);
        ClusterScatter.Offsets(seed, ClusterScatter.Sparklets, 3f, a);
        ClusterScatter.Offsets(ClusterScatter.Seed(7, 42, 3.25f, -8.5f), ClusterScatter.Sparklets, 3f, b);
        check(a.SequenceEqual(b), "scatter: the same call and impact give the same sparklet points on every peer");
        ClusterScatter.Offsets(ClusterScatter.Seed(7, 43, 3.25f, -8.5f), ClusterScatter.Sparklets, 3f, b);
        check(!a.SequenceEqual(b), "scatter: another throw scatters differently");
        ClusterScatter.Offsets(ClusterScatter.Seed(7, 42, 4.5f, -8.5f), ClusterScatter.Sparklets, 3f, b);
        check(!a.SequenceEqual(b), "scatter: another bomblet of the same call scatters differently");
        var inside = true;
        for (var i = 0; i < ClusterScatter.Sparklets; i++)
        {
            var distance = MathF.Sqrt(a[2 * i] * a[2 * i] + a[2 * i + 1] * a[2 * i + 1]);
            inside &= distance >= 1.5f - 1e-3f && distance <= 3f + 1e-3f;
        }
        check(inside, "scatter: every point lies between half and all of the radius");
        check(a[0] * a[2] + a[1] * a[3] < 3f * 3f * 0.9f, "scatter: the sparklets go in different directions");
        OrdnanceApi.RetainState = true;
        Strikes.Clear(); StrikeVisuals.Clear(); Physics.Hits = Array.Empty<Collider>();
    }
}
