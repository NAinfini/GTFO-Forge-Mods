using System;
using System.Collections.Generic;
using System.Text.Json;
using AIGraph;
using Enemies;
using Player;
using UnityEngine;

namespace ForgeOrdnance.Arsenal;

/// <summary>Payload type <c>arsenal.Strike</c>: the nine offensive call-ins. The host plans and resolves every shell, zone and
/// beam (<see cref="Strikes"/>) and tells every peer what to show through the carrier's per-call data; every peer, the host
/// included, shows it (<see cref="StrikeVisuals"/>).</summary>
internal sealed class StrikePayload : IOrdnancePayload
{
    /// <summary>This type as the carrier registered it: what the strikes' data and states are sent with.</summary>
    internal static RegisteredPayload Type = null!;

    public void OnBeaconLanded(OrdnancePresentation presentation)
    {
        StrikeVisuals.AdmitBeam(presentation);
        if (ForgeCore.Session.IsHost) Strikes.Announce(presentation);
    }

    /// <summary>A level's developer drop has no beacon, so its beam is admitted here, the same as a landed beacon would admit it (a beam update
    /// that came first is only dropped; the host sends the beam's state again every few seconds).</summary>
    public void OnArrived(OrdnancePresentation presentation) => StrikeVisuals.AdmitBeam(presentation);

    public object Parse(JsonElement payload) => StrikeSpec.Parse(payload);

    /// <summary>Strikes shake the camera on each impact instead of once on arrival.</summary>
    public bool HasOwnArrival => true;

    /// <summary>A strike has no vehicle to leave out: a level's developer drop has no beacon and its shells start at the point at once, so
    /// <c>Direct</c> and <c>Drop</c> are the same arrival. Declared so a level may place a strike at an author-written point, once, like any other call-in.</summary>
    public bool SupportsDirect => true;

    public void Execute(OrdnanceExecution execution) => Strikes.Start(execution);

    public void OnData(OrdnancePayloadData data) => StrikeVisuals.Receive(data);
}

/// <summary>Host only: the strikes in flight. Cleared when calls in flight are voided (level exit, checkpoint reload, host
/// change), like the carrier's own calls.</summary>
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
        internal OrdnanceDefinition Definition = null!;
        internal ulong Summoner;
        internal uint ThrowId;
        internal Guid Generation;
        internal PlayerAgent? Source;
        internal Vector3 Landing;
        /// <summary>The course node the strike landed in (null: none found); the hunter reaches only enemies in it.</summary>
        internal AIG_CourseNode? Room;
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

    /// <summary>The hunter's laser while it looks for its target or stays on it; its shell is an ordinary <see cref="Shell"/>.</summary>
    private sealed class Hunt
    {
        internal Call Call = null!;
        internal Vector3 Beacon, LastPoint;
        internal EnemyAgent? Target;
        /// <summary>The lock runs out at <see cref="LockUntil"/> (0: none yet); with no target the laser gives up at <see cref="GivesUpAt"/>.</summary>
        internal float LockUntil, GivesUpAt, NextScan, NextSend, LastSend;
    }

    /// <summary>A strafing run: the strip from the beacon along its direction, swept by the head that advances at the spec's speed.</summary>
    private sealed class Run
    {
        internal Call Call = null!;
        internal Vector3 Origin, Direction;
        internal float StartsAt, NextTick, Along;
        /// <summary>The strip's length after the first wall ahead cut it.</summary>
        internal float Length;
        /// <summary>The enemies this run has already hit (by instance id): one that walks on with the head is hit once.</summary>
        internal readonly HashSet<int> Hit = new();
    }

    /// <summary>Metres above and below the beacon's height the run still hits.</summary>
    private const float RunReachHeight = 4f;
    /// <summary>Metres above the floor a blast starts, so a bump in the floor does not shadow what it should reach.</summary>
    private const float BlastLift = 0.25f;
    /// <summary>Metres above the floor the line that looks for the wall ahead runs (over crates, under most ceilings), and how far before the wall it stops.</summary>
    private const float WallLookHeight = 1.6f, WallMargin = 0.4f;
    /// <summary>Which way each call faces, fixed when its beacon lands (not where the thrower walked to since).</summary>
    private static readonly Dictionary<(ulong Summoner, uint ThrowId), Quaternion> Facing = new();

    private static readonly List<Run> Runs = new();
    private static Guid _generation = Guid.NewGuid();
    private static readonly List<Shell> Shells = new();
    private static readonly List<Zone> Zones = new();
    private static readonly List<Beam> Beams = new();
    private static readonly List<Hunt> Hunts = new();
    private static readonly float[] DistanceBuffer = new float[256];
    private static readonly float[] ScratchBuffer = new float[256];
    private static readonly EnemyAgent?[] Candidates = new EnemyAgent?[256];

    /// <summary>The beacon landed: the call's direction is fixed now and every peer is told the ground it can hurt.</summary>
    internal static void Announce(OrdnancePresentation p)
    {
        if (p.Parameters is not StrikeSpec spec) return;
        var rotation = Vec.Identity;
        var thrower = ForgeCore.Level.PlayerByLookup(p.SummonerLookup);
        if (thrower != null)
        {
            var flat = p.Position - thrower.transform.position;
            flat.y = 0f;
            if (Vec.SqrLength(flat) > 0.01f) rotation = Quaternion.LookRotation(flat.normalized);
        }
        Facing[(p.SummonerLookup, p.ThrowId)] = rotation;
        SendDanger(spec, p.SummonerLookup, p.ThrowId, p.Position, rotation, p.Delay);
    }

    private static void SendDanger(StrikeSpec spec, ulong summoner, uint throwId, Vector3 at, Quaternion rotation, float delay)
    {
        var direction = Ahead(rotation);
        var length = spec.Kind == StrikeKind.Strafe ? ClearAhead(at, direction, spec.Length) : spec.Length;
        if (StrikeFootprint.Of(spec, length, delay) is not { } footprint) return;
        OrdnanceApi.SendData(StrikePayload.Type, summoner, throwId, (byte)DataTag.Danger, Wire.Danger(new DangerRecord(spec.Kind, at.x, at.y, at.z, direction.x, direction.z, footprint)));
    }

    private static Vector3 Ahead(Quaternion rotation)
    {
        var direction = rotation * new Vector3(0f, 0f, 1f);
        direction.y = 0f;
        return Vec.SqrLength(direction) < 0.0001f ? new Vector3(0f, 0f, 1f) : direction.normalized;
    }

    /// <summary>How many metres from a floor point the way along <paramref name="direction"/> is open, at most <paramref name="length"/>.</summary>
    private static float ClearAhead(Vector3 from, Vector3 direction, float length)
    {
        var lift = ForgeCore.Level.LiftUnderRoof(from, WallLookHeight, 0.2f);
        return Physics.Raycast(from + Vec.Up * lift, direction, out var wall, length, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore)
            ? Math.Max(1f, wall.distance - WallMargin) : length;
    }

    /// <summary>The planned point, brought back to this side of the first wall between it and the beacon: a shell of the pattern lands in the room it was called into
    /// or through an opening, never in rock or behind a wall.</summary>
    private static Vector3 WithinWalls(Vector3 origin, Vector3 point)
    {
        var offset = point - origin;
        offset.y = 0f;
        var distance = Vec.Length(offset);
        if (distance < 0.01f) return origin;
        var direction = offset * (1f / distance);
        return origin + direction * Math.Min(distance, ClearAhead(origin, direction, distance));
    }

    internal static void Start(OrdnanceExecution e)
    {
        var spec = (StrikeSpec)e.Parameters;
        var now = Time.time;
        var call = new Call { Generation = _generation, Spec = spec, Definition = e.Definition, Summoner = e.SummonerLookup, ThrowId = e.ThrowId, Source = e.Summoner, Landing = e.Position, Room = e.CourseNode };
        // A call with no beacon (a level's developer drop) or one a new host took over shows its ground now.
        if (!Facing.Remove((e.SummonerLookup, e.ThrowId), out var rotation))
        {
            rotation = e.Rotation;
            SendDanger(spec, e.SummonerLookup, e.ThrowId, e.Position, rotation, 0f);
        }
        switch (spec.Kind)
        {
            case StrikeKind.Beam:
                {
                    var top = Top(e.Position);
                    var active = now + StrikeTiming.BeamChargeSeconds;
                    Beams.Add(new Beam { Call = call, Top = top, Route = new BeamRoute(e.Position.x, e.Position.y, e.Position.z), Floor = e.Position.y, Started = now, ActiveAt = active, EndsAt = active + spec.Duration, NextTick = active, NextSend = now, NextScan = active, LastStep = now });
                    return;
                }
            case StrikeKind.Strafe:
                {
                    var direction = Ahead(rotation);
                    var starts = now + StrikeTiming.StrafeLeadSeconds;
                    var length = ClearAhead(e.Position, direction, spec.Length);
                    Runs.Add(new Run { Call = call, Origin = e.Position, Direction = direction, StartsAt = starts, NextTick = starts, Length = length });
                    Send(call, DataTag.Strafe, Wire.Strafe(new StrafeRecord(e.Position.x, e.Position.y, e.Position.z, direction.x, direction.z, length, spec.Speed, StrikeTiming.StrafeLeadSeconds, spec.Width * 0.5f)));
                    return;
                }
            case StrikeKind.Hunter:
                {
                    Hunts.Add(new Hunt { Call = call, Beacon = e.Position, LastPoint = e.Position, GivesUpAt = now + StrikeTiming.HunterSearchSeconds, LastSend = now });
                    return;
                }
        }
        var plan = StrikePlan.Build(spec, StrikePlan.Seed(e.SummonerLookup, e.ThrowId));
        foreach (var p in plan)
        {
            var offset = rotation * new Vector3(p.Right, 0f, p.Forward);
            var impact = p.Right == 0f && p.Forward == 0f ? e.Position : Ground(WithinWalls(e.Position, e.Position + offset));
            Shells.Add(new Shell { Call = call, Impact = impact, LaunchAt = now + p.Time, ImpactAt = now + p.Time + StrikeTiming.Pace(spec.Kind).Lead });
        }
    }

    internal static void Tick(float now)
    {
        for (var i = Hunts.Count - 1; i >= 0; i--)
        {
            var hunt = Hunts[i];
            try { if (HuntTick(hunt, now)) Hunts.RemoveAt(i); }
            catch (Exception error)
            {
                Hunts.RemoveAt(i);
                Plugin.Logger.ErrorOnce("Hunter laser of throw " + hunt.Call.ThrowId + " failed: " + error);
            }
        }
        for (var i = Shells.Count - 1; i >= 0; i--)
        {
            var s = Shells[i];
            try
            {
                if (!s.Launched && now >= s.LaunchAt)
                {
                    s.Launched = true;
                    Send(s.Call, DataTag.Shell, Wire.Shell(new ShellRecord(StrikePlan.Look(s.Call.Spec.Kind), s.Call.Spec.Kind, s.Impact.x, s.Impact.y, s.Impact.z,
                        Top(s.Impact).y, Math.Max(0f, s.ImpactAt - now), s.Call.Spec.Kind is StrikeKind.Gas or StrikeKind.Stasis or StrikeKind.Incendiary ? s.Call.Spec.ZoneRadius : s.Call.Spec.BlastRadius)));
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
                Plugin.Logger.ErrorOnce(s.Call.Spec.Kind + " shell of throw " + s.Call.ThrowId + " failed: " + error);
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
                Plugin.Logger.ErrorOnce(z.Call.Spec.Kind + " area of throw " + z.Call.ThrowId + " failed: " + error);
            }
        }
        for (var i = Runs.Count - 1; i >= 0; i--)
        {
            var run = Runs[i];
            try { if (RunTick(run, now)) Runs.RemoveAt(i); }
            catch (Exception error)
            {
                Runs.RemoveAt(i);
                Plugin.Logger.ErrorOnce("Strafing run of throw " + run.Call.ThrowId + " failed: " + error);
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
                Plugin.Logger.ErrorOnce("Cutting beam of throw " + b.Call.ThrowId + " failed: " + error);
            }
        }
    }

    /// <summary>The laser stays on the largest enemy near the beacon (the next largest when it dies) for the lock's time, then the shell
    /// drops on it; with no enemy it sweeps around the beacon and gives up. True when the laser is over.</summary>
    private static bool HuntTick(Hunt h, float now)
    {
        var spec = h.Call.Spec;
        var target = h.Target;
        // It reaches only the room it landed in: a target that walks out is dropped like a dead one.
        if (target != null && (target.WasCollected || !target.Alive || target.CourseNode != h.Call.Room))
        {
            target = h.Target = null;
            h.NextScan = 0f;
            h.GivesUpAt = Math.Max(h.GivesUpAt, now + 1f);
        }
        if (target == null && now >= h.NextScan)
        {
            h.NextScan = now + BeamScanSeconds;
            target = h.Target = ArsenalCombat.Largest(h.Beacon, spec.Spread, h.Call.Room);
            if (target != null) h.LockUntil = Math.Max(h.LockUntil, now + (h.LockUntil <= 0f ? StrikeTiming.HunterLockSeconds : StrikeTiming.HunterRelockSeconds));
        }
        if (target == null && now >= h.GivesUpAt)
        {
            SendLaser(h, LaserPhase.Abort, h.Beacon + Vec.Up * 0.1f, h.Beacon.y, 0f, now);
            Plugin.Session.RefundCall(h.Call.Summoner, h.Call.Definition.Key, StrikeTiming.HunterMissCooldown);
            return true;
        }
        if (target != null && now >= h.LockUntil)
        {
            var impact = Ground(target.Position);
            var drop = StrikeTiming.Pace(StrikeKind.Hunter).Drop;
            SendLaser(h, LaserPhase.Fire, ForgeCore.Enemies.AimPoint(target), impact.y, drop, now);
            Shells.Add(new Shell { Call = h.Call, Impact = impact, LaunchAt = now, ImpactAt = now + drop, Target = target });
            return true;
        }
        if (now < h.NextSend) return false;
        if (target != null)
        {
            SendLaser(h, LaserPhase.Lock, ForgeCore.Enemies.AimPoint(target), Ground(target.Position).y, Math.Max(0f, h.LockUntil - now), now);
            return false;
        }
        var angle = now * 2.4f;
        var sweep = Ground(h.Beacon + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Math.Min(4f, spec.Spread * 0.25f));
        SendLaser(h, LaserPhase.Search, sweep + Vec.Up * 0.1f, sweep.y, 0f, now);
        return false;
    }

    private static void SendLaser(Hunt h, LaserPhase phase, Vector3 point, float floor, float left, float now)
    {
        var dt = now - h.LastSend;
        var velocity = dt > 0.01f ? (point - h.LastPoint) * (1f / dt) : default;
        h.LastPoint = point;
        h.LastSend = now;
        h.NextSend = now + StrikeTiming.HunterSendSeconds;
        Send(h.Call, DataTag.Laser, Wire.Laser(new LaserRecord(phase, point.x, point.y, point.z, floor, Top(new Vector3(point.x, floor, point.z)).y, velocity.x, velocity.z, left)));
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
                ForgeCore.Combat.Explode(s.Impact + Vec.Up * BlastLift, spec.BlastRadius, spec.Damage, spec.Force);
                PlanZone(s.Call, s.Impact, now);
                break;
            case StrikeKind.Hunter:
                var target = s.Target;
                // The direct hit stays in the room too: during the shell's fall the target may have stepped through a door.
                if (target != null && !target.WasCollected && target.Alive && (target.CourseNode == s.Call.Room || ForgeCore.Level.HasSight(s.Impact, ForgeCore.Enemies.AimPoint(target))))
                    ForgeCore.Combat.ExplosionDamage(target.Damage, spec.Damage, s.Impact, Vec.Up * spec.Force);
                ForgeCore.Combat.AreaDamage(s.Impact, spec.BlastRadius, spec.TickDamage, 0.5f, spec.Force, ForgeCore.BlastLine.FromFloor, s.Call.Room);
                break;
            default:
                ForgeCore.Combat.Explode(s.Impact + Vec.Up * BlastLift, spec.BlastRadius, spec.Damage, spec.Force);
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
            if (ForgeCore.Level.HasSight(Here, ForgeCore.Enemies.AimPoint(enemy))) return true;
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
        if (found.Length > n) Plugin.Logger.ErrorOnce("arsenal-beam-candidates", "A strike beam looked at " + n + " of the " + found.Length + " enemies in reach; the rest were not considered.");
        for (var i = 0; i < n; i++)
        {
            Candidates[i] = found[i];
            DistanceBuffer[i] = Vec.SqrLength(found[i]!.Position - here);
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
        if (Flat(target.Position, here) > BeamHitRadius || !ForgeCore.Level.HasSight(here, ForgeCore.Enemies.AimPoint(target))) return;
        var point = ForgeCore.Enemies.AimPoint(target);
        var direction = (point - b.Top).normalized;
        ForgeCore.Combat.BulletDamage(target.Damage, b.Call.Spec.TickDamage, Alive(b.Call.Source), point, direction, -direction);
    }

    /// <summary>Every <c>Tick</c> seconds the head has moved on: each enemy standing in the strip between the head's last and new place
    /// takes one bullet hit through the game's own request (armour, weak spots, replication and hit reactions stay the game's), so every
    /// enemy is hit once as the head passes. True when the run is over.</summary>
    private static bool RunTick(Run run, float now)
    {
        var spec = run.Call.Spec;
        if (now < run.NextTick) return false;
        run.NextTick = now + spec.Tick;
        var from = run.Along;
        var to = Math.Min(run.Length, Math.Max(0f, now - run.StartsAt) * spec.Speed);
        run.Along = to;
        var mid = run.Origin + run.Direction * ((from + to) * 0.5f);
        var half = spec.Width * 0.5f;
        var radius = MathF.Sqrt((to - from) * (to - from) * 0.25f + half * half + RunReachHeight * RunReachHeight) + 0.5f;
        var source = Alive(run.Call.Source);
        foreach (var enemy in ForgeCore.Enemies.Near(mid, radius))
        {
            if (enemy == null || !enemy.Alive) continue;
            var offset = enemy.Position - run.Origin;
            var along = Vec.Dot(offset, run.Direction);
            if (along < from || along >= to && to < run.Length) continue;
            if (along > run.Length) continue;
            var lateral = run.Direction.z * offset.x - run.Direction.x * offset.z;
            if (MathF.Abs(lateral) > half || MathF.Abs(offset.y) > RunReachHeight) continue;
            var point = ForgeCore.Enemies.AimPoint(enemy);
            // The strip is drawn on the floor of its own room: one standing beside it behind a wall is not in it.
            if (!ForgeCore.Level.HasSight(run.Origin + run.Direction * along, point)) continue;
            if (!run.Hit.Add(enemy.GetInstanceID())) continue;
            ForgeCore.Combat.BulletDamage(enemy.Damage, spec.Damage, source, point, Vec.Down, Vec.Up);
        }
        return to >= run.Length;
    }

    private static PlayerAgent? Alive(PlayerAgent? player) => player == null || player.WasCollected ? null : player;

    private static void Send(Call call, DataTag tag, byte[] data) => OrdnanceApi.SendData(StrikePayload.Type, call.Summoner, call.ThrowId, (byte)tag, data);

    /// <summary>A zone or beam's whole current state, kept by the carrier and replayed to players who join later.</summary>
    private static void State(Call call, DataTag tag, byte[] data)
    {
        if (OrdnanceApi.SendState(StrikePayload.Type, call.Summoner, call.ThrowId, (byte)tag, data)) return;
        // dispatches even a state it could not retain. The owning tick's failure path retracts it.
        throw new InvalidOperationException("The host could not retain " + tag + " state for late joiners; effect cancelled.");
    }

    /// <summary>Ends a zone or beam on every peer and forgets its kept state.</summary>
    private static void End(Call call, DataTag tag)
    {
        Send(call, tag, tag == DataTag.BeamEnd ? call.Generation.ToByteArray() : Array.Empty<byte>());
        OrdnanceApi.ClearState(StrikePayload.Type, call.Summoner, call.ThrowId);
    }

    /// <summary>The floor under a point. The downward ray starts at most 3 m up and always below whatever is overhead, so a
    /// low ceiling (indoors, corridors) is never taken for the floor; without a floor within 9 m below, the point itself.</summary>
    internal static Vector3 Ground(Vector3 point)
    {
        var lift = ForgeCore.Level.LiftUnderRoof(point, 3f, 0.1f);

        return ForgeCore.Level.FloorBelow(point + Vec.Up * lift, lift + 9f, out var hit) ? hit.point : point;
    }

    /// <summary>Where a shell starts: 3 m above the ceiling over the impact (levels are underground), else 30 m up.</summary>
    internal static Vector3 Top(Vector3 impact)
        => Physics.Raycast(impact + Vec.Up * 0.5f, Vec.Up, out var hit, 40f, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore)
            ? new Vector3(impact.x, hit.point.y + 3f, impact.z) : impact + Vec.Up * 30f;

    internal static void Clear()
    {
        _generation = Guid.NewGuid();
        Shells.Clear();
        Zones.Clear();
        Beams.Clear();
        Runs.Clear();
        Hunts.Clear();
        Facing.Clear();
    }
}
