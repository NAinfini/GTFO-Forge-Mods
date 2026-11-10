using System;
using System.Collections.Generic;
using System.Text.Json;
using AIGraph;
using ForgeOrdnance;
using ForgeVisual;
using Player;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>Payload type <c>arsenal.Deployable</c>: the standing devices (the dispenser in its foam and mine forms, the scanner tower, the floodlight). The
/// device arrives in ForgeOrdnance's turret shell (reused through <c>OrdnanceDelivery.StartShell</c>): the shell bores in, throws its lids off
/// and the device rises out of it, works until its time runs out, then sinks back and the shell sinks away (the shell draws all of it from
/// <see cref="GetShellAppearance"/>). What the device does is <see cref="Deployables"/>' and <see cref="DeployableActions"/>'.</summary>
internal sealed class DeployablePayload : IOrdnancePayload
{
    public object Parse(JsonElement payload)
    {
        var spec = DeployableSpec.Parse(payload);
        if (Models.Get(spec.Head) is not { } head)
            throw new PayloadParameterException("Head", "no ForgeVisual model " + spec.Head + " is registered (its pack must be registered in its plugin's Load).");
        if (spec.Kind == DeployableKind.Dispenser && Dispenser.MissingPart(head.Prefab) is { } part)
            throw new PayloadParameterException("Head", "the model " + spec.Head + " has no part named " + part + " (a dispenser needs column, plate1..., mount1...).");
        if (spec.Munition.Length > 0 && Models.Get(spec.Munition) == null)
            throw new PayloadParameterException("Munition", "no ForgeVisual model " + spec.Munition + " is registered (its pack must be registered in its plugin's Load).");
        return spec;
    }

    /// <summary>The shell's own landing replaces the framework's arrival shake.</summary>
    public bool HasOwnArrival => true;

    public void Execute(OrdnanceExecution execution) => Deployables.HostArrived(execution);

    public void OnArrived(OrdnancePresentation presentation) => Deployables.Arrived(presentation);

    public void OnData(OrdnancePayloadData data) => Dispenser.OnData(data);

    public OrdnanceShellAppearance? GetShellAppearance(OrdnanceDefinition definition)
    {
        var spec = (DeployableSpec)definition.Parameters;
        var appearance = new OrdnanceShellAppearance(spec.Burrow, spec.WorkSeconds) { Head = Models.Get(spec.Head), HeadScale = spec.Scale, HeadFront = ArsenalArt.FrontToForward };
        // A dispenser draws its upper part back into the base between rounds.
        if (spec.Kind == DeployableKind.Dispenser) appearance.Dive = seconds => 1f - DispenserPlan.Pose(spec, seconds).Lift;
        return appearance;
    }
}

/// <summary>Every peer: the devices in the level. Each is drawn by its shell (the device rises out of the opened shell and sinks back at the end,
/// <see cref="OrdnanceShell.Head"/>) from the same call data, so peers agree to within a network delay; the host additionally runs what the
/// device does (<see cref="DeployableActions"/>). Cleared when calls in flight are voided (level exit, checkpoint reload, host change).</summary>
internal static class Deployables
{
    private static readonly Color Warm = new(1f, 0.94f, 0.8f);
    /// <summary>Where the floodlight's lamps are, in the head model's own units.</summary>
    private static readonly Vector3 LightLift = new(0f, 0.75f, 0f);
    /// <summary>How far a scanner's petals fold (they are modelled open), and how fast a scanner's top and a floodlight's fan turn, degrees a second.</summary>
    private const float FoldDegrees = 42f, ScanTurn = 45f, FanTurn = 720f;

    private sealed class HostCall
    {
        internal PlayerAgent? Source;
        internal AIG_CourseNode Node = null!;
    }

    private static readonly Dictionary<(ulong, uint), HostCall> Calls = new();
    private static readonly List<Deployable> Units = new();

    internal static IReadOnlyList<Deployable> All => Units;

    /// <summary>Host: remembers who called and where the shell lands (the game's own spawns need both).</summary>
    internal static void HostArrived(OrdnanceExecution e)
    {
        if (e.CourseNode == null) throw new InvalidOperationException("The landing course node is gone.");
        Calls[(e.SummonerLookup, e.ThrowId)] = new HostCall { Source = e.Summoner, Node = e.CourseNode };
    }

    internal static void Arrived(OrdnancePresentation p)
    {
        var spec = (DeployableSpec)p.Parameters;
        var ready = Time.time + OrdnanceDelivery.ShellReadyDelay(p.Arrival, spec.Burrow);
        var unit = new Deployable
        {
            Spec = spec,
            Summoner = p.SummonerLookup,
            ThrowId = p.ThrowId,
            Position = p.Position,
            Rotation = p.Rotation,
            OpenAt = ready - spec.Burrow.HeadSeconds,
            ReadyAt = ready,
            EndsAt = ready + spec.WorkSeconds,
            FieldEndsAt = ready + spec.WorkSeconds + spec.FieldSeconds,
            Seed = (int)(p.ThrowId * 2654435761u ^ (uint)p.SummonerLookup),
            DefinitionHash = ForgeCore.Fnv.Hash32(p.Definition.Id),
            Shell = OrdnanceDelivery.StartShell(p),
        };
        if (spec.Kind == DeployableKind.Floodlight) unit.LightSpec = new PointLightSpec(Warm, spec.LightRadius, spec.Intensity, 0f, 0.03f);
        if (ForgeCore.Session.IsHost && Calls.TryGetValue((p.SummonerLookup, p.ThrowId), out var call))
        {
            unit.Source = call.Source;
            unit.Node = call.Node;
        }
        Units.Add(unit);
    }

    internal static void Tick(float now)
    {
        if (Units.Count == 0) return;
        for (var i = Units.Count - 1; i >= 0; i--)
        {
            var u = Units[i];
            try
            {
                if (Step(u, now)) continue;
            }
            catch (Exception error) { Plugin.Logger.ErrorOnce("A " + u.Spec.Kind + " device failed and was removed: " + error); }
            Retire(u);
            Units.RemoveAt(i);
        }
    }

    /// <summary>False when the device has sunk away.</summary>
    private static bool Step(Deployable u, float now)
    {
        if (now < u.OpenAt) return true;
        var spec = u.Spec;
        var rise = u.Shell?.HeadRise ?? 0f;
        if (spec.Kind == DeployableKind.Floodlight && rise > 0f && ArsenalFxSlots.Floodlight is { } tower)
            Fx.Hold(tower, u.TowerKey, u.Position + LightLift * spec.Scale, Vector3.up, float.NaN, Color.white);
        if (u.Shell?.Head is { } moving)
        {
            if (rise > 0f && !u.Rose)
            {
                u.Rose = true;
                ArsenalAudio.PlayOn(ArsenalAudio.Cue.TurretDeploy, moving);
                if (spec.Kind == DeployableKind.Scanner) ArsenalAudio.PlayOn(ArsenalAudio.Cue.RadarDeploy, moving);
                if (spec.Kind == DeployableKind.Floodlight)
                {
                    ArsenalAudio.PlayOn(ArsenalAudio.Cue.FloodOn, moving);
                    ArsenalAudio.Loop(ArsenalAudio.Cue.FloodFan, moving, () => Time.time < u.EndsAt);
                }
            }
            Parts(u, moving, now);
        }
        // Each scan sounds once, when it goes out, with the sweep it shows.
        if (spec.Kind == DeployableKind.Scanner && now >= u.ReadyAt + spec.WorkDelay && now < u.EndsAt - spec.WorkDelay && now >= u.NextSweepFx)
        {
            u.NextSweepFx = now + Math.Max(0.5f, spec.Scan);
            if (u.Shell?.Head is { } scanner) ArsenalAudio.PlayOn(ArsenalAudio.Cue.RadarSweep, scanner);
            ArsenalFxSlots.Play(ArsenalFxSlots.ScannerSweep, u.Position + Vector3.up * (0.5f * spec.Scale), Vector3.up);
        }
        if (spec.Kind == DeployableKind.Floodlight && rise > 0f)
            PointLights.Hold(ref u.Light, u.Position + LightLift * spec.Scale, in u.LightSpec, now, rise);
        if (spec.Kind == DeployableKind.Floodlight) DeployableActions.ApplyFog(u, now);
        if (spec.Kind == DeployableKind.Dispenser)
        {
            if (u.Shell?.Head is { } head) Dispenser.Frame(u, head, now);
            else Dispenser.Settle(u, now);
        }
        if (ForgeCore.Session.IsHost)
        {
            if (now >= u.ReadyAt + spec.WorkDelay && now < u.FieldEndsAt - spec.WorkDelay) DeployableActions.HostWork(u, now);
            else if (now >= u.FieldEndsAt && !u.Ended)
            {
                u.Ended = true;
                DeployableActions.HostEnd(u);
            }
        }
        return now < Math.Max(u.EndsAt + spec.Burrow.HeadSeconds, u.FieldEndsAt);
    }

    /// <summary>A scanner rises folded, unfolds and turns its top while it works, and folds again before it sinks; a floodlight's fan keeps turning.
    /// Read from the time alone, so every peer draws the same pose.</summary>
    private static void Parts(Deployable u, GameObject head, float now)
    {
        if (u.Spec.Kind == DeployableKind.Dispenser) return;
        if (u.Art == null)
        {
            u.Art = new Articulation(head);
            u.Moving = u.Art.Named(u.Spec.Kind == DeployableKind.Scanner ? "petal" : "fan");
            if (u.Spec.Kind == DeployableKind.Scanner) u.Top = u.Art.Named("yaw").Count > 0 ? u.Art.Named("yaw")[0] : null;
        }
        if (u.Spec.Kind == DeployableKind.Floodlight)
        {
            foreach (var fan in u.Moving) u.Art.Turn(fan, Vector3.up, (now - u.OpenAt) * FanTurn % 360f);
            return;
        }
        var unfold = u.Spec.UnfoldSeconds;
        var open = unfold <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.Min((now - u.ReadyAt) / unfold, (u.EndsAt - now) / unfold));
        foreach (var petal in u.Moving) u.Art.Turn(petal, Vector3.right, (1f - open) * FoldDegrees);
        if (u.Top != null) u.Art.Turn(u.Top, Vector3.up, Mathf.Max(0f, now - u.ReadyAt - unfold) * ScanTurn % 360f);
    }

    private static void Retire(Deployable u)
    {
        Dispenser.Release(u);
        PointLights.Release(ref u.Light);
        if (ForgeCore.Session.IsHost && !u.Ended)
        {
            u.Ended = true;
            try { DeployableActions.HostEnd(u); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Ending a " + u.Spec.Kind + " device failed: " + error); }
        }
        Calls.Remove((u.Summoner, u.ThrowId));
    }

    internal static void Clear()
    {
        foreach (var u in Units)
        {
            PointLights.Release(ref u.Light);
            // A reload or a new host voids the calls but not the fog repeller the old host stood: it goes, so the replayed drop does not stand a second.
            try { DeployableActions.DespawnFog(u); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Taking back a floodlight's fog repeller failed: " + error); }
        }
        Units.Clear();
        Calls.Clear();
        DeployableActions.Clear();
    }
}

/// <summary>One device in the level, as every peer knows it; the host-only fields are what its work keeps between frames.</summary>
internal sealed class Deployable
{
    /// <summary>This device's own hold key for its floodlight tower.</summary>
    internal readonly int TowerKey = Fx.NewKey();
    internal DeployableSpec Spec = null!;
    internal ulong Summoner;
    internal uint ThrowId;
    internal Vector3 Position;
    internal Quaternion Rotation;
    /// <summary>The device starts to rise at <see cref="OpenAt"/> and has risen at <see cref="ReadyAt"/>; it works until <see cref="EndsAt"/>.</summary>
    internal float OpenAt, ReadyAt, EndsAt;
    /// <summary>When what the device left (a dispenser's laid munitions) is over; <see cref="EndsAt"/> for the others.</summary>
    internal float FieldEndsAt;
    internal int Seed;
    /// <summary>The hash of the definition's id: the dispenser's field record carries it so a peer that joined later can tell what the munitions are.</summary>
    internal uint DefinitionHash;
    /// <summary>The shell that draws the device; null when the art bundle did not load.</summary>
    internal OrdnanceShell? Shell;
    /// <summary>When the scanner tower's sweep effect next plays on this peer.</summary>
    internal float NextSweepFx;
    internal PointLightSpec LightSpec;
    /// <summary>The scanner's petals or the floodlight's fan, and the scanner's turning top.</summary>
    internal Articulation? Art;
    internal List<Transform> Moving = new();
    internal Transform? Top;
    internal int Light;

    // Host only.
    internal PlayerAgent? Source;
    internal AIG_CourseNode? Node;
    internal bool Started, Ended, Laid;
    /// <summary>The device's rise sound has played on this peer.</summary>
    internal bool Rose;
    /// <summary>Host only: this device's own random sequence.</summary>
    internal System.Random? Rng;
    internal float NextAt;
    internal Dispenser.HostField? Field;
    internal ItemReplicator? FogReplicator;

    // Every peer: the fog repeller the host stood for a floodlight, once it has spawned here and its sphere has been sized.
    internal FogRepellerInstance? FogItem;
    internal bool FogApplied;
}
