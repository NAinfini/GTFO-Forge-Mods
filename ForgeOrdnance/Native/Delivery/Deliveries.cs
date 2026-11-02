using System;
using System.Collections.Generic;
using LevelGeneration;
using SNetwork;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeOrdnance;

/// <summary>One timeline every peer plays from the arrival moment, and the host schedules its spawns against.</summary>
internal static class DeliveryTiming
{
    internal const float FallHeight = 40f;
    internal const float FallSeconds = PodTimeline.FallSeconds;
    /// <summary>The drill stops this far above its rest and then bores in.</summary>
    internal const float SinkDepth = 0.25f;
    internal const float SinkSeconds = PodTimeline.SinkSeconds;
    internal const float PodOpenAfterImpact = PodTimeline.PodOpenAfterImpact;
    /// <summary>The clamp-release sound starts this long before the doors begin to open.</summary>
    internal const float UnlatchLead = 1.2f;
    internal const float DoorSeconds = 1.4f;
    internal const float ItemsAfterOpen = 1.6f;
    internal const float PodLingerAfterEmpty = 15f;
    internal const float LeaveSeconds = 2.5f;
    internal const float ShellOpenAfterImpact = 1.2f;
    internal const float PetalSeconds = 1f;
    /// <summary>The turret stands up this long after the petals begin to open: when they are almost fully open.</summary>
    internal const float SentryAfterOpen = 1.2f;
    internal const float ShellLinger = 6f;

    /// <summary>When the doors stand open and the weapon racks show their contents (<see cref="OrdnanceDelivery.PodContentsDelay"/>).</summary>
    internal static float ContentsOpenDelay => FallSeconds + PodOpenAfterImpact + ItemsAfterOpen;
    internal static float SentrySpawnDelay => FallSeconds + ShellOpenAfterImpact + SentryAfterOpen;

    /// <summary>The pod's door axis (its local X) points back toward the thrower: <paramref name="yaw"/> faces from the thrower
    /// to the landing point.</summary>
    internal static Quaternion PodRotation(float yaw) => Quaternion.Euler(0f, yaw + 90f, 0f);
}

/// <summary>The drop pods and turret shells in the level (every peer) plus the host's delayed spawns and its watch over the
/// pickups in each pod. Cleared on level exit, checkpoint recovery and host change, like every call in flight.</summary>
internal static class Deliveries
{
    private const float ContentsCheckSeconds = 0.5f;
    /// <summary>How far from a bay's anchor a placed pickup still counts as lying in that bay.</summary>
    private const float ShutBayReach = 0.6f;
    private static readonly List<Delivery> Active = new();
    private static readonly List<(float Due, Action Work)> HostWork = new();
    private static readonly List<Contents> Watched = new();

    private sealed class Contents
    {
        internal ulong Thrower;
        internal uint ThrowId;
        internal ItemInLevel?[] Items = Array.Empty<ItemInLevel?>();
        internal float NextCheck;
    }

    /// <summary>The vehicle stage of an arrival (<see cref="ArrivalPlan"/>): none for a direct one.</summary>
    internal static OrdnancePod? StartPod(OrdnancePresentation p)
        => !ArrivalPlan.HasVehicle(p.Arrival) ? null
            : FindPod(p.SummonerLookup, p.ThrowId) ?? (Add(new DropPod(p.SummonerLookup, p.ThrowId, p.Position, p.Rotation.eulerAngles.y, 0f, false, p.Definition), out var d) ? ((DropPod)d).Handle : null);

    internal static OrdnanceShell? StartShell(OrdnancePresentation p, uint lookKey = 0)
        => !ArrivalPlan.HasVehicle(p.Arrival) ? null
            : FindShell(p.SummonerLookup, p.ThrowId) ?? (Add(new TurretShell(p.SummonerLookup, p.ThrowId, p.Position, p.Rotation.eulerAngles.y, 0f, false, lookKey), out var d) ? ((TurretShell)d).Handle : null);

    /// <summary>Host: what a player joining now needs to see every arrival in its current state.</summary>
    internal static List<DeliveryReplayMessage> ReplayState()
    {
        var now = Time.time;
        var list = new List<DeliveryReplayMessage>();
        foreach (var d in Active)
        {
            var landing = new Vec3(d.LandingPoint.x, d.LandingPoint.y, d.LandingPoint.z);
            var message = new DeliveryReplayMessage { Thrower = d.Thrower, ThrowId = d.ThrowId, Landing = landing, Yaw = d.Yaw, Elapsed = now - d.StartTime, SinceEmptied = -1f };
            if (d is DropPod pod) { message.Kind = DeliveryReplayMessage.Pod; message.SinceEmptied = pod.SinceEmptied(now); message.DefinitionKey = pod.DefinitionKey; }
            else { message.Kind = DeliveryReplayMessage.Shell; message.LookKey = ((TurretShell)d).LookKey; }
            list.Add(message);
        }
        return list;
    }

    /// <summary>A late joiner: shows an arrival the host replayed, already at its point in the timeline and without its past
    /// sounds, shake and events. <see cref="FindPod"/> and <see cref="FindShell"/> find it from now on.</summary>
    internal static void Replay(DeliveryReplayMessage m)
    {
        var landing = new Vector3(m.Landing.X, m.Landing.Y, m.Landing.Z);
        if (!m.Landing.IsFinite || !float.IsFinite(m.Yaw) || !float.IsFinite(m.Elapsed) || m.Elapsed < 0f) { Plugin.Error("Malformed delivery replay from the host for throw " + m.ThrowId + "."); return; }
        switch (m.Kind)
        {
            case DeliveryReplayMessage.Pod when FindPod(m.Thrower, m.ThrowId) == null:
                if (!OrdnanceContent.Set.TryGet(m.DefinitionKey, out var definition)) { Plugin.Error("Pod replay has unknown definition " + m.DefinitionKey + "."); return; }
                var pod = new DropPod(m.Thrower, m.ThrowId, landing, m.Yaw, m.Elapsed, true, definition);
                if (m.SinceEmptied >= 0f) pod.Emptied(m.SinceEmptied);
                Add(pod, out _);
                return;
            case DeliveryReplayMessage.Shell when FindShell(m.Thrower, m.ThrowId) == null:
                Add(new TurretShell(m.Thrower, m.ThrowId, landing, m.Yaw, m.Elapsed, true, m.LookKey), out _);
                return;
        }
    }

    internal static OrdnancePod? FindPod(ulong thrower, uint throwId)
    {
        foreach (var d in Active)
            if (d is DropPod pod && pod.Thrower == thrower && pod.ThrowId == throwId) return pod.Handle;
        return null;
    }

    internal static OrdnanceShell? FindShell(ulong thrower, uint throwId)
    {
        foreach (var d in Active)
            if (d is TurretShell shell && shell.Thrower == thrower && shell.ThrowId == throwId) return shell.Handle;
        return null;
    }

    internal static bool IsSupplyPosition(Vector3 position)
    {
        foreach (var delivery in Active)
            if (delivery is DropPod pod && pod.IsOpen)
                for (var i = 0; i < 4; i++)
                    if (pod.Anchor(i) is { } anchor && (anchor.position - position).sqrMagnitude < .09f) return true;
        return false;
    }

    /// <summary>A pod whose doors are still shut with a pickup bay at <paramref name="position"/>.</summary>
    internal static bool TryShutPodAt(Vector3 position, out ulong thrower, out uint throwId)
    {
        thrower = 0; throwId = 0;
        foreach (var delivery in Active)
        {
            if (delivery is not DropPod { IsOpen: false } pod) continue;
            for (var i = 0; i < ItemList.MaxPickups; i++)
            {
                if (pod.Anchor(i) is not { } anchor || (anchor.position - position).sqrMagnitude > ShutBayReach * ShutBayReach) continue;
                thrower = pod.Thrower; throwId = pod.ThrowId;
                return true;
            }
        }
        return false;
    }

    /// <summary>Host: shuts a just-placed pickup away until the doors of its pod open (<see cref="SupplyHold"/>).</summary>
    internal static void Hold(ItemInLevel item, ulong thrower, uint throwId) => SupplyHold.Hold(item.gameObject, thrower, throwId);

    private static bool Add(Delivery delivery, out Delivery added)
    {
        added = delivery;
        if (delivery.Root == null)
        {
            return false;
        }
        if (delivery.Quiet && !delivery.CatchUp())
        {
            delivery.Destroy();
            return false;
        }
        Active.Add(delivery);
        return true;
    }

    /// <summary>Host: runs <paramref name="work"/> after <paramref name="seconds"/> unless the calls in flight are voided first.</summary>
    internal static void HostAfter(float seconds, Action work) => HostWork.Add((Time.time + seconds, work));

    /// <summary>Host: watches the pickups placed in a pod and tells every peer when the last one has been taken.</summary>
    internal static void Watch(ulong thrower, uint throwId, ItemInLevel?[] items)
        => Watched.Add(new Contents { Thrower = thrower, ThrowId = throwId, Items = items, NextCheck = Time.time + ContentsCheckSeconds });

    /// <summary>Host: tells every peer (itself included) that a pod is empty.</summary>
    internal static void ReportEmpty(ulong thrower, uint throwId)
    {
        OrdnanceNetwork.Broadcast(Messages.DeliveryEvent, new DeliveryMessage { Thrower = thrower, ThrowId = throwId, State = DeliveryMessage.Emptied });
        Emptied(thrower, throwId);
    }

    /// <summary>Host: reports a failed supply delivery and retires its untrackable pod. Native pickups that did spawn are
    /// left alone and remain available.</summary>
    internal static void ReportFailure(ulong thrower, uint throwId)
    {
        Watched.RemoveAll(contents => contents.Thrower == thrower && contents.ThrowId == throwId);
        Plugin.Error("Drop pod of throw " + throwId + " failed; telling every peer to retire it. Any pickups already spawned remain available.");
        OrdnanceNetwork.Broadcast(Messages.DeliveryEvent, new DeliveryMessage { Thrower = thrower, ThrowId = throwId, State = DeliveryMessage.Failed });
        Plugin.Session.DeliveryFailed(thrower, throwId);
    }

    /// <summary>Every peer: the host reports a pod empty; it stays a while, then leaves.</summary>
    internal static void Emptied(ulong thrower, uint throwId)
    {
        RetirePod(thrower, throwId);
    }

    /// <summary>Every peer: a failed supply delivery retires its pod on the normal departure timeline without claiming the
    /// contents were taken.</summary>
    internal static void Failed(ulong thrower, uint throwId)
    {
        RetirePod(thrower, throwId);
    }

    private static void RetirePod(ulong thrower, uint throwId)
    {
        foreach (var d in Active)
            if (d is DropPod pod && pod.Thrower == thrower && pod.ThrowId == throwId) pod.Emptied();
    }

    /// <summary>Every frame, but an idle frame (no arrival, delayed step, watched pod, held pickup or spraying gravel) returns at
    /// once.</summary>
    internal static void Tick()
    {
        var now = Time.time;
        if (Active.Count == 0 && HostWork.Count == 0 && Watched.Count == 0 && !SupplyHold.Any && !OrdnanceFx.Busy(now))
            return;
        OrdnanceFx.Tick(now);
        for (var i = HostWork.Count - 1; i >= 0; i--)
        {
            if (now < HostWork[i].Due) continue;
            var work = HostWork[i].Work;
            HostWork.RemoveAt(i);
            try { work(); }
            catch (Exception error) { Plugin.Error("A delayed delivery step failed: " + error); }
        }
        if (SNet.IsMaster) CheckContents(now);
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            bool alive;
            try { alive = Active[i].Tick(now); }
            catch (Exception error) { Plugin.Error("A delivery animation failed and was removed: " + error); alive = false; }
            if (alive) continue;
            Active[i].Finished();
            Active[i].Destroy();
            Active.RemoveAt(i);
        }
        SupplyHold.Tick();
    }

    private static void CheckContents(float now)
    {
        for (var i = Watched.Count - 1; i >= 0; i--)
        {
            var c = Watched[i];
            if (now < c.NextCheck) continue;
            c.NextCheck = now + ContentsCheckSeconds;
            var left = false;
            foreach (var item in c.Items)
                if (item != null && !item.WasCollected && item.GetSyncComponent()?.TryCast<LG_PickupItem_Sync>()?.GetCurrentState().status == ePickupItemStatus.PlacedInLevel)
                    left = true;
            if (left) continue;
            Watched.RemoveAt(i);
            ReportEmpty(c.Thrower, c.ThrowId);
        }
    }

    internal static void Clear()
    {
        foreach (var d in Active) { d.Finished(); d.Destroy(); }
        Active.Clear();
        HostWork.Clear();
        Watched.Clear();
        SupplyHold.Clear();
        OrdnanceCues.Clear();
        OrdnanceArrival.Clear();
        OrdnanceFx.Clear();
    }

}

/// <summary>One animated arrival, the same on every peer.</summary>
internal abstract class Delivery
{
    /// <summary><paramref name="yaw"/> is the arrival's facing (thrower toward landing) in degrees; <paramref name="elapsed"/> the
    /// seconds already played on the host (a late joiner's replay), 0 for a fresh arrival. A replayed arrival skips the sounds
    /// shake and events of what already happened.</summary>
    protected Delivery(ulong thrower, uint throwId, Vector3 landing, float yaw, float elapsed, bool replayed)
    {
        AudioDimension = ForgeAudio.AudioKit.DimensionAt(landing);
        Thrower = thrower; ThrowId = throwId; Landing = landing; Yaw = yaw; Start = Time.time - elapsed; Quiet = replayed;
    }

    internal ulong Thrower { get; }
    internal uint ThrowId { get; }
    internal float Yaw { get; }
    internal Vector3 LandingPoint => Landing;
    internal float StartTime => Start;
    protected int AudioDimension { get; }
    protected Vector3 Landing { get; }
    protected float Start { get; }
    /// <summary>True while a replayed arrival restores its current pose and flags, before it is exposed.</summary>
    internal bool Quiet { get; private set; }

    internal bool CatchUp()
    {
        var alive = Tick(Time.time);
        Quiet = false;
        return alive;
    }
    internal GameObject? Root { get; set; }

    /// <summary>False once finished.</summary>
    internal abstract bool Tick(float now);

    /// <summary>Raised once when the delivery leaves the level (finished, or cleared by a level exit or reset).</summary>
    internal virtual void Finished() { }

    protected void Raise(Action? handler, string what)
    {
        if (Quiet || handler == null) return;
        try { handler(); }
        catch (Exception error) { Plugin.Error(GetType().Name + " of throw " + ThrowId + ": a " + what + " handler failed: " + error); }
    }

    /// <summary>The footprint of the model, which sizes the fall trail and the landing, door and departure effects.</summary>
    protected Vector3 FxSize { get; set; } = Vector3.one;
    /// <summary>Whether the falling arrival drags a smoke trail behind its flame (the pod does, the small turret shell only a short flame).</summary>
    protected virtual bool FallSmoke => true;
    private ForgeVisual.Trail? _trail;

    internal virtual void Destroy()
    {
        _trail?.End();
        _trail = null;
        if (Root != null && !Root.WasCollected) Object.Destroy(Root);
        Root = null;
    }

    /// <summary>The fall and the drill boring in; true from the frame of impact on.</summary>
    protected bool Fall(float t, ref bool impacted, OrdnanceImpact impact)
    {
        var rest = Landing;
        if (t < DeliveryTiming.FallSeconds)
        {
            var top = rest + Vector3.up * (DeliveryTiming.SinkDepth + DeliveryTiming.FallHeight);
            var position = Vector3.Lerp(top, rest + Vector3.up * DeliveryTiming.SinkDepth, t / DeliveryTiming.FallSeconds);
            Root!.transform.position = position;
            // Continuous, from the shared timeline: a late joiner mid-fall sees the tail from here on.
            _trail ??= OrdnanceFx.BeginFall(position, OrdnanceFx.ScaleOf(FxSize), FallSmoke);
            _trail?.Move(position, t / DeliveryTiming.FallSeconds);
            return false;
        }
        if (_trail != null) { _trail.End(); _trail = null; }
        var sink = Math.Min(1f, (t - DeliveryTiming.FallSeconds) / DeliveryTiming.SinkSeconds);
        Root!.transform.position = rest + Vector3.up * (DeliveryTiming.SinkDepth * (1f - sink));
        if (!impacted)
        {
            impacted = true;
            if (!Quiet)
            {
                OrdnanceArrival.Impact(Root!.transform, rest, impact);
            }
            Landed();
        }
        return true;
    }

    protected virtual void Landed() { }

    protected static float EaseOut(float x) { x = Math.Clamp(x, 0f, 1f); return 1f - (1f - x) * (1f - x); }
    protected static float EaseIn(float x) { x = Math.Clamp(x, 0f, 1f); return x * x; }

    /// <summary>The sign (+1 or -1) of a rotation about <paramref name="axis"/> (in the part's rest frame) that moves the part's
    /// mesh centre farther out horizontally from the parent's vertical axis, or (with <paramref name="upward"/>) higher.</summary>
    protected static float OpeningSign(Transform part, Vector3 axis, bool upward)
    {
        var filter = part.GetComponent<MeshFilter>();
        var centre = filter == null || filter.sharedMesh == null ? Vector3.zero : filter.sharedMesh.bounds.center;
        float Score(float sign)
        {
            var moved = part.localPosition + part.localRotation * (Quaternion.AngleAxis(sign * 90f, axis) * centre);
            return upward ? moved.y : new Vector2(moved.x, moved.z).sqrMagnitude;
        }
        return Score(1f) >= Score(-1f) ? 1f : -1f;
    }

    /// <summary>A hinged part: its rest rotation, axis and opening direction.</summary>
    protected readonly struct Hinge
    {
        internal Hinge(Transform part, Vector3 axis, float angle, bool upward)
        {
            Part = part; Rest = part.localRotation; Axis = axis; Angle = angle * OpeningSign(part, axis, upward);
        }

        internal Hinge(Transform part, Vector3 axis, float angle)
        {
            Part = part; Rest = part.localRotation; Axis = axis; Angle = angle;
        }

        /// <summary>A petal standing around the parent's vertical axis, hinged along the horizontal tangent through its own
        /// origin (the outer midpoint of its bottom edge).</summary>
        internal static Hinge Tangent(Transform part, float angle)
        {
            var radial = new Vector3(part.localPosition.x, 0f, part.localPosition.z);
            if (radial.sqrMagnitude < 1e-6f) throw new InvalidOperationException("Petal " + part.name + " sits on the shell axis; its hinge direction is undefined.");
            var tangent = Vector3.Cross(Vector3.up, radial.normalized);
            return new Hinge(part, Quaternion.Inverse(part.localRotation) * tangent, angle, false);
        }

        internal readonly Transform Part;
        internal readonly Quaternion Rest;
        internal readonly Vector3 Axis;
        internal readonly float Angle;

        internal void Set(float progress) => Part.localRotation = Rest * Quaternion.AngleAxis(Angle * progress, Axis);
    }

    protected static Transform Child(GameObject root, string name)
        => root.transform.Find(name) ?? throw new InvalidOperationException("The model has no part '" + name + "'.");
}

/// <summary>The one drop pod, for supplies and heavy weapons alike: falls, opens its four doors, holds the host's pickups on its
/// tiers or a weapon in a bay, and leaves a while after the host reports it empty. Pickups use the shelves; a weapon, which
/// stands a whole bay high, does not, so the shelves are only shown for the built-in <c>Item</c> payload.</summary>
internal sealed class DropPod : Delivery
{
    /// <summary>Deeper than the pod stands above the ground, so it is out of sight before it is removed.</summary>
    private const float LeaveMargin = 0.3f;
    private readonly Hinge[] _doors = Array.Empty<Hinge>();
    private readonly Transform[] _hinges = Array.Empty<Transform>();
    private readonly OrdnancePodAppearance? _appearance;
    private readonly Transform[] _weaponAnchors = Array.Empty<Transform>();
    private readonly Vector3 _size;
    /// <summary>The pod's touchdown: the thud and a heavy shake (its debris sound plays as the dust settles, in <see cref="Landed"/>).</summary>
    private static readonly OrdnanceImpact Impact = new() { Parts = OrdnanceImpactParts.Thud | OrdnanceImpactParts.Shake, Strength = 0.35f };
    private bool _impacted, _opened, _unlatched, _leaving;
    private float _emptiedAt = -1f;
    private readonly Transform?[] _anchors = new Transform?[ItemList.MaxPickups];

    internal OrdnancePod Handle { get; }
    internal uint DefinitionKey { get; }
    internal int WeaponBayCount => _appearance?.WeaponBayCount ?? 0;
    internal bool IsOpen => _opened;
    internal bool HasLanded => _impacted;
    internal Transform? Anchor(int index) => index >= 0 && index < _anchors.Length && Root != null ? _anchors[index] : null;
    internal Transform? WeaponAnchor => WeaponAnchorAt(0);

    internal Transform? WeaponAnchorAt(int index) => Root == null || index < 0 || index >= _weaponAnchors.Length ? null : _weaponAnchors[index];

    internal OrdnancePodBay? Bay(int index, bool weapon)
    {
        if (Root == null || index < 0) return null;
        var sockets = weapon ? _appearance!.WeaponBays : _appearance!.PickupBays;
        if (index >= sockets.Length) return null;
        var socket = sockets[index];
        var anchor = weapon ? _weaponAnchors[index] : _anchors[index]!;
        return new OrdnancePodBay(anchor.position, anchor.rotation * socket.Rotation, socket.Size, anchor);
    }

    internal DropPod(ulong thrower, uint throwId, Vector3 landing, float yaw, float elapsed, bool replayed, OrdnanceDefinition definition) : base(thrower, throwId, landing, yaw, elapsed, replayed)
    {
        Handle = new OrdnancePod(this);
        DefinitionKey = definition.Key;
        var hasPayload = OrdnanceApi.TryGetPayload(definition.PayloadType, out var payload);
        _appearance = (hasPayload ? payload.GetPodAppearance(definition) : null) ?? OrdnanceAssets.PodAppearance;
        if (_appearance == null) return;
        _doors = new Hinge[_appearance.Doors.Length];
        _hinges = new Transform[_doors.Length];
        _weaponAnchors = new Transform[_appearance.WeaponBays.Length];
        var model = _appearance.Model;
        var podRotation = DeliveryTiming.PodRotation(yaw);
        Root = model.Spawn(landing + Vector3.up * (DeliveryTiming.SinkDepth + DeliveryTiming.FallHeight), podRotation)
            ?? throw new InvalidOperationException("The drop pod model could not be spawned (ForgeVisual has logged why).");
        Root.name = "ForgeOrdnance.DropPod";
        _size = PodImpactEffects.ModelSize(Root);
        FxSize = _size;
        for (var i = 0; i < _doors.Length; i++)
        {
            var door = _appearance.Doors[i];
            _doors[i] = new Hinge(Child(Root, door.Path), door.Axis, door.Degrees);
            _hinges[i] = _doors[i].Part;
        }
        for (var i = 0; i < _appearance.PickupBays.Length; i++) _anchors[i] = Child(Root, _appearance.PickupBays[i].Path);
        for (var i = 0; i < _weaponAnchors.Length; i++) _weaponAnchors[i] = Child(Root, _appearance.WeaponBays[i].Path);
        if (definition.PayloadType != ItemPayload.TypeName)
            foreach (var shelf in OrdnanceAssets.ShelfPrefabs) Root.transform.Find(shelf)?.gameObject.SetActive(false);
        if (!Quiet) OrdnanceCues.Attach("drop.incoming", Root, AudioDimension);
    }

    internal void Emptied(float secondsAgo = 0f) { if (_emptiedAt < 0f) _emptiedAt = Time.time - secondsAgo; }

    /// <summary>Seconds since the host reported the pod empty, or -1.</summary>
    internal float SinceEmptied(float now) => _emptiedAt < 0f ? -1f : now - _emptiedAt;

    internal override bool Tick(float now)
    {
        var t = now - Start;
        if (!Fall(t, ref _impacted, Impact)) return true;
        var open = t - DeliveryTiming.FallSeconds - DeliveryTiming.PodOpenAfterImpact;
        if (!_unlatched && open >= -DeliveryTiming.UnlatchLead)
        {
            _unlatched = true;
            if (!Quiet) OrdnanceCues.World(OrdnanceSound.DropUnlatch, Root!, AudioDimension);
        }
        if (open >= 0f)
        {
            if (!_opened)
            {
                _opened = true;
                if (!Quiet)
                {
                    OrdnanceCues.Attach("drop.open", Root!, AudioDimension);
                    OrdnanceFx.PodOpened(Landing + Vector3.up * (_size.y * 0.4f), _hinges, _size);
                }
                Raise(Handle.OnOpened, "pod opened");
            }
            var progress = EaseOut(open / DeliveryTiming.DoorSeconds);
            foreach (var door in _doors) door.Set(progress);
        }
        if (_emptiedAt < 0f) return true;
        var leave = now - _emptiedAt - DeliveryTiming.PodLingerAfterEmpty;
        if (leave < 0f) return true;
        if (!_leaving)
        {
            _leaving = true;
            if (!Quiet)
            {
                OrdnanceCues.World(OrdnanceSound.DropLeave, Root!, AudioDimension);
                OrdnanceFx.PodLeaving(Landing, _size);
            }
        }
        if (leave >= DeliveryTiming.LeaveSeconds) return false;
        Root!.transform.position = Landing - Vector3.up * ((_size.y + LeaveMargin) * EaseIn(leave / DeliveryTiming.LeaveSeconds));
        return true;
    }

    protected override void Landed()
    {
        if (!Quiet)
        {
            PodImpactEffects.Play(Landing, _size);
            OrdnanceFx.PodLanded(Landing, _size);
            OrdnanceCues.World(OrdnanceSound.DropDebris, Root!, AudioDimension);
        }
        Raise(Handle.OnLanded, "pod landed");
    }

    internal override void Finished() => Raise(Handle.OnLeft, "pod left");

}

/// <summary>The turret's drop shell: falls, then its petals (<c>shell_1</c>, <c>shell_2</c>, ...) fall open outward about the
/// horizontal tangent through each petal's origin and the host's sentry stands inside; the opened shell sinks away after a few
/// seconds.</summary>
internal sealed class TurretShell : Delivery
{
    private const float PetalAngle = 92f;
    private const float SinkAway = 0.8f;
    private static readonly OrdnanceImpact Impact = new() { Parts = OrdnanceImpactParts.Thud | OrdnanceImpactParts.Shake, Strength = 0.3f };
    private readonly List<Hinge> _petals = new();
    private bool _impacted, _opened, _online;

    internal OrdnanceShell Handle { get; }
    internal uint LookKey { get; }
    internal bool IsOpen => _opened;
    internal bool HasLanded => _impacted;
    protected override bool FallSmoke => false;

    internal TurretShell(ulong thrower, uint throwId, Vector3 landing, float yaw, float elapsed, bool replayed, uint lookKey) : base(thrower, throwId, landing, yaw, elapsed, replayed)
    {
        Handle = new OrdnanceShell(this);
        LookKey = lookKey;
        var model = OrdnanceAssets.Shell;
        if (model == null) return;
        Root = model.Spawn(landing + Vector3.up * (DeliveryTiming.SinkDepth + DeliveryTiming.FallHeight), Quaternion.Euler(0f, yaw, 0f))
            ?? throw new InvalidOperationException("The turret shell model could not be spawned (ForgeVisual has logged why).");
        Root.name = "ForgeOrdnance.TurretShell";
        FxSize = PodImpactEffects.ModelSize(Root);
        for (var i = 1; Root.transform.Find("shell_" + i) is { } petal; i++) _petals.Add(Hinge.Tangent(petal, PetalAngle));
        if (_petals.Count == 0) throw new InvalidOperationException("The turret shell has no shell_1 petal.");
        if (!Quiet) OrdnanceCues.Attach("drop.incoming", Root, AudioDimension);
    }

    protected override void Landed()
    {
        // The pod's landing dust, scaled down to the shell's footprint, from the same pooled slots.
        if (!Quiet) PodImpactEffects.Play(Landing, FxSize);
        Raise(Handle.OnLanded, "shell landed");
    }

    internal override void Finished() => Raise(Handle.OnLeft, "shell left");

    internal override bool Tick(float now)
    {
        var t = now - Start;
        if (!Fall(t, ref _impacted, Impact)) return true;
        var open = t - DeliveryTiming.FallSeconds - DeliveryTiming.ShellOpenAfterImpact;
        if (open < 0f) return true;
        if (!_opened)
        {
            _opened = true;
            if (!Quiet) OrdnanceTurrets.RaiseDeployed(OrdnanceTurrets.IdOf(LookKey), Root!.transform);
            Raise(Handle.OnOpened, "shell opened");
        }
        // The turret comes online as the host's sentry appears (SentryAfterOpen): the same timeline on every peer.
        if (!_online && open >= DeliveryTiming.SentryAfterOpen)
        {
            _online = true;
            if (!Quiet) OrdnanceFx.SentryOnline(Landing + Vector3.up * 0.3f);
        }
        var progress = EaseIn(open / DeliveryTiming.PetalSeconds);
        foreach (var petal in _petals) petal.Set(progress);
        var away = open - DeliveryTiming.PetalSeconds - DeliveryTiming.ShellLinger;
        if (away < 0f) return true;
        if (away >= 1f) return false;
        Root!.transform.position = Landing - Vector3.up * (SinkAway * away);
        return true;
    }
}
