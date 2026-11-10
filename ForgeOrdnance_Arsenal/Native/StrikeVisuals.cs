using System;
using System.Collections.Generic;
using ForgeOrdnance;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceArsenal;

/// <summary>Every peer, the host included: what the host's strike data shows. From the moment the beacon lands the ground the call can hurt is drawn at its true size
/// (<see cref="DangerRecord"/>), and a player standing on it is told. A shell's landing is marked and heard for its warning
/// (<see cref="StrikeTiming.Pace"/>), then it falls, gaining speed, from above the ceiling through the level (no collision) onto the host's impact point, on the call's slant, nose first,
/// then flash, sound and shake; the hunter's laser follows the host's reports (<see cref="TargetLaser"/>); gas canisters and stasis emitters stay for
/// their area's lifetime; areas show a coloured light, a ring and their cloud (<see cref="StrikeEffects"/>), and stasis areas
/// slow the enemies inside on this peer (<see cref="StasisSlow"/>); the beam runs from above the ceiling down to the ground
/// or its target. Damage is never decided here.</summary>
internal static class StrikeVisuals
{
    /// <summary>Strike areas with a point light at once; the newest areas keep theirs.</summary>
    private const int MaxAreaLights = 2;
    /// <summary>The cutting beam's light where it burns (ForgeVisual's shared effect lights).</summary>
    private static readonly ForgeVisual.PointLightSpec BeamLight = new(StrikeEffects.BeamColour, 6f, 3f, 0f, 0f);
    /// <summary>Metres above the floor where a cluster bomblet splits into its sparklets.</summary>
    private const float BurstHeight = 3.2f;
    /// <summary>Metres the beam's shown point moves before it leaves the next burn mark.</summary>
    private const float BeamMarkSpacing = 0.8f;
    // Beam state is refreshed at least every five seconds by every valid definition. Allow one missed refresh before
    // treating the owner as gone, so a lost BeamEnd cannot leave its line, light and loop alive indefinitely.
    private const float BeamSilenceSeconds = 12f;
    private static Guid _beamGeneration;
    private static bool _beamReplayAllowed = true;
    private static readonly HashSet<(ulong Summoner, uint ThrowId)> BeamCalls = new();
    /// <summary>Where each cluster call's beacon is (its strike's centre) and which calls already drew their one scorch and light.</summary>
    private static readonly Dictionary<(ulong Summoner, uint ThrowId), Vector3> ClusterCentres = new();
    private static readonly HashSet<(ulong Summoner, uint ThrowId)> ClusterMarked = new();
    private const int ClusterBookCap = 64;

    internal static void BeginLevel() => _beamReplayAllowed = true;

    internal static void AdmitBeam(OrdnancePresentation p)
    {
        if (p.Parameters is StrikeSpec { Kind: StrikeKind.Beam }) BeamCalls.Add((p.SummonerLookup, p.ThrowId));
        else if (p.Parameters is StrikeSpec { Kind: StrikeKind.Cluster }) ClusterCentres[(p.SummonerLookup, p.ThrowId)] = p.Position;
    }

    /// <summary>A checkpoint reload voids every beam; a replayed beam update from before it is never admitted again this level.</summary>
    internal static void CloseReplay() => _beamReplayAllowed = false;

    private static void ForgetBeamCalls()
    {
        _beamGeneration = Guid.Empty;
        BeamCalls.Clear();
    }

    private sealed class Falling
    {
        internal GameObject? Root;
        internal ulong Summoner;
        internal uint ThrowId;
        internal ShellRecord Record;
        internal Vector3 From, To;
        /// <summary>Until <see cref="DropAt"/> only the landing mark and the whistle exist; the shell itself falls from then to <see cref="Lands"/>.</summary>
        internal float DropAt, Lands;
        internal bool Landed, Dropping;
        internal float RemoveAt;
        /// <summary>The smoke trail's slot (-1: none) and, for a cluster bomblet, above 0 until it splits in the air (at <see cref="BurstHeight"/> above the floor).</summary>
        internal int Trail = -1;
        internal float BurstAt;
    }

    private sealed class Area
    {
        internal ulong Summoner;
        internal uint ThrowId;
        internal GameObject? Root;
        /// <summary>The area's held light (ForgeVisual's shared effect lights; 0 while it has none) and its colour and reach.</summary>
        internal int Light;
        internal ForgeVisual.PointLightSpec LightSpec;
        internal Color Colour;
        internal float Start, Ends;
        internal GameObject? Prop;
        internal StrikeEffects.Cloud? Cloud;
        internal ForgeVisual.FxField? Field;
        internal StrikeKind Kind;
        internal Vector3 Centre;
        internal float Radius, NextBeat;
    }

    /// <summary>The hunter's laser as this peer shows it, and the object its sounds ride on.</summary>
    private sealed class LaserLook
    {
        internal ulong Summoner;
        internal uint ThrowId;
        internal TargetLaser? Fx;
        internal GameObject? Sound;
        internal LaserPhase Phase;
        internal float LastUpdate, FadeEnds, Lock, LockEnds;
        internal bool LockHeard;
    }

    private sealed class BeamLook
    {
        /// <summary>This beam's own hold key for its line.</summary>
        internal readonly int Key = ForgeVisual.Fx.NewKey();
        internal ulong Summoner;
        internal uint ThrowId;
        internal GameObject? Root;
        /// <summary>The beam's held light (0 while it has none), and the object its loop sound rides on, kept where the beam burns.</summary>
        internal int Light;
        internal GameObject? Loop;
        /// <summary>The top moves over the floor point with the host's reports, so the beam stays upright as it walks instead of leaning through the ceiling.</summary>
        internal Vector3 From, FromTarget, To, ShownTo, LastMark;
        internal bool HasTarget, Marked;
        internal float LastUpdate;
        /// <summary>The beam is drawn from this time on; before it the sky glow charges (none on a late joiner).</summary>
        internal float ShowAt;
        internal GameObject? CuttingAudio;
    }

    /// <summary>Sounds due a moment after their trigger (the debris after a blast, the ignition after a landing).</summary>
    private static readonly List<(float At, Vector3 Where, bool Ignition)> Echoes = new();
    private static readonly List<Falling> Shells = new();
    private static readonly List<Area> Areas = new();
    private static readonly List<BeamLook> Beams = new();
    private static readonly List<RunLook> Runs = new();
    private static readonly List<LaserLook> Lasers = new();
    private static readonly List<StrikeEffects.Danger> Dangers = new();
    /// <summary>A laser whose host stopped reporting is taken away after this long.</summary>
    private const float LaserSilenceSeconds = 0.8f;
    /// <summary>How long a laser that found nothing takes to flicker out.</summary>
    private const float LaserGiveUpSeconds = 0.5f;

    /// <summary>A strafing run as this peer shows it: a tracer streak with its ground point racing along the strip, and a scorch mark
    /// left on the ground every <see cref="RunMarkSpacing"/> metres.</summary>
    private sealed class RunLook
    {
        internal Vector3 Origin, Direction;
        internal float Length, Speed, HalfWidth, StartsAt, LastMark, LastBeat;
        internal StrikeEffects.Beam? Fx;
        /// <summary>The object the run's gun sound rides on, moving with the ground point.</summary>
        internal GameObject? Sound;
    }

    private const float RunMarkSpacing = 1.5f;
    private const float RunTailMetres = 6f;
    /// <summary>Metres between the run's impact sounds.</summary>
    private const float RunBeatSpacing = 4.5f;
    private static readonly Color RunColour = new(1f, 0.78f, 0.25f, 1f);

    internal static void Receive(OrdnancePayloadData d)
    {
        try
        {
            switch ((DataTag)d.Tag)
            {
                case DataTag.Danger when Wire.TryDanger(d.Data, out var danger): ShowDanger(danger); break;
                case DataTag.Shell when Wire.TryShell(d.Data, out var shell): ShowShell(d, shell); break;
                case DataTag.Zone when Wire.TryZone(d.Data, out var zone): StartArea(d, zone); break;
                case DataTag.Beam when Wire.TryBeam(d.Data, out var beam): AimBeam(d, beam); break;
                case DataTag.Strafe when Wire.TryStrafe(d.Data, out var run): StartRun(run); break;
                case DataTag.Laser when Wire.TryLaser(d.Data, out var laser): AimLaser(d, laser); break;
                case DataTag.BeamEnd: EndBeam(d); break;
                case DataTag.ZoneEnd: EndArea(d); break;
                default: break;
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Showing strike data (tag " + d.Tag + ") failed: " + error); }
    }

    /// <summary>The ground a call can hurt, from its beacon's landing until its last shell. Stasis and the gun run do not hurt players: they are drawn, but nobody is told to run.</summary>
    private static void ShowDanger(in DangerRecord r)
    {
        var look = StrikeEffects.PlaceDanger(r, Time.time);
        if (look != null) Dangers.Add(look);
        if (r.Kind is StrikeKind.Stasis or StrikeKind.Strafe) return;
        var agent = ForgeCore.Players.Local;
        if (agent == null || agent.WasCollected) return;
        var at = agent.Position;
        if (r.Covers(at.x, at.y, at.z)) OrdnanceApi.PostNotice(ArsenalText.T("strike.danger"));
    }

    private static void StartRun(in StrafeRecord r)
    {
        var now = Time.time;
        Runs.Add(new RunLook
        {
            Origin = new Vector3(r.X, r.Y, r.Z),
            Direction = new Vector3(r.DirX, 0f, r.DirZ),
            Length = r.Length,
            Speed = r.Speed,
            HalfWidth = r.HalfWidth,
            StartsAt = now + r.Lead,
            LastMark = -RunMarkSpacing,
            LastBeat = -RunBeatSpacing,
        });
        ArsenalAudio.PlayAt(ArsenalAudio.Cue.Incoming, new Vector3(r.X, r.Y, r.Z), StrikeTiming.WhistleStart(r.Lead));
    }

    /// <summary>A report of the hunter's laser. Every peer follows the same point, so every peer sees the same target.</summary>
    private static void AimLaser(OrdnancePayloadData d, in LaserRecord r)
    {
        var now = Time.time;
        var look = Lasers.Find(l => l.Summoner == d.SummonerLookup && l.ThrowId == d.ThrowId);
        if (look == null)
        {
            look = new LaserLook { Summoner = d.SummonerLookup, ThrowId = d.ThrowId, Fx = StrikeEffects.CreateLaser() };
            look.Sound = new GameObject("ForgeOrdnanceArsenal.LaserSound");
            look.Sound.transform.position = new Vector3(r.X, r.Y, r.Z);
            ArsenalAudio.PlayLoop(ArsenalAudio.Cue.BeamLoop, look.Sound);
            Lasers.Add(look);
        }
        look.Phase = r.Phase;
        look.LastUpdate = now;
        look.Fx?.Aim(r.Top, new Vector3(r.X, r.Y, r.Z), r.Floor, new Vector3(r.VelocityX, 0f, r.VelocityZ));
        switch (r.Phase)
        {
            case LaserPhase.Lock:
                look.LockEnds = now + r.Left;
                // The swell peaks as the lock runs out and the shell is released.
                if (!look.LockHeard && look.Sound != null)
                {
                    look.LockHeard = true;
                    ArsenalAudio.PlayOn(ArsenalAudio.Cue.HunterLock, look.Sound);
                }
                break;
            case LaserPhase.Fire:
                look.Lock = 1f;
                look.Fx?.Flare();
                look.FadeEnds = now + r.Left;
                break;
            case LaserPhase.Abort:
                look.FadeEnds = now + LaserGiveUpSeconds;
                ArsenalAudio.PlayAt(ArsenalAudio.Cue.BeamEnd, new Vector3(r.X, r.Y, r.Z));
                StrikeBursts.NoTarget(new Vector3(r.X, r.Floor, r.Z), now);
                if (d.SummonerLookup == SNetwork.SNet.LocalPlayer?.Lookup)
                    OrdnanceApi.PostNotice(ArsenalText.T("strike.noTargetRefund", (int)StrikeTiming.HunterMissCooldown));
                break;
        }
    }

    private static void TickLasers(float now)
    {
        for (var i = Lasers.Count - 1; i >= 0; i--)
        {
            var l = Lasers[i];
            var over = l.Phase is LaserPhase.Fire or LaserPhase.Abort ? now >= l.FadeEnds : now - l.LastUpdate >= LaserSilenceSeconds;
            if (over)
            {
                RemoveLaser(l);
                Lasers.RemoveAt(i);
                continue;
            }
            if (l.Phase == LaserPhase.Lock) l.Lock = Math.Clamp(1f - (l.LockEnds - now) / StrikeTiming.HunterLockSeconds, 0f, 1f);
            if (l.Fx != null)
            {
                l.Fx.Lock = l.Lock;
                // Giving up: it flickers out.
                l.Fx.Opacity = l.Phase == LaserPhase.Abort ? Math.Clamp((l.FadeEnds - now) / LaserGiveUpSeconds, 0f, 1f) * (Mathf.Sin(now * 70f) > 0f ? 1f : 0.25f) : 1f;
                l.Fx.Draw(now);
                if (l.Sound != null && !l.Sound.WasCollected) l.Sound.transform.position = l.Fx.Shown;
            }
        }
    }

    private static void RemoveLaser(LaserLook l)
    {
        l.Fx?.Release();
        Destroy(l.Sound);
    }

    private static void TickRuns(float now)
    {
        for (var i = Runs.Count - 1; i >= 0; i--)
        {
            var run = Runs[i];
            if (now < run.StartsAt) continue;
            var travelled = (now - run.StartsAt) * run.Speed;
            var along = Math.Min(run.Length, travelled);
            var head = Strikes.Ground(run.Origin + run.Direction * along);
            if (run.Sound == null)
            {
                run.Sound = new GameObject("ForgeOrdnanceArsenal.StrafeSound");
                run.Sound.transform.position = head;
                ArsenalAudio.PlayOn(ArsenalAudio.Cue.StrafeLine, run.Sound);
            }
            else if (!run.Sound.WasCollected) run.Sound.transform.position = head;
            if (along - run.LastBeat >= RunBeatSpacing && along < run.Length)
            {
                run.LastBeat = along;
                ArsenalAudio.PlayAt(ArsenalAudio.Cue.StrafeImpact, head);
            }
            if (travelled >= run.Length + RunTailMetres)
            {
                Destroy(run.Sound);
                run.Fx?.Release();
                Runs.RemoveAt(i);
                continue;
            }
            run.Fx ??= StrikeEffects.CreateBeam(RunColour, now);
            var tail = Strikes.Ground(run.Origin + run.Direction * Math.Clamp(travelled - RunTailMetres, 0f, run.Length));
            run.Fx?.Show(tail + Vector3.up * 0.25f, head + Vector3.up * 0.25f, now);
            if (along - run.LastMark >= RunMarkSpacing && along < run.Length)
            {
                run.LastMark = along;
                // The rounds walk over the whole strip, not one line down its middle; the same on every peer, and never through a wall beside it.
                var side = new Vector3(run.Direction.z, 0f, -run.Direction.x) * (run.HalfWidth * 0.8f * Mathf.Sin(along * 7.3f));
                var hit = ForgeCore.Level.Blocked(head, head + side) ? head : head + side;
                StrikeBursts.BeamMark(hit, RunColour, now);
                StrikeBursts.RunHit(hit, run.Direction, RunColour, now);
            }
        }
    }

    private static void ShowShell(OrdnancePayloadData d, in ShellRecord r)
    {
        var to = new Vector3(r.X, r.Y, r.Z);
        var from = new Vector3(r.X, Math.Max(r.Top, r.Y + 2f), r.Z);
        if (StrikeTiming.Slants(r.Kind))
        {
            var (sideX, sideZ) = StrikePlan.ApproachSide(d.SummonerLookup, d.ThrowId);
            var run = (from.y - to.y) * StrikeTiming.ApproachSlant;
            from.x += sideX * run;
            from.z += sideZ * run;
        }
        var now = Time.time;
        var drop = Math.Min(StrikeTiming.Pace(r.Kind).Drop, r.Fall);
        var falling = new Falling { Summoner = d.SummonerLookup, ThrowId = d.ThrowId, Record = r, From = from, To = to, DropAt = now + r.Fall - drop, Lands = now + r.Fall };
        // A cluster bomblet splits a little above the floor; it needs room to fall first.
        var height = from.y - to.y;
        if (r.Kind == StrikeKind.Cluster && r.Fall > 0f && height > BurstHeight + 1.5f)
            falling.BurstAt = 1f;
        Shells.Add(falling);
        if (r.Fall > drop) StrikeBursts.Warn(r.Kind, to, from, r.Blast, r.Fall - drop, now);
        ArsenalAudio.PlayAt(ArsenalAudio.Cue.Incoming, to, StrikeTiming.WhistleStart(r.Fall));
    }

    /// <summary>The shell starts falling: its model and trail appear above the ceiling. (A shell whose area started first is settled by <see cref="Land"/> and starts here too.)</summary>
    private static void BeginDrop(Falling s, float now)
    {
        s.Dropping = true;
        var r = s.Record;
        var model = ArsenalArt.Look(r.Look);
        if (model != null)
        {
            var upright = r.Look is ShellLook.Canister or ShellLook.Emitter;
            s.Root = model.Spawn(s.From, upright ? Quaternion.identity : ArsenalArt.NoseAlong(s.To - s.From));
            if (s.Root != null) s.Root.name = "ForgeOrdnanceArsenal." + r.Look;
        }
        s.Trail = StrikeBursts.BeginTrail(r.Look, r.Kind, s.From, now);
    }

    private static void Land(Falling s)
    {
        s.Landed = true;
        var r = s.Record;
        if (!s.Dropping) BeginDrop(s, Time.time);
        if (s.Root != null) s.Root.transform.position = s.To;
        StrikeBursts.EndTrail(s.Trail);
        s.Trail = -1;
        var now = Time.time;
        switch (r.Kind)
        {
            case StrikeKind.Gas:
                // r.Blast carries the cloud's radius, as for the stasis field below.
                StrikeBursts.GasBurst(s.To, r.Blast, now);
                ArsenalAudio.PlayAt(ArsenalAudio.Cue.GasRelease, s.To);
                s.RemoveAt = Time.time + StrikeTiming.ZoneStartDelay(r.Kind) + 2f; // bounded waiting for the area message
                return;
            case StrikeKind.Stasis:
                ArsenalAudio.PlayAt(ArsenalAudio.Cue.StasisPulse, s.To);
                // The deployment shockwave goes out with the landing; the field itself starts after its delay (r.Blast carries the field's radius).
                StrikeBursts.StasisPulse(s.To, r.Blast, now);
                s.RemoveAt = Time.time + StrikeTiming.ZoneStartDelay(r.Kind) + 2f;
                return;
        }
        var big = r.Kind is StrikeKind.Precision or StrikeKind.Hunter;
        // The big shells (precision, hunter) and the small ones draw their own blast; the game's trip mine explosion is not borrowed.
        if (r.Kind == StrikeKind.Cluster) MarkClusterCentre(s, now);
        StrikeBursts.Blast(r.Kind, s.To, r.Blast, now);
        if (big) StrikeBursts.ImpactColumn(s.To, s.From, r.Blast, now);
        ArsenalAudio.PlayAt(r.Kind == StrikeKind.Cluster ? ArsenalAudio.Cue.ClusterSplit
            : big ? ArsenalAudio.Cue.BigBoom : ArsenalAudio.Cue.SmallBoom, s.To);
        Echoes.Add((now + StrikeTiming.DebrisDelay, s.To, false));
        if (r.Kind == StrikeKind.Incendiary) Echoes.Add((now + StrikeTiming.IgniteDelay, s.To, true));
        s.RemoveAt = Time.time;
    }

    /// <summary>A cluster strike's one scorch and one short light: its first landing draws them at the beacon (this bomblet's own
    /// point when the beacon was not seen, as on a late joiner).</summary>
    private static void MarkClusterCentre(Falling s, float now)
    {
        if (ClusterMarked.Count >= ClusterBookCap) { ClusterMarked.Clear(); ClusterCentres.Clear(); }
        var key = (s.Summoner, s.ThrowId);
        if (!ClusterMarked.Add(key)) return;
        if (!ClusterCentres.Remove(key, out var centre)) centre = s.To;
        StrikeBursts.ClusterCentre(centre, s.Record.Blast, now);
    }

    private static void StartArea(OrdnancePayloadData d, in ZoneRecord z)
    {
        if (Areas.Exists(a => a.Summoner == d.SummonerLookup && a.ThrowId == d.ThrowId)) return;
        var centre = new Vector3(z.X, z.Y, z.Z);
        var colour = z.Kind switch
        {
            StrikeKind.Gas => new Color(0.45f, 0.95f, 0.25f),
            StrikeKind.Stasis => new Color(0.3f, 0.65f, 1f),
            _ => new Color(1f, 0.45f, 0.08f),
        };
        var root = new GameObject("ForgeOrdnanceArsenal." + z.Kind + "Area");
        root.transform.position = centre + Vector3.up * 0.6f;
        var lightSpec = new ForgeVisual.PointLightSpec(colour, z.Radius * 1.6f, 1f, 0f, 0f);
        var now = Time.time;
        // The ground mark at the area's true radius; the library's field stands over it.
        var cloud = StrikeEffects.CreateCloud(z.Kind, centre, z.Radius, colour, now, now + z.Seconds);
        if (z.Kind == StrikeKind.Stasis)
        {
            StasisSlow.Add(d.SummonerLookup, d.ThrowId, centre, z.Radius, z.Slow, z.Seconds);
        }
        // An area never starts before its shell has landed: the host sends the zone before this frame's visual tick, so settle the shell now, regardless of tick order.
        // The canister or emitter that landed here stays as the area's prop.
        GameObject? prop = null;
        foreach (var shell in Shells)
        {
            if (shell.Summoner != d.SummonerLookup || shell.ThrowId != d.ThrowId
                || shell.Record.Kind is not (StrikeKind.Gas or StrikeKind.Stasis or StrikeKind.Incendiary)) continue;
            if (!shell.Landed) Land(shell);
            if (shell.Record.Kind == StrikeKind.Incendiary) break;
            prop = shell.Root;
            shell.Root = null;
            shell.RemoveAt = 0f;
            break;
        }
        if (prop == null && d.Replayed && z.Kind is StrikeKind.Gas or StrikeKind.Stasis)
        {
            // A player who joined after the canister or emitter landed: stand it where it lies.
            var model = ArsenalArt.Look(StrikePlan.Look(z.Kind));
            if (model != null) { prop = model.Spawn(centre, Quaternion.identity); if (prop != null) prop.name = "ForgeOrdnanceArsenal." + StrikePlan.Look(z.Kind); }
        }
        // A replayed area shows for its full time at most; the host's ZoneEnd removes it when it really ends.
        Areas.Add(new Area
        {
            Summoner = d.SummonerLookup,
            ThrowId = d.ThrowId,
            Root = root,
            LightSpec = lightSpec,
            Colour = colour,
            Start = now,
            Ends = now + z.Seconds,
            Prop = prop,
            Cloud = cloud,
            Field = ArsenalFxSlots.FieldOf(z.Kind, centre + Vector3.up * 0.1f, z.Radius),
            Kind = z.Kind,
            Centre = centre,
            Radius = z.Radius,
            NextBeat = now + StrikeTiming.StasisBeatSeconds
        });
        ArsenalAudio.PlayLoop(z.Kind switch
        {
            StrikeKind.Gas => ArsenalAudio.Cue.GasLoop,
            StrikeKind.Stasis => ArsenalAudio.Cue.StasisLoop,
            _ => ArsenalAudio.Cue.FireLoop
        }, root);
    }

    private static void EndArea(OrdnancePayloadData d)
    {
        var index = Areas.FindIndex(a => a.Summoner == d.SummonerLookup && a.ThrowId == d.ThrowId);
        if (index < 0) return;
        RemoveArea(index);
    }

    private static void RemoveArea(int index)
    {
        var area = Areas[index];
        StasisSlow.Remove(area.Summoner, area.ThrowId);
        area.Cloud?.Release();
        ForgeVisual.PointLights.Release(ref area.Light);
        Destroy(area.Root);
        Destroy(area.Prop);
        // Also own a shell whose visual landing is still pending when the zone ends.
        for (var i = Shells.Count - 1; i >= 0; i--)
        {
            var shell = Shells[i];
            if (shell.Summoner != area.Summoner || shell.ThrowId != area.ThrowId) continue;
            StrikeBursts.EndTrail(shell.Trail);
            Destroy(shell.Root);
            Shells.RemoveAt(i);
        }
        Areas.RemoveAt(index);
    }

    private static void AimBeam(OrdnancePayloadData d, in BeamRecord r)
    {
        // Normal updates require this epoch's beacon admission. A new joiner may bootstrap from retained state;
        // after recall existing peers instead require a new beacon, even if no old beam update was ever seen.
        // ForgeOrdnance protocol 9 rejects transport epochs from before a reset. Local admission protects
        // callbacks already handed to us; retaining every past generation is unnecessary.
        if ((_beamGeneration != Guid.Empty && _beamGeneration != r.Generation)
            || (!BeamCalls.Contains((d.SummonerLookup, d.ThrowId)) && !(d.Replayed && _beamReplayAllowed && !ForgeCore.Checkpoint.Recalling))) return;
        // A retained update adopts this call on a late joiner. Its following live updates are ordinary messages, so the
        // replay must establish the same admission that seeing the original beacon would have established.
        if (d.Replayed) BeamCalls.Add((d.SummonerLookup, d.ThrowId));
        _beamGeneration = r.Generation;
        var beam = Beams.Find(b => b.Summoner == d.SummonerLookup && b.ThrowId == d.ThrowId);
        if (beam == null)
        {
            var root = new GameObject("ForgeOrdnanceArsenal.Beam");
            root.transform.position = new Vector3(r.OX, r.OY, r.OZ);
            var loop = new GameObject("ForgeOrdnanceArsenal.BeamLoop");
            loop.transform.SetParent(root.transform, false);
            var now = Time.time;
            // The sky gathers light for BeamChargeSeconds before the beam drops; a late joiner sees the beam already running.
            beam = new BeamLook { Summoner = d.SummonerLookup, ThrowId = d.ThrowId, Root = root, Loop = loop, From = root.transform.position, FromTarget = root.transform.position, ShowAt = d.Replayed ? now : now + StrikeTiming.BeamChargeSeconds };
            beam.ShownTo = beam.From;
            Beams.Add(beam);
            if (!d.Replayed)
            {
                // The shell start is 3 m above the ceiling: the glow gathers just under it, where the player can see it.
                StrikeBursts.BeamCharge(new Vector3(r.OX, Math.Max(r.OY - 3.15f, r.TY + 2f), r.OZ), now);
                ArsenalAudio.PlayAt(ArsenalAudio.Cue.BeamStart, new Vector3(r.TX, r.TY, r.TZ), StrikeTiming.ChargeStart(StrikeTiming.BeamChargeSeconds));
            }
            ArsenalAudio.PlayLoop(ArsenalAudio.Cue.BeamLoop, loop);
        }
        // With a target the point is on it; without one it is the floor where the beam sweeps, so the beam always reaches down.
        beam.HasTarget = r.HasTarget;
        beam.LastUpdate = Time.time;
        beam.To = new Vector3(r.TX, r.TY, r.TZ);
        beam.FromTarget = new Vector3(r.OX, r.OY, r.OZ);
        SetCuttingAudio(beam);
    }

    private static void EndBeam(OrdnancePayloadData d)
    {
        if (d.Data.Length != 16 || new Guid(d.Data) != _beamGeneration) return;
        BeamCalls.Remove((d.SummonerLookup, d.ThrowId));
        var index = Beams.FindIndex(b => b.Summoner == d.SummonerLookup && b.ThrowId == d.ThrowId);
        if (index < 0) return;
        var beam = Beams[index];
        ArsenalAudio.PlayAt(ArsenalAudio.Cue.BeamEnd, beam.ShownTo);
        ForgeVisual.PointLights.Release(ref beam.Light);
        Destroy(beam.CuttingAudio);
        Destroy(beam.Root);
        Beams.RemoveAt(index);
    }

    internal static void Tick(float now)
    {
        for (var i = Echoes.Count - 1; i >= 0; i--)
        {
            if (now < Echoes[i].At) continue;
            ArsenalAudio.PlayAt(Echoes[i].Ignition ? ArsenalAudio.Cue.FireIgnite : ArsenalAudio.Cue.Debris, Echoes[i].Where);
            Echoes.RemoveAt(i);
        }
        for (var i = Shells.Count - 1; i >= 0; i--)
        {
            var s = Shells[i];
            if (!s.Landed)
            {
                if (now < s.DropAt) continue;
                if (!s.Dropping) BeginDrop(s, now);
                var t = s.Lands <= s.DropAt ? 1f : Math.Clamp((now - s.DropAt) / (s.Lands - s.DropAt), 0f, 1f);
                var at = Vector3.Lerp(s.From, s.To, StrikeTiming.FallCurve(t));
                if (s.Root != null && !s.Root.WasCollected) s.Root.transform.position = at;
                if (s.Trail >= 0) StrikeBursts.MoveTrail(s.Trail, at, s.To - s.From, t, now);
                if (s.BurstAt > 0f && at.y - s.To.y <= BurstHeight && t < 1f)
                {
                    // The cluster bomblet splits: the carrier is gone.
                    s.BurstAt = 0f;
                    StrikeBursts.EndTrail(s.Trail);
                    s.Trail = -1;
                    Destroy(s.Root);
                    s.Root = null;
                    StrikeBursts.ClusterBurst(at, now);
                }
                if (t >= 1f)
                {
                    Land(s);
                    if (s.Record.Kind is StrikeKind.Gas or StrikeKind.Stasis)
                    {
                        var area = FindArea(s.Summoner, s.ThrowId);
                        if (area != null)
                        {
                            Destroy(area.Prop);
                            area.Prop = s.Root;
                            s.Root = null;
                            s.RemoveAt = 0f;
                        }
                    }
                }
                continue;
            }
            if (now < s.RemoveAt) continue;
            Destroy(s.Root);
            Shells.RemoveAt(i);
        }
        // Newest first: only the newest MaxAreaLights areas keep their point light.
        var lit = 0;
        for (var i = Areas.Count - 1; i >= 0; i--)
        {
            var a = Areas[i];
            if (now >= a.Ends)
            {
                RemoveArea(i);
                continue;
            }
            if (lit < MaxAreaLights && a.Root != null && !a.Root.WasCollected)
            {
                lit++;
                var fade = Math.Clamp((a.Ends - now) / 1.5f, 0f, 1f);
                var intensity = (2.2f + 0.6f * Mathf.Sin((now - a.Start) * 7f) + 0.3f * Mathf.Sin((now - a.Start) * 17f)) * fade;
                ForgeVisual.PointLights.Hold(ref a.Light, a.Root.transform.position, in a.LightSpec, now, intensity);
            }
            else ForgeVisual.PointLights.Release(ref a.Light);
            a.Cloud?.Tick(now);
            a.Field?.Hold();
            if (a.Kind == StrikeKind.Stasis && now >= a.NextBeat)
            {
                a.NextBeat = now + StrikeTiming.StasisBeatSeconds;
                StrikeBursts.StasisBeat(a.Centre, a.Radius, now);
            }
        }
        for (var i = Dangers.Count - 1; i >= 0; i--)
            if (!Dangers[i].Tick(now)) Dangers.RemoveAt(i);
        StrikeBursts.Tick(now);
        StasisSlow.Tick(now);
        TickRuns(now);
        TickLasers(now);
        for (var i = Beams.Count - 1; i >= 0; i--)
        {
            var b = Beams[i];
            if (now - b.LastUpdate >= BeamSilenceSeconds)
            {
                BeamCalls.Remove((b.Summoner, b.ThrowId));
                ForgeVisual.PointLights.Release(ref b.Light);
                Destroy(b.CuttingAudio);
                Destroy(b.Root);
                Beams.RemoveAt(i);
                Plugin.Logger.ErrorOnce("Cutting beam of throw " + b.ThrowId + " stopped after " + ForgeCore.Log.Num(BeamSilenceSeconds) + " s without a host update.");
                continue;
            }
            if (b.Root == null || b.Root.WasCollected) continue;
            b.ShownTo = Vector3.Lerp(b.ShownTo, b.To, Math.Min(1f, Time.deltaTime * (b.HasTarget ? 10f : 4f)));
            b.From = Vector3.Lerp(b.From, b.FromTarget, Math.Min(1f, Time.deltaTime * (b.HasTarget ? 10f : 4f)));
            if (b.CuttingAudio != null && !b.CuttingAudio.WasCollected) b.CuttingAudio.transform.position = b.ShownTo;
            var drawn = now >= b.ShowAt;
            if (drawn) ArsenalFxSlots.LineBeam(b.Key, b.From, b.ShownTo);
            if (drawn && Vector3.Distance(b.ShownTo, b.To) < 1.5f && (!b.Marked || Vector3.Distance(b.ShownTo, b.LastMark) >= BeamMarkSpacing))
            {
                // The route the beam walks stays scorched for a few seconds, glowing first and then dark.
                b.Marked = true;
                b.LastMark = b.ShownTo;
                StrikeBursts.BeamMark(b.ShownTo, StrikeEffects.BeamColour, now);
            }
            var burn = b.ShownTo + Vector3.up * 0.4f;
            if (b.Loop != null && !b.Loop.WasCollected) b.Loop.transform.position = burn;
            if (drawn) ForgeVisual.PointLights.Hold(ref b.Light, burn, in BeamLight, now);
            else ForgeVisual.PointLights.Release(ref b.Light);
        }
    }

    private static void SetCuttingAudio(BeamLook beam)
    {
        if (!beam.HasTarget)
        {
            Destroy(beam.CuttingAudio);
            beam.CuttingAudio = null;
            return;
        }
        if (beam.CuttingAudio != null && !beam.CuttingAudio.WasCollected) return;
        var holder = new GameObject("ForgeOrdnanceArsenal.BeamCuttingAudio");
        holder.transform.SetParent(beam.Root!.transform, true);
        holder.transform.position = beam.To;
        beam.CuttingAudio = holder;
        ArsenalAudio.PlayLoop(ArsenalAudio.Cue.BeamCutting, holder);
    }

    private static Area? FindArea(ulong summoner, uint throwId)
    {
        for (var i = 0; i < Areas.Count; i++)
            if (Areas[i].Summoner == summoner && Areas[i].ThrowId == throwId) return Areas[i];
        return null;
    }

    private static void Destroy(GameObject? root)
    {
        if (root != null && !root.WasCollected) Object.Destroy(root);
    }

    internal static void Clear()
    {
        ForgetBeamCalls();
        ClusterCentres.Clear();
        ClusterMarked.Clear();
        Echoes.Clear();
        foreach (var danger in Dangers) danger.Release();
        Dangers.Clear();
        foreach (var run in Runs) { run.Fx?.Release(); Destroy(run.Sound); }
        Runs.Clear();
        foreach (var l in Lasers) RemoveLaser(l);
        Lasers.Clear();
        foreach (var s in Shells) Destroy(s.Root);
        foreach (var a in Areas) { a.Cloud?.Release(); ForgeVisual.PointLights.Release(ref a.Light); Destroy(a.Root); Destroy(a.Prop); }
        foreach (var b in Beams) { ForgeVisual.PointLights.Release(ref b.Light); Destroy(b.CuttingAudio); Destroy(b.Root); }
        Shells.Clear();
        Areas.Clear();
        Beams.Clear();
        StasisSlow.Clear();
        StrikeBursts.Clear();
    }
}
