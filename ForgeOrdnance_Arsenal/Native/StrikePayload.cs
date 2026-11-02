using System;
using System.Collections.Generic;
using System.Text.Json;
using Enemies;
using ForgeOrdnance;
using GTFO.API;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>Payload type <c>arsenal.Strike</c>: the nine offensive call-ins. The host plans and resolves every shell, zone and
/// beam (<see cref="Strikes"/>) and tells every peer what to show through ForgeOrdnance's per-call data; every peer, the host
/// included, shows it (<see cref="StrikeVisuals"/>).</summary>
internal sealed class StrikePayload : IOrdnancePayload
{
    public StrikePayload() => LevelAPI.OnBuildDone += StrikeVisuals.BeginLevel;

    public void OnBeaconLanded(OrdnancePresentation presentation) => StrikeVisuals.AdmitBeam(presentation);

    public object Parse(JsonElement payload) => StrikeSpec.Parse(payload);

    /// <summary>Strikes shake the camera on each impact instead of once on arrival.</summary>
    public bool HasOwnArrival => true;

    public void Execute(OrdnanceExecution execution) => Strikes.Start(execution);

    public void OnData(OrdnancePayloadData data) => StrikeVisuals.Receive(data);
}

/// <summary>Host only: the strikes in flight. Cleared when calls in flight are voided (level exit, checkpoint reload, host
/// change), like ForgeOrdnance's own calls.</summary>
internal static class Strikes
{
    private const float BeamSendSeconds = 0.4f;
    /// <summary>The beam hurts its target while its floor point is within this many metres of it (horizontally).</summary>
    private const float BeamHitRadius = 1.5f;
    /// <summary>The beam looks for its next enemy at most this often (5 Hz); a dead or despawned target is noticed every frame.</summary>
    private const float BeamScanSeconds = 0.2f;
    /// <summary>While the beam moves its state is sent this often; at rest every <see cref="BeamSendSeconds"/>.</summary>
    private const float BeamMoveSendSeconds = 0.2f;
    /// <summary>Path searches one scan may spend, nearest candidate first.</summary>
    private const int BeamPathBudget = 6;
    /// <summary>An enemy whose route the beam was stopped on is not chosen again for this long.</summary>
    private const float BeamBlockedSeconds = 1f;

    private sealed class Call
    {
        internal StrikeSpec Spec = null!;
        internal ulong Summoner;
        internal uint ThrowId;
        internal Guid Generation;
        internal PlayerAgent? Source;
        internal Vector3 Landing;
    }

    private sealed class Shell
    {
        internal Call Call = null!;
        internal Vector3 Impact;
        internal float LaunchAt, ImpactAt;
        internal bool Launched;
        internal EnemyAgent? Target;
    }

    private sealed class Zone
    {
        internal Call Call = null!;
        internal Vector3 Centre;
        /// <summary>When the area starts (the shell's landing plus its start delay); until then it neither shows nor hurts.</summary>
        internal float StartsAt, EndsAt, NextTick;
        internal bool Started;
    }

    private sealed class Beam
    {
        internal Call Call = null!;
        internal Vector3 Top;
        /// <summary>The floor point the beam stands on and the navigation path it walks to <see cref="Target"/>, refreshed at every scan; the point's height follows the floor when sent.</summary>
        internal BeamRoute Route = null!;
        internal float Floor;
        /// <summary>The beam cuts and moves from <see cref="ActiveAt"/> on (the sky glow charges until then); <see cref="EndsAt"/> counts from it.</summary>
        internal float Started, ActiveAt, EndsAt, NextTick, NextSend, NextScan, LastStep, BlockedUntil;
        internal EnemyAgent? Target, Blocked;
        internal bool WasMoving;
    }

    private static Guid _generation = Guid.NewGuid();
    private static readonly List<Shell> Shells = new();
    private static readonly List<Zone> Zones = new();
    private static readonly List<Beam> Beams = new();
    private static readonly float[] HealthBuffer = new float[256];
    private static readonly float[] DistanceBuffer = new float[256];
    private static readonly float[] ScratchBuffer = new float[256];
    private static readonly EnemyAgent?[] Candidates = new EnemyAgent?[256];

    internal static void Start(OrdnanceExecution e)
    {
        var spec = (StrikeSpec)e.Parameters;
        var now = Time.time;
        var call = new Call { Generation = _generation, Spec = spec, Summoner = e.SummonerLookup, ThrowId = e.ThrowId, Source = e.Summoner, Landing = e.Position };
        switch (spec.Kind)
        {
            case StrikeKind.Beam:
            {
                var top = Top(e.Position);
                var active = now + StrikeTiming.BeamChargeSeconds;
                Beams.Add(new Beam { Call = call, Top = top, Route = new BeamRoute(e.Position.x, e.Position.y, e.Position.z), Floor = e.Position.y, Started = now, ActiveAt = active, EndsAt = active + spec.Duration, NextTick = active, NextSend = now, NextScan = active, LastStep = now });
                return;
            }
            case StrikeKind.Hunter:
            {
                var target = LargestNear(e.Position, spec.Spread);
                var impact = target != null ? Ground(target.Position) : e.Position;
                Shells.Add(new Shell { Call = call, Impact = impact, LaunchAt = now, ImpactAt = now + StrikeTiming.FallSeconds, Target = target });
                return;
            }
        }
        var plan = StrikePlan.Build(spec, StrikePlan.Seed(e.SummonerLookup, e.ThrowId));
        foreach (var p in plan)
        {
            var offset = e.Rotation * new Vector3(p.Right, 0f, p.Forward);
            var impact = p.Right == 0f && p.Forward == 0f ? e.Position : Ground(e.Position + offset);
            Shells.Add(new Shell { Call = call, Impact = impact, LaunchAt = now + p.Time, ImpactAt = now + p.Time + StrikeTiming.FallSeconds });
        }
    }

    internal static void Tick(float now)
    {
        for (var i = Shells.Count - 1; i >= 0; i--)
        {
            var s = Shells[i];
            try
            {
                if (!s.Launched && now >= s.LaunchAt)
                {
                    s.Launched = true;
                    Send(s.Call, DataTag.Shell, Wire.Shell(new ShellRecord(StrikePlan.Look(s.Call.Spec.Kind), s.Call.Spec.Kind, s.Impact.x, s.Impact.y, s.Impact.z,
                        Top(s.Impact).y, Math.Max(0f, s.ImpactAt - now), s.Call.Spec.Kind is StrikeKind.Gas or StrikeKind.Stasis ? s.Call.Spec.ZoneRadius : s.Call.Spec.BlastRadius)));
                }
                if (now < s.ImpactAt) continue;
                Impact(s, now);
                Shells.RemoveAt(i);
            }
            catch (Exception error)
            {
                Shells.RemoveAt(i);
                if (s.Call.Spec.Kind is StrikeKind.Gas or StrikeKind.Stasis or StrikeKind.Incendiary)
                    End(s.Call, DataTag.ZoneEnd);
                Plugin.Error(s.Call.Spec.Kind + " shell of throw " + s.Call.ThrowId + " failed: " + error);
            }
        }
        for (var i = Zones.Count - 1; i >= 0; i--)
        {
            var z = Zones[i];
            try
            {
                if (!z.Started)
                {
                    if (now < z.StartsAt) continue;
                    z.Started = true;
                    z.EndsAt = now + z.Call.Spec.Duration;
                    z.NextTick = now;
                    StartZone(z, now);
                }
                if (now >= z.EndsAt)
                {
                    Zones.RemoveAt(i);
                    End(z.Call, DataTag.ZoneEnd);
                    continue;
                }
                // Stasis does no damage: every peer slows the enemies inside from the zone record (StasisSlow).
                if (z.Call.Spec.Kind == StrikeKind.Stasis) continue;
                while (now >= z.NextTick)
                {
                    z.NextTick += z.Call.Spec.Tick;
                    VanillaCombat.Burn(z.Centre, z.Call.Spec.ZoneRadius, z.Call.Spec.TickDamage, Alive(z.Call.Source));
                }
            }
            catch (Exception error)
            {
                Zones.RemoveAt(i);
                End(z.Call, DataTag.ZoneEnd);
                Plugin.Error(z.Call.Spec.Kind + " area of throw " + z.Call.ThrowId + " failed: " + error);
            }
        }
        for (var i = Beams.Count - 1; i >= 0; i--)
        {
            var b = Beams[i];
            try
            {
                if (now >= b.EndsAt)
                {
                    Beams.RemoveAt(i);
                    End(b.Call, DataTag.BeamEnd);
                    continue;
                }
                BeamMove(b, now);
                while (now >= b.NextTick)
                {
                    b.NextTick += b.Call.Spec.Tick;
                    BeamTick(b);
                }
            }
            catch (Exception error)
            {
                Beams.RemoveAt(i);
                End(b.Call, DataTag.BeamEnd);
                Plugin.Error("Cutting beam of throw " + b.Call.ThrowId + " failed: " + error);
            }
        }
    }

    private static void Impact(Shell s, float now)
    {
        var spec = s.Call.Spec;
        switch (spec.Kind)
        {
            case StrikeKind.Gas:
            case StrikeKind.Stasis:
                PlanZone(s.Call, s.Impact, now);
                break;
            case StrikeKind.Incendiary:
                ForgeCore.Combat.Explode(s.Impact, spec.BlastRadius, spec.Damage, spec.Force);
                PlanZone(s.Call, s.Impact, now);
                break;
            case StrikeKind.Hunter:
                var target = s.Target;
                if (target != null && !target.WasCollected && target.Alive)
                    ForgeCore.Combat.ExplosionDamage(target.Damage, spec.Damage, s.Impact, Vector3.up * spec.Force);
                ForgeCore.Combat.Explode(s.Impact, spec.BlastRadius, spec.TickDamage, spec.Force);
                break;
            default:
                ForgeCore.Combat.Explode(s.Impact, spec.BlastRadius, spec.Damage, spec.Force);
                break;
        }
    }

    /// <summary>The area waits for its start delay after the shell lands (the release, pulse or ignition is heard first), then
    /// <see cref="StartZone"/> shows it on every peer and starts its damage or slow.</summary>
    private static void PlanZone(Call call, Vector3 centre, float now)
        => Zones.Add(new Zone { Call = call, Centre = centre, StartsAt = now + StrikeTiming.ZoneStartDelay(call.Spec.Kind) });

    private static void StartZone(Zone zone, float now)
    {
        var call = zone.Call;
        var centre = zone.Centre;
        var spec = call.Spec;
        var slow = spec.Kind == StrikeKind.Stasis ? spec.Slow : 1f;
        State(call, DataTag.Zone, Wire.Zone(new ZoneRecord(spec.Kind, centre.x, centre.y, centre.z, spec.ZoneRadius, spec.Duration, slow)));
    }

    private struct BeamReach : IReach
    {
        internal Beam Beam;
        internal Vector3 Here;
        internal float Now;

        public readonly bool Reaches(int index)
        {
            var enemy = Candidates[index]!;
            if (ReferenceEquals(enemy, Beam.Blocked) && Now < Beam.BlockedUntil) return false;
            if (!TryRoute(Here, enemy, Beam.Call.Spec.Spread, Beam.Route)) return false;
            if (ForgeCore.Level.HasSight(Here, AimPoint(enemy))) return true;
            Beam.Route.Begin();
            return false;
        }
    }

    private struct BeamBlocker : IBlocker
    {
        public readonly bool Blocked(float ax, float ay, float az, float bx, float by, float bz)
            => ForgeCore.Level.Blocked(new Vector3(ax, ay, az), new Vector3(bx, by, bz));
    }

    private static readonly Vector3[] RouteCorners = new Vector3[BeamRoute.Capacity];

    /// <summary>Where the beam looks at an enemy: its aim point, else a metre above its feet.</summary>
    private static Vector3 AimPoint(EnemyAgent enemy) => enemy.AimTarget != null ? enemy.AimTarget.position : enemy.Position + Vector3.up;

    /// <summary>The game's navigation path from the floor point to the enemy (<c>Level.TryRoute</c>, at most <paramref name="maxLength"/>
    /// metres) written into <paramref name="route"/>, which is left empty when there is none or the path has more corners than a route holds.</summary>
    private static bool TryRoute(Vector3 from, EnemyAgent enemy, float maxLength, BeamRoute route)
    {
        route.Begin();
        if (!ForgeCore.Level.TryRoute(from, enemy.Position, maxLength, RouteCorners, out var count)) return false;
        for (var i = 0; i < count; i++)
            if (!route.Add(RouteCorners[i].x, RouteCorners[i].y, RouteCorners[i].z)) return false;
        return true;
    }
    /// <summary>The nearest living enemy within reach of the beam point that it can walk to (a complete navigation path of at most
    /// <c>Spread</c> metres) and see from above its point; its path is left in the beam route. Null when there is none.</summary>
    private static EnemyAgent? PickReachable(Beam b, Vector3 here, float now)
    {
        var found = ForgeCore.Enemies.Near(here, b.Call.Spec.Spread);
        var n = Math.Min(found.Length, DistanceBuffer.Length);
        for (var i = 0; i < n; i++)
        {
            Candidates[i] = found[i];
            DistanceBuffer[i] = (found[i]!.Position - here).sqrMagnitude;
        }
        var probe = new BeamReach { Beam = b, Here = here, Now = now };
        var best = Targeting.NearestReachable(DistanceBuffer.AsSpan(0, n), ScratchBuffer, BeamPathBudget, ref probe);
        var enemy = best < 0 ? null : Candidates[best];
        Array.Clear(Candidates, 0, n);
        if (enemy == null) b.Route.Begin();
        return enemy;
    }

    /// <summary>Every frame: keeps the beam target, walks the floor point along the navigation path toward it (each move checked
    /// against the level first) and sends the point to every peer. At most five times a second the target path is searched again
    /// (it moves); a target that can no longer be walked to, or is dead, gives way to the nearest one that can, and with none the
    /// beam holds where it is.</summary>
    private static void BeamMove(Beam b, float now)
    {
        var spec = b.Call.Spec;
        var dt = Math.Clamp(now - b.LastStep, 0f, 0.1f);
        b.LastStep = now;
        var charged = now >= b.ActiveAt;
        var here = new Vector3(b.Route.X, b.Floor, b.Route.Z);
        var target = b.Target;
        var gone = target != null && (target.WasCollected || !target.Alive);
        if (charged && (gone || now >= b.NextScan))
        {
            b.NextScan = now + BeamScanSeconds;
            if (target == null || gone || !TryRoute(here, target, spec.Spread, b.Route))
            {
                var next = PickReachable(b, here, now);
                b.Target = target = next;
                b.NextSend = now;
            }
        }
        if (charged && target != null)
        {
            var blocker = new BeamBlocker();
            if (b.Route.Step(spec.Speed * dt, ref blocker) == RouteStep.Blocked)
            {
                b.Blocked = target;
                b.BlockedUntil = now + BeamBlockedSeconds;
                b.Target = target = null;
                b.Route.Begin();
                b.NextScan = now;
                b.NextSend = now;
            }
        }
        else b.Route.Moved = 0f;
        if (now < b.NextSend) return;
        here = Ground(new Vector3(b.Route.X, b.Floor, b.Route.Z));
        b.Floor = here.y;
        b.Route.Y = here.y;
        b.Top = Top(here);
        var moving = b.Route.Moved > 0f;
        b.NextSend = now + (moving || b.WasMoving ? BeamMoveSendSeconds : BeamSendSeconds);
        b.WasMoving = moving;
        State(b.Call, DataTag.Beam, Wire.Beam(new BeamRecord(b.Call.Generation, b.Top.x, b.Top.y, b.Top.z, here.x, here.y, here.z, target != null)));
    }

    private static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.x - b.x;
        var dz = a.z - b.z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Every <c>Tick</c> seconds: the beam cuts its target while its floor point stands on it.</summary>
    private static void BeamTick(Beam b)
    {
        var target = b.Target;
        if (target == null || target.WasCollected || !target.Alive) return;
        var here = new Vector3(b.Route.X, b.Floor, b.Route.Z);
        if (Flat(target.Position, here) > BeamHitRadius || !ForgeCore.Level.HasSight(here, AimPoint(target))) return;
        var point = AimPoint(target);
        var direction = (point - b.Top).normalized;
        ForgeCore.Combat.BulletDamage(target.Damage, b.Call.Spec.TickDamage, Alive(b.Call.Source), point, direction, -direction);
    }

    private static PlayerAgent? Alive(PlayerAgent? player) => player == null || player.WasCollected ? null : player;

    private static void Send(Call call, DataTag tag, byte[] data) => OrdnanceApi.SendData(StrikeSpec.TypeName, call.Summoner, call.ThrowId, (byte)tag, data);

    /// <summary>A zone or beam's whole current state, kept by ForgeOrdnance and replayed to players who join later.</summary>
    private static void State(Call call, DataTag tag, byte[] data)
    {
        if (OrdnanceApi.SendState(StrikeSpec.TypeName, call.Summoner, call.ThrowId, (byte)tag, data)) return;
        // dispatches even a state it could not retain. The owning tick's failure path retracts it.
        throw new InvalidOperationException("The host could not retain " + tag + " state for late joiners; effect cancelled.");
    }

    /// <summary>Ends a zone or beam on every peer and forgets its kept state.</summary>
    private static void End(Call call, DataTag tag)
    {
        Send(call, tag, tag == DataTag.BeamEnd ? call.Generation.ToByteArray() : Array.Empty<byte>());
        OrdnanceApi.ClearState(StrikeSpec.TypeName, call.Summoner, call.ThrowId);
    }

    private static EnemyAgent? LargestNear(Vector3 centre, float radius)
    {
        var enemies = ForgeCore.Enemies.Near(centre, radius);
        var n = Math.Min(enemies.Length, HealthBuffer.Length);
        for (var i = 0; i < n; i++)
        {
            HealthBuffer[i] = enemies[i]!.Damage.HealthMax;
            DistanceBuffer[i] = (enemies[i]!.Position - centre).magnitude;
        }
        var best = Targeting.Largest(HealthBuffer.AsSpan(0, n), DistanceBuffer.AsSpan(0, n));
        return best < 0 ? null : enemies[best];
    }

    /// <summary>The floor under a point. The downward ray starts at most 3 m up and always below whatever is overhead, so a
    /// low ceiling (indoors, corridors) is never taken for the floor; without a floor within 9 m below, the point itself.</summary>
    internal static Vector3 Ground(Vector3 point)
    {
        var lift = 3f;
        if (Physics.Raycast(point + Vector3.up * 0.1f, Vector3.up, out var roof, 2.9f, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore))
            lift = Math.Max(0.1f, roof.point.y - point.y - 0.15f);
        return ForgeCore.Level.FloorBelow(point + Vector3.up * lift, lift + 9f, out var hit) ? hit.point : point;
    }

    /// <summary>Where a shell starts: 3 m above the ceiling over the impact (levels are underground), else 30 m up.</summary>
    internal static Vector3 Top(Vector3 impact)
        => Physics.Raycast(impact + Vector3.up * 0.5f, Vector3.up, out var hit, 40f, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore)
            ? new Vector3(impact.x, hit.point.y + 3f, impact.z) : impact + Vector3.up * 30f;

    internal static void Clear()
    {
        _generation = Guid.NewGuid();
        Shells.Clear();
        Zones.Clear();
        Beams.Clear();
    }
}
