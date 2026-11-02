using System;
using System.Collections.Generic;
using ForgeOrdnance;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnanceArsenal;

/// <summary>Every peer, the host included: what the host's strike data shows. Shells fall from above the ceiling through the
/// level (no collision) onto the host's impact point, then flash, sound and shake; gas canisters and stasis emitters stay for
/// their area's lifetime; areas show a coloured light, a ring and their cloud (<see cref="StrikeEffects"/>), and stasis areas
/// slow the enemies inside on this peer (<see cref="StasisSlow"/>); the beam runs from above the ceiling down to the ground
/// or its target. Damage is never decided here.</summary>
internal static class StrikeVisuals
{
    private const float ShakeRange = 35f;
    /// <summary>Strike areas with a point light at once; the newest areas keep theirs.</summary>
    private const int MaxAreaLights = 2;
    private static readonly ForgeVisual.ShakeSpec BigShake = new(0.6f, ShakeRange, 0.6f, 20f), ShellShake = new(0.35f, ShakeRange, 0.6f, 20f),
        ClusterShake = new(0.2f, ShakeRange, 0.6f, 20f);
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
        internal float Start, Lands;
        internal bool Landed;
        internal float RemoveAt;
        /// <summary>The smoke trail's slot (-1: none) and, for a cluster bomblet, when it splits in the air (0: it does not).</summary>
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
    }

    private sealed class BeamLook
    {
        internal ulong Summoner;
        internal uint ThrowId;
        internal GameObject? Root;
        internal StrikeEffects.Beam? Fx;
        /// <summary>The beam's held light (0 while it has none), and the object its loop sound rides on, kept where the beam burns.</summary>
        internal int Light;
        internal GameObject? Loop;
        internal Vector3 From, To, ShownTo, LastMark;
        internal bool HasTarget, FxFailed, Marked;
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

    internal static void Receive(OrdnancePayloadData d)
    {
        try
        {
            switch ((DataTag)d.Tag)
            {
                case DataTag.Shell when Wire.TryShell(d.Data, out var shell): ShowShell(d, shell); break;
                case DataTag.Zone when Wire.TryZone(d.Data, out var zone): StartArea(d, zone); break;
                case DataTag.Beam when Wire.TryBeam(d.Data, out var beam): AimBeam(d, beam); break;
                case DataTag.BeamEnd: EndBeam(d); break;
                case DataTag.ZoneEnd: EndArea(d); break;
                default: break;
            }
        }
        catch (Exception error) { Plugin.Error("Showing strike data (tag " + d.Tag + ") failed: " + error); }
    }

    private static void ShowShell(OrdnancePayloadData d, in ShellRecord r)
    {
        var to = new Vector3(r.X, r.Y, r.Z);
        var from = new Vector3(r.X, Math.Max(r.Top, r.Y + 2f), r.Z);
        var model = ArsenalArt.Look(r.Look);
        GameObject? root = null;
        if (model != null)
        {
            var upright = r.Look is ShellLook.Canister or ShellLook.Emitter;
            root = model.Spawn(from, upright ? Quaternion.identity : ArsenalArt.NoseDown);
            if (root != null) root.name = "ForgeOrdnanceArsenal." + r.Look;
        }
        var now = Time.time;
        var falling = new Falling { Root = root, Summoner = d.SummonerLookup, ThrowId = d.ThrowId, Record = r, From = from, To = to, Start = now, Lands = now + r.Fall };
        falling.Trail = StrikeBursts.BeginTrail(r.Look, r.Kind, from, now);
        // A cluster bomblet splits a little above the floor (position follows the square-time fall); it needs room to fall first.
        var height = from.y - to.y;
        if (r.Kind == StrikeKind.Cluster && r.Fall > 0f && height > BurstHeight + 1.5f)
            falling.BurstAt = now + r.Fall * (float)Math.Sqrt(1f - BurstHeight / height);
        Shells.Add(falling);
        ArsenalAudio.PlayAt(ArsenalAudio.Cue.Incoming, to);
    }

    private static void Land(Falling s)
    {
        s.Landed = true;
        var r = s.Record;
        if (s.Root != null) s.Root.transform.position = s.To;
        StrikeBursts.EndTrail(s.Trail);
        s.Trail = -1;
        var now = Time.time;
        switch (r.Kind)
        {
            case StrikeKind.Gas:
                StrikeBursts.GasBurst(s.To, now);
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
        ArsenalAudio.PlayAt(r.Kind == StrikeKind.Cluster ? ArsenalAudio.Cue.ClusterSplit
            : big ? ArsenalAudio.Cue.BigBoom : ArsenalAudio.Cue.SmallBoom, s.To);
        Echoes.Add((now + StrikeTiming.DebrisDelay, s.To, false));
        if (r.Kind == StrikeKind.Incendiary) Echoes.Add((now + StrikeTiming.IgniteDelay, s.To, true));
        ForgeVisual.CameraShake.Shake(s.To, big ? BigShake : r.Kind == StrikeKind.Cluster ? ClusterShake : ShellShake);
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
        var cloud = StrikeEffects.CreateCloud(z.Kind, centre, z.Radius, colour, now, now + z.Seconds, d.ThrowId);
        if (cloud == null) Plugin.Error(z.Kind + " area of throw " + d.ThrowId + " has no cloud (the effect error above says why).");
        if (z.Kind == StrikeKind.Stasis)
        {
            StasisSlow.Add(d.SummonerLookup, d.ThrowId, centre, z.Radius, z.Slow, z.Seconds);
        }
        // The canister or emitter that landed here stays as the area's prop.
        GameObject? prop = null;
        foreach (var shell in Shells)
        {
            if (shell.Summoner != d.SummonerLookup || shell.ThrowId != d.ThrowId
                || shell.Record.Kind is not (StrikeKind.Gas or StrikeKind.Stasis)) continue;
            // The host sends the zone before this frame's visual tick. Settle and transfer it now, regardless of tick order.
            if (!shell.Landed) Land(shell);
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
        Areas.Add(new Area { Summoner = d.SummonerLookup, ThrowId = d.ThrowId, Root = root, LightSpec = lightSpec, Colour = colour, Start = now, Ends = now + z.Seconds, Prop = prop, Cloud = cloud });
        ArsenalAudio.PlayLoop(z.Kind switch { StrikeKind.Gas => ArsenalAudio.Cue.GasLoop,
            StrikeKind.Stasis => ArsenalAudio.Cue.StasisLoop, _ => ArsenalAudio.Cue.FireLoop }, root);
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
            || (!BeamCalls.Contains((d.SummonerLookup, d.ThrowId)) && !(d.Replayed && _beamReplayAllowed && !ForgeCore.Checkpoint.Recalling)))
        {
            return;
        }
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
            beam = new BeamLook { Summoner = d.SummonerLookup, ThrowId = d.ThrowId, Root = root, Loop = loop, From = root.transform.position, ShowAt = d.Replayed ? now : now + StrikeTiming.BeamChargeSeconds };
            beam.ShownTo = beam.From;
            Beams.Add(beam);
            if (!d.Replayed)
            {
                // The shell start is 3 m above the ceiling: the glow gathers just under it, where the player can see it.
                StrikeBursts.BeamCharge(new Vector3(r.OX, Math.Max(r.OY - 3.15f, r.TY + 2f), r.OZ), StrikeEffects.BeamColour, now);
                ArsenalAudio.PlayAt(ArsenalAudio.Cue.BeamStart, new Vector3(r.TX, r.TY, r.TZ));
            }
            ArsenalAudio.PlayLoop(ArsenalAudio.Cue.BeamLoop, loop);
        }
        // With a target the point is on it; without one it is the floor where the beam sweeps, so the beam always reaches down.
        beam.HasTarget = r.HasTarget;
        beam.LastUpdate = Time.time;
        beam.To = new Vector3(r.TX, r.TY, r.TZ);
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
        beam.Fx?.Release();
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
                var t = s.Lands <= s.Start ? 1f : Math.Clamp((now - s.Start) / (s.Lands - s.Start), 0f, 1f);
                var at = Vector3.Lerp(s.From, s.To, t * t);
                if (s.Root != null && !s.Root.WasCollected) s.Root.transform.position = at;
                if (s.Trail >= 0) StrikeBursts.MoveTrail(s.Trail, at, t, now);
                if (s.BurstAt > 0f && now >= s.BurstAt && t < 1f)
                {
                    // The cluster bomblet splits: the carrier is gone, its sparklets fly on to the points every peer derives from the call's seed.
                    s.BurstAt = 0f;
                    StrikeBursts.EndTrail(s.Trail);
                    s.Trail = -1;
                    Destroy(s.Root);
                    s.Root = null;
                    StrikeBursts.ClusterBurst(s.Summoner, s.ThrowId, at, s.To, s.Record.Blast, Math.Max(0f, s.Lands - now), now);
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
            if (Plugin.ExtraLights.Value && lit < MaxAreaLights && a.Root != null && !a.Root.WasCollected)
            {
                lit++;
                var fade = Math.Clamp((a.Ends - now) / 1.5f, 0f, 1f);
                var intensity = (2.2f + 0.6f * Mathf.Sin((now - a.Start) * 7f) + 0.3f * Mathf.Sin((now - a.Start) * 17f)) * fade;
                ForgeVisual.PointLights.Hold(ref a.Light, a.Root.transform.position, in a.LightSpec, now, intensity);
            }
            else ForgeVisual.PointLights.Release(ref a.Light);
            a.Cloud?.Tick(now);
        }
        StrikeBursts.Tick(now);
        StasisSlow.Tick(now);
        for (var i = Beams.Count - 1; i >= 0; i--)
        {
            var b = Beams[i];
            if (now - b.LastUpdate >= BeamSilenceSeconds)
            {
                BeamCalls.Remove((b.Summoner, b.ThrowId));
                b.Fx?.Release();
                ForgeVisual.PointLights.Release(ref b.Light);
                Destroy(b.CuttingAudio);
                Destroy(b.Root);
                Beams.RemoveAt(i);
                Plugin.Error("Cutting beam of throw " + b.ThrowId + " stopped after " + ForgeCore.Log.Num(BeamSilenceSeconds) + " s without a host update.");
                continue;
            }
            if (b.Root == null || b.Root.WasCollected) continue;
            b.ShownTo = Vector3.Lerp(b.ShownTo, b.To, Math.Min(1f, Time.deltaTime * (b.HasTarget ? 10f : 4f)));
            if (b.CuttingAudio != null && !b.CuttingAudio.WasCollected) b.CuttingAudio.transform.position = b.ShownTo;
            var drawn = now >= b.ShowAt;
            if (drawn && b.Fx == null && !b.FxFailed)
            {
                b.Fx = StrikeEffects.CreateBeam(StrikeEffects.BeamColour, now);
                if (b.Fx == null)
                {
                    b.FxFailed = true;
                    Plugin.Error("Cutting beam of throw " + b.ThrowId + " is not drawn (the effect error above says why).");
                }
            }
            b.Fx?.Show(b.From, b.ShownTo, now);
            if (drawn && b.Fx != null && Vector3.Distance(b.ShownTo, b.To) < 1.5f && (!b.Marked || Vector3.Distance(b.ShownTo, b.LastMark) >= BeamMarkSpacing))
            {
                // The route the beam walks stays scorched for a few seconds, glowing first and then dark.
                b.Marked = true;
                b.LastMark = b.ShownTo;
                StrikeBursts.BeamMark(b.ShownTo, StrikeEffects.BeamColour, now);
            }
            var burn = b.ShownTo + Vector3.up * 0.4f;
            if (b.Loop != null && !b.Loop.WasCollected) b.Loop.transform.position = burn;
            if (drawn && Plugin.ExtraLights.Value) ForgeVisual.PointLights.Hold(ref b.Light, burn, in BeamLight, now);
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
        foreach (var s in Shells) Destroy(s.Root);
        foreach (var a in Areas) { a.Cloud?.Release(); ForgeVisual.PointLights.Release(ref a.Light); Destroy(a.Root); Destroy(a.Prop); }
        foreach (var b in Beams) { b.Fx?.Release(); ForgeVisual.PointLights.Release(ref b.Light); Destroy(b.CuttingAudio); Destroy(b.Root); }
        Shells.Clear();
        Areas.Clear();
        Beams.Clear();
        StasisSlow.Clear();
        StrikeBursts.Clear();
    }
}
