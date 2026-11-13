using System;
using System.Collections.Generic;
using ForgeVisual;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnance.Arsenal;

/// <summary>One dispenser's field as every peer shows it: the munitions in its holes, flying, lying and bursting. Built from the device's own
/// timeline (<see cref="DispenserPlan"/>) and the field record the host sends (where each one lands and which still lie), so peers draw the same
/// throw; a player who joins later has no device and sees the lying ones still.</summary>
internal sealed class DispenserView
{
    /// <summary>The column's rise effect has played.</summary>
    internal bool RiseShown;
    /// <summary>The plates stand open (a sound plays when this changes).</summary>
    internal bool PlatesOpen;
    internal DeployableSpec? Spec;
    /// <summary>What a peer that joined later found by the record's definition hash: it shows the lying munitions and their bursts without the device.</summary>
    internal DeployableSpec? Late;
    internal int Count;
    internal ModelHandle? Model;
    internal Articulation? Art;
    internal Transform? Column;
    internal readonly List<Transform> Plates = new();
    internal Transform[] Mounts = Array.Empty<Transform>();
    internal GameObject?[] Look = Array.Empty<GameObject?>();
    internal Vector3[] At = Array.Empty<Vector3>();
    internal Vector3[] From = Array.Empty<Vector3>();
    internal bool[] Known = Array.Empty<bool>(), Gone = Array.Empty<bool>(), Flying = Array.Empty<bool>(), Landed = Array.Empty<bool>(), Armed = Array.Empty<bool>();

    internal void Size(int count)
    {
        if (count <= Count) return;
        Array.Resize(ref Look, count);
        Array.Resize(ref At, count);
        Array.Resize(ref From, count);
        Array.Resize(ref Known, count);
        Array.Resize(ref Gone, count);
        Array.Resize(ref Flying, count);
        Array.Resize(ref Landed, count);
        Array.Resize(ref Armed, count);
        Count = count;
    }

    internal void Destroy()
    {
        for (var i = 0; i < Count; i++)
        {
            if (Look[i] != null && !Look[i]!.WasCollected) Object.Destroy(Look[i]);
            Look[i] = null;
        }
    }
}

/// <summary>The device kind <see cref="DeployableKind.Dispenser"/> (its head is the upper half: column and plates; the closed base is the shell's, and the
/// shell lowers both between rounds): one flow for every munition (the data says which). Host: lays out where each
/// munition lands from the column's own turn, then watches the lying ones and bursts one when an enemy comes close (the game's explosion through
/// ForgeCore's <c>Combat.Explode</c>, C-foam sprayed all round through <c>ArsenalCombat.SprayGlue</c>, or both). Every peer: poses the plates and the
/// column and draws the munitions leave their holes and fly to the host's landing points.
/// <para>Model nodes: <c>column</c>, the part that turns about its own up axis; under it <c>plate1</c>, <c>plate2</c> ... (as many as the model has), parts that
/// open like a skirt (turned about their own X axis, hinged at the top, their bottom edge swinging outward, local -Z); <c>mount1</c>, <c>mount2</c> ..., where a
/// munition hangs until it is thrown, its top pointing out, each under its plate so it swings with it. Munitions take the mounts in number order, so a model
/// numbers them plate after plate round the column, the first hole of every plate and then the next (the first plate at the model's front, going
/// clockwise from above; the definition's <c>Plates</c> says how many). A model without these is refused when its definition is read.</para></summary>
internal static class Dispenser
{
    private const float OpenDegrees = 70f;
    private const float CheckSeconds = 0.05f;
    /// <summary>A munition on its mount: its top (up) along the mount's outward side (back).</summary>
    private static readonly Quaternion OnMount = Quaternion.Euler(-90f, 0f, 0f);
    private const float LeadDegrees = 25f;
    private const float Apex = 1.4f;
    /// <summary>Tries at a landing place for one munition, and the distance from the others that is good enough to stop trying.</summary>
    private const int LayAttempts = 8;
    private const float LaySpacing = 2f;

    private static readonly Dictionary<(ulong, uint), DispenserView> Views = new();

    /// <summary>The host's record of one field: where each munition lands and which still lie.</summary>
    internal sealed class HostField
    {
        internal float[] Positions = Array.Empty<float>();
        internal int Count;
        internal ulong Lying, Burst;
        internal float NextCheck;
        /// <summary>Half of the munitions are asked about on each check, the other half on the next: each is asked every other time.</summary>
        internal int Phase;
    }

    // ---- host -------------------------------------------------------------------------------------------------------

    internal static void HostWork(Deployable u, float now)
    {
        if (!u.Laid)
        {
            u.Laid = true;
            Lay(u);
        }
        if (u.Field is { } field) Check(u, field, now);
    }

    internal static void HostEnd(Deployable u)
    {
        if (u.Field is not { } field) return;
        // The field's time ran out: every munition that still lies just goes.
        field.Lying = 0;
        field.Burst = 0;
        if (Send(u, field)) OrdnanceApi.ClearState(DeployablePayload.Type, u.Summoner, u.ThrowId);
        u.Field = null;
    }

    private static bool Send(Deployable u, HostField field)
        => OrdnanceApi.SendState(DeployablePayload.Type, u.Summoner, u.ThrowId, (byte)DataTag.Mines, MineWire.Encode(field.Positions, field.Count, field.Lying, field.Burst, u.Position.x, u.Position.y, u.Position.z, u.DefinitionHash));

    /// <summary>Decides every landing at once, from the direction each hole points when its munition leaves (the hole's place round the column plus
    /// the column's turn at that moment, a little ahead of it), and a distance taken evenly over the ring's area; of the places tried for one, the one farthest from those already chosen.</summary>
    private static void Lay(Deployable u)
    {
        var spec = u.Spec;
        var total = spec.Count * spec.Rounds;
        var field = new HostField { Positions = new float[total * 3] };
        var yaw = u.Rotation.eulerAngles.y;
        var from = u.Position + Vec.Up * (1.2f * spec.Scale);
        var mask = LayerManager.MASK_WORLD;
        var inner = spec.MinRange * spec.MinRange;
        var outer = spec.MaxRange * spec.MaxRange;
        for (var i = 0; i < total; i++)
        {
            var heading = yaw + DispenserPlan.SlotDegrees(spec, i) + DispenserPlan.LaunchSpin(spec, i) + LeadDegrees;
            var best = -1f;
            var place = Vec.Zero;
            for (var attempt = 0; attempt < LayAttempts && best < LaySpacing; attempt++)
            {
                var azimuth = heading + (attempt == 0 ? 0f : (float)(u.Rng!.NextDouble() * 40.0 - 20.0));
                var range = Mathf.Sqrt(inner + (outer - inner) * (float)u.Rng!.NextDouble());
                var target = u.Position + Quaternion.Euler(0f, azimuth, 0f) * Vec.Forward * range;
                if (!ForgeCore.Level.FloorBelow(target + Vec.Up * 1.5f, 4f, out var floor) || floor.normal.y < 0.6f) continue;
                if (Physics.Linecast(from, floor.point + Vec.Up * 0.4f, mask, QueryTriggerInteraction.Ignore)) continue;
                var p = floor.point + Vec.Up * 0.03f;
                var nearest = float.MaxValue;
                for (var other = 0; other < i; other++)
                    if ((field.Lying & 1UL << other) != 0)
                        nearest = Mathf.Min(nearest, Vec.SqrLength(new Vector3(field.Positions[other * 3], field.Positions[other * 3 + 1], field.Positions[other * 3 + 2]) - p));
                nearest = Mathf.Sqrt(nearest);
                if (nearest <= best) continue;
                best = nearest;
                place = p;
            }
            if (best < 0f) continue;
            field.Positions[i * 3] = place.x;
            field.Positions[i * 3 + 1] = place.y;
            field.Positions[i * 3 + 2] = place.z;
            field.Lying |= 1UL << i;
            field.Count = i + 1;
        }
        if (field.Count == 0) { Plugin.Logger.ErrorOnce("The dispenser found no floor to land a munition on within " + spec.MaxRange + " m."); return; }
        if (!Send(u, field)) return;
        u.Field = field;
    }

    private static void Check(Deployable u, HostField field, float now)
    {
        if (now < field.NextCheck) return;
        field.NextCheck = now + CheckSeconds;
        field.Phase ^= 1;
        var spec = u.Spec;
        var t = now - u.ReadyAt;
        var changed = false;
        for (var i = 0; i < field.Count; i++)
        {
            if ((i & 1) != field.Phase || (field.Lying & 1UL << i) == 0 || t < DispenserPlan.LandAt(spec, i) + spec.ArmSeconds) continue;
            var at = new Vector3(field.Positions[i * 3], field.Positions[i * 3 + 1], field.Positions[i * 3 + 2]);
            var near = false;
            foreach (var enemy in ForgeCore.Enemies.Near(at, spec.TriggerRadius))
                if (enemy != null && enemy.Alive) { near = true; break; }
            if (!near) continue;
            field.Lying &= ~(1UL << i);
            field.Burst |= 1UL << i;
            changed = true;
            if (spec.Damage > 0f) ForgeCore.Combat.Explode(at + Vec.Up * 0.15f, spec.BlastRadius, spec.Damage, spec.Force);
            if (spec.Spray > 0)
                ArsenalCombat.SprayGlue(DeployableActions.Owner(u), at + Vec.Up * 0.3f, spec.Spray, spec.SpraySpeed * 0.7f, spec.SpraySpeed * 1.3f, u.Seed + i);
        }
        if (changed) Send(u, field);
    }

    // ---- every peer: the field record ---------------------------------------------------------------------------------

    internal static void OnData(OrdnancePayloadData data)
    {
        if (data.Tag != (byte)DataTag.Mines) return;
        if (!MineWire.TryDecode(data.Data, out var raw, out var count, out var lying, out var burst, out var definition))
        {
            Plugin.Logger.Error("A munition record of throw " + data.ThrowId + " was malformed and ignored.");
            return;
        }
        var key = (data.SummonerLookup, data.ThrowId);
        if (!Views.TryGetValue(key, out var view))
        {
            // Nothing of the device on this peer (it joined later): the lying ones are shown where they lie.
            if (lying == 0) return;
            view = new DispenserView { Late = DefinitionOf(definition) };
            Views[key] = view;
        }
        view.Size(count);
        var spec = view.Spec;
        var shown = spec ?? view.Late;
        var now = Time.time;
        for (var i = 0; i < count; i++)
        {
            var bit = 1UL << i;
            var at = new Vector3(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]);
            if ((lying & bit) != 0)
            {
                if (view.Known[i]) continue;
                view.At[i] = at;
                view.Known[i] = true;
                if (spec == null) PlaceStatic(view, i);
                continue;
            }
            if (view.Gone[i]) continue;
            view.Gone[i] = true;
            // A record replayed to a late joiner holds the field's history: those bursts are over, only the munitions still lying are shown.
            if ((burst & bit) != 0 && !data.Replayed) ShowBurst(shown, at, now);
            if (view.Look[i] != null && !view.Look[i]!.WasCollected) Object.Destroy(view.Look[i]);
            view.Look[i] = null;
        }
        // Holes past the record's end were never loaded with a munition that landed.
        if (spec != null)
            for (var i = count; i < view.Count; i++)
            {
                view.Gone[i] = true;
                if (view.Look[i] != null && !view.Look[i]!.WasCollected) Object.Destroy(view.Look[i]);
                view.Look[i] = null;
            }
        if (lying == 0 && spec == null) Views.Remove(key);
    }

    /// <summary>The dispenser definition whose id hashes to <paramref name="hash"/> (the one the device was made from), or null when this install has none.</summary>
    private static DeployableSpec? DefinitionOf(uint hash)
    {
        foreach (var definition in OrdnanceContent.Set.Definitions)
            if (definition.Parameters is DeployableSpec { Kind: DeployableKind.Dispenser } spec && ForgeCore.Fnv.Hash32(definition.Id) == hash) return spec;
        return null;
    }

    private static void PlaceStatic(DispenserView view, int i)
    {
        var model = (view.Late is { Munition.Length: > 0 } late ? Models.Get(late.Munition) : null) ?? ArsenalArt.Bomblet;
        view.Look[i] = model?.Spawn(view.At[i], ArsenalArt.FrontToForward);
    }

    /// <summary>A burst sized to its blast, with the big boom. A munition that only sprays foam has no burst of its own: the game's foam is the sight.</summary>
    private static void ShowBurst(DeployableSpec? spec, Vector3 at, float now)
    {
        if (spec != null && spec.Damage <= 0f)
        {
            ArsenalAudio.PlayAt(ArsenalAudio.Cue.GlueTrigger, at);
            ForgeAudio.AudioKit.PostGameEvent(ArsenalAudio.GlueGunShotEvent, at);
            ArsenalFxSlots.Play(ArsenalFxSlots.FoamSpray, at, Vec.Up, (spec.BlastRadius > 0f ? spec.BlastRadius : 3f) * 2f);
            return;
        }
        ArsenalAudio.PlayAt(ArsenalAudio.Cue.MineTrigger, at);
        ArsenalFxSlots.Play(ArsenalFxSlots.MineBlast, at, Vec.Up, (spec?.BlastRadius ?? 7f) * 2f);
        ArsenalAudio.PlayAt(ArsenalAudio.Cue.MineExplode, at);
    }

    // ---- every peer: the device's own motion ---------------------------------------------------------------------------

    /// <summary>Every frame on every peer: poses the plates and the column and moves each munition in its hole or through the air.
    /// <paramref name="head"/> is the spawned device model.</summary>
    internal static void Frame(Deployable u, GameObject head, float now)
    {
        var spec = u.Spec;
        var key = (u.Summoner, u.ThrowId);
        if (!Views.TryGetValue(key, out var view)) Views[key] = view = new DispenserView();
        if (view.Spec == null)
        {
            // The record may have come first (a player who joined as it started): what it showed becomes the device's.
            for (var i = 0; i < view.Count; i++)
                if (view.Look[i] != null && !view.Look[i]!.WasCollected) Object.Destroy(view.Look[i]);
            view.Spec = spec;
            view.Size(spec.Count * spec.Rounds);
        }
        if (view.Art == null) Bind(view, head, spec);
        var t = now - u.ReadyAt;
        var pose = DispenserPlan.Pose(spec, t);
        if (!view.RiseShown && pose.Open > 0f)
        {
            view.RiseShown = true;
            ArsenalFxSlots.Play(spec.Damage > 0f ? ArsenalFxSlots.MineRise : ArsenalFxSlots.FoamRise, u.Position, Vec.Up);
        }
        if ((pose.Open > 0f) != view.PlatesOpen)
        {
            view.PlatesOpen = !view.PlatesOpen;
            // The column's turning while the plates stand open; it ends with the plates or the device.
            if (view.PlatesOpen) ArsenalAudio.Loop(ArsenalAudio.Cue.TossSpin, head, () => view.PlatesOpen);
            ForgeOrdnance.OrdnanceArrivalSounds.Play(view.PlatesOpen ? ForgeOrdnance.OrdnanceArrivalSound.Open : ForgeOrdnance.OrdnanceArrivalSound.Leave, head.transform);
        }
        var art = view.Art!;
        var hinge = Vector3.right;
        for (var i = 0; i < view.Plates.Count; i++) art.Turn(view.Plates[i], hinge, pose.Open * OpenDegrees);
        art.Turn(view.Column!, Vec.Up, pose.SpinDegrees);
        for (var i = 0; i < spec.Count * spec.Rounds; i++) Munition(view, spec, i, t);
    }

    /// <summary>Every frame once the device's head has sunk away: munitions of the last round still flying carry on to where they land and arm
    /// there, until the field's own time runs out (the pose and the holes belong to the head and are gone).</summary>
    internal static void Settle(Deployable u, float now)
    {
        if (!Views.TryGetValue((u.Summoner, u.ThrowId), out var view) || view.Art == null) return;
        var spec = u.Spec;
        var t = now - u.ReadyAt;
        for (var i = 0; i < spec.Count * spec.Rounds; i++) Munition(view, spec, i, t);
    }

    private static void Bind(DispenserView view, GameObject head, DeployableSpec spec)
    {
        var art = new Articulation(head);
        view.Art = art;
        view.Model = (spec.Munition.Length > 0 ? Models.Get(spec.Munition) : null) ?? ArsenalArt.Bomblet;
        view.Plates.AddRange(art.Named("plate"));
        view.Column = art.Named("column")[0];
        var mounts = art.Named("mount");
        mounts.Sort((a, b) => Number(a.name).CompareTo(Number(b.name)));
        view.Mounts = mounts.ToArray();
    }

    private static int Number(string name) => int.TryParse(name.Substring("mount".Length), out var n) ? n : 0;

    /// <summary>The part a dispenser model lacks, or null.</summary>
    internal static string? MissingPart(GameObject model)
    {
        var art = new Articulation(model);
        foreach (var part in new[] { "column", "plate", "mount" })
            if (art.Named(part).Count == 0) return part;
        return null;
    }

    private static void Munition(DispenserView view, DeployableSpec spec, int i, float t)
    {
        if (view.Gone[i] || view.Model == null) return;
        var look = view.Look[i];
        var launch = DispenserPlan.LaunchAt(spec, i);
        if (look == null || look.WasCollected)
        {
            if (view.Flying[i] || t < DispenserPlan.LoadedAt(spec, i / spec.Count)) return;
            // In number order: the mounts go plate after plate, and one round's holes are not the next one's.
            var mount = view.Mounts[i % view.Mounts.Length];
            if (mount == null || mount.WasCollected) return;
            look = view.Model.Spawn(mount.position, mount.rotation * OnMount, mount);
            view.Look[i] = look;
            if (look == null) { view.Gone[i] = true; return; }
            // Its size in the hole, whatever scale the device has; it grows to its own size in flight.
            look.transform.localScale = Vector3.one * (spec.SeatScale / mount.lossyScale.x);
        }
        if (view.Landed[i])
        {
            if (!view.Armed[i] && t >= DispenserPlan.LandAt(spec, i) + spec.ArmSeconds)
            {
                view.Armed[i] = true;
                ArsenalFxSlots.Play(ArsenalFxSlots.MineArm, view.At[i], Vec.Up);
            }
            return;
        }
        if (t < launch || !view.Known[i]) return;
        var transform = look.transform;
        if (!view.Flying[i])
        {
            view.Flying[i] = true;
            var from = transform.position;
            ArsenalAudio.PlayAt(ArsenalAudio.Cue.TossOne, from);
            ArsenalFxSlots.Play(ArsenalFxSlots.MineScatter, from, Vec.Up);
            view.From[i] = from;
            transform.SetParent(null, true);
        }
        var k = (t - launch) / spec.FlightSeconds;
        if (k >= 1f)
        {
            view.Landed[i] = true;
            transform.SetPositionAndRotation(view.At[i], ArsenalArt.FrontToForward);
            transform.localScale = Vector3.one;
            ArsenalAudio.PlayAt(spec.Damage > 0f ? ArsenalAudio.Cue.MineLand : ArsenalAudio.Cue.GlueLand, view.At[i]);
            return;
        }
        transform.SetPositionAndRotation(Thrown.Arc(view.From[i], view.At[i], Apex, k), Quaternion.Euler(k * 540f, i * 47f, 0f) * ArsenalArt.FrontToForward);
        transform.localScale = Vector3.one * Mathf.Lerp(spec.SeatScale, 1f, k);
    }

    /// <summary>The device is gone (its field time ran out): its munitions go with it.</summary>
    internal static void Release(Deployable u)
    {
        if (!Views.Remove((u.Summoner, u.ThrowId), out var view)) return;
        view.Destroy();
    }

    internal static void Clear()
    {
        foreach (var view in Views.Values) view.Destroy();
        Views.Clear();
    }
}
